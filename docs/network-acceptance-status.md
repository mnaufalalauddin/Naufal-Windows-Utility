# Remaining acceptance work — 20 October 2026

This is an evidence checklist, not a claim that unavailable features were fixed.
It distinguishes observed faults, deliberate support limits, and untested paths.

| Item | Current evidence | Required next step |
| --- | --- | --- |
| Wi-Fi IPv4/IPv6 manual and rollback | Live reconnect, two-SSID switching and separate reboot audits passed on the development host. | Broader device/build coverage; no universal-support claim. |
| Old RA privacy addresses | Two addresses initially listed after roaming; none at 10 or 30 seconds in the follow-up. | Instantaneous transition isolation is not certified; never delete unrelated addresses to manufacture a pass. |
| Router IPv6 DNS timeouts | Inconsistent replies reproduced by bound probes and packet capture. | Router-side diagnosis requires router access/owner coordination. A working alternate resolver is not router repair. |
| Alternative DNS retained per SSID | New-boot verification completed on 15 October: 12/12 bound DNS/HTTPS checks and 8/8 IPv6 resolver queries; configuration unchanged by the check. | Broader device/build coverage; does not repair the original router DNS. |
| Wi-Fi UI status text | Blanket "not hardware-tested" wording removed; build limits and Ethernet acceptance gap retained. | Native UI suite passed 45,930 assertions after this edit. |
| Ethernet / additional action mutations | Owner-provided VMware v4 DNS-only Apply and exact configuration rollback passed after the narrow firewall-policy correction; 39 mocked policy cases also pass. | Other Ethernet mutations, printer, workgroup, drive, WINS, route, hosts and program tests still need separate acceptance. These actions are not silently run on the host. |
| Windows Update stuck download | Reviewed guest log on 15 October: all six cache/service repair stages passed, zero warnings; 29 focused simulated workflow assertions also pass. | Verify a formerly stuck online download actually completes; local repair success alone does not prove that. |
| RAID and USB bridges | Targeted protocol/decoder fixtures, not matching-device acceptance. | Supply/detect matching controller hardware. The current host scan found two internal NVMe drives, not evidence of a supported bridge/RAID path. |
| Broader Windows Wi-Fi builds | Private-ABI implementation uses exact reviewed binary hashes. | Review new binaries and test matching hosts before extending allowlist; do not bypass it. |
| Encrypted DNS and complex configurations | Preserved/rejected rather than silently rewritten. | New implementation and targeted tests required; existing guards are not bugs to disable. |
| SMTP | Bounded TCP connectivity check only. | Specify intended SMTP workflow and test server before implementing/testing protocol, TLS/authentication or mail sending; no full mail-client configuration claim. |

## Recorded execution limits

- On 15 October the authorized VMware Windows guest was running and snapshot metadata named
  `Before-NWU-WiFi-Test`. VM command access reports a required encryption password.
  Snapshot readability does **not** prove guest access or safe rollback execution.
  The owner has opened the guest desktop and is running reviewed guest commands;
  opening the desktop does not necessarily unlock vmrun. Do not transmit
  passwords in chat, disable encryption, or revert a snapshot without approval.
- The retained-DNS checkpoint has a separate **VerifiedAfterBoot** completion
  record from 15 October. The original checkpoint is intentionally unchanged.
  The hourly monitor was deleted after verification; no further reboot is requested.
- SMTP endpoints and matching RAID/USB bridge equipment have not been identified.

The default backend regression suite passed 4,984 assertions with no Windows
settings changed. These results do not resolve the blocked acceptance rows above.
