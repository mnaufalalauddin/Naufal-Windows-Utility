# Windows per-SSID IP/DNS provider

Implemented on 11 October 2026. This is a build-limited, experimental native
provider, not a claim of supported public Windows API compatibility.

## Scope and platform guard

The utility writes only the selected Windows WLAN profile's
`ProfileIpConfigurationV4` and `ProfileIpConfigurationV6` metadata.
It never substitutes adapter-wide NetTCPIP/DNS commands, registry writes or an
SSID-triggered watcher. The local utility profile/draft library is not involved.
Windows WLAN profiles still exist internally; they identify the saved SSID.

The private ABI was independently inspected in local Microsoft Windows binaries.
Production use is gated to x64 and these exact SHA-256 fingerprints:

| File | SHA-256 |
| --- | --- |
| wlanapi.dll | 23AE26CF389988BB08D34851FAF38E221E5556CDFCD120F09241B78055246D74 |
| WlanMediaManager.dll | CC0E99E666CBDA5984E03356E3EC0B073DE9078A665325BC9E3B2A44E9592ED4 |

The inspected wlanapi.dll file version is 10.0.26100.9539. An OS update or
different build can disable this provider until its ABI is reviewed. A matching
file version string alone does not satisfy the guard. There is no force-enable
switch or fallback to global adapter writes.

## Independent inspection evidence

In the inspected WlanMediaManager image (preferred base 0x180000000):

- The IP reader uses metadata keys at RVA 0xABF98 (V4) and 0xABF60 (V6),
  calling WlanGetProfileMetadata at RVA 0x639FA.
- The writer calls WlanAllocateProfileIpConfiguration at RVA 0x6444A and
  WlanSetProfileMetadata at RVA 0x652BF.
- The metadata arguments are interface GUID pointer, UTF-16 Windows profile
  name, flags (zero), UTF-16 property key, byte length, and buffer pointer.
  The getter uses output length and buffer instead. These calls do not take
  the WlanOpenHandle client handle as their first parameter.
- The IP metadata header is seven DWORDs: version, address count/offset,
  gateway count/offset, DNS count/offset. Header size is 28 bytes.
- Address/gateway entries occupy 136 bytes (128-byte socket storage followed
  by a DWORD prefix/metric and padding). DNS addresses occupy 128 bytes.
- Version 2 appends one 4104-byte DNS configuration entry per server:
  version DWORD, flags DWORD and a 4096-byte UTF-16 template area.
  Ordinary DNS entries use version 1, zero flags and an empty template.
- IPv4 uses AF_INET=2 with address bytes at socket offset 4; IPv6 uses
  AF_INET6=23 with address bytes at offset 8. Address prefix is at offset 128.
- Empty address/gateway or DNS lists represent automatic configuration for
  that part. The Windows reader distinguishes modes by these list counts.

The application does not ship Windows binary code. Its managed codec bounds
all counts, offsets and sizes, rejects unknown/noncanonical data, and preserves
untouched sections. Encrypted/custom DNS options prevent DNS edits rather than
being silently discarded. IP-only edits preserve those options.

## Identity, persistence and recovery

1. Documented WlanQueryInterface reads the active connection. WlanGetProfile
   reads identity without the plaintext-key flag. The profile name and exact
   SSID bytes must agree. Policy-managed profiles are rejected.
2. The main editor loads persisted SSID metadata, not guessed adapter defaults.
   The information card's diagnostics still show currently effective addresses.
3. Save validates values, administrator rights, active physical Wi-Fi adapter
   and existing local policy gates. It compares current metadata with the scan.
4. Both new buffers are prepared and validated before any write. The exact
   original and expected metadata are durably written to a local recovery file.
5. Each family write is preceded by a fresh connected-SSID identity check and
   metadata comparison. Each write is read back. Unedited families are untouched.
6. Failure attempts recovery only for attempted sections, only while connected
   to the original SSID, and only if metadata is still original or matches this
   operation's expected output. Conflicting external data is not overwritten.
7. An originally absent property is retained as JSON null. Recovery requests
   removal via zero-length metadata and verifies absence. If Windows rejects
   this, recovery is explicitly reported as unverified; the snapshot is retained.

Snapshots are under the existing application data root,
`NetworkSettings/WifiSnapshots`. They contain local network identities and
addresses but not Wi-Fi keys. Recovery never deletes or recreates a WLAN profile.
No automatic reconnect, adapter reset, global DHCP reset or reboot is performed.
Manual reconnection may be needed before the saved configuration is effective.

## Verification and remaining acceptance tests

- Managed codec and simulated save/recovery tests cover IPv4/IPv6 automatic,
  manual and mixed IP/DNS modes, malformed buffers, absent metadata, stale scans,
  exact SSID/profile identity, policy blocks, mid-save SSID switches, partial
  failures, recovery conflicts and unrelated-SSID write isolation.
