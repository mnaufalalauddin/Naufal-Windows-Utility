# Network Settings Manager

Development preview v8.0.0.1 — updated 20 October 2026. This page documents the current
implementation, not a claim of complete enterprise networking support.

## Start here

1. Open **Network Settings Manager** in the main sidebar. A read-only scan loads
   active physical Ethernet/Wi-Fi adapters and their current IP/DNS values.
2. Select your intended adapter. Changing adapters or choosing **Refresh / discard
   edits** discards unsaved edits and loads the selected adapter's current values.
3. For Ethernet, choose **Automatic** or **Manual** independently for IP and DNS,
   per address family. Enter an IPv4 subnet mask or IPv6 prefix length, optional
   gateway, preferred DNS and optional alternate DNS.
4. Review pending changes, choose **Save to Windows**, and confirm from the local
   console. Administrator rights are required. Only edited sections are written.
5. Save verifies configuration read-back and refreshes the editor. This is not
   proof of internet access, name resolution or gateway reachability.

There is no local profile library, draft Save, or automatic activation workflow.
The internal legacy record name remains for snapshot compatibility; it does not
represent a user-managed profile. Existing library files are not loaded, migrated
or deleted by this page.

The connection card shows description, link status/speed, MAC, interface index
and category. The main fields show current values; expandable diagnostics retain
all reported addresses, gateways and DNS servers for complex configurations.
Automatic fields are read-only until Manual is selected. Unchanged preserves a
section; complex/unreadable configurations are never silently simplified.
No public-IP service is contacted. Take care when sharing addresses or snapshots.

### Ethernet DNS policy checks

