# Engine release assessment — 2026-09-24

## Correction after installed validation

The original checks below missed the broker's separate hardcoded ProxiFyre
startup pin. v0.6.21 passed manifest/configuration validation but could not
connect because that pin still expected the 2.6.0 executable. v0.6.22 corrects
the pin and adds tests through the production startup check, plus manifest
consistency tests for all production engine options. See the
[hotfix notes](releases/v0.6.22.md). The earlier test results alone did not prove
that the broker could start the updated engine chain.

## Versions and compatibility

| Engine | Previous pin | Latest stable | Action |
| --- | --- | --- | --- |
| Xray-core | 26.3.27 | 26.3.27 | Keep the existing executable and GeoIP/GeoSite pins. |
| ProxiFyre | 2.6.0 | 2.6.1 | Update the x64 archive and three changed runtime-file hashes. |

Both official GitHub `releases/latest` API responses report `prerelease: false`.
Xray's release was published March 27; ProxiFyre's was published September 10.
The archive digests match GitHub release metadata, and ProxiFyre's ZIP also
matches its published `.sha256` sidecar. Paqet is unchanged.

[ProxiFyre 2.6.1](https://github.com/wiresock/proxifyre/releases/tag/v2.6.1)
reduces checksum CPU work and validates packet lengths more carefully, including
avoiding an out-of-bounds padding write for odd-length payloads. It also adds
low-level driver-library capabilities and fixes a legacy handle leak. Upstream
has not measured end-to-end throughput gains, so this update makes no speed claim.

The [tag comparison](https://github.com/wiresock/proxifyre/compare/v2.6.0...v2.6.1)
does not change the managed configuration contract. The runtime file set, CLI
integration, .NET Framework 4.7.2 floor, VC++ runtime floor, and Windows Packet
Filter requirement remain compatible with our integration. There is no profile
migration. The native checksum fixes take effect automatically in `socksify.dll`.

## Features that could improve PaqetFire

- **Adopt now:** ProxiFyre's checksum and packet-length fixes benefit our existing
  application-capture path without configuration changes. Its new library APIs
  do not expose a new JSON workflow; PaqetFire supervises a process and does not
  consume those APIs directly.
- **Best follow-up: explain routing decisions.** Xray 26.3.27 adds
  [per-rule webhooks](https://github.com/XTLS/Xray-core/pull/5722). A future local,
  opt-in diagnostics receiver could show which block or direct rule matched a
  connection. This requires a broker-owned receiver, authentication, bounded
  event storage, and destination redaction. No receiver or webhook is enabled
  by this update.
- **Separate backend exploration:** the
  [Xray release](https://github.com/XTLS/Xray-core/releases/tag/v26.3.27) includes
  Hysteria 2 inbound/transport support, XHTTP/3 improvements, expanded Finalmask,
  and WireGuard fixes. These apply to Xray transports; our external transport is
  Paqet, reached through local SOCKS5. Turning them on is not a drop-in Paqet
  performance improvement and would need compatible servers and profile changes.
- **Future TUN work:** Xray's
  [deterministic Windows adapter GUID](https://github.com/XTLS/Xray-core/pull/5811)
  may help stable adapter identity in a separate TUN backend. It does not provide
  hotspot forwarding or replace our per-application capture/kill-switch contract.
- **Keep privileged capture:** ProxiFyre's existing
  [`--allow-not-admin` mode](https://github.com/wiresock/proxifyre/blob/v2.6.1/docs/configuration.md#optional-unelevated-console-mode)
  allows unresolved process traffic to go direct. It is unsuitable for our
  catch-all enforcement expectations. Continue using the LocalSystem broker.

## Verification

- Fresh official downloads passed archive and installed-file hash checks.
- Full regression suite: 173 passed, three existing administrator-only lifecycle
  tests skipped under the current token.
- Ten new release-CI compatibility cases use the real bundled engines: six Xray
  policies cover all domain strategies with features enabled/disabled, bundled
  geographic data, direct routes, blocking, and authenticated LAN/hotspot inputs;
  four ProxiFyre cases cover catch-all/selected applications, protocol/address
  families, loop exclusions, and authenticated TLS configuration. The latter run
  the shipped managed parser under .NET Framework and check retained field values.
- Local Xray SOCKS authentication smoke test accepts the configured password and
  rejects an incorrect password. Installer configuration checks pass.
- Release solution build, self-contained x64 desktop/broker publishes, and WiX
  MSI build pass. Both builds report zero warnings and errors. Published broker
  payload hashes were rechecked against the updated manifest.

These checks do not prove live ProxiFyre driver capture, routed UDP, kill-switch
leak prevention, or connectivity through a real Paqet server. Those still need
an elevated end-to-end run with the installed drivers and a configured server.
No installed service or driver is changed by these repository checks.