- The actual Windows WlanVerifyProfileIpConfiguration function accepts the
  generated in-memory buffers. This check makes no network/profile changes.
- Native UI tests inject mock callbacks; they check in-page editing, changes-only
  Save, explicit WifiSsid routing and disabling unavailable/stale-provider edits.
- **Bounded live pass, 11 October 2026:** on one Intel Wireless-AC 9560 host,
  persisted the currently used IPv4 DNS server as manual DNS for the connected
  SSID, verified the stored bytes, then restored the originally absent metadata
  using zero-length removal. Both families matched their exact original state.
  Other saved profiles' IP/DNS metadata and the inspected global adapter values
  (`NameServer`, `EnableDHCP`, `IPAddress`, `SubnetMask`, `DefaultGateway` for
  IPv4/IPv6 on inventoried physical adapters) were unchanged. Ethernet remained
  connected. No reconnect or reboot was performed; effective DNS use was not
  established by this metadata-only round trip. Raw evidence remains local.
- **Reconnect/two-SSID live pass, 11 October 2026:** saved the first network's
  current IPv4 DNS and explicit IPv6 automatic mode, disconnected/reconnected,
  and verified active DNS, a DHCP IPv4 address/default route, and an IPv6 address
  learned through router advertisement. Switching to the second network restored
  its own DNS/default gateway; returning to the first preserved its saved metadata
  and expected DNS. Original metadata was restored and rechecked after another
  reconnect. Other saved-profile metadata and inspected global values matched
  their baselines. The host ended connected to its initial SSID. This verifies
  active configuration, not interface-bound traffic or Internet reachability.
- **Two-reboot live pass, 11 October 2026:** saved IPv4 DNS and IPv6 automatic
  metadata persisted after the first manual reboot. Exact original metadata was
  restored, then verified again after the second manual reboot together with
  active IPv4 DNS/DHCP and IPv6 automatic/RA configuration. Inspected global values
  and other saved-profile metadata matched their baselines. The audit exited 0
  with checkpoint Complete at 20:02:51 WIB; no further restart is required.
- **Interface-bound traffic pass, 11 October 2026:** on restored automatic
  configuration, 8 DNS A/AAAA queries over the Wi-Fi IPv4/IPv6 resolvers and
  4 HTTPS HEAD requests over IPv4/IPv6 to two public endpoints passed. Outbound
  sockets used the Wi-Fi interface index and source addresses, with interface
  read-back, TLS certificate verification and HTTP 200 responses. No default-route
  fallback or configuration change was used. Earlier probe-only IPv6 scope errors
  were corrected before the successful run; they were not network-setting failures.
- **Reserved manual IPv4 pass, 11 October 2026:** user-supplied IPv4, mask and
  gateway became effective after reconnect. Existing DNS was explicitly retained
  for the test; IPv6 metadata was untouched. Wi-Fi-bound HTTPS passed using the
  manual address. The other SSID regained its own DHCP address/gateway/DNS, without
  the test address. Returning reactivated the saved manual configuration. Exact
  metadata rollback restored DHCP and HTTPS remained functional. The final lease
  matched the new router reservation, not necessarily the old DHCP lease.
  The HTTPS test uses system hostname resolution and does not certify DNS routing
  during the manual-address run; the separate bound-DNS test above covers automatic mode.
- **Observed Windows projection:** while manual addressing was active, Windows
  changed the inspected Wi-Fi IPv4 EnableDHCP value from 1 to 0. No other inspected
  global value or saved-profile metadata changed. The value returned to baseline
  on the second SSID while manual metadata remained saved for the first, then
  again after rollback. Initial audits conservatively failed on this difference
  and restored successfully. The final harness permits only this exact projection,
  while requiring the second-SSID and post-rollback baseline checks to pass.
  Do not describe this as every interface registry value remaining unchanged
  while a static SSID is active, or as an application global-adapter setter.
- **Static IPv4 reboot pass, completed 12 October 2026:** exact manual IPv4,
  prefix, gateway and DNS survived the first boot on 11 October. Original metadata
  was restored, then a distinct boot on 12 October verified DHCP recovery,
  original gateway/DNS, IPv6 automatic/RA state and original inspected global
  values/other-profile metadata. Checkpoint Complete at 19:39:35 WIB, exit 0.
  An intervening same-boot follow-up was not counted: sub-millisecond WMI
  timestamp drift is rejected by the corrected guard. No reapply or reboot
  was performed by the final Resume. Post-boot traffic is not covered by this audit.
