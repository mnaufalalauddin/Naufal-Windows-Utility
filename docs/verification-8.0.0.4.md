# v8.0.0.4 audit — 24 October 2026

Local candidate, not published. Existing v8.0.0.3 work is preserved.

## Evidence

- Backend: 5,114 regression assertions passed. A new malformed-later-value test
  failed before the transaction fix and passed afterward. Added concurrent-save
  drift, case-insensitive duplicate targets, malformed multi-string values,
  queued progress and unavailable/failed/completed outcome checks.
- Native WinUI: 47,240 assertions, 276 layout cases and eight flyouts passed.
  Added navigation from a scrolled long page into another long page; the new
  page must start at the top. Existing small-window/workspace tests also passed.
- GPU metadata/service: 163 assertions, synthetic catalogs, no downloads.
- Network: 276 total policy/workflow assertions (simulated stores and temporary
  files; subnet/DNS/SSID tests did not change a live adapter).
- LSA: 54 assertions, fake providers and test-owned snapshot files.
- Historical security/offline: 105 assertions, fake providers/DISM.
- Historical servicing: 33 assertions, fake commands.
- English UI/resources: 394 assertions.
- Static wiring: 27 simplified-interface, 92 catalog routing, 26 monitoring and
  16 report-export checks. Static checks are not live clicks or export acceptance.
- Additional checks: 18 AppData routing, 13 OneDrive/Game Mode/runtime wiring,
  33 project identity, 16 installer-location, 10 publish-stage integrity,
  22 pure VM guards and 91 branding assertions passed. No VM was started.

Test suites that require atomic replacement of temporary files ran with host
filesystem permissions. No live audit flags, uninstall, policy mutation, network
Apply/Restore, reboot or Windows Update repair was executed.

## Local build

Native AOT x64 publishing and installer compilation completed. Staged files,
application/setup versions and installer checksum were verified after compilation.
The installer was not executed.

- File: `artifacts/installer/Naufal-Windows-Utility-Setup-8.0.0.4-x64.exe`
- Size: 39,110,382 bytes
- SHA256: `7C83537D2B2E16F307C266AC61E3CABB29DBA5F9189BFD94556FE54E4946292A`
- Authenticode: unsigned
- GitHub: not published by this task

## Still not certified

Passing these checks does not prove the entire application is bug-free. Actual
vendor uninstall, AI runtime behavior, startup across sign-in, background app
behavior and provisioning for a new account still need disposable-VM acceptance.
The real-app startup check was corrected but was not run during this audit.
Windows Update stuck-download recovery and physical Intel RST/ASM2362/RTL9210
behavior remain separate unverified cases. No online vendor-catalog freshness
or every physical display/DPI combination is claimed.

Registry drift is checked immediately before writes, but Windows registry edits
are not atomic across third-party processes. A partial failure retains the
original recovery record and requires review rather than blind retries.
