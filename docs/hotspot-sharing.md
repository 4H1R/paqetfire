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
3. Configure the shared proxy username and password in the **Proxy authentication** card if not already set.
4. Ensure the mobile hotspot is active on the laptop; the hotspot address
   field will populate (usually `192.168.137.1`).
5. The **SOCKS endpoint** text will update to show `socks5://user:pass@192.168.137.1:10808`.
6. Click **Copy** to copy the SOCKS URI to the clipboard.

After saving or reopening a profile, re-enter the existing proxy password on the
Routing page to enable password and URI copying. The desktop cannot retrieve the
saved secret from the broker. If you enter a different password, save the profile
before using it on your clients.

## Phone setup (SOCKS app required)

Bare Android/iOS WiFi Settings proxy is HTTP‑only and explicitly unsupported. A
SOCKS‑compatible app is required. PaqetFire includes dedicated, interactive visual
guides for both apps directly below the Hotspot sharing card:

- **v2rayNG Guide** (Android step-by-step setup)
- **HAPP / Hiddify Guide** (iOS, Android, and Desktop setup)

Clicking either guide in PaqetFire provides 1-click copy buttons for your active
hotspot IP, port, credentials, and full SOCKS URI.

### v2rayNG

1. Install v2rayNG from F-Droid or the Play Store.
2. Add a new outbound SOCKS proxy:
   - **Address**: the hotspot IP shown in PaqetFire (e.g. `192.168.137.1`).
   - **Port**: `10808` (or the custom port if changed).
   - **User**: the LAN SOCKS username (default `paqetfire`).
   - **Password**: the LAN SOCKS password (at least 8 characters).
3. Enable **Remote DNS** in the outbound settings so DNS queries are also
   routed through the proxy.
4. Select the system-wide or per‑app proxy as needed.

### HAPP / Hiddify

1. Install HAPP or Hiddify from the respective store.
2. Add a new SOCKS5 proxy:
   - **Address**: the hotspot IP (e.g. `192.168.137.1`).
   - **Port**: `10808`.
   - **Username**: the LAN SOCKS username.
   - **Password**: the LAN SOCKS password.
3. Enable **Remote DNS** if available.
4. Apply the proxy for the desired apps or system-wide.

## What to expect

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
