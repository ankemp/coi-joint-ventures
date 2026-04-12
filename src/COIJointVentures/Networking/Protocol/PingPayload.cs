using System.Runtime.Serialization;

namespace COIJointVentures.Networking.Protocol;

[DataContract]
internal sealed class PingPayload
{
    /// <summary>UTC ticks at the moment the host sent the request. Doubles as the correlation token.</summary>
    [DataMember] public long Token { get; set; }

    /// <summary>The peer ID this ping targets (or came from).</summary>
    [DataMember] public string PeerId { get; set; } = string.Empty;
}
