---
type: test matrix
sprint: 29
target version: 4.2.0
date authored: 2026-05-10
covers: merged tracks A, D, F, G, H, J, M, N (Track B cascade still in-flight on sprint29/track-b-cascade)
testers: Don (primary), Noel
screen readers: JAWS 2026, NVDA 2026.1
---

# Sprint 29 — Test Matrix

This matrix covers the seven merged Sprint 29 tracks. Track B (discovery cascade Phases 1-3) is still in-flight on `sprint29/track-b-cascade` and gets its own matrix when it lands.

Run order suggestion: A → H → F → G → J (install) → D + M + N (end-to-end update flow). A and H share the trace archive surface, so doing them adjacent makes verification cheaper.

For each row: Pass / Fail / Blocked with a one-line note. Screen reader column means "behavior speaks correctly under both readers" — flag any difference.

---

## Track A — Trace persistence + Short Action Labels

**What shipped:** Manifest-driven LZMA-compressed trace archive (`JJTrace/SessionArchive.cs`), outcome enum (`TraceOutcomeLabels.cs`), killed-session detection, AS-retry markers, manual prune helper. Plus `ShortActionLabel` populated for ~90 commands (verb-led, 3-6 words).

### Functional

- [ ] **Connect → disconnect cycle archives a trace.** Connect to any radio, disconnect cleanly. Find a new entry in the trace archive folder with outcome=`Success`.
- [ ] **AS-retry produces a marker.** Force an AS-retry condition (occupied slice, etc.). Trace archive entry has outcome=`AsRetryFailed` or `RetrySucceeded` — both acceptable, just verify a marker exists.
- [ ] **Killed session detected.** Connect to a radio, force-kill JJF via Task Manager. Reopen JJF. Most recent archive entry has outcome=`Killed` (or equivalent), not `Success`.
- [ ] **Manual prune helper works.** Settings → Diagnostics → Prune Old Traces removes entries >30 days old. Recent entries untouched.
- [ ] **Archive survives restart.** Close JJF, reopen. Archive list still populated, files still readable.
- [ ] **No-radio-connected announcement uses Short Action Label.** With no radio connected, press a bound hotkey (e.g. band-change). Speech announces "[Action] — no radio connected" using the verb-led label, not the function name or a generic "no radio."

### Screen reader (run with both JAWS and NVDA — flag any difference between readers)

- [ ] Trace archive entries readable in dialog list.
- [ ] Outcome labels read intelligibly, not raw enum names.
- [ ] Short Action Label announcements consistent across both readers.

---

## Track D — App-updater client (delta-fetch + XZ)

**What shipped:** `JJFlexUpdater/UpdaterClient.cs` + `ManifestFetcher.cs` + `XZStream.cs`. Settings → Updates tab. On-demand and auto-check (with consent for nightly).

### Functional

- [ ] **On-demand check finds current version.** Tools → Check for Updates with the latest installed → "you're up to date" speaks/displays.
- [ ] **On-demand check finds newer version.** Force the manifest to advertise a higher version → "version X.Y available" prompt shows.
- [ ] **SHA-256 verify catches tampered file.** (Manual: corrupt one staged file mid-download.) Updater rejects + retries or cleans up.
- [ ] **Channel selector — stable.** Default channel = stable. Switch shows beta/nightly entries.
- [ ] **Channel selector — beta.** Switching to beta surfaces a beta version if available.
- [ ] **Channel selector — nightly first pick prompts consent.** First time a user picks nightly, a consent dialog explains "less stable, expect rough edges." Subsequent picks don't re-prompt.
- [ ] **Network timeout — graceful skip.** Disconnect WAN, click Check for Updates. Friendly "couldn't reach update server, will try later" message; no crash, no silent freeze.
- [ ] **Manifest endpoint 404 — silent skip on auto-check.** If auto-check hits a 404 on the manifest URL, no popup interrupts the user (no-noise-unless-asked rule). Manual check shows the error.
- [ ] **Delta XZ download decompresses cleanly.** Watch staging dir during update — files arrive uncompressed and intact.

