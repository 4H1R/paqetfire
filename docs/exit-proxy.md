# Exit proxy

A profile can add an **exit proxy**: a second network that routed traffic leaves
through after the Paqet server. Destinations then see that network's address and
country instead of the Paqet server's. Two exits are supported, **Psiphon** and
**OpenVPN**; the default, **None**, keeps the earlier behavior, and profiles
saved before this option existed keep it.

Often the simpler fix for a flagged server address is on the Paqet server
itself, for example a WARP or second outbound there, which changes the exit for
every device and for UDP too. The client-side exits are for users who do not
control their server, or who want a country or provider of their own.

## Getting the exit engines

Neither exit engine is part of the installer, which keeps it small and ships
no extra kernel drivers to people who never use an exit.

- **Psiphon** is downloaded by the broker when the user presses **Download
  Psiphon** under **Routing → Exit proxy**. The download goes through Paqet
  while connected and directly otherwise, never directly while the kill switch
  holds traffic. The file comes from a URL pinned to a full commit in
  Psiphon-Labs/psiphon-tunnel-core-binaries and is accepted only with its exact
  size and pinned SHA-256 (`ExitEngineOptions.Psiphon`). If the download is
  blocked, the user can fetch the same file another way and pick it with **Use a
  downloaded file**; it must match the same digest. Only local drives are
  accepted, and the broker opens the file while impersonating the desktop's
  user, so a link on the drive cannot make it reach a network share as SYSTEM. The file is
  stored in `%ProgramData%\PaqetFire\engines`, a broker-only directory.
- **OpenVPN** is the user's own installation of OpenVPN Community 2.6 or newer
  from openvpn.net. Its signed installer brings `openvpn.exe`, the TAP-Windows6
  driver, and `tapctl.exe`; PaqetFire installs no driver itself. Because the
  broker runs OpenVPN as SYSTEM, it accepts an installation only when the
  executable, `tapctl.exe`, and their folder can be modified by nobody but
  SYSTEM, Administrators, or TrustedInstaller, and no folder above them can be
  renamed, deleted, or re-permissioned by anyone else, which holds for the
  default `Program Files` location. The installation is looked up again before
  every start.

## Data path

```text
selected applications -> ProxiFyre -> Xray SOCKS5 127.0.0.1:1081
LAN / hotspot devices ------------> Xray SOCKS5 shares
     Xray -> direct / block outbound for matching rules

     Psiphon:
          -> TCP: exit outbound -> Psiphon SOCKS5 127.0.0.1:1084
                     Psiphon -> Paqet SOCKS5 127.0.0.1:1080 (engine credentials)
                             -> Paqet server -> Psiphon server (chosen country) -> destination
          -> UDP: Paqet SOCKS5 127.0.0.1:1080 -> Paqet server -> destination

     OpenVPN:
          -> TCP and UDP: exit outbound, sockets bound to the "PaqetFire Exit" adapter
                     OpenVPN -> Paqet SOCKS5 127.0.0.1:1080 (engine credentials, TCP)
                             -> Paqet server -> VPN server -> destination
```

- Every Xray rule still applies first: DNS, custom direct destinations, blocking,
  BitTorrent, LAN bypass, the regional preset, and QUIC blocking. Only traffic
  that would have gone to Paqet is affected.
- Both exits reach their own network only through Paqet's authenticated loopback
  SOCKS5 listener, so nothing routed to an exit leaves the computer directly.
- DNS is unchanged: Xray's DNS-over-HTTPS queries still go through Paqet.

### Psiphon

- The console client is configured with `UpstreamProxyURL` pointing at Paqet's
  loopback SOCKS5 listener, which upstream documents as the proxy for all of
  Psiphon's outbound connections, including its server list downloads.
- Psiphon's local SOCKS5 proxy relays TCP only, so UDP (voice and video calls,
  games, and QUIC when it is not blocked) keeps leaving from the Paqet server.
