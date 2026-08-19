# Sprint 30 — Track B — Clean Signal

**Worktree:** `C:\dev\jjflex-30b`  **Branch:** `sprint30/track-b`  **Base:** `honest-tx-audio` @ `972e1438`
**Model:** fable  **Class:** BUILDABLE

Read `docs/planning/agile/sprint30-rescue-squelch-pileup.md` for the sprint's shape, and
`docs/planning/agile/sprint30-task-audit.md` before you trust ANY task description — fourteen
tasks marked pending are already done, and three have descriptions that would actively mislead
you. Yours are among the three.

You own the sound path. Three of your four items are measurement work: instrument first, then fix
from numbers, not from plausibility.

---

## House rules — these apply to every track, read them once, obey them throughout

**The user is blind and uses NVDA.** He is not a stakeholder at a distance; he is the person who
will operate every line you write.

- **No tables, no ASCII art, no diagrams** in any file you write — reports, docs, comments,
  anything. Prose or bullet lists.
- **Every control gets `AutomationProperties.Name`** (WPF) or `AccessibleName` (WinForms).
- **Keep disabled or unsupported controls OUT of the tab order.**
- **Do not put long explanations in `AutomationProperties.HelpText`.** NVDA reads HelpText as the
  control's description ON FOCUS, so text parked there is recited every time the user tabs past.
  Discovered the hard way on 2026-08-18; it is Track E's blocking first item. Need an explanation
  on a control? Write it in your report; E wires it to the on-demand mechanism it is building.
- **Visual layout still matters.** Grouping and placement, not just tab order.

**Speech core quarantine.** You may NOT edit `Radios\Speech\*`, `Radios\ScreenReaderOutput.cs`, or
change announcement TIMING anywhere. Track F owns those and runs live with the user's ear. You may
CALL `ScreenReaderOutput.Speak(...)`. If you think the speech core needs a change, report it.

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

Build the **project**, not just the solution. Verify the timestamp is current — `--no-incremental`
does NOT guarantee freshness; `dotnet clean` first when you need certainty. Four other agents are
building concurrently in sibling worktrees; separate `bin`/`obj`, so no collision, just slower.

**Commits and pushes.**

- Commit per completed item. Message format: `Sprint 30 Track B: <description>`.
- **Push after every commit**: `git push origin sprint30/track-b`.
- Push to `origin` (nromey). **NEVER `upstream`.**
- Never `git add -A` or `git add .` — stage specific files.

**Do not edit:** `CLAUDE.md`, `docs/CHANGELOG.md` (put changelog lines in your report),
`docs/help/md/*` (Track E monopoly — put doc content in your report).

**You are unattended. Never block on a question.** Take the most defensible option, write it down,
keep going. Every report ends with a **"Needs Noel"** section.

**Investigate before fixing.** The dominant defect class here is *description drift* — a comment,
doc or task body describing something that no longer exists. Two of your four tasks are flagged
DRIFTED by the audit. Verify before you change.

---

## Your work

### 1. Task #12 — paWinWasapiAutoConvert for the 44.1 kHz shared-mode refusal

**The task's framing is stale twice over. Read this instead of the task body.**

The code claim still holds: no `PaWasapiStreamInfo` binding exists anywhere, and both stream
builders null `hostApiSpecificStreamInfo` (`Audio.cs:112`, `MicProbe.cs:430`).

What changed:

- The task says "decide with #61". **#61 is decided and shipped** — you stand alone now.
- That decision **raises the stakes**: WASAPI is now the DEFAULT host API (`Devices.cs:624`), so
  the shared-mode rate refusal sits on the default path instead of an opt-in one.
- **But there may be nothing to fix.** A rate-negotiation fallback ladder exists at
  `Audio.cs:236` — 48000 down to 8000, including 44100. It may already absorb the refusal by
  opening at the device's native rate.

**So the job is a determination first, a fix second:** does the negotiation ladder make
AutoConvert unnecessary, or does a 44.1-locked device still fail to open? Only bind the struct if
the answer is the second one. Either answer is a good deliverable; a struct bound to solve a
problem that no longer exists is not.

### 2. Task #29 — tone monitor clicks, a 44.1 kHz provider on a 48 kHz path

The instrumentation the task asked for has landed: statusFlags logging at `Audio.cs:955-1000`,
first-appearance-per-flag plus close summaries. So the next repro will finally say whether
PortAudio saw glitches at all — which discriminates "we are feeding it badly" from "it is
resampling badly".

The bug itself is untouched. Suspects: `JJFlexWpf\VoicedToneSampleProvider.cs` and
`ContinuousToneSampleProvider.cs` producing at 44.1 against a 48 kHz stream.

Same family as #17 — instrument and measure both in one pass, so the final sitting can compare
ear against numbers once.

### 3. Task #17 — why the decoded PC-audio stream arrives quiet

**The task's anchor is gone.** It says "4.0x was hardcoded"; that boost is now the operator-facing
`PcOutputVolumeDbSetting`, 0-24 dB, default 12 (`FlexBase.cs:9483-9501`, applied at 12066).

**The audit surfaced a sharp new lead. Check this before touching anything else:** a comment at
`FlexBase.cs:12060-12062` claims the Opus path "bypasses FlexLib's RXGain scalar" — and 330 lines
later, at **12394**, the code sets `RXGain = 50` on that very same channel. If 50 is a mid-scale
attenuation, that could be most of the missing level, and the comment is simply false. Find out
what FlexLib actually does with that scalar, then re-measure.

