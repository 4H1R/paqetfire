using System.Security.Cryptography;

namespace PaqetFire.Core.Sharing;

public sealed class SharedProxyCredentialGenerator : ISharedProxyCredentialGenerator
{
    // Avoid visually ambiguous characters while retaining over five bits of
    // entropy per generated character.
    private const string PasswordAlphabet =
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789-_";

    public SharedProxyCredentials Rotate(string username, int passwordLength = 24)
    {
        if (passwordLength is < 16 or > 128)
        {
            throw new ArgumentOutOfRangeException(
                nameof(passwordLength),
                "Generated passwords must contain 16 to 128 characters.");
        }

        var password = RandomNumberGenerator.GetString(PasswordAlphabet, passwordLength);
        return new SharedProxyCredentials(username, password);
    }
}
