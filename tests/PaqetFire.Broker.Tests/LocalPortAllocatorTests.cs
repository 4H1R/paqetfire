using System.Net;
using System.Net.Sockets;
using PaqetFire.Broker.Engines;
using PaqetFire.Core.Configuration;
using Xunit;

namespace PaqetFire.Broker.Tests;

public sealed class LocalPortAllocatorTests
{
    private static readonly LocalEnginePorts Defaults = LocalEnginePorts.Default;

    [Fact]
    public void FreePortsStayWhereTheyAre()
    {
        var current = new LocalEnginePorts(31080, 31081, 31085, 31084);

        Assert.Equal(current, LocalPortAllocator.Plan(current, [], (_, _) => true));
    }

    [Fact]
    public void AHeldPortReturnsToItsDefaultWhenThatIsFree()
    {
        var current = Defaults with { Paqet = 31080 };

        var planned = LocalPortAllocator.Plan(current, [], (_, port) => port != 31080);

        Assert.Equal(Defaults, planned);
    }

    [Fact]
    public void AHeldPortMovesToAFreeCandidate()
    {
        var candidates = new Queue<int>([Defaults.XrayInbound, 25000]);

        var planned = LocalPortAllocator.Plan(
            Defaults,
            [],
            (_, port) => port != Defaults.Paqet,
            nextCandidate: (_, _) => candidates.Dequeue());

        // The first candidate is Xray's port: taking it would only push Xray off in turn.
        Assert.Equal(Defaults with { Paqet = 25000 }, planned);
    }

    [Fact]
    public void EnginesMoveOffASharePort()
    {
        var planned = LocalPortAllocator.Plan(
            Defaults,
            [Defaults.XrayInbound],
            (_, _) => true,
            nextCandidate: (_, _) => 26000);

        Assert.Equal(26000, planned.XrayInbound);
        Assert.Equal(Defaults.Paqet, planned.Paqet);
    }

    [Fact]
    public void APinnedRoleKeepsItsPortEvenWhileItLooksHeld()
    {
        // ProxiFyre still sends to Xray's port, so it must not move even while the port lingers.
        var planned = LocalPortAllocator.Plan(
            Defaults,
            [],
            (_, port) => port != Defaults.XrayInbound,
            new HashSet<LocalPortRole> { LocalPortRole.XrayInbound },
            (_, _) => 27000);

        Assert.Equal(Defaults, planned);
    }

    [Fact]
    public void NoFreePortIsAnError()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            LocalPortAllocator.Plan(Defaults, [], (_, _) => false, nextCandidate: (_, _) => 28000));

        Assert.Contains("Paqet", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInvalidSavedPlanStartsFromTheDefaults() =>
        Assert.Equal(Defaults, LocalPortAllocator.Plan(new LocalEnginePorts(1080, 1080, 1, 2), [], (_, _) => true));

    [Fact]
    public void IsFreeSeesAListenerAndItsRelease()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            Assert.False(LocalPortAllocator.IsFree(IPAddress.Loopback, port));
        }
        finally
        {
            listener.Stop();
        }

        Assert.True(LocalPortAllocator.IsFree(IPAddress.Loopback, port));
    }
}
