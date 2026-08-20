# Sprint 32 — elmer-meter-pileup

**Created 2026-08-19.** Base branch: `honest-tx-audio`. FlexLib 4.2.20.

## The sprint in one paragraph

The radio publishes over a hundred meters. JJ Flexible asks for the list on
every connect, traces it, and then offers the operator a hardcoded eight. This
sprint builds the layer that can see all of them, and then the two things that
need it: a meters panel that scales to any radio, amplifier or tuner, and a
diagnostic Elmer that tells an operator which stage of their radio is dead and
hands them evidence they can paste into an email to Flex.

Alongside it, three independent lanes: audio (one synthesis vocabulary, an earcon
registry, a real sonification bench), navigation (category-list navigation,
keyboard annotation), and persistence (why a released slice comes back, and the
two profile verbs that were never built).

## The three findings this plan is built on

**1. The enabling FlexLib patch is already written and reviewed.** `MIGRATION.md`
carries a section headed "Not yet applied: a public accessor for the meter list
(reviewed 2026-08-16)". A previous session hit this exact wall, worked out the
edit, decided the day had not come, and wrote down the precise patch plus the
reasoning "so the day someone needs a meter picker they find the exact edit
rather than rediscovering it." That day is today. Apply it as written; do not
redesign it.

**2. The narrowing to eight meters happens in our code, not FlexLib's.** FlexLib
exposes `Meter.DataReady(Meter meter, float data)` — a generic per-meter event
carrying the meter itself, with name, source, units and range. `FlexBase` then
subscribes to ten *named convenience* events (`MicDataReady`, `SWRDataReady`,
and so on), discards the meter identity, and re-emits as `Radios.MeterType`, an
eight-value enum. `JJFlexWpf.MeterToneEngine` declares the same eight again as
`MeterSource`. `MetersPanel` declares the same eight again as a hardcoded string
array. Three hand-maintained copies of the same literals, stacked over an API
that was never limited to eight. This is a lossy adapter, not a design — and
once identity is destroyed at the `FlexBase` boundary, nothing above can recover
it. The fix is to stop flattening, not to build a bigger picker.

**3. FlexLib has no meter-added event, and the list grows during registration.**
`traceMeterInventory` re-logs whenever the set *changes* rather than once,
precisely because the first version snapshotted the radio mid-registration and
captured eleven meters with the TX-side ones still to arrive. Any consumer that
reads the inventory at construction time gets a truncated census. That is the
same defect shape as #129 (`BuildSlotControls()` runs once in the constructor and
never resyncs). Treat "the meter world arrives late" as a first-class constraint,
not an edge case.

## Placement ruling (Noel, 2026-08-19)

The diagnostics go in the **Audio Workshop, as new tabs**, and the existing RX
advisory ("Why is my radio silent?") **moves in from Settings > Audio** to join
its TX sibling. Both #122 and #123 warned "do not add a seventh audio surface";
this is the answer.

Accepted consequence: the Workshop grows to five or six tabs, which is exactly
the "leaky tab strip" problem Noel objected to in Settings. So #134's
category-list navigation is no longer a Settings-only job — it becomes the
navigation model for both surfaces, and has to land in this sprint rather than
trailing it. That is Track G, and it is why Track G merges last.

---

# Track A — The meter inventory (foundation)

- **Location:** main repo, `C:\dev\JJFlex-NG`
- **Branch:** `sprint32/track-a`
- **Blocks:** B, C, D. Start A first and alone. (E also waits on A4, the Workshop
  file split, but on nothing else — it can start as soon as that one commit
  lands, ahead of the rest of Phase 1.)

## Phase 1 — BLOCKING. Report the moment it is committed.

**A1. Apply the FlexLib meter-list patch.**

Take it verbatim from `MIGRATION.md`, next to `FindMeterByName` in
`FlexLib_API/FlexLib/Radio.cs`:

```csharp
/// <summary>JJFlex patch: enumerate the radio's meter inventory.</summary>
public ImmutableList<Meter> GetMeters()
{
    lock (_meters)
        return _meters.ToImmutableList();
}
```

Then, in the same commit:

- Mark it `// JJFlex patch` the way the VitaSocket edits are marked.
- Add it to MIGRATION.md's numbered reapply list (it becomes item 11), and move
  the "Not yet applied" section into the applied list with today's date.
- **Delete the reflection block in `FlexBase.traceMeterInventory`.** Two ways to
  reach the same private field is how one of them rots unnoticed. The method
  keeps its job (trace the inventory on change); only the access route changes.
- Note it as reportable upstream to Flex — an enumerator for a list whose every
  other accessor is public is an obvious gap.

**A2. Identity-preserving meter subscription in `FlexBase`.**

Subscribe to FlexLib's generic `Meter.DataReady(Meter, float)` per meter, and
raise a new event that carries the meter itself.

**Do NOT delete `MeterType` or `MeterChanged` in this track.** `MeterToneEngine`
and other callers read them, and Track B is what retires them. Leaving the old
path alive as a shim is deliberate; removing it here breaks B mid-flight.

**A3. The `MeterInventory` service.**

Lives in `Radios` — the layering runs `Radios` BELOW `JJFlexWpf`, so anything
placed in `JJFlexWpf` is unreachable from the radio layer.

Per meter it must carry: name, `Meter.Source` (`SLC` / `AMP` / `HAAPI`),
`SourceIndex`, units, range low and high, current value, and a **last-update
timestamp**. Staleness is a reading, not an absence — a meter that stopped
updating is information, and #123's rules depend on being able to say so.

It must expose the inventory **partitioned by source and source index**, because
that is how amplifier, tuner and per-slice meters separate, and `Meter.Source`
already tags every meter this way. Track D needs no new concept, only this.

**Change notification is the load-bearing part.** FlexLib raises nothing when a
meter appears. Detect change centrally — the count-and-set comparison
`traceMeterInventory` already performs, done once in the service rather than
scattered — and raise an event when the set changes. Everything downstream binds
to that event rather than sampling at construction.

**A4. Split `AudioWorkshopDialog.xaml.cs`.**

