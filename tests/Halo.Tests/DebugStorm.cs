using System.Diagnostics;
using Halo.Shared;

namespace Halo.Tests;

/// <summary>
/// Drives <see cref="Log"/>'s Debug shed path deterministically.
///
/// <para><b>Why this is not just a for-loop.</b> A single-threaded flood cannot reach the shed
/// threshold: the pump absorbs records faster than one caller can format them (measured
/// 2026-09-17: 200,004 records in 171 ms, zero drops), so <c>Queue.Count</c> never approaches
/// three quarters of 8192 and the shed branch is never taken. Raising the record count does not
/// help — it is a rate problem, not a volume problem.</para>
///
/// <para><b>The lever.</b> <see cref="Log.SetMaxFileBytesForTests"/> set to 1 byte makes every
/// batch exceed the size cap, so the pump pays a stream close, a <c>File.Move</c> and a reopen for
/// each 64 KB batch instead of a single append. Measured on this machine that drops the sink from
/// well over a million records a second to roughly 34 MB/s, which any caller outruns. Several
/// producer threads are used on top of that purely for margin on slower machines. Measured
/// 2026-09-17: the shed threshold is reached in 13-34 ms across 1, 2, 4, 8 and 16 threads, with or
/// without the stall on a 20-core box; with the stall it holds on one thread alone.</para>
///
/// <para>Rolling is a real, documented behaviour of the logger rather than an injected fault, so
/// nothing here depends on a seam that production does not have. <see cref="LiftStall"/> takes the
/// cap away again as soon as the queue is deep, so the records under test are not exposed to the
/// rolling churn while they drain.</para>
/// </summary>
internal sealed class DebugStorm : IDisposable
{
    private readonly Thread[] _threads;
    private bool _stop;
    private long _produced;

    public DebugStorm(int payloadBytes = 900, int threads = 4)
    {
        Log.SetMaxFileBytesForTests(1);
        string payload = new string('d', payloadBytes);
        _threads = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
        {
            while (!Volatile.Read(ref _stop))
            {
                Log.Debug(payload);
                Interlocked.Increment(ref _produced);
            }
        })
        {
            IsBackground = true,
            Name = "halo-test-debug-storm",
        }).ToArray();
        foreach (Thread t in _threads) t.Start();
    }

    /// <summary>How many Debug records the storm has asked the logger to write.</summary>
    public long Produced => Interlocked.Read(ref _produced);

    /// <summary>Block until the logger has actually shed something. False means the shed path was
    /// never entered, which makes any assertion about shedding vacuous.</summary>
    public bool RunUntilShedding(int timeoutMs = 30_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            DeepestQueueSeen = Math.Max(DeepestQueueSeen, LogTestBase.QueuedNow());
            if (LogTestBase.DroppedQueueFull() > 0) return true;
            Thread.Sleep(1);
        }
        return false;
    }

    /// <summary>The deepest queue this storm was seen to build, sampled while it ran. Evidence that
    /// a drop really was queue pressure rather than something else.</summary>
    public long DeepestQueueSeen { get; private set; }

    /// <summary>Stop rolling on every batch, leaving the queue as deep as the storm made it.</summary>
    public void LiftStall() => Log.SetMaxFileBytesForTests(long.MaxValue);

    /// <summary>Stop producing but leave the sink stalled, so the backlog the storm built stays
    /// there while something else is measured against it.</summary>
    public void StopProducers()
    {
        Volatile.Write(ref _stop, true);
        foreach (Thread t in _threads) t.Join();
    }

    public void Stop()
    {
        StopProducers();
        LiftStall();
    }

    public void Dispose() => Stop();
}
