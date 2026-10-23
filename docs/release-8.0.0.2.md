# Naufal Windows Utility v8.0.0.2 — De-bloat Workspace Preview

23 October 2026 · **Development pre-release** · Windows 10 / 11 x64 targets

## Six new inline panels

Open **Advanced Windows Tweaks → De-bloat workspace**. Scan explicitly, review
each warning, and confirm individual changes. No automatic debloating is added.

1. **Individual Windows AI controls:** Copilot current-account removal/recovery;
   separate supported policies for Recall, Click to Do, Notepad AI and Paint AI.
   Combined AI/Paint controls retain mutual snapshot-ownership checks.
2. **Desktop / OEM apps:** searchable registered apps, publisher/version/scope
   details and confirmed launch of validated registered vendor uninstallers.
   Reload checks registration; launching an uninstaller is not proof of removal.
3. **Startup Apps Manager:** current-account Run entries with exact snapshot
   restore. Other entry types and unsupported approvals use Windows Settings.
4. **Per-app background controls:** supported packaged background-task apps only;
   machine-wide deny policy affects all Windows accounts. Not a Win32 process
   killer. Existing policy ownership is not overridden; notifications may stop.
5. **Reinstalled-app detection:** compares current inventory with this version's
   verified Built-in Windows Apps/Copilot removal history. Intentional restores
   clear the marker. No inference about why an app returned, and no auto-removal.
6. **Provisioned apps for new accounts:** explicit exact-identity removal of
   allowlisted packages. Existing accounts are separate. There is no automatic
   re-provisioning or package-payload backup; prepare VM/system recovery first.

Inline lists support scrolling and wrapped action labels. Registry transactions
retain typed originals before mutation, verify read-back and reject external drift.
Managed/unknown-management devices remain blocked for new policy/provisioning edits.

## Download and verification

- `Naufal-Windows-Utility-Setup-8.0.0.2-x64.exe`: self-contained x64 installer,
  **39,120,265 bytes**.
- `SHA256SUMS.txt`: compare this with the downloaded installer before running it.
- `CHANGELOG.md`: engineering history and verification boundaries.
- GitHub's automatic source archives contain source, not an installed app.

Installer SHA-256:

```text
5DE92DF3F6F96285291AAEB063E3570E9615B1D4E5C28FB655ABF895D07C5B37
```

Native AOT publish and Inno Setup 7.1.0 packaging completed successfully.
The complete staged file inventory and hashes were verified before publication;
application and installer numeric versions are both 8.0.0.2.

The installer is **unsigned**. Do not disable protection to run it. Earlier
release assets and tags are preserved; this pre-release does not replace the
stable release designation.

## Automated evidence — 23 October 2026

- **5,060** backend regression assertions and **387** English UI/resource checks.
- **46,591** native WinUI assertions from the isolated backend-free UI test host;
  the new workspace covers **12** width/theme/text-scale combinations, long labels,
  keyboard scrolling and last-card reachability, including 200% text scaling.
- **33** project identity, **16** installer-location and **10** publish-stage checks.
- **91** branding/icon checks against the staged Native AOT application.
- These are build/synthetic checks, not real uninstall or policy-effectiveness tests.
  No host app removal, startup modification, policy edit or provisioning change
  was performed to produce this release.

## Known limits / acceptance still pending

- Live disposable-VM acceptance of each new feature: AI after app restart, startup
  across sign-in, background notifications, vendor uninstall, genuine reinstall
  detection, new-account provisioning and snapshot restoration.
- Full installer installation/upgrade/uninstall matrix and signing.
- Windows Update Fix's previous guest evidence covers six local service/cache
  stages with zero warnings, **not** completion of a formerly stuck online download.
- Intel RST / ASMedia ASM2362 / Realtek RTL9210 remain fixture-tested, not
  physically verified. No RAID/bridge hardware certification is claimed.
- Existing guarded per-SSID/network scope, complex/encrypted DNS limitations and
  broader additional-action acceptance remain unchanged. Router DNS timeouts are
  not repaired by switching to another resolver.
- No guaranteed performance gain or blanket “safe debloat” claim.

[Feature boundaries and recovery](https://github.com/mnaufalalauddin/Naufal-Windows-Utility/blob/v8.0.0.2/docs/debloat-workspace.md) ·
[CHANGELOG](https://github.com/mnaufalalauddin/Naufal-Windows-Utility/blob/v8.0.0.2/CHANGELOG.md) ·
[Earlier verification matrix](https://github.com/mnaufalalauddin/Naufal-Windows-Utility/blob/v8.0.0.2/docs/verification-20261015.md)