It is 4,866 lines in a single file, already declared `partial`, never split —
while `SettingsDialog` in the same folder is already split into six per-tab
partial files. Follow that existing convention: one file per tab,
`AudioWorkshopDialog.<Tab>.cs`.

This is a **pure mechanical move with no behaviour change**, and it gets its own
commit. It is in the blocking phase because three tracks add a Workshop tab this
sprint and a fourth restructures its navigation. Without the split that is four
agents editing one region; with it, git sees four unrelated files.

## Phase 2 — after Phase 1 is reported

**A5. Meter Inventory tab in the Workshop.** Read-only. Which meters this radio
actually has, what each reads right now, grouped by source, with staleness
shown. #123 explicitly recommends shipping this *before* the decision tree: it
closes the invisible-meter-list finding (commit `d5aecf2b`), gives Don something
to quote today, and produces the real data needed to write good rules tomorrow.

**A6. Copyable text export of the inventory.** The seed of Track C's evidence
block, and useful on its own.

---

# Track B — Rebuild the meters panel

- **Worktree:** `../jjflex-32b`
- **Branch:** `sprint32/track-b`
- **Depends on:** A Phase 1
- **Covers:** #129, #124, #131, #126, #127
- **Owns:** `MetersPanel.xaml.cs`, `MeterToneEngine.cs`, the meter section of
  `AudioOutputConfig.cs`

These five tasks are one job, not five. Each independently rewrites or touches
the same 433-line file, and #129's root fix is the structural change the others
need. Doing them as separate tracks would rewrite `MetersPanel` twice.

**B1. #129, the root fix.** The panel must be a **live view over the engine's
slot collection**, not a constructor snapshot. `BuildSlotControls()` is called
once at construction and never again, so slots added later exist in the engine
with no controls at all — which is exactly what Noel hit: he added a slot, got
slot 5, and could see nothing else.

**B2. #124, the model move.** Off `MeterToneEngine.MeterSource` and the parallel
hardcoded string array, onto `MeterDefinition` / `MeterSourceRef` with a string
key. `MeterSlot`'s own doc comment already concedes this: "new code should use
Definition directly." The bridge was built and never crossed.

Populate the source picker from Track A's inventory. **A hundred entries in a
combo is its own accessibility problem**, so this needs a real design pass —
follow the device-picker precedent from #62: a "common meters" default and an
"all meters" mode, grouped by source (this radio / amplifier / slice N).

Track B is the **only** track permitted to retire `Radios.MeterType` and
`JJFlexWpf.MeterSource`. Do it once nothing reads them, and say so in the
completion report so the merge knows the shim is gone.

**B3. Config migration — highest risk in the sprint.** `AudioOutputConfig`
persists the meter source **as an integer**. Existing users have slots saved as
ints; without a migration, everyone's meter tones silently repoint to whatever
now sits at that ordinal. This is precisely the class of bug as #34, the
PortAudio device-index issue. **Write the migration first, and test it against a
real pre-existing `audioConfig.xml`, not a synthetic one.**

**B4. The slot redesign.** Noel's words: "Making it so that you have tabs to go
through all slots is not efficient, so you'd need a combo to select a tone and
then modify / enable / do whatever with it. Also would allow for del key /
remove yes/no query." So: a slot selector combo, one set of controls that
retarget to the selected slot, and Delete with a confirm.

**B5. #131, the runaway test tone.** The Test button's stop timer only fires
`slot.ToneProvider.Active = false` when `!MeterToneEngine.Enabled` — but the only
route into the panel, Ctrl+M, *enables* meters. So the stop condition is
guaranteed false and the tone never stops. Stop unconditionally on expiry.

**B6. #126, Ctrl+M does two jobs.** It shows the panel and turns meter tones on.
Separate them; the panel needs a way in that does not change audio state. This
touches key bindings, so the keyboard audit applies.

**B7. #127.** The meters expander is the only one on Home with no
expand/collapse earcon. Wire `PlayExpand` / `PlayCollapse`.

**B8. Pan resolution.** Three values (Left / Center / Right) are not enough —
Noel: "we need that to be slider or have more values though if we have more
items." Make pan continuous, or at minimum five to seven positions.

---

# Track C — The analyzer

- **Worktree:** `../jjflex-32c`
- **Branch:** `sprint32/track-c`
- **Depends on:** A Phase 1
- **Covers:** #123 (the engine) and #122 (the TX chain walk, as its first
  ruleset)
- **Placement:** Audio Workshop, per the ruling above

**C1. Rules as data, never as code.** A decision tree of any size hardcoded in
C# becomes unmaintainable and untestable. Express rules as a table or file:
preconditions, meter thresholds, verdict text, remedy. Then rules can be added
without a build, tested in isolation, and eventually shipped as updates through
the Data Provider.

**C2. Three-state observability, not two.** Every stage is BROKEN, HEALTHY, or
**NOT OBSERVABLE FROM HERE**. Over SmartLink some stages live on the far
machine; on some models a meter is simply absent. "Checked 14 of 19, could not
read 5" is honest. "All good" when five were unreadable sends the operator
hunting the wrong end of the problem, which is worse than saying nothing.
`SilentRadioAdvisory` already models the honest fallback — copy that discipline
rather than reinventing it.

**C3. Staleness is a reading.** Timestamp everything. Track A's inventory
supplies the timestamps.

**C4. #122, the TX chain walk, as the first ruleset.** Twelve stages, and nearly
every observable already exists as a meter, a trace line or a property — nothing
composes them:

1. Is a mic selected, and is it present
2. Is the mic capturing (dBFS and LUFS already measured)
3. PC-side gain and boost
4. Is PC audio even on (`startRemoteAudioThread` has exactly one caller, so this
   gates everything downstream)
5. Opus encoder built at the negotiated rate
6. VITA TX packets leaving, to the right port
7. Radio ACK'd the stream as OPUS
8. Radio mic input selection — PC, MIC or BAL. Wrong selection is silent
   transmit with everything upstream healthy
9. Is a mic profile selected — empty means no modulation
10. Radio TX chain: mic gain, processor, EQ, TX filter
11. MicData meter — the radio's own report of what it hears. A -120 floor here
    with stages 1-9 healthy is the signature that stalled the whole
    honest-tx-audio investigation
12. Forward power and SWR — did RF actually leave

