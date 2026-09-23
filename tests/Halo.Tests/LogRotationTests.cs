using System.Text.RegularExpressions;
using Halo.Shared;

namespace Halo.Tests;

/// <summary>
/// The size cap and the directory sweep.
///
/// <para><b>The regression being pinned.</b> The old logger's 64 MB cap did not roll — it appended
/// one "file output suppressed for the rest of this session" line and set the destination to "",
/// so the log went permanently silent at exactly the moment something was spamming it. Rolling has
/// to keep writing, which is what <see cref="PastTheSizeCap_ItRollsAndKeepsWriting"/> asserts by
/// writing more records <i>after</i> the rolls and finding them in the live file.</para>
/// </summary>
[Collection(LogTestCollection.Name)]
public sealed class LogRotationTests : LogTestBase
{
    public LogRotationTests() : base("rotation") { }

    /// <summary>
    /// <c>Durable</c> is used rather than the queued path on purpose: the cap is checked once per
    /// write and the pump batches up to 64 KB per write, so a queued flood would roll once for
    /// thousands of records. One synchronous write per record makes the roll boundary exact and the
    /// test free of timing.
    /// </summary>
    [Fact]
    public void PastTheSizeCap_ItRollsAndKeepsWriting()
    {
        const int Records = 40;
        Log.SetMaxFileBytesForTests(300); // about four ~83-byte records per file
        for (int i = 0; i < Records; i++)
            Log.Durable(LogLevel.Info, $"{Tag} record {i:00}", "rotation");

        string rolledFirst = BasePath() + ".1.log";
        Assert.True(File.Exists(rolledFirst), $"nothing rolled to {rolledFirst}. {Log.Health}");
        long rolls = long.Parse(HealthField("rolls"));
        Assert.True(rolls >= 2, $"expected several rolls, Health says {rolls}. {Log.Health}");

        // The point of the whole thing: the cap rolled the file, it did not end file output. The
        // destination is still a real path, and both write paths still land in it.
        // (Nothing is asserted about record 39 being in the live file: the cap is checked after the
        // write, so the record that trips it is in the rolled file and the live file is fresh.)
        Assert.NotEqual("", Log.CurrentPath);

        Log.SetMaxFileBytesForTests(long.MaxValue);
        Log.Durable(LogLevel.Info, $"{Tag} durable after the rolls", "rotation");
        Log.Info($"{Tag} queued after the rolls");
        Assert.True(Log.Flush(10_000).Reached, Log.Health);

        string live = LiveText();
        Assert.Contains($"{Tag} durable after the rolls", live);
        Assert.Contains($"{Tag} queued after the rolls", live);

        // Nothing was lost or reordered across the roll boundaries. LogFilesInWriteOrder puts the
        // rolled files oldest-first (Roll moves the live file to an increasing index) ahead of the
        // live one, so the concatenation is write order.
        int[] indexes = TaggedLines(AllText())
            .Select(l => Regex.Match(l, @"record (\d\d)$"))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToArray();
        Assert.Equal(Enumerable.Range(0, Records), indexes);
    }

    /// <summary>
    /// The sweep deletes by age, keeps what is fresh, and is not derailed by a file it is not
    /// allowed to delete.
    ///
    /// <para><b>How the sweep is reached.</b> <c>Sweep</c> is private and <c>Log.Init</c> is its
    /// only caller. The process name contains a <c>|</c> deliberately: the sweep runs before the
    /// destination is opened (<c>Log.cs:138</c> vs <c>:147</c>), so an unopenable filename
    /// exercises the sweep and then makes <c>Init</c> return at its "file logging is off" branch.
    /// That matters because a successful <c>Init</c> starts a <b>second</b> pump thread
    /// unconditionally (<c>Log.cs:154-155</c>) which would live for the rest of the test process
    /// and interleave batches with the first, breaking every ordering assertion in this suite. It
    /// also gets the degradation path asserted for free.</para>
    ///
    /// <para><b>What is not asserted, and why.</b> The live file of the <c>Init</c> under test is
    /// created after the sweep has already run, so it is structurally impossible for the sweep to
    /// touch it — there is nothing to test there. The real hazard is a fresh file belonging to
    /// another live instance, which is covered here, and an old file that cannot be deleted, which
    /// must not abort the rest of the sweep. The directory byte budget (24 MB portable, 160 MB
    /// installed) is not covered: reaching it costs tens of megabytes of writes for one
    /// assertion.</para>
    /// </summary>
    [Fact]
    public void Sweep_DeletesByAgeAndSurvivesAFileItCannotDelete()
    {
        string sweepDir = Path.Combine(Dir, "sweep");
        Directory.CreateDirectory(sweepDir);

        // 30 days covers both retention windows: 7 days installed, 3 days portable.
        string ancient = Seed(sweepDir, "collector-20260801-101500-1111.log", TimeSpan.FromDays(-30));
        string lockedAncient = Seed(sweepDir, "collector-20260801-101500-2222.log", TimeSpan.FromDays(-30));
        string recent = Seed(sweepDir, "widgets-20260917-101500-3333.log", TimeSpan.FromHours(-2));

        // Stand in for another instance still writing: the logger opens its own file exactly like
        // this (Log.cs:441), and FileShare.Read does not include Delete, so the sweep cannot
        // remove it.
        using (new FileStream(lockedAncient, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            try
            {
                Log.Init(sweepDir, "unopenable|name");

                Assert.False(File.Exists(ancient), "a 30-day-old log survived the sweep");
                Assert.True(File.Exists(recent), "a two-hour-old log was swept");
                Assert.True(File.Exists(lockedAncient),
                    "the test's own lock on this file should have made the delete fail");
                Assert.Equal("", Log.CurrentPath); // destination unopenable -> file logging off
            }
            finally
            {
                ReleaseOrphanedStreams();
                Log.ResetForTests(Dir, "rotation", LogLevel.Info);
            }
        }
    }

    private static string Seed(string dir, string name, TimeSpan age)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, $"a previous session's log ({name})\r\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow + age);
        return path;
    }
}
