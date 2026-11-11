namespace PaqetFire.Desktop.Presentation;

/// <summary>
/// Presentation of the advanced Paqet transport fields: one plain "none (insecure)" choice for
/// the two spellings Paqet accepts for no encryption, and whole-number input for the numbers.
/// </summary>
public static class AdvancedTransportText
{
    public const string NoEncryption = "none";
    public const string NoEncryptionLabel = "none (insecure)";

    /// <summary>Encryption algorithms offered in the picker; saved profiles may also hold "null".</summary>
    public static IReadOnlyList<string> EncryptionChoices { get; } =
        ["aes", "aes-128", "aes-128-gcm", "aes-192", "salsa20", "blowfish", "twofish", "cast5", "3des", "tea", "xtea", "xor", "sm4", NoEncryption];

    /// <summary>True for both spellings Paqet treats as no transport encryption.</summary>
    public static bool IsUnencrypted(string? block) =>
        string.Equals(block?.Trim(), "none", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(block?.Trim(), "null", StringComparison.OrdinalIgnoreCase);

    /// <summary>The picker choice that shows a saved value; "null" shows as the single none choice.</summary>
    public static string EncryptionChoice(string? block) => IsUnencrypted(block) ? NoEncryption : block?.Trim() ?? string.Empty;

    public static string EncryptionLabel(string choice) => IsUnencrypted(choice) ? NoEncryptionLabel : choice;

    public const string NoEncryptionWarning =
        "With none, traffic between this PC and the Paqet server is neither encrypted nor authenticated by the transport key, so anyone on the network path can read or alter it. The server must use the same setting.";

    /// <summary>Rounds a typed fraction to the whole number the field stores; null when it already is one.</summary>
    public static double? WholeNumberFor(double value) =>
        double.IsFinite(value) && value != Math.Truncate(value)
            ? Math.Round(value, MidpointRounding.AwayFromZero)
            : null;

    public static string NumberDescription(string explanation, int minimum, int maximum) =>
        $"{explanation} {minimum:N0} to {maximum:N0}; leave blank for the default.";
}
