# LAN SOCKS5 sharing

PaqetFire 0.6 can expose a separate Xray SOCKS5 listener to devices on the same
local network. Sharing is disabled by default and requires a username and a
password of at least eight characters.

See also [Hotspot SOCKS5 sharing](hotspot-sharing.md) for sharing over a
Windows mobile hotspot.

The broker detects the active adapter and binds Xray to that adapter's specific
IPv4 address. It does not listen on `0.0.0.0`. The installer creates inbound TCP
and UDP rules for Xray restricted to Windows Firewall's `localSubnet` scope.
The local ProxiFyre listener remains separate on `127.0.0.1:1081`.

When sharing is enabled, another device uses:

- server: the IPv4 address displayed by PaqetFire;
- port: the configured LAN SOCKS5 port (default `1082`);
- protocol: SOCKS5;
- username and password: the values saved in PaqetFire.

SOCKS5 authentication is access control, not encryption. Anyone able to observe
the local network may be able to inspect SOCKS5 traffic and credentials. Use the
feature only on a trusted LAN, never forward the port from a router, and never
expose it directly to the public internet.

The password is encrypted in broker-owned settings. Xray requires it in its
generated runtime configuration, so all generated engine configuration files
are restricted to SYSTEM and Administrators with protected Windows ACLs.
