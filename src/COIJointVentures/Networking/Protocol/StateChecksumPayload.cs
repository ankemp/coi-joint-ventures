using System.Runtime.Serialization;

namespace COIJointVentures.Networking.Protocol;

[DataContract]
internal sealed class StateChecksumPayload
{
    [DataMember(Order = 1)]
    public long Sequence { get; set; }

    [DataMember(Order = 2)]
    public int Checksum { get; set; }
}
