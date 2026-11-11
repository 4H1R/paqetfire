using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PaqetFire.Broker.Engines;

/// <summary>
/// Explains an engine that did not become ready in time from what can still be observed:
/// whether its process runs, who holds its port, and what it printed last. That is the
/// check a user would otherwise make by hand with tasklist and netstat.
/// </summary>
internal static class ReadinessReport
{
    private const int LastLineCount = 3;
    private const int MaximumLineLength = 240;

    /// <param name="probeFailure">Why the last connection to the port failed, when one was attempted.</param>
    /// <param name="notListening">What it means that the engine runs but has not opened the port yet.</param>
    /// <param name="silentHint">Added when the engine printed nothing.</param>
    public static string Describe(
        string engine,
        Process process,
        IPEndPoint endpoint,
        TimeSpan timeout,
        string? probeFailure,
        IReadOnlyList<string> output,
        string notListening,
        string? silentHint = null)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{engine} did not accept connections on {endpoint} within {timeout.TotalSeconds:0} seconds. ");

        int? processId = null;
        try
        {
            if (process.HasExited)
            {
                text.Append(CultureInfo.InvariantCulture, $"Its process had exited with code {process.ExitCode}. ");
            }
            else
            {
                processId = process.Id;
            }
        }
        catch (InvalidOperationException)
        {
            // The process object no longer describes a process.
        }

        var listeners = PortOwners.Find(endpoint.Port)
            .Where(owner => owner.Protocol == ProtocolType.Tcp &&
                            (owner.LocalEndpoint.Address.Equals(endpoint.Address) ||
                             owner.LocalEndpoint.Address.Equals(IPAddress.Any)))
            .ToArray();
        var others = listeners.Where(owner => owner.ProcessId != processId).ToArray();
        if (others.Length > 0)
        {
            text.Append("The port is held by ")
                .Append(string.Join(", ", others.Select(owner => $"{owner.ProcessName} (PID {owner.ProcessId})")))
                .Append(", not ").Append(engine).Append(". ");
        }
        else if (listeners.Length > 0)
        {
            text.Append(engine).Append(" listens on the port, but connections to it failed");
            text.Append(probeFailure is null ? ". " : $": {probeFailure}. ");
        }
        else if (processId is not null)
        {
            text.Append(engine).Append(" was still running but had not opened the port. ").Append(notListening).Append(' ');
        }

        AppendOutput(text, engine, output, silentHint);
        return text.ToString().TrimEnd();
    }

    /// <summary>The last lines an engine printed, for a message about why it stopped.</summary>
    public static void AppendOutput(StringBuilder text, string engine, IReadOnlyList<string> output, string? silentHint)
    {
        var lines = output.Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(LastLineCount)
            .Select(line => line.Length > MaximumLineLength ? line[..MaximumLineLength] + "…" : line.Trim())
            .ToArray();
        if (lines.Length == 0)
        {
            text.Append(engine).Append(" printed nothing.");
            if (silentHint is not null)
            {
                text.Append(' ').Append(silentHint);
            }

            return;
        }

        text.Append(engine).Append("'s last output: ").Append(string.Join(" | ", lines));
    }

    /// <summary>A short reason for a failed connection attempt to an engine's port.</summary>
    public static string DescribeConnectFailure(Exception exception) => exception switch
    {
        SocketException socket => socket.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => "the connection was refused",
            SocketError.TimedOut => "the connection timed out",
            SocketError.ConnectionReset => "the connection was reset",
            var code => $"socket error {code}",
        },
        OperationCanceledException => "no answer within half a second",
        IOException => "the connection closed during the SOCKS5 greeting",
        _ => exception.Message,
    };
}
