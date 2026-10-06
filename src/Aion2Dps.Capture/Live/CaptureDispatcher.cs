using System.Buffers;
using System.Collections.Concurrent;
using Aion2Dps.Contracts;

namespace Aion2Dps.Capture;

/// <summary>
/// Serialises live capture into ONE ordered dispatch: adapter reader threads only copy their packets into a single FIFO
/// queue (arrival = capture order), and one dedicated thread feeds them to the <see cref="GameFlowTracker"/>, drives its
/// clock (~4×/s) and runs actions queued by the supervisor (e.g. ending a flow whose process exited). Therefore every
/// sink call (flow sinks, factory create/release, session discontinuities) happens on that one thread, in order.
/// The thread emits <see cref="DiscontinuityReason.CaptureRestarted"/> first and releases every flow when completed.
/// Tracker access is additionally guarded by the shared <c>sync</c> lock so the supervisor can read its state.
/// </summary>
internal sealed class CaptureDispatcher
{
    private readonly GameFlowTracker _tracker;
    private readonly object _sync;
    private readonly long _maxQueuedBytes;
    private readonly int _maxQueuedPackets;
    private readonly BlockingCollection<WorkItem> _queue = new(new ConcurrentQueue<WorkItem>());
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new(false);
    private long _queuedBytes;
    private int _queuedPackets;
    private long _dropped;
    private long _processed;

    public CaptureDispatcher(GameFlowTracker tracker, object sync, long maxQueuedBytes, int maxQueuedPackets = 65536)
    {
        _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _maxQueuedBytes = maxQueuedBytes > 0 ? maxQueuedBytes : long.MaxValue;
        _maxQueuedPackets = Math.Max(1, maxQueuedPackets);
        _thread = new Thread(Loop) { IsBackground = true, Name = "Aion2Dps capture dispatch" };
    }

    /// <summary>Packets dropped because the queue held more than the byte limit.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Packets handed to the tracker.</summary>
    public long Processed => Interlocked.Read(ref _processed);

    /// <summary>Managed thread id of the dispatch thread (diagnostics/tests).</summary>
    public int ThreadId => _thread.ManagedThreadId;

    public bool IsRunning => _thread.IsAlive;
    internal long QueuedBytes => Interlocked.Read(ref _queuedBytes);
    internal int QueuedPackets => Volatile.Read(ref _queuedPackets);

    /// <summary>Starts the thread and waits until CaptureRestarted has been delivered.</summary>
    public void Start()
    {
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Called by the adapter reader threads: copies the frame into the queue (never blocks on the tracker).</summary>
    public void EnqueuePacket(int sourceId, DateTime timestampUtc, int linkType, ReadOnlySpan<byte> frame)
    {
        if (_queue.IsAddingCompleted) return;
        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, frame.Length));
        if (!TryReserve(buffer.Length))
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (Interlocked.Increment(ref _dropped) % 1000 == 1)
                AppLog.Warn("Capture", $"Capture dispatch queue is full; {Dropped} packet(s) dropped so far");
            return;
        }

        frame.CopyTo(buffer);
        try
        {
            _queue.Add(new WorkItem(sourceId, timestampUtc, linkType, buffer, frame.Length, null));
        }
        catch (InvalidOperationException)
        {
            // completed while adding (stopping)
            Release(buffer.Length);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private bool TryReserve(int bytes)
    {
        while (true)
        {
            int count = Volatile.Read(ref _queuedPackets);
            if (count >= _maxQueuedPackets) return false;
            if (Interlocked.CompareExchange(ref _queuedPackets, count + 1, count) == count) break;
        }

        while (true)
        {
            long held = Interlocked.Read(ref _queuedBytes);
            if (bytes > _maxQueuedBytes - held)
            {
                Interlocked.Decrement(ref _queuedPackets);
                return false;
            }
            if (Interlocked.CompareExchange(ref _queuedBytes, held + bytes, held) == held) return true;
        }
    }

    private void Release(int bytes)
    {
        Interlocked.Add(ref _queuedBytes, -bytes);
        Interlocked.Decrement(ref _queuedPackets);
    }

    /// <summary>Runs <paramref name="action"/> on the dispatch thread, in order with the packets queued before it.</summary>
    public void Enqueue(Action<GameFlowTracker> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            _queue.Add(new WorkItem(0, default, 0, null, 0, action));
        }
        catch (InvalidOperationException)
        {
            // stopping
        }
    }

    /// <summary>Processes what is still queued, releases every flow and ends the thread.</summary>
    public void Complete()
    {
        _queue.CompleteAdding();
        // The supervisor owns this wait. Public Stop has its own bounded wait and must retain this run until all
        // packets and flow releases finish, rather than losing the tail of the recording after a slow sink.
        if (Thread.CurrentThread != _thread && _thread.IsAlive) _thread.Join();
    }

    private void Loop()
    {
        var lastTick = DateTime.UtcNow;
        try
        {
            lock (_sync) _tracker.OnSessionDiscontinuity(DiscontinuityReason.CaptureRestarted);
        }
        catch (Exception ex)
        {
            AppLog.Error("Capture", "CaptureRestarted delivery failed", ex);
        }
        finally
        {
            _started.Set();
        }

        try
        {
            while (!_queue.IsCompleted)
            {
                if (_queue.TryTake(out var item, 100)) Process(item);
                var now = DateTime.UtcNow;
                // A queued retransmit can fill a hole whose capture timestamp is already old. Let queued packets drive
                // the capture clock before applying the wall clock, otherwise a recoverable hole becomes lost data.
                if (_queue.Count == 0 && now - lastTick >= TimeSpan.FromMilliseconds(250))
                {
                    lastTick = now;
                    lock (_sync) Safe(() => _tracker.Tick(now), "Tick");
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // completed
        }
        finally
        {
            lock (_sync) Safe(() => _tracker.UnlockAll(DateTime.UtcNow, "capture stopped"), "Releasing the game flows");
        }
    }

    private void Process(WorkItem item)
    {
        if (item.Action is { } action)
        {
            lock (_sync) Safe(() => action(_tracker), "Capture action");
            return;
        }

        try
        {
            lock (_sync)
            {
                try
                {
                    _tracker.OnPacket(item.TimestampUtc, item.LinkType, item.Buffer.AsSpan(0, item.Length), item.SourceId);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Capture", "Packet processing failed", ex);
                }
            }
        }
        finally
        {
            Interlocked.Increment(ref _processed);
            Release(item.Buffer!.Length);
            ArrayPool<byte>.Shared.Return(item.Buffer!);
        }
    }

    private static void Safe(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Error("Capture", $"{what} failed", ex);
        }
    }

    private readonly record struct WorkItem(int SourceId, DateTime TimestampUtc, int LinkType, byte[]? Buffer, int Length,
        Action<GameFlowTracker>? Action);
}
