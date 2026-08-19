# Sprint 30 — Track A — Rescue Centre

**Worktree:** `C:\dev\jjflex-30a`  **Branch:** `sprint30/track-a`  **Base:** `honest-tx-audio` @ `972e1438`
**Model:** opus  **Class:** BUILDABLE

Read `docs/planning/agile/sprint30-rescue-squelch-pileup.md` for the sprint's shape, and
`docs/planning/agile/sprint30-task-audit.md` before you trust ANY task description — fourteen
tasks marked pending are already done, and the drift is worst exactly where work has been
heaviest.

You own the connect experience, end to end. You are the biggest diff in the sprint and you own
the hub file. Everything downstream waits on you.

---

## House rules — these apply to every track, read them once, obey them throughout

**The user is blind and uses NVDA.** He is not a stakeholder you are building for at a distance;
he is the person who will operate every line you write, tonight or tomorrow. That single fact
drives most of what follows.

- **No tables, no ASCII art, no diagrams** in any file you write — reports, docs, comments,
  anything. Prose or bullet lists. A table read by a screen reader is a wall of disconnected
  cells.
- **Every control gets `AutomationProperties.Name`** (WPF) or `AccessibleName` (WinForms), and an
  `AutomationRole` where the role is not obvious. A control a screen reader cannot name does not
  exist to this user.
- **Keep disabled or unsupported controls OUT of the tab order.** Tabbing onto something that
  cannot work is worse than it being absent — it costs a keystroke and teaches a wrong model.
- **Do not put long explanations in `AutomationProperties.HelpText`.** NVDA reads HelpText as the
  control's description ON FOCUS, so text parked there is recited every single time the user tabs
  past. This was discovered the hard way on 2026-08-18 and it is Track E's blocking first item.
  If a control needs an explanation, write it in your report and Track E will wire it to the
  on-demand mechanism it is building.
- **Visual layout still matters.** Grouping and placement, not just tab order.

**Speech core quarantine.** You may NOT edit `Radios\Speech\*`, `Radios\ScreenReaderOutput.cs`,
or change announcement TIMING anywhere. Track F owns those, runs live with the user's ear in the
loop, and is the only place speech behaviour can actually be verified. You may CALL
`ScreenReaderOutput.Speak(...)`. If you believe the speech core needs a change, write it in your
report and stop.

**The window-boundary rule — this is load-bearing for your track specifically.** A screen reader
FLUSHES its speech queue on any window change. An utterance spoken just before a window opens is
destroyed, and it makes no difference whether it was queued or interrupting — both were tried on
2026-08-18 and the operator heard the new window's title and nothing else, every time. So:
**information that must cross a window boundary is carried BY the arriving window**, folded into
its `Title`, not spoken before it. `PendingDisconnectLead` in `globals.vb` is the working example;
copy that pattern. Your whole track is window transitions, so you will need this repeatedly.

**Escape closes every dialog.** No exceptions.

