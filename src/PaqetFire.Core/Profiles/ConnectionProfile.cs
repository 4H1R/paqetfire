using PaqetFire.Core.Configuration;

namespace PaqetFire.Core.Profiles;

/// <summary>A stable identity and the connection settings saved under it.</summary>
public sealed record ConnectionProfile(Guid Id, PaqetFireSettings Settings);
