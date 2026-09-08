# Hotspot SOCKS-only, no HTTP inbound

We stay SOCKS-only for hotspot v1 and require a phone SOCKS app (v2rayNG, HAPP/Hiddify), because bare Android/iOS WiFi proxy is HTTP-only and an HTTP inbound would add config surface while still lacking UDP/DNS parity with the existing SOCKS route.
