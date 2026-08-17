# Radio Audio Device Picker — Migration Research for Settings → Audio

**Date:** 2026-08-06. **Branch surveyed:** `track/flexlib-4220`. **Purpose:** plan the absorption of the JJ-written "Radio Audio Device" picker (JJPortaudio `devList` form + `Devices` engine + `audioDevices.xml`) into the modern Settings dialog's Audio tab. All claims below carry file paths and line numbers from the current branch. Prose and bullets only, per house rules.

---

## 1. What this thing is, in one paragraph

When "PC audio" is on, JJFlexRadio streams the radio's receive audio to the computer's speakers and the computer's microphone back to the radio, over Opus, through PortAudio. The pair of sound devices used for that — one input (PC mic, becomes TX audio) and one output (radio RX audio playback) — is chosen in a tiny WinForms dialog that dates to the original 2024-03-03 project import (`git log --follow` on `JJPortaudio/JJPortaudio/devList.cs` bottoms out at commit `e68dabc5`, "Add project files" — this is Jim-era code, confirming "JJ written"). The choice persists in `audioDevices.xml`. Nothing else in the app uses these two devices: earcons, meter tones, and CW notifications all go through NAudio via `EarconPlayer`, and the comment at `Radios\FlexBase.cs:8767` confirms DAX is not used. There is no CW-sidetone or DAX device in this picker — it is exactly two devices, PC-side, for the PC-audio path.

## 2. Current-state inventory

### 2.1 The form and engine (JJPortaudio project, C#)

- `JJPortaudio\JJPortaudio\devList.cs` — the picker form logic. WinForms `Form`, `internal` to the JJPortaudio assembly (line 17). One ListBox, Select and Cancel buttons. The form's title bar carries the actual instruction: "Select input device" or "Select output device" (lines 19-20, set at 84 and 89). Selecting nothing and pressing Select raises a `MessageBox` "You must select a device" (lines 23, 102).
- `JJPortaudio\JJPortaudio\devList.designer.cs` — the designer half. ListBox has `AccessibleName = "device list"` and `AccessibleRole.List` (lines 48-49); the "Device list" label (line 44) is visual-only, not programmatically associated; the buttons rely on their `Text` for names (lines 64, 76). Cancel is wired as the form's `CancelButton` (line 84), so Escape works.
- `JJPortaudio\JJPortaudio\devList.resx` — designer resources.
- `JJPortaudio\JJPortaudio\Devices.cs` — the engine and persistence. Public class wrapping:
  - `Device` abstraction (lines 30-45): a full snapshot of the PortAudio device info — name, host API, channel counts, latencies, default sample rate — plus `DevinfoID` (the PortAudio index, re-resolved each session).
  - `cfg` (lines 48-51): the serialized shape — an array of exactly two `Device` slots, input then output.
  - `Setup()` (lines 86-99): initializes PortAudio, builds the device lists, then reads the config file if present.
  - `readCFG`/`writeCFG` (lines 101-146): plain `XmlSerializer` load/save of `cfg`.
  - `GetConfiguredDevice(type, getNew)` (lines 154-164): returns the saved device if it still exists on the system; optionally launches the picker if not.
  - `getNewDevice(type)` (lines 171-201): shows the `devList` form modally, saves the choice to disk immediately on OK (line 196).
- Device enumeration lives in `devList.Setup()` (static, lines 125-228): `Pa_Initialize`, `Pa_GetDeviceCount`, `Pa_GetDeviceInfo` per device, `Pa_Terminate` at the end — a snapshot, not a live subscription. Two load-bearing filters:
  - **Stereo-only.** Input devices must have exactly 2 input channels (line 187, and line 153 for the default); output devices exactly 2 output channels (lines 167, 203). A mono USB headset mic is invisible to this picker. The remark at line 122-124 says so: "we only handle stereo devices at present."
  - **Default device pinned first** with a "Default: " display prefix (lines 42, 146-174), deduplicated from the main sweep (lines 190-215).
- Missing-device handling: `FindDevice` (lines 235-250) matches the saved device against the live system by **name plus both channel counts**, then rewrites `DevinfoID` — so PortAudio index reshuffles between sessions are tolerated, but a renamed or absent device makes `GetConfiguredDevice` return null.
- No-devices-at-all handling: a bare `MessageBox` "No audio devices were detected by PortAudio..." (line 222) and `Setup()` returns false.

