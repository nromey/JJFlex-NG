# Track reports — the ten-track fan-out, 2026-08-16

**Why this file exists:** each track agent reported to the orchestrator session
when it finished. Those reports lived only in a conversation, where Noel could
not read them. This is the durable copy.

**Where each track's code is:** worktree `C:\dev\jjflex-<track>`, branch
`bsr/track-<track>`, all pushed to origin. Nothing merged.

**Reading order for tomorrow: A first.** It is the largest change, the most
sensitive path, and its findings contradict the theory it was given.

---

## Track A — roster and connect · `bsr/track-a` · 8 commits · Fable

**Its Phase 1 map is at `docs/planning/active/qsy-pileup-handshake.md`, section
"Phase 1 deliverable — the roster and connect state machine, as read 2026-08-16"
(line ~519 in the track-a worktree).** Read that before the diff.

### Where the map contradicts the four roots it was given

- **Root A, half right.** The three erasure sites exist, but `PreferRemotePath`
  was documented and implemented as **session-only** — there was no persisted
  path preference anywhere to erase. **Don never lost a stored preference; he had
  no way to state one.** The "migrate the existing bool" instruction was vacuous:
  the chain is a new fact, not a migration. Naming landmine found:
  `RadioConfig.ConnectionPreference` already exists and means SmartLink
  *transport* (forward vs hole-punch), not path.
- **Root B, wrong mechanism.** The `PaintRoster` exclusion is real but only
  matters on repaint. The actual mechanism is two clobbers: `OnRadioFound`
  overwriting `row.Name` with the broadcast nickname, and — worse —
  **`RecordSighting` overwriting the profile's `Nickname` on disk**, destroying
  the operator's name permanently rather than hiding it.
- **Root C, prescription already implemented.** FlexBase already resolves by
  ClientHandle everywhere. The impostor record is **fabricated by FlexLib's
  discovery path** (`Discovery.cs:679` — client_id null, is_local_ptt false,
  IsThisClient never set; `Radio.cs:14458` drops the empty-client_id correction).
  The defect was FlexBase *trusting* `IsThisClient` and that record's contents.
- **Root D, right disease, wrong organ.** `LanAvailable` is already live-only and
  the connection cache is write-only on this branch. The immortal stale fact is
  **`AutoConnectConfig.IsRemote`, frozen at configure time** — the best code
  match for the 20–30 s local hang with zero SmartLink activity (two 10 s
  `TryAutoConnect` local waits).
- **The double Enter is two dialog-layer mechanisms, neither in FlexBase.** All
  three JIT-refresh sites provably continue after refreshing. Enter on an offline
  remote row *deliberately* started a SmartLink pass and dropped the connect
  intention (`HandleOfflineConnectAttempt`), plus a focus-restore-onto-a-
  discovery-button accident.

### What it built, in testability order

1. **One Connect button, per-radio ordered chain.** `RadioConfig.PathChain`
   (`ConnectPathKind` Local/SmartLink, Connect reserved), edited from the path
   combo — now enabled for *every* radio and persisted — and a checkable Default
   Connection Path submenu. `IsRemote` derives from the chain for every row.
   Enter carries the connect intention through SmartLink sign-in and resumes when
   the list lands: **one Enter.** Force verbs (Connect Locally / Connect over
   SmartLink) in the context menu; **force-remote never falls back.** The Remote
   button is gone; its list job became Show Remote Radios / Refresh Remote List
   in the same menu. The connect layer walks fallbacks **with announcements**.
2. **Availability expiry.** Auto-connect walks primary-then-other-path, announced,
   instead of obeying the frozen bool; the selector's offline dead ends walk to
   SmartLink instead of stopping.
3. **Presence.** Own-handle recognition regardless of `IsThisClient`; fabricated
   records cannot blank clientID, downgrade LocalPTT, or announce as "another
   client". FlexLib untouched.
4. **Union merge and names.** `UserNickname` choice field (mirrors the
   PreferredAccount pattern); Settings writes the choice, sightings keep the
   observation, display and speech prefer the choice.
