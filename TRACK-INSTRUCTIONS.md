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

1. **Radio Outputs group in Settings → Audio** (visible/enabled only with a
   radio connected). Headphone Level slider, Line Out Level slider (set-once,
   0–100, live-apply — audio feedback is immediate, no OK-button round trip),
   plus Headphone Mute, Line Out Mute, Front Speaker Mute checkboxes.
   Sliders announce their value as adjusted. Arrow keys must produce sane
   step sizes (5 matches the existing menu Up/Down step).
2. **PC Audio checkbox** in the same surface. It REFLECTS live state — PC
   audio auto-enables on remote connect (`FlexBase.cs` ~9875) — and allows
   manual off/on. A saved "off" must not silently fight the remote
   auto-enable: if the user unchecks it on a remote connection, say what that
   means ("Radio audio will no longer play through this computer").
3. **Rebuild the audio device picker** (old C2 item 16). Today "Radio Audio
   Device" runs `GetNewAudioDevices` (`globals.vb:1740`) which shows the
   legacy `devList` WinForms dialog TWICE in sequence
   (`JJPortaudio\Devices.cs:171`) — the device lists are unreachable by ear.
   Build ONE accessible dialog (JJFlexDialog family): clearly labelled Input
   and Output lists, arrow-readable, current selection announced on open,
   system default marked. Keep the existing menu entry pointing at the new
   dialog AND surface it inside Settings' audio section.
4. **One "Audio devices" surface** covering radio audio in/out, the alert
   device, and CW output — not three scattered ones.
5. **CW-enable grouping:** move/group the "Enable CW notifications" checkbox
   beside the Alert-device combo (SettingsDialog.xaml, ~line 783 section) so
   device + enable read as one unit. **Default stays FALSE** — decided by
   Noel 2026-08-07; do not change the default.
6. **Device-missing fallback:** when a configured audio device is absent,
   fall back to the system default WITH a spoken note — never silence.
   `EnsureAudioDevicesConfigured` (`globals.vb:1771`) already has the hook;
   when PC audio is requested with no devices configured, say so in words
   and offer to open the picker.
7. **"Why is my radio silent" ladder + visibility affordance.** First rung is
   CONNECTED state: a Flex makes no audio, including at its physical jacks,
   until a client connects. Then: outputs muted? levels at/near zero? PC
   audio off with no local path? Surface as a spoken advisory ("Radio outputs
   are at zero") where the settings surface shows the condition.
8. **Help:** write `docs/help/md/audio-troubleshooting.md` (the ladder in
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
