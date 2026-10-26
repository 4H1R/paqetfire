namespace PaqetFire.Core.Engines;

public enum EngineKind
{
    Paqet,
    Xray,
    ProxiFyre,
    Psiphon,
    OpenVpn,
}

public static class EngineKindExtensions
{
    /// <summary>An optional engine after Paqet that only the profiles selecting it run.</summary>
    public static bool IsExit(this EngineKind kind) => kind is EngineKind.Psiphon or EngineKind.OpenVpn;
}
