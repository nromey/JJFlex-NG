# The master test list, triaged by tier — what a machine should be doing instead of Noel

**Sprint 33 Track E · task #149 · docs only**
**Source document:** `docs/planning/for-noel/2026-08-19-master-test-list.md` (written `e072e22f`, 2026-08-19 09:59)
**Triaged against:** `sprint33/track-e` at `dbbaf280`, which is Sprint 32 merged.

---

## What this is, and why it exists

The master test list is a good document. It is also a list of forty-four tests
that one blind operator is expected to sit down and perform by hand, and the
large majority of them are some form of "press this, does it work" — a question a
machine answers faster, more repeatably, and without spending the scarcest
resource on the project.

This document assigns every assertion in that list to one of four tiers, using
the tiers defined in the sprint plan `barefoot-harness-pileup.md`:

- **Tier 1 — in-process.** Construct the dialog, walk its automation tree or call
  its logic directly, assert. No desktop, no radio, runs in CI.
- **Tier 2 — driven.** Launch the real executable, send real keystrokes, observe
  what happens. Needs a desktop.
- **Tier 3 — radio in the loop.** Command the 8600, read its own state back.
- **HUMAN.** Ears and judgement. Nothing else settles it.

**The residue is the point.** The classification is bookkeeping. What matters is
the short list at the top of this document: the things only Noel can judge, so a
testing session goes there instead of into tab-order checks.

---

## The split, in numbers

The list holds **forty-four numbered tests**. Many of them bundle several
assertions that belong to different tiers — A1 alone asks about a window title, a
focus landing, and a tab-stop inventory, and those are three different jobs. Split
by assertion, the list is **ninety-eight items**:

- **Tier 1 — forty-five.** Roughly half the list is settled by constructing a
  dialog in a test process and looking at it.
- **Tier 2 — thirty-three.** Real keystrokes against a running build.
- **Tier 3 — six.** Genuinely needs the radio, and only six do.
- **HUMAN — fourteen**, of which **one is not a test at all** but a design ruling
  sitting in the test list by mistake.

So a forty-four-test sitting becomes **thirteen things worth Noel's ears**, and
the other eighty-five become someone else's problem — which is the entire
argument for Sprint 33.

The Tier 3 count deserves a second look, because it is the surprising one. Only
six assertions in the whole list actually require hardware. Session D reads like a
radio session, and it mostly is not: the picker's path-suggestion heuristic, the
local-only answer persisting, the REM ON explanation text and the "feature
unavailable" menu items are all decidable with no radio in the room.

---

## The human-only residue — the actual list

This is the deliverable. Thirteen tests, plus one ruling.

- **A6d — the Workshop's offline half genuinely works.** The mic check needs a
  real microphone and a judgement about whether the verdict it returns is the
  right verdict. A machine can prove the section is enabled and reachable; it
  cannot say whether "Hot" was the correct answer for the sound that went in.
- **A7b — About reads aloud, all of it.** The About body renders inside a
  WebView2 island. Nothing in-process can see it, and what a screen reader does
  inside that island is browse-mode behaviour rather than dialog behaviour. **This
  one has no proxy at all** — see the proxy section below.
- **A8 — the Diagnostics opening sentence.** "Without any coaching, can you say
  what is being recorded, at what detail, and whether a capture is running?" That
  is a comprehension test on a human who has not read the code. A machine can
  assert three facts are present in the sentence and learn nothing about whether
  it lands.
- **B-listen — the two-minute Ctrl+F1 listening pass, as one pass.** Machines can
  prove every explanation exists, resolves to the right control, and is absent
  from the focus channel. Only Noel can say whether hearing a given explanation is
  worth the two seconds it costs, and whether it is written for a ham rather than
  for us. Do this once across the session, not once per control.
- **D1b — the connect narration, heard end to end.** The machine half (not one
  word about SmartLink on a local connect) is Tier 3 and mechanical. The human
  half is whether the whole narration is the right amount of talking.
- **E2c — audio through the last-resort resampler.** If the 44.1 kHz device opens
  by way of the Windows conversion engine, does what comes out sound acceptable?
- **E3b — clicks in the monitored tone.** Ears. The glitch counter added to the
  capture is a proxy and is discussed below.
- **E4 — PC audio loudness against the radio's own speaker.** Explicitly a
  comparison against a reference by ear, and the test itself warns that "too hot
  at the default" is a finding rather than something to quietly turn down.
