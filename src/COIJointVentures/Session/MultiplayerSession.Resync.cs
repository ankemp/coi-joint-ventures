using System;
using System.Linq;
using COIJointVentures.Networking.Protocol;
using COIJointVentures.Runtime;

namespace COIJointVentures.Session;

internal sealed partial class MultiplayerSession
{
    private void HandleStateChecksum(byte[] payload)
    {
        if (Mode != MultiplayerMode.Client)
        {
            return;
        }

        var checksumData = ProtocolCodec.DecodeStateChecksum(payload);
        _pendingHostChecksums[checksumData.Sequence] = checksumData.Checksum;
    }

    private int CalculateLocalChecksum()
    {
        var probe = GameStateProbe;
        if (probe == null) return 0;
        try { return probe(); }
        catch { return 0; }
    }

    public void TickChecksums()
    {
        if (Mode != MultiplayerMode.Host)
            return;

        _checksumTickCounter++;
        if (_checksumTickCounter % 100 != 0)
            return;

        var checksumPayload = new StateChecksumPayload
        {
            Sequence = _nextSequence,
            Checksum = CalculateLocalChecksum()
        };
        _transport.Broadcast(ProtocolCodec.WrapStateChecksum(checksumPayload));
    }

    private void OnSequenceGap(long expected, long received)
    {
        var missed = received - expected;
        _log.LogWarning($"[MINOR-RESYNC] Sequence gap {expected}..{received - 1} ({missed} command(s) missing).");

        if (_minorResyncInFlight)
        {
            _log.LogInfo("[MINOR-RESYNC] Request already in flight — skipping duplicate.");
            return;
        }

        _minorResyncInFlight = true;
        _minorResyncRequestTime = DateTime.UtcNow;

        var request = new MinorResyncRequestPayload { LastProcessedSequence = expected - 1 };
        _transport.SendToHost(ProtocolCodec.WrapMinorResyncRequest(request));

        PluginRuntime.Chat.AddSystem($"Catching up: requesting {missed} missed command(s) from host...");
        _log.LogInfo($"[MINOR-RESYNC] Sent MinorResyncRequest(lastProcessed={expected - 1}) to host.");
    }

    public void TickMinorResync()
    {
        if (!_minorResyncInFlight) return;
        if ((DateTime.UtcNow - _minorResyncRequestTime).TotalSeconds < 5.0) return;

        _log.LogWarning("[MINOR-RESYNC] Timed out waiting for response — escalating to major resync.");
        _minorResyncInFlight = false;
        RequestMajorResync();
    }

    private void RequestMajorResync()
    {
        if (DesyncState == DesyncState.SimulationDesync)
            return;  // already in flight

        DesyncState = DesyncState.SimulationDesync;
        StatusMessage = "Simulation desync detected — requesting full resync...";

        // flush all in-flight state before the incoming save overwrites everything
        _pendingCommands.Clear();
        _outboundCommandIds.Clear();
        _seenCommandIds.Clear();
        _pendingHostChecksums.Clear();
        _pendingAcks.Clear();
        _lastProcessedHostSequence = -1;
        _minorResyncInFlight = false;
        PluginRuntime.DrainReplicated();

        _transport.SendToHost(ProtocolCodec.WrapMajorResyncRequest());
        _log.LogError("[MAJOR-RESYNC] Simulation desync detected — requesting full save resync from host.");
        PluginRuntime.Chat.AddSystem("Simulation desync detected. Requesting full resync from host...");
    }