- **Temporary manual IPv6 pass, 12 October 2026:** reused the currently assigned
  full address with explicit user approval, retaining the current prefix, gateway
  and DNS values. Stored metadata and effective Manual/Preferred address matched
  after reconnect. DHCPv6 was disabled while router discovery remained enabled;
  additional RA privacy addresses may coexist. Wi-Fi-interface/source-bound IPv6
  HTTPS passed with TLS validation and HTTP 200. Exact original metadata and
  inspected global/other-profile state were restored, automatic DHCP/RA recovered,
  and the same HTTPS check passed again. Final audit exit 0 at 19:50:29 WIB.
  The initial attempt failed an overly strict test expectation that RA must be
  disabled, then restored successfully. Only the harness expectation changed;
  no global RA setter or production safety bypass was added. The corrected suite
  passes 263 network assertions. This temporary reuse is not a permanent static
  allocation. HTTPS uses system hostname resolution, not a bound-DNS assertion.
- **Post-boot traffic partial, 12 October 2026:** before manual IPv6 mutation,
  automatic-mode Wi-Fi-bound checks passed all four IPv4 DNS queries and all
  four IPv4/IPv6 HTTPS requests. Three of four router IPv6 DNS queries timed out:
  9/12 checks passed, overall incomplete. Settings remained unchanged. Earlier
  successful traffic runs do not erase these timeouts or establish stable DNS.
- **Manual IPv6 two-SSID pass with limitations, 12 October 2026:** the requested
  manual address was absent on the second network; its own automatic modes,
  gateway/DNS, saved metadata and inspected global baseline matched. Returning
  restored the requested Manual/Preferred address and Wi-Fi-bound HTTPS passed.
  Windows retained DHCPv6 on return. Two RA privacy addresses remained listed
  on the hotspot: this verifies requested manual-configuration isolation, not
  all-address isolation or the hotspot's IPv6 Internet service. An initial
  attempt failed the too-strict DHCPv6-disabled expectation and recovered;
  two subsequent attempts stopped before mutation while the hotspot was absent.
  The final preparation exited 0 with `AwaitTestBoot` and retained manual IPv6
  intentionally. The first new boot at 20:48 WIB on 12 October preserved exact
  manual IPv6/prefix/gateway/DNS; source/interface-bound IPv6 HTTPS passed with
  valid TLS and HTTP 200. Resume restored exact original metadata, inspected
  global/other-profile baseline and automatic runtime, exiting 0 in
  `AwaitRestoreBoot`. The second boot at 20:58:20 WIB retained original metadata,
  automatic DHCP/RA runtime and inspected global/other-profile state. Wi-Fi-bound
  IPv6 HTTPS passed again with valid TLS and HTTP 200. Checkpoint `Complete`,
  exit 0, at 21:00:11 WIB. Final Resume performed verification only, no reapply.
  The expanded harness passes 276 regression assertions; same-boot Resume was
  rejected before mutation and explicit cancellation restored an earlier checkpoint.
- **Settling follow-up, 13 October 2026:** the manual IPv6 round trip passed with
  two old RA privacy addresses initially listed on the hotspot, then zero at
  10 and 30 seconds. The requested manual address was absent throughout the
  hotspot observations. Return and exact rollback passed source/interface-bound
  IPv6 HTTPS, exit 0. This closes the persistent-RA concern for this tested pair,
  not instantaneous transition isolation or all hardware. No RA settings or
  temporary addresses were forcibly removed. The updated harness fails if old
  RA addresses persist after its settling observation.
- **DNS comparison, 13 October 2026:** bounded Wi-Fi capture recorded six queries
  to the router's IPv6 DNS endpoint and two replies; four probes timed out. Zero
  capture drops were reported. An authorized temporary Cloudflare IPv6 DNS-only
  per-SSID test passed eight Wi-Fi-bound A/AAAA queries after Save/reconnect
  (eight direct preflight queries also passed). Exact original metadata, router
  DNS, automatic addressing and inspected global/other-profile baseline were
  restored; exit 0. No resolver change was retained. Router DNS remains unresolved;
  an alternative resolver working is not proof the router has been repaired.
  A subsequent separately authorized `--ssid-dns-v6-apply-retain` operation kept
  the alternative DNS only for the selected SSID, after eight bound DNS queries
  and final metadata/global/other-profile checks passed. A further combined
  DNS/HTTPS check passed 12/12 through Wi-Fi. Original snapshot retained; no
  automatic provider selection, reboot or change for other users is introduced.
- **Not hardware-verified:** instantaneous IPv6 transition isolation and other
  device/build combinations. An IPv6 Interface ID alone is not an allocated
  full manual address/prefix/gateway. The bounded tests are not full certification.

The opt-in `Tests/NetworkSettings/Invoke-SsidHostAudit.ps1` launcher is read-only
unless `-Live`, `-AdapterId`, and `-ExpectedSsid` are explicitly supplied. Live mode
requires administrator rights and prior consent; it creates fresh evidence and
recovery files, performs the DNS-only test and immediately restores. Do not run
it unattended on a production network. Evidence may contain local addresses and
network identities; do not publish raw artifacts.