**Build verification.**

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
```

Build the **project**, not just the solution — building the solution can skip the main project.
After a build that matters, verify the exe is actually fresh:

```
powershell -Command "(Get-Item 'bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe').LastWriteTime"
```

If the timestamp is not current, the build did not produce a fresh binary. `--no-incremental`
does NOT guarantee freshness; `dotnet clean` first when you need certainty. Stale binaries have
wasted entire testing sessions here.

Four other agents are building concurrently in sibling worktrees. Each has its own `bin`/`obj`,
so you will not collide, but do not be surprised by slow builds.

**Commits and pushes.**

- Commit per completed item, not per session. Message format: `Sprint 30 Track A: <description>`.
- **Push after every commit**: `git push origin sprint30/track-a`. This is the durability model —
  if you die, the cost is only your in-flight item.
- Push to `origin` (nromey). **NEVER `upstream`** (that is KevinSShaffer's repo).
- Never `git add -A` or `git add .` — stage specific files.

**Do not edit these, ever:** `CLAUDE.md`, `docs/CHANGELOG.md` (the orchestrator writes the
changelog once at sprint close, in the house voice — put your changelog lines in your report),
`docs/help/md/*` (Track E has a monopoly this sprint — put doc content in your report).

**You are unattended. Never block on a question.** When you hit a decision only the owner can
make, take the most defensible option, write it down, and keep going. Every report ends with a
**"Needs Noel"** section listing those calls. A track that stalls waiting for an answer has
failed; a track that proceeds on a stated assumption has not.

**Investigate before fixing.** The dominant defect class in this codebase is *description drift* —
a comment, doc, or task description that describes something which no longer exists. Verify the
thing you are about to change still works the way you were told. If a task description conflicts
with the code, the code wins and you say so in your report.

**Do not modernise Jim-era code** (`skcc`, HamQTH, QRZ lookup paths). It is slated for wholesale
replacement. Suppress warnings there with a reason; do not refactor.

---

## Your work

### 1. Rescue-Centre Home — the headline

When no radio is connected, Home shows a **limited page offering only what actually works
offline**. This supersedes task #90 by construction: instead of gating individual controls, the
page simply does not offer what cannot work.

Assumed button set (owner has not ruled; proceed on this and flag it): **Connect, Settings, Audio
Workshop, Help, Exit.**

**Scope decision already taken for you: startup only.** Do NOT build the mid-session
radio-lost case. That is a window transition during live operation with all the window-flush
lessons applying, and it materially changes the design. Flag it in Needs Noel.

The Audio Workshop stays on this page deliberately — it is usable offline, because the microphone
check exists precisely so an operator can prove their input works without a radio. Which means:

**#90's real remainder is yours.** The audit found only HALF of #90 is fixed. Five checkboxes
(MicBoost, MicBias, Compander, Processor, Monitor) are gated when `_rig == null`
(`AudioWorkshopDialog.xaml.cs:589-599`). The radio-side VALUE controls are NOT: `_micGainControl`,
`_txFilterLowControl`, `_txFilterHighControl`, `_micSourceControl` have handler-only guards, stay
tabbable, and `ValueFieldControl` cheerfully speaks changing values with no rig attached — the
same confident lie the checkbox fix killed. Gate whole SECTIONS rather than enumerating controls,
so anything added later inherits the rule. Note the code comment at 567-588 already documents this.

One correction to carry: there is **no radio-side noise-reduction control** in the Workshop. The
cleanup section is PC-side by design and is valid offline.

### 2. Licence gating

Plus-gated features either genuinely work, or explain themselves. **No silent absences.** Use the
existing Feature Availability pattern — disabled with a reason, per CLAUDE.md's accessibility
guidance — rather than hiding.

```csharp
if (theRadio.FeatureLicense?.LicenseFeatDivEsc?.FeatureEnabled == true) { ... }
if (theRadio.DiversityIsAllowed) { ... }        // 2-SCU radios only
if (theRadio.AvailableSlices >= 2) { ... }      // MultiFlex awareness
```

Detect by `theRadio.Model`, `theRadio.DiversityIsAllowed`, `theRadio.MaxSlices` — never hardcode
model names.

**Open question you cannot answer from code:** on a purely local connect with no SmartLink
account, does `FeatureLicense` populate at all? Find out (a trace will tell you), and if it
cannot, decide what Plus-gated features should say in that state. Whatever you decide, it must not
claim a feature is unavailable when the truth is "we do not know".

**The stubbed `FeatureLicenseChangedHandler` in `MainWindow.xaml.cs` gets a real implementation.
Do not rename or relocate it** — Track D's failure hooks and future gating work reference it by
name. If you conclude it should move, report it, do not move it.

### 3. Registration warning on local connect, with the local-only offer

A local connect currently produces a registration complaint that has no business being there.
Replace it with a **local-only offer**: appears once, reads clearly, and the choice sticks.

Store the choice in the per-radio config, keyed by serial (that convention already exists in
`Radios\RadioConfig.cs`). Assumption taken: local-only is an **app-side** setting, not radio-side.

### 4. Task #85 — a local connect announces SmartLink activity it never asked for

**The task body names the wrong prime suspect.** It blames `AnnounceLoadedState` ("Remote
connection list loaded", `RigSelectorDialog.xaml.cs:777-782, 2018-2022`) — which does exist, but is
verbosity-gated and non-interrupting. The audit found stronger candidates:

- `AutoStartRemote` speaks **"Starting remote radios for your account."** on Loaded
  (`RigSelectorDialog.xaml.cs:649-656`)
- `StartRemoteFlow` speaks **"Connecting to SmartLink as {email}."** (1961-1968), plus a
  "Connecting to SmartLink..." window

Both are **unconditional and NOT verbosity-gated**, going through the legacy ungated overload.
That is literal SmartLink language on a local connect, whenever the per-account opt-in
(`SmartLinkAccountManager.cs:966-974`) is set.

Note also that `ScreenReaderOutput.cs:27-31` classifies account/discovery chatter as
Diagnostic-class, which those sites ignore.

**Keep the verbose-trace confirmation step.** The task warns that an attribution was already made
wrongly once by plausibility. Confirm with a trace before you change anything — just point the
trace at the `AutoStartRemote` chain first.

### 5. Task #79 — learn a connection path from a trend, never overwrite a choice

Current state: `PathChain` is a bare list with no provenance (`RadioConfig.cs:189-201`),
`LastSeenRemote` is a single bool (291), `EffectiveChain` is naive
(`RigSelectorDialog.xaml.cs:74-85`).

**Accelerant the task does not know about:** `Radios\ConnectionHistory.cs:9-49` already records a
**10-entry per-radio ring of timestamped path, outcome and duration**, written to
`radios\{serial}\connect-history.json`. Its header names the very policies this task wants and
declares them deliberately out of scope. **The substrate exists and nothing reads it. Start there.**

The contract, and it is the part with an invisible failure mode: **a learned value only ever
PREFILLS. A stored explicit choice always wins.** You cannot see the violation of that rule in a
diff — it looks identical to correct code until a user's deliberate setting silently evaporates.
Write a test that proves an explicit choice survives a contradicting trend.

Proposed threshold: **three consecutive successful connects** on a path. Flag it for confirmation.

### 6. Task #74 verification, and #75

Both shipped already per the audit (REM ON at `RadioConfig.cs:253-259` + `SettingsDialog.xaml:854-858`
+ `MainWindow.xaml.cs:2625-2630, 2893-2910`; the discovery-name union merge at
`RigSelectorDialog.xaml.cs:702-717`). **Do not rebuild them.** Verify they still work after your
changes — your rework of the connect path is exactly what would break them — and say so in your
report.

---

## Files you own

- `JJFlexWpf\MainWindow.xaml` and `MainWindow.xaml.cs` — **the hub. Yours exclusively this sprint.**
  Track D is fenced out of it. Track E touches it only post-merge. If another track's work seems to
  need a change here, it files a wiring note instead.
- `JJFlexWpf\Dialogs\RigSelectorDialog.xaml` and `.xaml.cs`
- `JJFlexWpf\Dialogs\SettingsDialog.xaml`, `SettingsDialog.RadioSetup.cs`, `SettingsDialog.RadioProfile.cs`
- `Radios\RadioConfig.cs` — **yours exclusively.** Track B is explicitly fenced out of this file.
- `Radios\ConnectionHistory.cs`
- `Radios\FlexBase.cs` — REM ON plumbing and discovery/name handling ONLY. See collisions below.
- `JJFlexWpf\Controls\ScreenFieldsPanel.xaml.cs`
- `JJFlexWpf\Dialogs\AudioWorkshopDialog.xaml.cs` — the #90 value-control gating only. Track B
  also touches this file; keep your change to `UpdateRadioControlAvailability` and its neighbours.
- `JJFlexWpf\NativeMenuBar.cs` — gated menu items
- `ConnectingForm.vb` if needed

## Collisions you must know about

- **`Radios\FlexBase.cs` — you and Track B.** B touches only the RX/Opus decode path (task #17).
  **B merges before you**, so you will do the resolution. That is deliberate: you are the opus
  track and the right party to resolve the sprint's one hard merge.
- **`JJFlexWpf\Dialogs\SettingsDialog.xaml` — you and Track E**, the highest textual conflict risk
  in the sprint. You merge FIRST. **Write your own help text inline, copying the existing pattern
  — do not wait for E.** E's final phase sweeps and resolves.
- **`AudioWorkshopDialog.xaml.cs` — you and Track B.** B merges first; keep your edit surgical.
- **`RigSelectorDialog.xaml.cs` — you and Track F.** No concurrency: F starts after you merge.

## Merge position

**Third**, after G and B. Build clean before you declare done — a conflict-free merge proves
nothing. On 2026-08-17 two tracks merged with zero textual conflict and a broken build, because
one moved a symbol the other was told to reuse. Git cannot see that.

## Your report

End with a written report containing: what landed per item, what you verified and how, any
changelog lines in the user-facing house voice (warm, first-person, no jargon), doc content for
Track E to integrate, any wiring notes for other tracks, and the **Needs Noel** section.

Known Needs-Noel items already: the rescue-page button set, the mid-session radio-lost case,
`FeatureLicense` behaviour on a local-only connect, the #79 trend threshold, and whether
"local-only" is app-side or radio-side.
