# Architecture

## Trust boundary

```text
PaqetFire.Desktop (asInvoker)
        |
        | authenticated, ACL-restricted named pipe
        v
PaqetFire.Broker (LocalSystem service)
        |-- Paqet adapter ------> bundled paqet.exe + generated config.yaml
        |-- Xray adapter -------> bundled xray.exe + generated config.json
        `-- ProxiFyre adapter --> bundled ProxiFyre engine + app-config.json
```

The desktop process owns presentation and user preferences. It cannot write
protected engine configuration or manipulate services directly. The broker
accepts a small, versioned set of typed commands and rejects arbitrary paths,
arguments, and unvalidated configuration.

Each profile can select a Windows network interface by GUID, or leave it unset
for automatic selection (Ethernet before Wi-Fi). The desktop preview and broker
use the same adapter discovery rules. Gateway MAC lookup is scoped to the
adapter's local IPv4 address. Preview failures clear earlier network details;
an unavailable selected interface or unresolved MAC prevents the broker from
applying the connection instead of silently choosing another interface. Use
**Detect adapter details** to refresh available interfaces, then save the profile
to apply a selection. Existing profiles default to automatic selection.

## Product payload

The installed layout is owned by PaqetFire and treated as one product:

```text
%ProgramFiles%\PaqetFire\
  app\
    PaqetFire.Desktop.exe
  broker\
    PaqetFire.Broker.exe
    payload\
      payload-manifest.json
      engines\
        paqet\<architecture>\...
        xray\<architecture>\...
        proxifyre\<architecture>\...
```

Each manifest file entry includes a relative path, size, and SHA-256 digest.
The installer verifies release artifacts before packaging. The broker resolves
only manifest-declared paths beneath the payload root and verifies files before
launching or registering them.

The two upstream engines remain distinct processes because their runtime and
driver models are different. They are nevertheless internal implementation
components: users install, configure, update, repair, and uninstall PaqetFire
only.

## Setup chain

The production setup executable will bootstrap, in order:

1. architecture and supported-Windows checks;
2. licensed packet-driver prerequisites;
3. required Microsoft Visual C++ runtime;
4. the PaqetFire MSI containing the self-contained UI, broker, and engines;
5. broker service registration and obsolete ProxiFyre-service cleanup;
6. firewall rules scoped to the exact installed engine paths.

Setup must be transactional and preserve user profiles during upgrades. Engine
services do not start until PaqetFire has a complete, valid connection profile.

## Planned lifecycle

1. Desktop connects to the broker and requests a status snapshot.
2. Broker verifies its bundled engines and discovers driver prerequisites.
3. User edits a profile in the desktop process.
4. Broker validates the complete profile again at the trust boundary.
5. Broker writes a temporary configuration, flushes it, and atomically replaces
   the live configuration while retaining one backup.
6. Broker starts or restarts only the selected engine.
7. Broker streams structured state and bounded log events back to the desktop.

## Engine loop prevention

PaqetFire supports a Route everything mode by emitting ProxiFyre's explicit
catch-all application entry (`""`) with TCP, UDP, IPv4, and IPv6 enabled.
LAN bypass defaults to off in this mode so LAN traffic is not silently exempted.

The generated exclusion list always contains the exact bundled Paqet and
ProxiFyre executable names. These entries are locked system exclusions: users
may add exclusions but cannot remove or override them. Paqet's upstream packets
must never be captured and sent back through Paqet's own SOCKS endpoint.

Npcap and Windows Packet Filter are kernel drivers, not carrier applications,
so they do not normally create user-mode traffic that needs a process-name
exclusion. The broker derives exclusions from the signed payload manifest rather
than assuming that an executable named `npcap.exe` exists.

Before applying a catch-all rule, the broker must verify that Paqet's SOCKS5
listener is accepting both TCP and UDP. Unsupported protocols or address
families fail closed rather than bypassing the proxy.
