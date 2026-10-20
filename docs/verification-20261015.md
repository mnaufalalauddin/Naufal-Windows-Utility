# October 15 preview: verification boundaries

Date: 15 October 2026, Asia/Jakarta. These are scoped results, not a claim that
every supported Windows build, controller or network has passed.

| Area | Evidence | Still required |
| --- | --- | --- |
| Windows Update Fix | Owner-operated Windows 11 Pro build 26300 VMware guest; screenshot and full app log reviewed; six stages completed, zero warnings at 21:46 WIB | A documented stuck download before repair followed by successful download after repair; live failure/recovery cases on additional builds |
| Intel RST / ASM2362 / RTL9210 | 89 synthetic vendor SMART assertions; exact transport identity, read-only packet construction, truncation/completion errors and member identity checks | Matching physical controller, driver and firmware; comparison of native device identity and SMART data. No matching hardware found in the development inventory |
| Wi-Fi DNS after reboot | New boot and intended SSID verified; 12/12 bound DNS/HTTPS checks plus 8/8 direct Cloudflare IPv6 DNS queries; configuration retained and unchanged by the check | Other devices/builds, complex DNS and repair of the original router DNS service |
| Ethernet DNS | Owner-provided VMware v4 audit: automatic to manual using the same DNS server and exact configuration rollback passed | Live static IP/gateway, IPv6, internet and reboot matrix |
| Installer | Native AOT/Inno build and checksum verified; owner reports installation in the VM and the launched app was observed | Independent installation/upgrade/uninstall matrix and signing certificate; installer remains unsigned |

## Windows Update guest result

The production Windows Update Fix ran at **2026-10-15 21:46:11 +07:00**.
The reviewed log confirms:

- BITS Start=2, wuauserv Start=3 and cryptsvc Start=2 preserved.
- All three stop operations returned exit 0 and reached Stopped.
- DataStore, Download and catroot2 renamed to distinct retained backup paths.
- All three start operations returned exit 0 and reached Running.
- Final startup/runtime read-back and existence of every backup verified.
- `REPAIR COMPLETED`, `Warnings: 0`. No automatic reboot, service ACL reset,
  force-kill, network/proxy/firewall reset or UsoSvc/DoSvc mutation.

The log explicitly says download completion was not tested. A screenshot of
completed repair alone cannot establish recovery of a stuck online download.
Keep the backups; do not delete them or reset networking just to obtain a pass.
Raw machine logs, VM images and personal network evidence are not release assets.

## Reproduce the non-mutating focused checks

```powershell
dotnet run --project Tests/ProfileVerification/ProfileVerification.Tests.csproj -c Release -- --repair-storage-regression
```

This exercises 29 repair assertions using simulated services and disposable cache
folders, plus 89 synthetic vendor SMART assertions. It does not change real
Windows services, initiate an update download, probe hardware or certify a RAID
array. The full suite remains separate.

## Physical storage acceptance

Use an actual supported bridge/controller, not a VMware virtual disk presented
under a similar model name. Record VID/PID or Intel PCI ancestry, driver/firmware,
device identity and the app's read-only report. Compare identity and available
SMART values against the device vendor's read-only diagnostics. Redact serials
before sharing. Unsupported or ambiguous data must remain Unknown.

Do not switch SATA/RAID modes, dismantle arrays, flash firmware, start destructive
tests or alter bridge modes for this test. A successfully decoded member is not
proof of aggregate RAID health. See [Disk Info](disk-info.md) for exact scope.
