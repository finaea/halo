using System.Diagnostics;
using Halo.Metrics;
using Halo.Shared;

namespace Halo.Collector;

/// <summary>
/// Runs each provider on its own thread at its fixed cadence (capped by MaxRateHz).
/// Failed providers are retried with backoff (1/5/30/60 s).
///
/// A provider <b>exception</b> never touches other providers (plan D2, §11): Initialize and Poll
/// are each wrapped, and a throwing provider costs only its own thread's tick. A native crash is
/// not the same thing — an access violation on a provider thread is uncatchable and takes the
/// whole collector with it, and nothing in this host can isolate that; only running providers in
/// separate processes would, and Halo does not. So a provider that can corrupt process state has
/// to avoid doing so by construction rather than rely on this host to contain it. See
/// LhmProvider's gate over LibreHardwareMonitor's process-global OpCode plumbing, which is the
/// one case known to have made that gap reachable (diagnosed 2026-09-14).
///
/// Each runner also owns one row of the section's provider table: state, needs-elevation,
/// rate, last poll timestamp and duration, and a short failure code. That table is what the
/// Settings System-check page reads — no log parsing (interface plan I5).
///
/// The host also owns the <b>freshness contract</b>: a value in shared memory is either fresh or
/// absent. A provider that fails, or that stops polling at all, has its readings marked N/A here
/// (<see cref="MetricSink.MarkProviderStale"/>) — otherwise every number it last wrote keeps a
/// valid timestamp and the widgets go on rendering a dead sensor as a live one, indefinitely.
/// </summary>
public sealed class ProviderHost : IDisposable
{
    /// <summary>
    /// How long a provider may go without completing a poll before its readings are marked N/A:
    /// <c>max(10 s, 10 x its own period)</c>, so a 0.5 Hz provider gets 20 s and a 40 Hz one gets
    /// the 10 s floor. Deliberately generous — far enough out that a hiccup, an init retry or a
    /// <c>rescan</c> never trips it, and the only thing it catches is a provider that really has
    /// stopped.
    /// </summary>
    private const double StaleFloorSeconds = 10;
    private const double StalePeriodMultiple = 10;

    /// <summary>How often the watchdog re-checks. Coarse on purpose: the bound it enforces is
    /// tens of seconds, so a second of resolution is free.</summary>
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(1);

    private readonly MetricSink _sink;
    private readonly List<Runner> _runners = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _watchdog;

