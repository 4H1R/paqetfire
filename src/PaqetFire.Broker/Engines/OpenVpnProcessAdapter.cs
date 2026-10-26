using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using PaqetFire.Core.Engines;

namespace PaqetFire.Broker.Engines;

/// <summary>
/// Runs the user's OpenVPN as the exit after Paqet. OpenVPN dials its server only through
/// Paqet's loopback SOCKS5 listener and owns the exit adapter Xray's exit outbound is bound
/// to. It counts as running once it reports a completed initialization.
/// </summary>
public sealed partial class OpenVpnProcessAdapter : ExitEngineProcessAdapter
{
    private const int NarrowestAllowedPrefix = 12;
    private static readonly TimeSpan AdapterToolTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LookupLifetime = TimeSpan.FromSeconds(5);
    private readonly OpenVpnProcessOptions options;
    private readonly Lock lookupLock = new();
    private OpenVpnLookup? lookup;
    private long lookupTimestamp;
    private OpenVpnInstallation? launching;
    private EventWaitHandle? stopEvent;
    private string? stopEventName;
    private bool tunnelActive;

    public OpenVpnProcessAdapter(OpenVpnProcessOptions options)
        : base((options ?? throw new ArgumentNullException(nameof(options))).ReadinessTimeout)
    {
        this.options = options;
    }

    public override EngineKind Kind => EngineKind.OpenVpn;

    public override string DisplayName => "OpenVPN";

    /// <summary>Whether the running OpenVPN has its tunnel up, rather than reconnecting.</summary>
    public bool TunnelActive
    {
        get
        {
            lock (StateLock)
            {
                return tunnelActive;
            }
        }
    }

    /// <summary>The installation the exit would run; cached briefly, as snapshots ask often.</summary>
    public OpenVpnLookup Installation
    {
        get
        {
            lock (lookupLock)
            {
                if (lookup is null || Stopwatch.GetElapsedTime(lookupTimestamp) > LookupLifetime)
                {
                    lookup = options.Locate();
                    lookupTimestamp = Stopwatch.GetTimestamp();
                }

                return lookup;
            }
        }
    }

    protected override string? Version => Installation.Installation?.Version.ToString(3);

    protected override string? Endpoint => options.AdapterName;

    internal override void HandleOutput(string line)
    {
        // Matched as a whole line: a server could echo the phrase inside its pushed options.
        if (InitializationCompleted().Match(line) is { Success: true } completed)
        {
            if (completed.Groups["errors"].Success)
            {
                // Without its route the adapter cannot carry Xray's exit traffic.
                ReportFatal($"OpenVPN connected but could not configure the {options.AdapterName} adapter.");
            }
            else if (CheckAssignedAddress() is { } problem)
            {
                ReportFatal(problem);
            }
            else
            {
                SetTunnelActive(true);
                MarkEstablished();
            }
        }
        else if (line.Contains("SIGUSR1[", StringComparison.Ordinal) || line.Contains("Restart pause", StringComparison.Ordinal))
        {
            SetTunnelActive(false);
        }
        else if (line.Contains("AUTH_FAILED", StringComparison.Ordinal))
        {
            SetProblem("The VPN server rejected the OpenVPN username or password.");
        }
        else if (line.Contains("All TAP-Windows adapters", StringComparison.Ordinal) ||
                 line.Contains("There are no TAP-Windows", StringComparison.Ordinal))
        {
            SetProblem($"The {options.AdapterName} adapter is in use by another OpenVPN connection.");
        }
        else if (line.Contains("socks", StringComparison.OrdinalIgnoreCase) &&
                 (line.Contains("error", StringComparison.OrdinalIgnoreCase) || line.Contains("fail", StringComparison.OrdinalIgnoreCase)))
        {
            SetProblem("OpenVPN could not reach its server through Paqet.");
        }

        AppendLog(line);
    }

