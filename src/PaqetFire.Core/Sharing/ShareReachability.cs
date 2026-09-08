using System.Diagnostics;
using System.Net.Sockets;

namespace PaqetFire.Core.Sharing;

public interface IShareReachabilityProbe
{
    Task<ShareReachabilityResult> CheckAsync(
        SocksShareEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public enum ShareReachabilityStatus
{
    Reachable,
    Unreachable,
    TimedOut,
}

public sealed record ShareReachabilityResult(
    SocksShareEndpoint Endpoint,
    ShareReachabilityStatus Status,
    TimeSpan Elapsed,
    string Summary);

public sealed class TcpShareReachabilityProbe : IShareReachabilityProbe
{
    public async Task<ShareReachabilityResult> CheckAsync(
        SocksShareEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The reachability timeout must be greater than zero and no longer than 30 seconds.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var timeoutCancellation = new CancellationTokenSource(timeout);
        using var combinedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        using var client = new TcpClient(AddressFamily.InterNetwork);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(
                endpoint.Address,
                endpoint.Port,
                combinedCancellation.Token).ConfigureAwait(false);
            stopwatch.Stop();
            return new ShareReachabilityResult(
                endpoint,
                ShareReachabilityStatus.Reachable,
                stopwatch.Elapsed,
                "The SOCKS listener accepted a TCP connection. Authentication was not tested.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ShareReachabilityResult(
                endpoint,
                ShareReachabilityStatus.TimedOut,
                stopwatch.Elapsed,
                "The SOCKS listener did not answer before the reachability timeout.");
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            stopwatch.Stop();
            return new ShareReachabilityResult(
                endpoint,
                ShareReachabilityStatus.Unreachable,
                stopwatch.Elapsed,
                "The SOCKS listener could not be reached over TCP.");
        }
    }
}
