using System.Globalization;
using System.Text.RegularExpressions;
using Halo.Shared;

namespace Halo.Tests;

/// <summary>
/// Attribution and framing: who wrote a line, at what level, in which session — and the rule that
/// <b>every physical line</b> carries that envelope.
///
/// <para>The framing rule is not cosmetic. Halo's logs get grepped, and a support log gets merged
/// with another process's. A bare stack-trace line in that stream reads as a record of its own
/// with no timestamp, no level and no owner, which is how a single exception turns into a dozen
/// phantom entries. <c>Log.Format</c> marks continuation lines <c>+</c> for exactly that reason,
/// so the assertions here are written as "no line may be unprefixed", never as a hand-built
/// expected string.</para>
/// </summary>
[Collection(LogTestCollection.Name)]
public sealed class LogEnvelopeTests : LogTestBase
{
    public LogEnvelopeTests() : base("envelope") { }

    [Theory]
    [InlineData(LogLevel.Debug, "DBG")]
    [InlineData(LogLevel.Info, "INF")]
    [InlineData(LogLevel.Warn, "WRN")]
    [InlineData(LogLevel.Error, "ERR")]
    public void EveryRecord_CarriesTimestampLevelSessionAndComponent(LogLevel level, string tag)
    {
        Log.SetLevel(LogLevel.Debug);
        Log.For("sensors").Durable(level, $"{Tag} a reading went missing");
        Assert.True(Log.Flush(10_000).Reached, Log.Health);

        string line = Assert.Single(TaggedLines(AllText()));

        Match m = Envelope.Match(line);
        Assert.True(m.Success, $"the record does not carry the envelope: [{line}]");
        Assert.Equal(tag, m.Groups["level"].Value);
        Assert.Equal(Log.SessionId, m.Groups["session"].Value);
        Assert.Equal("sensors", m.Groups["component"].Value);
        Assert.Equal(" ", m.Groups["cont"].Value); // a first line, not a continuation
        Assert.Equal($"{Tag} a reading went missing", m.Groups["message"].Value);

        // The stamp is a real ISO 8601 instant with an offset, not just regex-shaped: round-trip it.
        Assert.True(DateTimeOffset.TryParse(m.Groups["ts"].Value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTimeOffset stamped),
            $"the timestamp does not parse as ISO 8601 with an offset: [{m.Groups["ts"].Value}]");
        Assert.True((DateTimeOffset.Now - stamped).Duration() < TimeSpan.FromMinutes(5),
            $"the timestamp is not of this moment: {stamped:O}");
    }

    /// <summary>The session id ties a line to a session record even after the file is renamed and
    /// even though pids get reused, so it has to be the same on every line.</summary>
    [Fact]
    public void SessionId_IsEightHexCharsAndTheSameOnEveryLine()
    {
        Assert.Matches("^[0-9a-f]{8}$", Log.SessionId);

        Log.Info($"{Tag} one");
        Log.Warn($"{Tag} two");
        Log.Durable(LogLevel.Error, $"{Tag} three", "lifecycle");
        Assert.True(Log.Flush(10_000).Reached, Log.Health);

        string[] mine = TaggedLines(AllText());
        Assert.Equal(3, mine.Length);
        Assert.All(mine, l => Assert.Equal(Log.SessionId, Envelope.Match(l).Groups["session"].Value));
    }

    /// <summary><c>Log.For(x)</c> exists so a provider sets its column once instead of at every
    /// call site; the column has to actually be <c>x</c>.</summary>
    [Fact]
    public void For_PutsTheComponentInItsOwnColumn()
    {
        ComponentLog log = Log.For("lhm-storage");
        log.Info($"{Tag} from a component logger");
        Log.Info($"{Tag} from the bare logger");
        Assert.True(Log.Flush(10_000).Reached, Log.Health);

        Dictionary<string, string> byMessage = TaggedLines(AllText())
            .Select(l => Envelope.Match(l))
            .Where(m => m.Success)
            .ToDictionary(m => m.Groups["message"].Value, m => m.Groups["component"].Value);

        Assert.Equal("lhm-storage", byMessage[$"{Tag} from a component logger"]);
        Assert.Equal("-", byMessage[$"{Tag} from the bare logger"]);
    }

