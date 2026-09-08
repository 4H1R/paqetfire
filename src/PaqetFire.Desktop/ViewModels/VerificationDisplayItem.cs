namespace PaqetFire.Desktop.ViewModels;

public sealed class VerificationDisplayItem
{
    public VerificationDisplayItem()
    {
    }

    public VerificationDisplayItem(string glyph, string name, string status, string detail, string duration)
    {
        Glyph = glyph;
        Name = name;
        Status = status;
        Detail = detail;
        Duration = duration;
    }

    public string Glyph { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
}
