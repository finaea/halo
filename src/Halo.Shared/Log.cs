using System.Collections.Concurrent;
using System.Text;

namespace Halo.Shared;

/// <summary>Tiny dependency-free async file logger (logs\&lt;proc&gt;-yyyyMMdd.log).</summary>
public static class Log
{
    private static readonly BlockingCollection<string> Queue = new(4096);
    private static string _path = "";
    private static Thread? _thread;
    public static bool AlsoConsole;

    public static void Init(string logsDir, string processName, bool alsoConsole = false)
    {
        Directory.CreateDirectory(logsDir);
        _path = Path.Combine(logsDir, $"{processName}-{DateTime.Now:yyyyMMdd}.log");
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
            try { File.AppendAllText(_path, sb.ToString()); } catch { /* disk hiccup: drop */ }
        }
    }

    public static void Flush()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (Queue.Count > 0 && sw.ElapsedMilliseconds < 1000) Thread.Sleep(10);
    }
}
