# v8.0.0.3 local fix candidate — 23 October 2026

Not published to GitHub and not installed on the host by this task.

## Completed checks

- 5,102 backend regression assertions; isolated temporary-file tests required
  execution outside the filesystem sandbox to test atomic file replacement.
- 47,180 native WinUI assertions, 276 layout cases and eight flyouts.
- Focused run: 1,438 overlapping small-window assertions. Tool windows cover
  1366x700, 1280x640, 1024x550 and 800x480 at 100/150/200% text. Workspace tests
  cover 480x640, 800x480, 1024x550, 1280x640 and 1366x700 in two themes at
  100/200% text. Last-action bounds and synthetic button invocation are checked.
- Long confirmation scrolling also tested down to 480x320 at 200% text.
- 391 English UI/resource assertions, 16 installer-location assertions,
  10 publish-integrity assertions and 91 branding assertions.
- Native AOT x64 publish and Inno Setup compilation completed; full staged
  file inventory, installer checksum and application/setup versions verified.
- Read-only local PhysX/DDU registered-command resolution passed; the existing
  NVIDIA InstallerCore DLL reported a valid NVIDIA Authenticode signature.
  No registered uninstaller was launched.

## Local artifact

`artifacts/installer/Naufal-Windows-Utility-Setup-8.0.0.3-x64.exe`

- Version: 8.0.0.3
- Size: 39,099,731 bytes
- SHA256: `70E4D26CF4A6FDB4E4E0FACC1CEA8602E2CF4AABCA2B04788D0BCDAE7C7C80E4`
- Authenticode: unsigned

## Limits

These are code, synthetic UI and packaging checks, not a live uninstall or
installation/upgrade acceptance pass. No host startup/policy values were changed.
AI runtime effectiveness, actual vendor uninstall, sign-in behavior and other
workspace mutations still require controlled acceptance. Window-size/text-scale
tests do not establish coverage of every physical display or Windows DPI setting.
Existing unrelated VM, storage hardware and Windows Update acceptance limits
remain unchanged.
