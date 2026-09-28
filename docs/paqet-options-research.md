# Paqet client option coverage

Research date: 2026-09-28. This records the baseline before the accompanying
profile/settings changes; the gaps below are implementation checklist items,
not a claim that they remain unfixed.

## Versions and method

The bundled release is `v1.0.0-alpha.21`, commit
`4b81ce0ab56b7cc9aec7501c87c4804676cdb1b4`. Live upstream `master` resolved to
`b9fa0bd93bf93b2eff27197742562ed82201f16e`. GitHub tree blob hashes matched for
every `internal/conf/*.go`, `internal/tnet/kcp/*.go`, and
`example/client.yaml.example` between these revisions. The following inventory
therefore applies to both. Sources: [release tree](https://github.com/hanselime/paqet/tree/4b81ce0ab56b7cc9aec7501c87c4804676cdb1b4),
[master snapshot](https://github.com/hanselime/paqet/tree/b9fa0bd93bf93b2eff27197742562ed82201f16e).

Read the source directly: cached web results for `master` included old fields
that are absent from the current source. In particular, `transport.tcpbuf` and
`transport.udpbuf` no longer exist in either inspected revision. They must not
be presented as working controls for alpha.21. [Transport schema](https://github.com/hanselime/paqet/blob/4b81ce0ab56b7cc9aec7501c87c4804676cdb1b4/internal/conf/transport.go)

## Packet/network inventory

| YAML key | Upstream behavior | Baseline PaqetFire gap |
| --- | --- | --- |
| `network.interface` | Required, existing interface; at most 15 bytes | Already configurable/detected |
| `network.guid` | Required on Windows | Already detected |
| `network.ipv4.addr` | Local address and port; port 0 chooses a port | Writer hardcodes port 0 |
| `network.ipv4.router_mac` | Required for configured IPv4 | Already configurable/detected |
| `network.ipv6.addr` | Optional local IPv6 endpoint | Model/writer only |
| `network.ipv6.router_mac` | Required for configured IPv6 | Model/writer only |

At least one address family is required upstream; the app currently requires
IPv4. When both families are configured, their local ports must match. The
server's resolved address family must be available locally. A fixed local port
requires `transport.conn: 1`.
[Network validation](https://github.com/hanselime/paqet/blob/4b81ce0ab56b7cc9aec7501c87c4804676cdb1b4/internal/conf/network.go),
[cross-field validation](https://github.com/hanselime/paqet/blob/4b81ce0ab56b7cc9aec7501c87c4804676cdb1b4/internal/conf/conf.go)

| YAML key | Upstream default / accepted values | Baseline PaqetFire gap |
| --- | --- | --- |
| `network.tcp.local_flag` | `["PA"]`; ordered cycle of up to 64 combinations | UI exists; validator allows only 32 and omits `N`; writer deduplicates cycles |
| `network.tcp.remote_flag` | `["PA"]`; same rules | Same |
| `network.pcap.sockbuf` | Client 4,194,304 bytes; 1,024–104,857,600 | Model/writer only; validator incorrectly uses 65,536–1,073,741,824 |
| `transport.protocol` | Required `kcp` | Writer fixes this correctly |
| `transport.conn` | 1; range 1–256 | Model/writer only |

TCP letters are `F S R P A U E C N` (FIN, SYN, RST, PSH, ACK, URG, ECE,
CWR, NS). Repeated combinations are meaningful because flags cycle; preserve
sequence and duplicates. PCAP zero selects its default; powers of two are
recommended, not required.
[TCP parser](https://github.com/hanselime/paqet/blob/4b81ce0ab56b7cc9aec7501c87c4804676cdb1b4/internal/conf/tcp.go),
[PCAP defaults/validation](https://github.com/hanselime/paqet/blob/4b81ce0ab56b7cc9aec7501c87c4804676cdb1b4/internal/conf/pcap.go),
[transport](https://github.com/hanselime/paqet/blob/4b81ce0ab56b7cc9aec7501c87c4804676cdb1b4/internal/conf/transport.go)

## Complete KCP inventory

All following keys are under `transport.kcp`. Every field already exists in
`PaqetProfile` and its YAML writer. Baseline desktop settings expose only four
presets and the transport key; even `manual` is missing from the dropdown.

| Key | Client default / source validation |
| --- | --- |
| `mode` | `fast`; `normal`, `fast`, `fast2`, `fast3`, `manual` |
| `nodelay` | Manual parameter |
| `interval` | Manual parameter, milliseconds |
| `resend` | Manual parameter |
| `nocongestion` | Manual parameter |
| `wdelay` | Manual boolean, default false |
| `acknodelay` | Manual boolean, default false |
| `mtu` | 1350; 50–1500 |
| `rcvwnd` | 512; 1–32768 |
| `sndwnd` | 128; 1–32768 |
| `block` | `aes`; algorithms listed below |
| `key` | Required except for `none`/`null` |
| `smuxbuf` | 4,194,304; minimum 1024 |
| `streambuf` | 2,097,152; minimum 1024 |
| `smuxkalive` | 2 seconds |
| `smuxktimeout` | 8 seconds |
| `dshard` | 0, FEC disabled |
| `pshard` | 0, FEC disabled |

Zero selects defaults for MTU, windows, SMUX buffers and keepalive durations.
Algorithms: `aes`, `aes-128`, `aes-128-gcm`, `aes-192`, `salsa20`, `blowfish`,
`twofish`, `cast5`, `3des`, `tea`, `xtea`, `xor`, `sm4`, `none`, `null`.
[KCP configuration source](https://github.com/hanselime/paqet/blob/4b81ce0ab56b7cc9aec7501c87c4804676cdb1b4/internal/conf/kcp.go)

The example documents manual `nodelay`/`nocongestion` as 0/1, `interval` as
10–5000, and `resend` as 0–2. The top-level validator does not enforce those
ranges. The pinned KCP dependency clamps nonnegative intervals to 10–5000;
it accepts nonnegative resend values beyond 2. Negative values leave that
part of the initialized KCP state unchanged. The app may intentionally offer
the documented ranges, but should identify that as app policy.
[Client reference](https://github.com/hanselime/paqet/blob/4b81ce0ab56b7cc9aec7501c87c4804676cdb1b4/example/client.yaml.example),
[KCP NoDelay implementation](https://github.com/xtaci/kcp-go/blob/v5.6.72/kcp.go)

Preset values override all six manual controls, including both booleans:

| Mode | nodelay | interval | resend | nocongestion | wdelay | acknodelay |
| --- | ---: | ---: | ---: | ---: | --- | --- |
| normal | 0 | 40 | 2 | 1 | true | false |
| fast | 0 | 30 | 2 | 1 | true | false |
| fast2 | 1 | 20 | 2 | 1 | false | true |
| fast3 | 1 | 10 | 2 | 1 | false | true |

The runtime fixes stream mode to true, DSCP to 46, SMUX version to 2, and
SMUX maximum frame size to 65535. These are not YAML controls.
[Runtime configuration application](https://github.com/hanselime/paqet/blob/4b81ce0ab56b7cc9aec7501c87c4804676cdb1b4/internal/tnet/kcp/kcp.go)

SMUX validates the *effective* values after Paqet defaults: stream buffer must
not exceed receive buffer; both are at most 2,147,483,647 bytes. Keepalive
timeout must be at least the interval (equality is accepted despite the error
message). Validate these relations even when just one field is supplied. Use
positive duration inputs in the app; the dependency's interval check only
rejects zero and is not a useful user-facing policy for negatives.
[Pinned SMUX validation](https://github.com/xtaci/smux/blob/v1.5.53/mux.go)

FEC is disabled by omitted shards or explicit 0/0. The KCP encoder/decoder
return no FEC object when either count is nonpositive. The decoder accepts a
combined count up to 256; larger totals disable decoding. A useful app policy
is both zero, or both positive with total at most 256. The baseline app
rejects 0/0 and caps the total at 255.
[Pinned FEC implementation](https://github.com/xtaci/kcp-go/blob/v5.6.72/fec.go)

## Other client options and scope

| YAML key | Behavior | Baseline app coverage |
| --- | --- | --- |
| `role` | Explicit client/server | App fixes client |
| `log.level` | Default `none`; none/debug/info/warn/error/fatal | Model defaults info; UI absent |
| `server.addr` | Required endpoint, port 1–65535 | UI exists |
| `socks5[].listen` | Local listener | One app-managed internal listener |
| `socks5[].username`, `socks5[].password` | Optional local SOCKS authentication | Engine credentials must remain app-owned |
| `forward[].listen`, `forward[].target`, `forward[].protocol` | Optional local port forwarding | Model/writer only |

Sources: [top-level schema](https://github.com/hanselime/paqet/blob/4b81ce0ab56b7cc9aec7501c87c4804676cdb1b4/internal/conf/conf.go),
[logging](https://github.com/hanselime/paqet/blob/4b81ce0ab56b7cc9aec7501c87c4804676cdb1b4/internal/conf/log.go),
[server endpoint](https://github.com/hanselime/paqet/blob/4b81ce0ab56b7cc9aec7501c87c4804676cdb1b4/internal/conf/server.go),
[SOCKS](https://github.com/hanselime/paqet/blob/4b81ce0ab56b7cc9aec7501c87c4804676cdb1b4/internal/conf/socks.go),
[forwarding](https://github.com/hanselime/paqet/blob/4b81ce0ab56b7cc9aec7501c87c4804676cdb1b4/internal/conf/forward.go).

`listen.addr` belongs to server mode. Multiple SOCKS listeners and port
forwarding are listener/routing features rather than packet tuning; keeping
them outside the advanced packet form is an explicit scope decision. A
cross-PC profile should carry remote transport secrets/settings while the
destination retains app SOCKS credentials and redetects its interface/IP/MAC.

## Implementation checklist

- Expose typed advanced settings through desktop, broker messages, profile
  storage, import/export, and runtime projection; adding only model fields
  does not make a control functional.
- Add local IPv4 source port; enforce matching IPv6 port and connection count.
- Offer manual mode and all KCP fields; explain which controls presets ignore.
- Fix TCP NS support and preserve ordered flag cycles, including duplicates.
- Match PCAP and KCP-window ranges; validate effective SMUX relationships.
- Support disabled FEC and use a documented shard-count policy.
- Remove obsolete TCP/UDP buffer output rather than expose ineffective controls.
- Keep local SOCKS authentication separate from profile switching/import.

Baseline local files inspected: `PaqetProfile.cs`,
`PaqetConfigurationValidator.cs`, `PaqetYamlConfigurationWriter.cs`,
`PaqetFireSettings.cs`, `DesktopPreferences.cs`, `MainWindow.xaml`, and
`MainWindow.xaml.cs`.