Report **the first dead stage**, in the operator's own words, with the fix. The
shape is already written: "Your radio has no mic profile selected, so audio from
your computer will not be transmitted."

**C5. The evidence block.** Copyable, for a Flex support ticket: the readings
that justify the verdict, with units, timestamps, firmware version, model and
serial. Don should paste it into an email without translating anything. This is
what makes the analyzer worth building beyond our own debugging — it turns every
user into a competent bug reporter, and costs nothing per user.

**C6. Start small and honest.** Thresholds need field calibration from real
radios; what counts as a bad SWR or a hot PA is not a guess. Ship a handful of
high-confidence rules as a skeleton to grow from testers' evidence, not a
hundred speculative ones.

**C7. Move the RX advisory in.** `SilentRadioAdvisory` moves from Settings >
Audio into the Workshop beside its TX sibling. Leave a pointer behind where it
used to be — the same courtesy the CW notifications move already set as
precedent. **Move the call site; do not change the method's signature.**

---

# Track D — Amplifier support

- **Worktree:** `../jjflex-32d`
- **Branch:** `sprint32/track-d`
- **Depends on:** A Phase 1 (inventory partitioning)
- **Covers:** #125

**There is nothing to wait for.** Noel asked 4O3A directly for developer
material on 2026-08-19. Their answer: it is all in FlexLib and they have no code
to give. Driver downloads exist, but no SDK and no samples. That is a green
light, not a gap — no NDA, no SDK request, no follow-up. It also means **FlexLib
is the contract with no spec behind it**, so what the hardware publishes at
runtime is the only authority, which makes the bench trace capture the actual
documentation rather than a nice-to-have.

**E1. Wire the amplifier.** `Amplifier.cs` gives Handle, IP, Port, Model,
SerialNumber, Ant, State (PowerUp / SelfCheck / Standby / Idle / TransmitA /
TransmitB / Fault), IsOperate, `OutputConfiguredForAntenna`, `FindMeterByIndex`,
`FindMeterByName`, `MeterAdded` / `MeterRemoved` events, and its own
`List<Meter>`. Command `amplifier set <handle> operate=0/1`; subscription
`sub amplifier all`. On `Radio`: `AmplifierList`, `ActiveAmplifier`,
`FindAmplifierByHandle`, `FindMetersByAmplifier`.

**E2. Do not conflate two different amplifiers.** `HAAPI.cs` is the 8000-series
**built-in** amp (AmpMode, AmpFrequency, AmpModuleGain, AmpXmitState,
AmpIsSelected, AmplifierFault; `sub ha_api amplifier`, `sub ha_api fault`).
Noel's 8600 has this whether or not an external amp is attached. An external
4O3A amp is a separate concept on a separate path.

**E3. Not a bug — do not re-raise.** `FindMetersByTuner` filters on
`SOURCE_AMPLIFIER`. That is correct: the TGXL piggybacks on the amplifier status
stream. This is recorded in `4o3a-integration.md` and has been re-derived
repeatedly. Read it before flagging anything as a vendor defect.

**E4. Tuner: scaffold only.** `Tuner.cs` exists and is complete (State, IsOperate,
IsBypass, `AutoTune()`, RelayC1/C2/L, PttA/B, network settings, its own meter
list with Add/Remove). Both `Amplifier` and `Tuner` carry meter machinery Flex
built deliberately, so the tuner almost certainly publishes meters. But there is
no TGXL on site — Noel plans to order one from DXE by end of month, with a
Palstar dummy load. Scaffold read-only if cheap; do not guess at behaviour.

**E5. Deliverable: a for-noel bench procedure.** Verification requires moving the
amplifier near the radio on 120V and network to discover what meters it actually
adds. **Building is not blocked; verification is.** Write the procedure in the
briefing format with annotation slots, so the bench session is one run rather
than an improvised evening.

---

# Track E — The audio bench

- **Worktree:** `../jjflex-32e`
- **Branch:** `sprint32/track-e`
- **Depends on:** A Phase 1 (only for the Workshop file split)
- **Covers:** #112, #113, #119, #120, #128, #118, #114, and the tier half of #115

This is a dependency chain, and it runs in this order deliberately: **build the
instrument before the work.** #112 gives one vocabulary, #113 makes every sound
reachable, #119 and #120 make them auditionable against real band noise — and
only then are #114, #115 and #118 judgeable at all.

**F1. #112 — one synthesis vocabulary.** Three additive synthesisers exist and
do not know about each other: `VoicedToneSampleProvider` (the real engine — 15
named voices, arbitrary partials, brightness, inharmonicity, ADSR, tremolo,
vibrato, gating, tracked noise, equal-power normalisation, already documented as
intended for reuse); `DecayingGavelSynthesizer` (hand-rolled, unwired since
2026-04-21, kept as a reference nobody read); and `PlayAdditiveTone` (added
2026-08-19 for the warning alarm, the crudest of the three). Render alert
earcons through `VoicedToneSampleProvider`. Alert earcons then inherit ADSR —
Noel's decay request, "for some tones I'd also consider adding more of a fade
out (decay)... you might use it for a button press" — plus inharmonicity and
tracked band-noise for free.

**F2. #113 — an earcon registry.** `EarconPlayer` exposes 45 no-argument public
methods; the explorer reaches 18. Unreachable today: the entire connect series
including `ConnectSuccessTone` (the app's most recognisable sound, and you
cannot play it on demand), all four JJ-key leader tones, tune and ATU, mute,
dialog open and close, expand and collapse.

Drive the explorer from a **registry** — an attribute or a static table
`EarconPlayer` owns — so a new earcon appears automatically and adding a sound
never again requires remembering to edit a dialog. Sections must mirror the six
`EarconCategory` values so the explorer and the Settings on/off switches speak
one vocabulary; today "Meter Tones" heads a group of alert beeps that are not
meter tones. Continuous earcons (ATU progress) need a Start/Stop pair, not a
fire-and-forget Play.

**F3. #119 — the explorer becomes a live bench.** Start and stop, pan, volume,
play a series. Judged against real band noise, which is the only environment
that matters: Noel can produce plenty of noise at S2 with no antenna.