5. **Connection history.** `Radios/ConnectionHistory.cs`, 10-entry ring at
   `radios\{serial}\connect-history.json`, local-only, recorded from both connect
   paths. Record-only, per scope.
6. **Keyboard audit done** — Alt+R removal heads-up in the keyboard reference;
   getting-started and smartlink-remote pages rewritten (they also still
   described the browser sign-in the native dialog replaced).

### Judgement calls and owed

- `setupRemote`/`ReconnectRemote` gained **optional** `allowInteractive`
  parameters — outside its named area, reported per the signature rule; all
  existing call sites compile unchanged.
- The old dual-homed "this connect only" combo choice is gone; one-shot forcing
  lives only in the menu verbs.
- Auto-connect's `IsRemote` is not rewritten after a fallback success — the walk
  self-heals each startup. Candidate follow-up.
- **PRESS THE KEY is owed on everything:** Enter resume, menu verbs, path combo,
  Shift+F10 items, the removed Alt+R. Compile-verified only.

---

## Track B — telemetry honesty · `bsr/track-b` · 10 commits · Opus

- **Forward power.** The three bench samples now render as **0.05 / 0.174 /
  0.074 watts**. `SMeter`'s contract untouched; added `ForwardPowerWatts` plus
  shared compact/spoken formatters. Switched: Home's TX field and its Space
  announcement, `Ctrl+S`, the multi-slice status builder, the braille power cell
  (whose `watts > 0` guard made the cell *vanish* below a watt), the meter speech
  summary, and Live Meters.
- **Two more lies in the same readouts.** `_PowerDBM` defaulted to `0`, and 0 dBm
  is one milliwatt — idle now reads −150 dBm. And **above S9, `SMeter` already
  returns dB-over-S9 plus 9**; four sites multiplied the excess by 6 and `Ctrl+S`
  by **10**, so 4 dB over S9 was announced as "S9 plus 24" or "plus 40". **This
  changes spoken strings — sanity-check at the radio.**
- **The trace-flood fix had never landed** (the orchestrator reverted it and left
  the plan claiming otherwise). Fixed here.
- **Pacing: recommendation only, nothing changed.** Opus frames are 10 ms;
  `Thread.Yield()` returns immediately when nothing else is ready, so the no-data
  path **busy-spins at Highest priority on `OpusRXListLockObj` — the same lock
  `AddRXData` needs.** The finding: **`RXAudioStream` declares a public
  `OpusPacketReceived` event, raised per packet, and nothing subscribes.** So the
  loop can wait on a signal — no FlexLib patch, zero added latency. Constraints
  written down: the handler must only `Set()`, and the wait needs a ~10 ms
  timeout because a half-duplex radio sends no RX audio while keyed.
- **Handler leak fixed** — each meter claimed independently.
- **Mic selection** verified while keyed, warns aloud and re-asserts on
  divergence; changes made *through* JJ Flex move the expectation instead.
- **GPS** — lock leads, and **lock was not in the announcement's transition key at
  all**, so the reference actually locking could go unannounced. `freq_error_ppb`
  added.
- **Meter inventory** kept, three defects fixed: it traced 102 lines **while
  holding FlexLib's `_meters` lock**, `GetField` ran per mic-meter event, and the
  guard depended on `ICollection`. `MIGRATION.md` now carries the exact additive
  `GetMeters()` patch a picker would need.

**Decisions wanted:** milliwatts vs decimal watts for speech ("50 milliwatts"
reads better than "0.05 watts"); `DBmToPower` has no callers anywhere and could
be deleted; "1.0 watts" should be "1 watt".

---

## Track C — settings that stick · `bsr/track-c` · 5 commits · Fable

- **The convention.** Settings gained OK / Apply / Cancel; Apply always present
  and enabled. `ApplyAllSettings(bool closing)` is the core; NativeMenuBar's
  post-close persistence moved into a `SettingsApplied` callback so Apply-and-stay
  saves for real. Per-feature buttons removed: "Apply to connected radio",
  "Save profile", and `AutoConnectSettingsDialog`'s bespoke Save.
