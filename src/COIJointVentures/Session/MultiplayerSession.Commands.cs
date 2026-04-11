using System;
using System.Collections.Generic;
using System.Threading;
using COIJointVentures.Commands;
using COIJointVentures.Integration;
using COIJointVentures.Networking.Protocol;
using COIJointVentures.Runtime;
using Mafi.Core.Input;

namespace COIJointVentures.Session;

internal sealed partial class MultiplayerSession
{
    public void SubmitLocalCommand(string commandType, string payloadJson, Action<CommandEnvelope> apply)
    {
        var envelope = new CommandEnvelope
        {
            CommandType = commandType,
            IssuerPlayerId = LocalPeerId,
            Sequence = Interlocked.Increment(ref _nextSequence),
            Tick = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            PayloadJson = payloadJson
        };

        if (Mode == MultiplayerMode.Host)
        {
            ApplyAsHost(envelope, apply);
            return;
        }

        // remember this ID so we drop the echo when host sends it back
        _outboundCommandIds.Add(envelope.CommandId);
        _pendingCommands.Enqueue(new PendingCommand(envelope));
        // encode once — used for ACK tracking and (possibly chunked) transmission
        var encodedPayload = _codec.Encode(envelope);
        _pendingAcks[envelope.CommandId] = new PendingAck { SentAt = DateTime.UtcNow, EncodedPayload = encodedPayload };
        TransmitCommandToHost(envelope.CommandId, encodedPayload);
    }

    public bool ShouldReplicateCommand(NativeCommandInfo command)
    {
        if (Mode == MultiplayerMode.None)
        {
            return false;
        }

        if (command.IsVerificationCmd)
        {
            return false;
        }

        // replicate everything except verification cmds
        // (pause cmd has AffectsSaveState=false but still needs to go through)
        return true;
    }

    public void SubmitNativeCommand(IInputCommand command, NativeCommandCodec codec)
    {
        var typeName = command.GetType().FullName ?? command.GetType().Name;
        var payloadBase64 = codec.SerializeToBase64(command);
        SubmitLocalCommand(typeName, payloadBase64, _ => { });
    }

    public void ReceiveReplicatedCommand(CommandEnvelope envelope)
    {
        var codec = PluginRuntime.NativeCodec;
        if (codec == null)
        {
            _log.LogWarning($"[RECV] Cannot deserialize '{envelope.CommandType}' — codec unavailable.");
            return;
        }

        try
        {
            var nativeCommand = codec.DeserializeFromBase64(envelope.PayloadJson);
            var deserializedType = nativeCommand.GetType().FullName ?? nativeCommand.GetType().Name;
            var isProcessed = false;
            try { isProcessed = nativeCommand.IsProcessed; } catch { }
            _log.LogInfo($"[RECV] Deserialized '{envelope.CommandType}' -> actual type='{deserializedType}', IsProcessed={isProcessed}, AffectsSaveState={nativeCommand.AffectsSaveState}");
            PluginRuntime.EnqueueReplicated(nativeCommand);
            _log.LogInfo($"[RECV] Buffered '{deserializedType}' for injection. Buffer size={PluginRuntime.PendingReplicatedCount}");
        }
        catch (Exception ex)
        {
            _log.LogError($"[RECV] FAILED to deserialize '{envelope.CommandType}': {ex}");
        }
    }

    private void ApplyAsHost(CommandEnvelope envelope, Action<CommandEnvelope> apply)
    {
        _log.LogInfo($"[HOST-APPLY] Broadcasting '{envelope.CommandType}' to all clients.");
        apply(envelope);
        _commandHistory.Record(envelope);
        _transport.Broadcast(ProtocolCodec.WrapGameCommand(_codec.Encode(envelope)));
    }

