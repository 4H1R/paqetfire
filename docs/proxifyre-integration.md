# ProxiFyre integration contract

This note records the production integration contract for PaqetFire. It was
rechecked against ProxiFyre 2.6.4 documentation and source on 2026-10-22.

## Runtime model

PaqetFire must treat ProxiFyre as a separately managed engine process, not load
`socksify.dll` into the broker. The installed topology is:

- PaqetFire Broker running as `LocalSystem`, checking `NDISRD`, writing validated
  configuration, and supervising the bundled `ProxiFyre.exe` child process;
- `app-config.json` beside the installed `ProxiFyre.exe`; and
- program-scoped inbound TCP and UDP firewall rules for `ProxiFyre.exe`, because
  the redirect listeners use dynamically allocated ports.

PaqetFire runs the bundled executable as a broker-supervised child process under
LocalSystem and removes any obsolete standalone ProxiFyre service during upgrade.
Before launch, the broker protects the engine directory from standard-user writes
to satisfy ProxiFyre's service-path security check. See the upstream
[official installer contract](https://github.com/wiresock/proxifyre/blob/main/docs/installer.md#service)
and [firewall contract](https://github.com/wiresock/proxifyre/blob/main/docs/installer.md#firewall-rules).

The broker writes configuration atomically before starting or restarting
the process. ProxiFyre reads the configuration only at startup, so saving a
change without a restart does not apply it. The upstream configuration lives
beside the resolved executable and the upstream GUI preserves a `.bak` file;
see the [official configuration reference](https://github.com/wiresock/proxifyre/blob/main/docs/configuration.md).

## Configuration and route behavior

`ProxiFyreJsonConfigurationWriter` supports the complete ProxiFyre rule surface
below. The product currently always targets the local Xray listener over plain,
unauthenticated SOCKS5/TCP at `Info` log level; the TLS, authentication, and
log-level options have no UI.

- ordered application rules (first match wins);
- catch-all routing represented by exactly `"appNames": [""]` and placed last;
- higher-priority process exclusions;
- TCP and/or UDP;
- IPv4 and/or IPv6 destinations;
- plain SOCKS5/TCP or SOCKS5-over-TLS;
- username/password authentication (both or neither, each at most 255 UTF-8
  bytes);
- TLS server name, normalized 64-hex-character SHA-256 certificate pin, and the
  explicit unsafe-certificate option;
- LAN bypass; and
- all five engine log levels.

The upstream SOCKS endpoint itself must resolve over IPv4. IPv6 *destinations*
can still be routed. Unsupported destination families are blocked rather than
allowed to leak directly. Fragmented IPv6 datagrams are a documented exception:
ProxiFyre passes them directly. These constraints are documented in the
[official protocol/address-family reference](https://github.com/wiresock/proxifyre/blob/main/docs/configuration.md#protocols-and-address-families).

LAN bypass covers the upstream-documented local IPv4 ranges, including RFC1918,
link-local/APIPA, and multicast. It is intentionally opt-in because enabling it
means those destinations are direct.

## Loop-safe catch-all routing

For “route everything,” PaqetFire emits a catch-all rule and locks the following
exclusions so the user cannot remove them:

1. the full installed path of `paqet.exe`;
2. the full installed path of `xray.exe`;
3. the full installed path of `ProxiFyre.exe`; and
4. the full installed path of `PaqetFire.Broker.exe`; and
5. `SharedAccess`, the Internet Connection Sharing service.

ProxiFyre filters every adapter that is up with an address, the Mobile hotspot
adapter included, and `bypassLan` does not cover it when local devices are
routed. Internet Connection Sharing is the hotspot's DHCP server and DNS proxy,
so a catch-all carried its replies to a joining phone into Xray and the phone
never got an address. ProxiFyre names a socket owned by a service after the
service (its service tag), not after the shared `svchost.exe`, so this
exclusion leaves Windows Update, the Task Scheduler, and the other services in
that host routed.

Full paths are preferred. ProxiFyre's exclusion matcher is deliberately
permissive: a name-only exclusion is a substring match and can accidentally
bypass unrelated programs. The carrier tunnel process must be excluded or its
outer packets can be captured by the catch-all and recursively depend on the
tunnel itself. See the upstream
[application matching and exclusion semantics](https://github.com/wiresock/proxifyre/blob/main/docs/configuration.md#exclusions).

`NDISRD`, Npcap, `wpcap.dll`, and `Packet.dll` are drivers/libraries, not carrier
processes. They do not belong in `excludes`; excluding the owning Paqet process
is what prevents the route loop. If a future Paqet build launches a separate
network carrier helper, that helper's full executable path must be added to the
locked set.

## Required bundled payload

Do not copy only `ProxiFyre.exe`. Stage one complete architecture-matched
upstream release and keep all of its engine dependencies together. The current
official MSI file set includes `ProxiFyre.exe`, its `.config`,
`ProxiFyre.Configuration.dll`, `socksify.dll`, Newtonsoft.Json, NLog, Topshelf,
and `NLog.config`. The authoritative list is in the
[upstream MSI contents](https://github.com/wiresock/proxifyre/blob/main/docs/installer.md#msi-contents-and-behavior).

The bundled 2.6.4 payload comes from the official x64 ZIP and is verified against
the published archive checksum and the per-file manifest. The ZIP does not
include prerequisite installers. Upstream's first-party 2.6.4 binaries are
unsigned; archive and file digests establish the pinned artifact identity.

Since 2.6.3 the engine refuses to run from a directory that standard users can
modify, or whose files are owned by an individual account; 2.6.4 accepts the
standard inherited Program Files entries (`CREATOR OWNER`). The MSI layout under
`%ProgramFiles%\PaqetFire\broker\payload` satisfies this. A development payload
staged under a user profile does not, so live ProxiFyre tests must run from an
installed or SYSTEM-owned copy. The bundled `NLog.config` also rotates the
engine log at 50 MiB and keeps three archives.

For x64, the official ProxiFyre deployment contract currently requires:

- Windows Packet Filter `3.6.2.1` x64 MSI (819,200 bytes, SHA-256
  `9c388c0b7f189f7fa98720bae2caecf7d64f30910838b80b438ecf8956b8502c`);
- Windows Packet Filter API major `3`, minor `0x0601` or later, below major `4`;
- Microsoft Visual C++ v14 x64 runtime `14.44.35211.0` or later (the on-demand
  installer fetches `14.51.36247.0`); and
- .NET Framework 4.7.2 or later.

The driver identity, compatibility checks, upstream filename, size, and pinned
hash are maintained in the
[official Windows Packet Filter prerequisite section](https://github.com/wiresock/proxifyre/blob/main/docs/installer.md#windows-packet-filter-prerequisite).

An MSI must not install another MSI with a custom action, so the PaqetFire MSI
does not chain these prerequisites. The broker reports each one as missing and
blocks connecting; the desktop app downloads the pinned Windows Packet Filter and
VC++ installers from their official URLs, verifies their SHA-256, and launches
them. A single offline setup would need a WiX Burn bundle and redistribution
rights for the embedded payloads.

## Licensing and release gate

ProxiFyre is AGPL-3.0. Any distributed ProxiFyre binary needs the corresponding
license notices and source-availability compliance. The project states this in
its [official README](https://github.com/wiresock/proxifyre#license) and ships the
[AGPL-3.0 license](https://github.com/wiresock/proxifyre/blob/main/LICENSE).

Windows Packet Filter is a separate dependency. NT KERNEL states that it is free
for personal, educational, and nonprofit use, while publishers embedding its
precompiled driver in a product are offered a binary license. “No revenue” does
not automatically settle every redistribution case. Before distributing
PaqetFire beyond personal testing, confirm the intended use against the
[publisher's licensing page](https://www.ntkernel.com/windows-packet-filter/licensing/).

## Broker wiring

On every connect, `PaqetFireRuntime`:

1. verifies the payload manifest hashes;
2. passes the installed full paths of Xray and the broker, alongside Paqet and
   ProxiFyre, to `RoutingPolicyCompiler.CreateLockedExclusions(...)`, then writes
   the resulting `ProxiFyreJsonConfigurationWriter` JSON beside `ProxiFyre.exe`
   through the atomic protected store;
3. starts Paqet, then Xray, waiting for each local listener before starting
   ProxiFyre; and stops them in the reverse order.

Missing `NDISRD`, configuration validation, and engine start failures are
surfaced separately. The installer keeps the two program-scoped ProxiFyre firewall
rules and removes obsolete standalone `ProxiFyreService` registration; the broker
owns the process lifecycle. Before every connect, the broker also restores any of the installer's four engine
firewall rules that are missing, disabled, or point at another path, using the same
rule names so MSI uninstall still removes them. Windows Installer deletes these rules
by name when *any* PaqetFire package is uninstalled, and a firewall reset deletes them
too; without them the Private or Public profile silently drops ProxiFyre's redirected
traffic.
