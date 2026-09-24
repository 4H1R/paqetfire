using System.Diagnostics;
using PaqetFire.Broker.Engines;
using PaqetFire.Broker.Runtime;
using PaqetFire.Core.Configuration;
using PaqetFire.Core.Deployment;
using PaqetFire.Core.Engines;
using PaqetFire.Core.Routing;
using Xunit;

namespace PaqetFire.Broker.Tests;

// Release CI sets this only after staging the pinned payload. Ordinary source-only
// test runs skip these checks; an explicitly configured but incomplete payload fails.
public sealed class BundledEngineTheoryAttribute : TheoryAttribute
{
    public BundledEngineTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PAQETFIRE_TEST_PAYLOAD_ROOT")))
        {
            Skip = "Set PAQETFIRE_TEST_PAYLOAD_ROOT to a staged payload directory.";
        }
    }
}

public sealed class BundledEngineCompatibilityTests
{
    [BundledEngineTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionProxiFyreStartupValidatesBundledExecutable(bool tampered)
    {
        var executable = await VerifyEngineAsync(EngineKind.ProxiFyre);
        var configuration = Path.GetTempFileName();
        try
        {
            var paths = new RuntimePaths("unused", "unused", "unused", "unused", "unused",
                "unused", "unused", executable, configuration, "unused");
            var options = BundledEngineOptions.CreateProxiFyre(paths);
            if (tampered)
            {
                // Use the temporary file as a corrupted executable; never alter the payload.
                await File.WriteAllTextAsync(configuration, "corrupted executable");
                options = options with { ExecutablePath = configuration };
            }
            await using var adapter = new ProxiFyreProcessAdapter(options);
            if (tampered)
            {
                await Assert.ThrowsAsync<System.Security.Cryptography.CryptographicException>(
                    () => adapter.ValidatePayloadAsync(CancellationToken.None));
            }
            else
            {
                await adapter.ValidatePayloadAsync(CancellationToken.None);
            }
            var root = Environment.GetEnvironmentVariable("PAQETFIRE_TEST_PAYLOAD_ROOT")!;
            var manifest = ProductPayloadManifest.Load(Path.Combine(root, "payload-manifest.json"));
            Assert.Equal(manifest.Engines.Single(engine => engine.Engine == EngineKind.ProxiFyre).Version,
                (await adapter.GetStatusAsync(CancellationToken.None)).Version);
        }
        finally
        {
            File.Delete(configuration);
        }
    }

    [BundledEngineTheory]
    [InlineData(XrayDomainStrategy.AsIs, false)]
    [InlineData(XrayDomainStrategy.AsIs, true)]
    [InlineData(XrayDomainStrategy.IPIfNonMatch, false)]
    [InlineData(XrayDomainStrategy.IPIfNonMatch, true)]
    [InlineData(XrayDomainStrategy.IPOnDemand, false)]
    [InlineData(XrayDomainStrategy.IPOnDemand, true)]
    public async Task XrayAcceptsGeneratedPoliciesAndBundledGeoData(XrayDomainStrategy strategy, bool features)
    {
        var executable = await VerifyEngineAsync(EngineKind.Xray);
        var policy = new XrayRoutingPolicy(
            features ? RegionalRoutingPreset.IranDirect : RegionalRoutingPreset.None,
            strategy, features, features, features, features,
            features ? new LanSocksShare("192.168.50.12", 2082, "test-user", "test-password") : null,
            features ? new LanSocksShare("192.168.137.1", 2083, "test-user", "test-password") : null,
            features ? ["example.com", "*.example.net", "203.0.113.10", "2001:db8::/32"] : null);
        var configurationPath = Path.Combine(Path.GetTempPath(), $"PaqetFire-xray-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(configurationPath, new XrayJsonConfigurationWriter().Write(policy));
            using var validation = new Process
            {
                StartInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(executable)!,
                },
            };
            foreach (var argument in new[] { "run", "-test", "-config", configurationPath })
            {
                validation.StartInfo.ArgumentList.Add(argument);
            }
            validation.StartInfo.Environment["XRAY_LOCATION_ASSET"] = Path.GetDirectoryName(executable)!;
            await XrayProcessAdapter.RunValidationAsync(validation, TimeSpan.FromSeconds(30), CancellationToken.None);
        }
        finally
        {
            File.Delete(configurationPath);
        }
    }

