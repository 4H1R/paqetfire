namespace PaqetFire.Desktop.Presentation;

/// <summary>How a saved profile is named in the pickers and the tray menu.</summary>
public static class ProfileDisplayName
{
    public static string Format(string name, bool isDefault, bool hasTransportKey) =>
        name + (isDefault ? " · default" : string.Empty) + (hasTransportKey ? string.Empty : " · needs key");
}
