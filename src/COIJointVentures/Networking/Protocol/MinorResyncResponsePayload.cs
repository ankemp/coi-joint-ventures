using System.Collections.Generic;
using System.Runtime.Serialization;
using COIJointVentures.Commands;

namespace COIJointVentures.Networking.Protocol;

[DataContract]
internal sealed class MinorResyncResponsePayload
{
    /// <summary>
    /// When true the command history didn't cover the requested range
    /// and the client must escalate to a full save resync (Phase 3).
    /// </summary>
    [DataMember(Order = 1)]
    public bool FullResyncRequired { get; set; }

    /// <summary>
    /// The missed envelopes in chronological order.
    /// Empty when <see cref="FullResyncRequired"/> is true.
    /// </summary>
    [DataMember(Order = 2)]
    public List<CommandEnvelope> Commands { get; set; } = new();
}
