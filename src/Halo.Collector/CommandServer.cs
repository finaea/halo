using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Halo.Metrics;
using Halo.Shared;

namespace Halo.Collector;

/// <summary>
/// Tiny line-based control channel («Halo.Control.v2» named pipe) so non-elevated widgets and
/// the Settings app can ask the elevated collector for actions: "reset-max &lt;prefix&gt;",
/// "reset-net", "rescan", "reload-config", "ping". Commands are defined in Halo.Metrics.ControlPipe.
/// </summary>
public sealed class CommandServer : IDisposable
{
    public const string PipeName = Halo.Metrics.ControlPipe.PipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly Action<string> _handler;

    public CommandServer(Action<string> handler)
    {
        _handler = handler;
        new Thread(Run) { IsBackground = true, Name = "halo-cmd" }.Start();
    }

    private void Run()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var ps = new PipeSecurity();
                // allow authenticated users to connect (widgets run non-elevated)
                ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    PipeAccessRights.ReadWrite, AccessControlType.Allow));
                ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.FullControl, AccessControlType.Allow));

                using var server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.None, 4096, 4096, ps);
                server.WaitForConnection();
                using var reader = new StreamReader(server, Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    Log.Info($"command: {line}");
                    try { _handler(line); } catch (Exception ex) { Log.Error("command handler", ex); }
                }
            }
            catch (Exception ex)
            {
                if (_cts.IsCancellationRequested) return;
                Log.Error("command pipe", ex);
                Thread.Sleep(1000);
            }
        }
    }

    public void Dispose() => _cts.Cancel();
}
