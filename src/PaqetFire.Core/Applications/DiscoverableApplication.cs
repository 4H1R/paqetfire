namespace PaqetFire.Core.Applications;

public sealed record DiscoverableApplication(
    string DisplayName,
    string ExecutablePath,
    bool IsInstalled,
    bool IsRunning);
