namespace PaqetFire.Desktop.ViewModels;

public sealed class ProfileDisplayItem
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public bool IsDefault { get; set; }
}
