# Exit proxy: Psiphon

A profile can add an **exit proxy**: a second network that routed traffic leaves
through after the Paqet server. Destinations then see that network's address and
country instead of the Paqet server's. Psiphon is the first supported exit; the
default, **None**, keeps the earlier behavior, and profiles saved before this
option existed keep it.

## Data path

```text
selected applications -> ProxiFyre -> Xray SOCKS5 127.0.0.1:1081
LAN / hotspot devices ------------> Xray SOCKS5 shares
     Xray -> direct / block outbound for matching rules
          -> TCP: exit outbound -> Psiphon SOCKS5 127.0.0.1:1084
                     Psiphon -> Paqet SOCKS5 127.0.0.1:1080 (engine credentials)
                             -> Paqet server -> Psiphon server (chosen country) -> destination
          -> UDP: Paqet SOCKS5 127.0.0.1:1080 -> Paqet server -> destination
```

- Every Xray rule still applies first: DNS, custom direct destinations, blocking,
  BitTorrent, LAN bypass, the regional preset, and QUIC blocking. Only traffic
  that would have gone to Paqet is affected.
- Psiphon's console client is configured with `UpstreamProxyURL` pointing at
  Paqet's loopback SOCKS5 listener, which upstream documents as the proxy for
  all of Psiphon's outbound connections, including its server list downloads.
  Psiphon automatically limits itself to tunnel protocols that work through an
  upstream proxy.
- Psiphon's local SOCKS5 proxy relays TCP only, so UDP (voice and video calls,
  games, and QUIC when it is not blocked) keeps leaving from the Paqet server.
- DNS is unchanged: Xray's DNS-over-HTTPS queries still go through Paqet. With
  FakeDNS, Xray hands Psiphon the original domain name, so it is resolved on the
  Psiphon server, which suits CDNs near the exit country.

## Exit country

**Routing → Exit proxy** offers the provider and, for Psiphon, the exit country:
**Best available** (Psiphon picks the best performing server in any country) or
one ISO 3166-1 country code, written to Psiphon's `EgressRegion`. The list
starts with the countries Psiphon has served; while connected, the countries
Psiphon reports as available right now (its `AvailableEgressRegions` notice)
are added, and the card says which country the current server is in. A country
Psiphon cannot serve at the moment fails to connect rather than falling back
to another country.

## Lifecycle

- Connect starts Paqet and Xray together, then Psiphon once Paqet is ready, then
  ProxiFyre once Psiphon reports an established tunnel. Startup waits up to 60
  seconds, enough for a first run that downloads Psiphon's server list through
  Paqet. If Psiphon does not connect, Connect fails and rolls back; traffic
  never silently leaves through the Paqet server instead.
- Disconnect and the kill-switch guard stop Psiphon after ProxiFyre and before
  Xray and Paqet. Restarting the transport restarts Psiphon too, because its
  tunnel runs over Paqet.
- If Psiphon exits on its own while connected, the broker treats it like a
  Paqet failure and restarts the transport in place; ProxiFyre keeps capturing,
  so routed traffic fails closed meanwhile. If Psiphon loses its tunnel without
  exiting, it reconnects by itself and the Diagnostics card says so.
- The executable is pinned by SHA-256 in the payload manifest, hashed through a
  locked handle before every start, and runs in the broker's kill-on-close job.
  Its full path is a locked ProxiFyre exclusion, and leftover processes are
  stopped when the broker starts.
- The generated `config.json` sits beside the executable in the protected
  payload directory and contains Paqet's engine credentials. Psiphon's data
  store lives in `%ProgramData%\PaqetFire\psiphon`, restricted to SYSTEM and
  Administrators.

## Statistics and diagnostics

- The Overview session counts traffic through both the Paqet and the exit
  outbounds, and probes the public address through Psiphon, so it shows the
  exit address and country.
- Diagnostics shows the Psiphon engine state; the diagnostic report lists the
  exit, its country, and the Psiphon listener; Psiphon's notices appear in the
  activity log with proxy credentials removed. The client's own region notice
  is not logged.

## Limitations

- **Unauthenticated local listener.** Psiphon's local SOCKS5 proxy cannot
  require credentials, so another process on this PC could use it while the
  exit runs. That reaches only the Psiphon network through the route; the
  Paqet hop behind it still requires the engine credentials.
- **Public network configuration.** PaqetFire uses the public Psiphon network
  parameters shared by community clients (propagation channel and sponsor
  `FFFFFFFFFFFFFFFF`). Psiphon Inc. issues dedicated identifiers on request at
  developer-support@psiphon.ca and may change or restrict the public ones.
- **Windows build.** Psiphon publishes only a 32-bit Windows console client; it
  runs under WoW64 on x64 Windows.
- **No firewall confinement yet.** Upstream routes every Psiphon connection
  through the configured upstream proxy, but PaqetFire does not yet add a
  Windows Firewall rule that blocks Psiphon from anything but loopback.