- **The radio-name save half.** Profile edits commit on OK/Apply and **stash
  across tab revisits — the old tab silently lost edits on every tab switch.**
  Needs joint verification with Track A.
- **Port-forward edits survive OK**, with the same authority gate and
  confirmation. Also fixed: a tier-only connection-mode change used to be
  discarded, because it only saved as a side effect of the removed button.
- **Queued intents have a voice** — an OK-only `AdvisoryDialog` receipt listing
  each waiting item.
- **REM ON reachable while disconnected** — `RadioConfig.RemOnOnConnect`
  (LeaveAlone/TurnOn/TurnOff), applied at each connect.
- **No-physical-access flag** — pre-populated from the path chain with the guess
  *shown*, enumerated consent cascade, uncheck reverses only settings still in
  bundle state, teaching prompt suppressible via versioned key
  `no-physical-access-cascade-v1`, receipt always shown.
- **Router mapping truth** — the drifted `SetSmartLinkPortForwarding` comment
  corrected, plus **every UI string that repeated the lie**, plus
  `networking-tier1-manual-port.md`, **which was actively teaching the
  misconfiguration.**

**Reported, not fixed:** **the TX Controls dialog is a dead door** —
`FlexBase.ShowTXControlsDialog` has never been assigned since the Sprint 11
cutover, so its REM ON checkbox was unreachable at runtime. **`StaticIpControl`
keeps its own Apply button** deliberately — a half-typed static IP committed by a
blanket OK could strand a radio — **but that is a convention exception needing a
ruling.**

---

## Track D1 — Live Meters navigable · `bsr/track-d1` · 3 commits · Fable

Eight readings became focusable read-only text boxes (`MakeMeterReading` replaces
`MakeMeterLabel`); text assigns only on change so an unchanged reading never
resets the review cursor; fields renamed `_xLabel` → `_xBox` to prevent drift.

**Live region removed, with reasoning recorded** — eight polite announcers at
2 Hz, dominated by an S-meter that moves nearly every tick, starve the reading
you want and talk over the review commands the boxes exist to serve. **Behaviour
change: the Live Meters tab no longer speaks on its own.** *(Superseded in
design by the Announce decision — see the plan.)*

**Honesty papercuts taken:** "no reading yet" instead of `--` (which NVDA reads
as "dash dash"); "no radio connected" instead of frozen stale numbers; reset on
rig swap. **F6 now works on that tab** — it previously always reported "nothing
to adjust here" because nothing was focusable.

---

## Track D2 — voice engine and meter model · `bsr/track-d2` · 6 commits · Fable

`MeterVoice.cs` and `MeterModel.cs`, plus the contract doc
`docs/planning/active/meter-voice-model.md` for D3 and H. **A voice is a named
serialisable parameter set — no enum, no switch anywhere.** 15 built-ins ship as
data; user voices persist separately and can never shadow a built-in name.

`MeterDefinition` = source + range + voice, with **four source kinds**
(radio-reported, PC-derived, frequency-domain probe, derived stage-delta with a
`SecondaryKey` — "NB effectiveness" is expressible today). Live tweaks clone into
`VoiceOverride`; keep-as-copy / replace / discard on exit.

`VoicedToneSampleProvider` does additive synthesis with per-partial phases, RBJ
bandpass noise, frequency glide and **live** equal-power pan. `RenderMono` is H's
one-shot earcon path.

**The empirical answer:** it built `tools/voicelab` to render the real synthesis
through an emulation of the engine loop — 15 solos plus trio/quartet/quintet
ensembles, dry and under TTS speech, panned centre so the test holds in mono.
The objective screen caught **Reedy and Thin as near-twins**, fixed as data.
**Its opinion: identification survives five, tracking does not** — three to four
attended streams is a listener limit, not a synthesis failure. Recommends a large
alphabet with a **concurrency budget of about three audible meters**, event-shaped
voices on alarm-like sources.

