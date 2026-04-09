namespace COIJointVentures.Networking.Protocol;

internal enum ProtocolMessageType : byte
{
    GameCommand = 0x10,
    StateChecksum = 0x15,
    JoinRequest = 0x01,
    JoinAccepted = 0x02,
    JoinRejected = 0x03,
    SaveData = 0x04,
    ClientReady = 0x05,
    SaveChunk = 0x06,
    SaveComplete = 0x07,
    JoinSyncBegin = 0x08,
    JoinSyncEnd = 0x09,
    ChatMessage = 0x20,
    PlayerList = 0x25,
    Waypoint = 0x30,

    // Phase 2: minor catch-up (replay missed commands)
    MinorResyncRequest  = 0x40,
    MinorResyncResponse = 0x41,

    // Phase 3: major resync (full save transfer)
    MajorResyncRequest  = 0x42,
}
