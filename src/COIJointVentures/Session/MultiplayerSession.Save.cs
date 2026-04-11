using System;
using System.Collections.Generic;
using COIJointVentures.Integration;
using COIJointVentures.Networking;
using COIJointVentures.Networking.Protocol;
using COIJointVentures.Runtime;

namespace COIJointVentures.Session;

internal sealed partial class MultiplayerSession
{
    // 512 MB — no legit COI save comes close to this
    private const int MaxSaveSize = 512 * 1024 * 1024;

    public bool HasPendingSends => _pendingSends.Count > 0;

    public void ClearReceivedSave()
    {
        _receivedSaveData = null;
        _receivedSavePath = null;
    }

    private void SendSaveToClient(string peerId)
    {
        if (_saveManager == null)
        {
            _log.LogWarning("Save manager not available, cannot send save to client.");
            return;
        }

        var savePath = _currentSavePath ?? _saveManager.FindMostRecentSave();
        if (savePath == null)
        {
            _log.LogWarning("No save file found to send to client.");
            _transport.SendToClient(peerId, ProtocolCodec.WrapSaveData(Array.Empty<byte>()));
            return;
        }

        var saveBytes = _saveManager.ReadSaveFile(savePath);
        if (saveBytes == null || saveBytes.Length == 0)
        {
            _log.LogWarning($"Failed to read save file: {savePath}");
            _transport.SendToClient(peerId, ProtocolCodec.WrapSaveData(Array.Empty<byte>()));
            return;
        }

        // chunk it up so steam doesn't choke
        var chunkSize = ProtocolCodec.MaxChunkSize;
        var totalChunks = (saveBytes.Length + chunkSize - 1) / chunkSize;
        _log.LogInfo($"Sending save to '{peerId}': {saveBytes.Length} bytes in {totalChunks} chunk(s) from {savePath}");

        for (int i = 0; i < totalChunks; i++)
        {
            var offset = i * chunkSize;
            var length = Math.Min(chunkSize, saveBytes.Length - offset);
            var chunk = new byte[length];
            Buffer.BlockCopy(saveBytes, offset, chunk, 0, length);
            _transport.SendToClient(peerId, ProtocolCodec.WrapSaveChunk(i, totalChunks, saveBytes.Length, chunk));
        }

        _transport.SendToClient(peerId, ProtocolCodec.WrapSaveComplete());
        _log.LogInfo($"Save transfer complete to '{peerId}'.");
    }

    private void SendSaveToClients(IEnumerable<string> peerIds, byte[] saveBytes)
    {
        var chunkSize = ProtocolCodec.MaxChunkSize;
        var totalChunks = (saveBytes.Length + chunkSize - 1) / chunkSize;
        var peerList = new List<string>(peerIds);

        foreach (var peerId in peerList)
        {
            _log.LogInfo($"Queuing save for '{peerId}': {saveBytes.Length} bytes in {totalChunks} chunk(s)");

            for (int i = 0; i < totalChunks; i++)
            {
                var offset = i * chunkSize;
                var length = Math.Min(chunkSize, saveBytes.Length - offset);
                var chunk = new byte[length];
                Buffer.BlockCopy(saveBytes, offset, chunk, 0, length);
                _pendingSends.Enqueue((peerId, ProtocolCodec.WrapSaveChunk(i, totalChunks, saveBytes.Length, chunk)));
            }

            _pendingSends.Enqueue((peerId, ProtocolCodec.WrapSaveComplete()));
        }

        _log.LogInfo($"Queued {_pendingSends.Count} send operations for {peerList.Count} client(s).");
    }

    // burst sender - fires as many chunks as steam will accept per tick,
    // backs off on LimitExceeded and picks up next tick
    public void TickPendingSends()
    {
        if (_pendingSends.Count == 0)
            return;

        var steam = _transport as SteamTransport;
        if (steam == null)
        {
            // non-steam transport, just send everything
            while (_pendingSends.Count > 0)
            {
                var (peerId, payload) = _pendingSends.Dequeue();
                _transport.SendToClient(peerId, payload);
            }
            return;
        }

        // send as many as the buffer will take, stop on LimitExceeded
        while (_pendingSends.Count > 0)
        {
            var (peerId, payload) = _pendingSends.Peek();
            var conn = steam.GetConnectionForPeer(peerId);
            if (conn == null)
            {
                _pendingSends.Dequeue();
                continue;
            }

            if (!steam.TrySend(conn.Value, payload, $"chunk to {peerId}"))
            {
                // buffer full, try again next tick
                break;
            }

            _pendingSends.Dequeue();
        }
    }