**F4. #120 — extend the Earcon Scratchpad.** It already exists, it is already
the most interactive audio surface in the app, and it is not in the Workshop at
all. Add sustained tone, voice selection, scale walk, harmonic sweep — Noel's
"ol' slide whistle thingy". **UserVoices is import-only.** His ruling: "We'd
probably want to add it in code, I'm not sure how we'd 'author' a tone in the
actual interface, that might be too complex for a radio application." Import
yes; in-app authoring no. Reachable from a menu item.

**F5. #128 — the toggle-tone sweep.** Every operator-facing toggle plays the
on/off tone, whichever way it is reached, application-wide: checkbox on gives a
higher tone, off gives a lower one. PC Audio on and off plays nothing at all
today, which is what surfaced this.

**F6. #118.** `Beep(int frequencyHz = 800, int durationMs = 150)` and
`Warning1Beep()` are byte-identical, and the whole PTT warning family is one
sine getting higher. Differentiate them once F1 supplies the vocabulary.

**F7. #114 and the tier half of #115.** The confirmation tones read fine for
direction — Noel: "I can definitely tell rising from falling on feature on and
feature off, that's never been an issue" — they are simply bland next to the
alarm. The modern earcon tier sits at 0.2-0.3 volume against the legacy tier's
0.5-0.7, a 6 dB gap with no reason behind it. Normalise the tiers here.

**Deliberately NOT in this track: #115's camouflage problem and #116's ducking.**
Sounds under about 50 ms are clicks by physics and spectrally indistinguishable
from QRN; raising gain just makes a louder static crash. That needs the tonal
redesign F1-F4 enables, judged on the bench F3 builds. Ducking is separate
plumbing in a different audio stack (`PostDecodeProcessor`, PortAudio side —
earcons are NAudio) and only helps one listening topology. Sequence both after
this track proves what the vocabulary can do.

---

# Track G — Navigation and keyboard

- **Worktree:** `../jjflex-32g`
- **Branch:** `sprint32/track-g`
- **Covers:** #134, #130, #132
- **Merges LAST** — it restructures the container every other track is adding
  tabs to.

**G0. #132 — the destructive remove option is unreachable by Tab.** Do this
first; it is the smallest item and the highest-consequence one.

`RemoveRadioDialog.xaml` wraps its two scope radios in
`KeyboardNavigation.TabNavigation="Once"`, which gives the whole group ONE tab
stop. Tab lands on "Remove from the list only" (pre-checked), and the next Tab
goes straight to the Remove button. **Tab never visits the second option** —
only the arrow keys reach it. So an operator who tabs does not merely find the
destructive option hard to select, they never encounter it, and confirming
commits the safe default after an interaction that felt complete and deliberate.

That is what happened to Noel on 2026-08-19. His settings were NOT deleted and
the spoken receipt was accurate — `deleteSettings` was genuinely false. Nothing
in the code was wrong; the navigation was.