    /// <summary>
    /// A stack trace is many physical lines and every one of them must be attributable. This is the
    /// assertion that stops an exception masquerading as a run of unrelated records.
    /// </summary>
    [Fact]
    public void ErrorWithException_PrefixesEveryPhysicalLineAndMarksContinuations()
    {
        Log.For("lifecycle").Error($"{Tag} a provider poll blew up", Caught());
        Assert.True(Log.Flush(10_000).Reached, Log.Health);

        string all = AllText();

        // 1. The rule, over the whole file: not one bare line anywhere.
        AssertNoBareLines(all);

        // 2. The record really did span several lines, or the rule above proved nothing. Taken as
        //    "from the tagged line to the end of the file" rather than by looking for the "+"
        //    marker, so that the marker itself is still something to assert rather than assume;
        //    nothing else writes after it in this test.
        Match[] mine = RecordFrom(all, Tag);
        Assert.True(mine.Length >= 3,
            $"expected a message line plus stack frames, got {mine.Length} line(s)");
        Assert.All(mine, m => Assert.True(m.Success, "a line of the record carries no envelope"));

        // 3. First line carries the message; every later line is marked as a continuation.
        Assert.Equal(" ", mine[0].Groups["cont"].Value);
        Assert.Contains($"{Tag} a provider poll blew up", mine[0].Groups["message"].Value);
        Assert.Contains(nameof(InvalidOperationException), mine[0].Groups["message"].Value);
        Assert.All(mine.Skip(1), m => Assert.Equal("+", m.Groups["cont"].Value));

        // 4. The trace is really there, and the inner exception is reported rather than swallowed.
        Assert.Contains(mine, m => m.Groups["message"].Value.TrimStart().StartsWith("at "));
        Assert.Contains(mine, m => m.Groups["message"].Value.Contains("--- inner:")
            && m.Groups["message"].Value.Contains("the inner cause"));

        // 5. Every line of one record shares its level, session and component — that is what makes
        //    a merged or grepped file still readable.
        Assert.All(mine, m =>
        {
            Assert.Equal("ERR", m.Groups["level"].Value);
            Assert.Equal(Log.SessionId, m.Groups["session"].Value);
            Assert.Equal("lifecycle", m.Groups["component"].Value);
        });
    }

    /// <summary>The same rule for the synchronous path, which is the one a crash actually uses.</summary>
    [Fact]
    public void DurableWithException_AlsoPrefixesEveryLine()
    {
        Log.Durable(LogLevel.Error, $"{Tag} unhandled on the way out", Caught(), "lifecycle");

        string all = AllText();
        AssertNoBareLines(all);
        Match[] mine = RecordFrom(all, Tag);
        Assert.True(mine.Length >= 3, "the durable record did not span its stack trace");
        Assert.All(mine.Skip(1), m => Assert.Equal("+", m.Groups["cont"].Value));
    }

    private static void AssertNoBareLines(string all)
    {
        var bare = NonEmptyLines(all).Where(l => !Envelope.IsMatch(l)).ToArray();
        Assert.True(bare.Length == 0,
            $"{bare.Length} line(s) carry no envelope, the first being: [{bare.FirstOrDefault()}]");
    }

    /// <summary>Every line from the one containing <paramref name="marker"/> to the end of the
    /// file. Continuation lines do not repeat the message, so they cannot be selected by tag.</summary>
    private static Match[] RecordFrom(string all, string marker)
    {
        string[] lines = NonEmptyLines(all);
        int start = Array.FindIndex(lines, l => l.Contains(marker, StringComparison.Ordinal));
        Assert.True(start >= 0, "the record never reached the file");
        return lines[start..].Select(l => Envelope.Match(l)).ToArray();
    }

    /// <summary>A real thrown exception, so the stack trace is real rather than a string that
    /// happens to contain newlines. Nested twice, with an inner cause, so there is more than one
    /// frame and the inner-exception branch of <c>Log.Describe</c> is exercised.</summary>
    private static Exception Caught()
    {
        try { Outer(); }
        catch (InvalidOperationException ex) { return ex; }
        throw new InvalidOperationException("Inner() was supposed to throw and did not");
    }

    private static void Outer() => Inner();

    private static void Inner()
        => throw new InvalidOperationException("the poll failed", new IOException("the inner cause"));
}