**Reported:** the plan's claim that `AddSlot()` has no callers is **false** —
`MetersPanel.xaml.cs` has a full slot UI calling it, and D3 must absorb or
replace it. **Bug fixed in passing: pan changes never took effect** — pan was
baked into a `PanningSampleProvider` at mixer registration and nothing
re-registered. `MeterSlotConfig`/`MeterSlots` **was written by nothing, ever**.

---

## Track E — device and rate policy · `bsr/track-e` · 7 commits · Opus

- **Host-API selector, and the folding rule is gone.** "Audio system" combo,
  WASAPI default, persisted. `BuildPickerList` split into an identity index and a
  view filter. **One selector governs both directions** (DAW convention); the
  advanced toggle drops the filter and names the API per row, which is where
  input and output can differ. The filter applies to the **picker only** —
  `InputDevices`/`OutputDevices` stay complete so a device saved under another API
  keeps resolving. **`AdoptSystemDefault` now prefers the selected API**, because
  PortAudio nominates the MME endpoint and the old fallback would have quietly
  undone a deliberate WASAPI choice.
- **`Audio.Open` logs the host API**, read live rather than from the saved record.
- **Mono capture works.** Streams open at native channel count; the input
  callback duplicates, the output callback mixes down. `framesPerBuffer`
  unchanged.
- **The two refusal messages resolved by deleting one** — mono is now a statement
  of what happens to the audio, not a rejection.
- **Selectable Opus TX rate** 48/24/16/12/8 kHz, frame duration stays 10 ms so
  the radio still gets 100 frames a second.

**Re-measured, and the queue's assumption is wrong.** Neither #17 nor #29 could
have been moved by the rate fix. **#29 runs on a different stack entirely** —
NAudio `WaveOutEvent`, which is MME, not PortAudio. Two mechanisms found: the
fade-out is **buffer-length dependent and takes 50 ms** at the current latency,
while `StopTxToneMonitor` waits `Task.Delay(50)` — a race against a ~15 ms timer;
losing it cuts the tone mid-fade at roughly half amplitude, ≈ −15 dBFS broadband.
Secondary: the tone stack generates at 44100 into 48 kHz endpoints. **#17's
instrument was lying** — `WriteOpus` computed output as `raw × gain`, **silently
omitting `PostDecodeProcessor` where the neural and spectral NR run.** Now
measures the actual samples handed to PortAudio.

**Deliberately not done:** `paWinWasapiAutoConvert` — `PortAudioSharp` has no
`PaWasapiStreamInfo` binding and the pinned PortAudio source is not in the tree,
so the struct layout cannot be verified. **Needs `pa_win_wasapi.h` from commit
`a880212`.** And **#57's DAX IQ half cannot be done: there is no DAX IQ
implementation in the app at all.**

**Symbols another track could collide with:** `Devices.FindPickerRow` and
`SameDevice` are now endpoint identity; `UsableForRadioAudio` is now
`NativeChannels >= 1`; `BuildPickerList` no longer exists.

---

## Track F — presets and config truth · `bsr/track-f` · 5 commits · Fable

`Radios/MicrophoneProfile.cs` — **microphone-first.** A profile names the mic,
carries the capture half once (device identity, Windows input level, boost, and a
`NoiseGateSettings` slot **reserved for Track I at
`MicrophoneProfile.Capture.Gate`**), and holds per-radio bindings. Stage two is a
real discriminated type: `RadioProfileReference` (Flex — the *name* of a
radio-owned profile, nothing copied) or `RadioTxValues` (profile-less rigs).
Absent referenced profile → PC half applied, said plainly, no substitute, via
`FlexBase.SelectMicProfileIfPresent` **which never creates**. Creating on the
radio is offered in the save dialog only.

