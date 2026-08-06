# Auto-update research — current state, gaps, and the path to shipping

Written 2026-08-06 on `track/flexlib-4220`. Scope: the JJ Flex app updater (Sprint 29
Tracks D, M, N), its hosting, its accessibility posture, and how the firmware channel
relates. Everything below was verified against the working tree on this date; memory
entries were treated as point-in-time records and re-checked against code.

The headline: this is not a greenfield design problem. Roughly 80 percent of a
delta-capable, channel-aware, screen-reader-first updater already exists in the repo
and is wired into the running app. What remains is a short list of integration gaps —
two of which are silent killers — plus policy decisions Noel has mostly already made
but the code has not caught up to.

## 1. Current state inventory

### 1.1 Client library — JJFlexUpdater (real, complete, wired in)

`JJFlexUpdater\JJFlexUpdater.csproj` is a class library in the solution
(`JJFlexRadio.sln:70`), referenced by both the main app (`JJFlexRadio.vbproj:519`) and
the WPF layer (`JJFlexWpf\JJFlexWpf.csproj:55`). It is not scaffold; it is a finished
three-phase orchestrator:

- **Check.** `UpdaterService.CheckForUpdateAsync` (`JJFlexUpdater\UpdaterService.cs:66-88`)
  fetches the app manifest, filters the user's channel to the running platform
  (win-x64 / win-x86 via `Net\UpdaterHttpClient.cs:59-62`), and compares versions
  numerically.
- **Plan.** `PlanUpdateAsync` (`UpdaterService.cs:97-129`) fetches the per-version file
  manifest, hashes the local install (`Hashing\InstallInventoryWalker.cs`, with an
  mtime/size cache in `HashInventoryCache`), and computes a download/keep/delete plan
  (`Delta\DeltaPlanner.cs` — pure function, unit-testable).
- **Execute.** `ExecuteAsync` (`UpdaterService.cs:140-189`) downloads per-file XZ blobs
  into a `%TEMP%\JJFlexUpdate-<guid>` staging dir (`Staging\StagingDir.cs:36-45`),
  verifies each file twice (compressed sha256 in flight, uncompressed sha256 after
  decompression — `Download\DeltaDownloader.cs:125-171`), writes a handoff manifest,
  launches the helper exe, and returns so the caller can shut the app down. Any
  delta-path failure transparently falls back to `ExecuteFullBundleAsync`
  (`UpdaterService.cs:197-216`), which downloads the full NSIS installer, verifies its
  sha256 (`Download\FullBundleDownloader.cs:106-111`), and launches it silently with
  `/S` (`Staging\InstallerLauncher.cs:33`).

Supporting pieces, all present:

- **Channels.** `UpdateChannel.cs` — Stable / Beta / Nightly, wire strings locked to the
  2026-05-03 naming decision, legacy "daily" accepted on read.
- **Settings.** `UpdaterSettings.cs` — persisted to
  `%APPDATA%\JJFlexRadio\update-settings.json` (survives delta swaps because it lives
  outside the install dir). Defaults: channel Stable, auto-check on launch true,
  2-hour periodic check true. Also carries `SkippedVersion` and the one-time nightly
  consent flag.
- **Auto-check.** `AutoCheck\BackgroundUpdateCheck.cs` (never throws; structured result)
  and `AutoCheck\PeriodicUpdateChecker.cs` (2-hour timer with an `IRadioSessionGate` so
  no prompt lands mid-QSO). Gates centralized at `UpdaterService.cs:222-234`: periodic
  fires only if 2+ hours since last check; launch check only if 24+ hours.
- **Networking.** One shared `HttpClient` (`Net\UpdaterHttpClient.cs`, 30-second
  timeout, User-Agent `JJFlexRadio-Updater/{version} ({platform})`), and a shared retry
  policy (`Net\HttpRetry.cs` — 3 attempts, 2s/4s backoff, fail-fast on 4xx).
- **Endpoints.** `UpdaterEndpoints.cs:15-25` — app manifest at
  `https://data.jjflexible.radio/jjflex-app-manifest.json`, firmware catalogue at
  `https://data.jjflexible.radio/firmware/manifest.json`.

### 1.2 Host wiring — the app actually calls this today

- `MainWindow_Loaded` calls `StartUpdaterAutoCheck()` (`JJFlexWpf\MainWindow.xaml.cs:222`).
- `JJFlexWpf\MainWindow.AutoUpdate.cs` runs the launch check after an 8-second delay so
  the welcome speech finishes first (line 40), starts the periodic checker with a
  radio-session gate (lines 44-47), swallows background failures into the trace log
  (lines 80-89), and re-checks the session gate on the dispatcher before showing the
  dialog (lines 99-105).