Fix: `TabNavigation="Continue"` on the radio group so Tab visits both. **KEEP
the pre-checked safe default** — it is load-bearing for a separate deliberate
decision (RigSelectorDialog.xaml.cs ~600: bare Delete is safe as an unmodified
keypress precisely because the confirmation's default scope deletes nothing).
Reachability is the defect, not the default. Also name the arrow-key affordance
in the body text, where focus already starts.

Why it survived review, worth carrying into the rest of this track:
`TabNavigation="Once"` is textbook-correct WPF and right on a settings page with
many groups. It silently assumes the operator knows to arrow. In a dialog whose
whole purpose is one irreversible choice between two options, that assumption
becomes load-bearing in a way it never is on a settings page. **When auditing
navigation elsewhere in this track, ask what a Tab-only operator encounters —
not what is theoretically focusable.**

**G1. #134 — category-list navigation, NVDA-style.** Noel's spec: "they have a
category list... ctrl tab goes to the next category, ctrl+shift tab goes to the
previous category. That's cleaner than a leaky tab strip."

Apply it to **Settings and the Audio Workshop both.** The Workshop grows to five
or six tabs this sprint, which is the exact condition that makes a tab strip
leak. One pattern, two surfaces.

**G2. #130 — 29 commands bound to `Keys.None`,** only 2 of them annotated.
Nothing in the source distinguishes "menu-only on purpose" from "nobody ever
assigned a key." Annotate all 29 first — that is the deliverable, and it is what
makes the next pass possible. Then assign keys where one is genuinely missing.
Noel named PC Audio on and off ("No hotkey for PC audio on and off available
that I know of, you have to do it in the menu") and agreed the Ctrl+J leader
layer is the right home rather than a new flat hotkey.

**G3. The keyboard audit is this track's definition of done.** All seven steps
from CLAUDE.md, and step 7 is not optional: **press the key on a real build.**
An Alt+L binding shipped completely dead one build after being added because the
handler tested `e.Key == Key.L`, which is never true while Alt is held — WPF
reports `Key.System` and puts the real key in `e.SystemKey`. It compiled, it
reviewed clean, and the chord was simply never handled.

Also relevant here and easy to get wrong: `AutomationProperties.HeadingLevel`
does not give single-letter navigation inside a dialog. `H` and friends live in
browse mode; a WPF dialog runs in focus mode where `H` types a letter. Section
navigation needs a real key — F6 and Shift+F6, the Windows convention, which is
what the Workshop already uses.

---

# Track H — Profiles and persistence

- **Worktree:** `../jjflex-32h`
- **Branch:** `sprint32/track-h`
- **Covers:** #117 (which absorbs #59), #58's diagnostic, #70
- **Independent of everything else.** No shared files with A–G.

Added 2026-08-19 evening, after Noel's live test reframed #117 from "slices are
unmanageable" into something well-defined.

**H1. The slices are the radio's, not ours — this is a PROFILE problem.** JJ
Flexible contains zero slice-creation calls; the trace shows all four arriving
from the radio as `sliceAdded:mine`. Releasing a slice changes live state only;
the radio restores its global profile on the next connect. Noel released slice
D, relaunched, and it came back.

**H2. Tell the operator the change is provisional.** Releasing a slice succeeds,
sounds successful, and is silently discarded at disconnect. **This is the actual
defect** — the slice handling works; nothing communicates that the work will not
survive. The persistence step exists and is fully wired (Radio menu > Profiles >
Save → `Rig.SaveProfile` → `theRadio.SaveGlobalProfile` → `profile global save`),
and Noel — who knows this codebase better than anyone — could not find it.

Evaluate `Radio.AutoSaveProfile(string state)` FIRST. It exists in FlexLib
(`Radio.cs:8616`, command `profile autosave "<state>"`), JJ Flexible never calls
it, and it is the radio's own save-on-disconnect concept. **Its semantics are
radio-side and undocumented — the method is one line that sends a command, so
reading our source tells you nothing. This needs a bench observation, not code
reading.** Find out what it actually does before designing around it.

**Noel's framing, 2026-08-19, and the two traps in it.** He raised offering to
save on change: *"generally, if you tune the radio or any radio, when you turn
it off, it saves stuff. Of course, for connect that won't be the case."* The
analogy is right and he spotted the flaw in it himself.

- **THE DISCONNECT MOMENT IS NOT THE POWER-OFF MOMENT.** A standalone radio
  powering down has one operator and one state. A networked radio does not power
  down when a client leaves, and under MultiFlex another operator may still be
  on it. The global profile is global; the departure is per-client. **Auto-saving
  on disconnect can capture another operator's slice layout, filters and band,
  silently, overwriting a profile with a state this operator never chose.** Any
  automatic save must be gated on being the only client, or must not exist.
- **A PROMPT AT EXIT IS THE WRONG INSTRUMENT even single-client.** "Save changes
  before disconnecting?" is the unsaved-changes dialog, and its failure mode is
  that it fires whether or not anything meaningful changed, so operators learn to
  dismiss it reflexively — and one day it eats the change they wanted. A prompt
  trained to be dismissed is worse than no prompt, because it creates the belief
  that the operator was asked.

**RECOMMENDED SHAPE: a receipt at the moment of change, not a question at the
moment of departure.** "Slice D released — this will not survive disconnect
unless you save the profile" costs one utterance, arrives while the operator has
full context about what they just did, and demands no decision. Notify where
there is context; prompt only where there is a real choice. A disconnect prompt
has both properties wrong.

Noel on the prompt: *"I'm not sure I'd do this."* Trust that. It does not cost
the receipt.

If an auto-save setting is built anyway, it is per-radio — the serial-keyed
config model already exists for exactly this kind of per-rig preference.

**H3. Un-stub profile creation.** In `NativeMenuBar.cs` (~2550) the Profiles
dialog wires Select, Save and Delete for real, but `OnAdd` and `OnUpdate` are
stubs that speak *"Profile creation not yet available"* / *"Profile update not
yet available"*. So Save **overwrites the profile you are on** — there is no Save
As, and an operator cannot keep a four-slice layout and build a one-slice layout
beside it.

Two things to fix, and the second matters as much as the first: build the
missing verbs, **and stop presenting verbs that announce their own absence after
the operator has already navigated to them and pressed.** A sighted user often
sees a greyed control and does not try; a keyboard and screen-reader operator
pays the full round-trip first. Disable, hide, or label — any of the three beats
the current behaviour. Same pattern as #121; sweep for others while here.

**H4. #58 — Noel has specified the CW vocabulary, and it supersedes the guard.**

His ruling, 2026-08-19: *"the CW sends modes for all slices which isn't
necessary. All it needs to send is the number of slices taken, and the number of
slices available, and when you select mode or go to another slice, we send that,
SL A USB, change the mode and it sends SL A LSB."*

**This is not a smaller version of the current message — it is two different
messages at two different moments**, which is why the `ActiveSlice` guard was
never going to be right. The guard tried to make one message quieter. The real
problem is that a PER-SLICE property was being announced during a BULK STATE
REPLAY, so four individually-correct announcements answered a question nobody
asked. Connect wants a CENSUS of the set; a slice or mode change wants an
IDENTITY plus a STATE.

Filtering picks one member and calls it representative, which is arbitrary.
Summarising describes what actually happened. Note the same shape recurs in
Track C, where a hundred meters arrive and the operator wants "14 checked, 5
unreadable" rather than fourteen readings.

The two messages:

- **On connect (and on any bulk slice change): a census.** Slices taken and
  slices available. NOT one message per slice.
- **On mode change, or on moving to another slice: `SL <letter> <mode>`.**
  `SL A USB`; change the mode and it sends `SL A LSB`.

**Everything needed already exists — build nothing new to get the data:**

- `Slice.Letter` is a FlexLib property. There is no index-to-letter mapping to
  write; `FlexBase.ActiveSliceLetter` (FlexBase.cs:7860) already uses it.
- `Radio.AvailableSlices` (Radio.cs:15082) is radio-reported remaining capacity.
- `Radio.MaxSlices` (Radio.cs:15096) is the model ceiling.

**SETTLED by Noel, 2026-08-19: the census is USED / TOTAL.** His words: *"you
could just send 3/4 if 3 slices are used, 4 total."* So three slices open on his
8600 sends `3/4`, and a full radio sends `4/4`.

Why used-over-total rather than a bare free count, recorded so it is not
re-litigated: **the denominator varies by model** — 2 slices on a 6300 or 8400,
4 on a 6600 or 8600, 8 on a 6700. A bare "1 free" means something very different
on a 6700 than on an 8600, and forces the operator to remember which radio they
are on to interpret it. `3/4` carries both numbers in one token, makes `4/4`
read unmistakably as full, and leaves the free count trivially derivable. It is
also a shape a CW operator already reads fluently, the slash being the
portable-call prosign.

Take TOTAL from `Radio.MaxSlices`, not from `AvailableSlices` — the latter is
remaining capacity and is the wrong number for the denominator.

Both formats are now approved copy and need no further sign-off: the census as
`<used>/<total>`, and the per-slice announce as `SL <letter> <mode>`.

**The diagnostic is now optional, not blocking.** The guard is being replaced
rather than repaired, so the mechanism behind its failure stops mattering for
the fix. Log it anyway if cheap — what `theRadio.ActiveSlice` returns at each
`DemodMode`, alongside `s.Index`, `s.Active`, and `s.ClientHandle` versus
`theRadio.ClientHandle` — because static reading of `_slices.Add` plus
`ActiveSlice` predicts ONE announcement where Noel heard four, and an
unexplained contradiction in this area will resurface. **Do not guess at the
mechanism; measure it.**

**H4a. The exit farewell loses its LAST CHARACTER, and the obvious fix cannot
work.** Diagnosed 2026-08-19 from Noel's ear.

The app sends `73 <SK> ee` — note **two** trailing dits (E E). He consistently
gets `73 SK dit`. **One** dit. Not a truncated tail: exactly the final character.

**CORRECTION TO THE SPRINT 30 NOTE, which named the wrong constant.** It said
"the 1500 ms pre-teardown window is shorter than a 15 WPM farewell." There is no
1500 ms exit window — the only 1500 in the tree is `SpeakConnectStatus`'s connect
delay (MainWindow.xaml.cs:435). The shutdown handler allows
`PlayCwSK.Invoke().Wait(5000)` (ApplicationEvents.vb ~335), which is ample for a
sub-second string.

**The real mechanism.** `EarconCwOutput.PlayElementsAsync` completes on a
COMPUTED duration, never an observed one (EarconCwOutput.cs:225-232):

    int waitMs = totalMs + 50;
    await Task.Delay(waitMs, linked.Token).ConfigureAwait(false);
    item.Completion.TrySetResult();

Nothing asks the device whether the buffer drained. So `Wait(5000)` is SATISFIED
EARLY rather than expiring, and the next lines in the shutdown handler —
`EarconPlayer.Dispose()` and `ScreenReaderOutput.Shutdown()` — tear the device
down while the tail is still in hardware. 50 ms is less than a typical NAudio
output buffer. The final dit is the most vulnerable element in the string:
shortest, and last.

**THEREFORE: raising the timeout changes nothing.** A generous timeout and an
optimistic completion signal produce the identical symptom, and only one of them
responds to a bigger number. Anyone who tries `Wait(10000)` will see no change
and wrongly conclude the diagnosis was wrong. **Do not start there.**

**Fix by observing drain**, not by padding a guess: query the output's playback
position, or wait on `PlaybackStopped`, before resolving the completion. A
trailing silence element would also mask it and is acceptable as a stopgap, but
it leaves every other exit-time utterance exposed to the same race — the defect
is in the completion contract, not in this one string.

**Verify by ear, not by compiling.** The whole failure is that the code is
plausible and the audio is short. Close the app at a couple of speeds and count
the dits.

**H4b. CONNECTED close is far worse, and the double-fire guard is why.**
Diagnosed 2026-08-19 from Noel testing the same action with one variable changed.

His two results, each repeated twice:
- **Not connected, close:** `73 SK dit` — nearly complete, missing only the final
  dit. That is H4a.
- **Connected, close with Alt+F4 without disconnecting first:** **"dah dah"** and
  nothing else.

`--...` is the digit 7. So the connected path delivers the first TWO ELEMENTS of
a roughly two-second string — about 150 ms — before the audio device is destroyed
underneath it.

**The mechanism, and it is one cause producing both symptoms.** `FlexBase`
~2085, on the disconnect path:

    _ = ScreenReaderOutput.PlayCwSK?.Invoke();
    ScreenReaderOutput.SkAlreadyPlayedThisSession = true;

The `_ =` DISCARDS the task — nothing awaits it. Then the flag makes
`ApplicationEvents.MyApplication_Shutdown` skip its own
`PlayCwSK.Invoke().Wait(5000)`. So on a connected close the farewell is started
fire-and-forget and teardown runs straight through it into
`EarconPlayer.Dispose()` and `ScreenReaderOutput.Shutdown()`.

**THE GUARD IS THE CAUSE.** It was added for a real complaint — hearing 73 twice
when disconnecting via menu and then closing — and it works. But the WAIT lived
only in the path the guard suppresses. So the flag does not merely prevent a
second farewell; it removes the only code that was waiting for the first one.
Two paths both play, only one knows how to wait, and the flag hands the job to
the one that does not.

**Fix both layers, and in this order:**

1. **Make the disconnect path await its own farewell**, bounded the way Shutdown
   already bounds its call. Whoever plays it owns waiting for it — do not move
   the wait around, because the next path that plays SK will inherit the same
   trap. (There are exactly two today; assume a third.)
2. **Then fix the completion contract from H4a**, which is what still eats the
   final dit once the wait is honoured. Step 1 alone turns "dah dah" into
   "73 SK dit", not into a clean farewell.

**Do not "fix" this by deleting the guard.** That restores the doubled 73 Noel
complained about, and trades a truncated farewell for a repeated one.

**Verification is two cases, not one:** close while connected, and close while
not connected. A fix that only ever gets tested disconnected will look complete
and leave the worse bug in place — which is how it shipped this way.

**H5. #70 — repeat-last-message becomes a short history.** `_lastMessage` is a
single string; the coalescer's `_lastByKey` is per-key dedup state cleared on
urgent flush and unusable as history. This was stranded in Sprint 30's Track F
purely because of the speech-core quarantine — it is pure code and was never
bench-gated.

**The speech-core quarantine is DROPPED for this sprint** (Noel, 2026-08-19). It
was written to protect a live session that never ran, and had already lapsed in
practice. No track holds an exclusive lock on `Radios\Speech\*` or
`ScreenReaderOutput.cs` — the ordinary symbol contract below governs instead.

---

# A note on track letters: there is no Track F

Deliberate. Three separate things in this project's history are called "Track F"
— Audio Track F (#10, receiver simulation on IQ playback, bench-gated), Sprint 30
Track F ("The Operator's Ear", the live session that never ran), and this
sprint's audio bench, which was briefly labelled F before being renamed to E.
Skipping the letter costs nothing and removes a collision that would otherwise
cost a conversation.

---

# Model assignment

**Every track in this sprint runs on Opus.** Recorded explicitly rather than left
to the inherit-the-session default, because Sprint 30 annotated a model per track
and this plan originally did not.

Why no track is downgraded here:

- **A** is architecture — a vendor-tree patch, a new service, and a subscription
  rewrite that four other tracks build on. Getting the `MeterInventory` surface
  wrong costs three tracks a rework.
- **B** carries the config migration, which silently repoints every operator's
  meter tones if it is wrong. Same defect class as the PortAudio device-index bug.
- **C** is the hardest design work in the sprint: a rules engine, three-state
  observability, and evidence a stranger can act on.
- **D** reads an undocumented vendor contract at runtime with no spec behind it.
- **E** designs a synthesis vocabulary and a registry that must survive future
  additions.
- **G** is accessibility-critical. The Alt+L binding shipped completely dead
  because of one subtle WPF fact (`e.Key` versus `e.SystemKey` under Alt); that is
  precisely where a weaker model loses.
- **H** carries async race conditions, an audio completion contract, and approved
  user-facing copy it must not paraphrase.

The genuinely mechanical work — A4's file split, G2's 29 annotations — is a small
fraction of tracks that are otherwise hard, and splitting those out into cheaper
agents would cost more in coordination than it saves.

**When launching, omit the model override so the agent inherits the session
model, and run the session on Opus.** Do not pass a lower tier for B, C or D on
the assumption they are follow-on work; they are the expensive half of the sprint.

# Execution order

**Start immediately, four tracks:** A, E, G, H.

E, G and H are independent of the meter arc. E touches only the Workshop's earcon
tab (isolated by A4) and the earcon engine; G touches the dialog navigation
shells and `KeyCommands`; H touches profiles, slices and the speech core, and
shares no files with any other track.

**When Track A reports Phase 1 committed** — FlexLib patch applied and reflection
deleted, identity-preserving subscription in place, `MeterInventory` service with
change notification, and the Workshop file split — **start B, C and D.**

Peak concurrency is seven. Above the six-CLI guideline, which is fine under the
background-agent model (thirteen in one evening is proven) but would not be under
Model A.

# Merge plan

Track A is the merge target. Merge order as tracks complete:

**B, then D, then C, then E, then H, then G.**

- **B before D** — B is the only track permitted to retire `MeterType` and
  `MeterSource`. If D merges first it will have been coding against the shim
  while B removes it.
- **C after B and D** — the analyzer's rules reference meters by name, so they
  want the model settled before they land.
- **E second to last** — isolated by the file split, low conflict, but it is the
  largest single body of new UI.
- **H anywhere** — it shares no files. Placed late only because nothing depends
  on it; pull it forward freely if it finishes first.
- **G last, always** — it restructures the navigation container that A, C and E
  are all adding tabs to. Merging it before those tabs exist means restructuring
  a container that is about to change again.

**Build after EVERY merge, without exception.** Sprint 30's lesson: two tracks
merged with **zero textual conflict** and the build then failed, because one
track was told to reuse a symbol and another track moved it. Git cannot see that
class of collision and will not warn you. A clean `git merge` is not evidence
that the result compiles.

## Known merge collisions, recorded as tracks reported them

**`TRACK-INSTRUCTIONS.md` conflicts add/add on EVERY merge.** Each branch carries
its own. It is noise — resolve by keeping the target's copy, and note the cleanup
phase deletes them all anyway. Do not read it as a real conflict.

**`AudioWorkshopDialog.xaml` — three or more tracks append a `TabItem`.** Tracks
A (Meter Inventory), D (Amplifier) and C (analyzer) all add to the same element.
Textual conflict is certain and trivial: keep all the tabs.

**`VisibleSections()` — TAKE TRACK G'S VERSION WHOLE AND DELETE THE OTHER ARMS.**
This one is not trivial and getting it wrong silently reintroduces coupling.

Track A added an index arm (`3 => MeterInventoryContent`). Track D added a
name-based arm (`AmplifierTab.IsSelected ? AmplifierContent : ...`). **Track G
deleted the switch entirely** and discovers the content panel from the selected
`TabItem` at runtime, unwrapping ScrollViewer / Border / ContentControl /
Decorator. It needs neither index nor name, so it subsumes both arms — and a new
tab then works with no shell edit at all.

Track G proved the discovered panel is the IDENTICAL OBJECT the old switch named
on all three original categories, and re-pressed F6 and Shift+F6 on every
category through injected keystrokes. **Delete Track A's arm and Track D's arm
when taking G's version.** Leaving either one back-doors the index or name
dependency G removed.

**`NativeMenuBar.cs` — three tracks, three regions, no expected contention.**
Track H at ~1970 (the `AddNotImplemented`/`AddStub` helpers) and ~2550-2650 (the
Profiles dialog callbacks); Track G at ~2242 (the Settings deep link, which had
been calling `landed.Focus()` on a TabItem — a silent no-op once the tab strip is
templated away). Verify rather than assume, but these do not overlap.

**`EarconPlayer.cs` and `KeyCommands.cs` — Track H made additive edits to files
Tracks E and G own.** Three read-only accessors plus two members on the private
`AudioChannel` in the former; a six-line method body reduced to one line in the
latter. Nothing moved or resignatured. Both were flagged rather than hidden.

**`FlexBase.cs` gained the `partial` keyword** (one token, line 75) from Track D
so its amplifier code could live in its own file. Three tracks edit this file;
the meter section is Track A's and is untouched by everyone else.

### THE SHIM IS GONE — Track B retired it, as planned

`JJFlexWpf.MeterSource` is **deleted outright**, along with the `MeterSlot.Source`
and `MeterSlot.Waveform` bridges over it, replaced by `MeterSlot.Retarget(...)`.
`Radios.MeterType`, `MeterChangedDel` and `MeterChanged` are **deleted from
`FlexBase`** along with all eight raise sites; `MeterToneEngine` was the only
consumer.

Track A deliberately left these alive so Track B could code against them. **They
no longer exist.** Any track still calling them will fail to compile at merge —
which is the good outcome, since the alternative is a silent behaviour change.
Track B also corrected two comments in `FlexBase` that described the retired
symbols as live, one of which carried a `<see cref="MeterChanged"/>` that would
have dangled.

`MeterSlotConfig.Source` changed type from the deleted enum to `string`. That is
a signature change on a public class, forced by the retirement and correct
independently — an integer on disk would throw and lose the whole config file.
It is in a file Track B owns and was reported rather than done quietly.

### THE ONE HAZARD THAT IS NOT TEXTUAL — CLOSED 2026-08-20, it does not occur

**Tested properly and the interaction is FINE.** Noel left the Audio Workshop
open with the ATU progress tone running, Alt-Tabbed to the MAIN window,
confirmed the tone was still audible, and closed the app from there. His words:
*"closing keeps the tones playing and CW, then it kills the app, farewell works,
mixer is intact."*

So a long-lived alert-mixer input that never signals end-of-source does NOT
block, delay or suppress the farewell. Track H's drain observation waits on the
CW source specifically rather than on mixer silence — which is exactly what Track
E could not verify from its own branch, and it guessed the risk correctly while
guessing the mechanism wrong.

**A false alarm was recorded first, and how it happened is worth keeping.** The
first attempt reported the farewell missing entirely. It was not: Alt+F4 with the
Workshop focused closes THE WORKSHOP, not the app, because the Workshop is
non-modal with its Owner deliberately cleared so Alt+Tab works. A dialog close
correctly plays no farewell. The test instruction said "close the application",
which has two valid readings in a multi-window app.

**Rule for every future test procedure: a step that closes something must NAME
the window**, and where a chord could hit either, say how to be sure which has
focus.

Still open, and NOT settled by the above: whether a continuous earcon outlives
its dialog when the Workshop is CLOSED rather than merely unfocused. Track E
claims closing the dialog stops them; the Closed handler appears not to. Same
shape as #131. Being checked separately.

### Superseded — the original hazard note, kept for the reasoning

**A held bench tone may block application shutdown.** Neither track could catch
this alone, and no merge tool will show it.

Track H replaced the exit farewell's *computed* completion with **observed
drain**: the provider signals end-of-source on the first short read, then
`WaveOut.GetPosition()` must advance by the buffer-chain depth. That is the right
fix and it is why the farewell stopped being cut off.

Track E, separately, added **two LONG-LIVED alert-mixer inputs** — the ATU
progress earcon and the new bench tone. **Neither ever signals end-of-source**,
by design: they play until stopped.

Track E's own analysis, and it is the right question: *if the drain check waits
on mixer silence rather than on the CW source specifically, a held bench tone
would block shutdown.* Track E could not verify it, because Track H's
`EarconPlayer` additions are not on Track E's branch.

**Mitigating factors, which reduce the risk but do not close it:** both
long-lived inputs are removed on stop, and closing either dialog stops them. So
it needs an operator to close the app with a bench tone still running.

**The check, once E and H are both merged:** start the bench tone or ATU progress
earcon, leave it running, and close the application. It should exit promptly with
the normal farewell. If it hangs, or the farewell is delayed, the drain check is
waiting on the wrong thing and must be narrowed to the CW source rather than
mixer silence.

**Do this by ear on a real build.** It is a shutdown-path timing interaction; it
will not show up in a compile and it will not show up in a diff.

# Cross-track symbol contract

Ownership, so no two tracks move the same ground:

- **A owns** `Radio.GetMeters()`, `MeterInventory`, `FlexBase`'s meter
  subscription section, and the `AudioWorkshopDialog` partial-file split.
- **B owns** `MetersPanel`, `MeterToneEngine`, and the meter section of
  `AudioOutputConfig`. **B alone may retire `MeterType` / `MeterSource`.**
- **C owns** the analyzer files, and may move `SilentRadioAdvisory`'s call site
  but **not** its signature.
- **D owns** the amplifier and tuner files.
- **E owns** `EarconPlayer`, the earcon registry, the Scratchpad, and
  `AudioWorkshopDialog.Earcons.cs`.
- **G owns** the navigation shells of `SettingsDialog` and
  `AudioWorkshopDialog`, and `KeyCommands`.
- **H owns** `Profile_t`, `ProfileReporter`, the Profiles dialog wiring in
  `NativeMenuBar`, the slice and profile sections of `FlexBase`, and
  `Radios\Speech\*` / `ScreenReaderOutput` for #70. Note G also edits
  `NativeMenuBar` for navigation — **different regions, but flag it in both
  completion reports** so the merge knows to look.

**The rule that applies to every track:** reuse the symbol you are told to
reuse. **If you conclude that symbol should move or change signature, REPORT IT
in your completion report — do not do it.** That is exactly the invisible
dependency that broke the Sprint 30 merge.

# Definition of done

- Clean x64 build after every merge, verified by the `N Error(s)` summary line —
  not by grepping for the word "error", which matches warning prose.
- Verify the exe timestamp is current after every build. Stale binaries have
  wasted whole testing sessions.
- Keyboard audit for any track that changed a binding — certainly G, probably B
  via Ctrl+M.
- Config migration tested against a real pre-existing `audioConfig.xml`.
- Test matrix written to `docs/planning/agile/sprint32-test-matrix.md`.
- CHM rebuilt if any help page changed.
- Changelog entries in the user-facing voice — state, not developer action, and
  no internal jargon.

# Bench and tester dependencies

These gate verification, not building. Every track codes to completion
regardless.

- **Track D** needs the amplifier bench session: amp moved near the radio, on
  120V and network, to discover what meters it actually publishes.
- **Track C** needs field calibration for its thresholds. Ship the skeleton;
  grow it from testers' evidence.
- **Track B** wants Noel's ear on the common-versus-all meter split before the
  picker is final.

# Deliberately not in this sprint

- ~~**#117 and #59, slice management.**~~ **PROMOTED into Track H** the same
  evening. It was left out as "a different subsystem needing its own design
  pass" — then Noel ran the test, and the design pass took twenty minutes
  because the answer turned out to be profile persistence plus two stubbed
  verbs. Worth remembering as a pattern: a task that looks like it needs
  research may only need one observation from the operator.
- **#132**, the remove-radio dialog choosing the wrong path. Blocked on one
  observation: does NVDA report "Remove the radio and its settings" as checked or
  not checked. Small fix once known.
- **#21**, the orphan-process test. In flight on the laptop.
- **#133 and #135**, build hygiene. Small enough to do directly rather than as a
  track; #133 is queued for tonight's build work.
- **#115's camouflage half and #116's ducking.** Sequenced after Track E, for
  the reasons given there.
