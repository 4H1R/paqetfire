# Profiles, portable exports, and reset

An incomplete profile can be selected, duplicated, renamed, or deleted while
disconnected. Selecting a profile opens it for editing; connection validation
is required when starting a route. Deleting the final profile opens a fresh
empty default profile. To change to an incomplete profile while a route or
kill switch is active, disconnect first.

Transport keys belong to individual profiles. SOCKS sharing authentication
belongs to the application: importing, switching, duplicating, or deleting
profiles preserves the current username and password. Changing sharing
credentials updates them for every profile. Existing catalogs adopt the
active profile's credentials on upgrade. The protected v1 storage format
retains copies per profile for compatibility; they are kept in sync.

## Portable export schema v2

Export includes every profile's transport key. Store and share these files
privately. The `kind` identifies the document and `schemaVersion` versions its
layout independently of protected local storage. Each profile has an ID and
name, with settings grouped by owner:

- `paqet`: server endpoint, transport key, KCP mode, ordered TCP flag cycles,
  and advanced packet/transport options.
- `xray`: destination policy, LAN bypass, regional routing, and blocking rules.
- `proxiFyre`: application capture, exclusions, protocol/address-family choices,
  and kill switch.
- `sharing`: LAN/hotspot enablement and ports. Authentication remains local.

A minimal example (the key below is a placeholder):

```json
{
  "schemaVersion": 2,
  "kind": "paqetfire-portable-profile-export",
  "activeProfileId": "11111111-1111-1111-1111-111111111111",
  "defaultProfileId": "11111111-1111-1111-1111-111111111111",
  "profiles": [
    {
      "id": "11111111-1111-1111-1111-111111111111",
      "name": "Home",
      "paqet": {
        "serverEndpoint": "server.example:8443",
        "transportKey": "REPLACE_WITH_YOUR_KEY",
        "kcpMode": "fast",
        "localTcpFlags": ["PA"],
        "remoteTcpFlags": ["PA"],
        "advanced": { "kcpBlock": "aes", "kcpMtu": 1350 }
      },
      "xray": { "regionalPreset": "iranDirect", "bypassLan": true },
      "proxiFyre": { "routingMode": "allApplications", "killSwitchEnabled": false },
      "sharing": { "shareWithLan": false, "shareViaHotspot": false }
    }
  ]
}
```

Import replaces the catalog and works on a fresh installation. Version 1
`paqetfire-profile-export` redacted files remain supported; their missing
transport keys can be entered separately for each profile. New exports omit
the original PC's adapter GUID, so the target selects its own adapter. If
optional IPv6 capture details were explicitly configured, update those for
the destination network. Installed applications and drivers must exist on
the destination PC. Saving a disconnected profile with the kill switch off
does not require running engines or detecting an adapter.

## Factory reset

Settings → Reset app to defaults opens a confirmation. Confirming disconnects
the route and releases the kill switch, replaces the catalog with an empty
default profile, clears transport keys and sharing authentication, removes
generated engine configurations and their backups, and restores desktop
preferences including startup registration and network automation rules.
It does not uninstall PaqetFire or its drivers. Exported files are unaffected.

The broker and desktop use IPC v11 and must be upgraded together.