- **Settings → Updates tab** exists in `JJFlexWpf\Dialogs\SettingsDialog.xaml:913-970`
  (channel radios, both auto-check toggles, last-check text, Check now button — every
  control carries an `AutomationProperties.Name`). Code-behind in
  `SettingsDialog.Updates.cs`, including the first-time-nightly consent MessageBox
  (lines 75-128) and spoken channel-change confirmation.
- **Tools → Check for Updates** menu command (`JJFlexWpf\NativeMenuBar.cs:1112`,
  handler at 1583) — warns first if a radio is connected, speaks "up to date" or
  opens the dialog, honors SkippedVersion.
- **Update available dialog** (`JJFlexWpf\Dialogs\UpdateAvailableDialog.xaml` + `.cs`) —
  headline, current/available versions, delta-vs-full size with savings percentage,
  changelog hyperlink, Download and install / Skip this version / Cancel. Progress
  text is a polite live region (`UpdateAvailableDialog.xaml:50-56`); milestones are
  spoken at Critical, Cancel is always enabled per the dialog-escape rule
  (`UpdateAvailableDialog.xaml.cs:194-199`).

### 1.3 Helper exe — JJFlexUpdaterHelper (built and tested, but see gap 2.1)

`JJFlexUpdaterHelper\` is a standalone self-contained single-file console exe
(`JJFlexUpdaterHelper.csproj:14-18`; roughly 71 MB per the Sprint 29 test matrix,
`docs\planning\agile\sprint29-test-matrix.md:158`). Flow in `Program.cs:66-145`:

- Single-instance mutex guard, then load and echo the handoff manifest.
- Wait up to 30 seconds for the JJF PID to exit (`JjfProcessWaiter.cs:9`); bail without
  touching anything on timeout.
- Backup every file it will overwrite or delete into the staging dir's `backup\`
  (`BackupStep.cs` — fail-fast before any install mutation).
- Replace via sha256-verify, copy to `<target>.new` on the same volume, then atomic
  rename (`ReplaceStep.cs:46-50` — a crash mid-swap leaves the old file intact).
- Delete obsolete files, roll back from backup on any failure (`RollbackStep.cs`),
  relaunch JJF on success (`RelaunchStep.cs`), clean the staging dir.
- Path traversal defense on every rel_path (`PathGuard.cs:39-56`).
- Distinct exit codes for every failure mode; `helper.log` written inside the staging
  dir for forensics.

Track M's synthetic test pass covered clean update, mid-flight sha mismatch with
rollback, mutex contention, and backup blockers (Agent.md line 636).

### 1.4 Server tooling — tools\jjflex-manifest-gen (Track N, built)

`tools\jjflex-manifest-gen\manifest_gen.py` is a Python click CLI with a pytest suite:
walks a published build dir, XZ-compresses every file (FORMAT_XZ, matching the
client's SharpCompress XZStream expectations — `manifest_gen.py:29-32`), uploads blobs
to R2 at content-addressable keys `files/<2-char-prefix>/<sha256>.xz` keyed on the
*uncompressed* hash for URL stability (`manifest_gen.py:163-170`), writes a
per-version file manifest, and updates the top-level app manifest's channel pointer.
Cache headers are right: blobs immutable for a year, file manifests 5 minutes, the app
manifest no-cache (`manifest_gen.py:52-55`). Dedup on re-upload via HEAD checks.
Dry-run and no-upload modes exist for local testing.

The hook is already in the release pipeline: `build-installers.bat:177-220` invokes
manifest_gen for each built arch when `JJFLEX_MANIFEST_GEN_MODE` is set (`dry-run` or
`live`), with the channel from `JJFLEX_MANIFEST_GEN_CHANNEL`. Off by default for
local builds; intended for roarbox CI.

### 1.5 Firmware side — shares the plumbing, separate document

- `JJFlexUpdater\Firmware\FirmwareManifest.cs` + `FirmwareCatalog.cs` — separate
  catalogue at `data.jjflexible.radio/firmware/manifest.json`, per-image sha256,
  numeric version compare, `.partial`-then-rename download (`FirmwareCatalog.cs:129-190`),
  `breaking` flag + reason for must-have releases (`FirmwareManifest.cs:84-100`),
  `min_version_for_direct_update` advisory. The firmware R2 pipeline is live
  (`publish-firmware-to-r2.ps1` at repo root; memory records it went live 2026-08-03).
- Consumers: the connect-time advisory (`JJFlexWpf\MainWindow.FirmwareAdvisory.cs` —
  notify-only, once per radio+version per run, silent on every failure path) and Radio
  Setup step 3 (`JJFlexWpf\Dialogs\SettingsDialog.RadioSetup.cs:387+` — get from
  catalogue, choose local file, preflight, send).
- Transfer engine: `Radios\FlexBase.cs:2568+` — `PreflightFirmwareUpdate` (file
  exists, sha256, radio not transmitting), `BeginFirmwareUpdate` (wraps FlexLib
  `SendUpdateFile`), and `WatchFirmwareUpdateAsync` (watches discovery through the
  reboot). The detached-operations engine
  (`docs\planning\active\detached-operations-plan.md`, 2026-08-05) is the in-progress
  rework: a shared `DetachedRadioSession` that disconnects the GUI client before
  firmware send / registration, with real radio-reported progress bound to a real
  ProgressBar.
- `AppManifest.ChainedUpdates` (`Manifest\AppManifest.cs:64-88`) is the reserved slot
  for the chained-updater interlock (`min_client_version` → app update first, then the
  dependent update). Deserialized but not yet acted on — Track E's consumer side is
  unbuilt.

### 1.6 Version and distribution flow (context the updater rides on)

- Base version `4.1.16` lives in `JJFlexRadio.vbproj:45`; the 4th part Y is
  `git rev-list --count HEAD` plus `BUILDNUM_OFFSET=-468`
  (`build-installers.bat:35`, same constant in `build-debug.bat:39`), injected via
  `-p:Version=`. `install.bat:94-98` reads FileVersion (clean 4-part) from the built
  exe, never ProductVersion (which carries a commit-hash suffix).
- The app ships self-contained .NET 10 (`JJFlexRadio.vbproj:23-24`), installer packages
  the whole publish dir via `File /r` (`install template.nsi:85`), uninstall list
  generated by `generate-deletelist.ps1`. Installers are ~50-55 MB LZMA-solid.
- Distribution today is entirely manual: `build-debug.bat` zips Debug x64 to NAS and
  (on `--publish`) to the shared Dropbox `debug\` folder; stable installers go to
  Dropbox top level; public releases to GitHub Releases at nromey/JJFlex-NG. The
  updater, once live, replaces the tester-facing half of this with pull-based
  channels; Dropbox stays as the human-readable fallback.

### 1.7 What has never happened

Per `Agent.md:487`: Track D merged but **has never been exercised against a real
version-bump manifest end-to-end**. No `jjflex-app-manifest.json` has been published
to R2. Every check so far has exercised only the "manifest doesn't exist yet" path,
which the client deliberately treats as a normal, quiet state
(`Net\ManifestFetcher.cs:13-17`).

## 2. Gap analysis

Ordered by severity. The first two are silent killers — the system appears healthy
while doing nothing.

### 2.1 The helper exe is never shipped (delta path is dead on arrival)

`HelperLauncher.ResolveHelperPath` looks for `JJFlexUpdaterHelper.exe` next to
`jjflexible.exe` in the install dir (`Staging\HelperLauncher.cs:22-43`). Nothing puts
it there: the main app project does not reference or copy it, `install.bat`,
`install template.nsi`, `build-installers.bat`, and `generate-deletelist.ps1` contain
zero references to it (verified by grep across all .bat/.nsi/.ps1), and the helper's
build output sits only in `JJFlexUpdaterHelper\bin\`. Result: every delta update will
throw `HelperLaunchException` and silently fall back to the 50+ MB full-bundle
installer. The entire Track D delta machinery — the reason deltas were pulled into
scope "from day one" — is unreachable in a shipped build. Fix is small: build the
helper as part of the release pipeline and copy it into the publish dir before NSIS
packaging (it then rides `File /r` and the deleteList automatically). But see risk
5.2 on its size.

### 2.2 Client and server disagree on the app-manifest schema (checks would find nothing)

The client deserializes each channel as `{ "latest_version", "entries": [...] }` with
per-entry `file_manifest_url`, `full_installer_url`, `full_installer_sha256`,
`full_installer_size_bytes`, `changelog_url` (`Manifest\AppManifest.cs:26-72`). The
generator writes `{ "latest_version", "versions": [...] }` with per-entry
`manifest_url` and optional `release_notes_url` / `min_client_version`
(`tools\jjflex-manifest-gen\manifest_gen.py:226` and `:277-296`). Consequences, in
order:

- `Channels[channel].Entries` deserializes empty (`versions` ≠ `entries`), so
  `CheckForUpdateAsync` returns "up to date" forever. The updater would ship, run,
  and never once find an update — the worst possible failure mode because nothing
  errors.
- Even with the list key fixed, `manifest_url` ≠ `file_manifest_url`, so planning
  would fail.
- The generator never emits the full-installer fields at all, and Track N never
  uploads installers to R2 — so the full-bundle fallback (and the delta path's own
  failure fallback) has no URL and no hash to work with. Today the fallback would
  throw `FullBundleException("manifest entry has no full_installer_url")`
  (`Download\FullBundleDownloader.cs:45-48`).

This is exactly what "merged but never exercised end-to-end" looks like. The fix is a
half-day: pick one schema (recommend the client's — it is richer and already
compiled into the app), align manifest_gen and its tests, add an
`--installer-path` flag that uploads the NSIS exe to R2 and fills the three
full-installer fields, and then run one real end-to-end bump on the nightly channel.

### 2.3 No elevation story for the delta swap

The app installs to Program Files (`install template.nsi:26`, admin-level installer,
line 33). The helper is launched without elevation (`HelperLauncher.cs:67-74` — no
`runas` verb) and has no elevation manifest. On a standard UAC setup, `ReplaceStep`'s
first write into Program Files throws `UnauthorizedAccessException`, the helper rolls
back (or aborts pre-mutation at backup — actually backup writes to %TEMP%, so the
failure lands in ReplaceStep), and — see 2.4 — the user is left at the desktop with
no app and no explanation. The full-bundle fallback works because NSIS requests
elevation itself and Windows shows a UAC prompt. Note the nightly-tester case
mostly dodges this today because testers run from unzipped folders in user-writable
locations, which may be why it has not been felt yet. Options in section 6, question 3.

### 2.4 Failed updates strand a blind user in silence

Two compounding behaviors:

- On any post-handoff failure the helper preserves the staging dir and exits — but
  only relaunches JJF on success (`JJFlexUpdaterHelper\Program.cs:104-144`;
  `RelaunchStep` is called at line 137, success path only). The app closed itself for
  the update; after a rollback the user is sitting at an empty desktop. For a screen
  reader user there is no signal at all that anything went wrong or that the app can
  simply be started again.
- Nothing on the next launch notices a failed update. There is no "the update last
  night didn't take — you're still on 4.2.0.57" announcement, no staging-dir sweep,
  no surfacing of `helper.log`. The forensic record exists but no one is told it does.

Both fixes are cheap and high-value: relaunch JJF even after rollback (the rolled-back
install is by definition launchable), and add a launch-time check for leftover
`JJFlexUpdate-*` staging dirs that speaks a one-line outcome and offers retry or
dismissal.

### 2.5 Policy drift: the 2026-08-03 "Chrome model" is not what the code does

Noel's latest policy (memory `project_sprint29_updater_vision.md`, 2026-08-03 section)
says: unless updates are off, check automatically, **download automatically without
asking**, then notify via message box that the update installs on next app
close/relaunch. The implemented flow instead prompts *before* downloading
(UpdateAvailableDialog's Download and install button) and installs *immediately* upon
consent (the dialog calls `Application.Current.Shutdown()` right after handoff —
`UpdateAvailableDialog.xaml.cs:113-118`). There is no install-on-exit deferral
anywhere. The built flow is more consent-forward and arguably fine; but it is not
what the standing decision says. Needs an explicit call — section 6, question 1.

### 2.6 Smaller gaps, listed

- **No privacy note on the Updates tab.** The diagnostics-settings memo specifies
  wording ("Auto-update sends only the current version string…"); the XAML
  (`SettingsDialog.xaml:913-970`) has channel/toggle text but no privacy disclosure.
  One TextBlock.
- **No update-check trace lines for user actions.** The "user-state-changing actions
  get traced" principle wants channel changes and consent decisions in the trace;
  channel change speaks but does not trace.
- **`chained_updates` has no consumer** (Track E) — fine to defer, but the firmware
  interlock ("this firmware needs a newer JJ Flex") silently does not exist yet.
- **No Authenticode verification of downloaded artifacts.** Zero signature checks in
  JJFlexUpdater (grep: no X509/WinVerifyTrust anywhere). Integrity rests entirely on
  TLS to data.jjflexible.radio plus manifest sha256 — meaning whoever can write to the
  R2 bucket (or the manifest) fully controls what user machines execute. Acceptable
  for the tester pool; not the right end state once signing exists. Section 6,
  question 4.
- **No partial-download resume.** Delta blobs are small (whole-file retry is fine);
  the full installer (~50-55 MB) and firmware images (up to ~370 MB) restart from
  byte zero on interruption. HTTP Range against R2 is straightforward if it earns
  its keep. Section 6, question 8.
- **Nightly channel has no automated publisher.** The manifest-gen hook exists only in
  `build-installers.bat` (Release); `build-debug.bat` — the thing that actually
  produces nightlies — has no hook (verified by grep). Until roarbox CI exists,
  nightly-channel manifests would be hand-run.
- **Version picker (Phase C) unbuilt** — but the manifest's per-channel entries list
  already retains every published version, so the server side is ready for it.
- **helper.log is deleted on success** (CleanupStep removes the staging dir including
  the log). Fine day-to-day; consider copying the last helper.log into
  `%APPDATA%\JJFlexRadio\` for "what happened last update" support questions.

## 3. Proposed architecture

The right move is to finish what exists rather than redesign. Concretely:

### 3.1 Channels — map is already correct

Nightly / Beta / Stable in the enum map onto the existing distribution tiers:
nightly = today's Debug zips to private testers, beta = today's Dropbox-top-level
stable installers to the invited pool, stable = today's GitHub public releases. The
updater does not replace those channels on day one; it adds a pull path beside them,
and the push paths (Dropbox, GitHub) retire per-tier as confidence builds. Default
channel Stable; nightly gated behind the existing consent dialog. One naming caution:
"beta" as a wire string is already locked in the enum, while the distribution memory
calls the middle tier "Stable Release" — keep the user-facing labels from the
Settings tab (Stable / Beta / Nightly) and never rename wire strings after the first
manifest publishes.

### 3.2 Manifest hosting — R2 primary, GitHub Releases as the human mirror

Weighing the two:

- **R2 (data.jjflexible.radio)** is already implemented on both ends, costs nothing at
  egress, serves through Cloudflare's edge, supports the content-addressable delta
  store (which GitHub Releases structurally cannot — release assets are per-release,
  not content-addressed), lets nightly builds publish without spamming a public repo's
  Releases page, and keeps update checks on infrastructure we control — consistent
  with the no-phone-home posture (a GitHub API check leaks every user's IP and
  check cadence to a third party, and unauthenticated API calls rate-limit at 60 per
  hour per IP, which shared-NAT hamfest scenarios can actually hit).
- **GitHub Releases API** would win only if we had no hosting — Civ VI Access's
  `GitHubReleasesClient.cs` proves the pattern works — but we do have hosting, and
  the delta store needs S3-shaped storage anyway.

Recommendation: R2 is canonical for the app manifest, file manifests, delta blobs,
full installers, and firmware. GitHub Releases continues as the public, human-facing
mirror for stable installers (people can still download by hand), and the manifest's
`full_installer_url` may point at R2 always — one canonical URL, no divergence.

### 3.3 Manifest format — adopt the client schema, extend deliberately

Keep the compiled-in C# schema (`AppManifest.cs` / `FileManifest.cs`) as the contract
and fix the generator to match (gap 2.2). Additions worth making at the same time,
while the schema is still un-published and free to change:

- Populate `full_installer_url` / `sha256` / `size_bytes` from a new
  `--installer-path` input to manifest_gen (upload the exe to
  `installers/<version>/<filename>` on R2).
- Emit `changelog_url` (point at the CHM anchor page or a hosted changelog snippet).
- Keep `schema_version` at 1; the client already skips unknown fields, so additive
  evolution is safe.
- Carry `min_client_version` through to the C# `ChainedUpdate` shape when Track E
  lands; the generator already accepts the flag.

### 3.4 Download and integrity — layered, mostly done

Today: TLS to our domain, sha256 on every delta blob (compressed and uncompressed),
sha256 on the full installer, sha256 re-verification by the helper before any file
replaces (`ReplaceStep.cs:35`), path-traversal guards on both sides. Add, in order of
value:

1. **Sign the installers and both exes** via the already-provisioned Microsoft Trusted
   Signing account (profile `romeycert`; the Civ VI Access workflow at
   `C:\dev\Civ-vi-access\.github\workflows\release.yml` is the working reference).
   This is planned already (`docs\planning\active\signing-track.md`).
2. **Client-side Authenticode check on the downloaded full installer** before
   launching it (X509Certificate / WinVerifyTrust against the expected publisher).
   Cheap and closes the "R2 bucket compromise executes code" hole for the fallback
   path.
3. **Manifest signing later if warranted** — a detached signature over the app
   manifest verified in-client. Defensible to defer: the manifest only points at
   artifacts that are themselves hash-pinned and (post step 2) signature-checked.

### 3.5 Self-replacement flow — keep the helper, fix delivery and dignity

The chained-helper design is correct and tested: stage, hand off, wait for exit,
backup, atomic-rename swap, rollback, relaunch. The changes it needs:

- Ship it (gap 2.1) — build in the release pipeline, copy into the publish dir.
- Shrink it (risk 5.2) — a console-only app with no WPF/WinForms is the ideal
  NativeAOT candidate: single-digit MB instead of ~71 MB self-contained single-file.
  Alternative: framework-dependent against the app's own runtime is tempting but
  wrong — the helper comment is right that the runtime files may be the very thing
  mid-swap.
- Relaunch on failure too, and add the next-launch failed-update announcement
  (gap 2.4).
- Decide the elevation strategy (question 3) before delta goes live for installed
  (Program Files) copies.

### 3.6 UI touchpoints — screen-reader-first, mostly built

Existing and good: Updates settings tab with named controls; update dialog with
polite live-region progress, Critical-level milestone speech, Escape-closable
everything, spoken deltas ("Download size 8 MB, 84 percent smaller than the full
installer"). To add:

- Privacy note TextBlock on the Updates tab (exact wording exists in the
  diagnostics-settings memo).
- Post-update "What's new" hook: first launch after a version change opens/offers the
  CHM What's New anchor (Sprint 28 built the anchors; this is the promised
  consumer).
- The failed-update announcement from gap 2.4.
- If the Chrome model is adopted (question 1): a "downloaded, installs when you
  close" state that is *announced once* and visible in the Updates tab, never a
  surprise at exit. Every state change speaks; no silent background transitions
  beyond the check itself.

### 3.7 Check cadence and the no-phone-home rule — reconciled honestly

The standing principle is "no telemetry; outbound only on explicit per-event action."
A default-on background check is a literal exception to that, and it should be named
as one rather than papered over. The honest framing: the check is a single
unauthenticated HTTPS GET to our static R2 host; the only quasi-identifying payload
is the User-Agent (`JJFlexRadio-Updater/4.2.0.57 (win-x64)` —
`UpdaterEndpoints.cs:34-35`) plus the IP inherent to any HTTP request; no identity,
no usage data, nothing stored server-side beyond standard access logs. Noel already
ruled on this (2026-08-03: auto-check is disclosed, toggleable, and consistent with
the principle because it hits only our static host with no identity payload). Options
anyway, because the principle deserves the respect of an explicit choice:

- **A. Default-on with disclosure (current code, Noel's standing decision).** Both
  toggles on; privacy note on the tab; both off-switches one keystroke deep. Matches
  the friction-tax principle — missed updates are missed accessibility fixes.
- **B. First-run consent.** One-time dialog on first launch: "May JJ Flex check for
  updates automatically? It sends only the version number." Maximal principle
  purity, one more dialog in every new user's first five minutes.
- **C. Default-off.** Purest reading, worst outcome — the users who most need fixes
  are the least likely to find the toggle.

Recommendation: A, with two refinements — the privacy note actually on the tab, and
the User-Agent version string documented in it (it is the one thing we "send").

### 3.8 Publishing pipeline per channel

- **Nightly:** roarbox CI (or interim: manual `build-debug.bat` extension) runs
  manifest_gen with `--channel nightly` after a green build. Until CI exists, accept
  that nightly manifests are published deliberately, not automatically — which also
  matches "distribution is a deliberate act."
- **Beta/Stable:** `build-installers.bat` release runs already have the hook; add
  signing before manifest generation so hashes cover signed binaries (order matters:
  build → sign → manifest-gen → publish).
- One rule worth writing into the runbook: **the app manifest updates last**, after
  all blobs and file manifests are uploaded, because it is the only no-cache document
  and the pointer that makes a version visible. manifest_gen already does this in the
  right order within a run.

## 4. Firmware channel tie-in

Shared: the transport layer (`UpdaterHttpClient`, `HttpRetry`), the hosting
(`data.jjflexible.radio` R2 bucket, `publish-firmware-to-r2.ps1`), the hash-verified
`.partial` download discipline, and eventually the chained-updater interlock via
`chained_updates` / `min_client_version`.

Deliberately not shared: everything else, and that is correct. The two catalogues are
separate documents on independent publish cadences (FlexRadio's releases versus ours
— `UpdaterEndpoints.cs:19-25` documents the reasoning). The policies are opposite by
design: app updates trend toward automatic; firmware is notify-only forever, because
it reboots the radio, is LAN-only at the transport level, and is a deliberate act
with an operator present. The delivery mechanisms never converge — app updates
replace files on the PC; firmware rides FlexLib's `SendUpdateFile` inside the
detached-operations engine (`detached-operations-plan.md`), which has its own
progress, watcher, and recovery semantics.

The one real coupling to build: when a firmware image (or any future dependent
artifact) declares `min_client_version` above the running JJ Flex, the app updater
runs first with a single combined consent, then the firmware offer reappears after
restart. The manifest fields exist on both sides; the consumer logic (Track E) does
not. It should be scoped into the same implementation phase as the schema fix, since
it touches the same code.

Sequencing note: firmware currently leads the app channel in production readiness —
its manifest is live on R2 and the advisory + Radio Setup flow ship in the app, while
the app manifest has never been published. The firmware pipeline is therefore the
de-facto pilot of the hosting; the app channel inherits proven infrastructure.

## 5. Risks and constraints

- **5.1 Unsigned interregnum.** Until Trusted Signing is wired into
  `build-installers.bat`, every auto-downloaded installer still hits SmartScreen as
  unknown-publisher when launched — and a *silent* NSIS launch that stalls on a UAC
  prompt for an unsigned binary is a confusing experience for a screen reader user
  who was told "installing now." Mitigation: land signing before the updater's first
  public-channel use; the exe rename to jjflexible.exe already happened specifically
  so reputation accrues to the final identity. For the nightly tester pool this is
  tolerable today.
- **5.2 Helper payload size.** ~71 MB self-contained single-file helper inside a
  ~180 MB publish dir adds maybe 25-30 MB to the LZMA-solid installer and 71 MB to
  every install footprint — to run for four seconds per update. NativeAOT or
  trimming the helper is the fix; do it before shipping it, not after (once shipped,
  the helper itself is updated via the delta path, and its hash churn is just another
  file entry).
- **5.3 Elevation (gap 2.3).** Delta updates into Program Files fail without an
  elevated helper. Whatever the answer (question 3), it must be decided before the
  first delta reaches an installed copy — the failure mode today is the stranded
  desktop from gap 2.4.
- **5.4 Dropbox notification externality.** Unchanged from today, but worth stating:
  the updater must never publish into the shared Dropbox tree as part of automation —
  every file dropped there notifies the whole shared group. The updater's channels
  bypass Dropbox entirely (R2 pull), which is itself the fix. Keep `--publish` as the
  only Dropbox writer, human-triggered.
- **5.5 Interrupted downloads.** No resume anywhere (gap 2.6). For deltas this is a
  non-issue; for the ~50 MB installer on rural DSL — the actual audience includes
  exactly these connections — a restart-from-zero on a flaky link can mean never
  completing. HTTP Range resume for the full-bundle and firmware downloaders is a
  contained enhancement.
- **5.6 Rollback story, current truth.** Within one update attempt: solid (backup +
  atomic rename + rollback + preserved staging dir). Across versions: thin — there
  is no in-app "go back to 4.2.0.55." The manifest retains every version per channel,
  so the version picker (Phase C) is the eventual answer; until then rollback is
  "download the older installer from NAS/Dropbox/GitHub by hand." Acceptable for
  soft-launch; say so out loud in the tester notes.
- **5.7 App-manifest write concurrency.** manifest_gen does read-modify-write on the
  app manifest with no compare-and-swap (noted "racy by design" at Track N merge,
  Agent.md line 637). Two publishes racing (x64 and x86 runs back-to-back are
  sequential in the .bat, so the realistic race is human + CI) could drop an entry.
  Fine at current scale; revisit when CI publishes without a human watching.
- **5.8 Schema freeze pressure.** The moment the first real manifest is published and
  a build that reads it reaches testers, `jjflex-app-manifest.json` becomes a
  compatibility surface for every client version in the field. All schema
  corrections (gap 2.2) must land *before* that first publish — this is the cheapest
  week there will ever be to change it.
- **5.9 The 8-second launch delay is a heuristic.** Launch check fires 8 seconds after
  window load to avoid talking over the welcome speech
  (`MainWindow.AutoUpdate.cs:38-42`). On a slow machine with a long welcome plus a
  fast network, collisions are still possible; harmless, but if testers report
  speech pile-ups, gate on the welcome-speech-complete event instead of a timer.

## 6. Open questions for Noel

Each numbered, with a recommendation and the reasoning. Decisions here scope the
implementation phase.

1. **Prompt-first (as built) or Chrome model (as decided 2026-08-03)?** The code asks
   before downloading and installs immediately on consent; your August decision says
   auto-download silently, then message-box "installs on next close."
   **Recommendation: adopt the Chrome model for Stable/Beta, keep prompt-first for
   Nightly.** Stable users get the frictionless path the decision intended; nightly
   testers often *want* to choose the moment they roll forward mid-test-session, and
   nightly deltas land daily so an auto-download that races the next nightly is
   wasted bandwidth. If two behaviors is one too many, Chrome model everywhere —
   the dialog's Skip/Cancel semantics port cleanly to an install-on-close banner.

2. **Which schema wins, and may I treat the first R2 publish as the freeze point?**
   **Recommendation: the C# client schema wins** (richer, compiled into the app,
   costs one Python file + tests to align), full-installer fields become mandatory
   per entry, and we do one deliberate end-to-end rehearsal on the nightly channel —
   publish a real manifest for the current build, bump a test version, watch check →
   delta → helper → relaunch fire on a scratch install — before any tester's build
   points at it.

3. **Elevation strategy for the delta swap.** Three options: (a) helper carries a
   `requireAdministrator` manifest — one UAC prompt per delta update, simple,
   honest; (b) per-machine install stays admin but updates go through a small
   elevated service — over-engineered for this project, not recommended; (c) move to
   per-user install under `%LOCALAPPDATA%` (Squirrel-style) — no UAC ever again, but
   it abandons the existing per-machine install base and HKLM identity, and a
   migration is its own project. **Recommendation: (a) now — one UAC prompt beats a
   broken silent update — and consider (c) only if/when a major-version install-base
   reset happens anyway.** Note NVDA itself ships per-machine with UAC-prompting
   updates; the audience is used to this exact pattern.

4. **How much verification beyond sha256?** **Recommendation: Authenticode-verify the
   downloaded full installer in-client as soon as signing lands (cheap, closes the
   bucket-compromise hole on the highest-privilege path); defer manifest signing** —
   it is real work, and hash-pinning plus signed artifacts covers the threat model at
   this scale. Revisit at public-audience scale.

5. **Helper size: NativeAOT it before shipping?** **Recommendation: yes.** It is a
   console app with no WPF — the one component in the project that is trivially
   AOT-able — and it turns a 71 MB rider into single-digit MB. Half a day including
   re-running the Track M synthetic test pass against the AOT build.

6. **Failure dignity: relaunch-on-rollback plus next-launch announcement — approve as
   in-scope?** **Recommendation: yes, non-negotiable before any tester relies on the
   updater.** A blind user must never end an update at a silent desktop. This is the
   accessibility floor of the whole feature; it is two small changes.

7. **Nightly publishing until roarbox CI exists: manual or hook build-debug.bat?**
   **Recommendation: add the same env-var-gated manifest-gen hook to build-debug.bat
   but leave it off by default**, so a deliberate "publish nightly manifest" is one
   env var away during the proving period, and CI flips it on later without new
   plumbing. Keeps "distribution is a deliberate act" intact.

8. **Range-resume for big downloads: now or later?** **Recommendation: later for the
   app installer (the delta path makes big app downloads rare once the helper
   ships), but now-ish for firmware** — 370 MB images to rural connections with a
   radio waiting is where resume pays for itself first, and `FirmwareCatalog
   .DownloadAsync`'s `.partial` scheme is already halfway there.

9. **Does the version picker (Phase C) ride this implementation phase or the next?**
   **Recommendation: next.** The manifest already retains per-channel version
   history, so nothing is lost by waiting; the picker's policy questions (downgrade
   floor, prerelease visibility) were explicitly deferred to their own conversation,
   and this phase's win condition is "updates flow end-to-end," not "time travel."

10. **Chained-update consumer (Track E): bundle into this phase?** **Recommendation:
    yes, minimally.** Implement only the `min_client_version` gate on firmware offers
    (chain to app update with the single combined consent), because firmware +
    matching client is the concrete near-term need from the Flex alpha cadence, and
    the schema fields already exist on both ends. The generalized hub (drivers,
    NVDA add-ons) waits.

## Appendix: file map for the implementation phase

- Client orchestrator: `JJFlexUpdater\UpdaterService.cs`
- Manifest types: `JJFlexUpdater\Manifest\AppManifest.cs`, `FileManifest.cs`
- Delta + download: `JJFlexUpdater\Delta\`, `JJFlexUpdater\Download\`
- Staging + handoff: `JJFlexUpdater\Staging\`
- Auto-check: `JJFlexUpdater\AutoCheck\`, host wiring `JJFlexWpf\MainWindow.AutoUpdate.cs`
- UI: `JJFlexWpf\Dialogs\UpdateAvailableDialog.xaml(.cs)`,
  `JJFlexWpf\Dialogs\SettingsDialog.Updates.cs`, `SettingsDialog.xaml:913-970`,
  `JJFlexWpf\NativeMenuBar.cs:1112,1583`
- Helper: `JJFlexUpdaterHelper\` (all files; entry `Program.cs`)
- Server tooling: `tools\jjflex-manifest-gen\manifest_gen.py`, `r2_client.py`, tests
- Pipeline: `build-installers.bat` (manifest hook at 177-220), `install.bat`,
  `install template.nsi`, `build-debug.bat`, `generate-deletelist.ps1`
- Firmware: `JJFlexUpdater\Firmware\`, `JJFlexWpf\MainWindow.FirmwareAdvisory.cs`,
  `JJFlexWpf\Dialogs\SettingsDialog.RadioSetup.cs:387+`, `Radios\FlexBase.cs:2568+`,
  `docs\planning\active\detached-operations-plan.md`, `publish-firmware-to-r2.ps1`
