# Xray regional policy routing

PaqetFire bundles Xray-core 26.3.27 as a local destination-policy engine.
Xray is not a second VPN and does not replace Paqet.

The official latest-stable release and archive digest were rechecked on
2026-09-24; 26.3.27 remains current. See the
[engine release assessment](engine-update-2026-09-24.md) for feature opportunities
and upgrade validation.

## Data path

```text
selected Windows applications
  -> ProxiFyre
  -> Xray SOCKS5 on 127.0.0.1:1081
     -> direct outbound for enabled bypass/block rules
     -> Paqet SOCKS5 on 127.0.0.1:1080 for the catch-all
```

ProxiFyre always excludes the full installed paths of Paqet, Xray, ProxiFyre,
and the broker. Xray's direct and Paqet-bound sockets therefore cannot re-enter
the application redirector and form a loop.

## Default policy

- `geosite:private` and `geoip:private` go direct when LAN bypass is enabled.
- `geosite:category-ir` and `geoip:ir` go direct when the Iran preset is enabled.
- all remaining TCP and UDP traffic goes to Paqet.
- advertising blocking and direct BitTorrent are on for new profiles; UDP/443
  blocking is off.
- `IPIfNonMatch` is the default domain strategy.

The UI can disable the regional preset with `None`. It also exposes the three
Xray domain strategies and optional ads, QUIC, and BitTorrent rules. Direct
BitTorrent explicitly warns that it exposes the machine's normal public IP.

## Lifecycle and integrity

The broker validates the generated JSON with `xray run -test` before launch.
Connect order is Paqet, Xray, then ProxiFyre. Disconnect and rollback use the
reverse order. Xray's executable and GeoIP/GeoSite databases are pinned in the
payload manifest and verified before a connection may start.

The generated `config.json` is broker-owned and written atomically under the
protected installation payload directory. It is not accepted from the UI.