**The coupled default, which is the part that bites at ship time:** if you make the source arrive
hotter, the existing +12 dB default becomes too loud in the same release. Whatever you change,
state what the default should become and why.

**Deliverable includes numbers.** Measure digital RMS (and LUFS if the existing helpers make it
cheap) on the decoded stream before and after. The final test sitting will compare the user's ear
against your figures; without them, "does it sound right" has nothing to sit against.

### 4. Tasks #44 and #94 — mic profiles, and the ownership question

**Read task #94 in full before starting. It is a design task with a hard fence.**

The task's "no PC-side field at all" is stale — `AudioChainPreset` now carries `PcInputDevice`
(line 58) plus `RadioMicInput`, TX EQ and a schema version. Still missing: PC-side gain, PC NR,
sample rate, and the operator/rig two-file split that is the actual design.

**What you DO:**

- Implement the **PC-side half only**: the operator/rig split inside `JJFlexWpf\AudioOutputConfig.cs`
  and `Radios\AudioChainPreset.cs`. A preset records what it was tuned for; a mismatch on load is
  **announced, not absorbed and not silently corrected**.
- Write the **design doc** for the ownership model, and route its open questions to
  `docs/planning/for-noel/`.

**What you do NOT do, and why each fence exists:**

- **Do NOT touch `Radios\RadioConfig.cs`.** That is Track A's file this sprint, exclusively.
  The ownership flag belongs there, so it waits.
- **Do NOT implement the ownership flag.** The recommended design is not ratified.
- **Do NOT apply the `diag/don-audio-708` mic-profile auto-select.** That branch (origin, commit
  `7b2c427e`) carries nineteen lines guaranteeing a mic profile whenever the radio's selection is
  empty — pcap-diffed against SmartSDR, and it fixes a real silent-TX failure. But applying it
  unconditionally **writes to shared radio state on what may be a guest connection**. That is
  exactly what the ownership answer gates.

**The finding that makes ownership undecidable from registration, and it is worth understanding
because it will tempt you:** Noel proposed deriving ownership from SmartLink registration — your
radio if it is registered to your account. It fails on the case that prompted the question, tested
the same day: he connected to Margaret's radio **using Margaret's account**. The trace reads
"Connecting to MargaretGaffney over SmartLink as mmgaffney@comcast.net". A registration test would
have called him the owner, because to SmartLink he was.

Registration answers **who has access**, not **whose radio it is**. Those coincide for a solo
operator and diverge the moment anyone helps anyone else — which is most of what the tester pool
does. Two more cases it cannot see: a LAN-only radio has no registration at all, and Don's 6300
lives at Tony's house (local to Tony, remote to Don, unambiguously Don's), so physical location
does not settle it either.

**Conclusion to build the doc around:** ownership is a per-radio flag the operator SETS.
Registration or local discovery may seed a first guess; neither can be the source of truth.
Recommendation to put to Noel, not to implement: do not overload "save preset" with "write to
radio" — keep the Workshop preset PC-side and portable, and make writing to the radio a separate
explicit action with its own verb, surfaced only on a radio the operator has marked as theirs.
Two destinations, two verbs, no ambiguity about what a Save just did.

### 5. Task #54 — nothing to build

The audit closed it: the finding is already durably recorded at `JJPortaudio\JJPortaudio\Devices.cs:282-294`
("Built-in versus a jack is NOT claimed, and that is a finding, not an omission"), with the
evidence and the KSPROPERTY alternate route named. **Do not build anything.** Confirm the comment
is still there and still true, and say so in one line of your report.

---

## Files you own

- `JJPortaudio\JJPortaudio\Audio.cs`, `Devices.cs`, `MicProbe.cs`
- `JJFlexWpf\VoicedToneSampleProvider.cs`, `ContinuousToneSampleProvider.cs`
- `JJFlexWpf\Dialogs\AudioDevicesDialog.xaml` and `.xaml.cs`
- `JJFlexWpf\AudioOutputConfig.cs`, `Radios\AudioChainPreset.cs`
- `JJFlexWpf\Dialogs\AudioWorkshopDialog.xaml` and `.xaml.cs` — preset behaviour only
- `Radios\FlexBase.cs` — **the RX/Opus decode path ONLY**. See collisions.
- A design doc under `docs/planning/` and its questions under `docs/planning/for-noel/`

## Collisions you must know about

- **`Radios\FlexBase.cs` — you and Track A.** A touches REM ON plumbing and discovery/name
  handling. **You merge FIRST**, deliberately: your changes are surgical, and A (running on opus)
  is the right party to resolve. Keep your edits tightly scoped to the decode path so A's
  resolution is easy.
- **`AudioWorkshopDialog.xaml.cs` — you and Track A and Track E.** A gates the radio-side value
  controls (`UpdateRadioControlAvailability` and neighbours); you change preset behaviour; E adds
  help later. Stay out of the gating code.
- **`AudioOutputConfig.cs` — you and Track E.** You may restructure; **E adds fields only, never
  structure.** If you rename a public accessor, say so loudly in your report — E reads it.

## Merge position

**Second**, right after G. Small and surgical by design, so the hard `FlexBase` resolution lands
on A rather than on you. Build clean before you declare done.

## Your report

What landed per item, **the measured numbers for #17 and #29**, what you verified and how,
changelog lines in the user-facing house voice, doc content for Track E, and **Needs Noel**.

Known Needs-Noel items already: everything in the #94 ownership design, and the coupled default
question from #17 (if the source gets hotter, what should the +12 dB default become?).