DNS-only edits (IP and network category unchanged) allow one narrowly reviewed
machine policy: `NC_PersonalFirewallConfig`, DWORD 0 or 1, without other values
or subkeys. Microsoft documents it as a legacy Internet Connection Firewall
policy, not a DNS-setting restriction ([policy definition](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-networkconnections#nc_personalfirewallconfig)).
Its value is never changed. Domain membership, DNS policy keys, user network
policy keys and unknown policy content still block the operation. IP/category
edits do not receive this exception. Read failures are not treated as permission.
The guest false-positive is reproduced and the fix has mocked regression coverage.
The owner-provided VMware v4 audit subsequently passed DNS-only Apply and exact
configuration rollback. IP/gateway, IPv6, connectivity and reboot acceptance are
separate tests, not implied by that result.

## Wi-Fi — connected-SSID saving

On reviewed x64 Windows WLAN builds, selecting an active Wi-Fi adapter reads its
connected SSID, exact Windows profile identity and persisted per-SSID IP/DNS
metadata. The IPv4/IPv6 cards become editable in the main page. **Save to Windows**
writes only edited sections of that SSID's metadata after confirmation, local
policy/admin checks, a durable snapshot and fresh identity checks. No utility
profile/draft library or global adapter mutation is involved.

The form shows **stored SSID configuration**; automatically assigned addresses may
only appear in the connection card's current diagnostics. Automatic IP/DNS modes
clear that section's explicit per-SSID values. IP-only edits preserve DNS settings.
Encrypted/custom DNS metadata blocks DNS edits instead of being silently removed.

If the SSID changes, metadata changes outside the editor, or the Windows build
is not reviewed, writes stop. Failed writes attempt guarded recovery of only
attempted sections; failure/conflict is explicitly reported and snapshots remain.
Select a Wi-Fi snapshot under Recovery while connected to its original SSID.
Originally absent metadata is distinguished from an empty buffer.

This provider uses an independently inspected **private Windows ABI** with an exact
DLL fingerprint allowlist. An OS update can disable it until reviewed. Unsupported
builds and policy-managed profiles remain read-only. **Open Windows per-SSID
settings** remains available. No force-enable option or adapter-wide fallback exists.

**A bounded live IPv4 DNS Save/rollback passed on 11 October 2026.** The currently
used DNS server was persisted per-SSID and the exact originally absent metadata
was restored. Other saved SSID metadata and inspected adapter-global values stayed
unchanged. A subsequent live reconnect/two-SSID test also passed for IPv4 DNS and
IPv6 automatic mode: active DNS/DHCP/IPv6 router-advertisement state, the second
network's own DNS/default gateway, persisted metadata on return, and exact recovery
after reconnect were checked. Two manual reboots verified saved configuration
persistence and subsequent exact rollback, including active IPv4 DNS/DHCP and
IPv6 automatic/RA state. A later reserved manual IPv4/subnet/gateway round trip
passed reconnect, two-SSID isolation, Wi-Fi-bound HTTPS and rollback to DHCP.
Separate DNS/HTTPS IPv4/IPv6 traffic checks passed with automatic configuration.
Static IPv4 persistence and exact rollback/DHCP recovery across two manual reboots
also passed, completing the checkpoint on 12 October 2026. Temporary manual IPv6
Save/reconnect/HTTPS/rollback passed on the same host. Post-boot traffic remains
partial because three router IPv6 DNS queries timed out; HTTPS passed in both
families. Requested manual IPv6 address isolation passed a two-SSID round trip,
and a 13 October follow-up found no old RA addresses at 10 or 30 seconds after
switching, although two were briefly listed initially. Instantaneous isolation
during transition is not certified. Manual
IPv6 persisted across the first restart and Wi-Fi-bound HTTPS passed. Exact rollback
and automatic runtime recovery remained verified after the second restart,
including Wi-Fi-bound HTTPS. The checkpoint is Complete. Broader hardware support
remains unverified.
See [provider details, fingerprints and evidence](wifi-ssid-provider.md).

## Ethernet IP, DNS and recovery

| Section | Behavior |
| --- | --- |
| IPv4 automatic | Enables DHCP; replaces the selected adapter's manual IPv4 configuration/default gateway. |
| IPv6 automatic | Enables DHCPv6 and router discovery; does not disable the IPv6 protocol binding. |
| Manual IP | Configures one unicast address, subnet/prefix and optional default gateway for the selected family. |
| DNS | Automatic or ordered preferred/alternate servers, independently per family. Encryption/DoH is not changed. |
| Network location | Optional Ethernet Public / Private editor, under its own expander. Loaded category is not changed unless edited; domain-authenticated categories are not overridden. Included in confirmation and snapshot recovery. |
| Recovery | Stores a local snapshot before Apply. Failed Apply/read-back attempts recovery; failed recovery is explicitly reported. Manual snapshot restore is available. |

Only selected sections are changed. Configurations with multiple static addresses,
multiple default gateways or mixed manual/automatic IPv6 modes can be rejected
because this editor cannot safely reconstruct them. Use Windows advanced adapter
properties for those cases. Read failures are not interpreted as blank settings.
Domain membership and known network policy keys block local control; this is not
a comprehensive MDM/enrollment audit and is not a policy bypass.

Snapshots restore the address/DNS/category values represented by this editor,
not an exact DHCP lease, router advertisement lifetime, every advanced route
property, every Windows networking setting, or proven connectivity. A stale
snapshot can overwrite newer changes in its selected sections. Snapshots are
retained after use and contain sensitive local addresses.

Application-owned files are under the existing data directory:
`%LOCALAPPDATA%\Naufal Windows Powertoys\NetworkSettings`.
`Snapshots` contains recovery records. A legacy `profiles.json`, if present, is left untouched and is no longer used by the page.
The existing data-root name is retained for compatibility.

## Additional actions

Additional values are temporary editor input. Actions are **run individually**
and are **not** included in Save to Windows or IP/DNS rollback.
Each mutating action requires a separate confirmation.

| Panel | Implemented behavior and limits |
| --- | --- |
| Adapter / Wi-Fi | Enable/disable the selected adapter; list Windows Wi-Fi profiles and request connection to an existing profile. No Wi-Fi password extraction or new-profile credential storage. Association is asynchronous. |
| Printer | List installed printers and set/read back a default printer for the execution account. Windows automatic printer management can subsequently override it. |
| Computer / workgroup | Request a local computer rename or workgroup change. Requires a manual reboot and later verification. Domain-managed PCs are blocked. No automatic reboot. |
| Network drive | Map an unused D:–Z: to a UNC share using existing Windows credentials; verify the mapping. Elevated and normal Explorer logon contexts can differ. Existing drive mappings are not replaced. |
| WINS | Ethernet only: set preferred/alternate IPv4 WINS addresses; report the provider return code. Verify in advanced Windows TCP/IP properties. |
| Static route | Ethernet only: add a non-default persistent route without replacing an existing destination. Read back stored route; no connectivity certification or automatic route rollback. |
| Hosts | Update/remove only an application-marked block, preserving other content. Full original backup is retained beside `hosts`. Manual recovery is required; no DNS-cache flush. |
| Script / program | Run an explicitly confirmed local `.exe` or `.ps1` after displaying/rechecking its SHA-256. Uses the utility's privileges; default PowerShell execution policy is retained. No arbitrary argument field, UNC program, automatic execution or rollback of script effects. |
| SMTP | Enter a hostname/port and test a bounded TCP connection. Does not send mail, store credentials, verify SMTP/TLS/authentication, run a relay, redirect mail clients or rewrite their settings. |
| Windows tools | Open Wi-Fi, printers, adapter properties, workgroup/computer properties, drives, Device Manager, proxy, VPN and firewall interfaces. These are Windows pages, not bundled replacements for their full functionality. |

Program/script execution can modify anything permitted by its account. A hash
identifies a file; it is not a signature or malware assessment. Only run trusted
local files and do not treat the network snapshot as protection from script effects.
A two-minute timeout terminates the started program process tree: use short tasks,
not interactive editors or long-running installers.

## Validation status

- Pure validation, serialization, temporary-file storage and simulated
  Apply/read-back/recovery success/failure tests are provided in `Tests/NetworkSettings`.
- The physical-adapter inventory was exercised read-only on the development PC.
- Native WinUI layout/callback tests use synthetic adapters and mock callbacks,
  covering Light/Dark, narrow/wide windows and increased text scaling.
- Embedded PowerShell scripts are syntax-checked. These checks do not substitute
  for live Windows mutation tests.
- **Bounded host acceptance passed on 11 October 2026:** per-SSID IPv4 DNS and
  IPv6 automatic mode, reconnect/two-SSID switching, and persistence/recovery
  across two manual reboots. Active DNS/DHCP/IPv6 RA configuration was checked;
  exact original metadata was restored. Inspected adapter-global values and
  other saved-profile metadata remained unchanged. The boot checkpoint is Complete.
- **Manual IPv4 and traffic acceptance:** a user-reserved IPv4, subnet mask and
  gateway became effective after reconnect. Switching SSIDs recovered the other
  network's DHCP address/gateway/DNS; returning reactivated the saved manual
  configuration. Wi-Fi-bound HTTPS succeeded before and after exact rollback.
  Windows changed only the inspected active Wi-Fi IPv4 EnableDHCP flag from 1 to
  0 while static addressing was active; it returned to 1 on the other SSID and
  after rollback. No global setter was used. Separate automatic-mode traffic
  checks passed 8 DNS queries and 4 certificate-validated HTTPS requests bound
  to Wi-Fi across IPv4/IPv6. This is bounded endpoint coverage, not all traffic.
- **Static IPv4 reboot acceptance:** manual address/prefix/gateway/DNS persisted
  after the first reboot; original metadata was restored and DHCP recovery was
  verified after a distinct second boot. The checkpoint completed on 12 October
  2026 with exit 0. Inspected global values and other saved-profile metadata
  matched the original baseline. This audit checks configuration, not post-boot traffic.
- **Manual IPv6 acceptance, 12 October:** temporarily reused the current assigned
  address with explicit user consent. Exact manual address/prefix/gateway/DNS
  became effective after reconnect, with DHCPv6 disabled and router discovery
  retained by Windows. Wi-Fi-bound IPv6 HTTPS returned HTTP 200 with certificate
  validation. Exact metadata/global/other-profile rollback restored automatic
  DHCP/RA and HTTPS passed again. The test does not establish a permanent address
  allocation or certify DNS routing while manual; its HTTPS hostname resolution
  uses the system resolver.
- **Post-boot traffic, 12 October:** restored automatic configuration passed four
  IPv4 DNS queries and four IPv4/IPv6 HTTPS requests bound to Wi-Fi. Three of four
  queries through the router's IPv6 DNS resolver timed out: 9/12 checks passed,
  overall incomplete. This was before the manual IPv6 test; no DNS setting was
  changed to conceal the failure.
- **Manual IPv6 two-SSID test, 12 October:** the requested manual address was
  absent on the second network, which recovered its own automatic modes,
  gateway/DNS and inspected global baseline. Returning to the original SSID
  restored the manual address and Wi-Fi-bound HTTPS. Two RA-learned privacy
  addresses remained listed on the second network; all-address isolation and
  hotspot IPv6 Internet service are not certified. Windows retained DHCPv6 on
  return despite the exact Manual/Preferred address. The first user-initiated
  reboot at 20:48 WIB retained exact manual IPv6/prefix/gateway/DNS; Wi-Fi-bound
  HTTPS passed with certificate validation and HTTP 200. Exact original metadata,
  inspected global/other-profile state and automatic runtime were restored.
  The second boot at 20:58:20 WIB retained original metadata, automatic runtime
  and inspected global/other-profile state; Wi-Fi-bound HTTPS again returned
  HTTP 200 with valid TLS. Checkpoint `Complete`, exit 0, at 21:00:11 WIB.
  The temporary earlier checkpoint was cancelled with
  verified original-metadata/automatic-runtime recovery.
- **RA settling and DNS follow-up, 13 October:** repeating the manual IPv6
  two-SSID round trip found two old RA privacy addresses immediately after
  switching, but zero at both the 10- and 30-second checks. The requested manual
  address was absent on the hotspot. Returning to the original SSID and exact
  rollback both passed Wi-Fi-bound HTTPS; audit exit 0. No RA/privacy disabling
  or address deletion was used. The harness now refuses a pass if old addresses
  remain after the settling observation, even when a connection attempt fails.
  A separate Wi-Fi packet capture saw six router IPv6 DNS queries leave and only
  two replies, with zero capture drops reported. With explicit consent, both
  Cloudflare IPv6 DNS addresses passed eight queries before configuration changes
  and another eight after a DNS-only per-SSID Save/reconnect. Exact metadata,
  original router DNS, automatic addressing and inspected global/other-profile
  values were restored; audit exit 0. That temporary test retained no alternative DNS.
  This demonstrates a working alternative, not a router/ISP repair or a guarantee
  of future resolver availability. With separate explicit owner authorization,
  a subsequent DNS-only operation retained the alternative for the tested SSID.
  Eight DNS queries passed after reconnect, followed by all 12 combined
  Wi-Fi-bound IPv4/IPv6 DNS and HTTPS checks. Original recovery snapshot retained;
  addressing, inspected global values and other SSID metadata were unchanged.
  This host-specific choice is not applied automatically by the app.
- **Still unverified:** instantaneous IPv6 address isolation during SSID transition,
  repaired/stable router IPv6 DNS,
  broader hardware/router
  combinations, additional-action mutations, SMTP servers and program execution
  on a real network. Test with recovery access before relying on the preview;
  bounded configuration checks are not universal connectivity certification.

## Windows platform references

For remaining live tests and required equipment, see
[the acceptance checklist](network-acceptance-status.md). Test infrastructure and
passing fixtures are not a substitute for the listed live evidence.

- [Set-NetIPInterface](https://learn.microsoft.com/en-us/powershell/module/nettcpip/set-netipinterface)
- [Set-DnsClientServerAddress](https://learn.microsoft.com/en-us/powershell/module/dnsclient/set-dnsclientserveraddress)
- [Netsh interface commands](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/netsh-interface)
- [Windows per-network IP settings](https://support.microsoft.com/en-us/windows/experience/connectivity-networking/essential-network-settings-and-tasks-in-windows)
- [Windows Settings URI reference](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings)
- [Read saved WLAN profiles](https://learn.microsoft.com/en-us/windows/win32/api/wlanapi/nf-wlanapi-wlangetprofilelist)
