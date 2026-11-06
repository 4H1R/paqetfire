# Paqet integration

## Pinned runtime

PaqetFire pins the Windows x64 Paqet payload to `v1.0.0-alpha.21-a2` of the
[4H1R/paqet](https://github.com/4H1R/paqet) fork
(`3f9014f0986384357256efe9da093006162eac54`), which is upstream
`hanselime/paqet` `v1.0.0-alpha.21` plus transport performance work, client
pool and configuration-loading fixes, and a leaner packet path: a hand-written
packet encoder and decoder, payloads handed to KCP straight from the capture
buffer, one goroutine per relayed connection, idle sockets releasing their copy
buffers, allocation-free SOCKS5 UDP, and freed memory returned to the OS when
traffic goes quiet. The release binary is 9,529,344 bytes with SHA-256:

`8939E1D8AC79E67075D6E6A4DED0FF8BB69141A9C1C3EF5AA955CEC37CC3DFB5`

Fork tags follow `v<upstream-version>-a<N>`. The wire protocol is unchanged, so
the fork interoperates with upstream servers and clients in every combination;
the server still needs the same key, KCP mode, MTU, and FEC shards.

The release artifact must be downloaded from the fork's GitHub release, not
copied from an existing machine. Release automation independently verifies the
published archive digest and the extracted executable's size and digest in
`payload/payload-manifest.json`.

Runtime invocation is:

```text
paqet_windows_amd64.exe run -c <broker>\payload\engines\paqet\x64\config.yaml
```

The broker owns that child process, captures bounded stdout/stderr logs, hashes
the executable through a handle that blocks writes and deletes until the process
has started, refuses a SOCKS endpoint that is already occupied, and terminates
the whole process tree on disconnect. Readiness polls the Windows TCP listener
table every 50 ms, then requires a real SOCKS5 greeting on `127.0.0.1:1080`
within 500 ms per attempt. Paqet and Xray start at the same time; ProxiFyre
starts only after both are ready. Paqet runs in a kill-on-close
job object, so it also ends if the broker crashes. Startup does not probe
UDP-associate; the on-demand connection verification report does.

## Configuration support

`PaqetProfile`, `PaqetConfigurationValidator`, and `PaqetYamlConfigurationWriter`
cover the client options of Paqet v1.0.0-alpha.21-a2:

- SOCKS5 listen endpoint and optional username/password authentication;
- TCP/UDP port forwards;
- IPv4 and optional IPv6 capture details, TCP flag cycling, and PCAP buffer;
- connection count and IPv4 source port;
- KCP presets and manual controls, MTU/windows, write/ACK delay, all upstream
  encryption choices, SMUX buffers/keepalives, and optional FEC shards.

The Connection page exposes server, key, interface, KCP preset and flags.
Advanced transport settings additionally expose logging, IPv4 source port,
optional IPv6 capture details, PCAP buffering, connection count, and all KCP
tuning fields through `PaqetAdvancedOptions`. These settings survive save,
duplicate, switch, protected storage, and portable exports. Null numeric/boolean
values are deliberately omitted so the bundled engine supplies its defaults,
except the KCP windows: blank receive/send windows are written as 1024/512
packets. Paqet's client defaults of 512/128 cap a 150 ms route at roughly
37/9 Mbit/s, while its server sends with a 1024-packet window by default.
Logging defaults to `warn` because `info` records every SOCKS destination.
Existing profiles keep their saved log level. Manual controls are disabled when
a preset would override them. The app offers the documented manual ranges; its
keepalive inputs use positive seconds up to one day. Blank fields select
defaults instead of upstream's zero sentinel. Optional low-level port-forward
rules remain a writer capability, separate from packet tuning. The `transport.tcpbuf` and `transport.udpbuf` fields that appear
in older upstream examples do not exist in alpha.21 and are not written.

The fork logs unknown or misplaced configuration keys as a warning instead of
ignoring them silently, and warns when manual KCP controls are set with a
preset mode; the writer emits neither. Its optional `log.stats_interval` and
`network.dscp` (default 46) keys are not written, so the engine defaults apply.
PaqetFire does not parse Paqet's log output, so the fork's per-second
`retrans=`/`lost=` statistics format, and the `capture_drops=` field a2 adds to
it, do not affect it.

a2 adds `network.pcap.backend` (`auto`, `afpacket`, `libpcap`). `auto` selects
AF_PACKET on Linux and Npcap elsewhere, and `afpacket` is rejected on Windows,
so the writer omits the key. a2 also logs a warning when `transport.kcp.mtu`
plus 60 bytes of IPv4/TCP headers (80 with IPv6) exceeds the interface MTU,
because such packets are dropped and the tunnel stalls once data flows. The
app's MTU field still accepts up to 1500 and does not check the interface.

SOCKS listener wiring remains owned by the application. The listener requires
the broker's engine credentials, generated once and stored DPAPI-protected in
`%ProgramData%\PaqetFire\engine-credentials.json`; Xray's Paqet outbound, the
live-statistics probe, and ProxiFyre's link to Xray use the same credentials.
The readiness probe offers both no-auth and username/password so it only checks
that a SOCKS5 server answers.

A server host name is resolved by the broker before Paqet starts, querying all
of the adapter's resolvers in parallel from the broker's own socket (first
answer wins, 1.5 seconds overall), then the Windows resolver, then the last
address that worked. Transport restarts reuse the last resolved address without
a lookup, and that address is kept in the broker's protected state so it
survives a service restart. Paqet resolves a host name once at
startup through Windows, whose lookups ProxiFyre may be capturing into a route
that is not up yet, as during a transport restart or a kill-switch guard.

When updating the pinned release, re-audit the client schema in the fork's
[`internal/conf`](https://github.com/4H1R/paqet/tree/3f9014f0986384357256efe9da093006162eac54/internal/conf)
rather than cached web examples, and align the validator's ranges with it.

## Tunnel watchdog

Paqet keeps running and retrying when its server stops answering, so process
state alone would report a dead route as connected. The broker probes right
after every connect and transport restart, then sends a HEAD request on a fresh
SOCKS stream through Paqet every 10 seconds, to `cp.cloudflare.com` and
`connectivitycheck.gstatic.com` in parallel, with a 4-second reachability
timeout. After a failure it re-probes every 3 seconds, so three consecutive
failures, and a stalled tunnel, are detected in about 12 seconds. A successful
live-statistics latency check or growing Xray download counters within the last
5 seconds count as success, so a busy tunnel is not restarted for a slow probe.

On a stall the broker re-detects the network, rewrites the engine
configurations, and restarts Paqet, plus Xray only if its own configuration
changed. ProxiFyre keeps running, so routed traffic fails closed rather than
going direct. Restart backoff doubles from 10 seconds to 60 seconds (5 minutes
while Windows reports no network) and resets on success or a network change.

## Recovery and network changes

The broker, not the desktop app, keeps the route the user asked for:

- If Paqet exits or faults while Xray and ProxiFyre are running, only Paqet is
  restarted, after 1, 2, 5, 10, then every 30 seconds. After five attempts
  without a confirmed healthy tunnel (about 18 seconds), the route is released
  to its configured safe state: the kill-switch guard or a full disconnect. A
  network change or resume re-arms recovery.
- Network address changes are debounced for 1.5 seconds and compared by
  fingerprint (interface, IPv4 address, gateway and its MAC, DNS servers, hotspot
  address). Only engines whose generated configuration changed are restarted;
  ProxiFyre never is. When Xray restarts, the Windows DNS cache is flushed
  because FakeDNS placeholders belong to the Xray process that issued them.
- Resume from sleep always restarts Paqet, whose KCP session is stale by then.
- The intent to stay connected and the active profile are kept in
  `%ProgramData%\PaqetFire\broker-state.json` (DPAPI, SYSTEM and
  Administrators only), so a restarted broker reconnects by itself. A user
  disconnect, factory reset, or failed explicit connect clears it.

Automatic adapter selection follows the Windows route to the server
(`GetBestInterfaceEx`), falling back to Ethernet before Wi-Fi. The gateway MAC
comes from the neighbor table first, then up to three ARP requests 500 ms apart.

After a connect, a background request through Paqet to an IPv6 literal checks
whether the server has IPv6 egress. The result is remembered per server; when
it has none, later connects stop handing applications AAAA records, which would
otherwise stall before falling back to IPv4. The running Xray is not restarted
for this.

## Licensing boundary

- Paqet is MIT licensed and can be redistributed when its copyright/license
  notice is included (`payload/licenses/Paqet-MIT.txt`).
- paqctl is AGPL-3.0. PaqetFire does not need to ship or invoke paqctl: the small
  Windows script's network discovery and configuration behavior is implemented
  independently in the broker. Copying paqctl code into PaqetFire would add
  AGPL source-distribution and notice obligations.
- Npcap is not open source and the free installer explicitly forbids
  redistribution. A public all-in-one installer may only bundle Npcap after an
  Npcap OEM redistribution license or written open-source redistribution grant
  is obtained. Without that grant, detect Npcap and direct the user to the
  official interactive download; do not put the free installer in the MSI.

The Npcap restriction applies even when PaqetFire is free and earns no revenue.
