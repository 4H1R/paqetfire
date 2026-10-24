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
        |-- Psiphon adapter ----> bundled psiphon-tunnel-core + config.json (exit proxy, optional)
        `-- ProxiFyre adapter --> bundled ProxiFyre engine + app-config.json
```

The desktop process owns presentation and user preferences. It cannot write
protected engine configuration or manipulate services directly. The broker
accepts a small, versioned set of typed commands and rejects arbitrary paths,
arguments, and unvalidated configuration.

Each profile can select a Windows network interface by GUID, or leave it unset
for automatic selection: the interface Windows would use to reach the server,
falling back to Ethernet before Wi-Fi. The desktop preview and broker
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
        paqet\x64\      paqet_windows_amd64.exe, config.yaml
        xray\x64\       xray.exe, geoip.dat, geosite.dat, config.json
        psiphon\x64\    psiphon-tunnel-core-i686.exe, config.json
        proxifyre\x64\  ProxiFyre.exe and dependencies, app-config.json
%ProgramData%\PaqetFire\
  settings.json, profiles.json, engine-credentials.json, broker-state.json
  routing-data\        downloaded GeoIP/GeoSite releases (optional)
  psiphon\             Psiphon server list and data store (exit proxy only)
```

Each manifest file entry includes a relative path, size, and SHA-256 digest. The
manifest is not signed; the hashes pin the upstream artifacts. Release CI
verifies the downloaded archives before packaging. The broker hashes every
manifest-declared file at startup and re-hashes a file on connect only when its
volume, file ID, size, or last-write time changed. Each executable is hashed
through a handle that blocks writes and deletes, held until the process has
started. Generated engine configurations are written
atomically beside each engine with a `.bak` copy, restricted to SYSTEM and
Administrators, and regenerated from the saved profile on every connect.

The upstream engines remain distinct processes because their runtime and
driver models are different. Paqet and Xray run in a kill-on-close job object
and the service restarts automatically after a failure. Leftover Paqet and Xray
processes are stopped when the broker starts. Psiphon, the optional
[exit proxy](psiphon-exit.md), runs only for profiles that select it, in the
same job, and is stopped at broker start as well. A leftover ProxiFyre is kept while
the kill switch is on or a reconnect is pending, so routed applications stay
blocked during a broker restart; it is replaced when the broker starts its own,
or stopped if the route is released without the kill switch. They are nevertheless internal implementation
components: users install, configure, update, repair, and uninstall PaqetFire
only.

## Installation and prerequisites

Setup is a single x64 MSI. It installs the desktop app, the broker service, and
the engine payload, removes any obsolete standalone `ProxiFyreService`, and
creates program-scoped firewall rules for `ProxiFyre.exe` (TCP/UDP) and
`xray.exe` (LAN SOCKS5 TCP/UDP, local subnet only). Before every connect, the
broker restores any of those four rules that are missing, disabled, or point at
another path.

Firewall rule repair reads the rule list once, runs in the background at broker
startup and on connect, reuses a clean result for five minutes, and is awaited
only just before ProxiFyre starts.

`%ProgramData%` lets standard users create folders, so `%ProgramData%\PaqetFire`
is secured before any store reads from it: the installer and the broker at every
start set its owner to SYSTEM and a protected DACL (SYSTEM and Administrators
full control, Users read). The broker then removes links, hard-linked files, and
entries with an untrusted owner, and every store re-validates the directory
through a handle opened without following reparse points before each load or
save.

Drivers and runtimes are not chained by the MSI. The broker reports Npcap,
Windows Packet Filter (`NDISRD`), the VC++ runtime, and .NET Framework 4.7.2, and
blocks connecting while one is missing. The desktop app can download, hash-verify,
and launch the official Windows Packet Filter and VC++ installers; Npcap must be
installed from its official site because its free license forbids redistribution.

## Engine loop prevention

The default **All supported applications** routing mode emits ProxiFyre's
catch-all application entry (`""`). The generated exclusion list always contains
the full installed paths of Paqet, Xray, Psiphon, ProxiFyre, and `PaqetFire.Broker.exe`.
These are locked system exclusions: users may add exclusions but cannot remove or
override them. Full paths are used because ProxiFyre treats a name-only exclusion
as a substring match. Paqet's and Xray's upstream sockets must never be captured
and sent back through the local SOCKS chain. Both loopback SOCKS5 hops require
broker-generated engine credentials, so other local processes cannot use the
route over TCP.

Npcap and Windows Packet Filter are kernel drivers, not carrier applications,
so they do not create user-mode traffic that needs a process exclusion.

Paqet and Xray start in parallel. With an exit proxy, Psiphon starts once Paqet
is ready and counts as ready when it reports an established tunnel; capture
waits for it. Before ProxiFyre starts, the broker waits for
a SOCKS5 greeting from Paqet and for Xray's local listener, both detected from
the Windows TCP listener table. ProxiFyre counts as ready shortly after its log
reports the SOCKS5 local router started, with a fixed two-second wait as the
fallback. UDP-associate is not part
of startup readiness; the on-demand connection verification report checks it
separately.