### 2.2 Persistence: audioDevices.xml

- Filename constant `audioDevicesBasename = "audioDevices.xml"` at `globals.vb:1736`; full path assembled at `globals.vb:686` as `BaseConfigDir & "\audioDevices.xml"`, where `BaseConfigDir` is `%AppData%\JJFlexRadio` (`globals.vb:651-652`).
- **Keying: none.** One file per Windows user profile per machine. Not per-operator (unlike the `k5ner_Noel_Romey_*.xml` family), not per-radio (unlike the new `radios\<serial>\config.xml` store), not per-account. Two operators on one PC share it; one operator's two radios share it.
- Schema (verified against the live file on this machine): root `cfg`, containing `devs` with two `Device` elements — the input device then the output device — each carrying `DevinfoID`, `Type`, `Name`, `hostApi`, channel counts, four latency values, and `defaultSampleRate`. The `Name` + channel counts are the durable identity; `DevinfoID` is advisory and re-resolved by `FindDevice` each run.
- The file is written by exactly one code path: `Devices.getNewDevice` → `writeCFG` (`Devices.cs:196`), i.e. only when the user OKs the picker.

### 2.3 How the form is reached (four paths)

- **Menu.** `JJFlexWpf\NativeMenuBar.cs:629-630`: "Radio Audio Device" item, added by `BuildAudioItems` (line 555), which feeds both the top-level "Audio" popup (lines 1102-1103) and the Slice → Audio submenu (lines 962-963). Deliberately placed outside the `Rig != null` guard — the doc comment at 552-553 says device setup is "always available (no radio required)." It invokes `MainWindow.AudioSetupCallback` (`JJFlexWpf\MainWindow.xaml.cs:1216`), wired at `ApplicationEvents.vb:111` to `GetNewAudioDevices` in `globals.vb`.
- **Command Finder / hotkey.** `JJFlexWpf\KeyCommands.cs:256-258` registers "Select audio device" (`CommandValues.AudioSetup`, `Radios\KeyCommandTypes.cs:66`) with keywords audio/device/setup/settings/configure/preferences/sound; default binding is `Keys.None` (`KeyCommands.cs:995`), scope Radio — so it is discoverable through Ctrl+/ but has no key out of the box, and it is correctly absent from `docs/help/md/keyboard-reference.md`. The handler flows through `KeyCommandContext.AudioSetup` (`JJFlexWpf\KeyCommandContext.cs:72`), populated at `globals.vb:914`.
- **First-audio-use prompt (partial).** `globals.vb:1771-1809` `EnsureAudioDevicesConfigured(prompt)`: checks both configured devices; if either is missing and `prompt` is true, asks "Audio devices are not configured. Select input and output devices now?" via MessageBox, then runs the two pickers. Called from exactly one place: the `globals.PCAudio` property setter (`globals.vb:1763`).
- **Silent fallback inside the audio thread.** `Radios\FlexBase.cs:8744-8764` (`remoteAudioProc`): builds its own `Devices` from `Callouts.AudioDevicesFile` (plumbed via `OpenParms.AudioDevicesFile`, `globals.vb:2333`, declared at `Radios\AllRadios.cs:2486`) and calls `GetConfiguredDevice(type, getNew: true)` — which will pop the WinForms picker **from the RemoteAudio background thread** (started at `FlexBase.cs:8688-8696`, no `SetApartmentState(STA)`, priority Highest, no owner window). This is the path a first-run machine actually hits.

The driver of all this, `GetNewAudioDevices` (`globals.vb:1740-1745`), has a UX wart worth killing during migration: it calls `getNewDevice(input)` then `getNewDevice(output)` unconditionally, so cancelling the input picker still marches you into the output picker. (`EnsureAudioDevicesConfigured` gets this right — it bails after a cancelled input at lines 1798-1801.) Also note `InputAudioDevice`/`OutputAudioDevice` (`globals.vb:1738`) are write-only vestiges — nothing reads them; FlexBase always re-reads the XML through its own `Devices` instance.

### 2.4 Who bypasses the first-run guard

