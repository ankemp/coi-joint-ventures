using System;
using COIJointVentures.Networking.Protocol;
using COIJointVentures.Runtime;

namespace COIJointVentures.Session;

internal sealed partial class MultiplayerSession
{
    public void SendChatMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _log.LogInfo($"SendChatMessage Mode={Mode}, Player='{LocalPlayerName}', Text='{text}'");

        if (_chatCommandHandler.TryHandle(text))
        {
            return;
        }

        var msg = new ChatMessagePayload
        {
            SenderName = LocalPlayerName,
            SenderPeerId = LocalPeerId,
            Kind = 0,
            Text = text
        };

        var wrapped = ProtocolCodec.WrapChatMessage(msg);
        if (Mode == MultiplayerMode.Host)
        {
            _transport.Broadcast(wrapped);
        }
        else
        {
            _transport.SendToHost(wrapped);
        }

        PluginRuntime.Chat.AddChat(LocalPlayerName, text);
        _log.LogInfo($"Broadcasted chat from '{LocalPlayerName}': {text}");
    }

    public void SendActionLog(string description)
    {
        var msg = new ChatMessagePayload
        {
            SenderName = LocalPlayerName,
            SenderPeerId = LocalPeerId,
            Kind = 1,
            Text = description
        };

        var wrapped = ProtocolCodec.WrapChatMessage(msg);
        if (Mode == MultiplayerMode.Host)
        {
            _transport.Broadcast(wrapped);
        }
        else
        {
            _transport.SendToHost(wrapped);
        }

        PluginRuntime.Chat.AddAction(LocalPlayerName, description);
    }

    public void SendSimControlLog(string description)
    {
        var msg = new ChatMessagePayload
        {
            SenderName = LocalPlayerName,
            SenderPeerId = LocalPeerId,
            Kind = 2,
            Text = description
        };

        var wrapped = ProtocolCodec.WrapChatMessage(msg);
        if (Mode == MultiplayerMode.Host)
        {
            _transport.Broadcast(wrapped);
        }
        else
        {
            _transport.SendToHost(wrapped);
        }

        PluginRuntime.Chat.AddSimControl(LocalPlayerName, description);
    }

    private void HandleChatMessage(string senderPeerId, byte[] payload)
    {
        var msg = ProtocolCodec.DecodeChatMessage(payload);
        _log.LogInfo($"HandleChatMessage Mode={Mode}, SenderPeerId='{senderPeerId}', SenderName='{msg.SenderName}', Text='{msg.Text}', Kind={msg.Kind}");

        // host stamps the real sender identity before relaying — don't trust the payload
        if (Mode == MultiplayerMode.Host && senderPeerId != HostPeerId)
        {
            msg.SenderPeerId = senderPeerId;
            msg.SenderName = ResolvePeerName(senderPeerId);
            _log.LogInfo($"Server received chat from '{senderPeerId}' ({msg.SenderName}): {msg.Text}");

            if (_pingHandler.TryHandleIncomingChatMessage(senderPeerId, msg))
            {
                return;
            }

            _transport.Broadcast(ProtocolCodec.WrapChatMessage(msg));
            _log.LogInfo($"Broadcasted chat from '{msg.SenderName}' to clients.");
        }

        // skip our own messages, already in the log
        if (string.Equals(msg.SenderPeerId, LocalPeerId, StringComparison.Ordinal))
        {
            return;
        }

        if (_pingHandler.TryHandleIncomingPong(msg))
        {
            return;
        }

        if (msg.Kind == 0)
        {
            PluginRuntime.Chat.AddChat(msg.SenderName, msg.Text);
        }
        else if (msg.Kind == 2)
        {
            PluginRuntime.Chat.AddSimControl(msg.SenderName, msg.Text);
        }
        else if (msg.Kind == 3)
        {
            PluginRuntime.Chat.AddSystem(msg.Text);
        }
        else
        {
            PluginRuntime.Chat.AddAction(msg.SenderName, msg.Text);
        }
    }

    public void HandlePingCommand(string text)
    {
        _pingHandler.HandlePingCommand(text);
    }

    public bool WasCommand(string text, string command)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var trimmed = text.Trim();
        var normalized = command.StartsWith("/") ? command : "/" + command;
        if (!trimmed.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (trimmed.Length == normalized.Length || char.IsWhiteSpace(trimmed[normalized.Length]))
        {
            _observedNativeCommands.Add(normalized);
            return true;
        }

        return false;
    }

    public void ActivateDebugPacketDrop()
    {
        _simulateDropNextPacket = true;
        PluginRuntime.Chat.AddSystem("HICCUP ACTIVE: The next host command will be silently dropped to force a desync.");
        _log.LogWarning("[DEBUG] Client activated /hiccup command.");
    }
}
