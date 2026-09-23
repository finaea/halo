using System.Diagnostics;
using Halo.Shared;

namespace Halo.Tests;

/// <summary>
/// The flush contract, and the one regression this whole suite exists for.
///
/// <para><b>The bug.</b> <c>Flush()</c> used to wait on <c>Queue.Count &gt; 0</c>. The pump's
/// <c>Queue.Take()</c> had already emptied the queue and then sat in its batching wait before
/// writing, so <c>Flush()</c> saw a count of zero and returned <i>successfully</i> while the
/// records were still in the pump's StringBuilder — and the background thread died with the
/// process a moment later. Every graceful shutdown silently lost its last lines, including the
/// widgets' fatal handler. Nothing about that is visible by reading the code, which is why it is
/// pinned here: <see cref="AcceptedBeforeFlush_IsOnDiskWhenFlushReturns"/> must read the file back
/// with no sleep of its own.</para>
/// </summary>
[Collection(LogTestCollection.Name)]
public sealed class LogFlushTests : LogTestBase
{
    public LogFlushTests() : base("flush") { }

    /// <summary>
    /// THE regression test. Records accepted before <c>Flush()</c> are on disk the instant it
    /// returns <c>Reached</c> — asserted by reading the file with no wait, no retry and no poll.
    ///
    /// <para>Volume on purpose: one record could be persisted by luck between the call and the
    /// read. Fifty guarantee the pump is inside its 20 ms batching window when <c>Flush</c> is
    /// called, which is exactly the state the old implementation reported success from.</para>
    /// </summary>
    [Fact]
    public void AcceptedBeforeFlush_IsOnDiskWhenFlushReturns()
    {
        for (int i = 0; i < 50; i++) Log.Info($"{Tag} queued record {i}");

        FlushResult result = Log.Flush(10_000);

        Assert.True(result.Reached, $"Flush did not reach its target: {result} · {Log.Health}");

        // No Thread.Sleep, no polling: "Reached" has to mean the bytes are in the file already.
        string text = LiveText();
        var missing = Enumerable.Range(0, 50)
            .Where(i => !text.Contains($"{Tag} queued record {i}", StringComparison.Ordinal))
            .ToArray();
        Assert.True(missing.Length == 0,
            $"Flush returned Reached=true but {missing.Length}/50 records were not on disk "
            + $"(first missing: {(missing.Length > 0 ? missing[0] : -1)}). {result} · {Log.Health}");
        Assert.Equal(0, result.Dropped);
    }

    /// <summary>A synchronous record is on disk before the call returns — no <c>Flush</c> at all.
    /// This is what a crash handler depends on, where there is no "later".</summary>
    [Fact]
    public void Durable_IsOnDiskBeforeItReturns()
    {
        Log.Durable(LogLevel.Info, $"{Tag} durable breadcrumb", "lifecycle");

        Assert.Contains($"{Tag} durable breadcrumb", LiveText());
    }

