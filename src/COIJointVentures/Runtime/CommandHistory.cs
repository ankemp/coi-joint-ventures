using System;
using System.Collections.Generic;
using COIJointVentures.Commands;

namespace COIJointVentures.Runtime;

/// <summary>
/// Fixed-size ring buffer that records the last <see cref="Capacity"/> envelopes
/// broadcast by the host. Used by the minor-resync path to replay missed
/// commands without requiring a full save transfer.
/// </summary>
internal sealed class CommandHistory
{
    public const int Capacity = 512;

    private readonly CommandEnvelope?[] _buffer = new CommandEnvelope[Capacity];
    private int _head;   // next write slot
    private int _count;  // number of valid entries (capped at Capacity)

    public void Record(CommandEnvelope envelope)
    {
        _buffer[_head] = envelope;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity)
            _count++;
    }

    /// <summary>
    /// Returns all recorded envelopes whose <see cref="CommandEnvelope.Sequence"/>
    /// is &gt;= <paramref name="fromSequence"/>, in chronological order.
    /// </summary>
    public List<CommandEnvelope> GetSince(long fromSequence)
    {
        var result = new List<CommandEnvelope>();
        // oldest entry in the ring
        var start = _count < Capacity ? 0 : _head;
        for (var i = 0; i < _count; i++)
        {
            var envelope = _buffer[(start + i) % Capacity];
            if (envelope != null && envelope.Sequence >= fromSequence)
                result.Add(envelope);
        }
        return result;
    }

    /// <summary>Number of envelopes currently stored (0 – <see cref="Capacity"/>).</summary>
    public int Count => _count;

    /// <summary>
    /// The sequence number of the oldest recorded envelope, or <c>null</c> if empty.
    /// </summary>
    public long? OldestSequence
    {
        get
        {
            if (_count == 0) return null;
            var oldestSlot = _count < Capacity ? 0 : _head;
            return _buffer[oldestSlot]?.Sequence;
        }
    }

    public void Clear()
    {
        Array.Clear(_buffer, 0, _buffer.Length);
        _head = 0;
        _count = 0;
    }
}