- **#49** — corrupt stores are **sidelined** (renamed `.unreadable-<timestamp>`,
  so no save destroys the evidence) and spoken at Critical, for audio presets,
  filter presets and mic profiles. **Bonus find: the filter preset editor was
  silently losing edits** — it edited a transient defaults list when a mode had
  no saved presets, and Delete/Move were dead.
- **#50** — `schemaVersion` on store and exports; TX EQ captured/applied via new
  wrappers, with `TxEqCaptured` guarding old files so a pre-EQ preset never zeroes
  a radio's EQ.
- **#51** — presets record `RadioMicInput` and announce a mismatch on load,
  never auto-switching.
- **#68** — real migration: canonical home is the config root, `Load` honours a
  newer or lone legacy copy for one release and heals forward. **Dated note in
  code: drop the legacy read after 4.1.17.**

**D2 coordination as executed:** no structural change to the config model's
shape; every new type in new files. **Reported:** the plan's "not one reference
outside FlexLib" was inexact — Jim's `Profile_t`/`SelectProfile` plumbing and the
Sprint 20 `ProfileReporter` already touch it. `EqualizerDialog.xaml.cs` is
**orphaned** — nothing constructs it.

---

## Track G — the honest About page · `bsr/track-g` · 3 commits · Fable

`Radios/DiagnosticSnapshot.cs` — one sectioned structure, each probe individually
guarded, consumed by **three** surfaces: the About page's System tab, the crash
reporter, and the debug bundle's new `system-info.txt`. Existing crash-report
labels preserved verbatim for triage continuity.

Runtime queries proven against the real shipped DLLs: Opus answers
`libopus 1.6.1`; PortAudio answers `PortAudio V19.7.0-devel, revision a880212`
and is displayed **revision-first**, with a bare 19.7.0 never appearing and an
unstamped build reporting itself as unidentifiable. Also live: .NET 10.0.11,
WebView2, OS/arch, self-containment (checked by where `System.Private.CoreLib`
actually loaded from), executable path, live trace path, screen reader + braille.

**Found and fixed: `FlexLib.dll` claimed version 0.0.0.0** — the vendored
csproj's `<Version>` sat at the placeholder since the SDK conversion, so **no
runtime query could ever have been honest.** Stamped `4.2.20.41343` with a
comment, and added to **`MIGRATION.md` as reapply item 8**. *Merge note: this
touches `FlexLib_API/FlexLib/FlexLib.csproj`.*

**Copy is now Copy Everything** — its accessible name always said "all
information" while the code copied one tab. **Escape now closes from inside the
WebView2 document.** A selectable-TextBox fallback shows the same facts when
WebView2 is missing — **that path previously showed nothing at all.**

**Reported:** `POpusCodec.Wrapper.opus_get_version()` is internal, unused and
buggy (marshals ANSI as UTF-16); three dead About surfaces with zero callers
(`About.vb`, `AboutProgram.vb`, `AboutProgramDialog`); the Escape-script pattern
now duplicated in two dialogs.

---

## Track I — transmit conditioning · `bsr/track-i` · 7 commits · Fable

**The early answer: `NoiseReductionProvider` is fully reusable on transmit.** Not
welded to `RxAudioPipeline` — it has a standalone constructor and a plain
float-buffer `ProcessInPlace`, added for exactly this. This sized the track small.

1. **The hook** — `TxAudioProcessorCallback` in `Audio.cs`'s input callback: mic →
   tone injection → **processor** → LUFS meter → Opus. FlexBase owns a persistent
   `TxAudioConditioner`.
2. **The gate** (`JJPortaudio\TxNoiseGate.cs`) — attack 3 ms, hold 150 ms, release
   200 ms, range 25 dB **clamped at 40, so it cannot gate to silence by
   construction**. **Resets OPEN on key-down so it can never eat a first
   syllable.** Threshold derived from `LufsMeter.Profile.NoiseFloorLufs` + 8 dB,
   refreshed twice a second; until the meter has ~3 s of transmitted speech it
   sits inert at −60 dB — **unmeasured means does-nothing, never eats-speech.**