- With FakeDNS, Xray hands Psiphon the original domain name, so it is resolved
  on the Psiphon server, which suits CDNs near the exit country.

### OpenVPN

OpenVPN works at the IP layer, so on its own it would take over the computer's
default route. PaqetFire runs it so that it changes nothing outside its own
adapter:

- OpenVPN uses a dedicated TAP adapter named **PaqetFire Exit**, created with
  `tapctl` the first time it is needed (`--hwid root\tap0901`). OpenVPN's data
  channel offload driver cannot be used: `--socks-proxy` turns it off, and
  OpenVPN then uses TAP-Windows6.
- It reaches the VPN server through `socks-proxy 127.0.0.1 1080` with Paqet's
  engine credentials, read from a file. SOCKS5 carries TCP only, so the server
  must accept TCP; a UDP configuration is refused at import.
- `route-nopull` drops pushed routes, `redirect-*`, DHCP, and DNS options;
  `pull-filter` repeats that and also drops IPv6 settings and
  `block-outside-dns`. The one route OpenVPN adds is a default route on its own
  adapter with metric 9999, the highest Windows accepts, so ordinary traffic
  always prefers the computer's own route.
- The server still chooses the adapter's address, and Windows adds an on-link
  route for its network. Once OpenVPN reports a connection, the broker accepts
  only a private network of /12 or narrower that overlaps no other network of
  the computer; otherwise the start fails, or a running exit is ended and
  recovered like a crash.
- Xray's exit outbound is a `freedom` outbound whose sockets are bound to the
  adapter (`sockopt.interface`, IP_UNICAST_IF on Windows). Names resolve through
  Xray's DNS to IPv4 (`ForceIPv4`), because the tunnel carries IPv4 only. While
  the tunnel is down, those sockets fail instead of following another route:
  routed traffic fails closed rather than leave from the Paqet server.
- TCP and UDP both leave through the VPN server.

#### What PaqetFire keeps from an .ovpn

OpenVPN runs as SYSTEM, and an .ovpn can name programs to run and files to read
or write. `OpenVpnConfiguration` therefore keeps only an allowlist of client
options (servers, ciphers, TLS verification, MTU, keepalive, compression, and
similar) and drops everything else, including `up`, `down`, `route-up`,
`plugin`, `tls-verify`, `auth-user-pass-verify`, `management`, `log`, `status`,
`setenv`, routes, and DNS options. The import shows which options were removed.
PaqetFire then adds its own `script-security 1`, `auth-retry none`, adapter,
proxy, and route lines. Certificates and keys must be embedded (`<ca>`, `<cert>`,
`<key>`, `<tls-auth>`, `<tls-crypt>`, `<tls-crypt-v2>`); an option that points to
a file is refused, as are `<connection>` blocks and bridged (`dev tap`) servers.

#### Account and storage

- A configuration with `auth-user-pass` needs the account's OpenVPN username
  and password. For Proton VPN these are the **OpenVPN / IKEv2** credentials
  from the account page, not the Proton login, and the configuration is the
  Windows download with the TCP protocol.
- The imported .ovpn may embed a private key, so the profile catalog encrypts it
  with DPAPI like the transport key, and the password too. The desktop is never
  sent either in snapshots; it only learns whether they are saved and the first
  server name. A save that leaves them empty keeps the stored ones, but the
  stored password only for the same configuration and username, so importing
  a configuration for another server needs the password again.
- Portable exports carry the .ovpn and the username; the password stays on each
  PC and is entered again after an import.
