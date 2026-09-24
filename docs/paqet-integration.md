# Paqet integration

## Pinned runtime

PaqetFire should pin the Windows x64 Paqet payload to `v1.0.0-alpha.21`
(`4b81ce0ab56b7cc9aec7501c87c4804676cdb1b4`). The locally verified release
binary reports Go 1.27.0, is 9,562,112 bytes, and has SHA-256:

`7D73F5130757B538C26C3ED76439A150978C3E06289C724DE37D916789AAF5DC`

The release artifact must be downloaded from the upstream release, not copied
from an existing machine. Release automation must independently verify the
published archive digest and then record the extracted executable's size and
digest in `payload-manifest.json`.

Runtime invocation is:

```text
paqet_windows_amd64.exe run -c C:\ProgramData\PaqetFire\config\paqet\client.yaml
```

The broker owns that child process, captures bounded stdout/stderr logs, checks
the executable digest immediately before launch, refuses a SOCKS endpoint that
is already occupied, waits for a real SOCKS5 greeting, and terminates the whole
process tree on disconnect or broker shutdown.

## Configuration support

`PaqetProfile`, its validator, and `PaqetYamlConfigurationWriter` cover every
client option documented by Paqet v1.0.0-alpha.21:

- SOCKS5 listen endpoint and optional username/password authentication;
- TCP/UDP port forwards;
- IPv4 and optional IPv6 capture details, TCP flag cycling, and PCAP buffer;
- connection count and TCP/UDP buffers;
- KCP presets and manual controls, MTU/windows, write/ACK delay, all upstream
  encryption choices, SMUX buffers/keepalives, and optional FEC shards.

The normal UI can expose server, key, interface, SOCKS port, KCP preset and
flags. Advanced controls can map directly to the additional init-properties on
`PaqetProfile`. Null numeric/boolean values are deliberately omitted so the
pinned Paqet binary supplies its own version-matched defaults.

## Broker registration

The composition root must:

1. Create `PaqetRuntimePaths.CreateDefault()` and ensure the configuration root
   and `paqet` child directory exist with Administrators/System write access and
   normal users read denied.
2. Load and successfully verify `payload\payload-manifest.json` before creating
   either engine.
3. Select the single `EngineKind.Paqet` manifest entry and pass it to
   `PaqetEngineFactory.Create(paths, entry, new IPEndPoint(IPAddress.Loopback,
   configuredPort))`.
4. Register that adapter as the Paqet `IEngineAdapter` used by
   `ConnectionController` and register it for async disposal.
5. Register `PaqetYamlConfigurationWriter`, an `AtomicConfigurationStore`
   constrained to `paths.ConfigurationPath`, and `PaqetConfigurationService`.
6. Use the shared `NetworkAdapterDetector` to populate the adapter selector and
   `PaqetPrerequisiteInspector.Inspect()` to block connection when Npcap is
   absent.

No user-supplied path is accepted by the process adapter or configuration
store.

## Payload layout

```text
broker\
  PaqetFire.Broker.exe
  payload\
    payload-manifest.json
    engines\
      paqet\
        x64\
          paqet_windows_amd64.exe
          LICENSE.paqet.txt
```

The manifest Paqet entry point is
`engines/paqet/x64/paqet_windows_amd64.exe`. Both the executable and license must be present
in its file inventory so integrity inspection fails closed if either is missing
or modified.

## Licensing boundary

- Paqet is MIT licensed and can be redistributed when its copyright/license
  notice is included.
- paqctl is AGPL-3.0. PaqetFire does not need to ship or invoke paqctl: the small
  Windows script's network discovery and configuration behavior is implemented
  independently in the broker. Copying paqctl code into PaqetFire would add
  AGPL source-distribution and notice obligations.
- Npcap is not open source and the free installer explicitly forbids
  redistribution. A public all-in-one installer may only bundle Npcap after an
  Npcap OEM redistribution license or written open-source redistribution grant
  is obtained. Without that grant, detect Npcap and direct the user to the
  official interactive download; do not put the free installer in the MSI.

The Npcap restriction applies even when PaqetFire is free and earns no revenue.
