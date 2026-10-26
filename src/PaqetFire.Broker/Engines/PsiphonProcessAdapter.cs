using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PaqetFire.Broker.Configuration;
using PaqetFire.Core.Engines;

namespace PaqetFire.Broker.Engines;

/// <summary>
/// Runs psiphon-tunnel-core as the exit after Paqet. Psiphon dials its servers only through
/// Paqet's loopback SOCKS5 listener and serves Xray's exit outbound on its own loopback
/// listener. It counts as running once it reports an established tunnel and listens.
/// </summary>
public sealed partial class PsiphonProcessAdapter : ExitEngineProcessAdapter
{
    private readonly PsiphonProcessOptions options;
    private PsiphonSession session = PsiphonSession.None;

    public PsiphonProcessAdapter(PsiphonProcessOptions options)
        : base((options ?? throw new ArgumentNullException(nameof(options))).ReadinessTimeout)
    {
        this.options = options;
    }

    public override EngineKind Kind => EngineKind.Psiphon;

    public override string DisplayName => "Psiphon";

    /// <summary>What the running Psiphon process last reported about its tunnel.</summary>
    public PsiphonSession Session
    {
        get
        {
            lock (StateLock)
            {
                return session;
            }
        }
    }

    protected override string? Version => options.Version;

    protected override string? Endpoint => $"{options.SocksEndpoint.Address}:{options.SocksEndpoint.Port}";

    /// <summary>
    /// Applies one console-client notice, a JSON object such as
    /// <c>{"noticeType":"Tunnels","data":{"count":1},"timestamp":"..."}</c>.
    /// Anything else is kept as a plain log line.
    /// </summary>
    internal override void HandleOutput(string line)
    {
        if (!TryParseNotice(line, out var type, out var data))
        {
            AppendLog(line);
            return;
        }

        var established = false;
        lock (StateLock)
        {
            switch (type)
            {
                case "Tunnels" when TryGetInt(data, "count", out var count):
                    session = session with { TunnelActive = count > 0 };
                    established = count > 0;
                    UpdateRunningDetail();
                    break;
                case "ConnectedServerRegion" when TryGetString(data, "serverRegion", out var region):
                    session = session with { ConnectedRegion = region };
                    break;
                case "AvailableEgressRegions" when data.TryGetProperty("regions", out var regions) &&
                                                   regions.ValueKind == JsonValueKind.Array:
                    session = session with
                    {
                        AvailableRegions = regions.EnumerateArray()
                            .Where(region => region.ValueKind == JsonValueKind.String)
                            .Select(region => region.GetString()!)
                            .Where(region => region.Length == 2)
                            .Order(StringComparer.Ordinal)
                            .ToArray(),
                    };
                    break;
                case "UpstreamProxyError" or "Error" or "Alert" when TryGetString(data, "message", out var message):
                    SetProblem(message);
                    break;
                case "SocksProxyPortInUse":
                    SetProblem($"Its local SOCKS5 port {options.SocksEndpoint.Port} is in use.");
                    break;
            }
        }

        if (established)
        {
            MarkEstablished();
        }

        if (type is not ("ClientRegion" or "Homepage" or "Info"))
        {
            AppendLog($"{type} {data.GetRawText()}");
        }
    }

    internal static bool TryParseNotice(string line, out string type, out JsonElement data)
    {
        type = string.Empty;
        data = default;
        if (!line.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetString(document.RootElement, "noticeType", out type))
            {
                return false;
            }

            data = document.RootElement.TryGetProperty("data", out var payload) && payload.ValueKind == JsonValueKind.Object
                ? payload.Clone()
                : JsonDocument.Parse("{}").RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Removes credentials from any proxy URL, such as the upstream Paqet hop in an error.</summary>
    internal static string RedactProxyUrls(string text) => ProxyCredentials().Replace(text, "$1***@");

    protected override string? FindUnavailableReason() => File.Exists(options.ExecutablePath)
        ? null
        : "Psiphon is not downloaded. Download it under Routing → Exit proxy to use the Psiphon exit.";

    protected override async ValueTask<IAsyncDisposable> PrepareAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(options.ConfigurationPath))
        {
            throw new FileNotFoundException("The generated Psiphon configuration is missing.", options.ConfigurationPath);
        }

        var executableLock = await VerifiedExecutable.OpenAsync(
                options.ExecutablePath,
                options.ExpectedExecutableSha256,
                () => new CryptographicException(
                    "The downloaded Psiphon executable failed its SHA-256 integrity check. Download it again under Routing → Exit proxy."),
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (LoopbackListeners.IsListening(options.SocksEndpoint))
            {
                throw new InvalidOperationException($"Psiphon's local SOCKS5 endpoint {options.SocksEndpoint} is already in use.");
            }

            if (options.DataDirectoryPolicy is { } policy)
            {
                new ProtectedDirectory(options.DataDirectory, policy).Ensure();
            }
            else
            {
                Directory.CreateDirectory(options.DataDirectory);
            }

            return executableLock;
        }
        catch
        {
            await executableLock.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    protected override ProcessStartInfo CreateStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            WorkingDirectory = options.DataDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-config");
        startInfo.ArgumentList.Add(options.ConfigurationPath);
        startInfo.ArgumentList.Add("-dataRootDirectory");
        startInfo.ArgumentList.Add(options.DataDirectory);
        return startInfo;
    }

    protected override bool IsReady() => LoopbackListeners.IsListening(options.SocksEndpoint);

    protected override string DescribeRunning() => session switch
    {
        { TunnelActive: false } => "Psiphon lost its tunnel and is reconnecting through Paqet.",
        { ConnectedRegion: { Length: > 0 } region } => $"Psiphon is connected through a server in {region}.",
        _ => "Psiphon is connected.",
    };

    protected override void ResetSession() => session = PsiphonSession.None;

    protected override string Redact(string message) => RedactProxyUrls(message);

    [GeneratedRegex(@"(socks5?://|socks4a://|https?://)[^@\s/""]+@", RegexOptions.IgnoreCase)]
    private static partial Regex ProxyCredentials();

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }
}

/// <param name="ConnectedRegion">The country of the Psiphon server in use, as Psiphon reports it.</param>
/// <param name="AvailableRegions">The exit countries Psiphon can currently serve.</param>
public sealed record PsiphonSession(bool TunnelActive, string? ConnectedRegion, IReadOnlyList<string> AvailableRegions)
{
    public static PsiphonSession None { get; } = new(false, null, []);
}
