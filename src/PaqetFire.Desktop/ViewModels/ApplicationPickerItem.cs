namespace PaqetFire.Desktop.ViewModels;

public sealed class ApplicationPickerItem
{
    public string DisplayName { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;

    public string StateText { get; set; } = string.Empty;

    public bool IsSelected { get; set; }
}
