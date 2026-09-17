using Halo.Shared;

namespace Halo.Tests;

/// <summary>Level filtering and spelling. Nothing exotic, but a level that quietly swallows Error
/// or a config spelling that silently resets the level are both the kind of fault you only notice
/// when the log you needed is empty.</summary>
[Collection(LogTestCollection.Name)]
public sealed class LogLevelTests : LogTestBase
{
    public LogLevelTests() : base("level") { }

    [Theory]
    [InlineData(LogLevel.Debug, "DBG INF WRN ERR")]
    [InlineData(LogLevel.Info, "INF WRN ERR")]
    [InlineData(LogLevel.Warn, "WRN ERR")]
    [InlineData(LogLevel.Error, "ERR")]
    public void OnlyRecordsAtOrAboveTheLevel_AreWritten(LogLevel level, string expected)
    {
        Log.SetLevel(level);
        Log.Debug($"{Tag} DBG");
        Log.Info($"{Tag} INF");
        Log.Warn($"{Tag} WRN");
        Log.Error($"{Tag} ERR");
        Assert.True(Log.Flush(10_000).Reached, Log.Health);

        string[] written = TaggedLines(AllText())
            .Select(l => Envelope.Match(l).Groups["message"].Value.Replace(Tag + " ", ""))
            .ToArray();
        Assert.Equal(expected.Split(' '), written);
    }

    /// <summary>Error is written at every level, on both the queued and the synchronous path. It is
    /// the one level that must never be filterable — a log configured to "error" that then drops
    /// errors is worse than no log.</summary>
    [Theory]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Info)]
    [InlineData(LogLevel.Warn)]
    [InlineData(LogLevel.Error)]
    public void Error_IsAlwaysWritten(LogLevel level)
    {
        Log.SetLevel(level);
        Log.Error($"{Tag} queued error");
        Log.Durable(LogLevel.Error, $"{Tag} durable error", "lifecycle");
        Assert.True(Log.Flush(10_000).Reached, Log.Health);

        string all = AllText();
        Assert.Contains($"{Tag} queued error", all);
        Assert.Contains($"{Tag} durable error", all);
    }

    [Theory]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("dbg", LogLevel.Debug)]
    [InlineData("verbose", LogLevel.Debug)]
    [InlineData("trace", LogLevel.Debug)]
    [InlineData("info", LogLevel.Info)]
    [InlineData("inf", LogLevel.Info)]
    [InlineData("information", LogLevel.Info)]
    [InlineData("warn", LogLevel.Warn)]
    [InlineData("wrn", LogLevel.Warn)]
    [InlineData("warning", LogLevel.Warn)]
    [InlineData("error", LogLevel.Error)]
    [InlineData("err", LogLevel.Error)]
    // A settings.json is hand-edited and an env var is typed at a prompt, so casing and stray
    // whitespace are the normal case, not the exotic one.
    [InlineData("DEBUG", LogLevel.Debug)]
    [InlineData("Warning", LogLevel.Warn)]
    [InlineData("  error  ", LogLevel.Error)]
    public void TryParseLevel_AcceptsTheDocumentedSpellings(string text, LogLevel expected)
    {
        Assert.True(Log.TryParseLevel(text, out LogLevel parsed), $"[{text}] was rejected");
        Assert.Equal(expected, parsed);
    }

    /// <summary>Junk is rejected <b>and leaves the level alone</b>. A typo in settings.json must not
    /// be able to silently turn the log down.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("fatal")]
    [InlineData("critical")]
    [InlineData("off")]
    [InlineData("none")]
    [InlineData("2")]
    [InlineData("infoo")]
    public void TryParseLevel_RejectsJunkWithoutChangingTheLevel(string? text)
    {
        Log.SetLevel(LogLevel.Warn);

        Assert.False(Log.TryParseLevel(text, out _), $"[{text}] was accepted");
        Assert.Equal(LogLevel.Warn, Log.Level);

        // And the live filter really is still Warn, not just the property.
        Log.Info($"{Tag} should not appear");
        Log.Warn($"{Tag} should appear");
        Assert.True(Log.Flush(10_000).Reached, Log.Health);
        string all = AllText();
        Assert.DoesNotContain($"{Tag} should not appear", all);
        Assert.Contains($"{Tag} should appear", all);
    }

    /// <summary>The env var is the escape hatch for a settings.json that will not parse, so config
    /// must not be able to override it once it has pinned the level.</summary>
    [Fact]
    public void SetLevel_IsANoOpWhileTheEnvVarHasPinnedTheLevel()
    {
        // ResetForTests clears the pin, so reach it the way Init does: through Log.Init, which
        // calls ApplyEnvLevel. The process name is deliberately unopenable as a filename so Init
        // returns before starting a second pump thread — see LogRotationTests for the same trick.
        string? previous = Environment.GetEnvironmentVariable(Log.LevelEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(Log.LevelEnvVar, "debug");
            Log.Init(Dir, "pinned|name");
            Assert.Equal(LogLevel.Debug, Log.Level);

            Log.SetLevel(LogLevel.Error);
            Assert.Equal(LogLevel.Debug, Log.Level);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Log.LevelEnvVar, previous);
            // Init orphaned the stream this test's constructor opened; collect it before
            // ResetForTests tries to delete the file it still holds. See ReleaseOrphanedStreams.
            ReleaseOrphanedStreams();
            Log.ResetForTests(Dir, "level", LogLevel.Info);
        }
    }
}

