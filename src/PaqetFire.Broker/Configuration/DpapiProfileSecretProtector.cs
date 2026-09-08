using System.Security.Cryptography;
using System.Text;
using PaqetFire.Core.Profiles;

namespace PaqetFire.Broker.Configuration;

public sealed class DpapiProfileSecretProtector : IProfileSecretProtector
{
    public string Protect(string purpose, string clearText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentNullException.ThrowIfNull(clearText);
        var clear = Encoding.UTF8.GetBytes(clearText);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(
                clear,
                CreateEntropy(purpose),
                DataProtectionScope.LocalMachine));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public string Unprotect(string purpose, string protectedText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentNullException.ThrowIfNull(protectedText);
        var encrypted = Convert.FromBase64String(protectedText);
        var clear = ProtectedData.Unprotect(encrypted, CreateEntropy(purpose), DataProtectionScope.LocalMachine);
        try
        {
            return Encoding.UTF8.GetString(clear);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static byte[] CreateEntropy(string purpose) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(purpose));
}
