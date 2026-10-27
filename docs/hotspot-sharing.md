# Hotspot SOCKS5 sharing

PaqetFire can expose a separate Xray SOCKS5 listener to devices on the same
Windows mobile hotspot network. Sharing is disabled by default and requires a
username and a password of at least eight characters.

The broker detects the active hotspot adapter and binds Xray to that adapter's
specific IPv4 address (usually `192.168.137.1`). It does not listen on
`0.0.0.0`. The installer creates inbound TCP and UDP rules for Xray restricted
to Windows Firewall's `localSubnet` scope — this already covers the hotspot
subnet `192.168.137.0/24`, so no new firewall rule is needed.

Detection requires an active Windows Wi-Fi Direct or Hosted Network adapter
without an IPv4 gateway, using the default `192.168.137.*` or `192.168.173.*`
subnet. Custom hotspot subnets are not currently supported.

When sharing is enabled, another device uses:

- server: the IPv4 address displayed by PaqetFire (usually `192.168.137.1`);
- port: the configured hotspot SOCKS5 port (default `10808`);
- protocol: SOCKS5;
- username and password: the shared proxy credentials configured in PaqetFire.

SOCKS5 authentication is access control, not encryption. Anyone able to observe
the local network may be able to inspect SOCKS5 traffic and credentials. Use the
feature only on a trusted network, never forward the port from a router, and
never expose it directly to the public internet.

The password is encrypted in broker-owned settings. Xray requires it in its
generated runtime configuration, so all generated engine configuration files
are restricted to SYSTEM and Administrators with protected Windows ACLs.

## Prerequisites

- Laptop is online via Ethernet or Wi‑Fi (upstream required — the hotspot
  downstream shares the same route; without an upstream connection, the broker
  will fail closed with a re‑detect prompt).
- Mobile hotspot is turned on in Windows Settings (ensure the hotspot is active
  before clicking **Detect** in PaqetFire).
- Paqet is connected to your server (the server endpoint and transport key must
  be configured).
- **Local network devices (Direct access)** must be enabled so hotspot clients
  can communicate with the host without local traffic being redirected into the
  proxy. If disabled, turning on Hotspot SOCKS proxy will automatically prompt
  to enable it.

## Desktop steps

1. Open PaqetFire and go to the **Routing** page.
2. Enable **Hotspot SOCKS proxy**. If prompted, confirm enabling direct access for local network devices.
3. Set the username and password in the **Proxy sharing credentials** card if
   not already set, or use **Generate strong password**. These credentials are
   shared by LAN and hotspot sharing and apply across all profiles.
4. Ensure the mobile hotspot is active on the laptop; the hotspot address
   field will populate (usually `192.168.137.1`) and the **SOCKS endpoint** text
   shows the address and port.
5. Scan the QR code on the hotspot card with the phone's SOCKS client, or click
   **Copy URI** to copy the `socks5://` URI. Use **Test listener** to check that
   the listener is reachable, or **Copy redacted setup** to share the settings
   without the password.

The QR code and URI need the sharing password. The desktop keeps it in memory for
the rest of the session after you enter, rotate, or save it. After restarting the
app it cannot read the saved secret back from the broker; use **Rotate
credentials to show QR**, or re-enter the current password. If you enter a
different password, save before using it on your clients.

## Phone setup (SOCKS app required)

Bare Android/iOS WiFi Settings proxy is HTTP‑only and explicitly unsupported. A
SOCKS‑compatible app is required. The **v2rayNG Guide** and **HAPP / Hiddify
Guide** below the Hotspot sharing card walk through client setup with copy
buttons for the live address, port, credentials, and URI. Enable **Remote DNS**
in the client so DNS queries are also routed through the proxy.

## What to expect

- Devices join the hotspot and get an address while PaqetFire is connected,
  whatever the routing mode: the Internet Connection Sharing service that
  answers them is a locked ProxiFyre exclusion.
- The same regional/direct‑routing, ad‑blocking, QUIC and BitTorrent policy
  that applies to LAN clients also applies to hotspot clients — the route is
  shared.
- If the hotspot is turned off, PaqetFire will show
  **"Hotspot unavailable — turn on Mobile hotspot in Windows Settings, then click Detect hotspot."**
  Turning the hotspot back on automatically updates the status, or clicking **Detect hotspot**
  refreshes it immediately.
- Only one radio can be used for both Wi‑Fi connection and hotspot broadcasting
  on many laptop drivers; if you experience failures, prefer an Ethernet
  upstream connection.
- The 2.4 GHz vs 5 GHz hotspot band is Windows‑controlled; some drivers
  restrict simultaneous use. If hotspot detection fails, try the other band or
  connect via Ethernet.
- SOCKS does not encrypt phone → laptop traffic. Keep the hotspot WPA2
  password strong and never forward port `10808` (or your custom port) from
  a router.

## Security

- WPA2 + strong SOCKS password recommended.
- SOCKS5 is access‑control only; it does not encrypt traffic.
- Never forward the sharing port from a router or expose it to the public
  internet.
- The broker fails closed if the hotspot is absent — the user must re‑detect
  when the hotspot is turned back on.