    public ProviderHost(MetricSink sink)
    {
        _sink = sink;
        _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "halo-freshness" };
        _watchdog.Start();
    }

    public void Add(ISensorProvider provider)
    {
        var r = new Runner(provider, _sink, _cts.Token);
        lock (_runners) _runners.Add(r);
        r.Start();
    }

    private Runner[] Snapshot()
    {
        lock (_runners) return _runners.ToArray();
    }

    /// <summary>
    /// Rule 2 of the freshness contract: stale the readings of any provider that has not completed
    /// a poll inside its bound. Rule 1 (below, in the runner) covers a provider that throws — this
    /// covers the one case no failure path can: a provider wedged inside a native call that never
    /// returns, whose thread is still alive and whose last published values look perfectly fresh.
    /// </summary>
    private void WatchdogLoop()
    {
        while (!_cts.Token.WaitHandle.WaitOne(WatchdogInterval))
        {
            // This thread only ever marks values N/A. An exception escaping it would be unhandled
            // on a background thread, which ends the collector — a bookkeeping fault must never
            // cost the user every metric they have.
            try
            {
                foreach (var r in Snapshot()) r.CheckFreshness();
            }
            catch (Exception ex)
            {
                Log.Error("freshness watchdog", ex);
            }
        }
    }

    public IReadOnlyList<(string Name, bool Available, double RateHz, double LastPollMs)> Status()
        => Snapshot().Select(r => (r.Provider.Name, r.Available, r.RateHz, r.LastPollMs)).ToList();

    /// <summary>
    /// Handle a <c>rescan</c> command: every provider whose Initialize enumerates hardware is
    /// woken and re-initialised on its own thread, so a GPU, fan channel or volume that appeared
    /// since start-up gets registered without restarting the collector. Returns how many were
    /// asked (the work itself happens asynchronously on the provider threads), or -1 when the
    /// command was ignored as a repeat.
    ///
    /// Repeats inside <see cref="RescanDebounce"/> are dropped. Re-opening a
    /// LibreHardwareMonitor <c>Computer</c> is not free (see
    /// <see cref="Providers.LhmProvider.RescanReinitialises"/>), and nothing a user can plug in
    /// appears twice in a few seconds — so a held-down button must not become a re-open storm.
    /// </summary>
    public int Rescan()
    {
        lock (_rescanLock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastRescan < RescanDebounce) return -1;
            _lastRescan = now;
        }
        return Snapshot().Count(r => r.RequestRescan());
    }

    private static readonly TimeSpan RescanDebounce = TimeSpan.FromSeconds(10);
    private readonly object _rescanLock = new();
    private DateTime _lastRescan = DateTime.MinValue;

    public void Dispose()
    {
        _cts.Cancel();
        _watchdog.Join(2000);
        var runners = Snapshot();
        foreach (var r in runners) r.Wake();   // cut short a long sleep (lhm-storage waits 10 s)
        foreach (var r in runners) r.Join(2000);
        foreach (var r in runners) { try { r.Provider.Dispose(); } catch { } }
        // The wake handles are deliberately not disposed: a runner that missed its 2 s join would
        // then throw ObjectDisposedException out of its wait, on a thread with no handler.
    }

    private sealed class Runner(ISensorProvider provider, MetricSink sink, CancellationToken ct)
    {
        public ISensorProvider Provider { get; } = provider;
        public volatile bool Available;
        public double RateHz => Math.Min(Provider.DefaultRateHz, Provider.MaxRateHz);
        public double LastPollMs;

        private readonly int _providerIndex = sink.RegisterProvider(provider.Name, provider.NeedsElevation);
        private Thread? _thread;
        private string? _lastErrorSig;
        private DateTime _nextErrorLog;

        /// <summary>QPC of the last poll that returned without throwing, or 0 while this provider
        /// has never got that far. Written by the poll thread, read by the host's watchdog.</summary>
        private long _lastGoodPollQpc;
        /// <summary>Latch so a failed provider is staled once rather than on every watchdog tick.
        /// Races between the two threads are benign: the worst case is one redundant pass, or one
        /// skipped pass that the next tick repeats.</summary>
        private volatile bool _metricsStale = true;

        /// <summary>Lets the host interrupt the inter-poll sleep — a rescan or a shutdown should
        /// not have to wait out lhm-storage's 10 s period.</summary>
        private readonly AutoResetEvent _wake = new(false);
        private volatile bool _rescanRequested;

        public void Start()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = $"halo-{Provider.Name}" };
            _thread.Start();
        }

        /// <summary>Ask this provider to re-enumerate. False = it opted out (see
        /// <see cref="ISensorProvider.RescanReinitialises"/>).</summary>
        public bool RequestRescan()
        {
            if (!Provider.RescanReinitialises) return false;
            _rescanRequested = true;
            _wake.Set();
            return true;
        }

        public void Wake() => _wake.Set();

        public void Join(int ms) => _thread?.Join(ms);

        /// <summary>
        /// Publish the state of a provider whose poll just worked. A provider that needs admin and
        /// did not get it is <b>degraded</b>, not ok: LibreHardwareMonitor happily initialises and
        /// finds the CPU object unelevated, then reads nothing off it, so reporting "ok" would have
        /// the System check tell the user their sensors are fine while every value reads N/A.
        /// </summary>
        private void PublishHealthy()
        {
            bool unelevated = Provider.NeedsElevation && !Elevation.IsElevated;
            sink.SetProviderState(_providerIndex,
                unelevated ? ProviderState.Degraded : ProviderState.Ok,
                RateHz,
                unelevated ? ProviderError.Unelevated : ProviderError.None);
        }

        /// <summary>
        /// Rule 1 of the freshness contract: this provider just failed, so everything it publishes
        /// is now a number nobody re-read. Mark it N/A rather than leaving the last sample looking
        /// live. Static metrics and .max companions are exempt — see
        /// <see cref="MetricSink.MarkProviderStale"/>.
        ///
        /// Note this is keyed to the failure itself, not to the published <c>Degraded</c> state: an
        /// unelevated provider that is polling perfectly well also reports Degraded, and staling
        /// that one would be exactly backwards.
        /// </summary>
        private void StaleMetrics()
        {
            if (_metricsStale) return;
            _metricsStale = true;
            sink.MarkProviderStale(Provider.Name);
        }

        /// <summary>A poll came back: the values it wrote are fresh again.</summary>
        private void MarkFresh()
        {
            Volatile.Write(ref _lastGoodPollQpc, Stopwatch.GetTimestamp());
            _metricsStale = false;
        }

        /// <summary>
        /// Rule 2, called from the host's watchdog thread. A provider wedged inside a native call
        /// never reaches its own catch block, so nothing on this thread can report it — the bound
        /// on how old a completed poll may be is the only thing that catches it.
        /// </summary>
        public void CheckFreshness()
        {
            if (_metricsStale) return;
            long last = Volatile.Read(ref _lastGoodPollQpc);
            if (last == 0) return;                      // never polled: nothing published to stale
            double age = (double)(Stopwatch.GetTimestamp() - last) / Stopwatch.Frequency;
            double bound = Math.Max(StaleFloorSeconds, StalePeriodMultiple / RateHz);
            if (age < bound) return;
            Log.Warn($"{Provider.Name}: no completed poll for {age:0.#} s (bound {bound:0.#} s) — its metrics now read N/A");
            StaleMetrics();
        }

        private void Run()
        {
            int initFailures = 0;
            while (!ct.IsCancellationRequested)
            {
                // ---- init with backoff ----
                try
                {
                    Available = Provider.Initialize(sink);
                }
                catch (Exception ex)
                {
                    Log.Error($"{Provider.Name}: Initialize threw", ex);
                    Available = false;
                    sink.SetProviderState(_providerIndex, ProviderState.Unavailable, RateHz, ProviderError.Failed);
                }

                if (!Available)
                {
                    sink.SetProviderState(_providerIndex, ProviderState.Unavailable, RateHz,
                        Provider.UnavailableReason ?? ProviderError.Failed);
                    StaleMetrics();
                    int delay = initFailures switch { 0 => 1000, 1 => 5000, 2 => 30000, _ => 60000 };
                    initFailures++;
                    if (initFailures <= 3) Log.Warn($"{Provider.Name}: unavailable, retry in {delay} ms");
                    // A rescan cuts the backoff short — that is exactly the case where the user
                    // just plugged in the hardware this provider was waiting for.
                    if (WaitHandle.WaitAny([ct.WaitHandle, _wake], delay) == 0) return;
                    _rescanRequested = false;
                    continue;
                }

                initFailures = 0;
                PublishHealthy();
                Log.Info($"{Provider.Name}: initialised, polling at {RateHz:0.##} Hz (cap {Provider.MaxRateHz} Hz)");

                // ---- poll loop ----
                long periodTicks = (long)(Stopwatch.Frequency / RateHz);
                long next = Stopwatch.GetTimestamp();
                int consecutiveErrors = 0;
                var sw = new Stopwatch();

                // Live rate accounting. The window is long enough for at least five polls, so a
                // 0.1 Hz provider is measured over ~50 s rather than reported as 0 every 2 s.
                long rateWindowTicks = (long)Math.Max(2.0, 5.0 / RateHz) * Stopwatch.Frequency;
                long rateMarkQpc = Stopwatch.GetTimestamp();
                int pollsInWindow = 0;

                while (!ct.IsCancellationRequested)
                {
                    if (_rescanRequested)
                    {
                        _rescanRequested = false;
                        Log.Info($"{Provider.Name}: re-enumerating hardware (rescan)");
                        break;  // Available stays true, so the outer loop re-runs Initialize now
                    }

                    sw.Restart();
                    try
                    {
                        Provider.Poll(sink);
                        MarkFresh();
                        if (consecutiveErrors > 0) PublishHealthy();
                        consecutiveErrors = 0;
                    }
                    catch (Exception ex)
                    {
                        consecutiveErrors++;
                        sink.SetProviderState(_providerIndex, ProviderState.Degraded, RateHz, ProviderError.Failed);
                        StaleMetrics();
                        // identical failures repeat across re-init cycles (e.g. LHM NRE
                        // streaks) — full detail on first sight, then one line per 5 min
                        // so a flaky sensor can't flood the log
                        string sig = $"{ex.GetType().Name}:{ex.Message}";
                        if (sig != _lastErrorSig)
                        {
                            Log.Error($"{Provider.Name}: poll failed ({consecutiveErrors})", ex);
                            _lastErrorSig = sig;
                            _nextErrorLog = DateTime.UtcNow.AddMinutes(5);
                        }
                        else if (DateTime.UtcNow >= _nextErrorLog)
                        {
                            Log.Warn($"{Provider.Name}: poll still failing ({sig})");
                            _nextErrorLog = DateTime.UtcNow.AddMinutes(5);
                        }
                        if (consecutiveErrors >= 10)
                        {
                            Log.Warn($"{Provider.Name}: too many poll failures, re-initialising");
                            Available = false;
                            break; // back to init loop
                        }
                    }
                    LastPollMs = sw.Elapsed.TotalMilliseconds;
                    long polledQpc = Stopwatch.GetTimestamp();
                    sink.SetProviderPoll(_providerIndex, polledQpc, LastPollMs);

                    // effectiveRateHz in the registry: what this provider is really managing, so a
                    // consumer can see an overrunning provider instead of trusting the constant.
                    pollsInWindow++;
                    if (polledQpc - rateMarkQpc >= rateWindowTicks)
                    {
                        double measured = pollsInWindow * (double)Stopwatch.Frequency / (polledQpc - rateMarkQpc);
                        sink.SetEffectiveRateScale(Provider.Name, measured / RateHz);
                        rateMarkQpc = polledQpc;
                        pollsInWindow = 0;
                    }

                    next += periodTicks;
                    long now = Stopwatch.GetTimestamp();
                    if (next <= now)
                    {
                        next = now; // overran the period: don't try to catch up, just go on
                        continue;
                    }
                    int sleepMs = (int)((next - now) * 1000 / Stopwatch.Frequency);
                    if (sleepMs > 0)
                    {
                        int woke = WaitHandle.WaitAny([ct.WaitHandle, _wake], sleepMs);
                        if (woke == 0) return;                              // cancelled
                        if (woke == 1) next = Stopwatch.GetTimestamp();     // woken early: re-base the cadence
                    }
                }
            }
        }
    }
}