The `EnsureAudioDevicesConfigured` prompt only protects the `globals.PCAudio` property. Both interactive PC-audio toggles skip it:

- The hotkey context toggle at `globals.vb:917-919` sets `RigControl.PCAudio` directly.
- The menu item "PC Audio On/Off" at `NativeMenuBar.cs:594-599` sets `Rig.PCAudio` directly.

And the connect path: `FlexBase.mainThreadProc` auto-enables PC audio on remote connects at `FlexBase.cs:9634-9637` (`if (RemoteRig & !PCAudio) { PCAudio = true; }`), while a second, older enable site in the connected handler is commented out at `FlexBase.cs:738` (`//PCAudio = true;`). The research queue (`docs/planning/active/research-queue.md:53-58`) asserts PC audio is "NOT auto-enabled on remote connect (the line is commented out)" — that matches line 738 but not 9634, which is live code. Either the queue note over-generalized from line 738, or `mainThreadProc`'s enable is failing silently in the field (its failure mode is exactly `remoteAudioProc` bailing at 8746-8764 with only trace lines). **This needs a live verification before the migration ships**, because it decides what the first-run rescue flow must handle (see risks and open question 5). Whichever way it resolves, on a machine with no `audioDevices.xml` the current behavior is: remote connect → background thread → raw prompt-less WinForms picker (or a silent trace-only failure) — precisely the ms-02 situation flagged at `research-queue.md:55-58`.

## 3. What Settings → Audio already holds

The Audio tab (`JJFlexWpf\Dialogs\SettingsDialog.xaml:202-303`) currently contains, top to bottom: master volume (207-209), an Alerts group with alert volume and the **Alert device** combo (213-226), a Meter Tones group with volume, **Meter device** combo, preset combo, Peak Watcher and meter-speech checkboxes (230-259), Frequency Entry typing sound (263-273), a Braille Display group (277-291), and an Audio Workshop launcher button (295-300). Every control has an `AutomationProperties.Name`; the tab scrolls in a `ScrollViewer`.

Key facts for the merge:

- **The existing device combos are NAudio, not PortAudio.** They are populated from `EarconPlayer.GetOutputDevices()` (`SettingsDialog.xaml.cs:196-226`) and store NAudio device numbers into `AudioOutputConfig.EarconDeviceNumber` / `MeterDeviceNumber` on OK (`SettingsDialog.xaml.cs:982-1010`). `-1` means "Windows default" / "Same as Alerts" (`JJFlexWpf\AudioOutputConfig.cs:17, 35`). The radio-audio picker's PortAudio enumeration is a different stack that can render different display names for the same physical hardware — the merged tab will put both stacks side by side.
- **Its persistence is `audioConfig.xml`, per-operator-ish with a root/Radios dual-copy dance.** `AudioOutputConfig.Load/Save` (`AudioOutputConfig.cs:150-183`) write `audioConfig.xml` into whatever directory the caller passes. In practice two copies exist: root (`%AppData%\JJFlexRadio`) and the connected config dir (`OpenParms.ConfigDirectory` = `BaseConfigDir\Radios`, `globals.vb:2332`). `NativeMenuBar.ShowSettingsDialog` (`NativeMenuBar.cs:1478-1569`) loads per-radio when connected and merges user-global fields from root, saves user-scope fields back to root on OK (1554-1564); `MainWindow` saves the full config to the connected dir at PowerOff (`MainWindow.xaml.cs:481-485`) and loads it at PowerOn (2295) with a CW migrate-to-root pass (2325-2343). This duality has already produced real bugs (the ms-02 CW silence, `research-queue.md:485-509`; the TuningHash unlock miss in `Agent.md:2206`). Worth knowing before choosing to fold anything new into it.
- **Audio Workshop does not overlap.** Despite the help page's phrasing ("pick which output device your radio audio flows to", `docs/help/md/audio-workshop.md:3`), the dialog itself is TX audio sculpting, live meters, and earcon exploration (`JJFlexWpf\Dialogs\AudioWorkshopDialog.xaml.cs:11-14, 24-52`) — radio-side controls, no PC device selection anywhere in it (zero matches for Device/PortAudio/NAudio in its code-behind). The help page should be corrected as a side effect of this work.
- **The Settings plumbing the new controls will ride on already exists:** `OpenSettings(tab)` can open the dialog directly at a named tab (`NativeMenuBar.cs:1473`), and the dialog is a `JJFlexDialog` (Escape-closable per house rule).