    /// <summary>
    /// The VPN server chooses the adapter's address and netmask, and Windows adds an on-link
    /// route for that network which outranks the computer's own routes. Only a private network
    /// of /12 or narrower that overlaps no other adapter is accepted.
    /// </summary>
    internal static string? CheckAdapterAddress(
        IPAddress address,
        int prefixLength,
        IEnumerable<(IPAddress Address, int PrefixLength)> otherNetworks)
    {
        if (prefixLength < NarrowestAllowedPrefix)
        {
            return $"The VPN server assigned the exit adapter a /{prefixLength} network, wide enough to take over other traffic.";
        }

        if (!IsPrivate(address))
        {
            return $"The VPN server assigned the exit adapter a public address ({address}).";
        }

        return otherNetworks.Any(other => Overlaps(address, prefixLength, other.Address, other.PrefixLength))
            ? "The VPN server assigned the exit adapter a network that overlaps another network of this computer."
            : null;
    }

    /// <summary>
    /// Creates the exit adapter if it is missing. Called before Paqet starts: adding a network
    /// adapter rebinds packet filter drivers, which must not happen under a running capture.
    /// Does nothing while OpenVPN is not installed; the start then explains why.
    /// </summary>
    public async Task PrepareAdapterAsync(CancellationToken cancellationToken)
    {
        if (Installation.Installation is { } installation)
        {
            await EnsureAdapterAsync(installation, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override string? FindUnavailableReason() =>
        Installation.Installation is null ? Installation.Problem ?? "OpenVPN is not installed." : null;

    protected override async ValueTask<IAsyncDisposable> PrepareAsync(CancellationToken cancellationToken)
    {
        // A start always re-checks the installation it is about to run as SYSTEM.
        lock (lookupLock)
        {
            lookup = null;
        }

        var installation = Installation.Installation ??
                           throw new InvalidOperationException(Installation.Problem ?? "OpenVPN is not installed.");
        if (!File.Exists(options.ConfigurationPath))
        {
            throw new FileNotFoundException("The generated OpenVPN configuration is missing.", options.ConfigurationPath);
        }

        await EnsureAdapterAsync(installation, cancellationToken).ConfigureAwait(false);
        await RemoveStaleRoutesAsync(cancellationToken).ConfigureAwait(false);
        stopEventName = $"PaqetFire.OpenVpn.{Guid.NewGuid():N}";
        stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, stopEventName);
        launching = installation;
        return new FileStream(installation.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    protected override ProcessStartInfo CreateStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = launching!.ExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(options.ConfigurationPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // --service makes OpenVPN non-interactive and exit cleanly once the event is set.
        startInfo.ArgumentList.Add("--service");
        startInfo.ArgumentList.Add(stopEventName!);
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(options.ConfigurationPath);
        return startInfo;
    }

    protected override string DescribeRunning() =>
        tunnelActive ? "OpenVPN is connected." : "OpenVPN lost its tunnel and is reconnecting through Paqet.";

    protected override void ResetSession() => tunnelActive = false;

    protected override async Task RequestExitAsync(Process owned)
    {
        if (Volatile.Read(ref stopEvent) is not { } signal)
        {
            return;
        }

        try
        {
            signal.Set();
            using var graceful = new CancellationTokenSource(options.GracefulStopTimeout);
            await owned.WaitForExitAsync(graceful.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
        {
            // Not closed in time, or already released; the base class kills it.
        }
    }

    protected override void ReleaseProcessResources() => Interlocked.Exchange(ref stopEvent, null)?.Dispose();

    /// <summary>
    /// Pushed DNS options are filtered, so DNS servers on the exit adapter mean a filter did not
    /// hold; Windows would then send other applications' lookups into the tunnel. The site-local
    /// fec0:0:0:ffff::1-3 entries are placeholders Windows lists on adapters without DNS.
    /// </summary>
    internal static string? CheckAdapterDns(IEnumerable<IPAddress> dnsServers) =>
        dnsServers.Any(server => !server.IsIPv6SiteLocal)
            ? "The VPN server set DNS servers on the exit adapter, which would affect the rest of the computer."
            : null;

    private string? CheckAssignedAddress()
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(options.AddressWait.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                                  adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToArray();
            var networks = adapters
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses
                    .Where(unicast => unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(unicast => (adapter.Name, unicast.Address, unicast.PrefixLength)))
                .ToArray();
            var exit = networks.FirstOrDefault(network =>
                string.Equals(network.Name, options.AdapterName, StringComparison.OrdinalIgnoreCase));
            if (exit.Address is not null)
            {
                return CheckAdapterAddress(
                           exit.Address,
                           exit.PrefixLength,
                           networks.Where(network => network.Name != exit.Name).Select(network => (network.Address, network.PrefixLength))) ??
                       CheckAdapterDns(adapters.First(adapter => adapter.Name == exit.Name).GetIPProperties().DnsAddresses);
            }

            if (Stopwatch.GetTimestamp() > deadline)
            {
                return $"The {options.AdapterName} adapter received no IPv4 address from the VPN server.";
            }

            Thread.Sleep(250);
        }
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }

    private static bool Overlaps(IPAddress first, int firstPrefix, IPAddress second, int secondPrefix)
    {
        var prefix = Math.Min(firstPrefix, secondPrefix);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return (ToUInt32(first) & mask) == (ToUInt32(second) & mask);
    }

    private static uint ToUInt32(IPAddress address) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    [GeneratedRegex(@"^(?:\S+ \S+ )?Initialization Sequence Completed(?<errors> With Errors)?$", RegexOptions.CultureInvariant)]
    private static partial Regex InitializationCompleted();

    private void SetTunnelActive(bool active)
    {
        lock (StateLock)
        {
            tunnelActive = active;
            UpdateRunningDetail();
        }
    }

    /// <summary>
    /// Deletes a default route a crashed OpenVPN left on the exit adapter, which would make the
    /// next start fail to add its own. Scoped to this adapter: other routes are never touched.
    /// </summary>
    private async Task RemoveStaleRoutesAsync(CancellationToken cancellationToken)
    {
        var index = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => string.Equals(adapter.Name, options.AdapterName, StringComparison.OrdinalIgnoreCase))
            .Select(adapter => adapter.GetIPProperties().GetIPv4Properties()?.Index)
            .FirstOrDefault();
        if (index is not { } interfaceIndex)
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "netsh.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
                 {
                     "interface", "ipv4", "delete", "route", "prefix=0.0.0.0/0",
                     $"interface={interfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}", "store=active",
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var netsh = Process.Start(startInfo);
        if (netsh is null)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AdapterToolTimeout);
        try
        {
            // "Element not found" when there is no stale route is the usual outcome.
            await netsh.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            netsh.Kill(entireProcessTree: true);
        }
    }

    private async Task EnsureAdapterAsync(OpenVpnInstallation installation, CancellationToken cancellationToken)
    {
        if (NetworkInterface.GetAllNetworkInterfaces()
            .Any(adapter => string.Equals(adapter.Name, options.AdapterName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        AppendLog($"Creating the {options.AdapterName} adapter.");
        var startInfo = new ProcessStartInfo
        {
            FileName = installation.AdapterToolPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // tap-windows6 is the driver OpenVPN uses with a SOCKS5 proxy.
        foreach (var argument in new[] { "create", "--name", options.AdapterName, "--hwid", @"root\tap0901" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var tool = Process.Start(startInfo) ??
                         throw new InvalidOperationException("Windows did not start OpenVPN's tapctl.");
        var output = tool.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = tool.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(AdapterToolTimeout);
        try
        {
            await tool.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            tool.Kill(entireProcessTree: true);
            throw new TimeoutException($"OpenVPN's tapctl did not create the {options.AdapterName} adapter in time.");
        }

        var message = $"{await output.ConfigureAwait(false)} {await errors.ConfigureAwait(false)}".Trim();
        if (message.Length > 0)
        {
            AppendLog(message);
        }

        if (tool.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"OpenVPN could not create the {options.AdapterName} adapter (tapctl exit code {tool.ExitCode}). Reinstall OpenVPN Community with its TAP-Windows6 driver.");
        }
    }
}
