using System.Collections.Concurrent;
using System.Text;

namespace Halo.Shared;

/// <summary>Tiny dependency-free async file logger (logs\&lt;proc&gt;-yyyyMMdd.log).
/// Files older than 7 days are deleted at Init; a file that reaches 64 MB stops
/// growing for the rest of the session (runaway-spam backstop).</summary>
public static class Log
{
    private const int RetentionDays = 7;
    private const long MaxFileBytes = 64 * 1024 * 1024;

    private static readonly BlockingCollection<string> Queue = new(4096);
    private static string _path = "";
    private static long _written;
    private static Thread? _thread;
    public static bool AlsoConsole;

    public static void Init(string logsDir, string processName, bool alsoConsole = false)
    {
        Directory.CreateDirectory(logsDir);
        try
        {
            foreach (var f in Directory.EnumerateFiles(logsDir, "*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-RetentionDays))
                    File.Delete(f);
        }
        catch { /* retention is best-effort */ }

        _path = Path.Combine(logsDir, $"{processName}-{DateTime.Now:yyyyMMdd}.log");
        try { _written = File.Exists(_path) ? new FileInfo(_path).Length : 0; } catch { _written = 0; }
        AlsoConsole = alsoConsole;
        _thread = new Thread(Pump) { IsBackground = true, Name = "halo-log" };
        _thread.Start();
        Info($"=== {processName} start pid={Environment.ProcessId} ===");
    }

    public static void Info(string msg) => Enqueue("INF", msg);
    public static void Warn(string msg) => Enqueue("WRN", msg);
    public static void Error(string msg) => Enqueue("ERR", msg);
    public static void Error(string msg, Exception ex) => Enqueue("ERR", $"{msg}: {ex.GetType().Name} {ex.Message}\n{ex.StackTrace}");

    private static void Enqueue(string lvl, string msg)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} [{lvl}] {msg}";
        if (AlsoConsole) Console.WriteLine(line);
        if (_path.Length == 0) return;
        Queue.TryAdd(line);
    }

    private static void Pump()
    {
        var sb = new StringBuilder();
        while (true)
        {
            sb.Clear();
            sb.AppendLine(Queue.Take());
            while (sb.Length < 64 * 1024 && Queue.TryTake(out var more, 50))
                sb.AppendLine(more);
            try
            {
                File.AppendAllText(_path, sb.ToString());
                _written += sb.Length;
                if (_written >= MaxFileBytes)
                {
                    File.AppendAllText(_path,
                        $"{DateTime.Now:HH:mm:ss.fff} [WRN] log reached {MaxFileBytes / (1024 * 1024)} MB cap — file output suppressed for the rest of this session\r\n");
                    _path = "";
                }
            }
            catch { /* disk hiccup: drop */ }
        }
    }

    public static void Flush()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Queue.Count > 0 && sw.ElapsedMilliseconds < 1000) Thread.Sleep(10);
    }
}