- **F-prose — the preset and profile mismatch messages.** Three of them (F2, F3,
  and the corrupt-file message in F1) exist to be understood at a moment of
  confusion. Whether they are clear enough to act on is a reading judgement, done
  once across the session.
- **G4d — the failure-moment offer.** Does it read clearly at the moment you are
  already annoyed that something failed? Everything else about G4 — the dialog
  appears, Escape closes it, accepting produces a capture, declining does not
  nag — is Tier 2.
- **H-ears — earcon audibility and the category vocabulary.** Are the category
  names the right words, and does each family survive real band noise? **The
  document this was meant to be run from is already stale** — see the stale
  section.
- **I1c — the disconnect is actually heard.** The mechanism (the news rides the
  arriving window's title, so a queue flush cannot eat it) is Tier 1. Whether it
  reaches the operator's ears is not.
- **I3b — a dialog title arriving mid-speech.** Whether the title follows the
  speech underway or steps on it is a timing behaviour with a perceptual verdict.
  The coalescer's state is observable, which makes a proxy possible, but the
  question asked is "does this get in the way when you are operating."

And one item that is in the test list and should not be:

- **H4's parenthetical is a design ruling, not a test.** "Whether a mute SHOULD
  outlive the session is an open question routed to you." That belongs in the
  questions queue. The test underneath it — the app and its help page agree,
  whichever way the ruling goes — is real and is Tier 1 and Tier 2.

---

## Stale, wrong, or incomplete — flagged, not dropped

A dropped test and a passed test look identical later, so every one of these
stays on the list with a correction rather than being quietly removed.

**Two items were stale within hours of being written.** The list was committed at
09:59 on 2026-08-19. Both of the following landed the same afternoon.

- **A1c is wrong. There are six rescue buttons, not five.** The test says "Tab
  should move you through exactly five buttons: Connect a radio, Settings, Audio
  Workshop, Help, Exit." Sprint 31 Track R (`6e5bf1a1`, 11:53 the same day, #101a)
  inserted **Radio Setup as the second button**, deliberately placed directly
  under the thing that just failed. The current order is Connect a radio, Radio
  Setup, Settings, Audio Workshop, Help, Exit. **A correct build fails this test
  as written**, which is the worst kind of stale.
- **H1a is wrong. There are six category checkboxes, not five.** Sprint 31 added
  **Warning sounds** (`42538245`, 15:42 the same day, #111) once the warning alarm
  had a second member to keep it company. The six are Connection, Transmit, Dialog
  and panel, Tuning and filter, Command and confirmation, and Warning sounds.

**Incomplete rather than wrong:**

- **A3 should include Radio Setup.** It carries its own Ctrl+F1 explanation, and
  the test names only Connect a radio, Settings and Audio Workshop.
- **A4 names one honest-empty case; there are two.** Exit carries no explanation
  and neither does the Help button, so both should answer with "No extra help
  here. F1 opens the help file." The wording in the test matches the code exactly,
  which is worth keeping.
- **A6's offline inventory predates three new radio-side surfaces.** It lists
  eight controls (Mic Gain, Mic Boost, Mic Bias, Compander, Speech Processor, TX
  Filter Low and High, TX Monitor). Sprint 32 added **Live Meters**, **Meter
  Inventory** and **Amplifier** tabs to the same dialog, all radio-side. The test
  as written passes while three new surfaces go unexamined — which is precisely
  the drift shape the list was created to catch, happening to the list itself.
- **H3 credits only the master switch for silencing the ATU tune sound.** Verified
  in `EarconPlayer.StartATUProgressEarcon`: it now consults
  `On(EarconCategory.Transmit)`, so it sits under the master gate **and** under the
  Transmit category. Unchecking Transmit sounds alone silences it, and that is
  worth a line in the test.
- **H2's "everything else still sounds" now spans six families**, and the sixth is
  the one the code itself flags as the one to think twice about switching off —
  Warnings fires when the app has something to say that nobody asked for.

**Stale by dependency, not by content:**

- **H-ears cannot be run from the document it points at.** The earcon masking test
  (`docs/planning/for-noel/2026-08-19-earcon-masking-test.md`) was marked stale on
  2026-08-19 by `d450a9e1`: its section 1 compares two sounds "at the same volume,
  0.30" after Sprint 32 Track E closed exactly that six-decibel tier gap, its
  section names were replaced when the explorer was rebuilt around the six
  EarconCategory values, and its build stamp predates every Track E commit. The
  question is still worth asking. The document needs rewriting against the merged
  build first.

**Two tests assert specifics where the sprint plan asks for invariants:**

- **A7a pins version numbers into the test text** — FlexLib 4.2.20.41343, Opus
  1.6.1, PortAudio revision a880212. These are correct today and become wrong at
  the next dependency bump, at which point the test fails for the wrong reason and
  trains the reader to ignore it. Restate as the invariant: **About's versions must
  match what the binaries actually report.** That is a Tier 1 assertion against
  `DiagnosticSnapshot.ComponentAssemblies`, which is now the single assembler
  behind the About page, the plain-text copy and the crash report alike.
- **A1c and H1a broke for the same reason** — both counted controls. "Exactly five
  buttons" and "five category checkboxes" are the "the third tab stop is Load
  Preset" failure the plan warns about. The durable forms are "no non-button
  control on the rescue panel is a tab stop" and "every earcon category has a
  checkbox and every checkbox maps to a category."

**A portability nit worth fixing while it is cheap:**

- **C1 hardcodes `C:\dev\JJFlex-NG\check-jjflex-processes.ps1`.** Every sprint
  build reaches Noel from a worktree, so this runs the main repo's copy of the
  script. The check is process-name based so it still gives the right answer
  today, but the path should follow the build under test.

---

## Where a machine check is a proxy — say it out loud

A suite that claims coverage it does not have is worse than one that admits the
gap. Every one of these is a machine check that **approximates** a human judgement
and must be labelled as such wherever it is reported.

- **"You should hear X" is never proven by a machine.** The strongest available
  assertion is that the text was composed correctly and dispatched to the speech
  layer. What NVDA actually says depends on NVDA — its version, verbosity,
  punctuation level and synth. This caveat applies to every "should say" in
  sessions A, B, D, F and G. It is a good proxy: it catches the wrong text, the
  missing text and the text on the wrong channel. It does not catch NVDA.
- **Focus-channel cleanliness (I2, and the first half of every B test) is a
  mechanism proof, not a taste proof.** Tier 1 can assert that a control's
  automation Name and `AutomationProperties.HelpText` are short identifiers and
  that the long explanation lives only in `JJFlexHelp.Text` — which is exactly the
  #91 defect, and exactly why `JJFlexHelp` is not an AutomationProperties member.
  Whether a given short name is short *enough* remains taste.
- **E3a's glitch counter is a proxy for clicks.** An audible click may not
  register, and a registered glitch may be inaudible. It localises a fault
  extremely well — it settles whether the audio engine saw the problem — and it
  does not answer "did that sound clean."
- **G3c's "neither sends anything anywhere" is best proven as an invariant**, not
  by watching one network trace. Assert that no network client is reachable from
  the diagnostics export path. A single clean trace proves one run.
- **H2b's "no dings" asserts no play call was made.** That is a stronger claim
  than silence at the speaker, which is the point — but note that a broken audio
  device also produces silence and would pass a listening test.
- **H4c compares a help page against a behaviour.** A machine can assert the page
  mentions persistence. Whether the page and the behaviour agree *in meaning* is a
  reading.
- **A7b has no proxy.** The About body is a WebView2 document. An in-process
  automation walk cannot see into the island at all, so there is no degraded
  machine version of this test to fall back on. It stays human until the version
  facts are surfaced somewhere the tree can reach.

---

## The triage, session by session

Each item gives its tier and the reason. Where a numbered test was split, the
letters are ours, not the source document's.

### Session A — cold start, no radio

- **A1a — Tier 2.** The shell window title ends with "no radio connected". The
  title is composed on the WinForms shell (`globals.vb`), so an in-process WPF
  tree walk does not see it. Tier 1 if the composition is extracted into something
  callable.
- **A1b — Tier 2.** Focus lands somewhere real. Focus behaviour without a shown
  window is not trustworthy in-process.
- **A1c — Tier 1.** The tab-stop inventory of the rescue panel. **Stale as
  written** — six buttons now, see above. The durable form is "nothing that is not
  one of the rescue buttons is a tab stop."
- **A2 — Tier 2.** Escape closed the picker and not the program. Assert the
  process is alive.
- **A3a — Tier 1.** Every rescue button carries a non-empty `JJFlexHelp.Text` and
  `FindExplanation` returns it from that button. This is a direct static call, no
  window required.
- **A3b — Tier 1.** Neither the Name nor `AutomationProperties.HelpText` of those
  buttons carries the sentence. The #91 invariant.
- **A3c — Tier 2.** Ctrl+F1 actually routes from a focused rescue button.
  **Proxy** for "you hear it."
- **A4a — Tier 1.** The empty-case string is exactly "No extra help here. F1 opens
  the help file." Verified present in `KeyCommands.cs`, spoken with Interrupt at
  Critical verbosity.
- **A4b — Tier 2.** The key produces it when focus is on a control with no
  explanation anywhere up the tree.
- **A5a — Tier 1.** The rescue page maps to `pages/home-no-radio.htm` and that
  topic exists in the built help. Verified in `HelpLauncher.cs`; the topic file is
  `docs/help/md/home-no-radio.md`, titled "Home Without a Radio".
- **A5b — Tier 2.** The Help button launches it.
- **A6a — Tier 1.** With no radio, every radio-side Workshop control is disabled.
  Construct the dialog with no rig.
- **A6b — Tier 1.** Those controls are out of the tab order — not merely greyed.
  The value controls too, which is the half that historically slipped.
- **A6c — Tier 2.** Arrow keys on a disabled value control speak nothing. Largely
  implied by A6b, but the Alt+L lesson says press the key.
- **A6d — HUMAN.** The mic check and the This Computer section genuinely work.
  Needs a real microphone and a judgement about the verdict.
- **A7a — Tier 1.** The version facts are correct and match the binaries. Restate
  as an invariant rather than pinned numbers.
- **A7b — HUMAN.** All of it reads aloud. No proxy exists.
- **A8 — HUMAN.** The Diagnostics opening sentence answers all three questions
  without coaching.

### Session B — the Ctrl+F1 listening pass

Every B test has the same two halves, and they land in the same two places every
time. The first half — "on focus you hear the name and nothing more" — is **Tier
1** for all five controls: assert the Name is an identifier and no sentence lives
in `AutomationProperties.HelpText`. The second half — "Ctrl+F1 gives you the
explanation" — is **Tier 1 for the lookup**, because `JJFlexHelp.FindExplanation`
is a static method over a DependencyObject and can be called directly on the
constructed control.

- **B1a, B1b — Tier 1.** Debounce tuning speech, on the Tuning tab.
- **B2a, B2b — Tier 1.** The Radio name box on the Radios tab.
- **B3a, B3b — Tier 1.** The SmartLink intent combo, "Whether you want to reach
  this radio from away."
- **B4a, B4b — Tier 1.** Mic Gain in the Workshop, and its explanation keyed to
  the mic check's Good, Hot and Quiet verdicts.
- **B5a, B5b — Tier 1.** The Audio system combo, both device lists and the
  Transmit audio quality combo in Audio Devices — the three-sentence lecture must
  be reachable only by the key.
- **B6 — Tier 1.** The short hint that stayed. `CycleFieldControl` is the
  canonical case: `AutomationProperties.HelpText` remains legitimate for a
  three-word operating hint, and "Arrows to change" should still be there. The
  durable form is a length bound, not a string match.
- **B-route — Tier 2.** The chord itself routes from inside a modal dialog. One
  test, not one per control.
- **B7 — Tier 2.** Ctrl+F1 with a dropdown open. This exercises the logical-tree
  fallback in `FindExplanation`, which exists precisely because a popup's visual
  chain ends at the popup root. Tier 1 only if a popup can be realised without
  showing the window, which is doubtful; assume Tier 2.
- **B-listen — HUMAN.** Length and clarity of the explanations, once.

### Session C — process hygiene

- **C1 — Tier 2, and fully scriptable.** Ten launch-and-exit cycles varying the
  exit path, then `check-jjflex-processes.ps1`. There is no human judgement
  anywhere in this test, and it is tedious enough that a human will do it once and
  never again. Automate it and run it every build.

### Session D — local connect

- **D1a — Tier 3.** Not one SmartLink word in a local connect narration.
- **D1b — HUMAN.** Whether the whole narration is the right amount of talking.
- **D2a — Tier 3.** The local-only offer appears on a connect to an unregistered
  radio, in its own window, Escape-declinable.
- **D2b — Tier 1.** The answer persists and is not asked again, and shows up in
  Settings. A config round trip through `RadioConfig`.
- **D3a — Tier 3.** Full Home replaces the rescue page after a connect lands.
- **D3b — Tier 2.** F2 lands on Home.
- **D4a — Tier 1.** The gated-feature menu items exist and carry their
  explanation. Verified present in `NativeMenuBar.cs` for both advanced noise
  reduction and diversity; buildable against a stubbed rig.
- **D4b — Tier 2.** Selecting one speaks which gate is shut.
- **D4c — Tier 3.** The gate shown matches the real 8600's real licence.
- **D5a — Tier 1.** The REM ON explanation text.
- **D5b — Tier 2.** The setting changes cleanly in the UI.
- **D5c — Tier 3.** The radio's REM ON state matches at the next connect. This is
  the only part of D5 that needs hardware.
- **D6a — Tier 1.** The three-times-running suggestion heuristic. Pure logic.
- **D6b — Tier 1.** An explicit choice beats the prefill, now and next time —
  persistence logic. Tier 2 confirms it in the picker.

### Session E — the sound path

- **E1 — Tier 1.** The status line and device rows name the host API in play and
  mark the system default. String composition over an enumerated device list.
- **E2a — Tier 2, with a hardware precondition.** A 44.1-locked device opens
  under WASAPI rather than refusing. Needs that device present; needs no radio.
- **E2b — Tier 1.** The diagnostic log states plainly that Windows is resampling
  and how to get native audio back. The decision is unit-testable.
- **E2c — HUMAN.** Whether the result sounds acceptable.
- **E3a — Tier 2.** Run the tone under a detailed capture and read the audio
  engine's glitch record. **Proxy** — see above.
- **E3b — HUMAN.** Clicks, by ear.
- **E4 — HUMAN.** Loudness against a reference, and the coupled-default trap.

### Session F — presets and profiles

Almost the whole session is Tier 1. These are file-format and decision-logic
tests wearing a bench session's clothes.

- **F1a — Tier 1.** A corrupt presets file produces the "could not be read"
  outcome rather than a silent reset.
- **F1b — Tier 1.** The unreadable file is kept next to the new one.
- **F1c — Tier 2.** The app actually says so at launch.
- **F2a — Tier 1.** Save, export, re-import: schema version present, TX EQ
  carried, recorded radio mic input carried. A serialization round trip.
- **F2b — Tier 1.** Applying on a different input changes nothing by itself.
- **F2c — Tier 2.** The mismatch is announced.
- **F3a — Tier 1.** A mic profile made with a different Windows microphone leaves
  the Windows level alone.
- **F3b — Tier 2.** It says the computer is using a different microphone.
- **F4 — Tier 1.** The PC Cleanup chain — noise reduction, gate and its values —
  rides the mic profile through save and apply.
- **F-prose — HUMAN.** Are those three messages clear enough to act on.

### Session G — diagnostics at the moment of need

- **G1a — Tier 2.** Ctrl+J then Ctrl+D from inside an open Settings dialog, and
  again to stop.
- **G1b — Tier 1.** The two strings, and that the second carries a real time and
  length.
- **G2a — Tier 1.** The session name composition marks it as a capture with its
  time.
- **G2b — Tier 2.** It appears in Browse saved logs as its own session.
- **G3a — Tier 2.** Export writes one file where you chose.
- **G3b — Tier 2.** The problem-report bundle gathers recent sessions plus a
  setup snapshot into one file.
- **G3c — Tier 1.** Neither sends anything anywhere — as a no-egress invariant.
- **G4a — Tier 2.** The offer appears in its own titled window and Escape closes
  it.
- **G4b — Tier 2.** Accepting produces a usable capture.
- **G4c — Tier 2.** Declining is remembered; no nagging loop.
- **G4d — HUMAN.** Whether it reads clearly at the moment of failure.
- **G5a — Tier 2.** "Measure now" produces a real folder total and crash-report
  count.
- **G5b — Tier 2.** "Delete loose log text files" reports what it removed.
- **G5c — Tier 2.** The compressed sessions still list afterwards. This is the
  assertion that catches a delete that took too much.
- **G6a — Tier 1.** The tab-to-help-topic mapping: Diagnostics goes to "The
  Diagnostic Log", every other Settings tab to Settings and Profiles.
- **G6b — Tier 2.** F1 pressed on each tab opens what the mapping promised.

### Session H — earcon categories

- **H1a — Tier 1.** The category checkboxes exist, indented under the master.
  **Stale: six, not five.**
- **H1b — Tier 1.** Each category checkbox explains itself on Ctrl+F1.
- **H2a — Tier 1.** The gate logic. `EarconPlayer.On(category)` is master AND
  category, both static, trivially testable.
- **H2b — Tier 2.** End to end: with Dialog and panel sounds off, opening a
  dialog makes no play call while other families still do.
- **H3a — Tier 1.** The master outranks every category switch.
- **H3b — Tier 1, and the highest-value item in the session.** *No* earcon path
  plays without consulting the gate. Every earcon carries an `[Earcon]` attribute
  naming its category, so this is a registry-driven invariant over all of them
  rather than a spot check on the two that historically escaped — which is the
  only version of this test that stays true as earcons are added.
- **H4a — Tier 1.** The quick mute writes through to config immediately.
- **H4b — Tier 2.** After a relaunch, earcons are still off and the Settings
  checkbox agrees.
- **H4c — Tier 1, weakly.** The help page and the behaviour agree. **Proxy** — a
  machine checks the page mentions persistence, not that it means the same thing.
- **H-ears — HUMAN.** Audibility against band noise, and the category vocabulary.
  Blocked on rewriting the masking-test document.
- **H4-ruling — HUMAN, and not a test.** Should a mute outlive the session.

### Session I — the speech-day regressions

- **I1a — Tier 1.** The arriving picker window's title carries the disconnect
  news. This is the whole mechanism of the fix — the news must ride the window
  that causes the flush, not sit in the queue the flush destroys — and it is a
  string assertion on a constructed window.
- **I1b — Tier 3.** A real radio pulled out from under the app.
- **I1c — HUMAN.** It is actually heard.
- **I2 — Tier 1, and the flagship.** No control in Settings recites a paragraph as
  its name or description. This is a sweep over every dialog, it is the #87 and
  #91 pair expressed as an invariant, and it is the single assertion most likely
  to catch the next regression of this class before Noel does.
- **I3a — Tier 2.** The coalescer queued rather than flushed. **Proxy** for the
  perceptual question.
- **I3b — HUMAN.** Whether the title steps on the speech underway.

### Session J — installer

- **J1 — Tier 2, with a clean-VM precondition.** Nothing here needs ears: install
  on a machine that has never had .NET 10, launch, confirm no runtime prompt. It
  is human today only because no VM harness exists. It remains a mandatory
  pre-public-release gate either way.

---

## What the list does not cover at all

Not part of the triage, but found while doing it, and a test list that has gone
five days without absorbing a sprint will keep drifting. Sprint 32 landed these
surfaces and nothing on the master list touches them:

- **The meter arc.** The rebuilt Live Meters panel as a live view, the Meter
  Inventory tab, Ctrl+M opening the panel (and no longer switching your audio on),
  the meter-source migration and its config version stamp.
- **The Amplifier and Tuner tab** in the Workshop.
- **The Earcon Explorer as a voice bench**, and #138 with it: the scratchpad road
  mutes the radio "so earcon sounds are audible", which defeats a bench whose
  entire question is whether a sound survives band noise — and the menu road does
  not mute, so one dialog behaves two ways depending on how it was opened.
- **The shutdown-drain hazard recorded in `d450a9e1`.** Start the bench tone,
  leave it running, close the app. Track H replaced the farewell's computed
  completion with an observed drain; Track E added two long-lived mixer inputs
  that never signal end-of-source. If the drain waits on mixer silence rather than
  on the CW source, a held tone blocks shutdown. The commit filed it as a check to
  run by ear — **it is not an ear test.** It belongs in Session C and the
  assertion is that the process exits within a bound.
- **Category-list navigation** in Settings and the Workshop (#134).
- **Repeat-last-message as a short history** (#70).
- **The twenty-nine commands bound to no key** (#130) — Track B's territory, but
  nothing on the master list would notice if one silently acquired or lost a
  binding.

---

## Recommendations

- **Correct the two stale expectations before the next pass.** A1c and H1a both
  fail against a correct build today.
- **Tag the master list with tiers in place**, so a future sitting can skip what
  the harness already covers. This document deliberately does not edit
  `2026-08-19-master-test-list.md` — it is an execution doc that Noel writes
  results into, and the Sprint 30 lineage owns it. Tagging is a small edit made
  once, against the merged build, by whoever next rewrites it.
- **Restate counted assertions as invariants.** Every test in this list that broke
  in five days broke because it counted controls or pinned a version.
- **Where the harness reports a proxy, make it say "proxy" in its output.** The
  point of writing them down here is lost if the suite prints a green tick that
  reads like the human test passed.