### Screen reader (run with both JAWS and NVDA — flag any difference between readers)

- [ ] Settings → Updates tab navigable via Tab order.
- [ ] Channel-change consent dialog reads in full.
- [ ] "Update available" prompt has named buttons, not "Button 1".

---

## Track F — Tuning UX bundle

**What shipped:** Removed `C` toggle for tuning step. Up/Down = coarse, Shift+Up/Down = fine. Removed Ctrl+< / Ctrl+> audio gain hotkeys (volume now lives in Audio expander). RIT/XIT 1-4 enters quasi-modal scale-adjust mode.

### Functional

- [ ] **Up/Down tunes by coarse step.** Default coarse step (set in Settings) advances frequency on each press.
- [ ] **Shift+Up/Down tunes by fine step.** Smaller step than coarse — verify both are independently configurable in Settings.
- [ ] **`C` no longer toggles tuning step.** Press `C` — should do nothing tuning-related (free for re-binding later, but verify no orphaned behavior).
- [ ] **Coarse + Fine settings split in dialog.** Settings → Tuning shows two distinct fields, not one with a mode toggle. Existing user's old coarse value populates BOTH fields on first launch (one-shot migration).
- [ ] **Audio gain hotkeys gone.** Ctrl+< and Ctrl+> do nothing for headphones/line out gain. Audio expander surfaces the controls instead.
- [ ] **RIT scale-adjust modal entry.** With RIT focused, press `1`. Mode entered (audible cue + speech). Up/Down adjusts scale. Escape exits. Focus loss exits.
- [ ] **RIT scale-adjust — bounds clamp.** Drive scale to maximum (10,000 Hz per spec). Further presses clamp, don't reject.
- [ ] **XIT scale-adjust mirror behavior.** Same flow on XIT.
- [ ] **Modal lock on duplicate hotkey.** While in RIT scale-adjust, press `1` again → ignored, no double-modal, no exit.

### Screen reader (run with both JAWS and NVDA — flag any difference between readers)

- [ ] Coarse vs fine tune step announced distinctly.
- [ ] RIT/XIT scale-adjust mode entry announces "scale adjust" or equivalent.
- [ ] Modal exit on Escape and on focus loss both announce the exit.
- [ ] Audio expander controls labeled correctly.

---

## Track G — Stuck-modal escape changelog

**What shipped:** `docs/CHANGELOG.md` entry only. The implementation (`fdb987e6`) shipped 2026-05-04; Track G fills the docs gap.

### Functional

- [ ] **Changelog entry present and readable.** Open `docs/CHANGELOG.md`. Find the "Connecting cancellation" entry under the current in-progress version.
- [ ] **Entry follows tone conventions.** First-person, warm, no internal jargon, no track labels.

(No code behavior to test — Track G is documentation-only.)

---

## Track H — Trace Archive Browser tab

**What shipped:** New "Trace Browser" tab inside TraceAdmin dialog. Filter row (date range + outcome dropdown + text search), sortable list, detail panel, action buttons (View, Export, Delete, Prune).

### Functional

- [ ] **Tab appears in TraceAdmin dialog.** Operations → Tracing → Trace Browser tab visible and tab-reachable.
- [ ] **List populates from archive.** Entries from Track A's archive show in list with date / radio / outcome / duration columns.
- [ ] **Date range filter works.** Set date from/to, list shrinks to entries in window.
- [ ] **Outcome filter works.** Pick `AsRetryFailed` from dropdown, list shows only those entries.
- [ ] **Text search works.** Type a substring (e.g. radio model name), list filters.
- [ ] **Sort by column.** Click date column header, ordering reverses. Same for radio/outcome/duration.
- [ ] **Detail panel updates on selection.** Click a row, panel shows trace path, file size, outcome reason.
- [ ] **View action.** Select a row, click View → trace path is on clipboard. Paste somewhere to verify.
- [ ] **Export action.** Select a row, click Export → file picker opens, exports to chosen location.
- [ ] **Delete action.** Select a row, click Delete → confirm dialog → row gone, file gone from archive folder.
- [ ] **Prune action.** Click Prune → confirm dialog mentions ">30 days" → entries older than 30 days removed.
- [ ] **Externally-deleted file handled.** Select a row, delete the trace.zip via Explorer, click View → graceful "file not found" message rather than crash.

