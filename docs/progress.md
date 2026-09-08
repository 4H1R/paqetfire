# Development progress

## Implemented for the next feature release

- Added an on-demand connection verification report for the engine chain, local
  Xray SOCKS handshake, routed TCP/public IPv4, SOCKS5 UDP-associate DNS, and
  IPv6. ProxiFyre process/policy state is an explicit warning because it cannot
  prove application capture or the absence of system DNS/WebRTC leaks.
- Added trusted/untrusted network automation policy with stable network IDs,
  profile targeting, captive-portal and hotspot interlocks, sleep/network-change
  settling, temporary pause, and bounded reconnect backoff.
- Added a protected multi-profile catalog with active/default selection,
  renaming, duplication, deletion, legacy migration, machine-scoped DPAPI secrets, and
  strict redacted import/export.
- Added searchable installed/running application discovery, missing-path warnings,
  and a routing picker.
- Added cryptographic sharing-password rotation, escaped URI/QR payloads,
  redacted setup bundles, and LAN/hotspot listener reachability checks.

## Completed 0.6.8 patch

- Removed the unsupported `bittorrent` Xray sniffing override while retaining
  the valid direct-BitTorrent routing rule.
- Added a regression test covering both local and LAN-sharing Xray inbounds.
- Prevented the broker health monitor from treating normal sequential engine
  startup or shutdown as an unhealthy chain.
- Preserved the original engine failure detail in broker responses and Xray
  diagnostics instead of reducing the failure to a generic stopped state.
- Regenerate engine configurations from the protected saved profile on every
  connection so upgrades and network-adapter changes cannot leave stale files.
- Replaced the unreliable standalone ProxiFyre Windows service with a
  broker-supervised child process and remove the obsolete service on upgrade.
- Harden the ProxiFyre engine directory before launch so ProxiFyre 2.6.0's
  service-path safety check cannot reject inherited `CREATOR OWNER` access.

## Completed 0.6.4 patch

- Moved LAN SOCKS5 sharing from Paqet connection settings to Routing.
- Enabled ad blocking and direct BitTorrent routing for new profiles by default.
- Made TCP, UDP, IPv4, and IPv6 routing controls selectable and persisted their
  values through the desktop, broker IPC, and generated ProxiFyre policy.
- Reject routing profiles that disable every protocol or every address family.

## Completed 0.6.3 patch

- Fixed the broker becoming unavailable after the first desktop IPC connection.
- Changed the broker to one long-lived desktop session at a time so Windows does
  not need to create overlapping secured pipe instances under LocalSystem.
- Added a bounded retry when Windows temporarily prevents listener creation.
- Added `scripts/Test-InstalledBroker.ps1` as a safe, secret-free installed
  broker diagnostic.

## Completed 0.6 vertical slice

- Added opt-in LAN sharing through a separate authenticated Xray SOCKS5 inbound.
- Bind the shared listener to the broker-detected LAN IPv4 address rather than
  all interfaces.
- Added configurable port and username plus an encrypted broker-owned password.
- Added Xray-only TCP and UDP firewall rules restricted to the local subnet.
- Hardened every generated engine configuration to SYSTEM and Administrators;
  this protects both the LAN password and the Paqet transport key at rest.
- Added validation and deterministic Xray configuration regression tests.

## Completed 0.5 vertical slice

- Added an optional routing kill switch backed by the existing ProxiFyre
  lifecycle seam.
- Added a stable `Guarded` state: ProxiFyre remains running while Paqet and Xray
  are stopped, preventing protected applications from falling back to a direct
  connection.
- Restore the guarded state after broker restart and after a failed connection.
- Updated the health monitor to normalize an unhealthy chain to either guarded
  or fully disconnected according to the saved policy.
- Clarified local-network routing labels and documented the scope of protection.
- Added lifecycle regression tests for guard, connect, rollback, and disconnect.

## Completed 0.4.1 patch

- Constrained the title-bar version badge to a compact 22 px chip.
- Centered the version label and removed vertical padding that stretched the badge.

## Completed 0.4 vertical slice

- Updated the pinned ProxiFyre payload to 2.6.0 with its official checksum.
- Added close-to-notification-area behavior, enabled by default.
- Added tray actions to restore or fully exit PaqetFire.

## Completed 0.3 vertical slice

- Bundled Xray-core 26.3.27 with pinned GeoIP and GeoSite databases.
- Added a default Iran-direct regional preset and a `None` preset.
- Added optional ads, QUIC, and direct-BitTorrent routing rules.
- Added selectable `AsIs`, `IPIfNonMatch`, and `IPOnDemand` domain strategies.
- Extended ordered lifecycle and health monitoring to Paqet -> Xray -> ProxiFyre.

## Completed 0.2 vertical slice

- Native WinUI 3 desktop shell and branded application icon.
- Self-contained .NET 10 project structure.
- Typed route-everything policy with immutable carrier exclusions.
- Deterministic Paqet YAML generation with strict value validation.
- Deterministic ProxiFyre JSON generation with rule-order and loop checks.
- Hash-verifiable, architecture-specific bundled payload manifest.
- Broker payload integrity inspection before engine activation.
- Atomic broker-owned configuration writes with backup and rollback.
- Versioned, bounded, ACL-restricted named-pipe protocol.
- Desktop named-pipe client with request correlation and typed events.
- Ordered connection lifecycle with stable-state checks and rollback.
- Concrete bundled Paqet process adapter with bounded readiness and shutdown.
- Concrete ProxiFyre Windows service adapter with bounded state transitions.
- Live desktop status, connect, disconnect, and refresh controls.
- Version-pinned Paqet alpha.21 and ProxiFyre 2.6.0 payloads with SHA-256 checks.
- Automatic active-adapter, IPv4, gateway, and gateway-MAC discovery.
- Broker-managed profile persistence with DPAPI encryption and hardened ACLs.
- Route-everything and selected-app modes with full-path carrier exclusions.
- TCP, UDP, IPv4, IPv6, LAN-bypass, KCP mode, and TCP-flag controls.
- Overview, connection, routing, diagnostics, autostart, and connect-on-launch UI.
- WiX installer for the self-contained desktop app, broker, engine payloads,
  broker-supervised ProxiFyre process, and scoped firewall rules.
- Regression tests for profile validation, catch-all routing, loop exclusions,
  and deterministic Paqet/ProxiFyre configuration output.

## Runtime behavior

The broker verifies the bundled payload, validates and atomically writes both
engine configurations, starts Paqet, waits for SOCKS5 readiness, and starts
ProxiFyre only after the carrier is healthy. Disconnect reverses that order.
The health monitor restores the configured safe disconnected state if the
carrier fails: guarded when the routing kill switch is enabled, fully stopped
otherwise.

## Explicitly not claimed by 0.5

- Add an end-to-end SOCKS5 UDP ASSOCIATE readiness probe.
- Implement a machine-wide Windows Firewall kill switch for traffic ProxiFyre
  cannot attribute to a protected application.
- Add packet-capture leak tests and signed release/update infrastructure.
- Bundle packet drivers only after the required redistribution rights exist.

## Test seams

Automated tests will observe behavior through these public seams:

1. `RoutingPolicyCompiler`: policy in, immutable ProxiFyre route plan out.
2. `PayloadVerifier`: protected root plus manifest entry in, pass/fail out.
3. `IConnectionController`: connect, disconnect, and aggregate status.

Tests do not reach into private implementation details or couple themselves to
process-management internals.
