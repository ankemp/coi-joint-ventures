using System;
using COIJointVentures.Networking.Protocol;

namespace COIJointVentures.Session;

internal sealed partial class MultiplayerSession
{
    public event Action<WaypointPayload>? WaypointReceived;

    public void SendWaypoint(float x, float y, float z)
    {
        var wp = new WaypointPayload
        {
            SenderPeerId = LocalPeerId,
            SenderName = LocalPlayerName,
            X = x,
            Y = y,
            Z = z,
            ColorIndex = Mode == MultiplayerMode.Host ? GetOrAssignColor(LocalPeerId) : _localColorIndex
        };

        var wrapped = ProtocolCodec.WrapWaypoint(wp);
        if (Mode == MultiplayerMode.Host)
        {
            _transport.Broadcast(wrapped);
        }
        else
        {
            _transport.SendToHost(wrapped);
        }

        // show it locally too
        WaypointReceived?.Invoke(wp);
    }

    private void HandleWaypoint(string senderPeerId, byte[] payload)
    {
        var wp = ProtocolCodec.DecodeWaypoint(payload);

        // host stamps sender, assigns color, and relays
        if (Mode == MultiplayerMode.Host && senderPeerId != HostPeerId)
        {
            wp.SenderPeerId = senderPeerId;
            wp.SenderName = ResolvePeerName(senderPeerId);
            wp.ColorIndex = GetOrAssignColor(senderPeerId);
            _transport.Broadcast(ProtocolCodec.WrapWaypoint(wp));
        }

        // don't double-show our own
        if (string.Equals(wp.SenderPeerId, LocalPeerId, StringComparison.Ordinal))
        {
            return;
        }

        WaypointReceived?.Invoke(wp);
    }
}
