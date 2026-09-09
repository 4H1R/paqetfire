# PaqetFire installer

The x64 MSI installs the self-contained WinUI desktop application under
`Program Files\PaqetFire`, registers and starts the `PaqetFire Broker` Windows
service, adds a Start Menu shortcut, and supports repair, upgrade, and uninstall.
During an upgrade or uninstall, setup closes the running desktop application,
registers the installed engine paths with Windows Restart Manager, and waits for
the broker to stop its owned engine processes before replacing files.
The setup wizard shows an explicit completion screen and installs both Start Menu
and Desktop shortcuts so users can immediately tell that installation succeeded.
It also installs the pinned Paqet alpha.21 payload, Xray 26.3.27, ProxiFyre 2.6.0, registers
`ProxiFyreService` for on-demand routing, and creates its program-scoped Windows
Firewall exceptions.
Xray receives local-subnet-only TCP and UDP firewall exceptions for the optional,
password-protected LAN SOCKS5 listener. The listener itself is disabled unless
the user enables sharing in PaqetFire.

ICE03 validation is suppressed because .NET 10's self-contained native runtime
files expose version-resource language metadata that is not representable in the
MSI `File.Language` column. Other Windows Installer validation remains enabled.

Npcap and Windows Packet Filter are kernel drivers and remain separately
licensed prerequisites. The app detects both and links to their official setup
when missing. Npcap Free must not be copied into this MSI; a fully unattended,
single-file driver chain requires an Npcap OEM payload and suitable Windows
Packet Filter redistribution rights.

This project uses WiX Toolset 7 with `AcceptEula=wix7`. The project owner has
confirmed that PaqetFire will not generate revenue and explicitly authorized
acceptance of the WiX 7 binary EULA for this build.

Build inputs are created with:

```powershell
dotnet publish src\PaqetFire.Desktop\PaqetFire.Desktop.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o artifacts\publish\win-x64\desktop -p:DebugType=None -p:DebugSymbols=false
dotnet publish src\PaqetFire.Broker\PaqetFire.Broker.csproj -c Release -r win-x64 --self-contained true -o artifacts\publish\win-x64\broker -p:DebugType=None -p:DebugSymbols=false
dotnet build installer\PaqetFire.Installer\PaqetFire.Installer.wixproj -c Release
```

The installer UX regression check can be run independently with:

```powershell
.\installer\Test-InstallerConfiguration.ps1
```
