# v8.0.0.5 release — verification and limitations

Published release asset: `Naufal-Windows-Utility-Setup-8.0.0.5-x64.exe`.
SHA-256: `4CEC6094AB73075CABD94143340008C7D238DDC80146726CBEB70CE161B4AF26`.
The installer is unsigned. Compare the downloaded file with the release's
attached `SHA256SUMS.txt` before running it.

## Live observations on the preceding v8.0.0.4 build

- VirtualBox guest `W11 26H2 Pro x64`; recovery snapshot `Before-NWU-Test`
  verified before mutation. The About dialog showed v8.0.0.4.
- At 1366 x 768, the workspace could be scrolled and its six sections reached.
  This is not a full display/DPI matrix pass.
- User specifically approved uninstalling Google Chrome 155.0.8059.85 in the
  guest. NWU opened the real vendor confirmation; the browsing-data deletion
  checkbox remained unchecked. After confirmation and Analyze/reload, NWU
  reported the original Chrome registration absent. No all-files-removed claim.
- Startup scan found OneDrive, SecurityHealth and VBoxTray. These entries are
  routed to dedicated controls/Windows Settings, not generic Run mutation.
  No eligible disposable current-user Run entry or sign-in behavior was tested.
- Windows Update was awaiting installation, not visibly stuck downloading.
- Windows Update Fix at 06:40:27 +07:00 failed at service stop: `sc.exe stop
  cryptsvc` exit 1051 with cryptsvc Running. BITS was already stopped; wuauserv
  stopped and was recovered to Running. No cache rename was attempted.

## Change and verification scope

The 1051 regression failed against the old workflow before implementation.
The release recognizes only a non-timeout 1051 with read-back Running for
cryptsvc. It leaves all dependents and catroot2 untouched, independently checks
BITS/wuauserv before SoftwareDistribution renames, and verifies services/backups.
Its result is PartialRepair=true, Success=false, with catalog stage SKIPPED.
Any unrelated failure or failed final read-back still produces a failed result.

The Microsoft troubleshooting guidance documents stopping update services and
renaming caches: https://support.microsoft.com/en-us/windows/deployment/updates-lifecycle/troubleshoot-problems-updating-windows
The narrow dependency fallback is an application design decision; it is not
represented as a Microsoft-certified repair or proof of online recovery.

## Local validation on 25 October 2026

- Full ProfileVerification suite: 5,129 assertions passed; no Windows settings
  changed.
- Focused repair/storage suite: 44 Windows Update workflow assertions and 89
  synthetic vendor SMART assertions passed. Services were simulated and cache
  directories disposable; these do not establish live update recovery.
- Native WinUI layout suite: 47,240 assertions, 276 layout cases and 8 flyouts
  passed. The workspace includes 20 small-window combinations and isolated
  button invocation, with no backend mutations.
- Installer icon validation: 91 assertions passed.
- Native AOT installer build completed successfully. File:
  `Naufal-Windows-Utility-Setup-8.0.0.5-x64.exe`, 39,118,720 bytes.
  SHA256: `4CEC6094AB73075CABD94143340008C7D238DDC80146726CBEB70CE161B4AF26`.
  A copy in the existing VM shared folder was verified with the same hash.

## Live v8.0.0.5 repair — 25 October 2026

After the user installed the candidate in the same VirtualBox guest, About
confirmed v8.0.0.5. Windows Update was awaiting download, not installing.
The repair started at 18:41:05 +07:00 and finished in about six seconds.
The visible report was reviewed through its final verification:

- cryptsvc stop again returned 1051 with the service Running. The operation
  continued with SoftwareDistribution only; catroot2 was explicitly SKIPPED.
- DataStore and Download were renamed to retained backups with suffixes
  `20261025-114107-f19483ed` and `20261025-114107-72dd7d79`, respectively.
- Final read-back showed BITS Start=2, wuauserv Start=3 and cryptsvc Start=2,
  all Running; both backup paths were verified present by the repair report.
- The title and final summary showed **Partial repair**, one warning, and
  explicitly stated that download completion was not verified.

This verifies the limited dependency fallback in the guest, not the full
catroot2 reset or recovery of an actually stuck download. Before this repair,
the available update was KB5127648 (26300.9711); the previously approved
KB5127694 was no longer listed. No replacement update was selected.
No host repair or automatic VM reboot was performed.

## Follow-up inspection — 27 October 2026

Read-only UI inspection of the running VirtualBox guest again confirmed
v8.0.0.5 in About. Windows Update displayed **You're up to date**, with its
last check recorded as 25 October 2026 at 20:55. Update history showed:

- `2026-10 .NET Framework Security Update (KB5127694)` — Successfully installed
  on 25 October 2026.
- `2026-10 Security Update (KB5127648) (26300.9711)` — Successfully installed
  on 23 October 2026.

These are observed Windows UI records, not an agent-observed download/install
trace. The history supplies dates without installation times. The agent did
not select/install a replacement update or initiate a reboot. Continuity of
guest state across the interruption was not independently established; these
records cannot establish that the repair caused recovery or that an originally
stuck download was reproduced and fixed. Reinstalling the already installed
KB5127694 solely to repeat the test was not attempted.

## Controlled Pause/Resume check — 27 October 2026

The user explicitly approved Pause/Resume in this VM, including updates that
could run automatically after Resume, with no automatic reboot. The observed
sequence at approximately 05:56–05:57 +07:00 was:

1. Baseline: **You're up to date**, last checked today at 05:16.
2. Selected 1 November in the pause date picker. Windows confirmed **Updates
   paused until 01/11/2026** and displayed **Resume updates**.
3. Clicked Resume once. Windows displayed **Checking for updates...** and
   removed the pause date.
4. The next observation after a 20-second wait showed **You're up to date**,
   last checked today at 05:57. No error, offered download or restart request
   was visible.

Result: this one Pause/Resume cycle passed its control/scan check; the reported
failure was **not reproduced**. No Windows Update Fix was run during this cycle,
no installation was observed and no reboot was initiated. Updates were left
resumed. The current fully updated guest cannot validate pause/resume during
an active or pending download.

## Still pending

The user clarified the original trigger on 27 October: pause Windows Update,
resume it, encounter an update failure, then invoke Windows Update Fix. The
original update error code and precise failed phase are not remembered. This
is distinct from cryptsvc error 1051 inside the repair itself. No evidence yet
shows that stale pause metadata is the cause, so pause/policy values must not
be deleted or reset speculatively.

Further reproduction needs the actual failing phase/error or a VM with a
genuine offered update. Capture that evidence before evaluating repair and
retry; the successful control/scan check above is not proof of download repair.

- A controlled reproduction of a stuck download followed by an observed
  recovery/install trace. KB5127694 installation is now confirmed in Windows
  history, but its relationship to the repair is not established.
- AI runtime and eligible startup disable/restore across sign-in.
- Real NVIDIA/other vendor uninstall coverage beyond Chrome.
- Physical Intel RST/RAID, ASM2362 and RTL9210 validation.
- No host Windows repair, uninstall, network/security setting change or reboot.
