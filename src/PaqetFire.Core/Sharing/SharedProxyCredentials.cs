using System.Text;

namespace PaqetFire.Core.Sharing;

public sealed class SharedProxyCredentials
{
    public SharedProxyCredentials(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) ||
            username.Length > 64 ||
            username.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(username) > byte.MaxValue)
        {
            throw new ArgumentException(
                "The shared proxy username must contain 1 to 64 printable characters.",
                nameof(username));
        }

        if (string.IsNullOrEmpty(password) ||
            password.Length is < 8 or > 128 ||
            password.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(password) > byte.MaxValue)
        {
            throw new ArgumentException(
                "The shared proxy password must contain 8 to 128 printable characters.",
                nameof(password));
        }

        Username = username;
        Password = password;
    }

    public string Username { get; }

    public string Password { get; }

    public override string ToString() => $"{nameof(SharedProxyCredentials)} {{ Username = {Username}, Password = [REDACTED] }}";
}