    /// <summary>
    /// A durable record must not land ahead of the queued records that came before it. For the
    /// crash handler — the caller this API exists for — the alternative puts the crash line in the
    /// file above the events that caused it.
    /// </summary>
    [Fact]
    public void Durable_DoesNotOvertakeRecordsQueuedBeforeIt()
    {
        for (int i = 0; i < 200; i++) Log.Info($"{Tag} before {i:000}");
        Log.Durable(LogLevel.Error, $"{Tag} the durable one", "lifecycle");

        FlushResult result = Log.Flush(10_000);
        Assert.True(result.Reached, $"{result} · {Log.Health}");

        string[] tagged = TaggedLines(AllText());
        int durableAt = Array.FindIndex(tagged, l => l.Contains("the durable one", StringComparison.Ordinal));
        Assert.True(durableAt >= 0, $"the durable record never reached the file. {Log.Health}");

        int lastQueuedAt = Array.FindLastIndex(tagged, l => l.Contains(" before ", StringComparison.Ordinal));
        Assert.True(lastQueuedAt < durableAt,
            $"the durable record is at line {durableAt} but a record queued before it is at "
            + $"{lastQueuedAt} — the durable write overtook the backlog.");

        // And nothing queued before it was simply dropped on the floor to make that true.
        Assert.Equal(200, tagged.Count(l => l.Contains(" before ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// When the drain cannot finish inside its budget the record is still written, and the file
    /// says so: "ordered, or explicitly told it isn't", never silently reordered.
    ///
    /// <para><b>How the pump is stalled.</b> <c>SetMaxFileBytesForTests(1)</c> makes every batch
    /// exceed the size cap, so the pump pays a close + <c>File.Move</c> + reopen for each one.
    /// Measured on this machine that is ~1.9 ms per 64 KB batch, i.e. about 34 MB/s, so a ~16 MB
    /// backlog needs roughly 480 ms to clear against <c>Durable</c>'s 150 ms budget — a ~3x margin
    /// rather than a coin flip. The 2000 records stay under the 8192 queue capacity so nothing is
    /// shed, which keeps the counters' arithmetic honest.</para>
    /// </summary>
    [Fact]
    public void Durable_ThatCannotDrainInTime_IsStillWrittenAndSaysWhatItLeftBehind()
    {
        Log.SetMaxFileBytesForTests(1);
        string payload = new string('p', 8 * 1024);
        for (int i = 0; i < 2000; i++) Log.Info($"{Tag} backlog {i} {payload}");

        long queuedBefore = QueuedNow();
        Assert.True(queuedBefore > 0,
            $"the pump was not stalled, so there is no timeout path to test. {Log.Health}");

        var sw = Stopwatch.StartNew();
        Log.Durable(LogLevel.Error, $"{Tag} forced out ahead of the backlog", "lifecycle");
        long durableMs = sw.ElapsedMilliseconds;

        // Let the rest drain without spraying another few hundred rolled files at the disk.
        Log.SetMaxFileBytesForTests(long.MaxValue);

        Assert.True(WaitForOnDisk($"{Tag} forced out ahead of the backlog"),
            $"the durable record was never written. {Log.Health}");
        Assert.True(WaitForOnDisk("queued record(s) were still unwritten"),
            $"the durable record was forced out after {durableMs} ms with {queuedBefore} queued, "
            + $"but nothing in the file admits the backlog was left behind. {Log.Health}");
    }

    /// <summary>
    /// A budget it cannot meet returns <c>Reached == false</c> with the real shortfall, promptly —
    /// it neither hangs nor claims success. Then a generous budget reaches the same target, so the
    /// honest "no" is a budget answer and not a wedged logger.
    /// </summary>
    [Fact]
    public void Flush_ThatCannotMeetItsTarget_SaysSoInsteadOfLying()
    {
        Log.SetMaxFileBytesForTests(1); // stall the sink; see the test above for why this works
        string payload = new string('p', 900);
        for (int i = 0; i < 2000; i++) Log.Info($"{Tag} pending {i} {payload}");

        var sw = Stopwatch.StartNew();
        FlushResult tooSoon = Log.Flush(1);
        long elapsed = sw.ElapsedMilliseconds;

        Assert.False(tooSoon.Reached,
            $"Flush(1) claimed it had persisted everything. {tooSoon} · {Log.Health}");
        Assert.True(tooSoon.Persisted < tooSoon.Accepted,
            $"Flush reported no shortfall while refusing to confirm the target: {tooSoon}");
        Assert.True(elapsed < 2000, $"Flush(1) took {elapsed} ms — it is meant to give up, not hang.");

        Log.SetMaxFileBytesForTests(long.MaxValue);
        FlushResult given = Log.Flush(60_000);
        Assert.True(given.Reached, $"a 60 s budget still could not drain 2000 records: {given} · {Log.Health}");
        Assert.Contains($"{Tag} pending 1999 ", AllText());
    }

    /// <summary>
    /// Regression test for a bug this suite found and <c>Log.Outstanding</c> now fixes. Kept in
    /// full because the arithmetic is the kind that gets "simplified" back into being wrong.
    ///
    /// <para><c>Flush</c> compared <c>target = _accepted</c> against
    /// <c>done = _persisted + _droppedQueueFull + _droppedWriteFailed</c>,
    /// but <c>_droppedQueueFull</c> counts records that were <b>never in</b> <c>_accepted</c>: the
    /// Debug shed path increments it without touching <c>_accepted</c> (<c>Log.cs:319-323</c>), and
    /// the queue-full path increments it and then decrements <c>_accepted</c> back down
    /// (<c>Log.cs:326-328</c>). So every shed record inflates <c>done</c> by one against a target it
    /// never contributed to, and <c>Flush</c> returns <c>Reached == true</c> with up to that many
    /// records still sitting in the queue — the exact failure this feature was built to kill,
    /// reintroduced through the drop accounting. <c>DrainBefore</c> carried its own copy of the
    /// same sum, so <c>Durable</c>'s ordering guarantee dissolved after a shed too.</para>
    ///
    /// <para><b>Measured 2026-09-17 by running this very test with the Skip removed:</b>
    /// <c>Flush(20000)</c> returned "flushed (6072 persisted, 722 dropped)" while Health still read
    /// <c>accepted=6283 persisted=6072 queued=142</c>. The arithmetic is plain: target 6283, done
    /// 6072 + 722 = 6794, so the 722 shed records covered a 211-record shortfall and 142 records
    /// were reported as flushed while still in the queue. A separate probe with a heavier storm hid
    /// 6015 unwritten records the same way (<c>persisted=6072 accepted=12077 queued=6015</c>), so
    /// the size of the lie scales with how much was shed.</para>
    ///
    /// <para>Only reachable once something has been shed, which is why every other test in this
    /// file keeps its drop count at zero and why the shedding tests were written to poll the file
    /// rather than trust <c>Flush</c>.</para>
    ///
    /// <para><b>The fix:</b> <c>_droppedQueueFull</c> no longer appears on the discharge side.
    /// Those records are rejected *before* acceptance, so they were never a debt to settle. Only
    /// persistence and <c>_droppedWriteFailed</c> — the sink refusing a record we had already
    /// accepted — discharge one, and <c>Flush</c> and <c>DrainBefore</c> now share the single
    /// <c>Outstanding</c> definition instead of each carrying their own copy of the sum.</para>
    /// </summary>
    [Fact]
    public void Flush_AfterShedding_StillTellsTheTruth()
    {
        Log.ResetForTests(Dir, "flush", LogLevel.Debug);
        using var storm = new DebugStorm(payloadBytes: 900, threads: 4);
        Assert.True(storm.RunUntilShedding(), $"could not reach the shed threshold. {Log.Health}");

        Log.Warn($"{Tag} written during the storm");

        // Producers stop, the stall does NOT: the backlog has to still be there while Flush runs,
        // or the queue drains inside the few milliseconds the call takes and the bug hides.
        storm.StopProducers();
        long queuedBefore = QueuedNow();
        Assert.True(queuedBefore > 100, $"no backlog left to flush. {Log.Health}");

        var sw = Stopwatch.StartNew();
        FlushResult result = Log.Flush(20_000);
        long flushMs = sw.ElapsedMilliseconds;
        long stillQueued = QueuedNow();

        Assert.True(result.Reached, $"{result} · {Log.Health}");

        // This is the assertion that failed before the fix: Reached must mean nothing is left outstanding.
        Assert.True(stillQueued == 0,
            $"Flush returned {result} after {flushMs} ms, but {stillQueued} of the {queuedBefore} "
            + $"queued records are still unwritten. {Log.Health}");
        Assert.Contains($"{Tag} written during the storm", AllText());
    }
}
