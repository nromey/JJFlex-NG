# Sprint 30 — Track E — Help Where Your Hands Are

**Worktree:** `C:\dev\jjflex-30e`  **Branch:** `sprint30/track-e`  **Base:** `honest-tx-audio` @ `972e1438`
**Model:** fable  **Class:** BUILDABLE

Read `docs/planning/agile/sprint30-rescue-squelch-pileup.md` for the sprint's shape, and
`docs/planning/agile/sprint30-task-audit.md` before you trust ANY task description.

You own help. **Your first item blocks everything else you will do**, including work other people
are counting on — so do it first and do it properly.

---

## House rules — these apply to every track, read them once, obey them throughout

**The user is blind and uses NVDA.** Every word you write this sprint will be heard, out loud, by
one person, hundreds of times. That is the whole job.

- **No tables, no ASCII art, no diagrams** in any file you write — reports, help pages, docs,
  comments. Prose or bullet lists. A table read by a screen reader is a wall of disconnected cells.
- **Every control gets `AutomationProperties.Name`** (WPF) or `AccessibleName` (WinForms).
- **Keep disabled or unsupported controls OUT of the tab order.**
- **Visual layout still matters.** Grouping and placement, not just tab order.
- **All user-facing prose is flagged for Noel's review.** Your report lists every string you wrote.
  This is a standing rule, not a courtesy — he reads them.
- **Help-page voice:** warm, personal, written for blind ham operators. No internal jargon — no
  WPF, WinForms, AutomationPeer, async, interop, track labels, sprint numbers, bug IDs.
  Screen-reader specifics ARE fine ("your screen reader announces the callsign"); framework
  specifics are not. Read existing pages in `docs/help/md/` for the register.

**Speech core quarantine.** You may NOT edit `Radios\Speech\*`, `Radios\ScreenReaderOutput.cs`, or
change announcement TIMING anywhere. Track F owns those and runs live with the user's ear.
You may CALL `ScreenReaderOutput.Speak(...)`.

**The window-boundary rule.** A screen reader FLUSHES its speech queue on any window change. An
utterance spoken just before a window opens is destroyed — queued or interrupting makes no
difference. Information crossing a window boundary is carried BY the arriving window, folded into
its `Title`. `PendingDisconnectLead` in `globals.vb` is the working example.

**Escape closes every dialog.** No exceptions.