## 4. Accessibility state of the current picker, honestly

It is better than nothing and worse than the house standard:

- WinForms, modal, sequential: two dialogs in a row (input, then output) with no announcement that a second one is coming, and — via the menu path — no announcement of why a dialog appeared at all. The window title is the only context, and it does the heavy lifting (devList.cs:84, 89).
- The ListBox is labeled ("device list", role List — designer 48-49), but generically: it does not say *input* or *output*; a screen reader user who missed the title change hears the same control both times. The static label "Device list" is not associated with the ListBox.
- Buttons are fine by accident (WinForms exposes `Text`), Escape works (CancelButton wired, designer 84). Tab order is sane (label 10, list 11, Select 90, Cancel 99) with no dead controls.
- Zero `ScreenReaderOutput` integration: no announcement of the resulting choice, no confirmation that anything saved, no speech when validation fails (a MessageBox appears instead).
- Errors are raw MessageBoxes from library code (`devList.cs:102, 133, 142, 222`) — including, on the `remoteAudioProc` path, from a background non-STA thread with no owner window, where focus handoff to NVDA is unreliable and the dialog can land behind the main window. This same thread can show the full picker (FlexBase.cs:8752, 8759 with `getNew: true`).
- The stereo-only filter silently hides devices; there is no "your device exists but was filtered out" affordance, which reads to a user as "JJ Flex can't see my headset."

## 5. Proposed migration

### 5.1 What moves into Settings → Audio

Add a new group at the **top** of the Audio tab (it is the most consequential audio decision on the page): heading "Radio Audio (PC audio devices)", containing:

- **Output device combo** — "Radio receive audio plays through". First, because RX audio is what most users configure this for.
- **Input device combo** — "Microphone sent to the radio".
- A one-line state readout per combo when unconfigured: the combo shows a "Not configured" placeholder item rather than silently defaulting.
- A **Refresh devices** button (PortAudio enumeration is a snapshot; see risks) that re-enumerates and announces "Device list refreshed, N output and M input devices" via `ScreenReaderOutput`.

Engine work to support it: add a UI-free enumeration API to `JJPortaudio.Devices` (lift the guts of `devList.Setup()` — lines 125-228 — into `Devices`, returning the two lists; today they are `internal static` fields on the form class, `devList.cs:52-53`). Add a `SetConfiguredDevice(type, device)` that writes through `writeCFG`. The `devList` form then has no remaining reason to exist (see 5.3). This keeps `Devices.cs`'s persistence, matching (`FindDevice`), and `cfg` schema completely intact — FlexBase's consumption path (`FlexBase.cs:8744-8764`) does not change at all.

Apply semantics: selections save on Settings OK (same as the rest of the tab). If PC audio is running when the devices change, offer to restart the audio path (stop/start via the existing `PCAudio` setter round-trip) and announce the result; if not running, the next start picks the new devices up naturally since `remoteAudioProc` re-reads the file every start.

### 5.2 What the existing entry points become

- **Menu item stays, retargeted.** "Radio Audio Device" (`NativeMenuBar.cs:629`) becomes "Radio Audio Devices..." and calls `OpenSettings("Audio")`. Muscle memory and help docs keep a working path; the destination is just better.
- **Command Finder entry stays** ("Select audio device", `KeyCommands.cs:256-258`) with the same keywords, retargeted the same way.
- **First-run rescue changes shape.** `EnsureAudioDevicesConfigured` (`globals.vb:1771`) keeps its role but its yes-branch opens Settings at the Audio tab (focused on the output combo) instead of chaining two naked pickers. The two direct-toggle bypasses (`globals.vb:917-919`, `NativeMenuBar.cs:594-599`) get routed through the ensure-check so every road to PC-audio-on passes the same gate.
- **The background-thread picker dies.** `remoteAudioProc`'s `getNew: true` (FlexBase.cs:8752, 8759) becomes `getNew: false`; on null it surfaces a Critical spoken announcement on the UI thread — "PC audio needs a sound device. Press Enter to choose one in Settings" (or an advisory dialog with an Open Settings button, the pattern `OpenSettings` was built for per `NativeMenuBar.cs:1470-1472`) — instead of either popping WinForms UI from a worker thread or dying with only a trace line. This also fixes the silent-failure mode where "no RX antenna" masquerades as an audio-path failure (`research-queue.md:56-58`).

