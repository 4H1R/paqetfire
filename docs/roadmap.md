# PaqetFire roadmap

Open work only; shipped changes are recorded in [release notes](releases/).

## Connection safety

- Gate ProxiFyre startup on a SOCKS5 UDP-associate probe, not only the TCP
  greeting.
- Machine-wide Windows Firewall kill-switch mode for traffic ProxiFyre cannot
  attribute to a routed application.

## Exit proxy

- Windows Firewall rule that blocks Psiphon from everything but loopback, as
  defence in depth for its upstream-proxy configuration.
- Dedicated Psiphon network identifiers from Psiphon Inc. instead of the public
  community parameters.
- Further exit providers (for example a user's own SOCKS5 or WARP endpoint).

## Reliability

- Check Paqet client/server version compatibility.
- One-click rollback to the last known-good engine configuration (the `.bak`
  copies already exist).
- Journal in-flight transitions for interrupted upgrades. The intent to stay
  connected already survives broker restarts.

## Leak validation

- Capture-based TCP/UDP/DNS/IPv4/IPv6 leak tests through ProxiFyre capture, not
  only the explicit Xray route used by the verification report.
- Fault injection: engine crash, driver stop, port collision, corrupt config,
  interrupted update, network loss, sleep/resume, and forced shutdown.
- Clean-machine installer tests on Windows 10/11.

## User experience

- Test the Paqet handshake against the server from the setup checklist, not
  only ICMP reachability.
- Localization and explicit light/dark theme support.
- Optional routing-decision diagnostics using Xray per-rule webhooks, with a
  broker-owned authenticated receiver and destination redaction.

## Diagnostics

- Redact engine logs at capture time, not only in the Copy diagnostics report,
  and add retention controls.
- CPU, memory, and packet-loss metrics alongside the existing throughput and
  latency.

## Installer and supply chain

- Sign executables, the broker service, the MSI, and update metadata; verify
  signatures before applying updates.
- Single setup executable (WiX Burn) that chains driver prerequisites, subject to
  Npcap OEM and Windows Packet Filter redistribution rights.
- ARM64 builds.
