# Sprint 30 — Track D — Front Door Diagnostics

**Worktree:** `C:\dev\jjflex-30d`  **Branch:** `sprint30/track-d`  **Base:** `honest-tx-audio` @ `972e1438`
**Model:** opus  **Class:** BUILDABLE

Read `docs/planning/agile/sprint30-rescue-squelch-pileup.md` for the sprint's shape, and
`docs/planning/agile/sprint30-task-audit.md` before you trust ANY task description.

You own the reporting pipeline — the tool everyone will use to debug everything else. Being wrong
here is expensive downstream, and every mistake in your track is silent.

---

## House rules — these apply to every track, read them once, obey them throughout

**The user is blind and uses NVDA.** He is the person who will operate every line you write, at
the exact moment something has just gone wrong for him.

- **No tables, no ASCII art, no diagrams** in any file you write. Prose or bullet lists.
- **Every control gets `AutomationProperties.Name`** (WPF) or `AccessibleName` (WinForms).
- **Keep disabled or unsupported controls OUT of the tab order.**
- **Do not put long explanations in `AutomationProperties.HelpText`.** NVDA reads HelpText as the
  control's description ON FOCUS, so text parked there is recited every time the user tabs past.
  This matters unusually much for you, because your dialogs are the ones that most need
  explaining. Write the explanations in your report; Track E wires them to the on-demand
  mechanism it is building this sprint. Do not park them in HelpText "for now".
- **Visual layout still matters.**

**Speech core quarantine.** You may NOT edit `Radios\Speech\*`, `Radios\ScreenReaderOutput.cs`, or
change announcement TIMING anywhere. You may CALL `ScreenReaderOutput.Speak(...)`.

**The window-boundary rule — load-bearing for your failure-moment offer.** A screen reader FLUSHES
its speech queue on any window change. An utterance spoken just before a window opens is
destroyed, whether queued or interrupting. So **your offer dialog carries its context in its own
`Title`** — do not announce "something failed" and then open a window, because the announcement
will not survive. `PendingDisconnectLead` in `globals.vb` is the working example of handing an
utterance to the arriving window.

**Escape closes every dialog.** No exceptions. Your offer dialog especially — it appears
unbidden, so it must be trivially dismissible.

