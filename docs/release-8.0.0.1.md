# Naufal Windows Utility v8.0.0.1 — Windows Debloat, Network & Repair Preview

20 October 2026 · **Development pre-release** · Windows 10 / 11 x64 targets

## What's included

- Built-in Windows Apps: Installed / Not installed / Not verified filters,
  alphabetical lists, counts and visible-only selection.
- Essential, Gaming and Advanced Windows Tweaks & De-Bloat: analyzed ON / OFF /
  partial / unavailable / not verified filters. Hidden selections are cleared;
  changing filters never applies a tweak.
- Windows Update Fix: verified stop/restart of BITS, wuauserv and cryptsvc,
  preserved DataStore/Download/catroot2 backups, diagnostic errors and recovery
  after interrupted operations. Protected UsoSvc/DoSvc are not force-stopped.
- Network Settings Manager: direct Ethernet and connected-SSID Wi-Fi IP/DNS
  editing with validation, read-back and recovery snapshots. Wi-Fi support is
  build-limited; global adapter defaults are not substituted for per-SSID edits.
- Ethernet DNS-only policy false-positive correction with conservative gates
  retained for other policy contents, domains and IP/category changes.
- Intel GPU catalog request-header fix, shared GPU action header and driver
  channels retained. No driver installs automatically after an update check.
- Version raised to **8.0.0.1** in application and package/executable manifests.
  README, CHANGELOG and acceptance documentation refreshed.

## Download

- `Naufal-Windows-Utility-Setup-8.0.0.1-x64.exe` — **38,932,824 bytes**.
- `SHA256SUMS.txt` — checksum for this installer.
- `CHANGELOG.md` — engineering history and verification boundaries.
- GitHub's automatic source archives contain source, not an installed app.

Installer SHA-256:

```text
608A82DB10355EE024C6DA53A0109A43CE96BA8236021B19D3DCA39731BDEF92
```

The installer is **unsigned**. Verify its origin and checksum, retain backups,
and review confirmations before changing Windows. Do not disable protection to
run it. Previous releases remain intact; this preview does not replace the
previous stable release designation.

## Verification

Fresh Native AOT / Inno Setup packaging completed for v8.0.0.1. Automated checks:

- 4,984 backend regression assertions.
- 276 network workflow assertions, plus SSID codec/identity and policy fixtures.
- 375 English UI/localization assertions; 163 GPU metadata/service assertions.
- 33 project identity, 16 installer-location and 10 publish-stage assertions.
- Full publish-stage file inventory/hashes and numeric executable versions checked.

The focused repair/storage runner contains 29 simulated Windows Update workflow
assertions and 89 synthetic vendor SMART assertions, also covered by the full suite.
These counts are not additive evidence of physical hardware compatibility.

The owner-operated **15 October** VMware test of the preceding local build
completed all six Windows Update cache/service stages with zero warnings;
service runtime/startup read-back and retained backups were verified. The same
repair source is included here. That log does **not** prove that a previously
stuck online download finished. No new live Windows repair was run for this package.

Earlier scoped network tests verified per-SSID DNS after a new boot (12/12 bound
DNS/HTTPS checks and 8/8 resolver queries), and VMware Ethernet DNS Apply/rollback.
They do not certify every adapter, Windows build, IP/gateway edit or DNS setup.

## Known limits

- Intel RST / ASMedia ASM2362 / Realtek RTL9210 paths remain **fixture-tested,
  not physically verified**. No supported test hardware was available; RAID
  array health and hidden-member enumeration are not inferred.
- Online stuck-download recovery, broader live network/additional-action tests,
  and installation/upgrade/uninstall coverage remain incomplete.
- Original router IPv6 DNS timeouts are not repaired by using another resolver.
- Complex/encrypted DNS remains guarded; SMTP is a bounded connectivity check,
  not a certified full mail configuration workflow.

[README](https://github.com/mnaufalalauddin/Naufal-Windows-Utility#readme) ·
[CHANGELOG](https://github.com/mnaufalalauddin/Naufal-Windows-Utility/blob/v8.0.0.1/CHANGELOG.md) ·
[Verification details](https://github.com/mnaufalalauddin/Naufal-Windows-Utility/blob/v8.0.0.1/docs/verification-20261015.md)
