using System;
using System.Collections.Generic;
using BepInEx.Logging;
using COIJointVentures.Networking;
using COIJointVentures.Networking.Protocol;
using COIJointVentures.Runtime;

namespace COIJointVentures.Session;

internal sealed class LatencyTracker
{
    private const double PingIntervalSeconds = 3.0;

    private readonly MultiplayerSession _session;
    private readonly INetworkTransport _transport;
    private readonly ManualLogSource _log;

    // Host-side: in-flight pings and last-measured values
    private readonly Dictionary<string, (long Token, DateTime SentAt)> _pendingPings = new();
    private readonly Dictionary<string, int> _latencyByPeerId = new();
    private DateTime _lastPingRound = DateTime.MinValue;

    public LatencyTracker(MultiplayerSession session, INetworkTransport transport, ManualLogSource log)
    {
        _session = session;
        _transport = transport;
        _log = log;
    }

    /// <summary>
    /// Called every game frame. On the host, periodically sends a PingRequest to every active peer.
    /// Returns true when new latency data is ready (caller should broadcast the player list).
    /// </summary>
    public bool Tick()
    {
        if (_session.Mode != MultiplayerMode.Host) return false;

        var now = DateTime.UtcNow;
        if ((now - _lastPingRound).TotalSeconds < PingIntervalSeconds) return false;

        _lastPingRound = now;

        foreach (var peerId in _session.ActivePeers)
        {
            var token = now.Ticks;
            _pendingPings[peerId] = (token, now);
            _transport.SendToClient(peerId, ProtocolCodec.WrapPingRequest(new PingPayload { Token = token, PeerId = peerId }));
        }

        return false; // broadcast happens when responses arrive
    }

    /// <summary>Client: host sent a PingRequest — echo it back immediately.</summary>
    public void HandlePingRequest(PingPayload payload)
    {
        if (_session.Mode == MultiplayerMode.Host) return;
        _transport.SendToHost(ProtocolCodec.WrapPingResponse(payload));
    }

    /// <summary>
    /// Host: client echoed our PingRequest — measure RTT and record it.
    /// Returns true so the caller knows to refresh the player-list broadcast.
    /// </summary>
    public bool HandlePingResponse(PingPayload payload)
    {
        if (_session.Mode != MultiplayerMode.Host) return false;

        if (!_pendingPings.TryGetValue(payload.PeerId, out var pending) || pending.Token != payload.Token)
            return false;

        _pendingPings.Remove(payload.PeerId);
        var rtt = (int)(DateTime.UtcNow - pending.SentAt).TotalMilliseconds;
        _latencyByPeerId[payload.PeerId] = rtt;
        _log.LogInfo($"Latency for '{payload.PeerId}': {rtt}ms");
        return true;
    }

    /// <summary>Returns the last measured RTT for a peer in milliseconds, or -1 if unknown.</summary>
    public int GetLatency(string peerId)
    {
        return _latencyByPeerId.TryGetValue(peerId, out var ms) ? ms : -1;
    }

    /// <summary>Handles the /ping chat command. Shows the cached latency for the local player.</summary>
    public void HandlePingCommand()
    {
        _log.LogInfo("Executed /ping command.");

        if (_session.Mode == MultiplayerMode.Host)
        {
            PluginRuntime.Chat.AddSystem("pong (0ms)");
            return;
        }

        // On the client, the host-measured RTT is included in the PlayerList broadcast.
        // Find our own entry to report it.
        var localId = _session.LocalPeerId;
        foreach (var p in _session.PlayerList)
        {
            if (string.Equals(p.Name, _session.LocalPlayerName, StringComparison.Ordinal) && p.LatencyMs >= 0)
            {
                PluginRuntime.Chat.AddSystem($"pong ({p.LatencyMs}ms)");
                return;
            }
        }

        PluginRuntime.Chat.AddSystem("Ping measurement pending...");
    }

    /// <summary>Clears all state on disconnect.</summary>
    public void Clear()
    {
        _pendingPings.Clear();
        _latencyByPeerId.Clear();
        _lastPingRound = DateTime.MinValue;
    }
}
