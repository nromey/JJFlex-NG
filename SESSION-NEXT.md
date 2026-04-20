# Session Next — Brief for the Next Claude Session

**Read this FIRST.** Written at end of 2026-04-15 session so the next
session doesn't rebuild context from the ground up or redo completed work.

## You are here

Branch: `sprint25/track-a`. Head: `5e030221`.
Last shipped debug: `4.1.16.10` (on NAS at `historical\4.1.16.10\x64-debug\`; NOT on Dropbox yet).
Sprint 25 is substantially tested and working. A CW engine rewrite shipped
in 4.1.16.10 with tone quality confirmed good — but a cancellation race
(BUG-057) means only SK fires reliably during connect events.

## What is DONE — do not redo

- **NAS history refactor**: Noel finished his work moving debug archives
  to `\\nas.macaw-jazz.ts.net\jjflex\historical\<version>\x64-debug\`
  with zip + NOTES + exe + pdb per version. Scripts committed:
  `build-debug.bat` (uses `NAS_HISTORICAL` variable), `backfill-historical-debug.ps1`,
  `migrate-nas-to-historical.ps1`. **Do not modify these scripts or the
  NAS layout.** If the build pipeline seems odd to you, read
  `build-debug.bat` in full — it's the current authoritative design.

- **CW engine rewrite (4.1.16.10)**: `CwToneSampleProvider` + element-batch
  `ICwNotificationOutput` + rewritten `EarconCwOutput` + `MorseNotifier`
  are all in place and working. Tone quality (sine + raised-cosine envelope +
  PARIS timing) confirmed good by Noel. **Do not redesign the engine.**
  The outstanding issue is only the cancellation behavior — see BUG-057
  below. Leave `CwToneSampleProvider` alone; it's correct.

- **Everything else Sprint 25**: Network tab, waterfall focus fix, Discovery
  NRE fix, antenna-wait bump, show-panadapter toggle — all shipped and
  confirmed. Don't re-touch these unless there's a bug report.

- **Memory entries**: `feedback_numeric_identifiers.md`,
  `feedback_dont_duplicate_platform_warnings.md`,
  `feedback_dropbox_publish_is_explicit.md` — all in place. Honor them.

## What the "queue" is (clarification for BUG-057 confusion)

The current CW engine handles a new prosign/message by **cancelling**
any in-flight sequence (disposes the `CancellableCwProvider`, makes
`Read()` return 0, mixer drops it). This is a race: the audio mixer
has a ~50 ms buffer window before playback actually starts, so when
a second event fires during that window, it cancels the first BEFORE
any audio hits the speaker. That's why on a real connect sequence,
only SK plays — it's the only event where nothing follows it.

**The fix is a FIFO queue.** Concept:

- One `Channel<IReadOnlyList<CwElement>>` (or equivalent queue) per
  `EarconCwOutput` instance.
- Single consumer task drains the queue. For each dequeued sequence:
  build the `ConcatenatingSampleProvider`, submit to the mixer, await
  its total-duration Task. Only then dequeue the next.
- `PlayElementsAsync` becomes `Enqueue`. No more `Cancel()` in the
  normal path.
- Rapid events (BT → mode-announce-Morse → ...) play IN ORDER, each
  run to completion, nothing clobbered.

**Why this design, specifically:**

- Fixes BUG-057 (prosigns during connect sequence now all play).
- Sets up on-air CW message sending (future) which will enqueue
  character-by-character.
- Sets up iambic keyer (future) which will enqueue element-by-element.
- Sets up code-practice tutor (future) which will enqueue lesson text.
- **One primitive, four downstream features.** See
  `docs/planning/design/cw-keying-design.md` "Cancellation — and why
  the next revision replaces it with a queue" section for the full
  design.

**Keep `Cancel()` as an interface method** — still needed for
"user-initiated stop" (e.g., app shutdown flushing the queue).
Default is never-cancel-on-new-item.

## Concrete next steps in order

### 1. BUG-057 — replace cancel-and-replace with a queue

Files touched:

- `JJFlexWpf/EarconCwOutput.cs` — implement the queue + consumer loop.
- `JJFlexWpf/MorseNotifier.cs` — remove the `Cancel()` call at the
  start of `PlayCharacter` and `PlayString`. Keep public `Cancel()`
  available for shutdown.
- `JJFlexWpf/ICwNotificationOutput.cs` — interface unchanged in
  shape; `PlayElementsAsync` now returns when ITS sequence finishes
  (still awaits its own completion, but doesn't prevent other
  sequences queuing behind it).

Verify by: build, launch, connect to a radio, listen for BT on
connect. Disconnect + reconnect, listen again. Test with speech off
for the full CW-assisted experience. Confirm SK still fires on
app-close.

### 2. SWR-after-tune announcement (Don's HIGH priority ask)

Files touched:

- `JJFlexWpf/AudioOutputConfig.cs` — new `bool AnnounceSwrAfterTune
  { get; set; } = true;`
- `Radios/FlexBase.cs` — subscribe to `SWRDataReady`, track
  `_lastSwrReading`. Detect tune-end via `FlexTunerOn: true → false`
  transition (property change). Wait ~200 ms after transition, then
  announce via `ScreenReaderOutput.Speak($"SWR {val:F1} to 1",
  VerbosityLevel.Terse, interrupt: false)`.
- `JJFlexWpf/Dialogs/SettingsDialog.xaml` — checkbox in the
  Notifications tab under the Display section (or a new SWR section).
- `JJFlexWpf/Dialogs/SettingsDialog.xaml.cs` — wire load/save of the
  new config flag.

Verify by: turn on ATU, tune. Announce. Turn off ATU, use Ctrl+T
manual tune. Announce. Toggle the checkbox, confirm it respects the
setting.

### 3. Build + archive

Run `build-debug.bat --no-commit` (do not edit the script — it's
Noel's design, and `--no-commit` is needed because there may be other
uncommitted WIP). Archives to NAS `historical\<version>\x64-debug\`.
DOES NOT touch Dropbox.

### 4. Ask Noel for Dropbox publish

Do NOT run `build-debug.bat --publish` without Noel's explicit
confirmation. This is a **feedback memory** (`feedback_dropbox_publish_is_explicit.md`):
every debug build → NAS automatically; Dropbox only on explicit
"publish" / "ship to Don" / end-of-day.

### 5. Directed test plan

After Noel publishes to Dropbox, provide a directed test checklist
covering:

- CW prosigns fire correctly during connect sequence (BT, AS, SK, mode-change Morse).
- Speech off + CW on produces a usable operating mode.
- SWR-after-tune with various tune scenarios (ATU auto, Ctrl+T manual,
  ATU auto with bad SWR).
- Remainder of `docs/planning/agile/sprint25-test-matrix.md` items.

## What NOT to do

- Do not modify `build-debug.bat` — it reflects Noel's current NAS layout.
- Do not modify `backfill-historical-debug.ps1` or `migrate-nas-to-historical.ps1`.
- Do not redesign `CwToneSampleProvider` — the envelope and timing are correct.
- Do not add feature work outside BUG-057 + SWR-after-tune unless Noel asks.
- Do not publish to Dropbox without explicit go from Noel.
- Do not bump the `<Version>` in `JJFlexRadio.vbproj` — we're still 4.1.16,
  Y auto-increments.

## Where to find more context

- **`Agent.md`** — "Session 2026-04-15" section has the full narrative of
  the previous session: what broke, what got fixed, what tested PASS, what
  the connection path diagnosis uncovered (UPnP → manual forward →
  internal 4994/4993 → Discovery race → antenna wait).
- **`docs/planning/vision/JJFlex-TODO.md`** — BUG-054 through BUG-057,
  plus the Near-term HIGH PRIORITY section with SWR-after-tune.
- **`docs/planning/design/cw-keying-design.md`** — full CW engine design,
  with the new queue section at the end.
- **`docs/planning/design/session-latency.md`** — per-session RTT probe
  design (not immediate work but relevant if CW timing conversations
  surface).
- **Memory directory** — user preferences and feedback that apply
  across sessions.

## Expected total scope

BUG-057 fix: under an hour. SWR-after-tune: about an hour with testing.
Build + NAS archive: few minutes. Waiting for Noel's publish decision:
variable. Directed test pass: Noel's call. Whole session is plausibly
two or three hours of focused work plus testing time.