### Screen reader (run with both JAWS and NVDA — flag any difference between readers)

- [ ] List view rows announce all four columns.
- [ ] Filter dropdowns labeled (date from, date to, outcome, search).
- [ ] Action buttons named distinctly (View, Export, Delete, Prune).
- [ ] Confirm dialogs read in full.

---

## Track J — Self-contained build pipeline

**What shipped:** `<SelfContained>true</SelfContained>` + `<PublishReadyToRun>true</PublishReadyToRun>` in vbproj. New `generate-deletelist.ps1` for NSIS recursive cleanup. Installer payload ~50-55 MB (publish folder ~180-190 MB).

### Functional (installer + launch)

- [ ] **Release build produces self-contained output.** `dotnet build -c Release -p:Platform=x64`. Verify presence of `coreclr.dll`, `clrjit.dll`, `hostfxr.dll`, `hostpolicy.dll`, `wpfgfx_cor3.dll` in `bin\x64\Release\net10.0-windows\win-x64\`.
- [ ] **Publish folder size in expected range.** ~180-190 MB, ~364 files.
- [ ] **Installer file size in expected range.** Setup .exe ~50-55 MB.
- [ ] **Installer satellite-resource subdirs present.** Top-level subdirs include `cs/`, `de/`, `es/`, `fr/`, `it/`, `ja/`, `ko/`, `pl/`, `pt-BR/`, `ru/`, `tr/`, `zh-Hans/`, `zh-Hant/` (13 satellite dirs) plus `runtimes/`, `help/`, `Resources/`.

### Functional (fresh-VM install — load-bearing accessibility test)

- [ ] **Fresh Windows VM install — no .NET 10 pre-installed.** Install JJFlexRadio on a Windows VM that has never had .NET 10. Launcher runs without prompting for runtime install.
- [ ] **JJF reaches Home screen.** On first launch after fresh install, app reaches Home screen without errors.
- [ ] **Uninstall removes everything.** Run uninstaller. Install dir is gone, including all 13 satellite subdirs and `runtimes/`. Verify with Explorer.

### Screen reader (run with both JAWS and NVDA — flag any difference between readers)

- [ ] Installer reads ToS, install path, and other prompts.
- [ ] First-run app reaches Home announcing as expected, no runtime-install dialog interrupting.

---

## Track M — Updater helper exe (atomic file replacement)

**What shipped:** New `JJFlexUpdaterHelper` project (~71 MB self-contained). Atomic file replacement: backup → download to .new → rename → swap. Single-instance mutex. Append-only `helper.log`. Rollback on any failure. JJF relaunch via `Process.Start UseShellExecute=true`.

### Functional

- [ ] **Track D hands off to helper.** When an update is accepted, JJF stages files, writes `handoff-manifest.json`, launches helper exe, then exits. Watch helper.log for "received manifest" entry.
- [ ] **Helper waits for JJF PID.** Helper.log shows "waiting for PID NNNN" then "PID exited."
- [ ] **Per-file replacement steps logged.** For each file: backup → download → rename → swap entries in helper.log.
- [ ] **SHA mismatch triggers per-file rollback.** (Manual: corrupt one staged file before triggering update.) Helper marks file as failed, rolls back the partial swap.
- [ ] **Disk full triggers full rollback.** (Manual or simulated.) All swapped files restored from .bak, helper.log records the abort reason.
- [ ] **Single-instance mutex.** Launch helper twice in quick succession. Second instance exits cleanly with "another helper already running" log entry.
- [ ] **Successful update relaunches JJF.** After successful update, JJF re-opens with new version. Verify via About dialog.
- [ ] **helper.log is append-only.** Multiple update runs add to same log; no overwrite.
- [ ] **Helper exits cleanly when JJF PID already gone.** Kill JJF before helper sees it. Helper proceeds (no infinite wait), logs "PID already gone."
- [ ] **Relaunch failure reports.** (Manual: rename JJF exe between unstage and relaunch.) Helper exits with error code, log explains.

### Screen reader

Helper has no UI — runs as background process. Manual log review only.

---

## Track N — Server-side manifest generation

**What shipped:** `tools/jjflex-manifest-gen/manifest_gen.py` (Python CLI), `r2_client.py` (Cloudflare R2 wrapper), pytest suite.

### Functional

- [ ] **Dry-run computes hashes without uploading.** `manifest_gen.py --version 4.2.0 --channel stable --source-dir <dir> --dry-run` prints summary, no R2 writes.
- [ ] **Real run uploads new files.** Upload mode pushes uncached files to `data.jjflexible.radio/files/{hash}.xz`, updates `jjflex-app-manifest.json`.
- [ ] **Hash-match skip.** Re-run upload on same source: files already on R2 (matching hash) skipped, "skip (already on R2)" logged.
- [ ] **XZ compression FORMAT_XZ.** Compressed file headers verify FORMAT_XZ (magic bytes `FD 37 7A 58 5A`), matching Track D's `XZStream` decoder.
- [ ] **Manifest JSON structure.** Output JSON has channel keys (stable/beta/nightly), per-channel version + per-file pointers.
- [ ] **Missing source file errors out.** Invalid `--source-dir` produces clean error message, no half-written manifest.
- [ ] **pytest suite passes.** `cd tools/jjflex-manifest-gen && pytest` — all green.
- [ ] **Retry on transient S3 failure.** (Inject failure if possible.) Retries 3× with backoff before giving up.

### Screen reader

CLI tool — n/a for screen reader matrix beyond ensuring the output text reads cleanly in a terminal.

---

## Integration — End-to-end update flow

These cross-track tests validate D + M + N + J together. The full update pipeline only works if all four cooperate.

- [ ] **Full happy-path update.** Server (N) publishes v X.Y → client (D) detects on auto-check → user accepts → client stages files → helper (M) swaps atomically → JJF relaunches at v X.Y. About dialog confirms new version.
- [ ] **Self-contained update preserves runtime.** After update, .NET 10 runtime files (coreclr.dll etc.) are still present and JJF launches without runtime-install prompt.
- [ ] **Failed update doesn't brick JJF.** Force a failure mid-update (network drop after stage, before helper completes). User can relaunch old JJF version successfully. Either rollback completed or original files were never touched.
- [ ] **Crash report flow not broken by update.** After update, trigger a test crash (if a debug hook exists) → crash bundle still POSTs to crashes.jjflexible.radio.

---

## Tester notes for Don

- Each row is a single test step; mark Pass / Fail / Blocked / Skipped with a one-line note.
- Screen reader columns are split JAWS / NVDA — only fill in if you're using that reader for the run.
- Anything ambiguous, mark Blocked with the question and we'll clarify before re-running.
- "Manual" qualifiers mean steps that require you to inject a failure (corrupt file, disconnect WAN, etc.) — skip if the setup isn't worth the effort and just mark Skipped.
- Track A's killed-session test will leave a process behind; close TraceAdmin between runs so you don't accumulate dead entries.
- Track J's fresh-VM install is the load-bearing one for the whole 4.2.0 release — please flag any first-run friction immediately.

---

## Coverage gap

- **Track B (discovery cascade Phases 1-3)** — still in-flight on `sprint29/track-b-cascade`. Will get its own matrix when it merges.
- **Track C (crash reporter)** — landed pre-Sprint-29 seal as standalone work. Receiver-side (rarbox) was validated 2026-05-08 (F3-G run); client side may want its own dedicated test pass with the trace-archive integration from Track A.
- **Track E (firmware update Phase D)** — still on `track/flexlib-42`, gated on FlexLib 4.2.18 silent-discovery resolution.
