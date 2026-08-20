# Sprint 33 Track F — CW notifications: pitch, waveform, and repeat

**Worktree:** `C:\dev\jjflex-33f` · **Branch:** `sprint33/track-f`
**Plan:** `docs/planning/active/barefoot-harness-pileup.md`
**Merges into Track A. The only feature track in a test sprint — see below.**

Build your own worktree only:
`dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal`

---

## Why these three are one track and not three

Tasks #145, #146 and #153 all touch the same files: `JJFlexWpf/MorseNotifier.cs`,
`JJFlexWpf/EarconCwOutput.cs`, `JJFlexWpf/AudioOutputConfig.cs`,
`JJFlexWpf/Dialogs/SettingsDialog.xaml.cs` and its XAML, plus
`Radios/ScreenReaderOutput.cs`. Split across tracks they would collide on every
one of them.

Together they are a single coherent thing: **a CW notification settings group** —
pitch, speed, waveform and repeat in one place, with one vocabulary.

**Sprint 33 is otherwise a test sprint and you are the exception.** You touch no
test infrastructure and no other track touches your files, so you merge cleanly.
Stay in your lane and that stays true.

## Why CW notifications matter more as we add them

Noel's framing, from 2026-08-19: *"This will be more important as we add more CW
notifications and haptics."* And the audibility problem in his words: *"The CW
now sounds like a keyer which is great, but if someone has a hard time picking
that out, even differentiating it from actual keying sound, it might be hard to
hear."*

**That last point is the real design pressure.** A CW notification that sounds
like CW is competing with actual received CW and with the operator's own
sidetone. Distinguishability is the requirement, not prettiness.

---

## #146 — pitch: follow the radio's sidetone, or use a configured tone

**Noel's exact instruction, and note what it rules out:** *"I'd also make this
configurable to 'follow side tone or use a configured tone'."*

He said this while correcting an earlier recommendation to AUTOMATICALLY offset
the notification pitch away from the sidetone. **Do not auto-offset.** Two named
choices the operator makes, nothing clever.

**The plumbing already exists for a different consumer, which makes this small.**
`Radios/FlexBase.cs:6015` already handles the radio's `CWPitch` property change,
and inside it: `if (useCWMon) CWMon.Frequency = (uint)r.CWPitch;`

The CW MONITOR already follows the radio's sidetone. The CW NOTIFIER simply is
not wired to the same event. `theRadio.CWPitch` is readable and writable through
`FlexBase` at `:10198` and `:10210`, and `FlexLib.Radio.CWPitch` is at
`Radio.cs:9636`.

So "follow sidetone" means pointing `MorseNotifier.SidetoneHz` at the radio's
pitch and keeping it current on that same property change.

**Handle the disconnected case honestly.** With no radio there is no sidetone to
follow. Fall back to the configured tone and make sure nothing announces an
error about it — this is a normal state, not a fault.

Today the setting is a plain text box, `CwSidetoneBox` in
`SettingsDialog.xaml.cs:345` and `:1284`, backed by
`AudioOutputConfig.CwSidetoneHz` (default 700, clamped 400 to 1200 at
`MainWindow.xaml.cs:241`). The new choice sits alongside it.

## #145 — waveform: a pure sine is the easiest thing on earth to bury

**Noel:** *"recommend allowing the user to change CW generation sound type if
it's hard to hear with band noise. Now sine, allow for square, saw, and the
harmonics you've implemented."*

`CwToneSampleProvider` is constructed at exactly ONE site,
`JJFlexWpf/EarconCwOutput.cs:195`:

```
providers.Add(new CwToneSampleProvider(
    sr, item.SidetoneHz, el.DurationMs, item.RiseFallMs, item.Volume));
```

One construction site is good news — the waveform choice threads through one
place.

**"The harmonics you've implemented"** refers to the additive synthesis in the
meter voice engine. Reuse it rather than writing a third synthesiser; task #112
already notes the project has three implementations of one idea and should not
grow a fourth.

**Keep the rise/fall envelope on every waveform.** `RiseFallMs` exists to stop
key clicks, and a square or saw wave with a hard edge will click harder than a
sine, not less. A waveform option that reintroduces clicks is a regression
wearing a feature's clothes.

## #153 — repeat: and the constant that will bite you

