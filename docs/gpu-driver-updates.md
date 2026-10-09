# GPU driver release checks

Added in the 7 October 2026 source revision, application v8.0.0.0, and included in
the [8 October development preview](https://github.com/mnaufalalauddin/Naufal-Windows-Utility/releases/tag/v8.0.0.0-build.20261008).
The historical 2 October preview does not contain this addition.
The [9 October preview](https://github.com/mnaufalalauddin/Naufal-Windows-Utility/releases/tag/v8.0.0.0-build.20261009)
also includes the Intel HTTP negotiation fix described below.

## Using the panel

Open **Advanced Windows Tweaks → GPU Driver Manager**. After the existing device
inventory loads, the selected GPU is checked online. The result appears inside
the same window, below the installed-driver inventory and device details.

The fixed header groups **Check for updates**, **Repair driver** and **Download &
install** in equal responsive columns, below the **Driver type** selector. Labels
wrap at high text scaling; metadata scrolls without moving the action header.
NVIDIA's installed version is shown in its public form (for example **617.14**).
The Windows INF version is retained for backend verification and diagnostic logs.
Intel's four-part published versions remain intact; AMD's Adrenalin version is
not guessed by truncating its unrelated INF version.

- **New GPU driver available:** the selected catalog's comparable version is
  newer than the installed driver. Installation is still a separate action.
- **Driver is up to date for this catalog:** the versions match. This is not a
  compatibility certification or a statement about every vendor release channel.
- **Installed driver is newer than this catalog:** no downgrade is suggested.
- **Release found — version comparison unavailable:** release metadata was
  found, but the package version and installed Windows version cannot be matched
  unambiguously. Review the official source and OEM support guidance.
- **Unable to verify the latest driver:** no verified result is available, for
  example because of a blocked request, offline connection, changed page, timeout,
  or unsupported model. This never becomes an "up to date" result.

**Check for updates** performs a fresh request. Results are cached only for the
current manager window and GPU, with distinct releases for each channel;
refreshing the inventory clears them. The selected channel is remembered per GPU
within that window. If it disappears from a refreshed catalog, no replacement is
selected silently: choose another verified type explicitly. Switching
GPUs cancels the previous request, and closing the window cancels pending work.
A late response cannot replace the newly selected GPU's result. There is no
scheduled background check or notification while the manager is closed.

**Download & install** and **Repair driver** both display the selected type and
version in their confirmation and revalidate the same channel before downloading.
A changed release, withdrawn channel, unverified GPU/OS or newer installed version
blocks the operation before binary download. Repair reinstalls the selected
catalog release and can update the version or switch the channel; it does not
claim same-version repair unless that selected version is already installed.
The selected type is a catalog choice, not detection of the currently installed
driver type. An identical numeric version does not establish which channel is
installed. No substitute-channel, Auto-Detect or generated-URL fallback is used.

## Release date versus release time

The panel separates **Latest catalog version**, **Release date**, **Release
time**, **Channel**, **Source**, and **Checked at**. Installed Windows driver
version and driver date remain in the inventory; the INF driver date is not the
vendor's public release date.

Only a clock explicitly present in release metadata is displayed as a release
time. An explicit UTC offset is preserved; a clock with no timezone is labelled
accordingly. Most vendor metadata exposes only a date: the time then reads
**Not published by vendor**. Failed checks show **Not verified** instead. Page
publication timestamps, HTTP Last-Modified and the PC's check time are never used
as substitutes for the release time. **Checked at** is the local PC time and
offset, clearly labelled as not the release time.

## Current catalog coverage

| Vendor | Scope and comparison |
| --- | --- |
| NVIDIA | Exact model mapping followed by returned product-list and OS verification. **Game Ready** and **Studio** are queried independently for GeForce/TITAN models. **NVIDIA RTX Driver / Enterprise Production Branch** is queried for workstation RTX/Quadro models, not consumer GeForce RTX. Only active non-beta matching releases with official direct packages are offered. Converts the installed Windows version to the comparable NVIDIA display version. |
| AMD | Named desktop **Radeon RX 5000 / 6000 / 7000 / 9000** products whose exact model and OS section are present on the official product page. Offers the newest full **Recommended** and **Optional** Adrenalin WHQL package independently; Recommended is the initial selection. Auto-Detect utility versions are excluded. Comparison uses Driver Store versions from matching release notes, never the Adrenalin package number. Multiple hardware branches can be ambiguous. Notebook/APU/OEM/PRO/legacy models without an exact mapping require manual checking; universal PRO support is not claimed. |
| Intel | Identified Arc/Core Ultra, 11th–14th Gen processor graphics, and supported legacy family pages. Compares four-part Windows versions. For Arc devices, regular Graphics and **Arc Pro Graphics** pages are checked against the explicit product list; Pro is not assumed compatible with every Arc card. CPU-only inference and 6th-Gen labels are rejected. No speculative Beta option is added. OEM customization still requires review. |

Channel verification requires live vendor access. An unavailable or empty catalog
does not populate an unverified driver type just to keep installation enabled.
**Official source** remains available for manual review. The 9 October
revision adds explicit HTML and English-language content negotiation for Intel
catalog requests. This resolves the reproduced 403 on the development connection:
the same URL/client returned 403 without these headers and 200 with them. This
does not guarantee every Intel CDN/network route. No browser session, cookies,
credentials, UA impersonation or third-party proxy is used. Download requests and
other vendors' request headers are unchanged. The 8 October release predates this fix.

Requests use approved official HTTPS hosts and a bounded timeout. Vendor changes,
regional access restrictions or anti-bot responses can make checks temporarily
unavailable. Use **View official release details** for a verified result, or the
existing **Official source** button when a check cannot be completed. No third-party
driver mirror is used. An update check retrieves metadata only; the existing
download integrity, signature/publisher checks and post-install device read-back
are retained. Package resolution now follows the selected channel. Matching AMD
Driver Store versions, when supplied by release notes, also participate in
post-install target-version verification.

## Verification

- Offline service/parser fixtures cover numeric version comparison, exact model
  mapping, Windows 10/11 catalog selection, date-only and timezone-aware metadata,
  missing/invalid dates, AMD Auto-Detect exclusion, AMD branch ambiguity, HTTP
  failure, malformed responses, and cancellation. Installer execution and binary
  downloads are disabled in this test harness.
- Channel fixtures cover Studio's distinct package URL, workstation-versus-consumer
  eligibility, supported-product/OS validation, AMD Recommended/Optional full
  packages, Intel Pro's explicit product list, release changes before download,
  and rejection of fallback or automatic downgrade.
- Backend-free native WinUI tests exercise the production header and release
  panel in Light and Dark, 25/100/200% text scaling, two window sizes, header
  alignment while scrolling, driver-type selection, isolated button invocation,
  and checking/error/current-version states. The previews use synthetic data.
- A read-only live check on the development RTX 4070 Ti SUPER identified distinct
  Game Ready and Studio packages on 7 October. A synthetic RTX A4000 probe
  verified the workstation Enterprise catalog. A synthetic AMD RX 7900 XTX probe
  read Recommended/Optional packages but retained an ambiguous version comparison.
  Intel initially returned HTTP 403. With the 9 October request fix, all
  four Intel catalog probes (Arc A770, Arc Pro A60, UHD 630 and Iris Xe) read
  versions/dates/direct-package metadata successfully. Pro and consumer catalog
  filtering still rejects products absent from the corresponding list. Synthetic
  probes do not establish installed-hardware compatibility; none installed a driver.

Run offline tests without changing Windows:

```powershell
dotnet run --project .\Tests\GpuUpdates\GpuUpdates.Tests.csproj
```

Optional read-only online probes (Internet access and vendor requests):

```powershell
dotnet run --project .\Tests\GpuUpdates\GpuUpdates.Tests.csproj -- --live
dotnet run --project .\Tests\GpuUpdates\GpuUpdates.Tests.csproj -- --live-catalogs
dotnet run --project .\Tests\GpuUpdates\GpuUpdates.Tests.csproj -- --live-intel
```

The first uses the PC's actual GPU inventory. The second uses synthetic AMD,
Intel and NVIDIA workstation product identities; the third checks four synthetic
Intel identities against their live catalogs. None downloads driver packages, installs software or
changes Windows settings. Live results can change after the check completes.
