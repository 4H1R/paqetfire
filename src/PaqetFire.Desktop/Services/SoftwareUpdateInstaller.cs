using System.Diagnostics;

namespace PaqetFire.Desktop.Services;

public static class SoftwareUpdateInstaller
{
    public static void Start(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath) ||
            !Path.GetExtension(installerPath).Equals(".msi", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(installerPath))
        {
            throw new FileNotFoundException("The verified PaqetFire installer is unavailable.", installerPath);
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            ArgumentList = { "/i", installerPath, "/passive", "/norestart" },
            UseShellExecute = true,
            Verb = "runas",
        });

        if (process is null)
        {
            throw new InvalidOperationException("Windows Installer could not be started.");
        }
    }
}