    private void HandleMinorResyncRequest(string senderPeerId, byte[] payload)
    {
        if (Mode != MultiplayerMode.Host) return;

        var request = ProtocolCodec.DecodeMinorResyncRequest(payload);
        var fromSeq = request.LastProcessedSequence + 1;
        _log.LogInfo($"[MINOR-RESYNC] Request from '{senderPeerId}': replay from seq={fromSeq}.");

        var oldest = _commandHistory.OldestSequence;
        if (_commandHistory.Count == 0 || oldest == null || oldest.Value > fromSeq)
        {
            _log.LogWarning($"[MINOR-RESYNC] Cannot fulfil: oldest recorded seq={oldest?.ToString() ?? "none"}, requested seq={fromSeq}. Sending FullResyncRequired.");
            var fallback = new MinorResyncResponsePayload { FullResyncRequired = true };
            _transport.SendToClient(senderPeerId, ProtocolCodec.WrapMinorResyncResponse(fallback));
            return;
        }

        var commands = _commandHistory.GetSince(fromSeq);
        _log.LogInfo($"[MINOR-RESYNC] Replaying {commands.Count} command(s) to '{senderPeerId}'.");
        PluginRuntime.Chat.AddSystem($"Catching up {ResolvePeerName(senderPeerId)}: replaying {commands.Count} missed command(s).");

        var response = new MinorResyncResponsePayload { Commands = commands };
        var encoded = ProtocolCodec.WrapMinorResyncResponse(response);

        if (encoded.Length > ProtocolCodec.MaxChunkSize)
        {
            _log.LogWarning($"[MINOR-RESYNC] Batch too large ({encoded.Length} bytes) — sending FullResyncRequired.");
            var fallback = new MinorResyncResponsePayload { FullResyncRequired = true };
            _transport.SendToClient(senderPeerId, ProtocolCodec.WrapMinorResyncResponse(fallback));
            return;
        }

        _transport.SendToClient(senderPeerId, encoded);
    }

    private void HandleMinorResyncResponse(byte[] payload)
    {
        if (Mode != MultiplayerMode.Client) return;

        _minorResyncInFlight = false;

        var response = ProtocolCodec.DecodeMinorResyncResponse(payload);

        if (response.FullResyncRequired)
        {
            _log.LogWarning("[MINOR-RESYNC] Host cannot fulfil catch-up — escalating to major resync.");
            RequestMajorResync();
            return;
        }

        _log.LogInfo($"[MINOR-RESYNC] Applying {response.Commands.Count} replayed command(s).");

        foreach (var envelope in response.Commands.OrderBy(e => e.Sequence))
        {
            // skip duplicates — may already be in _seenCommandIds if the echo arrived late
            if (!_seenCommandIds.Add(envelope.CommandId))
            {
                _log.LogInfo($"[MINOR-RESYNC] Skipping already-seen envelope seq={envelope.Sequence}.");
                _lastProcessedHostSequence = envelope.Sequence;
                continue;
            }

            // skip our own optimistically-executed commands
            if (_outboundCommandIds.Remove(envelope.CommandId))
            {
                _log.LogInfo($"[MINOR-RESYNC] seq={envelope.Sequence} was our own optimistic command — skipping reinject.");
                _lastProcessedHostSequence = envelope.Sequence;
                continue;
            }

            ReceiveReplicatedCommand(envelope);
            _lastProcessedHostSequence = envelope.Sequence;
        }

        if (DesyncState == DesyncState.SequenceGap)
        {
            DesyncState = DesyncState.None;
            _log.LogInfo("[MINOR-RESYNC] Catch-up complete — desync state cleared.");
            PluginRuntime.Chat.AddSystem("Packet loss recovered.");
        }
    }

    private void HandleMajorResyncRequest(string senderPeerId)
    {
        if (Mode != MultiplayerMode.Host || _joinCoordinator == null) return;

        var playerName = ResolvePeerName(senderPeerId);
        _log.LogWarning($"[MAJOR-RESYNC] '{playerName}' ({senderPeerId}) requested full resync.");

        // move back to pending so HandleClientReady transitions correctly
        lock (_activePeers) { _activePeers.Remove(senderPeerId); }
        lock (_pendingPeers) { _pendingPeers.Add(senderPeerId); }

        PluginRuntime.Chat.AddSystem($"Resyncing {playerName} — saving and transferring world data...");
        _joinCoordinator.BeginJoin(senderPeerId, playerName);
    }
}
