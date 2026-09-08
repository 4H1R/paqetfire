using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaqetFire.Core.Sharing;

public sealed record RedactedShareSetupBundle
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private RedactedShareSetupBundle(
        string? name,
        string address,
        int port,
        string username)
    {
        Name = name;
        Address = address;
        Port = port;
        Username = username;
    }

    public int SchemaVersion => 1;

    public string? Name { get; }

    public string Protocol => "socks5";

    public string Address { get; }

    public int Port { get; }

    public string Username { get; }

    public bool RequiresPassword => true;

    public string EndpointUri => $"socks5://{Address}:{Port}";

    public static RedactedShareSetupBundle Create(
        SocksShareEndpoint endpoint,
        SharedProxyCredentials credentials,
        string? name = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credentials);
        if (name is not null && (name.Length > 80 || name.Any(char.IsControl)))
        {
            throw new ArgumentException(
                "A setup bundle name must contain at most 80 printable characters.",
                nameof(name));
        }

        return new RedactedShareSetupBundle(
            string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            endpoint.Address,
            endpoint.Port,
            credentials.Username);
    }

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);
}