Mirror the speech repeat, which shipped in Sprint 32 Track H (`f5391540`).
`Radios/ScreenReaderOutput.cs` holds `_history` (depth 10), `_historyCursor`,
and `RepeatRecent()` at `:823`. Bound to `Ctrl+F4` via
`CommandValues.RepeatLastMessage`. First press says the newest; a prompt second
press steps further back; running off the oldest wraps.

**The five CW delegates are already statics on that same class** — `PlayCwAS`,
`PlayCwBT`, `PlayCwSK`, `PlayCwMode` (wired but no longer called; Track H left it
deliberately), `PlayCwText`. So the CW history goes beside the speech history, in
the class that already owns both, with no new dependency direction. `FlexBase`
calls these without knowing JJFlexWpf exists and that must stay true.

**THE TRAP: `HistoryWalkResetMs = 6000` at `ScreenReaderOutput.cs:769`.**

That is the window in which a second press means "step back" rather than "start
over." Six seconds is roomy for speech. It is not roomy for CW. At 20 words per
minute a dit is 60 ms and "SL A USB" runs about 4.4 seconds; at the 10 WPM floor
the app allows, the same string is about 8.9 seconds — **past the reset**. An
operator running slow code would press twice and get the newest message again,
and the walk would look broken to exactly the people most likely to want it.

**Measure the window from when playback ENDS, not when it starts** — or derive it
from `SpeedWpm`. This is the same mistake as #143's flat 5000 ms farewell
timeout; fix it with the same idea, and say in your report whether #143 should
adopt it too.

**Three things to settle before building:**

1. **Does repeat cancel what is playing?** Speech repeats with `interrupt: true`.
   `MorseNotifier.Cancel()` exists — but it reaches `_output.Cancel()` on the
   SHARED `EarconCwOutput`, and a continuous earcon such as ATU progress may be
   running on it. Noel confirmed CW-plus-ATU-tone is a live combination on
   2026-08-20. **Verify a CW repeat does not also kill a running earcon.**
2. **Do prosigns enter the history?** AS, BT and SK are punctuation — wait,
   connected, closing. Repeating "AS" tells nobody anything. The messages that
   carry content are the slice census and slice vocabulary added by Track H at
   `Radios/FlexBase.cs:12442` and `:12469`. **Recommend text only; ask before
   deciding.**
3. **Own CW history, or send the last SPOKEN message as CW?** Noel said "similar
   to repeating speech keys," which reads as its own history. The other reading
   is nearly free given `PlayString` already handles letters, digits and the
   stroke — a key that puts ANY announcement into Morse when speech is buried
   under band noise. **Different features. Ask, do not guess.**

## Binding, and the audit that goes with it

The standing preference is the `Ctrl+J` leader layer over new flat chords. Speech
repeat is flat `Ctrl+F4` because it is used constantly; if CW repeat is used as
often, a flat key is defensible. **Noel's call — ask.**

**Whatever is chosen, the keyboard audit is definition of done, not follow-up:**
`docs/help/md/keyboard-reference.md`, Command Finder search keywords, F1 context
help, and a changelog line.

**And PRESS THE KEY on a real build.** On 2026-08-13 an `Alt+L` binding shipped
completely dead one build after being added — the handler tested `e.Key ==
Key.L`, which is never true while Alt is held because WPF reports `Key.System`
and puts the real key in `e.SystemKey`. It compiled. It reviewed clean. Nobody
pressed it.

## User-facing prose needs Noel's approval

Setting labels, the choice names for waveforms, the follow-versus-configured
wording. **Draft it, show it, do not ship it unreviewed.** Approval of copy is
not authorisation to build, and vice versa.

## House rules

- **No tables** in any output, report or doc. Prose or bullets. Screen reader first.
- Do not touch files outside your worktree.
- Noel is blind and at the keyboard, and may be operating the radio. Anything
  that plays audio or takes focus collides with him — coordinate before a run.

## Commits

`Sprint 33 Track F: <description>`.

## Completion report

State: the three questions above with your recommendation and what Noel decided;
whether cancelling a CW repeat kills a running earcon; the binding chosen and
confirmation you pressed it on a real build; the keyboard audit items updated;
and whether #143 should adopt the same speed-derived timing.
