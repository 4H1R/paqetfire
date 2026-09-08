namespace PaqetFire.Core.Sharing;

public interface ISharedProxyCredentialGenerator
{
    SharedProxyCredentials Rotate(string username, int passwordLength = 24);
}
