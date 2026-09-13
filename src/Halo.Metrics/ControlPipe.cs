using System.IO.Pipes;
using System.Text;

namespace Halo.Metrics;

/// <summary>
/// Client side of the «Halo.Control.v2» pipe (the server lives in the collector). One-way,
/// newline-delimited UTF-8, fire and forget: a missing collector just returns false.
/// Adding commands is backwards-compatible; unknown lines are logged and ignored.
/// </summary>
public static class ControlPipe
{
    public const string PipeName = "Halo.Control.v2";

    // ---- commands (docs\metrics-protocol.md) ----
    /// <summary>"reset-max [prefix]" — clear session maxima whose base metric starts with prefix.</summary>
    public const string ResetMax = "reset-max";
    /// <summary>"reset-net" — restart the network session counters.</summary>
    public const string ResetNet = "reset-net";
    /// <summary>"rescan" — re-enumerate hardware (GPUs, fans, volumes).</summary>
    public const string Rescan = "rescan";
    /// <summary>"reload-config" — re-read the config files now instead of waiting for the watcher.</summary>
    public const string ReloadConfig = "reload-config";
    /// <summary>"ping" — no-op liveness check.</summary>
    public const string Ping = "ping";

    public static bool Send(string command, int timeoutMs = 500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            byte[] bytes = Encoding.UTF8.GetBytes(command + "\n");
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch { return false; }
    }
}
