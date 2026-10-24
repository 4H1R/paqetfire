# Xray regional policy routing

PaqetFire bundles Xray-core 26.3.27 as a local destination-policy engine.
Xray is not a second VPN and does not replace Paqet.

## Data path

```text
selected Windows applications -> ProxiFyre -> Xray SOCKS5 127.0.0.1:1081
LAN devices                    ------------> Xray SOCKS5 <LAN IPv4>:1082       (optional, authenticated)
hotspot clients                ------------> Xray SOCKS5 <hotspot IPv4>:10808  (optional, authenticated)
     Xray -> direct / block outbound for matching rules
          -> Paqet SOCKS5 on 127.0.0.1:1080 for everything else
          -> with an exit proxy: TCP to Psiphon SOCKS5 on 127.0.0.1:1084, which
             reaches Psiphon through Paqet; UDP still to Paqet
```

See [Exit proxy: Psiphon](psiphon-exit.md).

All inbounds sniff HTTP, TLS, and QUIC hostnames and share the same routing
rules. ProxiFyre always excludes the full installed paths of Paqet, Xray,
ProxiFyre, and the broker, so Xray's direct and Paqet-bound sockets cannot
re-enter the application redirector and form a loop.

Both loopback SOCKS5 hops require the broker's engine credentials, so other
local processes cannot open TCP connections through the route. Xray's UDP relay
does not authenticate individual datagrams; that is an upstream limitation.

## DNS

New profiles answer DNS inside Xray instead of the Windows resolver. The
resolver is selectable: Cloudflare (default), Google, Quad9, or System, which
keeps the previous behavior.

- Port 53 traffic from the ProxiFyre, LAN, and hotspot inbounds goes to a
  `dns` outbound. A and AAAA queries are answered by Xray's DNS module. Other
  query types (SRV, TXT, MX, HTTPS) are forwarded as plain DNS to the
  provider's resolver through Paqet, so they are never visible locally.
- The DNS module queries the provider over DNS-over-HTTPS at IP-literal
  endpoints, so no bootstrap lookup is needed, through the Paqet outbound.
- `geosite:private`, `geosite:category-ir` with the Iran preset, and custom
  direct domains use the active adapter's IPv4 DNS servers directly. These
  destinations already connect directly, and local answers keep Iranian CDN
  edges and LAN names working. Without a detected adapter resolver, they use
  DNS-over-HTTPS as well.
- Resolver rules come first, so user direct-IP or regional rules can never
  divert a DoH or domestic query.
- The direct outbound resolves through the same module (`UseIP`), and the query
  strategy drops unrouted address families: IPv4-only routing returns no AAAA
  records, so applications are not steered onto an uncaptured IPv6 path.

### FakeDNS (on for new profiles)

With FakeDNS on, the DNS module answers lookups that are not pinned to the
domestic resolver with placeholders from `198.18.0.0/15`, and every inbound
adds the `fakedns` sniffer. Connections to a placeholder reach Paqet as the
original domain, so the real lookup happens on the Paqet server and one tunnel
round trip per new domain is skipped. Xray's own lookups for routing and the
direct outbound never receive placeholders.

- Only IPv4 placeholders are issued. ProxiFyre's LAN bypass exempts
  `fc00::/7`, which contains Xray's default IPv6 pool; AAAA lookups therefore
  fall through to DNS-over-HTTPS.
- The broker flushes the Windows DNS cache on connect, disconnect, kill-switch
  guard, profile activation, and broker startup. A placeholder is meaningful
  only to the Xray process that issued it, so transport recovery restarts Xray
  only when its own configuration changed (for example the network's resolvers
  or the LAN share address), and the Windows DNS cache is flushed when it does.
- Validation rejects FakeDNS with the System resolver, without IPv4 routing,
  and with application exclusions in all-applications mode, where excluded
  applications share the Windows cache but bypass capture.
- With the `IPIfNonMatch` domain strategy, a domain that matches no domain rule
  is still resolved over DNS-over-HTTPS once so the IP rules (`geoip:ir`,
  custom ranges) can be checked; FakeDNS removes the application's own lookup,
  not that one. Xray caches the answer.
- Profiles saved before FakeDNS became the default keep their stored value.

### DNS leak check

Verify route looks up `whoami.akamai.net`, whose answer is the address of the
resolver that asked, once through Xray addressed to the adapter's resolver
(as Windows would) and once directly from the broker. The same answer on both
paths means lookups leave through this network; a FakeDNS placeholder means
Xray answered locally.

Lookups outside the route are not covered: applications not selected in
selected-applications mode (Windows resolves for them in `svchost.exe`), and
queries to a LAN resolver while ProxiFyre's local network bypass is on.

## Routing rules

Rules are evaluated in this order; the first match wins:

1. **DNS** (unless the resolver is System): the DNS module's DoH and domestic
   queries, then port 53 from the user inbounds to the `dns` outbound.
2. **Custom direct destinations**: one domain, `*.domain`, IP address, or CIDR
   range per line. A bare domain also matches its subdomains.