3. **The residual monitor** — `removed = input − output`, played on the PC
   speakers via its own device so it cannot perturb the radio engine. Modes: Off /
   what goes out / what was removed / **Split (output left, residual right)** —
   "both" is a split because summing them reconstructs the input exactly.
   **Bypassed produces exact digital silence in the residual**, harness-proven.
4. **NR on transmit**, with live strength read per-sample.
5. **UI** — "PC Cleanup" in the Workshop between Microphone and Processing
   (capture → clean → sculpt). Basic: two toggles plus strength. Advanced gate
   fields appear when the gate is on, each label explaining its default. Status
   line says where the threshold came from. Reset-to-recommended button.

**Harness `tools\TxGateHarness`, 18 checks, 0 failed:** onset after closure keeps
**79% of its first 20 ms** (a slow gate passes 0.3%); a 250 ms pause never moves
the gain; long silence closes to **−23 dB**, inside the window and provably not
silence.

**Deviation flagged:** the processor is **skipped while the test tone is
engaged** — the tone is a calibrated reference and RNNoise would eat a steady sine
and silently break it. **Track F:** `TxConditioningSettings` is the ready-made
payload for the microphone profile; **no store of its own was created.**
Auto-bypasses in CW/digital; NR honestly bypasses if the device is not 48 kHz.

---

## Small-fixes sweep · `bsr/track-sweep` · 12 commits · Fable

- **Both May audits DISCHARGED, not obsolete.** Three months of "fixes deferred"
  meant **fixed sideways and never checked off**. All eight missing key bindings
  are documented and verified still bound; the Ctrl+Shift+F finding was overtaken
  by the 2026-08-07 reassignment. The JJ+H audit's options 1 and 2 shipped in the
  August keys track — `KeyInventory.LeaderHelpSpeech()` **cites the audit by name
  in a comment**. Both files now carry a "Resolution — 2026-08-16" section and say
  "safe to archive". **New fact recorded: F1 today opens the help book, not
  per-control pages** — the ContextMap is only consulted with an explicit key and
  the global handler passes none.
- **`testtone.armed` is NOT reproducible from this tree**, stated with evidence:
  the literal exists in no branch tip and no commit in history. The wording half
  was real and is fixed — disarm said "Test tone off" against arm's "armed"; both
  now say "armed"/"disarmed".
- **#71 fixed** — one stem everywhere. Its second finding routed to Track C.
- **#18** — the design already exists and is ratified; interim fix only, the
  frozen "Start or stop tracing" accessible name now follows state.
- **#26 — all four answered.** Headline: **`Ctrl+J, D` was never available** — it
  has been the tuning-debounce toggle since before the design was written. The
  capture chord becomes `Ctrl+J, Ctrl+D`.
- **#32 — verified by building a real x64 installer.** 371 files, 54.2 MB
  compressed, deleteList complete. **Found: `JJLogIO.xml` and `JJTrace.xml` have
  shipped in every installer since the .NET 10 migration** — the cleanup line
  pointed at a path dead for months.
- **#40** — Command Finder gained its own F1 page. The "What's &New" ampersand is
  **intentional**: native Win32 menu, `&` is the mnemonic and does not reach
  speech. **CLAUDE.md needs a carve-out.**
- **Seven docs archived**, the four excluded sets left in place.

---

## Cross-track items for the merge

- **`FlexLib_API/FlexLib/FlexLib.csproj`** — touched by G only.
- **`AudioOutputConfig.cs`** — D2 (additive) and F (persistence internals only).
  Both report no structural collision.
- **`AudioWorkshopDialog.xaml.cs`** — D1 (Live Meters region), F (TX/preset
  regions and `SetRig`), I (new PC Cleanup section). Different regions.
- **`FlexBase.cs`** — A (client identity), B (meters/power/GPS/loop), C (REM ON),
  F (profile wrappers), I (conditioner ownership).
- **Merge order:** D1 before D3 (not yet run). A last — largest diff.
