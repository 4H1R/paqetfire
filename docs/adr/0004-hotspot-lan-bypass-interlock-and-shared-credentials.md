# Hotspot LAN Bypass Interlock and Unified Sharing Credentials

## Context

When users activate Hotspot SOCKS proxy sharing without enabling the LAN SOCKS proxy, they previously could not view or configure the required SOCKS username and password because the input fields were nested exclusively within the LAN proxy card. Additionally, hotspot sharing requires local network direct access (`BypassLan`) to prevent local subnet communications between hotspot clients and the host machine from being redirected or dropped into the tunnel.

## Decision

1. **Unified Sharing Credentials**: We extract SOCKS username and password into a dedicated "Proxy authentication" section accessible whenever either LAN share or Hotspot share is active, ensuring a single source of truth and allowing independent configuration of Hotspot sharing without requiring LAN sharing.
2. **Hotspot LAN Bypass Interlock**: When toggling Hotspot sharing ON while "Local network devices" (`BypassLan`) is OFF, PaqetFire presents a confirmation dialog informing the user that local network devices must be direct. Upon confirmation, both "Local network devices" direct access and Hotspot sharing are enabled simultaneously.
3. **Inversion Guard**: If "Local network devices" is toggled OFF while Hotspot sharing is actively enabled, PaqetFire prompts the user to either keep direct access or disable Hotspot sharing, preventing broken routing.
