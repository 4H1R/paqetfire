namespace PaqetFire.Core.Profiles;

/// <summary>
/// Protects catalog secrets at the persistence seam. Purpose is stable associated
/// data that adapters should bind cryptographically to the protected value.
/// </summary>
public interface IProfileSecretProtector
{
    string Protect(string purpose, string clearText);

    string Unprotect(string purpose, string protectedText);
}
