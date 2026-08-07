# QB Track B — Settings → Audio surface + device pickers

**Recommended model: Opus.** The spec below is complete; judgment calls have
been made. If you hit a genuine design fork the spec doesn't cover, note it,
pick the conservative option, and flag it in your completion report.

## Context

You are one of six parallel tracks in the 2026-08-07 queue-burn session
(plan: `docs/planning/active/nightowl-pileup-ragchew.md`). JJ Flex is a
screen-reader-first FlexRadio client; our users are blind hams. Field driver
for this track: Noel plugged headphones into his FLEX-8600, heard nothing,
and had no way to inspect or set the radio's output levels from the app — on
a non-M radio, software is the only volume knob that exists. Separately, the
audio device picker is the last un-swept legacy dialog pair and it gates ALL
audio on a fresh install.

Read first:
- `docs/planning/active/research-queue.md` — Track B section (queue of record)
- `docs/planning/for-noel/2026-08-06-radio-audio-settings-research.md` — the
  research doc this track implements
- `CLAUDE.md` — accessibility guidelines, build rules

## Architecture rules

- WPF work lives in `JJFlexWpf/`; radio abstraction in `Radios/FlexBase.cs`.
  Never edit vendor FlexLib (`FlexLib_API/`) without an explicit JJFlex
  comment marker and a MIGRATION.md entry — for this track you should not
  need to touch it at all.
- Existing wrappers you build on: `HeadphoneGain`/`LineoutGain` (0–100,
  `FlexBase.cs:7332-7357`). FlexLib exposes `HeadphoneMute`, `LineoutMute`,
  `FrontSpeakerMute` — add FlexBase wrappers if missing, following the
  null-conditional guarded getter pattern from the 2026-08-05 ActiveSlice
  sweep (single-expression, race-free, per-property defaults).
- Every spoken string goes through `ScreenReaderOutput.Speak` with an
  appropriate `VerbosityLevel`. No silent keystrokes: every control action
  speaks in every state, including "no radio connected."
- Multiple-doors principle (ratified): settings reachable from a menu remain
  reachable there; this track ADDS the settings-surface door, it removes
  nothing.

## Work items

**Status: all eight DONE (2026-08-07).** Per-item notes follow each entry.

1. **[DONE]** **Radio Outputs group in Settings → Audio** (visible/enabled only with a
   radio connected). Headphone Level slider, Line Out Level slider (set-once,
   0–100, live-apply — audio feedback is immediate, no OK-button round trip),
   plus Headphone Mute, Line Out Mute, Front Speaker Mute checkboxes.
   Sliders announce their value as adjusted. Arrow keys must produce sane
   step sizes (5 matches the existing menu Up/Down step).
2. **[DONE]** **PC Audio checkbox** in the same surface. It REFLECTS live state — PC
   audio auto-enables on remote connect (`FlexBase.cs` ~9875) — and allows
   manual off/on. A saved "off" must not silently fight the remote
   auto-enable: if the user unchecks it on a remote connection, say what that
   means ("Radio audio will no longer play through this computer").
3. **[DONE]** **Rebuild the audio device picker** (old C2 item 16). Today "Radio Audio
   Device" runs `GetNewAudioDevices` (`globals.vb:1740`) which shows the
   legacy `devList` WinForms dialog TWICE in sequence
   (`JJPortaudio\Devices.cs:171`) — the device lists are unreachable by ear.
   Build ONE accessible dialog (JJFlexDialog family): clearly labelled Input
   and Output lists, arrow-readable, current selection announced on open,
   system default marked. Keep the existing menu entry pointing at the new
   dialog AND surface it inside Settings' audio section.
4. **[DONE]** **One "Audio devices" surface** covering radio audio in/out, the alert
   device, and CW output — not three scattered ones.
5. **[DONE]** **CW-enable grouping:** move/group the "Enable CW notifications" checkbox
   beside the Alert-device combo (SettingsDialog.xaml, ~line 783 section) so
   device + enable read as one unit. **Default stays FALSE** — decided by
   Noel 2026-08-07; do not change the default.
6. **[DONE]** **Device-missing fallback:** when a configured audio device is absent,
   fall back to the system default WITH a spoken note — never silence.
   `EnsureAudioDevicesConfigured` (`globals.vb:1771`) already has the hook;
   when PC audio is requested with no devices configured, say so in words
   and offer to open the picker.
