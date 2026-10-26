namespace PaqetFire.Broker.Ipc;

/// <summary>
/// The desktop process behind a request. Files it names are opened as that user, so the
/// SYSTEM broker never reads, or authenticates to, a location the user could not reach.
/// </summary>
public sealed class BrokerRequester(Func<string, FileStream>? openFileAsUser)
{
    public static BrokerRequester Unknown { get; } = new(null);

    public FileStream OpenFileAsUser(string path) =>
        openFileAsUser?.Invoke(path) ??
        throw new InvalidOperationException("Files can only be chosen in the PaqetFire app.");
}
