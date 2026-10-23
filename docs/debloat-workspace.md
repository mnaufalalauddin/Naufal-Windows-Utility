# De-bloat workspace (v8.0.0.2 preview)

Open **Advanced Windows Tweaks → De-bloat workspace**. Six expandable panels
stay inside the main window. Scan explicitly; scans do not uninstall apps or
change startup entries. Lists scroll vertically and action labels wrap.

## Available controls and boundaries

| Panel | Implemented behavior | Important boundary |
| --- | --- | --- |
| Individual Windows AI controls | Copilot current-account uninstall/Store recovery; separate disable/restore policies for Recall snapshots, Click to Do, Notepad AI and Paint AI | Not a promise of runtime effectiveness. Build/edition and applicable app versions are checked. Existing combined AI/Paint snapshots must be restored before using individual policies; the reverse is also enforced. Recall snapshot data is not backed up. |
| Desktop / OEM apps | Search registered desktop apps by name/publisher/identity; display version, location, scope and registered uninstaller; launch that uninstaller after confirmation and fresh registration validation | No automatic “bloat” classification. Metadata is not a trust verdict. System components and ambiguous, remote or script-host executables are not launched. Windows Installed apps is the fallback. A launched installer is not an uninstall pass; reload checks whether its registration remains. |
| Startup Apps Manager | Inspect Run registrations; disable current-account entries and restore their exact original registration | Machine-wide entries, Windows-disabled approvals, startup folders and packaged tasks use Windows Settings. Existing OneDrive/Edge controls retain ownership. No process/service termination. Restore requires the registered local executable to exist. |
| Per-app background controls | Inspect package capabilities; select eligible background-task apps and apply a machine-wide per-package deny list; restore the original list | Applies to all accounts, not Win32/full-trust processes. Existing policies are not overridden. Notifications/sync/alarms can stop. Restore the saved selection before choosing a different one. Runtime behavior needs an app restart and separate testing. |
| Reinstalled app detection | Compare current-account inventory with verified removal history; intentional verified restores clear the removal marker | History begins with successful Built-in Windows Apps/Copilot operations recorded by this version. It does not infer old removals or the cause of a reinstall. Deferred/failed inventories remain unverified. Never automatically removes returned apps. |
| Provisioned apps | Inventory Windows' online provisioning catalog; explicitly deprovision exact allowlisted package families for new accounts | Existing account installations are separate. Frameworks/system packages outside the allowlist are read-only. No automatic re-provisioning or package-payload backup. Have system recovery media/VM snapshot before proceeding. |

## Recovery and safety

- New policy/startup snapshots are stored under the utility's per-user application
  data, `Backups/DebloatWorkspace`. Snapshots are persisted before mutation.
- Registry value type, missing values, full target identity and read-back matter.
  External drift blocks restoration instead of overwriting another actor's change.
  Partial failures retain snapshots. No snapshot means no guessed default restore.
- AI, background and provisioning operations require administrator privileges and
  a confirmed unmanaged-device result. Organization/unknown management is blocked.
- App-removal history and provisioning logs are under the utility's `Logs` folder.
  A log is **not** an app-data or package-payload backup. Do not publish personal
  inventories or raw evidence without reviewing them.
- No host uninstall, startup change, policy change or deprovisioning was performed
  as part of implementing this feature. Synthetic tests are not live acceptance.

## Microsoft references

- [Windows AI policies](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-windowsai)
- [Notepad AI policy and app version requirements](https://learn.microsoft.com/en-us/windows/client-management/manage-notepad)
- [Per-app background policies](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-privacy#letappsruninbackground_forcedenytheseapps)
- [Background app limitations](https://support.microsoft.com/en-us/windows/privacy/windows-background-apps-and-your-privacy)
- [Windows Settings entry points](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings)
- [Provisioned package removal](https://learn.microsoft.com/en-us/powershell/module/dism/remove-appxprovisionedpackage)

## Local verification — 23 October 2026

- Release x64 Native AOT publish completed successfully. Packaging and release
  details are recorded in [v8.0.0.2 release notes](release-8.0.0.2.md).
  No installer was run on the host as part of feature verification.
- Backend suite: **5,060** assertions passed, including simulated journal failures,
  partial writes, external drift, exact restore, verified app history, provisioned
  identity validation, servicing timeout/failure and post-operation read-back.
- Native WinUI test host: **46,591** assertions passed across the existing UI suite.
  The new workspace was tested in **12** width/theme/text-scale combinations,
  including 200% text, full-label button height, keyboard scrolling and reaching
  the last inventory card. Its inventory is synthetic and mutation backends are
  not linked into that test host.
- English-only UI/resource/navigation suite: **387** assertions passed.
- Tests and evidence do not establish real uninstall, policy effectiveness,
  sign-in behavior or new-account provisioning acceptance.

## Acceptance still required

In a disposable Windows VM, verify each supported policy after restarting its app,
startup disable/restore across sign-in, background notifications before/after,
one vendor uninstaller, reappearance after a genuine app reinstall, and provisioning
with a newly created test account. Confirm snapshot restore and unsupported/managed
device behavior. These checks must not be represented as passed from build/test
output alone.