    private void HandleSaveData(byte[] saveBytes)
    {
        if (State != ConnectionState.ReceivingSave)
        {
            _log.LogWarning($"Received save data ({saveBytes.Length} bytes) but state is {State}, ignoring.");
            return;
        }

        // hash check so we know the save isn't corrupted
        using (var md5 = System.Security.Cryptography.MD5.Create())
        {
            var hash = BitConverter.ToString(md5.ComputeHash(saveBytes)).Replace("-", "");
            _log.LogInfo($"[SAVE-RECV] Save hash (MD5): {hash}, size: {saveBytes.Length}");
        }

        if (saveBytes.Length == 0)
        {
            _log.LogWarning("Received empty save data from host. The host may not have a save file.");
            StatusMessage = "Warning: host sent empty save. You may need to load a save manually.";
            ConfirmSaveLoaded();
            return;
        }

        _log.LogInfo($"Received save data: {saveBytes.Length} bytes. Writing to disk...");
        _receivedSaveData = saveBytes;
        State = ConnectionState.LoadingSave;
        StatusMessage = $"Received save ({saveBytes.Length / 1024} KB). Loading...";

        var gameName = "Multiplayer";
        var saveName = $"mp_joined_{DateTime.UtcNow:yyyyMMdd_HHmmss}";

        // write it to disk
        var saveManager = PluginRuntime.SaveManager;
        string? writtenPath = saveManager?.WriteSaveFile(saveBytes, saveName, gameName);

        _receivedSavePath = writtenPath;

        if (writtenPath == null)
        {
            StatusMessage = "Failed to write save file.";
            _log.LogError("Could not write received save to disk.");
            return;
        }

        // try auto-loading, fingers crossed
        if (MainCapture.HasMain)
        {
            StatusMessage = "Loading save...";
            _log.LogInfo($"Auto-loading save '{saveName}' (game: '{gameName}') via IMain.LoadGame...");
            if (MainCapture.TryLoadGame(saveName, gameName))
            {
                StatusMessage = "Loading game...";
                return;
            }

            _log.LogWarning("Auto-load failed, falling back to manual load.");
        }

        // auto-load failed, they'll have to do it manually
        StatusMessage = $"Save written. Load '{saveName}' from the game's load menu, then click Confirm.";
        _log.LogInfo($"Manual load required: {writtenPath}");
    }

    private void HandleSaveChunk(byte[] payload)
    {
        ProtocolCodec.DecodeSaveChunk(payload, out var chunkIndex, out var totalChunks, out var totalSize, out var chunkData);

        if (chunkIndex == 0)
        {
            if (totalSize <= 0 || totalSize > MaxSaveSize || totalChunks <= 0)
            {
                _log.LogWarning($"Rejecting save: totalSize={totalSize}, totalChunks={totalChunks} — out of range.");
                return;
            }

            _saveChunkBuffer = new byte[totalSize];
            _saveChunksReceived = 0;
            _saveChunksExpected = totalChunks;
            State = ConnectionState.ReceivingSave;
            StatusMessage = $"Receiving save ({totalSize / 1024} KB)...";
            _log.LogInfo($"Starting chunked save receive: {totalSize} bytes in {totalChunks} chunks.");
        }

        if (_saveChunkBuffer == null)
        {
            _log.LogWarning("Received save chunk but no buffer initialized.");
            return;
        }

        if (chunkIndex < 0 || chunkIndex >= _saveChunksExpected)
        {
            _log.LogWarning($"Rejecting chunk with out-of-range index {chunkIndex} (expected 0..{_saveChunksExpected - 1}).");
            return;
        }

        long offset = (long)chunkIndex * ProtocolCodec.MaxChunkSize;
        if (offset + chunkData.Length > _saveChunkBuffer.Length)
        {
            _log.LogWarning($"Rejecting chunk {chunkIndex}: write at {offset}+{chunkData.Length} exceeds buffer size {_saveChunkBuffer.Length}.");
            return;
        }

        Buffer.BlockCopy(chunkData, 0, _saveChunkBuffer, (int)offset, chunkData.Length);
        _saveChunksReceived++;
        StatusMessage = $"Receiving save... ({_saveChunksReceived}/{_saveChunksExpected})";
        _log.LogDebug($"Received save chunk {chunkIndex + 1}/{totalChunks} ({chunkData.Length} bytes).");
    }

    private void HandleSaveComplete()
    {
        if (_saveChunkBuffer == null || _saveChunksReceived < _saveChunksExpected)
        {
            _log.LogWarning($"Save complete signal but only {_saveChunksReceived}/{_saveChunksExpected} chunks received.");
        }

        _log.LogInfo($"All save chunks received ({_saveChunkBuffer?.Length ?? 0} bytes). Processing...");
        var saveBytes = _saveChunkBuffer ?? Array.Empty<byte>();
        _saveChunkBuffer = null;
        _saveChunksReceived = 0;
        _saveChunksExpected = 0;

        // hand it off to the normal save handler
        HandleSaveData(saveBytes);
    }
}
