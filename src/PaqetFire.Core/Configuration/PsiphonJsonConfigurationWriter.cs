using System.Text;
using System.Text.Json;

namespace PaqetFire.Core.Configuration;

/// <summary>
/// Writes the psiphon-tunnel-core console client configuration. Every Psiphon connection,
/// including server list downloads, goes through Paqet's loopback SOCKS5 listener.
/// </summary>
public sealed class PsiphonJsonConfigurationWriter
{
    public const int LocalSocksPort = 1084;

    // The public network parameters shared by community Psiphon clients such as warp-plus.
    // Psiphon Inc. issues dedicated identifiers on request (developer-support@psiphon.ca);
    // these may be changed or restricted by Psiphon at any time.
    internal const string PropagationChannelId = "FFFFFFFFFFFFFFFF";
    internal const string SponsorId = "FFFFFFFFFFFFFFFF";
    internal const string RemoteServerListUrl =
        "https://s3.amazonaws.com//psiphon/web/mjr4-p23r-puwl/server_list_compressed";
    internal const string RemoteServerListSignaturePublicKey =
        "MIICIDANBgkqhkiG9w0BAQEFAAOCAg0AMIICCAKCAgEAt7Ls+/39r+T6zNW7GiVpJfzq/xvL9SBH5rIFnk0RXYEYavax3WS6HOD35eTAqn8AniOwiH+DOkvgSKF2caqk/y1dfq47Pdymtwzp9ikpB1C5OfAysXzBiwVJlCdajBKvBZDerV1cMvRzCKvKwRmvDmHgphQQ7WfXIGbRbmmk6opMBh3roE42KcotLFtqp0RRwLtcBRNtCdsrVsjiI1Lqz/lH+T61sGjSjQ3CHMuZYSQJZo/KrvzgQXpkaCTdbObxHqb6/+i1qaVOfEsvjoiyzTxJADvSytVtcTjijhPEV6XskJVHE1Zgl+7rATr/pDQkw6DPCNBS1+Y6fy7GstZALQXwEDN/qhQI9kWkHijT8ns+i1vGg00Mk/6J75arLhqcodWsdeG/M/moWgqQAnlZAGVtJI1OgeF5fsPpXu4kctOfuZlGjVZXQNW34aOzm8r8S0eVZitPlbhcPiR4gT/aSMz/wd8lZlzZYsje/Jr8u/YtlwjjreZrGRmG8KMOzukV3lLmMppXFMvl4bxv6YFEmIuTsOhbLTwFgh7KYNjodLj/LsqRVfwz31PgWQFTEPICV7GCvgVlPRxnofqKSjgTWI4mxDhBpVcATvaoBl1L/6WLbFvBsoAUBItWwctO2xalKxF5szhGm8lccoc5MZr8kfE0uxMgsxz4er68iCID+rsCAQM=";

    public string Write(PsiphonClientProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(profile.UpstreamCredentials);
        if (!PsiphonExitOptions.IsValidRegion(profile.EgressRegion))
        {
            throw new ConfigurationValidationException(["The Psiphon exit country is invalid."]);
        }

        if (profile.LocalSocksPort is < 1024 or > 65535 || profile.UpstreamSocksPort is < 1 or > 65535 ||
            profile.LocalSocksPort == profile.UpstreamSocksPort)
        {
            throw new ConfigurationValidationException(["The Psiphon SOCKS5 ports are invalid."]);
        }

        if (string.IsNullOrEmpty(profile.UpstreamCredentials.Username) ||
            string.IsNullOrEmpty(profile.UpstreamCredentials.Password))
        {
            throw new ConfigurationValidationException(["Psiphon's upstream SOCKS5 hop requires authentication."]);
        }

        var upstream =
            $"socks5://{Uri.EscapeDataString(profile.UpstreamCredentials.Username)}:" +
            $"{Uri.EscapeDataString(profile.UpstreamCredentials.Password)}@127.0.0.1:{profile.UpstreamSocksPort}";

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("PropagationChannelId", PropagationChannelId);
            writer.WriteString("SponsorId", SponsorId);
            writer.WriteString("RemoteServerListUrl", RemoteServerListUrl);
            writer.WriteString("RemoteServerListSignaturePublicKey", RemoteServerListSignaturePublicKey);
            writer.WriteString("RemoteServerListDownloadFilename", "remote_server_list");
            writer.WriteString("UpstreamProxyURL", upstream);
            if (profile.EgressRegion.Length > 0)
            {
                writer.WriteString("EgressRegion", profile.EgressRegion);
            }

            // Loopback only: no ListenInterface. The HTTP proxy is unused.
            writer.WriteNumber("LocalSocksProxyPort", profile.LocalSocksPort);
            writer.WriteBoolean("DisableLocalHTTPProxy", true);
            // The broker bounds startup itself; once running, Psiphon keeps re-establishing.
            writer.WriteNumber("EstablishTunnelTimeoutSeconds", 0);
            writer.WriteBoolean("EnableUpgradeDownload", false);
            writer.WriteBoolean("EmitDiagnosticNotices", false);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }
}
