using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace PaqetFire.Core.Configuration;

public static partial class PaqetConfigurationValidator
{
    private const int MaximumBufferBytes = int.MaxValue;
    private static readonly HashSet<char> TcpFlagCharacters = new("FSRPAUECN");
    private static readonly HashSet<string> KcpModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "normal", "fast", "fast2", "fast3", "manual",
    };
    private static readonly HashSet<string> KcpBlocks = new(StringComparer.OrdinalIgnoreCase)
    {
        "aes", "aes-128", "aes-128-gcm", "aes-192", "salsa20", "blowfish",
        "twofish", "cast5", "3des", "tea", "xtea", "xor", "sm4", "none", "null",
    };
    private static readonly HashSet<string> LogLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "debug", "info", "warn", "error", "fatal",
    };

    public static IReadOnlyList<string> Validate(PaqetProfile? profile, string? transportKey)
    {
        if (profile is null)
        {
            return ["A Paqet profile is required."];
        }

        var errors = new List<string>();
        ValidateEndpoint(profile.ServerEndpoint, "Server endpoint", loopbackOnly: false, errors);
        ValidateEndpoint(profile.LocalSocksEndpoint, "Local SOCKS endpoint", loopbackOnly: true, errors);
        // Paqet v1.0.0-alpha.21 enforces the pcap/IFNAMSIZ limit on all platforms.
        ValidateText(profile.InterfaceName, "Network interface", 15, errors);

        if (!TryParseInterfaceGuid(profile.InterfaceGuid, out _))
        {
            errors.Add("The network interface GUID is invalid.");
        }

        if (!IPAddress.TryParse(profile.LocalIpv4Address?.Trim(), out var localIp) ||
            localIp.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(localIp) || localIp.Equals(IPAddress.Any))
        {
            errors.Add("The local IPv4 address must be a usable address assigned to the selected interface.");
        }

        ValidateMac(profile.RouterMac, "router MAC address", errors);
        ValidateFlags(profile.LocalTcpFlags, "local", errors);
        ValidateFlags(profile.RemoteTcpFlags, "remote", errors);

        ValidateSocksCredentials(profile.SocksUsername, profile.SocksPassword, errors);
        ValidateForwardRules(profile.ForwardRules, errors);
        if (RequiresTransportKey(profile.KcpBlock) || !string.IsNullOrEmpty(transportKey))
            ValidateTransportKey(transportKey, errors);
        ValidateTuning(profile, errors);
        return errors;
    }

    public static IReadOnlyList<string> ValidateAdvanced(PaqetAdvancedOptions? options, string kcpMode)
    {
        if (options is null) return ["Advanced Paqet settings are required."];
        var errors = new List<string>();
        var tuning = options.ApplyTo(new PaqetProfile(string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, [], [], kcpMode));
        ValidateTuning(tuning, errors);
        return errors;
    }

    public static bool RequiresTransportKey(string? block) =>
        !string.Equals(block, "none", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(block, "null", StringComparison.OrdinalIgnoreCase);

    private static void ValidateTuning(PaqetProfile profile, ICollection<string> errors)
    {
        ValidateOptionalIpv6(profile, errors);
        ValidateRange(profile.LocalIpv4Port, 0, 65_535, "IPv4 source port", errors);
        if (profile.LocalIpv4Port != 0 && profile.ConnectionCount != 1)
            errors.Add("A fixed source port requires exactly one Paqet connection.");
        if (!string.IsNullOrWhiteSpace(profile.LocalIpv6Endpoint) &&
            TryParseNumericEndpoint(profile.LocalIpv6Endpoint, true, out _, out var ipv6Port) &&
            ipv6Port != profile.LocalIpv4Port)
            errors.Add("IPv4 and IPv6 source ports must match.");

        if (string.IsNullOrWhiteSpace(profile.LogLevel) || !LogLevels.Contains(profile.LogLevel.Trim()))
        {
            errors.Add("Log level must be none, debug, info, warn, error, or fatal.");
        }

        ValidateRange(profile.PcapSocketBufferBytes, 1_024, 104_857_600, "PCAP socket buffer", errors);
        ValidateRange(profile.ConnectionCount, 1, 256, "Connection count", errors);

        if (string.IsNullOrWhiteSpace(profile.KcpMode) || !KcpModes.Contains(profile.KcpMode.Trim()))
        {
            errors.Add("KCP mode must be normal, fast, fast2, fast3, or manual.");
        }

        if (string.IsNullOrWhiteSpace(profile.KcpBlock) || !KcpBlocks.Contains(profile.KcpBlock.Trim()))
        {
            errors.Add("KCP encryption must be one of the algorithms supported by the bundled Paqet version.");
        }

        ValidateKcp(profile, errors);
    }

    internal static bool TryParseInterfaceGuid(string? value, out Guid guid)
    {
        guid = default;
        if (string.IsNullOrWhiteSpace(value) || ConfigurationValuePolicy.ContainsControlCharacter(value))
        {
            return false;
        }

        var candidate = value.Trim();
        const string devicePrefix = @"\Device\NPF_";
        if (candidate.StartsWith(devicePrefix, StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[devicePrefix.Length..];
        }

        return Guid.TryParse(candidate.Trim('{', '}'), out guid);
    }

    private static void ValidateOptionalIpv6(PaqetProfile profile, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(profile.LocalIpv6Endpoint))
        {
            if (!string.IsNullOrWhiteSpace(profile.Ipv6RouterMac))
            {
                errors.Add("An IPv6 address is required when an IPv6 router MAC is configured.");
            }

            return;
        }

        if (!TryParseNumericEndpoint(profile.LocalIpv6Endpoint, allowPortZero: true, out var address, out _) ||
            address.AddressFamily != AddressFamily.InterNetworkV6 || address.IsIPv6Multicast ||
            address.Equals(IPAddress.IPv6Any) || IPAddress.IsLoopback(address))
        {
            errors.Add("The local IPv6 endpoint must use bracket notation and a usable IPv6 address.");
        }

        ValidateMac(profile.Ipv6RouterMac, "IPv6 router MAC address", errors);
    }

    private static void ValidateForwardRules(
        IReadOnlyList<PaqetForwardRule>? rules,
        ICollection<string> errors)
    {
        if (rules is null)
        {
            errors.Add("The Paqet forward-rule collection is invalid.");
            return;
        }

        if (rules.Count > 64)
        {
            errors.Add("At most 64 Paqet port-forward rules may be configured.");
        }

        var listens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (rule is null)
            {
                errors.Add("A Paqet port-forward rule is invalid.");
                continue;
            }

            ValidateEndpoint(rule.ListenEndpoint, "Forward listen endpoint", loopbackOnly: true, errors);
            ValidateEndpoint(rule.TargetEndpoint, "Forward target endpoint", loopbackOnly: false, errors);
            if (!string.Equals(rule.Protocol, "tcp", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(rule.Protocol, "udp", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("A forward protocol must be tcp or udp.");
            }

            if (ConfigurationValuePolicy.TryNormalizeEndpoint(rule.ListenEndpoint, out var listen) &&
                !listens.Add($"{rule.Protocol}:{listen}"))
            {
                errors.Add($"Duplicate Paqet forward listen endpoint: {listen}.");
            }
        }
    }

    private static void ValidateSocksCredentials(
        string? username,
        string? password,
        ICollection<string> errors)
    {
        var hasUsername = !string.IsNullOrEmpty(username);
        var hasPassword = !string.IsNullOrEmpty(password);
        if (hasUsername != hasPassword)
        {
            errors.Add("SOCKS5 username and password must either both be set or both be empty.");
            return;
        }

        if (!hasUsername)
        {
            return;
        }

        if (Encoding.UTF8.GetByteCount(username!) > 255 || Encoding.UTF8.GetByteCount(password!) > 255 ||
            ConfigurationValuePolicy.ContainsControlCharacter(username!) ||
            ConfigurationValuePolicy.ContainsControlCharacter(password!))
        {
            errors.Add("SOCKS5 credentials must be at most 255 UTF-8 bytes and contain no control characters.");
        }
    }

    private static void ValidateKcp(PaqetProfile profile, ICollection<string> errors)
    {
        ValidateRange(profile.KcpMtu, 50, 1_500, "KCP MTU", errors);
        ValidateRange(profile.KcpReceiveWindow, 1, 32_768, "KCP receive window", errors);
        ValidateRange(profile.KcpSendWindow, 1, 32_768, "KCP send window", errors);
        ValidateRange(profile.SmuxBufferBytes, 1_024, MaximumBufferBytes, "SMUX buffer", errors);
        ValidateRange(profile.StreamBufferBytes, 1_024, MaximumBufferBytes, "Stream buffer", errors);
        ValidateRange(profile.SmuxKeepAliveSeconds, 1, 86_400, "SMUX keepalive", errors);
        ValidateRange(profile.SmuxKeepAliveTimeoutSeconds, 1, 86_400, "SMUX keepalive timeout", errors);

        if ((profile.StreamBufferBytes ?? 2 * 1024 * 1024) > (profile.SmuxBufferBytes ?? 4 * 1024 * 1024))
            errors.Add("Stream buffer must not exceed the SMUX buffer (defaults: 2 MiB and 4 MiB).");

        if ((profile.SmuxKeepAliveTimeoutSeconds ?? 8) < (profile.SmuxKeepAliveSeconds ?? 2))
        {
            errors.Add("SMUX keepalive timeout must be at least its keepalive interval (defaults: 8 and 2 seconds).");
        }

        if (string.Equals(profile.KcpMode, "manual", StringComparison.OrdinalIgnoreCase))
        {
            ValidateRequiredRange(profile.KcpNoDelay, 0, 1, "KCP nodelay", errors);
            ValidateRequiredRange(profile.KcpIntervalMilliseconds, 10, 5_000, "KCP interval", errors);
            ValidateRequiredRange(profile.KcpResend, 0, 2, "KCP resend", errors);
            ValidateRequiredRange(profile.KcpNoCongestion, 0, 1, "KCP congestion switch", errors);
        }

        if ((profile.FecDataShards is null) != (profile.FecParityShards is null))
        {
            errors.Add("Both FEC data and parity shard counts are required when FEC is enabled.");
        }
        else if (profile.FecDataShards is { } data && profile.FecParityShards is { } parity &&
                 !((data == 0 && parity == 0) || (data > 0 && parity > 0 && (long)data + parity <= 256)))
        {
            errors.Add("FEC shard counts must both be zero to disable FEC, or positive with a total of at most 256.");
        }
    }

    private static void ValidateTransportKey(string? value, ICollection<string> errors)
    {
        if (string.IsNullOrEmpty(value))
        {
            errors.Add("A Paqet transport key is required.");
        }
        else if (value.Length > 1_024 || ConfigurationValuePolicy.ContainsControlCharacter(value))
        {
            errors.Add("The Paqet transport key must be at most 1,024 characters and contain no control characters.");
        }
    }

    private static void ValidateEndpoint(
        string? endpoint,
        string label,
        bool loopbackOnly,
        ICollection<string> errors)
    {
        if (!ConfigurationValuePolicy.TryNormalizeEndpoint(endpoint, out var normalized) ||
            (loopbackOnly && (!TryParseNumericEndpoint(normalized, allowPortZero: false, out var address, out _) ||
                              !IPAddress.IsLoopback(address))))
        {
            errors.Add(loopbackOnly
                ? $"{label} must be a loopback address and a port from 1 to 65535."
                : $"{label} must contain a valid host and a port from 1 to 65535.");
        }
    }

    private static bool TryParseNumericEndpoint(
        string? endpoint,
        bool allowPortZero,
        out IPAddress address,
        out int port)
    {
        address = IPAddress.None;
        port = -1;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        var candidate = endpoint.Trim();
        string host;
        string portText;
        if (candidate.StartsWith("[", StringComparison.Ordinal))
        {
            var closingBracket = candidate.IndexOf(']');
            if (closingBracket < 2 || closingBracket + 2 >= candidate.Length ||
                candidate[closingBracket + 1] != ':')
            {
                return false;
            }

            host = candidate[1..closingBracket];
            portText = candidate[(closingBracket + 2)..];
        }
        else
        {
            var separator = candidate.LastIndexOf(':');
            if (separator < 1 || candidate[..separator].Contains(':', StringComparison.Ordinal))
            {
                return false;
            }

            host = candidate[..separator];
            portText = candidate[(separator + 1)..];
        }

        return IPAddress.TryParse(host, out address!) && int.TryParse(portText, out port) &&
               port >= (allowPortZero ? 0 : 1) && port <= 65_535;
    }

    private static void ValidateText(
        string? value,
        string label,
        int maximumLength,
        ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"A {label.ToLowerInvariant()} is required.");
        }
        else if (value.Length > maximumLength || ConfigurationValuePolicy.ContainsControlCharacter(value))
        {
            errors.Add($"{label} must be at most {maximumLength} characters and contain no control characters.");
        }
    }

    private static void ValidateMac(string? value, string label, ICollection<string> errors)
    {
        var normalized = value?.Trim().Replace('-', ':');
        if (string.IsNullOrWhiteSpace(value) || !MacAddressPattern().IsMatch(value.Trim()) ||
            normalized!.Equals("00:00:00:00:00:00", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("ff:ff:ff:ff:ff:ff", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"The {label} is invalid.");
        }
    }

    private static void ValidateFlags(
        IReadOnlyList<string>? flags,
        string label,
        ICollection<string> errors)
    {
        if (flags is null || flags.Count == 0 || flags.Count > 64 || flags.Any(flag =>
                string.IsNullOrWhiteSpace(flag) || flag.Length > TcpFlagCharacters.Count ||
                flag.Any(character => !TcpFlagCharacters.Contains(character))))
        {
            errors.Add($"At least one valid uppercase {label} TCP flag is required.");
        }
    }

    private static void ValidateRange(
        int? value,
        int minimum,
        int maximum,
        string label,
        ICollection<string> errors)
    {
        if (value is { } actual && (actual < minimum || actual > maximum))
        {
            errors.Add($"{label} must be between {minimum:N0} and {maximum:N0}.");
        }
    }

    private static void ValidateRange(
        int value,
        int minimum,
        int maximum,
        string label,
        ICollection<string> errors) => ValidateRange((int?)value, minimum, maximum, label, errors);

    private static void ValidateRequiredRange(
        int? value,
        int minimum,
        int maximum,
        string label,
        ICollection<string> errors)
    {
        if (value is null)
        {
            errors.Add($"{label} is required in manual mode.");
        }
        else
        {
            ValidateRange(value, minimum, maximum, label, errors);
        }
    }

    [GeneratedRegex("^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex MacAddressPattern();
}