**Build verification.**

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
powershell -Command "(Get-Item 'bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe').LastWriteTime"
```

Build the **project**, not just the solution. `--no-incremental` does NOT guarantee freshness;
`dotnet clean` first when you need certainty. Four other agents build concurrently in sibling
worktrees — separate `bin`/`obj`, no collision, just slower.

**Commits and pushes.**

- Commit per completed item. Message format: `Sprint 30 Track D: <description>`.
- **Push after every commit**: `git push origin sprint30/track-d`.
- Push to `origin` (nromey). **NEVER `upstream`.**
- Never `git add -A` or `git add .`.

**Do not edit:** `CLAUDE.md` (flag needed changes in your report — the orchestrator applies them),
`docs/CHANGELOG.md`, `docs/help/md/*` (Track E monopoly).

**You are unattended. Never block on a question.** Take the most defensible option, write it down,
keep going. Every report ends with **"Needs Noel"**. If one of your design questions needs an
owner ruling rather than research, route it to `docs/planning/for-noel/` and proceed on the rest —
do not stall the track.

**Investigate before fixing.** Description drift is the house defect class, and your track
contains a perfect specimen of it (see item 5).

---

## Your work

### 1. Task #18's real remainder — build the diagnostic log surface

**The task is two tasks and the audit split them.** The dialog-polish half is overtaken: the
ratified design at `docs/planning/active/diagnostic-log-surface.md` RETIRES the current tracing
dialog. Your job is the design, not the polish.

Task #26 answered the four open questions that were blocking it — see
`diagnostic-log-surface.md:454-484`, under a heading that literally reads "Open questions —
ANSWERED 2026-08-16":

- Capture chord: **`Ctrl+J, Ctrl+D`** (plain D was taken)
- Default: **ON**
- **Export on stop**, confirmed
- `KeepDailyTraceLogs`: **retired immediately**

Build to that design. Where the design is silent, decide and record the decision in the doc.

**If you add or change any key binding — and `Ctrl+J, Ctrl+D` is one — the CLAUDE.md keyboard
audit applies in full.** That includes updating `docs/help/md/keyboard-reference.md` (you cannot,
it is Track E's — so put the exact line E must add in your report), Command Finder keywords, and
**PRESSING THE KEY on a real build**. A binding shipped completely dead on 2026-08-13 because it
compiled, reviewed clean, and was never pressed: the handler tested `e.Key == Key.L`, which is
never true while Alt is held, because WPF reports `Key.System` and puts the real key in
`e.SystemKey`. Compiling is not verification.

### 2. Task #78 — offer the trace at the moment something fails

Confirmed absent. Today: the per-radio save failure says only "See the trace file for details"
(`SettingsDialog.RadioProfile.cs:815-820`); the `SaveForRadio` self-heal is retry-and-trace with no
user-facing hook (`RadioConfig.cs:502-530`); `CrashReporter.vb` is crash-path only. The task's
plumbing claims (AutoFlush, shareable live trace, SessionArchive) all hold.

**This is the item where every mistake is silent, which is why this track is opus.** An offer that
fires at the wrong moments trains the user to dismiss it, permanently — and an offer that fails to
fire is worse than absent, because the user believes the safety net exists. Be conservative about
what counts as a failure worth interrupting for, and say in your report exactly which conditions
you chose and which you rejected.

**Hard scope fence:** your failure-moment hooks live in the **trace and connect-error layer
ONLY**. You do **NOT** edit `JJFlexWpf\MainWindow.xaml.cs` — that file is Track A's exclusively for
the duration, and A is restructuring it heavily. Where a hook belongs in code A owns, expose the
event from your own layer and put a **one-line wiring note** in your report. The orchestrator
hands it to A or applies it post-merge.

Note `SettingsDialog.RadioProfile.cs` is also Track A's. Same rule: expose, note, do not wire.

### 3. Task #92 — 2.2 GB in AppData with no retention policy

Measured on the developer's own machine: `%AppData%\JJFlexRadio` is **2.2 GB**.

- **Errors: 1.8 GB.** Three full-memory `.dmp` files at 517 MB, 488 MB, 429 MB (4-7 August) plus
  their zips. Eight files. Nothing prunes them.
- **firmware: 369 MB** of downloaded images. Re-downloadable by definition — pure cache.
- **Traces: 14 MB archived, plus 34 MB of loose `JJFlexRadioTrace-*.txt` at the folder root** (30
  files). This is what prompted the report and is the SMALLEST part of the problem.
- **WebView2: 37 MB.**

Nothing in the application ever mentions any of this, so it lands hardest on the operator least
able to notice a folder quietly growing.

**The design tension, which must NOT be resolved by pruning hard:** the crash reporter's entire
value is having the dump when support asks for it. Deleting dumps eagerly defeats the feature.
The honest shape is **"keep the most recent N, and never delete one that has not been submitted or
explicitly dismissed"** — the same logic `backup-claude-state-to-nas.ps1` already uses with
keep-last-12. Three is probably the right N given the size. Zips are cheap; the `.dmp` files are
the cost.

- **firmware** is a cache, so age-out is safe. Consider clearing after a successful update.
- **Trace pruning exists but misses the loose files.** Auto-prune covers `Traces\` only, not the
  `JJFlexRadioTrace-*.txt` files at the folder root. Noel also asked for MANUAL control: archive
  or delete on demand, **including files newer than the auto-prune horizon**, and a way to drop
  large files that are no longer interesting.
- **Surface the total somewhere honest.** The diagnostics surface is the natural home;
  `DiagnosticSnapshot` already reports the trace folder location.

The manual controls belong in the same surface as the tracing explanation — a place that explains
what tracing is AND lets the operator manage what it has produced.

### 4. The fictional `_isTracing` state

`TraceAdminDialog` initializes `_isTracing` to false on every open, and `NativeMenuBar.cs:1648-1665`
never passes live `Tracing.On`. So opening the dialog **mid-trace announces "Start tracing" for a
trace that is already running**, and pressing it restarts to a new file — silently discarding the
capture the user was in the middle of.

The 2026-08-17 sweep fixed the frozen accessible name in this dialog
(`TraceAdminDialog.xaml.cs:65-77`, Content and name change together) — but that fix now faithfully
reflects a fictional state.

**Fix it even though the dialog is being retired.** It survives at least one more release, and
this is exactly the failure the reporting pipeline exists to prevent.

Also: no plain-language explanation of tracing or of the levels exists in either dialog — the
level combo is five bare words. Write that prose; put it in your report for Track E to wire.

### 5. The duplicate version assembler — a specimen of the house defect

`AboutDialog.xaml.cs:202-229` carries a **second, independent version assembler** that re-derives
FlexLib's version. `Radios\DiagnosticSnapshot.cs:169-178` has a doc comment explicitly forbidding
exactly that. One assembler is the rule; there are two.

It is small, exact, and it is in the component built to fight description drift. Collapse it onto
`DiagnosticSnapshot` (which already renders .NET, FlexLib, Opus via live `opus_get_version_string`,
PortAudio revision-first, WebView2, speech backend, OS, trace paths, braille at lines 478-543).

**Version traps to respect while you are in there** — CLAUDE.md documents both:

- **PortAudio's version string lies.** It reports `PortAudio V19.7.0-devel, revision a880212`.
  Upstream never bumped 19.7.0, and a five-year-old build reports identical text. **The revision
  suffix is the only honest identifier.** `PortAudioDisplay()` at 528-543 already renders it
  revision-first and never as a bare 19.7.0 — preserve that.
- **FlexLib's version comes from the vendored tree, not from memory or from this file.** Main
  vendors 4.2.20.41343 as of 2026-08-11. Verify with `git log --oneline -1 -- FlexLib_API/` rather
  than trusting any prose, including CLAUDE.md's, which has been wrong four times.

### 6. Task #32 residual — optional, small

The audit closed #32 (installer file list) as done, with one harmless residual: the generated
deleteList contains `Delete` lines for `.pdb`/`.xml` files the installer never ships. NSIS no-ops
on missing files, so nothing breaks — but the two scripts disagree about what shipped. Reconcile
`generate-deletelist.ps1:33-43` with the NSIS exclusions (`install template.nsi:85-90`) if you have
room. Lowest priority item in your track; skip it without guilt if the pipeline work is large.

---

## Files you own

- `JJFlexWpf\Dialogs\TraceAdminDialog.xaml` and `.xaml.cs`
- `JJTrace\Tracing.cs`
- `globals.vb` — trace flags and `BootTrace` only
- `JJFlexWpf\Dialogs\AboutDialog.xaml` and `.xaml.cs`, `Radios\DiagnosticSnapshot.cs`
- `JJFlexWpf\NativeMenuBar.cs` — the trace menu wiring only (Track A also touches this file for
  gated menu items; stay in your region)
- `CrashReporter.vb`, and whatever new surface the diagnostic-log design calls for
- `docs/planning/active/diagnostic-log-surface.md` — update it as you build
- `install.bat`, `install template.nsi`, `generate-deletelist.ps1` — item 6 only

**Verify which About dialog is live before editing.** Both `AboutDialog.xaml` and
`AboutProgramDialog.xaml` exist. Description drift is the house defect class; do not assume.

## Collisions you must know about

- **`JJFlexWpf\MainWindow.xaml.cs` — Track A's, exclusively. You are fenced out.** Expose events,
  file wiring notes.
- **`SettingsDialog.RadioProfile.cs` — Track A's.** Same rule.
- **`globals.vb` — you and possibly Track E** (earcon defaults). Single-line scale; named so
  neither of you is surprised.
- **`NativeMenuBar.cs` — you and Track A.** Different regions.

## Merge position

**Fourth**, after A. That ordering is deliberate: your failure hooks get wired against the connect
flow as A actually reshaped it, not as it used to be. Build clean before you declare done — a
conflict-free merge proves nothing.

## Your report

What landed per item, which failure conditions you chose to offer the trace on and which you
rejected, the exact `keyboard-reference.md` line Track E must add, the tracing-explanation prose
for E to wire, wiring notes for Track A, changelog lines in the user-facing house voice, any
CLAUDE.md corrections you found, and **Needs Noel**.
