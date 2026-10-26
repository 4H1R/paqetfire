using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PaqetFire.Core.Configuration;

namespace PaqetFire.Broker.Configuration;

public sealed class MachineSettingsStore(string settingsPath, ProtectedDirectory? directory = null) : IMachineSettingsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PaqetFire.Settings.v1");
    private static readonly byte[] LanPasswordEntropy = Encoding.UTF8.GetBytes("PaqetFire.LanSocksPassword.v1");
    private static readonly byte[] OpenVpnConfigurationEntropy = Encoding.UTF8.GetBytes("PaqetFire.OpenVpnConfiguration.v1");
    private static readonly byte[] OpenVpnPasswordEntropy = Encoding.UTF8.GetBytes("PaqetFire.OpenVpnPassword.v1");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _settingsPath = ValidatePath(settingsPath);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<PaqetFireSettings?> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProtectedDirectory.EnsureFor(directory, _settingsPath);
            if (!File.Exists(_settingsPath))
            {
                return null;
            }

            await using var stream = new FileStream(
                _settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var stored = await JsonSerializer.DeserializeAsync<StoredSettings>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("The saved settings are empty.");

            var key = Unprotect(stored.ProtectedTransportKey, Entropy);
            var lanPassword = string.IsNullOrEmpty(stored.ProtectedLanSocksPassword)
                ? string.Empty
                : Unprotect(stored.ProtectedLanSocksPassword, LanPasswordEntropy);
            var openVpn = stored.Settings.OpenVpn ?? new();
            return stored.Settings with
            {
                TransportKey = key,
                LanSocksPassword = lanPassword,
                OpenVpn = openVpn with
                {
                    Configuration = UnprotectOptional(stored.ProtectedOpenVpnConfiguration, OpenVpnConfigurationEntropy),
                    Password = UnprotectOptional(stored.ProtectedOpenVpnPassword, OpenVpnPasswordEntropy),
                },
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveAsync(
        PaqetFireSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProtectedDirectory.EnsureFor(directory, _settingsPath);
            var openVpn = settings.OpenVpn ?? new();
            var stored = new StoredSettings(
                settings with
                {
                    TransportKey = string.Empty,
                    LanSocksPassword = string.Empty,
                    OpenVpn = openVpn with { Configuration = string.Empty, Password = string.Empty },
                },
                Protect(settings.TransportKey, Entropy),
                string.IsNullOrEmpty(settings.LanSocksPassword)
                    ? null
                    : Protect(settings.LanSocksPassword, LanPasswordEntropy))
            {
                ProtectedOpenVpnConfiguration = ProtectOptional(openVpn.Configuration, OpenVpnConfigurationEntropy),
                ProtectedOpenVpnPassword = ProtectOptional(openVpn.Password, OpenVpnPasswordEntropy),
            };
            var temporary = _settingsPath + $".{Guid.NewGuid():N}.new";
            try
            {
                await using (var stream = ProtectedConfigurationFile.CreateNew(temporary))
                {
                    await JsonSerializer.SerializeAsync(stream, stored, JsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, _settingsPath, overwrite: true);
                ProtectedConfigurationFile.Harden(_settingsPath);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The machine settings path must be absolute.", nameof(path));
        }

        return Path.GetFullPath(path);
    }

    private static string Protect(string value, byte[] entropy)
    {
        var clear = Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(
                clear,
                entropy,
                DataProtectionScope.LocalMachine));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static string Unprotect(string value, byte[] entropy)
    {
        var encrypted = Convert.FromBase64String(value);
        var clear = ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.LocalMachine);
        try
        {
            return Encoding.UTF8.GetString(clear);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    private static string? ProtectOptional(string? value, byte[] entropy) =>
        string.IsNullOrEmpty(value) ? null : Protect(value, entropy);

    private static string UnprotectOptional(string? value, byte[] entropy) =>
        string.IsNullOrEmpty(value) ? string.Empty : Unprotect(value, entropy);

    private sealed record StoredSettings(
        PaqetFireSettings Settings,
        string ProtectedTransportKey,
        string? ProtectedLanSocksPassword = null)
    {
        // An imported .ovpn may embed a private key, so it is protected like the passwords.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ProtectedOpenVpnConfiguration { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ProtectedOpenVpnPassword { get; init; }
    }
}
