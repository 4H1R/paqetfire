<p align="center">
  <img src="src/PaqetFire.Desktop/Assets/PaqetFire-1024.png" width="128" alt="PaqetFire icon">
</p>

<h1 align="center">PaqetFire</h1>

<p align="center">
  A native Windows control center for Paqet transport, Xray destination policy,
  and ProxiFyre per-application routing.
</p>

<p align="center">
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows%2010%2B-0078D4">
  <img alt=".NET" src="https://img.shields.io/badge/.NET-10.0-512BD4">
  <img alt="Version" src="https://img.shields.io/badge/version-0.6.18-FF6B42">
  <img alt="Status" src="https://img.shields.io/badge/status-alpha-F59E0B">
  <a href="https://github.com/4H1R/paqetfire/actions/workflows/windows-build.yml"><img alt="Windows build" src="https://github.com/4H1R/paqetfire/actions/workflows/windows-build.yml/badge.svg?branch=main"></a>
</p>

<p align="center">
  <img src="docs/images/paqetfire-overview.jpg" width="1100" alt="PaqetFire overview showing an active private route">
</p>

> [!IMPORTANT]
> PaqetFire is alpha software. Paqet itself is also under active development.
> Review the routing and leak-protection limitations before relying on it for
> sensitive traffic.

## What is PaqetFire?

PaqetFire brings three complementary networking tools into one desktop app:

- **[Paqet](https://github.com/hanselime/paqet)** provides the raw-packet/KCP
  transport and a local SOCKS5 endpoint.
- **[Xray-core](https://github.com/XTLS/Xray-core)** applies destination rules,
  including regional direct-routing presets and domain blocking.
- **[ProxiFyre](https://github.com/wiresock/proxifyre)** transparently captures
  selected Windows applications—or everything—and sends them to the route.

The user manages one profile and one connection. A protected Windows service
validates configuration, verifies bundled engine hashes, and starts the engine
chain in a safe order.

## Highlights

- Route every supported application or only selected executables.
- Find installed and running applications with a searchable executable picker.
- Keep multiple protected profiles, rename/switch/duplicate them, and import/export redacted profile catalogs.
- Independently enable TCP, UDP, IPv4, and IPv6 routing.
- Lock Paqet, Xray, ProxiFyre, and the broker out of catch-all rules to prevent
  recursive proxy loops.
- Use the Iran-direct Xray preset or route all destinations through Paqet.
- Route chosen domains, wildcard subdomains, IP addresses, and CIDR ranges directly.
- Block advertising domains, block QUIC, or send BitTorrent directly.
- Keep local routers, printers, and NAS devices reachable with LAN bypass.
- Optionally fail closed for ProxiFyre-covered applications with the routing
  kill switch.
- Share an authenticated Xray SOCKS5 port with trusted devices on the local
  subnet.
- Detect the active adapter, IPv4 address, gateway, and gateway MAC address.
- Verify engine health, the local SOCKS route, routed TCP/public IPv4, routed
  UDP/DNS, and IPv6 on demand; ProxiFyre process state is reported separately
  and is not presented as proof of capture or leak prevention.
- Apply trusted/untrusted network rules with resume/network-change recovery and bounded reconnect backoff.
- Store secrets with Windows DPAPI and restrict generated engine configuration
  to `SYSTEM` and Administrators.
- Rotate sharing credentials, test listener reachability, copy URI/QR payloads,
  and copy redacted client setup bundles.
- Minimize to the notification area and optionally connect on launch.
- Install the desktop app, broker, Paqet, Xray, and ProxiFyre as one product.

## Traffic flow

```text
Windows application
        |
        v
ProxiFyre (application capture and protocol/address-family selection)
        |
        v
Xray SOCKS5 :1081 (regional policy, blocking, direct routes)
        |
        v
Paqet SOCKS5 :1080
        |
        v
Paqet server -> destination
```

Trusted LAN devices can optionally enter through a separate authenticated Xray
SOCKS5 listener. ProxiFyre is not involved in LAN-client traffic, but the same
Xray regional and blocking policy applies.

Trusted devices on the Windows mobile hotspot can also enter through the same
SOCKS5 listener, sharing the identical Xray regional and blocking policy.

## Requirements

- Windows 10 version 2004 (build 19041) or newer, x64.
- Administrator approval for installation and the broker service.
- A compatible Paqet server endpoint and transport key.
- [Npcap](https://npcap.com/#download) for Paqet packet capture.
- [Windows Packet Filter 3.6.x](https://github.com/wiresock/ndisapi/releases/tag/v3.6.2),
  [.NET Framework 4.7.2+](https://dotnet.microsoft.com/download/dotnet-framework/net472),
  and the [Microsoft Visual C++ 2015–2022 x64 runtime](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)
  for ProxiFyre.

Paqet, Xray, and ProxiFyre are bundled in release installers. The Diagnostics
page detects every prerequisite and offers verified, on-demand installation
where redistribution allows it. Prerequisite installers are not bundled with
PaqetFire, so this helper does not increase the app's installed size. Npcap is
opened from its official download page because Npcap Free may not be redistributed.

## Install and connect

1. Install the latest PaqetFire x64 MSI from the project's Releases page when a
   release is available.
2. Open PaqetFire, then use **Diagnostics → Required software** to resolve
   anything missing. Npcap must be installed with WinPcap-compatible mode enabled.
3. Open **Paqet connection**, enter the server endpoint and transport key, then
   detect the active adapter.
4. Open **Routing** and review application mode, destination policy, protocol
   families, exclusions, and optional LAN sharing.
5. Save the profile and connect.

PaqetFire never searches for or modifies unrelated Paqet or ProxiFyre
installations. It only starts version-pinned payloads beneath its own protected
installation directory.

## Important routing behavior

- **Iran — Direct** sends Iranian domains and IP ranges directly from the host.
- **Send BitTorrent directly** exposes the host's normal public IP to peers.
- **LAN bypass** keeps private destinations outside PaqetFire.
- **Custom direct destinations** accept one domain, `*.domain`, IP address, or CIDR range per line. A bare domain also includes its subdomains. Custom entries are evaluated before optional blocking and regional rules. Domain matching requires Xray to receive or recover a hostname; add the IP or CIDR for protocols that expose only a resolved address.
- The **routing kill switch** covers traffic ProxiFyre can attribute to routed
  applications. It is not a machine-wide Windows Firewall kill switch.
- LAN SOCKS5 authentication controls access but does not encrypt traffic between
  the LAN device and this computer. Never expose the listener to the internet.

Read [LAN sharing](docs/lan-sharing.md), the
[routing kill switch](docs/kill-switch.md), and the
[Xray policy](docs/xray-integration.md) before enabling these options.

## Security architecture

```text
PaqetFire.Desktop (signed-in user, non-elevated)
        |
        | versioned + ACL-restricted named pipe
        v
PaqetFire.Broker (LocalSystem Windows service)
        |
        +-- validates and atomically writes configuration
        +-- verifies manifest paths, sizes, and SHA-256 hashes
        +-- starts Paqet -> Xray -> ProxiFyre
        `-- stops ProxiFyre -> Xray -> Paqet
```

The UI cannot submit arbitrary executable paths or command-line arguments to the
privileged broker. Release payloads are declared in
`payload/payload-manifest.json`, and engine paths must remain under the protected
payload root.

More detail is available in [Architecture](docs/architecture.md) and
[Connection lifecycle](docs/lifecycle-design.md).

## Build from source

### Tooling

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Visual Studio with the .NET desktop and Windows App SDK workloads
- WiX Toolset 7 for MSI packaging

### Compile and test

```powershell
dotnet restore PaqetFire.slnx
dotnet test PaqetFire.slnx -c Release
dotnet build PaqetFire.slnx -c Release
```

The UI and broker compile without downloaded engine binaries, but the broker
will report the payload as unavailable. For a functional installer, use the
release lock to download and verify the pinned upstream artifacts, then publish:

```powershell
.\scripts\Stage-ReleasePayload.ps1
```

```powershell
dotnet publish src\PaqetFire.Desktop\PaqetFire.Desktop.csproj `
  -c Release -r win-x64 --self-contained true `
  -o artifacts\publish\win-x64\desktop

dotnet publish src\PaqetFire.Broker\PaqetFire.Broker.csproj `
  -c Release -r win-x64 --self-contained true `
  -o artifacts\publish\win-x64\broker

dotnet build installer\PaqetFire.Installer\PaqetFire.Installer.wixproj -c Release
```

See [payload staging](payload/README.md) and the
[installer documentation](installer/README.md) for the release contract.

## Continuous integration builds

Every push to `main` runs the Windows build workflow. It downloads the pinned
Paqet, Xray, and ProxiFyre archives from their official GitHub releases, verifies
the archive and installed-file SHA-256 hashes, runs the test suite, and packages
the complete x64 MSI with WiX 7.

Pull requests run build and test validation with read-only repository permissions,
without downloading engine payloads or publishing a release. The Windows test
project covers broker IPC deadlines, subprocess cleanup, configuration permissions,
snapshot caching, and desktop save behavior. Protected-file lifecycle tests require
an elevated Administrator or SYSTEM token; ordinary local test runs report these
tests as skipped.

To download a build, open the repository's **Actions** page, select the latest
successful **Windows build** run, and download the
`PaqetFire-windows-x64-<commit>` artifact. The artifact contains the MSI and its
SHA-256 checksum and is retained for 30 days. CI builds are currently unsigned
development builds; Windows may show a publisher warning.

## Repository layout

```text
src/
  PaqetFire.Desktop/   WinUI 3 desktop application
  PaqetFire.Broker/    privileged Windows service and engine adapters
  PaqetFire.Core/      configuration, routing, IPC, and lifecycle contracts
tests/                 deterministic configuration and lifecycle tests
installer/             WiX 7 MSI project
payload/               manifest, notices, and local payload-staging contract
docs/                  architecture and engine-integration notes
```

Generated builds, downloaded engines, credentials, certificates, and driver
packages are intentionally excluded from Git.

## Project status

Version 0.6.18 is a usable development preview. The current source includes the
native desktop UI, broker service, engine adapters, regional routing, protocol
selection, kill switch, LAN SOCKS5 sharing, tray behavior, diagnostics, and the
WiX installer.

Notable work still planned:

- end-to-end SOCKS5 UDP-associate and packet-leak tests;
- a machine-wide firewall kill-switch mode;
- code signing and trusted automatic updates;
- a redistributable driver bootstrapper, subject to upstream licensing;

See the [roadmap](docs/roadmap.md) and
[development progress](docs/progress.md).

## Third-party software and licensing

PaqetFire distributes independently maintained components as an aggregate:

| Component | Pinned version | License |
| --- | ---: | --- |
| [Paqet](https://github.com/hanselime/paqet) | `v1.0.0-alpha.21` | MIT |
| [Xray-core](https://github.com/XTLS/Xray-core) | `26.3.27` | MPL-2.0 |
| [ProxiFyre](https://github.com/wiresock/proxifyre) | `2.6.0` | AGPL-3.0 |

The repository contains the corresponding notices and upstream license texts.
See [THIRD_PARTY_NOTICES.md](payload/THIRD_PARTY_NOTICES.md) for exact-source
links and packet-driver licensing notes. PaqetFire reimplements the Windows
workflow it needs and does not bundle `paqctl`.

## Contributing

Issues and focused pull requests are welcome. Please include a clear description
of routing behavior changes and add deterministic tests when changing generated
Paqet, Xray, or ProxiFyre configuration. Do not commit engine binaries, driver
installers, secrets, generated configuration, or local certificates.
