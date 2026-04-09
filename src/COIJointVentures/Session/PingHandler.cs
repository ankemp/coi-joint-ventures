using System;
using BepInEx.Logging;
using COIJointVentures.Networking;
using COIJointVentures.Networking.Protocol;
using COIJointVentures.Runtime;

namespace COIJointVentures.Session;

internal sealed class PingHandler
{
    private readonly MultiplayerSession _session;
    private readonly INetworkTransport _transport;
    private readonly ManualLogSource _log;
    private DateTime? _lastPingSentAt;

    public PingHandler(MultiplayerSession session, INetworkTransport transport, ManualLogSource log)
    {
        _session = session;
        _transport = transport;
        _log = log;
    }

    public void HandlePingCommand(string text)
    {
        _log.LogInfo("Executed /ping command.");

        if (_session.Mode == MultiplayerMode.Host)
        {
            PluginRuntime.Chat.AddSystem("pong (0ms)");
            _log.LogInfo("Responded to /ping with pong on host.");
            return;
        }

        if (!_lastPingSentAt.HasValue)
        {
            _lastPingSentAt = DateTime.UtcNow;
        }
        else
        {
            PluginRuntime.Chat.AddSystem("Ping already in progress.");
            _log.LogInfo("Ignored /ping command: previous ping still pending.");
            return;
        }

        var pingMsg = new ChatMessagePayload
        {
            SenderName = _session.LocalPlayerName,
            SenderPeerId = _session.LocalPeerId,
            Kind = 0,
            Text = text
        };

        _transport.SendToHost(ProtocolCodec.WrapChatMessage(pingMsg));
        PluginRuntime.Chat.AddSystem("Ping sent to server.");
        _log.LogInfo("Sent ping request to server.");
    }

    public bool TryHandleIncomingChatMessage(string senderPeerId, ChatMessagePayload msg)
    {
        if (_session.Mode != MultiplayerMode.Host || msg.Kind != 0)
        {
            return false;
        }

        if (!msg.Text.Trim().Equals("/ping", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var pong = new ChatMessagePayload
        {
            SenderName = "Server",
            SenderPeerId = _session.HostPeerId,
            Kind = 3,
            Text = "pong"
        };

        _transport.SendToClient(senderPeerId, ProtocolCodec.WrapChatMessage(pong));
        _log.LogInfo($"Received ping from '{senderPeerId}', replied with pong.");
        return true;
    }

    public bool TryHandleIncomingPong(ChatMessagePayload msg)
    {
        if (_session.Mode == MultiplayerMode.Host || msg.Kind != 3)
        {
            return false;
        }

        if (!msg.Text.Trim().Equals("pong", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!_lastPingSentAt.HasValue)
        {
            return false;
        }

        var elapsedMs = (long)(DateTime.UtcNow - _lastPingSentAt.Value).TotalMilliseconds;
        PluginRuntime.Chat.AddSystem($"pong ({elapsedMs}ms)");
        _lastPingSentAt = null;
        return true;
    }
}