    private void HandleGameCommand(string senderPeerId, byte[] payload)
    {
        _log.LogInfo($"[GAME-CMD] Received from '{senderPeerId}', payload={payload.Length} bytes, Mode={Mode}, HostPeerId={HostPeerId}");

        var envelope = _codec.Decode(payload);
        if (!envelope.IsValid)
        {
            _log.LogWarning($"[GAME-CMD] Rejected invalid command from '{senderPeerId}'.");
            return;
        }

        _log.LogInfo($"[GAME-CMD] Decoded: type='{envelope.CommandType}', issuer='{envelope.IssuerPlayerId}', seq={envelope.Sequence}");

        if (!_seenCommandIds.Add(envelope.CommandId))
        {
            _log.LogWarning($"[GAME-CMD] Duplicate CommandId {envelope.CommandId} from '{senderPeerId}' — dropped.");
            return;
        }

        if (Mode == MultiplayerMode.Host && senderPeerId != HostPeerId)
        {
            if (!string.Equals(envelope.IssuerPlayerId, senderPeerId, StringComparison.Ordinal))
            {
                _log.LogWarning($"[GAME-CMD] Rejected command from '{senderPeerId}': IssuerPlayerId '{envelope.IssuerPlayerId}' doesn't match sender.");
                return;
            }

            _log.LogInfo($"[GAME-CMD] HOST: Processing client command '{envelope.CommandType}' from '{senderPeerId}' -> re-stamp (seq={_nextSequence + 1}) + reinject + broadcast.");
            envelope.Sequence = Interlocked.Increment(ref _nextSequence);
            var reEncodedPayload = _codec.Encode(envelope);
            ReceiveReplicatedCommand(envelope);
            _commandHistory.Record(envelope);
            _transport.Broadcast(ProtocolCodec.WrapGameCommand(reEncodedPayload));
            return;
        }

        if (Mode == MultiplayerMode.Client && senderPeerId == HostPeerId)
        {
            // debug sim drop: drop before advancing seq so the gap is detectable
            if (_simulateDropNextPacket)
            {
                _simulateDropNextPacket = false;
                _log.LogError($"[DEBUG] Silently dropping host command '{envelope.CommandType}' (seq {envelope.Sequence}) to simulate network loss!");
                PluginRuntime.Chat.AddSystem($"Dropped packet: seq {envelope.Sequence}");
                return;
            }

            // seq gap detection now covers ALL commands including our own echoes, because
            // the host re-stamps every relayed client command with its own monotonic counter
            if (!IsJoinSyncActive && _lastProcessedHostSequence >= 0 && envelope.Sequence != _lastProcessedHostSequence + 1)
            {
                var expected = _lastProcessedHostSequence + 1;
                _log.LogWarning($"[SEQ-GAP] Expected seq {expected}, got {envelope.Sequence} — {envelope.Sequence - expected} command(s) lost.");
                if (DesyncState == DesyncState.None)
                    DesyncState = DesyncState.SequenceGap;
                OnSequenceGap(expected, envelope.Sequence);
            }
            _lastProcessedHostSequence = envelope.Sequence;

            // drop echoes of our own commands — seq already tracked above so no spurious gap
            if (_outboundCommandIds.Remove(envelope.CommandId))
            {
                _pendingAcks.Remove(envelope.CommandId); // host confirmed receipt → cancel retry timer
                _log.LogInfo($"[GAME-CMD] CLIENT: Echo acked '{envelope.CommandType}' id={envelope.CommandId} seq={envelope.Sequence}.");
                if (_pendingCommands.Count > 0 && _pendingCommands.Peek().Envelope.CommandId == envelope.CommandId)
                {
                    _pendingCommands.Dequeue();
                }
                return;
            }

            _log.LogInfo($"[GAME-CMD] CLIENT: Received host command '{envelope.CommandType}' seq={envelope.Sequence} -> reinject.");
            ReceiveReplicatedCommand(envelope);

            if (_pendingHostChecksums.TryGetValue(envelope.Sequence, out var hostChecksum))
            {
                _pendingHostChecksums.Remove(envelope.Sequence);
                var localChecksum = CalculateLocalChecksum();
                if (localChecksum != hostChecksum)
                {
                    _log.LogError($"[DESYNC] Sequence {envelope.Sequence}: Host Hash={hostChecksum}, Local Hash={localChecksum}");
                    RequestMajorResync();
                }
            }

            return;
        }

        _log.LogWarning($"[GAME-CMD] UNHANDLED: sender='{senderPeerId}', mode={Mode}, hostPeer='{HostPeerId}' — command dropped!");
    }

    private void TransmitCommandToHost(Guid commandId, byte[] encodedPayload)
    {
        if (encodedPayload.Length <= ProtocolCodec.MaxCommandChunkDataSize)
        {
            _transport.SendToHost(ProtocolCodec.WrapGameCommand(encodedPayload));
            return;
        }

        // payload exceeds single-message limit — split into chunks
        var chunkSize = ProtocolCodec.MaxCommandChunkDataSize;
        var totalChunks = (encodedPayload.Length + chunkSize - 1) / chunkSize;
        _log.LogWarning($"[CMD-CHUNK] Command {commandId}: {encodedPayload.Length} bytes → {totalChunks} chunk(s).");
        _transport.SendToHost(ProtocolCodec.WrapCommandChunkStart(commandId, totalChunks, encodedPayload.Length));
        for (var i = 0; i < totalChunks; i++)
        {
            var offset = i * chunkSize;
            var length = Math.Min(chunkSize, encodedPayload.Length - offset);
            var chunk = new byte[length];
            Buffer.BlockCopy(encodedPayload, offset, chunk, 0, length);
            _transport.SendToHost(ProtocolCodec.WrapCommandChunk(commandId, i, chunk));
        }
    }

