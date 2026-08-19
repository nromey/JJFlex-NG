# Speech flow — consolidated survey result (task #86)

Seven agents, 429 textual call sites, ~441 intents once local wrappers are
expanded. Branch `honest-tx-audio`, surveyed 2026-08-18.

## Totals

- INTERRUPT 276
- QUEUE 105
- LATEST 35
- DELETE 24
- dead code 1

Of 664 total speech sites in the repo, 429 pass interrupt-true — 65%.

## The headline

276 sites are already correct and need only a rename. The actual work is
105 queue conversions, 35 coalescing keys, 24 deletions. That is a fifth
of the surface, not two thirds.

## Per cluster

- **Tuning** (82) — INT 62, LATEST 17, QUEUE 2, dead 1
- **Keys and main window** (93) — INT 63, QUEUE 18, LATEST 12
- **Settings** (93) — INT 61, QUEUE 26, DELETE 6, LATEST 0
- **Connect lifecycle** (63) — QUEUE 46, INT 13, LATEST 1, DELETE 3
- **Audio, PTT, GPS** (57 sites / 70 intents) — INT 60, DELETE 5, LATEST 3, QUEUE 1
- **Controls and tail** (24) — INT 14, QUEUE 3, LATEST 2, DELETE 5
- **Uncovered orphans** (17) — INT 3, QUEUE 9, DELETE 5, LATEST 0

The distribution validates the owner's framing empirically: every cluster is
majority-INTERRUPT except connect lifecycle, the one cluster that is a single
continuous series end to end, which is 73% QUEUE.

## Highest-leverage single fixes

1. **`JJFlexWpf/JJFlexDialog.cs:88`** — every dialog speaks its Title with
   interrupt-true on Loaded. 74 files reference the base class. Opening any
   dialog anywhere destroys whatever was being spoken. Confirmed victims:
   MainWindow:2480 (radio disconnected), NativeMenuBar:2024 (update error),
   MainWindow:3989 (SmartLink login), SettingsDialog.Updates:175, and the
   ConnectingDialog path in the traced connect sequence.

2. **`ValueFieldControl`** — 2 LATEST sites, key `value-field:{Label}`.
   48 field declarations across 4 hosts (ScreenFieldsPanel 22, AudioWorkshop
   17, NoiseProfiles 5, AudioLevels 4). One fix corrects coalescing for every
   adjustable value in the application. No live-region collision: the control
   deliberately suppresses its own UIA announcement during adjustment.

3. **Local wrappers** — one signature change re-types dozens of callers:
   `NativeMenuBar.SpeakAfterMenuClose` 85, `ScreenFieldsPanel.ToggleRig` 33,
   `KeysDialog.Announce` 20, `StaticIpControl.Report` 17, `AudioDevicesDialog.
   Announce` 13, `SmartLinkSignUpDialog.SetStatus` / `SmartLinkLoginForm.
   SetStatus` 11 each, `AudioWorkshopDialog.SetToggle` 10,
   `NoiseProfiles.Speak` 9, `ScreenFieldsPanel.ToggleBoolRig` 10.

4. **`globals.vb:2735`** — `If(CurrentRig?.Name, "radio")` passes the literal
   "Unknown" sentinel through to phase speech and every slow-connection
   diagnostic. One line. This is the other half of yesterday's half-fixed
   "Connecting to Unknown".

## The three rulings needed before code

### 1. Safety tier for transmit warnings

Plain INTERRUPT cuts what is speaking but does NOT flush what is queued, so
stale readouts can play after a safety warning. Sites arguing for an
interrupt-and-flush tier: `PttSafetyController:408` ("Transmit ending now!"),
`:333` via its three forceSpeech callers (timeout, hard kill, ALC release),
`AudioWorkshopDialog:4215` (hardware keying still transmitting), `:4178`
(radio did not key), `:2511` (armed tone outside TX filter).

Separately: `PttSafetyController:396` "Transmit timeout soon" is Critical but
QUEUED today, so it can sit behind stale slider values during a lock.
Recommend INTERRUPT minimum regardless of the tier decision.

### 2. Speak versus live region — 34 regions across 20 files

`AutomationProperties.LiveSetting="Polite"` regions exist in 20 files, 13 of
them in SettingsDialog.xaml alone. In those files code both Speaks AND updates
the live region. Today interrupt-true suppresses the live-region utterance, so
it is heard once. A mechanical interrupt-to-queue conversion makes both
survive and the operator hears everything TWICE.

Affected files needing a channel decision before any bucket change:
StaticIpControl (2), AudioDevicesDialog (3), AudioWorkshopDialog (2),
ClusterDialog, CommandFinderDialog, DefineCommandsDialog, EarconScratchpad,
EscDialog, ExportDialog, GpsStatusDialog, ImportDialog, KeysDialog,
LOTWMergeDialog, SettingsDialog (13), UpdateAvailableDialog, LogEntryControl.

Caveat to verify at the radio: JAWS live-region reliability differs from NVDA.

### 3. Deliberate double-speak races

`ScreenFieldsPanel` checkbox family (402/412/439/447/462/475/1059/1067) —
the comment documents that interrupt is deliberate, to beat NVDA's own
"checked / not checked". Same pattern at `NativeMenuBar:1788`, which cuts
NVDA's post-menu title re-announcement. Either keep winning the race or
DELETE and let UIA speak. Needs one decision applied consistently.

## Migration hazard summary

The bool-to-enum map is safe for the 276 INTERRUPT sites and for queue
conversions in files with no live region. It is NOT safe in the 20
live-region files. Sequence the work so those files are handled by hand.

Also: the delegate seams `globals.vb:2544` and `:2673`
(`_callbacks.ScreenReaderSpeak`) forward a raw bool. They must carry the
intent enum or RigSelectorDialog stays mechanism-typed no matter what the
rest of the migration does.

## Description drift found along the way (separate defect list)

- MainWindow 3766/3773 claim F10/F11 for mode cycling; actual Alt+M / Alt+Shift+M
- MainWindow region header 3660 claims Ctrl+Shift+F for SpeakFrequency; owned by ToggleFreqReadout
- MainWindow `ShowActionToolbar` uncalled since Sprint 28 Phase 3.5; site 3369 dead
- FreqOutHandlers `EnterFreqDigit`:598 and FrequencyDisplay `AnnounceField`:718 — zero callers
- FreqOutHandlers `AdjustFreq` doc advertises positional digit entry and "-N" step entry; neither exists
- FreqOutHandlers:1225 interrupt rationale stale — SilentTextBox already suppresses UIA change events
- FreqOutHandlers `AdjustSMeter` claims "no interactive keys" but handles Space plus universal keys
- SettingsDialog.xaml:978 comment says "diagnostics.md", code uses "networking-diagnostics.md"
- SettingsDialog.RadioSetup:1055 no-rig guard unreachable, would leave toggle un-reverted
- SettingsDialog.Audio:257/271 guards unreachable (panel collapsed)
- CrashReporter:516 guard unreachable — BuildUploadBundle never returns empty
- Dead WPF twins of both AutoConnect dialogs; live ones are WinForms via globals.vb:2306
- NoiseProfilesDialog:311 hardcodes hotkey claim "Control J then Shift S" in an utterance — verify against leader map
- AuthFormWebView2:462 can misdescribe a cookie-skipped page
- DebugInfo:159 spoken short form diverges from shown long form
