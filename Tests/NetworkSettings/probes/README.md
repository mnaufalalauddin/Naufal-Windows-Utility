# Opt-in Wi-Fi traffic probes

These probes send real DNS requests and must only be run with authorization for
the target interface, network and resolver. They do not change Windows settings.
The regular .NET regression suite does not run them.

`wifi_traffic_probe.py` binds the source address and Windows outgoing interface
for DNS and HTTPS. Global IPv6 destinations use scope zero; link-local
destinations require the interface scope. HTTPS validates TLS certificates.
`wifi_dns_cloudflare_probe.py` sends eight A/AAAA queries to the two
[official Cloudflare IPv6 resolvers](https://developers.cloudflare.com/1.1.1.1/setup/windows/).
Failures remain failures; no retries or default-interface fallback conceal them.

For a separately authorized temporary Save/reconnect/traffic/rollback test,
build `NetworkSettings.Tests.csproj`, then use an elevated terminal:

```text
NetworkSettings.Tests.exe --ssid-dns-v6-roundtrip <adapter-guid> "<exact-ssid>" <adapter-mac> "<fresh-evidence-directory>" "<python.exe>" "<absolute-path-to-wifi_dns_cloudflare_probe.py>"
```

Review both Python files first and keep them together. The test requires an
automatic-addressing baseline, absent IPv6 per-SSID metadata and a connected
Ethernet fallback. It changes IPv6 DNS only through the production per-SSID
backend, retains a snapshot before mutation, reconnects the same network, checks
runtime settings and bound queries, then restores in `finally`. It never reboots.
Do not switch SSIDs or modify settings concurrently. Recovery conflicts stop with
retained evidence; a process crash or power loss can require snapshot recovery.

A passing alternative-resolver test does not repair the router DNS service.
In roundtrip mode the original router DNS is restored, even when it was previously unreliable.

Only with separate explicit authorization to retain the resolver change, replace
the mode with `--ssid-dns-v6-apply-retain`. It uses the same arguments and safeguards,
rolls back on failure, and retains settings only after verified metadata, reconnect,
eight successful bound queries, and a final isolation check. `retained.json` records
the exact recovery snapshot. It is not a default mode or a general DNS recommendation.

## Read-only post-boot verification

`../Invoke-SsidDnsPostBootCheck.ps1` checks an already retained DNS configuration;
it does not save settings, reconnect, restart, or register a scheduled task.
Run `-Stage Prepare` with `-EvidenceDirectory`, `-PythonPath`, and the explicitly
authorized `-AdapterId`, `-ExpectedSsid`, and `-ExpectedMac`. It records a boot
identity and current effective configuration. After a **manual** restart, connect
that SSID and run `-Stage Resume` with the same evidence directory and Python path.
Resume derives network identity from the checkpoint, refuses the same/older boot,
checks automatic addressing/DNS/gateways, then tests Wi-Fi-bound DNS and HTTPS.
It preserves the original checkpoint and creates a separate successful completion
record; an AwaitBoot checkpoint alone is not a pass. Windows PowerShell 5.1 and
PowerShell 7 timestamp representations are handled without relaxing the boot gate.
