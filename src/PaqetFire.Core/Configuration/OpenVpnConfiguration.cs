using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PaqetFire.Core.Configuration;

/// <param name="Host">A host name or IP address, as the configuration names it.</param>
public sealed record OpenVpnRemote(string Host, int Port)
{
    public override string ToString() =>
        Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}

/// <summary>
/// An imported .ovpn file reduced to the client options PaqetFire passes to OpenVPN.
/// </summary>
/// <remarks>
/// OpenVPN runs as SYSTEM, and an .ovpn can name programs to run (<c>up</c>, <c>plugin</c>,
/// <c>tls-verify</c>, ...) and files to read or write (<c>log</c>, <c>status</c>, file
/// arguments to <c>ca</c> and friends). Only allowlisted options survive; every other option
/// is dropped and reported in <see cref="IgnoredOptions"/>. Certificates and keys must be
/// embedded, and the server must accept TCP, because OpenVPN reaches it through Paqet's
/// SOCKS5 proxy.
/// </remarks>
public sealed partial class OpenVpnConfiguration
{
    /// <summary>Provider configurations with embedded certificates are a few kilobytes.</summary>
    public const int MaximumLength = 256 * 1024;

    private const int DefaultPort = 1194;

    private const string TcpRequired =
        "OpenVPN reaches its server through Paqet's SOCKS5 proxy, which needs TCP. Download the TCP version of this configuration.";

