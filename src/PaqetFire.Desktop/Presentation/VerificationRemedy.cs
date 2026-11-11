using PaqetFire.Core.Diagnostics;

namespace PaqetFire.Desktop.Presentation;

/// <summary>
/// One plain line on what to do about a failed or inconclusive check, and the page
/// (a navigation tag) where the user can do it, when there is one.
/// </summary>
public sealed record VerificationRemedy(string Text, string? ActionText = null, string? Destination = null)
{
    public const string ConnectionPage = "connection";
    public const string RoutingPage = "routing";

    private const string OpenConnection = "Open Paqet connection";
    private const string OpenRouting = "Open Routing";

    /// <summary>Null for passed, skipped, and informational checks.</summary>
    public static VerificationRemedy? For(VerificationCheckKind kind, VerificationCheckStatus status) => status switch
    {
        VerificationCheckStatus.Failed => ForFailure(kind),
        VerificationCheckStatus.Warning => ForInconclusive(kind),
        _ => null,
    };

    /// <summary>Order a list so problems come first: failed, warning, passed and notes, then skipped.</summary>
    public static int SortRank(VerificationCheckStatus status) => status switch
    {
        VerificationCheckStatus.Failed => 0,
        VerificationCheckStatus.Warning => 1,
        VerificationCheckStatus.Passed or VerificationCheckStatus.Info => 2,
        _ => 3,
    };

    private static VerificationRemedy ForFailure(VerificationCheckKind kind) => kind switch
    {
        VerificationCheckKind.EngineChain => new(
            "Connect the route, then verify again. If it will not connect, check the server address and transport key.",
            OpenConnection, ConnectionPage),
        VerificationCheckKind.ApplicationCapture => new(
            "Reconnect. If capture still fails, check that Windows Packet Filter is installed under Required software."),
        VerificationCheckKind.LocalRoute => new(
            "Disconnect and connect again. If it keeps failing, copy diagnostics and include them in a bug report."),
        VerificationCheckKind.TcpIpv4 or VerificationCheckKind.PublicAddress => new(
            "Check the server address and transport key, and that the Paqet server is running.",
            OpenConnection, ConnectionPage),
        VerificationCheckKind.UdpDns => new(
            "Make sure the server allows UDP, or turn off UDP routing.",
            OpenRouting, RoutingPage),
        VerificationCheckKind.DnsLeak => new(
            "Choose an encrypted DNS resolver, save, and verify again.",
            OpenRouting, RoutingPage),
        VerificationCheckKind.Ipv6 => new(
            "If your server or network has no IPv6, turn off IPv6 routing.",
            OpenRouting, RoutingPage),
        _ => new("Copy diagnostics and include them in a bug report."),
    };

    private static VerificationRemedy ForInconclusive(VerificationCheckKind kind) => kind switch
    {
        VerificationCheckKind.UdpDns => new(
            "No answer came back. Verify again in a minute; if it keeps happening, the server may block UDP.",
            OpenRouting, RoutingPage),
        VerificationCheckKind.Ipv6 => new(
            "If your server or network has no IPv6, turn off IPv6 routing; otherwise verify again in a minute.",
            OpenRouting, RoutingPage),
        _ => new("The test service did not answer, so the result is unknown. Verify again in a minute."),
    };
}
