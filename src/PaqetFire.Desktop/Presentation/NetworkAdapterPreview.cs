using PaqetFire.Core.Network;

namespace PaqetFire.Desktop.Presentation;

public sealed record NetworkInterfaceOption(Guid? InterfaceGuid, string DisplayName);

public sealed class NetworkAdapterPreview(NetworkAdapterDetector detector)
{
    public Guid? SelectedInterfaceGuid { get; private set; }

    public IReadOnlyList<NetworkInterfaceOption> Options { get; private set; } = [];

    public DetectedNetworkAdapter? Detected { get; private set; }

    public void Refresh(Guid? selectedInterfaceGuid, bool resolveRouterMac = true)
    {
        SelectedInterfaceGuid = selectedInterfaceGuid;
        Detected = null;
        IReadOnlyList<NetworkAdapterDetails> adapters = [];
        try
        {
            adapters = detector.GetAdapters();
            if (resolveRouterMac)
                Detected = detector.Detect(adapters, selectedInterfaceGuid);
        }
        finally
        {
            var options = new List<NetworkInterfaceOption>
            {
                new(null, "Automatic (Ethernet, then Wi-Fi)"),
            };
            options.AddRange(adapters.Select(adapter => new NetworkInterfaceOption(
                adapter.InterfaceGuid, $"{adapter.InterfaceName} ({adapter.LocalAddress})")));
            if (selectedInterfaceGuid is { } selected && adapters.All(adapter => adapter.InterfaceGuid != selected))
                options.Add(new(selected, $"Unavailable interface ({selected})"));
            Options = options;
        }
    }
}