    // Client options with no side effect outside the tunnel: no programs, files, routes,
    // DNS changes, or management interfaces.
    private static readonly FrozenSet<string> KeptOptions = new[]
    {
        "nobind", "persist-key", "persist-tun", "float", "fast-io", "resolv-retry",
        "remote-random", "remote-random-hostname", "connect-retry", "connect-retry-max",
        "connect-timeout", "server-poll-timeout", "hand-window", "tran-window", "tls-timeout",
        "cipher", "data-ciphers", "data-ciphers-fallback", "ncp-ciphers", "auth", "key-direction",
        "tls-version-min", "tls-version-max", "tls-cipher", "tls-ciphersuites", "tls-groups",
        "tls-cert-profile", "remote-cert-tls", "remote-cert-ku", "remote-cert-eku",
        "verify-x509-name", "ns-cert-type", "reneg-sec", "reneg-bytes", "reneg-pkts",
        "tun-mtu", "tun-mtu-extra", "link-mtu", "mssfix", "comp-lzo", "compress",
        "allow-compression", "ping", "ping-restart", "ping-exit", "keepalive", "push-peer-info",
        "mute", "mute-replay-warnings", "replay-window", "sndbuf", "rcvbuf", "auth-nocache",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Certificates and keys, accepted only as embedded blocks.
    private static readonly FrozenSet<string> InlineBlocks = new[]
    {
        "ca", "cert", "key", "tls-auth", "tls-crypt", "tls-crypt-v2", "extra-certs", "pkcs12",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Options PaqetFire writes itself; dropping them is not worth reporting.
    private static readonly FrozenSet<string> SuppliedOptions = new[]
    {
        "client", "pull", "tls-client", "dev", "dev-type", "dev-node", "proto", "port", "rport",
        "remote", "auth-user-pass", "auth-retry", "windows-driver", "verb",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> TcpProtocols = new[]
    {
        "tcp", "tcp-client", "tcp4", "tcp4-client", "tcp6", "tcp6-client",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly List<string> errors = [];
    private readonly List<string> ignored = [];
    private readonly List<string> keptLines = [];
    private readonly List<(string Name, string Content)> blocks = [];
    private readonly List<(string Host, int? Port)> remotes = [];
    private bool everyRemoteNamesTcp = true;
    private bool protocolNamed;
    private int? defaultPort;

    private OpenVpnConfiguration()
    {
    }

    /// <summary>Why OpenVPN cannot use this configuration; empty when it can.</summary>
    public IReadOnlyList<string> Errors => errors;

    /// <summary>Options and blocks PaqetFire removed, in the order they first appear.</summary>
    public IReadOnlyList<string> IgnoredOptions => ignored;

    /// <summary>Every server the configuration names, in order.</summary>
    public IReadOnlyList<OpenVpnRemote> Remotes { get; private set; } = [];

    public OpenVpnRemote? Remote => Remotes.Count > 0 ? Remotes[0] : null;

    /// <summary>Whether the server expects a username and password (<c>auth-user-pass</c>).</summary>
    public bool RequiresCredentials { get; private set; }

    public bool IsValid => errors.Count == 0;

    internal IReadOnlyList<string> KeptLines => keptLines;

    internal IReadOnlyList<(string Name, string Content)> Blocks => blocks;

    public static OpenVpnConfiguration Parse(string? text)
    {
        var configuration = new OpenVpnConfiguration();
        configuration.Read(text ?? string.Empty);
        return configuration;
    }

    private void Read(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            errors.Add("The OpenVPN configuration is empty.");
            return;
        }

        if (text.Length > MaximumLength)
        {
            errors.Add($"The OpenVPN configuration is larger than {MaximumLength / 1024} KB.");
            return;
        }

        if (text.Contains('\0', StringComparison.Ordinal))
        {
            errors.Add("The OpenVPN configuration is not a text file.");
            return;
        }

        var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line.StartsWith('<'))
            {
                index = ReadBlock(lines, index, line);
                continue;
            }

            ReadOption(Tokenize(line), index + 1);
        }

        // Without a proto line OpenVPN uses UDP, unless every remote names TCP itself.
        if (!protocolNamed && remotes.Count > 0 && !everyRemoteNamesTcp)
        {
            AddError(TcpRequired);
        }

        if (remotes.Count == 0)
        {
            errors.Add("The OpenVPN configuration names no server (remote line).");
        }

        if (!blocks.Any(block => block.Name == "ca"))
        {
            errors.Add("The OpenVPN configuration has no embedded <ca> certificate.");
        }

        Remotes = remotes.Select(remote => new OpenVpnRemote(remote.Host, remote.Port ?? defaultPort ?? DefaultPort)).ToArray();
    }

    private void ReadOption(List<string> tokens, int lineNumber)
    {
        var name = (tokens[0].StartsWith("--", StringComparison.Ordinal) ? tokens[0][2..] : tokens[0]).ToLowerInvariant();
        if (!OptionName().IsMatch(name))
        {
            errors.Add($"Line {lineNumber} is not an OpenVPN option.");
            return;
        }

        var arguments = tokens.Skip(1).ToArray();
        switch (name)
        {
            case "remote":
                ReadRemote(arguments, lineNumber);
                break;
            case "proto":
                protocolNamed = true;
                if (arguments.Length != 1 || !TcpProtocols.Contains(arguments[0]))
                {
                    AddError(TcpRequired);
                }

                break;
            case "port" or "rport":
                if (arguments.Length == 1 && TryParsePort(arguments[0], out var port))
                {
                    defaultPort = port;
                }
                else
                {
                    errors.Add($"Line {lineNumber} names an invalid port.");
                }

                break;
            case "auth-user-pass":
                RequiresCredentials = true;
                break;
            case "dev" or "dev-type":
                if (arguments.Length > 0 && !arguments[0].StartsWith("tun", StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add("PaqetFire supports routed OpenVPN servers (dev tun) only; this configuration uses a bridged (dev tap) server.");
                }

                break;
            case var _ when InlineBlocks.Contains(name):
                ReadCertificateOption(name, arguments);
                break;
            case var _ when KeptOptions.Contains(name):
                keptLines.Add(arguments.Length == 0 ? name : $"{name} {string.Join(' ', arguments.Select(Quote))}");
                break;
            case var _ when !SuppliedOptions.Contains(name):
                Ignore(name);
                break;
        }
    }

    private void ReadRemote(string[] arguments, int lineNumber)
    {
        if (arguments.Length is < 1 or > 3 || !HostName().IsMatch(arguments[0]))
        {
            errors.Add($"Line {lineNumber} names an invalid server.");
            return;
        }

        int? port = null;
        if (arguments.Length >= 2)
        {
            if (!TryParsePort(arguments[1], out var parsed))
            {
                errors.Add($"Line {lineNumber} names an invalid port.");
                return;
            }

            port = parsed;
        }

        if (arguments.Length == 3 && !TcpProtocols.Contains(arguments[2]))
        {
            AddError(TcpRequired);
            return;
        }

        everyRemoteNamesTcp &= arguments.Length == 3;
        remotes.Add((arguments[0], port));
    }

    private void ReadCertificateOption(string name, string[] arguments)
    {
        if (arguments.Length == 0 || arguments[0] == "[inline]")
        {
            // The embedded block carries the data; a tls-auth direction becomes key-direction.
            if (name == "tls-auth" && arguments.Length == 2 && arguments[1] is "0" or "1")
            {
                keptLines.Add($"key-direction {arguments[1]}");
            }

            return;
        }

        errors.Add($"The {name} option points to a file ({arguments[0]}). Use a configuration with its certificates and keys embedded.");
    }

    private int ReadBlock(string[] lines, int start, string opening)
    {
        if (!opening.EndsWith('>') || opening.StartsWith("</", StringComparison.Ordinal))
        {
            errors.Add($"Line {start + 1} is not an OpenVPN option.");
            return start;
        }

        var name = opening[1..^1].Trim().ToLowerInvariant();
        var closing = $"</{name}>";
        var content = new StringBuilder();
        for (var index = start + 1; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (string.Equals(line, closing, StringComparison.OrdinalIgnoreCase))
            {
                if (name == "connection")
                {
                    errors.Add("PaqetFire does not support <connection> blocks. Use a configuration with plain remote lines.");
                }
                else if (InlineBlocks.Contains(name))
                {
                    if (blocks.Any(block => block.Name == name))
                    {
                        errors.Add($"The OpenVPN configuration embeds <{name}> twice.");
                    }

                    blocks.Add((name, content.ToString()));
                }
                else
                {
                    Ignore($"<{name}>");
                }

                return index;
            }

            if (line.StartsWith('<'))
            {
                break;
            }

            if (line.Length > 0)
            {
                content.Append(line).Append('\n');
            }
        }

        errors.Add($"The <{name}> block is not closed.");
        return lines.Length;
    }

    private void AddError(string message)
    {
        if (!errors.Contains(message, StringComparer.Ordinal))
        {
            errors.Add(message);
        }
    }

    private void Ignore(string name)
    {
        if (!ignored.Contains(name, StringComparer.Ordinal))
        {
            ignored.Add(name);
        }
    }

    private static bool TryParsePort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    /// <summary>Splits an option line the way OpenVPN does: on whitespace, honoring double and single quotes.</summary>
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        var inToken = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else if (character == '\\' && quote == '"' && index + 1 < line.Length)
                {
                    current.Append(line[++index]);
                }
                else
                {
                    current.Append(character);
                }
            }
            else if (character is '"' or '\'')
            {
                quote = character;
                inToken = true;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
            }
            else if (character is '#' or ';' && !inToken)
            {
                break;
            }
            else
            {
                current.Append(character);
                inToken = true;
            }
        }

        if (inToken)
        {
            tokens.Add(current.ToString());
        }

        return tokens.Count == 0 ? [string.Empty] : tokens;
    }

    /// <summary>Writes an argument back so OpenVPN reads it as the same single token.</summary>
    internal static string Quote(string argument) =>
        argument.Length > 0 && argument.All(character => !char.IsWhiteSpace(character) && character is not ('"' or '\'' or '\\' or '#' or ';'))
            ? argument
            : $"\"{argument.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex OptionName();

    // Host names, IPv4, and IPv6 literals; nothing OpenVPN would read as a second token.
    [GeneratedRegex(@"^(?=.{1,253}$)[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex HostName();
}
