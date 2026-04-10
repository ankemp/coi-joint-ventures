namespace COIJointVentures.Session;

internal enum DesyncState
{
    /// <summary>Everything is in sync.</summary>
    None,

    /// <summary>
    /// One or more host commands were not received.
    /// A minor catch-up may be able to recover this.
    /// </summary>
    SequenceGap,

    /// <summary>
    /// A state checksum mismatch was detected.
    /// Only a full save resync can recover this.
    /// </summary>
    SimulationDesync,
}
