namespace PaqetFire.Core.Ipc;

public static class IpcProtocol
{
    public const int Version = 10;

    public const string PipeName = "PaqetFire.Broker.v10";

    // Profile import/export messages can contain an escaped copy of the bounded
    // profile document in addition to the normal broker snapshot envelope.
    public const int MaxMessageSizeBytes = 20 * 1024 * 1024;
}
