# Sprint 33 Track F — how the app sounds: CW pitch, waveform, repeat, and the sine-versus-modern voice set

**Worktree:** `C:\dev\jjflex-33f` · **Branch:** `sprint33/track-f`
**Plan:** `docs/planning/active/barefoot-harness-pileup.md`
**Merges into Track A. The only feature track in a test sprint — see below.**

Build your own worktree only:
`dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal`

---

## Scope: five tasks, one subject

**#146** CW pitch follows the radio's sidetone or a configured tone. **#145** CW
waveform. **#153** a CW repeat key. **#147** a setting that selects the original
sine-based sounds or the new ones. **#144** the connect series still sounds like
the old sounds.

## Why they are one track and not five

They touch the same files: `JJFlexWpf/MorseNotifier.cs`,
`JJFlexWpf/EarconCwOutput.cs`, `JJFlexWpf/EarconVoices.cs`,
`JJFlexWpf/EarconPlayer.cs`, `JJFlexWpf/AudioOutputConfig.cs`,
`JJFlexWpf/Dialogs/SettingsDialog.xaml.cs` and its XAML, plus
`Radios/ScreenReaderOutput.cs`. Split across tracks they would collide on every
one of them.

**And they are one subject: HOW THE APPLICATION SOUNDS, and how much of that the
operator controls.** #145 and #147 are literally the same question — how rich
should a sound be — asked about CW and about earcons. They should end up sharing
a vocabulary in the settings UI, and they cannot do that if different tracks
invent different words.

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

---

## #147 — the sine-versus-modern voice set, and it is NOT a code-path restoration

**Noel, 2026-08-20:** *"Remember also allowing the user to select original sounds
based on sine or the new sounds as a setting."*

**#147 as filed says the classic path was DELETED and implies restoring it. Do
not restore it.** Track E removed the old synthesisers in `283a216e` on purpose,
and bringing back a second code path to maintain forever would undo the best
thing that sprint did.

**The architecture Track E left behind already supports this setting almost for
free.** `JJFlexWpf/EarconVoices.cs` defines exactly SEVEN named voices — `Plain`,
`Press`, `Chime`, `Alarm`, `WarningCalm`, `WarningInsistent`, `WarningUrgent` —
and `EarconPlayer.cs` references them 35 times. **Every one of the 45 earcons
names a voice; not one of them carries its own timbre.**

So the setting is: **two definitions for those seven voices, and one setting that
picks the table.** The classic set is sine-based — `Partials = { 1f }`, flat
sustain, a simple envelope. The modern set is exactly what ships today. Every
earcon follows automatically, because each references the NAME and not the data.

The natural place is the definition site itself: turn each static voice into
something that resolves through the currently-selected set. One file, one
indirection. Check `DecayingOver` at `EarconVoices.cs:174` still behaves — it
clones `baseVoice ?? Plain`, so whatever `Plain` resolves to must be a real voice
at all times, never null and never mid-swap.

**Be honest in the user-facing wording about what this is.** It gives a plain,
sine-based voice set — which is what the old sounds were like. It is NOT a
byte-for-byte restoration; some original earcons also differed in duration and
sequencing, not only timbre. **Do not label it in a way that promises the
literal old sounds.** If Noel ever wants those exactly, they are in git at
`283a216e^`, and that is a separate conversation.

**This is the same idea as #145 one layer up.** CW waveform is "how rich should
the CW be"; the voice set is "how rich should the earcons be." Use one vocabulary
across both settings so the operator learns the idea once.

## #144 — the connect series sounds unchanged, and the filed diagnosis is wrong

**Noel, by ear:** *"Love the new sounds ... much more to them. Right now they seem
not to be attached to connecting at least (that's still playing the old
versions."*

**He heard correctly, but #144's stated cause is wrong — check this yourself
before acting.** The connect series was NOT skipped by Track E's migration.
`ConnectPhase1Tone` at `EarconPlayer.cs:580` and its siblings already call
`PlayVoiced` and `PlayVoicedSequence`. They are on the new engine.

**They are driven by `EarconVoices.Plain`** — `Partials = { 1f, 0.12f }`, a
fundamental plus one faint harmonic at 12 percent, sustaining flat, described in
its own source comment as "clean tone with a little warmth." The sounds Noel
liked use `Press` and the `Warning` family. So the connect series runs the new
engine through the voice closest to a bare sine, which is why it sounds
unchanged — because it very nearly is.

**The fix is a voice choice, not wiring.** Give the connect steps and the
signature double-beep voices with some character to them.

**This is taste, so it needs his ears before it lands.** Build it so he can hear
the candidates — the Earcon Explorer reaches the connect series now that Track E
added the attributes. Do not pick final voices unilaterally.

**And note the interaction with #147:** whatever richer voice you choose for the
connect series must ALSO have a sine-set counterpart, or selecting the classic
set will leave the connect tones as the only thing that did not get plainer.

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

State: the three #153 questions with your recommendation and what Noel decided;
whether cancelling a CW repeat kills a running earcon; the binding chosen and
confirmation you pressed it on a real build; the keyboard audit items updated;
whether #143 should adopt the same speed-derived timing; how the voice-set
setting is implemented and whether all seven voices have both definitions; and
the connect-series voice candidates you want Noel to hear.