**Build verification.**

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
powershell -Command "(Get-Item 'bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe').LastWriteTime"
```

Build the **project**, not just the solution. `--no-incremental` does NOT guarantee freshness.
Four other agents build concurrently in sibling worktrees.

**Commits and pushes.**

- Commit per completed item. Message format: `Sprint 30 Track E: <description>`.
- **Push after every commit**: `git push origin sprint30/track-e`.
- Push to `origin` (nromey). **NEVER `upstream`.**
- Never `git add -A` or `git add .`.

**Do not edit:** `CLAUDE.md`, `docs/CHANGELOG.md` (changelog lines go in your report).

**`docs/help/md/*` is YOURS ALONE this sprint.** Tracks A, B and D are forbidden from editing it
and will ship doc content in their reports for you to integrate. That monopoly converts four
potential merge conflicts into zero. It also means you are the only one who can fix a help page,
so take the hand-offs seriously.

**You are unattended. Never block on a question.** Take the most defensible option, write it down,
keep going. Every report ends with **"Needs Noel"**.

**Investigate before fixing.** Description drift — a doc describing something that no longer
exists — is the house defect class, and your track is where it concentrates. Two of your items are
literally "this documentation is fiction".

---

## Your work, in this order

### 1. Task #91 FIRST. Nothing else until it lands.

**Ctrl+F1 works. And it achieved nothing.**

On 2026-08-18, seventeen long explanations were moved out of `AutomationProperties.Name` and into
`AutomationProperties.HelpText`, to stop them being recited on every focus change. They are still
recited on every focus change. **NVDA reads `HelpText` as the control's description ON FOCUS.**
Same words, same moment, same cost — they just arrive from a different slot.

**The fix:** a custom attached property that UIA never surfaces — a `DependencyProperty` on a
static helper class in `JJFlexWpf`, e.g. `JJFlexHelp.Text`. UIA has no reason to announce a
property it does not know about, so the text becomes genuinely on-demand instead of relocated.

Work, in order:

- Add the attached property.
- Point `SpeakContextHelpHandler` (`JJFlexWpf\KeyCommands.cs:555-595`) at it, walking up the visual
  tree as it already does. **Keep `HelpText` as a SECOND source, checked after the custom one**, so
  any control that legitimately wants a UIA description still works.
- Migrate the 17 `AutomationProperties.HelpText` values in `SettingsDialog.xaml` that the
  2026-08-18 split created. **Do NOT migrate HelpText that predates 2026-08-18 without checking
  it** — some of it is an intentional description and should stay.

**There is a second, live defect in the same handler you must resolve while you are there.** By
21:05 on 2026-08-18 the handler was instrumented because it reported "no extra explanation" on
controls that DO carry HelpText. The comment at `KeyCommands.cs:560-565` suspects the boundary
between WinForms `Keys`-routed-through-the-shell and WPF dialog focus — where "what has focus" has
more than one answer. **The trace lines are already in place to answer it.** Fix the focus boundary
before you widen coverage; help nobody can surface is just more description drift.

**Why this blocks the rest of your track, and part of Track D's:** #84's remaining work is
widening coverage, and #73 rides the same mechanism. Building either on `HelpText` would spread the
defect across the entire app — every control gaining an explanation would gain a longer focus
announcement at the same time. Track D is also holding explanations in its report for you rather
than parking them in HelpText, on the strength of this item landing.

**PRESS THE KEY.** A keyboard change is not verified by compiling. On 2026-08-13 an Alt+L binding
shipped completely dead one build after being added: the handler tested `e.Key == Key.L`, which is
never true while Alt is held, because WPF reports `Key.System` and puts the real key in
`e.SystemKey`. It compiled and reviewed clean. Build it, run it, press Ctrl+F1, and confirm by
observation — both that it speaks on demand AND that tabbing onto the same control no longer
recites the explanation. That second half IS the acceptance test for this task.

### 2. Task #84 — coverage, after #91

Current coverage is Settings-only: 15 `HelpText` attributes in `SettingsDialog.xaml` and 5
`SetHelpText` calls in the Workshop code-behind (all mic-area). Nothing else in the app.

Widen it well past that. Prioritise controls where the operator's most likely question is "what
does this actually do" — DSP, audio devices, connection paths, diagnostics.

**Soften the empty case.** "No extra explanation for this control" currently fires nearly
everywhere, which reads as broken rather than as "nothing here". Once coverage is real, the
fallback should sound deliberate. Note the task's own framing: the F1 ContextMap
current-context mechanism was never built, and `docs/help/md/keyboard-reference.md:71` documents it
as though it were — that doc lie is yours to correct.

### 3. Task #73 — the DSP controls explain themselves

Confirmed: **zero** HelpText on the Processing controls. The Workshop XAML has none at all; the
five code-behind calls are all mic-area.

The delivery vehicle is #91's mechanism, not the F1 ContextMap the task cites — so this is
sequenced behind #91. **But the prose can be written in parallel**, and should be: start drafting
while you work on #91.

The task asks for explanations shaped as "what it does, and how to set it" — the peak-steady,
loudness-climbing kind of guidance an operator can act on, not a restatement of the control's name.

### 4. Tasks #39 and #43, merged — earcon categories

They are one decision approached from two sides: fix the page, or build the categories.

**Reality has drifted FURTHER from the page than the task says:**

- Line 22's path "Settings > Audio > Earcons" **does not exist** — the master switch is on
  Notifications (`SettingsDialog.xaml:1165-1168`).
- Per-category controls exist **nowhere**. `JJFlexWpf\EarconPlayer.cs` has no enum of any kind:
  roughly 60 flat static methods behind one global `EarconsEnabled` gate.
- Line 11's "lasts for your current session" is **wrong twice over**. The quick-mute persists via
  an immediate config save on toggle (`KeyCommands.cs:1144-1157`) AND shutdown capture
  (`AudioOutputConfig.cs:550`). The page's "temporary layer on top" claim is also false —
  quick-mute and the Settings checkbox are the same single bit.

**Treat `docs/help/md/audio-earcon-control.md` as SUSPECT, not as a spec.** This is a decision the
orchestrator has already taken for you: verify each promised control against reality and report,
rather than faithfully building a wrong list. Where the page promises something sensible, build
it; where it promises something nobody would want, say so instead of building it.

**The hidden second decision, which needs Noel:** *should an earcon mute silently outlive the
session?* It does today, and the page says it does not. Do not guess — route it, and make the code
match whichever behaviour you ship, and the page match the code.

**Persistence:** earcon preferences go into the config store. **Track B may restructure
`AudioOutputConfig.cs` this sprint. You ADD FIELDS ONLY — never restructure it**, and read B's
report for any renamed public accessors. B merges before you, so this is a rebase note rather than
a landmine.

### 5. Task #40 — nothing to build

The audit closed it. Both gaps are resolved: `HelpLauncher.cs:80` maps CommandFinder to
`pages/command-finder.htm` (the page exists, is in all three CHM project files, and
`CommandFinderDialog.xaml.cs:65` requests it), and the ampersand question is a documented
carve-out at `NativeMenuBar.cs:952-954` — native Win32 menus render `&` as access keys and NVDA
reads them cleanly.

Three residuals you may tidy if you have room: the carve-out comment sits on `BuildTransmitItems`
rather than the Help builder; "What's &New" is the only Help item WITH a mnemonic, so the menu's
letter navigation is inconsistent; and CLAUDE.md's no-ampersand guideline lacks the carve-out
(flag that in your report — you cannot edit CLAUDE.md).

### 6. Task #55 — the master test list

Build it in for-noel format under `docs/planning/for-noel/`, runnable as a guided session on
request. Its **first output is the script for this sprint's own final acceptance pass** — section 6
of the sprint plan is the content; your job is to make it a guided session a blind operator can
run start to finish without a sighted helper.

Format rules: bullets and prose, **never tables**. Numbered steps the operator can keep their place
in. Each step says what to do, then what should happen — so a mismatch is obvious without needing
to see the screen. Include the #21 orphan-process check with a helper script, so counting processes
does not require Task Manager spelunking.

The untested backlog it must cover grew by the 2026-08-17 ten-track merge and the entire
2026-08-18 speech day.

### 7. FINAL PHASE — the post-merge sweep. Do not skip this; it is why you merge last.

**After Tracks A, B and D merge**, the orchestrator will message you with the list of controls
they landed — REM ON, the rescue-Home buttons, the reworked diagnostics surface, the preset and
device changes, and D's tracing-explanation prose. **Sweep help onto all of them**, integrate the
doc content from every track's report, and add D's `keyboard-reference.md` line for its
`Ctrl+J, Ctrl+D` capture chord.

**An E that merges early has done half its job.** Wait for the message.

If any binding changed anywhere in the sprint — and D's did — run the **full CLAUDE.md keyboard
audit**: `keyboard-reference.md` updated, Command Finder keywords, context help, changelog line,
CHM rebuild check, and **press the key**.

---

## Files you own

- `JJFlexWpf\KeyCommands.cs` — the Ctrl+F1 handler and the new attached property
- A new static helper class in `JJFlexWpf` for `JJFlexHelp.Text`
- `JJFlexWpf\HelpLauncher.cs`, `ShowHelpDialog.xaml.cs` — fallback behaviour
- `JJFlexWpf\EarconPlayer.cs` — per-category plumbing
- `JJFlexWpf\Dialogs\SettingsDialog.xaml`, `SettingsDialog.xaml.cs`, `SettingsDialog.Audio.cs`
- HelpText/`JJFlexHelp.Text` attributes across many dialog `.xaml` files
- `JJFlexWpf\AudioOutputConfig.cs` — **fields only, no restructuring**
- `docs/help/md/*` — **yours alone this sprint**
- `docs/planning/for-noel/` — the #55 deliverable
- `globals.vb` — earcon defaults only, if needed

## Collisions you must know about

- **`SettingsDialog.xaml` — you and Track A**, the highest textual conflict risk in the sprint.
  A merges FIRST and writes its own help inline. You sweep and resolve in your final phase.
- **`AudioOutputConfig.cs` — you and Track B.** B may restructure; you add fields only.
- **`AudioWorkshopDialog.xaml` / `.xaml.cs` — you, Track A and Track B.** Both merge before you.
- **`KeyCommands.cs` — you and Track F.** F is sequenced after the whole train; no concurrency.
- **`globals.vb` — you and Track D** (trace flags). Single-line scale.

## Merge position

**Sixth and last of the buildable tracks**, by design.

## Your report

What landed per item, **every user-facing string you wrote** (Noel reviews them), what you
verified and how — including the observed result of pressing Ctrl+F1 and of tabbing onto the same
control, changelog lines in the house voice, the CLAUDE.md ampersand carve-out note, and
**Needs Noel**.

Known Needs-Noel items already: whether an earcon mute should silently outlive the session, and
which of `audio-earcon-control.md`'s promised categories are worth building at all.