3. **Block ads** (`geosite:category-ads-all`), on by default, then **Block
   malware, phishing and cryptominers** when updated routing data is installed.
4. **Direct BitTorrent**, on by default. The UI warns that it exposes the
   machine's normal public IP to peers.
5. **LAN bypass** (`geosite:private`, `geoip:private`).
6. **Iran — Direct** regional preset (`geosite:category-ir`, `geoip:ir`), the
   default; `None` disables it.
7. **Block QUIC** (UDP/443), on for new profiles. It follows every direct rule,
   so only QUIC headed into the tunnel is blocked and direct destinations keep
   HTTP/3. Measured through a Paqet route on 2026-10-19, HTTP/3 had slightly
   lower request latency than HTTP/2 (warm requests 113–117 ms against
   154–155 ms) but under half the download throughput (10–11 Mbit/s against
   17–23 Mbit/s); browsers fall back to TCP immediately when QUIC is blocked.
8. Everything else goes to Paqet. With an exit proxy, TCP goes to the `exit`
   outbound and UDP to Paqet.

`IPIfNonMatch` is the default domain strategy; `AsIs` and `IPOnDemand` are also
available. TCP/UDP and IPv4/IPv6 selection is enforced by ProxiFyre capture, not
by Xray rules.

## Routing data

Xray matches `geosite:` and `geoip:` rules against two data files. The installer
ships the files from the pinned Xray release. The broker can replace them with
the daily releases of the
[Iran-v2ray-rules](https://github.com/Chocolate4U/Iran-v2ray-rules) project, a
superset of v2fly's domain list with a larger `category-ir` (Iranian sites on any
top-level domain), Persian advertising and tracking domains in
`category-ads-all`, and `malware`, `phishing`, and `cryptominers` categories.

- **Settings → Routing data** shows the active source and version, checks once a
  day when **Update automatically** is on (the default), and offers **Update
  now** and **Use bundled data**.
- An update reads the latest release tag from the GitHub API, downloads both
  files and their `.sha256sum` files, rejects files over 32 MB, and verifies the
  digests. A release that fails is not downloaded again for an hour, manual
  updates run at most once every five minutes, and one attempt is capped at 15
  minutes. The pinned `xray.exe` must then accept a test configuration that uses
  every category PaqetFire can emit, with `XRAY_LOCATION_ASSET` pointing at the
  new files. Only then does the release become active.
- While the route is connected, the download goes only through Paqet, so it
  works where GitHub is filtered and never leaves the route. While the kill
  switch is holding traffic, nothing is downloaded. When disconnected, it goes
  direct.
- Each release is kept in its own directory under
  `%ProgramData%\PaqetFire\routing-data`, restricted to SYSTEM and
  Administrators; the state file is the commit point, and the previous release
  stays until the next update. Before every Xray start the broker re-verifies the
  active files and holds them open against writes until Xray is ready. Any
  mismatch falls back to the bundled files and is reported; if updated data
  cannot be verified or loaded at launch, the broker rewrites the configuration
  for the bundled files and retries once. Transient file errors use the bundled
  files for that launch only.
- New data takes effect at the next Xray start. A running Xray is never
  restarted for it.
- The files are downloaded on the user's machine and are not redistributed in
  the installer; see the project for its sources and licenses.

**Block malware, phishing and cryptominers** (Routing page, on for new profiles)
blocks `geosite:malware`, `geosite:phishing`, `geosite:cryptominers`,
`geoip:malware`, and `geoip:phishing`. Those categories exist only in updated
routing data, so the rule is written only when the data Xray will load provides
them; with bundled data the page shows a hint instead.

## Lifecycle and integrity

The broker validates the generated JSON with `xray run -test` before launch,
and skips that extra process when the configuration is byte-for-byte the one
that last passed. Paqet and Xray start at the same time; Xray only dials Paqet
when traffic arrives. ProxiFyre starts once both are ready. Disconnect and
rollback stop ProxiFyre, then Xray and Paqet. Xray's executable and GeoIP/GeoSite databases are pinned in the
payload manifest and verified before a connection may start.

The generated `config.json` is broker-owned and written atomically under the
protected installation payload directory. It is not accepted from the UI.

## Live session statistics

Generated configurations enable Xray's `StatsService` on `127.0.0.1:10085` (loopback only; `HandlerService` is not exposed) with outbound uplink/downlink counters. The broker reads the `outbound>>>paqet>>>traffic>>>*` and `outbound>>>exit>>>traffic>>>*` counters over gRPC for the Overview session card, so totals and rates include only traffic routed through Paqet or the exit that rides on it, never direct or blocked traffic. Latency is probed directly against the Paqet SOCKS5 listener, bypassing Xray so probe bytes are not counted; the exit address is probed the same way, or through Psiphon's listener when the profile uses the Psiphon exit. The exit country is resolved offline from the bundled `geoip.dat`; no address is sent to a geolocation service.