- At connect, the generated configuration and its two credential files (Paqet's
  loopback login and the VPN account) are written to
  `payload\engines\openvpn\`, restricted to SYSTEM and Administrators. Both
  credential files are deleted when the route stops using the OpenVPN exit.

## Exit country (Psiphon)

**Routing → Exit proxy** offers the provider and, for Psiphon, the exit country:
**Best available** (Psiphon picks the best performing server in any country) or
one ISO 3166-1 country code, written to Psiphon's `EgressRegion`. The list
starts with the countries Psiphon has served; while connected, the countries
Psiphon reports as available right now (its `AvailableEgressRegions` notice)
are added, and the card says which country the current server is in. A country
Psiphon cannot serve at the moment fails to connect rather than falling back
to another country. With OpenVPN, the country is whichever server the imported
configuration names.

## Lifecycle

- Connect starts Paqet and Xray together, then the exit once Paqet is ready,
  then ProxiFyre once the exit reports a tunnel: an established Psiphon tunnel,
  or OpenVPN's `Initialization Sequence Completed`. Startup waits up to 60
  seconds for Psiphon, enough for a first run that downloads its server list
  through Paqet, and 45 seconds for OpenVPN. If the exit does not connect,
  Connect fails and rolls back; traffic never silently leaves through the Paqet
  server instead. A selected exit that is not installed fails Connect with
  directions to get it.
- Disconnect and the kill-switch guard stop the exit after ProxiFyre and before
  Xray and Paqet. Restarting the transport restarts the exit too, because its
  tunnel runs over Paqet. OpenVPN is asked to close its tunnel through its
  `--service` exit event and is killed only if it does not exit in time.
- If the exit process ends on its own while connected, the broker treats it like
  a Paqet failure and restarts the transport in place; ProxiFyre keeps
  capturing, so routed traffic fails closed meanwhile. If the exit loses its
  tunnel without exiting, it reconnects by itself and the card says so.
- Both exits run in the broker's kill-on-close job. Psiphon is hashed through a
  locked handle before every start. Each exit's executable is a locked ProxiFyre
  exclusion while a profile uses it. Leftover Psiphon processes are stopped when
  the broker starts; OpenVPN is not matched by path, because that is the user's
  own installation, which may run their other connections.
- Psiphon's data store lives in `%ProgramData%\PaqetFire\psiphon`, restricted to
  SYSTEM and Administrators.

## Statistics and diagnostics

- The Overview session counts traffic through both the Paqet and the exit
  outbounds, and probes the public address through the exit: through Psiphon's
  listener, or for OpenVPN with a socket bound to the exit adapter that asks
  `https://1.1.1.1/cdn-cgi/trace`, so the probe needs no DNS lookup.
- Diagnostics shows the exit engine state and whether each exit engine is
  installed. The report lists the exit, Psiphon's country and listener or the
  OpenVPN server count, account state, ignored options, and adapter state, but
  never the VPN server's name, which is redacted like the Paqet server. Engine
  notices appear in the activity log, with proxy credentials removed.

## Limitations

- **Unauthenticated local listener (Psiphon).** Psiphon's local SOCKS5 proxy
  cannot require credentials, so another process on this PC could use it while
  the exit runs. That reaches only the Psiphon network through the route; the
  Paqet hop behind it still requires the engine credentials.
- **Public network configuration (Psiphon).** PaqetFire uses the public Psiphon
  network parameters shared by community clients (propagation channel and
  sponsor `FFFFFFFFFFFFFFFF`). Psiphon Inc. issues dedicated identifiers on
  request at developer-support@psiphon.ca and may change or restrict the public
  ones.
- **Windows build (Psiphon).** Psiphon publishes only a 32-bit Windows console
  client; it runs under WoW64 on x64 Windows.
- **TCP only to the VPN server (OpenVPN).** OpenVPN over TCP inside Paqet's
  KCP carrier stacks two retransmission loops, so it is slower than a direct
  VPN, most of all on lossy networks.
- **IPv4 only (OpenVPN).** The exit adapter carries IPv4; IPv6 destinations
  routed to the exit fail, and applications fall back to IPv4.
- **No firewall confinement yet.** Both exits are configured to dial only
  through Paqet, but PaqetFire does not yet add Windows Firewall rules that
  block them from anything but loopback.