7. **[DONE]** **"Why is my radio silent" ladder + visibility affordance.** First rung is
   CONNECTED state: a Flex makes no audio, including at its physical jacks,
   until a client connects. Then: outputs muted? levels at/near zero? PC
   audio off with no local path? Surface as a spoken advisory ("Radio outputs
   are at zero") where the settings surface shows the condition.
8. **[DONE]** **Help:** write `docs/help/md/audio-troubleshooting.md` (the ladder in
   prose, warm voice, for blind hams migrating from conventional rigs —
   "radio on but silent? Connect first") and add a getting-started line.
   Docs ship with features; no tables, prose and bullets only.

## Ownership boundaries (do not cross)

- SettingsDialog **audio section only**. Do NOT touch the Network or Radios
  tabs (Track C), `RigSelectorDialog` (Track E), or the RadioSetup partial
  (Track F).
- Do not add or change key bindings without flagging the orchestrator
  (keyboard audit applies at merge).

## Build & verify

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
powershell -Command "(Get-Item 'bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe').LastWriteTime"
```
Timestamp must match now — stale binaries have wasted whole sessions.

Verify checklist per dialog/surface you touch: Escape closes it; Tab order is
sane; every control has AccessibleName; arrow through every line under the
NVDA mental model (blank lines in read-only text need a single space; every
IsDefault button needs explicit AutomationProperties AccessKey/AcceleratorKey
or NVDA says "carriage return").

## Commit style

Commit after each work item: `QB Track B: <what changed>`. Push to `origin`
(never `upstream`). When all items are done, update this file's item list
with DONE markers, commit, and report completion to Noel.

---

## Design decisions (2026-08-07)

Judgment calls made while executing, with the reasoning. Where the spec or the
research doc pointed one way and this track went another, that is called out.

### The old `devList` form was fully retired, not kept as a fallback

Research open question 1 recommended full retirement and this track did it:
`devList.cs`, `devList.designer.cs` and `devList.resx` are deleted, and its
enumeration guts moved into `Devices` as a UI-free static API. Keeping it
compiled "just in case" would mean maintaining two device-selection UIs
forever, and the emergency it would serve — no device configured at connect
time — is now handled better by a spoken fallback that needs no UI at all. Git
history holds the form if it is ever wanted.

### Devices identity persists host API type, and matching is two-pass

Noel's 2026-08-06 addendum required saved selections to survive the device
list changing shape. `Device` gained two optional fields, `hostApiTypeId` and
`hostApiName`. `FindDevice` tries name + channel counts + host API type id
first, then falls back to the pre-existing name + channels rule.

Two things this deliberately does NOT do. It does not match on the PortAudio
index, ever — that is the failure mode the addendum names, and its worst case
is transmitting from the wrong microphone. And it does not treat a host-API
mismatch as a miss: if a host API disappears from the system, the device is
still the device the user picked, so the second pass rebinds silently. Only a
genuine no-match is announced.

The schema addition is backward and forward compatible. XmlSerializer ignores
unknown elements on read and defaults missing ones, so existing
`audioDevices.xml` files load unchanged and only gain the new fields on the
next save. No migration step, no version bump on the file.

### `audioDevices.xml` stayed exactly where it was

Research question 2, recommendation followed without modification: same path,
same schema, same single writer, machine scope. It is the one audio config in
the app whose scope is already right. Folding it into `audioConfig.xml` would
have put machine truth inside a file that exists in two drifting copies, which
is the class of bug that produced the ms-02 CW silence.

### Stereo-only filter kept, but now stated out loud — NEEDS NOEL

Research question 3 recommended relaxing the input filter to accept 1–2
channels, since mono USB headset mics are common blind-operator hardware, and
flagged it as the riskiest change available. This track took the conservative
option and kept stereo-only, because relaxing it requires verifying that
`JJPortaudio.Audio`'s capture path handles mono cleanly (the Opus path runs
48k stereo) and that is a live-audio test, not something to land unverified in
a parallel track.

What changed instead: the limitation is now said in words, in the picker
("Only two-channel (stereo) devices are listed here. A mono microphone will
not appear — that is a JJ Flex limitation, not a fault with your device") and
in the help page. Previously it silently hid devices, which reads to a user as
"JJ Flex can't see my headset". `Devices.StereoOnly` is a named constant so
the relaxation is a one-line change plus a capture-path verification.

### Saved-device-missing policy: fall back and announce, never block

Research question 4, recommendation followed. `ResolveAudioDevice` in
`FlexBase` adopts the system default, says so at Critical priority, and lets
the connect proceed. Blocking would punish the common "docked laptop left the
USB hub" case. The saved entry stays in the file, so the device is re-adopted
when it reappears.

### PC audio is NOT persisted, deliberately

The spec said a saved "off" must not silently fight the remote auto-enable.
The cleanest way to guarantee that is to not save it at all, which also
matches the queue's framing of this as an "inspectable/override surface, not
required setup". The checkbox reflects and overrides the current session only,
and the status line under it says so. This sidesteps the whole conflict rather
than arbitrating it.

Research question 5 (does remote connect actually auto-enable PC audio? code
says yes at `FlexBase.cs:10047`, a field note said no) is **not resolved by
this track** — it needs a live trace. The design is correct either way: the
checkbox reads live state, so it tells the truth whichever the answer is.

### CW notifications moved to the Audio tab wholesale, not just the checkbox

The spec said to move/group the enable checkbox beside the alert-device combo.
Moving only the checkbox would have split one feature across two tabs, with
the switch on Audio and its sidetone/speed on Notifications — worse for a
screen-reader user than the problem being fixed. So the whole CW Morse
Notifications block moved into the Audio tab's Alerts group, renamed "Alerts
and CW Notifications".

Verified before asserting it in the UI: CW notifications really do play
through the alert device (`MorseNotifier` → `EarconCwOutput` →
`EarconPlayer.SubmitCwSequence` → the alert mixer). There is no separate CW
output device to choose, and the dialog says so rather than leaving a user
hunting for one.

The Notifications tab keeps a pointer line. A setting that has moved should
say where it went; silently vanishing is its own accessibility failure.

**Default stays FALSE.** Untouched, as instructed.

### Live-apply for output levels and mutes, commit-on-OK for everything else

This surface breaks the dialog's usual "changes commit on OK" contract for
exactly two things: the output levels and the three mutes. Audio feedback is
instantaneous — you raise a level to find out whether it is now right — and a
value that only takes effect after an OK-and-reopen round trip cannot be found
by ear at all. Volumes, device choices and CW parameters still commit on OK,
because those are preferences rather than a knob you are turning while
listening.

### Step of 5, matching the keys rather than the menu

The instructions said "5 matches the existing menu Up/Down step". It does not:
`KeyCommands` moves by 5 (`HeadphonesUpHandler` and friends), the Audio menu
moves by 10 (`BuildAudioItems`, `const int gainStep = 10`). Used 5 as
instructed, and chose the keys as the anchor deliberately — that is the
surface an operator uses while listening. **The menu/key 5-vs-10 mismatch is
pre-existing and was left alone**; it is worth someone deciding on, but not
worth a parallel track changing unilaterally.

### The silent-radio ladder lives in `FlexBase`, not in the dialog

`FlexBase.SilentRadioAdvisory()` returns the first true rung or null. Putting
it on the radio abstraction rather than in the Settings code-behind means the
same answer is available to any future surface — a hotkey, a status dialog,
the crash-report bundle — without the ladder being re-implemented and drifting.
It stops at the first rung on purpose: a ladder read out in full is a list
nobody finishes.

### Status TextBlocks are focusable and set their own accessible names

The existing pattern in this dialog is a non-focusable TextBlock with
`LiveSetting="Polite"`, which is fine for text that only ever changes as a
side effect of something else. These lines are different — they carry the
answer the user came for — so they are in the tab order and reachable.

That created a trap worth recording: a focusable TextBlock reports
`AutomationProperties.Name`, not its `Text`. A Name authored once in XAML
would have read the same stale sentence forever while the visible text moved
on. `AudioDevicesDialog.SetStatusLine` sets both together, and turns empty
text into a single space so a blank line is not a hole a screen reader arrows
straight past.

### Scope taken slightly beyond the audio section

Three edits outside `SettingsDialog`'s audio section, all audio-topic and all
small:

- `NativeMenuBar.BuildAudioItems` — the device menu entry renamed and the PC
  Audio toggle changed to announce the outcome rather than the wish (it said
  "PC audio on" even when the audio path failed to start).
- `NativeMenuBar.ShowSettingsDialog` — one added line handing the
  audioDevices.xml path to the dialog.
- `MainWindow` / `ApplicationEvents.vb` — one property so globals can publish
  that path at startup, since Settings must reach the picker with no radio
  connected.

Nothing in the Network tab, Radios tab, `RigSelectorDialog`, or the RadioSetup
partial was touched.

### Command Finder metadata changed; no key bindings changed

`CommandValues.AudioSetup` was relabelled "Audio devices" and gained keywords
for what the dialog now covers (microphone, speaker, headphone, playback,
alert, soundcard) while keeping every old term. Its binding is unchanged —
still `Keys.None` by default — so **no keyboard audit is triggered by this
track**. `keyboard-reference.md` correctly does not list it and still does not
need to.

### Vestigial fields removed

`globals.InputAudioDevice` / `OutputAudioDevice` are gone. They were
write-only: the old picker assigned them and nothing ever read them back,
since `FlexBase` always re-reads `audioDevices.xml` through its own `Devices`
instance. Two module-level fields that looked like the current selection but
were not.

### Known residual risk

PortAudio's `Pa_Initialize`/`Pa_Terminate` are reference-counted but not
thread-safe against each other, and enumeration can now be requested from the
UI (the picker's Refresh button) while the audio thread is doing its own
setup. This track serializes JJ Flex's own enumeration calls with a lock and
removed the double sweep per Refresh. It does not protect against PortAudio
work started elsewhere in the process — that exposure is pre-existing, not
introduced here, and fixing it properly means a process-wide PortAudio
lifetime owner, which is its own piece of work.
