using System.IO.Pipes;
using System.Text;

namespace Halo.Shared;

/// <summary>Client side of the «Halo.Control.v1» pipe (server lives in the collector).</summary>
public static class ControlPipe
{
    public const string PipeName = "Halo.Control.v1";

    public static bool Send(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(500);
            byte[] bytes = Encoding.UTF8.GetBytes(command + "\n");
            client.Write(bytes);
            client.Flush();
            return true;
        }
        catch { return false; }
    }
}