/// <summary>
/// Debug shedding: a debug storm must never be able to consume the queue in the moment before a
/// crash, and what it costs must be counted rather than silently absorbed.
/// </summary>
[Collection(LogTestCollection.Name)]
public sealed class LogSheddingTests : LogTestBase
{
    public LogSheddingTests() : base("shed", LogLevel.Debug) { }

    /// <summary>
    /// Flood Debug past the shed threshold, then put a Warn and an Error in while the queue is
    /// still that deep, and prove both reach the file.
    ///
    /// <para>Shed depth is three quarters of the 8192-slot queue, so the policy reserves 2048 slots
    /// that Debug can never touch; Warn and Error also get 250 ms of patience rather than a
    /// zero-timeout <c>TryAdd</c>. That is the contract, and it is what makes this test
    /// deterministic rather than a race: once Debug is being shed the depth is pinned at the
    /// threshold, so there is always room for the critical lane.</para>
    ///
    /// <para><b>Not verified with <c>Log.Flush</c> on purpose.</b> Flush cannot be trusted once
    /// anything has been shed — see <c>LogFlushTests.Flush_AfterShedding_StillTellsTheTruth</c> —
    /// so the markers are polled for on disk instead.</para>
    /// </summary>
    [Fact]
    public void ADebugStorm_CannotStarveWarnOrError()
    {
        using var storm = new DebugStorm();
        Assert.True(storm.RunUntilShedding(),
            $"the shed threshold was never reached, so this test would prove nothing. {Log.Health}");

        long deepest = storm.DeepestQueueSeen;
        long shedSoFar = DroppedQueueFull();

        // Stop rolling before the critical records go in, so they are not exposed to the churn
        // that was only there to build queue depth. The queue stays deep either way.
        storm.LiftStall();
        Log.Warn($"{Tag} a warning during the storm");
        Log.Error($"{Tag} an error during the storm");

        // Keep the storm running for a moment so those two really had to compete.
        Thread.Sleep(30);
        storm.Stop();

        Assert.True(shedSoFar > 0, "nothing was shed");
        Assert.True(deepest >= 4096,
            $"the deepest queue seen was only {deepest}; the shed threshold is 6144, so the drop "
            + "was not caused by the queue pressure this test is meant to create.");
        Assert.True(WaitForOnDisk($"{Tag} a warning during the storm"),
            $"a Warn logged during a debug storm never reached the file. {Log.Health}");
        Assert.True(WaitForOnDisk($"{Tag} an error during the storm"),
            $"an Error logged during a debug storm never reached the file. {Log.Health}");
    }

    /// <summary>Drops are accounted, not silent. <see cref="Log.Health"/> is the only place a
    /// stranger's log can admit the logger threw records away, so the number has to be there and
    /// has to be credible.</summary>
    [Fact]
    public void ShedRecords_AreCountedInHealth()
    {
        Assert.Equal(0L, DroppedTotal());

        using var storm = new DebugStorm();
        Assert.True(storm.RunUntilShedding(), $"the shed threshold was never reached. {Log.Health}");
        storm.Stop();

        long shed = DroppedQueueFull();
        long produced = storm.Produced;

        Assert.True(shed > 0, $"records were shed but Health reports none: {Log.Health}");
        Assert.True(shed <= produced,
            $"Health claims {shed} records were shed but only {produced} were ever offered.");
        Assert.Contains($"dropped={shed}+", Log.Health);
    }
}
