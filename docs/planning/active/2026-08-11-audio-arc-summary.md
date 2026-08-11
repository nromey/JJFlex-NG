# Where things stand — audio arc, end of 2026-08-11

Written as a file rather than chat so you can read it at your own pace. Nothing
in here lives only in scrollback.

**Headline: everything is merged to `main`, which is also now on FlexLib
4.2.20.** Today's delta on main is 85 files, about 4,900 insertions. Debug x64
builds clean.

---

## 1. What got proven at the radio

The measurement that mattered: **a tone injected at −10 dBFS read −11 on the
radio's `SC_MIC` meter.** One decibel across the tone generator, the Opus
encode, the VITA transport, the radio's decoder, and its meter. That is the
honest-transmit-audio claim demonstrated end to end with a number, and it
calibrates the coaching thresholds against a real reference for the first time.

You took it at **zero watts**, which separately proved that an audio check never
needs to touch the air — every meter the coaching depends on sits upstream of
the power amplifier.

Also confirmed working by you, at the rig:

- PC audio plays, and PC Output Volume genuinely changes it.
- `Ctrl+J, V` volume mode works, and you said you will use it.
- The live mic verdict speaks (from the main window).

## 2. What shipped today

**Already on `main`, merged and building.**

- **Track A — the audio hub.** PC Output Volume became a real setting (0 to
  +24 dB, default +12, which is the 4.0× you had been living with), with a hard
  limiter added to a gain loop that previously had none. Audio menu split into
  PC-audio and on-radio groups. Home expander fields. `Ctrl+J, V` volume mode.
- **Track C — the test tone.** 440 Hz default, frequency yours to choose and
  remembered, replaces the mic rather than mixing, passband warnings, local
  monitor toggle.
- **Workshop track (A-2 + C-2).** Two levels dialogs replacing the menu up/down
  pairs, the old duplicate items deleted, the Audio Check now defaulting to
  dummy load with a low-power alternative, a read-only readout field with focus
  landing on Start, and `Ctrl+S` / `Ctrl+O` / `Ctrl+Enter`.
- **Keys track.** `KeyScope.Global` made genuinely global, `Ctrl+Space` PTT
  fixed in the expanders, `Alt+Shift+S` now leads with the verdict while keyed,
  `Ctrl+J, K` mic check, `Ctrl+J, G` tone arm.
- **Engine track.** LUFS metering per BS.1770 with a 37-check harness, and the
  orphan-process shutdown chain fixed at every link.
- **PortAudio** upgraded from a 2021 build to master `a880212`, with the git
  revision stamped into the DLL so it identifies itself.
- **Main moved to FlexLib 4.2.20** — the clean fast-forward that had been
  pending since 3 August.

## 3. The three findings worth remembering

**The `Alt+Shift+S` failure was an absence, not a collision.** The main window
routes keys through the WinForms shell before WPF sees them, so Global works
there. WPF dialogs had **no registry dispatch at all**, so every global chord
died in every dialog — and WPF's access-key matching then claimed the orphan.
The audit list of commands that were dead inside every dialog includes **F12,
stop CW**. You could not stop a CW transmission from any open dialog. You found
that class of bug by hitting its most harmless instance.

**The ghost processes and the "settings won't save" afternoon were one bug.** A
wedged audio close left a thread in an unbounded wait; `Audio.Finished()`'s
outer timeout was 5000 *iterations* rather than 5 seconds and was unreachable
anyway; the abandoned thread was a **foreground** thread, so it pinned the
process after the window closed; and the surviving ghost then raced the live
instance over the shared config file.

**Opus was already 1.6.1 and the docs were wrong.** Worth remembering as a
pattern rather than an incident — four times today a hand-maintained
description had drifted from the thing it described: the meter that read the
analog-only path, a plan doc whose "DRAFT" status outlived its shipped feature,
`LeaderKeyHelp()` silently dropping six commands, and CLAUDE.md's Opus version.
The fix in each case is the same: generate the description, or make it
verifiable in seconds.

## 4. What you found by using it that code review would not have

Six design defects, all now built or queued, none of which were visible from
reading the source:

- A menu is the wrong instrument for riding a value — it dismisses after every
  activation.
- The tracing dialog cannot turn the automatic trace on or off, because that
  trace is a code-level flag with no setting behind it.
- The Audio Check should not transmit by default.
- The safety line reports watts without naming the mode, so dummy load reads as
  "transmitting, 0 watts."
- Global keys die inside dialogs.
- The transmit-status preamble sits in front of the answer while you are keyed.

## 5. What is not verified

**Everything from the three new tracks is compile-verified only.** Nothing has
been launched. Specifically worth exercising:

- **`Alt+Shift+S` inside the Audio Workshop** — it should now speak transmit
  status instead of saving a preset. This is the single best one-keystroke check
  that the routing fix works.
- The Audio Check's dummy-load default, and the safety line in both modes.
- The two levels dialogs under a live rig.
- The readout field's behaviour with NVDA — quiet while it updates, fresh when
  you review it.
- **Repeated launch and exit cycles**, watching Task Manager for ghost
  `jjflexible.exe` processes. That is the orphan fix's only real test.
- **Audio through the new PortAudio.** It is a deliberately self-contained
  commit so it can be reverted alone if something misbehaves.

## 6. Open items, in rough priority order

- Live-verify the merged build (above).
- The tracing and diagnostics surface — auto-trace control, the state-blind
  "Start or Stop Tracing" button, level explanations, and renaming the
  user-facing concept to "diagnostic log" rather than "log", since log means the
  QSO logbook to a ham.
- Command Finder keywords for the new levels dialogs and workshop keys — a
  handoff the Workshop track could not do, since another track owned that file.
- Track F, the receiver simulation for IQ playback, including the AGC sweep.
- Track D, the input-rescue pipeline. Its presets want Track F to tune against.
- The PC-audio stream arriving at −20 to −35 dBFS, which is why +12 dB was
  needed in the first place.
- `paWinWasapiAutoConvert` for the 44.1 kHz shared-mode refusal — we never set
  WASAPI stream info at all.
- Sprint 29's 83 unticked test items, of which 45 need no radio.

## 7. Housekeeping

- Three track worktrees still exist: `jjflex-audio-w`, `jjflex-audio-k`,
  `jjflex-audio-e`. Removable whenever.
- `honest-tx-audio` and `main` are identical and **unpushed** — 30-odd commits
  of local-only work. Worth pushing for durability.
- Your working tree still holds about 210 insertions of uncommitted planning
  work from a previous session, plus an untracked `elmer-beacon-patch.md`. Not
  mine, so I left them alone.