### 5.3 What dies, what stays for compatibility

- **Dies:** the `devList` form (`devList.cs`, `devList.designer.cs`, `devList.resx`), the two-modals-in-a-row UX, the cancel-input-still-asks-output wart in `GetNewAudioDevices` (`globals.vb:1740-1745`), the raw MessageBoxes as primary UX, and the vestigial `globals.InputAudioDevice`/`OutputAudioDevice` fields (`globals.vb:1738`). Recommendation: fully absorb — do **not** keep the old form as a first-run rescue. A rescue path that routes to Settings is strictly better for screen reader users than a rescue path that resurrects the unlabeled-context modal, and keeping the form alive means maintaining two device-selection UIs forever. The form is `internal` to JJPortaudio with exactly one consumer (`Devices`), so removal is contained.
- **Stays:** `Devices.cs` engine essentially whole (enumeration relocated, persistence untouched), `audioDevices.xml` in place and format, `Callouts.AudioDevicesFile` plumbing (`AllRadios.cs:2486`, `globals.vb:2333`), and the `remoteAudioProc` consumption pattern.

## 6. Persistence plan

**Recommendation: keep `audioDevices.xml` exactly as-is — same path, same schema, same single writer.** Reasoning:

- **The scope is already correct.** Sound cards are a property of the machine. The file lives once per Windows profile at `%AppData%\JJFlexRadio\audioDevices.xml`, unkeyed — which is exactly what the per-radio principle (memory `project_per_radio_config_serial_keyed.md`) prescribes for non-radio-state config. Migrating it anywhere would be motion without improvement.
- **Folding into `audioConfig.xml` would make the scope wrong.** `AudioOutputConfig` lives in the root/Radios dual-copy arrangement (`NativeMenuBar.cs:1493-1522`, `MainWindow.xaml.cs:2295, 2325-2343`, PowerOff save at 481-485) that has already caused two field bugs by letting machine-truth and per-context copies drift. Device identity is machine truth; putting it inside a file that exists in two copies invites the exact class of bug the CW flag just exhibited. Also `AudioOutputConfig` is a JJFlexWpf type while the consumer of device config is the Radios layer — folding in would add a cross-assembly dependency for zero user benefit.
- **Folding into `RadioConfig` (the new serial-keyed store, `Radios\RadioConfig.cs:29-45`) would be the wrong axis entirely.** Two operators of one radio do not share a sound card; one operator's two radios do. The new store's own doc comment draws this line correctly ("settings that describe THE RADIO ... rather than the operator", RadioConfig.cs:35-39). Audio devices are the canonical counter-example. No per-radio device override should ship in v1 — if a concrete need appears later (say, a dedicated headset only when operating the remote 6300), `RadioConfig` can grow an optional override that shadows the machine file, and the migration cost of adding that later is near zero precisely because the machine file stays authoritative.
- **Migration cost of keeping it: zero.** Existing installs (Don's, the laptop) keep working with no upgrade step. The only writer moves from `devList`-OK to Settings-OK; the bytes are identical.
- One deliberate schema-adjacent improvement is allowed without breaking anything: tolerate a missing/partial file gracefully in the new UI (it already loads defaults — `Devices.Setup` skips `readCFG` when the file is absent, `Devices.cs:93-96`), and treat "device not found by `FindDevice`" as a first-class displayed state rather than a null.

## 7. Accessibility notes for the new surface

- Both combos get explicit `AutomationProperties.Name` phrased by function, not plumbing: "Radio receive audio output device", "Microphone input device sent to the radio". Follow the existing tab's label-plus-combo `StackPanel` pattern (`SettingsDialog.xaml:219-226`).
- The group heading is a `TextBlock` in the established `DialogLabel` semibold style so it reads as a section landmark in tab order context, matching "Alerts" and "Meter Tones".
- **No dead controls in tab order:** if PortAudio reports zero usable devices, replace the combos with a single explanatory TextBlock ("No stereo audio devices were detected...") and keep the Refresh button; do not present empty disabled combos.
- Selection changes announce via `ScreenReaderOutput.Speak` on commit (Settings OK), e.g. "Radio audio output: Main Output 1/2 Audient EVO8" — matching the house rule that every change speaks. The combos themselves are native WPF, so NVDA reads item changes while browsing; the announcement covers the *applied* state.
- Preserve the "Default:" concept but as data, not decoration: first item "(Windows default) — <resolved name>" so the user hears what default actually resolves to today.
- The vanished-device state must be audible: if the saved device fails `FindDevice`, the combo shows and announces "Saved device not connected: Mic | Line 1/2 (Audient EVO8)" as the selected-but-flagged item rather than silently snapping to index 0 — silent remapping is how a blind operator ends up transmitting from the wrong mic.
- The rescue announcement path (5.2) uses Critical priority with interrupt, consistent with the connect-progress speech conventions in `AudioOutputConfig.SpeakConnectionProgress` (`AudioOutputConfig.cs:117-126`).
- Escape-closability comes free with `JJFlexDialog`; no long-running work happens in the tab (enumeration is fast; do it on open and on Refresh only).

## 8. Risks

- **First-run flow on a machine with no `audioDevices.xml` (the live ms-02 case, `research-queue.md:55-58`).** Today that machine either gets background-thread WinForms pickers mid-connect or a silent audio failure dressed up as "no RX antenna". The migration replaces this with a spoken, focusable route into Settings — but the auto-enable ambiguity (FlexBase.cs:9634 live vs :738 commented; queue note claims no auto-enable) must be resolved first, because it determines whether the rescue fires during connect or only when the user first toggles PC audio. Test matrix must include: fresh profile, remote connect, LAN connect, PC-audio toggle by menu, by hotkey, by Settings.
- **Device hot-plug.** PortAudio enumeration is a `Pa_Initialize` snapshot (`devList.cs:131, 226`); `known-issues.md:17` already documents that JJ Flex misses hot-plugged devices until restart. The Refresh button mitigates inside Settings, but a device yanked while PC audio is streaming still lands in `remoteAudioProc`'s failure path. Out of scope to fix streaming-time recovery here; in scope to make the failure speak.
- **Saved device vanished.** `FindDevice` (Devices.cs:235-250) returns false; with the picker gone, policy is needed: recommend fall back to the Windows default device with a Critical announcement, never block the connect, and reflect the fallback in Settings as the flagged state (section 7). The alternative — hard-stop until the user re-picks — punishes the common "docked laptop left the USB hub" case.
- **Stereo-only filter surprises.** Mono headset mics and >2-channel interfaces are invisible (devList.cs:187, 203). If the filter is kept, the new UI must say so ("Only stereo devices are listed"); if relaxed, `JJPortaudio.Audio`'s stream-open path needs verification that it handles mono capture (the Opus path runs at 48k stereo, `FlexBase.cs:8589`). Relaxing input to "1 or 2 channels" is probably the single highest-value functional change available in this migration, and also the riskiest one — keep it a separately testable commit.
- **Two enumeration stacks, adjacent combos.** The NAudio names (Alert/Meter devices) and PortAudio names (radio audio) for the same hardware will differ in the merged tab. Not fixable cheaply; mitigate with the group headings making the domains obvious ("Alerts", "Radio Audio").
- **Threading regression risk is retired, not created:** removing the worker-thread `ShowDialog` (FlexBase.cs:8752/8759) deletes the only non-UI-thread UI in the audio path. Nothing new runs off-thread.
- **x86 + x64 native `portaudio.dll` resolution** is untouched (`runtimes/` + `NativeLoader.vb`), since the enumeration still goes through the same PortAudioSharp binding.

## 9. Open questions for Noel

1. **Fully retire the old `devList` form, or keep it compiled as an emergency fallback?** Recommendation: retire it completely. Its only consumer is `Devices.getNewDevice`; the rescue path becomes "announce + open Settings → Audio", which is strictly more accessible than the old modal, and keeping both means every future device-UX change is done twice. Git history preserves it if nostalgia strikes.
2. **Keep `audioDevices.xml` as a standalone machine-scope file?** Recommendation: yes, unchanged (section 6). It is the only audio config in the app whose scope is already right; the root/Radios `audioConfig.xml` duality is the store that deserves future cleanup, and this migration should not enter that swamp.
3. **Relax the stereo-only device filter?** Recommendation: yes for input (accept 1-2 channels — mono USB headset mics are the most common blind-operator hardware there is), keep output stereo-only for now. Gate it behind verification that `JJPortaudio.Audio`'s capture path upmixes mono cleanly; ship as its own commit so it can be reverted independently of the UI move.
4. **Policy when the saved device is missing at PC-audio start?** Recommendation: fall back to the Windows default device, announce at Critical ("Saved radio audio output not found, using Windows default"), keep the saved entry in the file so the device is re-adopted when it reappears. Never block or silently fail the connect.
5. **Should remote connect auto-enable PC audio?** The code says yes (`FlexBase.cs:9634-9637` is live), the 2026-08-05 field note says it doesn't happen (`research-queue.md:53-58` cites the commented line 738). Recommendation: first verify which is true on the wire (one trace with a remote connect answers it), then keep auto-enable **conditional on devices being configured** — configured machines get audio without ceremony (friction-tax principle), unconfigured machines get the spoken Settings rescue instead of a mid-connect dialog ambush.
6. **Any appetite for per-radio audio device overrides in `RadioConfig`?** Recommendation: no for v1. Sound cards are machine-scope; the serial-keyed store just got its first tenant (connection preference) and should stay lean. Revisit only if a real two-radio-two-headset workflow shows up.
7. **Does the "Radio Audio Device" menu item survive as a Settings shortcut?** Recommendation: yes — rename to "Radio Audio Devices..." and point it at `OpenSettings("Audio")`. It costs one line, preserves every documented path (including the whats-new history and Don's habits), and the Command Finder entry retargets identically.

## Addendum — Noel, 2026-08-06 late: radio output levels join the tab

Scope addition from the field: the merged Settings → Audio tab also gets a
**"Radio Outputs" group** (present when a radio is connected) with direct-set
sliders for Headphone Level and Line Out Level plus the mute checkboxes —
live-apply, since audio level feedback is immediate, not save-on-OK. The
driver: Noel plugged headphones into the 8600 itself, heard nothing, and
misread it as an antenna problem; PC audio proved the signal was fine.
Resolved same night: levels were 50/50 unmuted all along — **a Flex's audio
outputs, jacks included, are silent until a client connects** (it's a
server, not a TS-2000). The sliders still belong in the tab (on a non-M
radio they are the only volume control that exists), and the planned
silent-radio advisory should check connected state first, before levels
and mutes. See the queue entry for the docs/help deliverable. Everything below the
UI already exists: FlexBase wraps both gains 0–100 (`FlexBase.cs:7332-7357`),
FlexLib has the mutes, and ScreenFields Audio (Ctrl+Shift+U) already renders
direct-set fields for both (`ScreenFieldsPanel.xaml.cs:410-416`) — this is a
second door to the same values, per the one-concept-two-doors pattern the tab
already uses. Full details and audit hooks (the `!PCAudio` gate on the
Lineout key handlers, dead `LocalAudioMute`, an at-zero visibility
affordance) live in the research queue entry dated 2026-08-06 late.

## Addendum — Noel, 2026-08-06: device identity must survive reshuffles

Requirement added after the research round: the saved device selection must
survive the device LIST changing shape — a USB headset re-plugged into a
different port, a new audio cable or interface arriving, a device removed.
PortAudio device indexes reorder in all of those cases, so persistence by
index is wrong by construction. Implementation implications:

- Persist stable identity (device name plus host API at minimum; whatever
  else PortAudio exposes that is stable), never a bare index.
- Re-resolve identity to a current index at startup AND on device-change
  events, not just once.
- A reshuffle that still contains the saved device must rebind to it
  silently and correctly — the user did nothing wrong and hears nothing.
- Only when the saved device is genuinely gone does the announced-fallback
  path fire (Windows default plus a spoken notice, per the main proposal).
  The failure mode to design against is silently binding to the WRONG
  device because an index moved — worst case that is TX audio into the
  wrong output.