    [BundledEngineTheory]
    [InlineData(RoutingMode.AllApplications, true, true, true, true, false)]
    [InlineData(RoutingMode.SelectedApplications, true, false, true, false, false)]
    [InlineData(RoutingMode.SelectedApplications, false, true, false, true, false)]
    [InlineData(RoutingMode.SelectedApplications, true, true, true, true, true)]
    public async Task ProxiFyreAcceptsGeneratedRules(
        RoutingMode mode, bool tcp, bool udp, bool ipv4, bool ipv6, bool tls)
    {
        var executable = await VerifyEngineAsync(EngineKind.ProxiFyre);
        var directory = Path.GetDirectoryName(executable)!;
        var paqet = Path.Combine(directory, "paqet.exe");
        var additionalExclusions = new[] { Path.Combine(directory, "xray.exe"), Path.Combine(directory, "PaqetFire.Broker.exe") };
        var policy = new RoutingPolicy(mode, "127.0.0.1:1081", ["test-app.exe"], [],
            BypassLan: tls, RouteTcp: tcp, RouteUdp: udp, RouteIpv4: ipv4, RouteIpv6: ipv6,
            Username: tls ? "test-user" : null, Password: tls ? "test-password" : null,
            Transport: tls ? Socks5Transport.Tls : Socks5Transport.Tcp,
            TlsServerName: tls ? "proxy.example.com" : null,
            TlsPinnedSha256: tls ? new string('a', 64) : null);
        var plan = RoutingPolicyCompiler.Compile(policy, paqet, executable, additionalExclusions);
        var locked = RoutingPolicyCompiler.CreateLockedExclusions(paqet, executable, additionalExclusions);
        var json = new ProxiFyreJsonConfigurationWriter().Write(plan, locked);

        // Windows PowerShell hosts .NET Framework, matching ProxiFyre's runtime.
        // Only load the shipped managed parser, without opening the packet driver.
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"PaqetFire-proxifyre-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var scriptPath = Path.Combine(temporaryDirectory, "validate.ps1");
            var inputPath = Path.Combine(temporaryDirectory, "input.json");
            var outputPath = Path.Combine(temporaryDirectory, "output.json");
            await File.WriteAllTextAsync(inputPath, json);
            await File.WriteAllTextAsync(scriptPath, """
                param([string]$EngineDirectory, [string]$InputPath, [string]$OutputPath)
                $ErrorActionPreference = 'Stop'
                Add-Type -Path (Join-Path $EngineDirectory 'Newtonsoft.Json.dll')
                Add-Type -Path (Join-Path $EngineDirectory 'ProxiFyre.Configuration.dll')
                $serializer = New-Object ProxiFyre.Configuration.ConfigurationSerializer
                $validator = New-Object ProxiFyre.Configuration.ConfigurationValidator
                $model = $serializer.Deserialize([IO.File]::ReadAllText($InputPath))
                if ($model.ExtensionData.Count -gt 0) { throw 'Unknown top-level configuration properties.' }
                foreach ($rule in $model.Proxies) {
                    if ($rule.ExtensionData.Count -gt 0) { throw 'Unknown proxy rule properties.' }
                }
                $result = $validator.Validate($model)
                if (-not $result.IsValid) { throw ($result.Errors -join [Environment]::NewLine) }
                [IO.File]::WriteAllText($OutputPath, $serializer.Serialize($model))
                """);
            using var validation = new Process
            {
                StartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath,
                "-EngineDirectory", directory, "-InputPath", inputPath, "-OutputPath", outputPath })
            {
                validation.StartInfo.ArgumentList.Add(argument);
            }
            Assert.True(validation.Start());
            var stdout = validation.StandardOutput.ReadToEndAsync();
            var stderr = validation.StandardError.ReadToEndAsync();
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await validation.WaitForExitAsync(deadline.Token);
            }
            finally
            {
                if (!validation.HasExited)
                {
                    validation.Kill(entireProcessTree: true);
                    await validation.WaitForExitAsync();
                }
                await Task.WhenAll(stdout, stderr);
            }
            Assert.True(validation.ExitCode == 0, (await stdout) + (await stderr));

            // Ensure the real parser retained the routing contract, rather than
            // accepting unknown properties and silently falling back to defaults.
            var roundTrip = await File.ReadAllTextAsync(outputPath);
            using var expected = System.Text.Json.JsonDocument.Parse(json);
            using var actual = System.Text.Json.JsonDocument.Parse(roundTrip);
            foreach (var property in expected.RootElement.EnumerateObject())
            {
                if (property.NameEquals("proxies"))
                {
                    var actualRules = actual.RootElement.GetProperty("proxies");
                    Assert.Equal(property.Value.GetArrayLength(), actualRules.GetArrayLength());
                    for (var index = 0; index < property.Value.GetArrayLength(); index++)
                    {
                        // Upstream can add defaults for omitted optional fields.
                        foreach (var field in property.Value[index].EnumerateObject())
                        {
                            Assert.True(System.Text.Json.JsonElement.DeepEquals(field.Value,
                                actualRules[index].GetProperty(field.Name)), $"ProxiFyre changed '{field.Name}'.");
                        }
                    }
                    continue;
                }
                Assert.True(System.Text.Json.JsonElement.DeepEquals(property.Value,
                    actual.RootElement.GetProperty(property.Name)), $"ProxiFyre changed '{property.Name}'.");
            }
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static async Task<string> VerifyEngineAsync(EngineKind kind)
    {
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("PAQETFIRE_TEST_PAYLOAD_ROOT")!);
        var manifest = ProductPayloadManifest.Load(Path.Combine(root, "payload-manifest.json"));
        var engine = Assert.Single(manifest.Engines, engine => engine.Engine == kind);
        foreach (var file in engine.Files)
        {
            await PayloadVerifier.VerifyAsync(root, file, CancellationToken.None);
        }
        return Path.Combine(root, engine.EntryPoint);
    }
}
