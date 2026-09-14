using System.IO;
using System.Diagnostics;
using Halo.Shared.Config;
using Xunit;

namespace Halo.Tests;

/// <summary>
/// Audit finding 6 — two Halo processes write the same config files (the Settings app on a 200 ms
/// debounce while a slider moves, the widget process on drag-end), and nothing serialised them.
///
/// <para>
/// <b>This test must stay cross-PROCESS.</b> <c>ConfigStore._writeLock</c> is an instance lock, so
/// a two-thread version of this test passes against the broken code — it would be green on the
/// exact defect it is named after, which is worse than having no test. The peer is therefore a
/// real second process: <c>Halo.Tests.exe</c> re-run with the magic argument that
/// <see cref="Program"/> turns into a config writer.
/// </para>
///
/// Measured against the pre-fix commit, 400 iterations per peer:
/// <list type="bullet">
/// <item>6b, the shared <c>&lt;file&gt;.tmp</c> written with FileShare.None — 424 IOException and
/// 93 UnauthorizedAccessException across the two peers, i.e. over half of all writes collided.</item>
/// <item>6a, the lost update — the peers finished on 399 and <b>398</b>; one write vanished.</item>
/// </list>
/// So 150 iterations is ample to catch a regression; the failure is not rare, it is the norm.
/// </summary>
public sealed class ConfigStoreCrossProcessTests : IDisposable
{
    // Sized from the numbers above: the old code collided on >50% of attempts, so this fails
    // essentially always against a regression while costing about a second.
    private const int Iterations = 150;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Halo.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
    }

    [Fact]
    public async Task TwoProcessesWritingTheSameFile_LoseNothing()
    {
        string peerExe = Path.Combine(AppContext.BaseDirectory, "Halo.Tests.exe");
        Assert.True(File.Exists(peerExe),
            $"the test assembly's own exe is the peer writer and it is missing: {peerExe}");

        using (var seed = new ConfigStore(_dir, watch: false))
        {
            seed.Widgets.Widgets.Add(new WidgetInstance { Id = "a", Type = "clock" });
            seed.Widgets.Widgets.Add(new WidgetInstance { Id = "b", Type = "clock" });
            seed.SaveWidgets();
        }

        // One of each real-world shape, not two of the same: the Settings app's whole-document
        // flush against the widget process's single-field patch.
        Process[] peers =
        [
            StartPeer(peerExe, "a", "settings"),
            StartPeer(peerExe, "b", "widget"),
        ];

        // Drain both pipes concurrently. Reading stdout to completion first deadlocks a peer that
        // is failing: it fills its stderr buffer, blocks on the write, and never reaches the exit
        // this side is waiting for. That only happens when writes are being refused — i.e. exactly
        // when this test is earning its keep — so getting it wrong makes the regression case hang
        // instead of fail. Measured before the fix: 2 m 58 s and a peer whose output never arrived.
        var reads = peers.Select(p => (Peer: p,
            Out: p.StandardOutput.ReadToEndAsync(),
            Err: p.StandardError.ReadToEndAsync())).ToArray();

        var failures = new List<string>();
        foreach ((Process p, Task<string> stdout, Task<string> stderr) in reads)
        {
            Assert.True(p.WaitForExit(60_000), "a config-writing peer did not finish inside 60 s");
            if (p.ExitCode == 0) { p.Dispose(); continue; }
            // One line per failed write, and there can be hundreds — keep the assertion readable.
            // Awaited, not blocked on: the peer has already exited, so both drains are done or
            // about to be. The tasks were started eagerly above — that is what keeps the pipes
            // concurrent — so this only collects results, it does not create the ordering.
            string[] detail = (await stderr).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            failures.Add($"peer reported {(await stdout).Trim()} failed write(s); first few:"
                + Environment.NewLine + string.Join(Environment.NewLine, detail.Take(5))
                + (detail.Length > 5 ? $"{Environment.NewLine}... and {detail.Length - 5} more" : ""));
            p.Dispose();
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));

        // Every write landed, so the last value each peer wrote must be the one on disk. A lost
        // update shows up here as an earlier number, not as an exception.
        using var check = new ConfigStore(_dir, watch: false);
        Assert.Equal(2, check.Widgets.Widgets.Count);
        Assert.Equal(Iterations - 1, check.Widgets.Widgets.Single(w => w.Id == "a").X);
        Assert.Equal(Iterations - 1, check.Widgets.Widgets.Single(w => w.Id == "b").X);

        // 6b again from the other side: a failed write must not leave its scratch file behind.
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    private Process StartPeer(string exe, string widgetId, string style)
        => Process.Start(new ProcessStartInfo(exe)
        {
            ArgumentList = { Program.PeerArgument, _dir, widgetId, Iterations.ToString(), style },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException($"could not start the peer writer {exe}");
}
