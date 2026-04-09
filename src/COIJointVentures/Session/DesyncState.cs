namespace COIJointVentures.Session;

internal enum DesyncState
{
    /// <summary>Everything is in sync.</summary>
    None,

    /// <summary>
    /// One or more host commands were not received.
    /// A minor catch-up (Phase 2) may be able to recover this.
    /// </summary>
    SequenceGap,

    /// <summary>
    /// A state checksum mismatch was detected.
    /// Only a full save resync (Phase 3) can recover this.
    /// </summary>
    SimulationDesync,
}
