using System;
using COIJointVentures.Networking.Protocol;
using COIJointVentures.Runtime;

namespace COIJointVentures.Session;

internal sealed partial class MultiplayerSession
{
    private void HandleJoinRequest(string senderPeerId, byte[] payload)
    {
        if (Mode != MultiplayerMode.Host)
        {
            _log.LogWarning($"Received JoinRequest from '{senderPeerId}' but not in host mode.");
            return;
        }

        var request = ProtocolCodec.DecodeJoinRequest(payload);
        _log.LogInfo($"Received join request from '{request.PlayerName}'.");

        // check version
        if (!string.Equals(request.ModVersion, Plugin.PluginVersion, StringComparison.Ordinal))
        {
            var clientVer = string.IsNullOrEmpty(request.ModVersion) ? "unknown" : request.ModVersion;
            _log.LogInfo($"Rejected '{request.PlayerName}': version mismatch (client={clientVer}, host={Plugin.PluginVersion}).");
            var rejection = new JoinResponse
            {
                Accepted = false,
                Reason = $"Version mismatch: you have {clientVer}, host has {Plugin.PluginVersion}."
            };
            _transport.SendToClient(senderPeerId, ProtocolCodec.WrapJoinRejected(rejection));
            return;
        }

        // check password
        if (_serverConfig != null && _serverConfig.HasPassword)
        {
            if (!string.Equals(_serverConfig.Password, request.Password, StringComparison.Ordinal))
            {
                _log.LogInfo($"Rejected '{request.PlayerName}': wrong password.");
                var rejection = new JoinResponse
                {
                    Accepted = false,
                    Reason = "Incorrect password."
                };
                _transport.SendToClient(senderPeerId, ProtocolCodec.WrapJoinRejected(rejection));
                return;
            }
        }

        // looks good, let em in
        lock (_peerNames) { _peerNames[senderPeerId] = request.PlayerName; }
        lock (_pendingPeers) { _pendingPeers.Add(senderPeerId); }

        var acceptance = new JoinResponse
        {
            Accepted = true,
            ServerName = _serverConfig?.ServerName ?? "COI Server",
            AssignedPeerId = senderPeerId,
            ColorIndex = GetOrAssignColor(senderPeerId)
        };
        _transport.SendToClient(senderPeerId, ProtocolCodec.WrapJoinAccepted(acceptance));
        _log.LogInfo($"Accepted '{request.PlayerName}' (peer={senderPeerId}). Starting coordinated join...");

        // kick off the whole pause->save->send->wait flow
        if (_joinCoordinator != null)
        {
            _joinCoordinator.BeginJoin(senderPeerId, request.PlayerName);
        }
        else
        {
            // fallback if coordinator isn't set up
            SendSaveToClient(senderPeerId);
        }
    }

    private void HandleJoinAccepted(byte[] payload)
    {
        if (State != ConnectionState.WaitingForAccept)
        {
            return;
        }

        var response = ProtocolCodec.DecodeJoinResponse(payload);
        _localColorIndex = response.ColorIndex;
        State = ConnectionState.ReceivingSave;
        StatusMessage = $"Accepted by '{response.ServerName}', receiving save...";
        _log.LogInfo($"{StatusMessage} (assigned color {_localColorIndex})");
    }

    private void HandleJoinRejected(byte[] payload)
    {
        var response = ProtocolCodec.DecodeJoinResponse(payload);
        State = ConnectionState.Idle;
        Mode = MultiplayerMode.None;
        StatusMessage = $"Rejected: {response.Reason}";
        _log.LogInfo($"Join rejected: {response.Reason}");
    }

    private void HandleClientReady(string senderPeerId)
    {
        if (Mode != MultiplayerMode.Host)
        {
            return;
        }

        lock (_pendingPeers) { _pendingPeers.Remove(senderPeerId); }
        lock (_activePeers) { _activePeers.Add(senderPeerId); }

        _log.LogInfo($"Client '{senderPeerId}' is ready and active.");
        StatusMessage = $"Hosting — {_activePeers.Count} player(s) connected.";

        PluginRuntime.Chat.AddSystem($"{ResolvePeerName(senderPeerId)} joined the game.");
        _joinCoordinator?.OnClientReady(senderPeerId);
        BroadcastPlayerList();
    }

    public void ConfirmSaveLoaded()
    {
        if (State != ConnectionState.ReceivingSave && State != ConnectionState.LoadingSave)
        {
            return;
        }

        State = ConnectionState.Connected;
        Mode = MultiplayerMode.Client;
        ActiveSince = DateTime.UtcNow;
        StatusMessage = "Connected to server, playing.";

        // clear any desync state — we've just loaded a fresh save
        DesyncState = DesyncState.None;
        _lastProcessedHostSequence = -1;
        _pendingAcks.Clear();

        // keep the overlay up until the host says everyone's in
        IsJoinSyncActive = true;
        JoinSyncPlayerName = "players";

        _transport.SendToHost(ProtocolCodec.WrapClientReady());
        _log.LogInfo("Save loaded, sent ClientReady to host. Waiting for JoinSyncEnd.");
    }
}
