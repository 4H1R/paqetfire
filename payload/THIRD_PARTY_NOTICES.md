# PaqetFire third-party notices

PaqetFire distributes the following independently maintained engine payloads as
an aggregate. Each engine remains under its upstream license.

## Paqet v1.0.0-alpha.21

- Project: https://github.com/hanselime/paqet
- Exact source: https://github.com/hanselime/paqet/tree/v1.0.0-alpha.21
- License: MIT

## ProxiFyre 2.6.4

- Project: https://github.com/wiresock/proxifyre
- Exact source: https://github.com/wiresock/proxifyre/tree/v2.6.4
- License: GNU Affero General Public License v3.0

The corresponding source for the bundled ProxiFyre release is available at the
exact-source link above at no charge. PaqetFire does not remove or restrict any
rights granted by the AGPL-3.0 license.

## Psiphon tunnel core (console client, binaries commit d95d7f9)

- Project: https://github.com/Psiphon-Labs/psiphon-tunnel-core
- Exact binary: https://github.com/Psiphon-Labs/psiphon-tunnel-core-binaries/blob/d95d7f964c043a5a0ebd25cca801bc2753e14f74/windows/psiphon-tunnel-core-i686.exe
- Exact source: https://github.com/Psiphon-Labs/psiphon-tunnel-core/tree/c4a09096ab401b57f52e092c8f2f829c8fac7628
- License: GNU General Public License v3.0

The unmodified upstream console client runs as a separate process only when a
profile selects the Psiphon exit. The corresponding source is available at the
exact-source link above at no charge. PaqetFire does not remove or restrict any
rights granted by the GPL-3.0 license. The client connects to the Psiphon
network operated by Psiphon Inc., whose terms apply to that service.

## Xray-core 26.3.27

- Project: https://github.com/XTLS/Xray-core
- Exact source: https://github.com/XTLS/Xray-core/tree/v26.3.27
- License: Mozilla Public License 2.0

PaqetFire uses Xray only as a local routing-policy engine. The bundled
`geoip.dat` and `geosite.dat` files are the unmodified files from the official
Xray Windows release.

## Downloaded routing data

When updated routing data is enabled, the broker downloads `geoip.dat` and
`geosite.dat` from https://github.com/Chocolate4U/Iran-v2ray-rules on the
user's machine. Those files are not part of the PaqetFire installer. The
project is licensed under GPL-3.0, and its upstream sources keep their own
licenses; see the project page.

## Packet drivers

Paqet requires Npcap. The free Npcap installer is not redistributed because its
license does not grant general redistribution rights. PaqetFire detects an
existing Npcap installation and can be built with a separately licensed Npcap
OEM payload.

ProxiFyre requires Windows Packet Filter. Its project and signed installers are
available from https://github.com/wiresock/ndisapi.