    private void HandleCommandChunkStart(string senderPeerId, byte[] payload)
    {
        if (Mode != MultiplayerMode.Host) return;

        ProtocolCodec.DecodeCommandChunkStart(payload, out var commandId, out var totalChunks, out var totalSize);
        if (totalSize <= 0 || totalSize > MaxCommandSize || totalChunks <= 0)
        {
            _log.LogWarning($"[CMD-CHUNK] Rejecting oversized command from '{senderPeerId}': size={totalSize}, chunks={totalChunks}.");
            return;
        }

        _commandChunkBuffers[commandId] = new CommandReassemblyBuffer
        {
            Data = new byte[totalSize],
            TotalChunks = totalChunks,
            StartedAt = DateTime.UtcNow,
            SenderPeerId = senderPeerId
        };
        _log.LogInfo($"[CMD-CHUNK] Started reassembly for {commandId} from '{senderPeerId}': {totalSize} bytes in {totalChunks} chunk(s).");
    }

    private void HandleCommandChunk(string senderPeerId, byte[] payload)
    {
        if (Mode != MultiplayerMode.Host) return;

        ProtocolCodec.DecodeCommandChunk(payload, out var commandId, out var chunkIndex, out var chunkData);

        if (!_commandChunkBuffers.TryGetValue(commandId, out var buffer))
        {
            _log.LogWarning($"[CMD-CHUNK] Chunk for unknown command {commandId} from '{senderPeerId}' — ignoring.");
            return;
        }

        if (!string.Equals(buffer.SenderPeerId, senderPeerId, StringComparison.Ordinal))
        {
            _log.LogWarning($"[CMD-CHUNK] Sender mismatch for {commandId}: expected '{buffer.SenderPeerId}', got '{senderPeerId}'.");
            return;
        }

        var offset = chunkIndex * ProtocolCodec.MaxCommandChunkDataSize;
        if (offset < 0 || offset + chunkData.Length > buffer.Data.Length)
        {
            _log.LogWarning($"[CMD-CHUNK] Chunk {chunkIndex} from '{senderPeerId}' writes out of bounds — discarding command.");
            _commandChunkBuffers.Remove(commandId);
            return;
        }

        Buffer.BlockCopy(chunkData, 0, buffer.Data, offset, chunkData.Length);
        buffer.ChunksReceived++;

        if (buffer.ChunksReceived == buffer.TotalChunks)
        {
            _commandChunkBuffers.Remove(commandId);
            _log.LogInfo($"[CMD-CHUNK] Reassembly complete for {commandId} ({buffer.Data.Length} bytes). Processing.");
            HandleGameCommand(senderPeerId, buffer.Data);
        }
    }

    public void TickPendingAcks()
    {
        if (Mode != MultiplayerMode.Client || _pendingAcks.Count == 0) return;

        var now = DateTime.UtcNow;
        Guid? escalateId = null;

        foreach (var kvp in _pendingAcks)
        {
            var id = kvp.Key;
            var ack = kvp.Value;
            var age = (now - ack.SentAt).TotalSeconds;
            if (ack.RetryCount == 0 && age > AckTimeoutSeconds)
            {
                _log.LogWarning($"[ACK] Command {id} unacknowledged after {AckTimeoutSeconds:0}s — retrying once.");
                ack.RetryCount++;
                ack.SentAt = now;
                TransmitCommandToHost(id, ack.EncodedPayload);
            }
            else if (ack.RetryCount > 0 && age > AckTimeoutSeconds)
            {
                _log.LogError($"[ACK] Command {id} still unacknowledged after retry — host never received it, requesting major resync.");
                escalateId = id;
                break;
            }
        }

        if (escalateId.HasValue)
        {
            _pendingAcks.Clear(); // state gets flushed by RequestMajorResync anyway
            RequestMajorResync();
        }
    }

    public void TickCommandChunks()
    {
        if (Mode != MultiplayerMode.Host || _commandChunkBuffers.Count == 0) return;

        var now = DateTime.UtcNow;
        var expired = new List<Guid>();
        foreach (var kvp in _commandChunkBuffers)
        {
            if ((now - kvp.Value.StartedAt).TotalSeconds > 30.0)
            {
                _log.LogWarning($"[CMD-CHUNK] Reassembly for {kvp.Key} from '{kvp.Value.SenderPeerId}' timed out — discarding.");
                expired.Add(kvp.Key);
            }
        }
        foreach (var id in expired)
            _commandChunkBuffers.Remove(id);
    }

    // ── Nested types ──────────────────────────────────────────────────────────

    private sealed class PendingAck
    {
        public DateTime SentAt;
        public int RetryCount;
        public byte[] EncodedPayload = Array.Empty<byte>();
    }

    private sealed class CommandReassemblyBuffer
    {
        public byte[] Data = Array.Empty<byte>();
        public int ChunksReceived;
        public int TotalChunks;
        public DateTime StartedAt;
        public string SenderPeerId = string.Empty;
    }
}