`Invoke-SsidManualV4Audit.ps1` additionally requires an explicitly reserved IPv4,
mask, gateway, matching Wi-Fi MAC and authorized second saved SSID. It performs
the manual-address/reconnect/switch/HTTPS test and restores in a finally block.
Ethernet must be available. It never changes IPv6, router reservations or global
adapter configuration directly, and never reboots. Keep evidence and recovery
snapshots until the final exit code and rollback checks are reviewed.

`Invoke-SsidManualV6Audit.ps1` requires explicit consent to reuse the exact IPv6
currently assigned by router advertisement, its prefix/gateway, matching Wi-Fi
MAC and connected SSID. Ethernet must remain available. It preserves IPv4
metadata, retains existing DNS values, reconnects, checks manual runtime and
Wi-Fi-bound IPv6 HTTPS, then restores in a finally block in default Roundtrip mode.
Optional `-SecondSsid` explicitly opts into switching to an already saved,
authorized network: capture its baseline before mutation, verify the requested
manual address is absent there, then return and check manual address/metadata and
HTTPS again. RA-learned privacy addresses can initially remain listed by Windows
on the second network. The harness observes them again after 10 and 30 seconds,
performs bounded old-source connection diagnostics if they remain, and refuses
a pass on persistent leftovers even if the connection fails. This does not
certify instantaneous isolation or hotspot IPv6 service.
Windows can also retain DHCPv6 after returning while the requested address is
Manual/Preferred. The harness requires exact address origin/prefix/gateway/DNS,
not a DHCP flag alone; it never forces DHCP/RA globally to satisfy the test.

`-Stage Prepare` retains manual IPv6 only after all requested pre-boot checks
pass and a durable checkpoint is saved as `AwaitTestBoot`. Restart manually,
reconnect the original SSID with Ethernet available, then run the same launcher
with only `-Stage Resume -EvidenceDirectory <existing-directory>`. The first
Resume verifies saved metadata, manual runtime and Wi-Fi-bound HTTPS, restores
the exact original metadata, reconnects and verifies automatic recovery. A second
manual reboot/Resume certifies post-boot recovery. A failed first-boot test remains
failed even if recovery succeeds. Same-boot and finalized checkpoints cannot pass.
`-Stage Restore` with the existing directory provides explicit cancellation/recovery
without requiring a new boot; it refuses conflicting external metadata. No reboot
or startup task is installed. Inspect exit code and recovery evidence before treating
the test as successful; a failed test is not made a pass by successful recovery.

Adding `-Reconnect -SecondSsid` explicitly opts into the bounded two-network test
above, which requires both profiles already saved and Ethernet available. The
separate `Invoke-SsidBootAudit.ps1` stages Prepare/Resume across two **manual**
reboots. A durable checkpoint records boot identity and exact recovery snapshot;
the first Resume verifies saved configuration and restores, the second verifies
post-boot recovery. Same-boot, wrong-machine and completed checkpoints are rejected.
No reboot or scheduled startup task is installed. Awaiting a reboot is not a pass.
The same launcher supports `Prepare -ManualIPv4` with explicit `-ExpectedMac`,
`-ReservedIPv4`, `-SubnetMask` and `-Gateway`. It retains IPv6 metadata unchanged,
keeps the existing IPv4 DNS for the test, reconnects and verifies static runtime
before changing the durable checkpoint from Preparing to AwaitTestBoot. Resume
reads the mode and expected address from the checkpoint/recovery snapshot; it
does not accept a new address or reapply. Static verification requires exact
manual address/prefix/gateway, matching MAC, expected DNS and only the narrowly
observed active DHCP-flag projection. After restoring, a second manual reboot
is required to verify DHCP recovery. Preparing/failed/completed checkpoints
cannot be mistaken for a completed reboot test.
Both hardware reboot stages passed on 11 October 2026. The first verified saved
configuration and restored original metadata; the second verified post-boot
rollback and finalized the checkpoint as `Complete`. This covers IPv4 DNS and
IPv6 automatic mode, not manual addresses or traffic reachability.
Baseline comparisons retain original adapter IDs even if a previously
disconnected USB NIC is absent after boot; its saved values are still checked.

The SSID preflight checks machine and user policy locations. A known, valid
`NC_PersonalFirewallConfig` value alone does not block IP/DNS editing because it
controls legacy firewall behavior, not addressing. It is never modified. Other
network policy values, unknown subkeys, DNS policy and domain membership remain
blocking. Nine pure PowerShell predicate cases cover this distinction. See
[Microsoft's policy reference](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-networkconnections#nc_personalfirewallconfig).

Implementation and the bounded live DNS round trip are verified, but general
real-network correctness is not certified. Complete the above tests on a disposable Wi-Fi
test PC before relying on this experimental provider.
