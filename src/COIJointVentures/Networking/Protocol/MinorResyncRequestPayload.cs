using System.Runtime.Serialization;

namespace COIJointVentures.Networking.Protocol;

[DataContract]
internal sealed class MinorResyncRequestPayload
{
    /// <summary>
    /// The highest sequence number the client successfully processed
    /// before the gap was detected.
    /// </summary>
    [DataMember(Order = 1)]
    public long LastProcessedSequence { get; set; }
}
