# PaqetFire third-party notices

PaqetFire distributes the following independently maintained engine payloads as
an aggregate. Each engine remains under its upstream license.

## Paqet v1.0.0-alpha.21

- Project: https://github.com/hanselime/paqet
- Exact source: https://github.com/hanselime/paqet/tree/v1.0.0-alpha.21
- License: MIT

## ProxiFyre 2.6.1

- Project: https://github.com/wiresock/proxifyre
- Exact source: https://github.com/wiresock/proxifyre/tree/v2.6.1
- License: GNU Affero General Public License v3.0

The corresponding source for the bundled ProxiFyre release is available at the
exact-source link above at no charge. PaqetFire does not remove or restrict any
rights granted by the AGPL-3.0 license.

## Xray-core 26.3.27

- Project: https://github.com/XTLS/Xray-core
- Exact source: https://github.com/XTLS/Xray-core/tree/v26.3.27
- License: Mozilla Public License 2.0

PaqetFire uses Xray only as a local routing-policy engine. The bundled
`geoip.dat` and `geosite.dat` files are the unmodified files from the official
Xray Windows release.

## Packet drivers

Paqet requires Npcap. The free Npcap installer is not redistributed because its
license does not grant general redistribution rights. PaqetFire detects an
existing Npcap installation and can be built with a separately licensed Npcap
OEM payload.

ProxiFyre requires Windows Packet Filter. Its project and signed installers are
available from https://github.com/wiresock/ndisapi.
