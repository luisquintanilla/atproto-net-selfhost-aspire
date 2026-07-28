using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace AtProto.Firehose;

/// <summary>One sequenced, already-encoded firehose frame.</summary>
public readonly record struct SequencedFrame(long Seq, byte[] Frame);

/// <summary>
/// In-memory firehose sequencer + fan-out, shared by the PDS and (later) the Relay. Assigns a
/// monotonic <c>seq</c>, keeps a bounded backfill log, and broadcasts each frame to live
/// subscribers. <see cref="Subscribe"/> replays the backfill after a cursor and then streams live
/// with no gaps or duplicates: registration and backfill snapshot happen under the same lock that
/// <see cref="Publish"/> holds, so every frame is delivered exactly once and in order.
/// </summary>
public sealed class FirehoseBroadcaster
{
    private readonly object _gate = new();
    private readonly List<SequencedFrame> _backfill = new();
    private readonly Dictionary<Guid, Channel<SequencedFrame>> _subscribers = new();
    private readonly int _backfillCapacity;
    private long _seq;

    /// <param name="backfillCapacity">Max frames retained for cursor replay.</param>
    /// <param name="initialSeq">
    /// Seq to resume from (0 for a fresh stream). A Relay seeds this from its persisted global seq so
    /// the sequence stays monotonic across restarts and downstream cursors remain valid.
    /// </param>
    public FirehoseBroadcaster(int backfillCapacity = 4096, long initialSeq = 0)
    {
        _backfillCapacity = backfillCapacity;
        _seq = initialSeq;
    }

    /// <summary>The highest seq assigned so far (0 before any event).</summary>
    public long CurrentSeq
    {
        get { lock (_gate) return _seq; }
    }

    /// <summary>Reserve the next monotonic seq (used to stamp an event before encoding it).</summary>
    public long NextSeq()
    {
        lock (_gate)
            return ++_seq;
    }

    /// <summary>Publish an already-encoded, already-sequenced frame to backfill and all subscribers.</summary>
    public void Publish(SequencedFrame frame)
    {
        lock (_gate)
            PublishLocked(frame);
    }

    /// <summary>
    /// Atomically reserve the next seq, encode the frame with it, and publish — all under one lock.
    /// This is the safe path when multiple producers publish concurrently (e.g. a Relay fanning in
    /// several upstream hosts): seq assignment and backfill append stay strictly in order, so a
    /// subscriber resuming from a cursor never sees a gap or an out-of-order frame. Returns the seq.
    /// </summary>
    public long PublishNext(Func<long, byte[]> encodeWithSeq)
    {
        ArgumentNullException.ThrowIfNull(encodeWithSeq);
        lock (_gate)
        {
            long seq = ++_seq;
            PublishLocked(new SequencedFrame(seq, encodeWithSeq(seq)));
            return seq;
        }
    }

    private void PublishLocked(SequencedFrame frame)
    {
        _backfill.Add(frame);
        int overflow = _backfill.Count - _backfillCapacity;
        if (overflow > 0)
            _backfill.RemoveRange(0, overflow);

        foreach (Channel<SequencedFrame> channel in _subscribers.Values)
            channel.Writer.TryWrite(frame);
    }

    /// <summary>
    /// Subscribe from an optional cursor (exclusive). Yields retained frames with <c>seq &gt; cursor</c>
    /// first, then live frames, exactly once each.
    /// </summary>
    public async IAsyncEnumerable<SequencedFrame> Subscribe(
        long? cursor, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<SequencedFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var id = Guid.NewGuid();
        List<SequencedFrame> backfill;
        lock (_gate)
        {
            backfill = cursor is long c
                ? _backfill.Where(f => f.Seq > c).ToList()
                : new List<SequencedFrame>();
            _subscribers[id] = channel;
        }

        try
        {
            foreach (SequencedFrame frame in backfill)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return frame;
            }

            await foreach (SequencedFrame frame in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return frame;
        }
        finally
        {
            lock (_gate)
                _subscribers.Remove(id);
        }
    }
}
