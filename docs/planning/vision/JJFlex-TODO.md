# JJ Flex — TODO / Rolling Backlog

Last updated: 2026-04-17

## Open Bugs

### BUG-061: CW word/prosign spacing timing not standard (2026-04-17 testing)
- **Symptom:** "73 SK" and other multi-element CW output runs together — inter-word and prosign-boundary spacing feels tighter than standard Morse timing. Noel's ear against W1AW practice streams and electronic keyers flagged it. Not fatal for notification-level CW (BT/SK/mode names still readable) but noticeable.
- **Standard:** PARIS word timing — 50 dit-durations total including 7-unit inter-word gap. Inter-character gap is 3 units. Our generator may be using shorter gaps.
- **Leading theory (Noel 2026-04-17):** the running-together effect may be because `PlayCwSK` calls `PlayString("73")` + `PlaySK()` back-to-back — two separate rendering passes through the FIFO queue. Each pass has its own envelope and inter-element timing context; the gap between them is the queue/buffer gap, not a true 7-unit word space. Fix may be to build a single rendering pass that takes a list of elements (plain chars + prosigns) and emits one continuous waveform with PARIS-standard gaps throughout. Candidate API: `_morseNotifier.PlaySequence(List<CwElement>)` or extend `PlayString` to understand prosign syntax (e.g. `"73 <SK>"` where `<SK>` renders as the joined prosign).
- **Why it matters now vs later:** For notification-level CW this is cosmetic. For the future CW practice mode (virtual keyer + decoder), incorrect timing would teach operators bad habits and fail real-decoder testing. Whatever fix we land must hit PARIS-compliant timing precisely.
- **Related:** BUG-055 (CW prosign envelope + timing quality) — partially addressed in Sprint 25 with CwToneSampleProvider rewrite. Timing math may need a second pass. Also see FEATURE below for dedicated CW processor.
- **Scope:** Audit `MorseNotifier` + `EarconCwOutput` + `CwToneSampleProvider` — element durations, inter-element space, inter-character space, inter-word space, prosign no-gap semantics. Compare to PARIS standard. Instrument with a test harness that feeds sample patterns and verifies gap lengths.
- **Priority:** Medium. Address as a dedicated CW-quality pass before CW practice mode ships.
- **Status:** Logged.

### FEATURE: Dedicated CW processor/engine (2026-04-17 design direction, Noel)
- **Context:** As JJFlex grows into CW practice mode + on-air CW keying + iambic/bug/straight-key support, the CW rendering logic is becoming a first-class subsystem, not a notification helper. Currently spread across `MorseNotifier`, `EarconCwOutput`, `CwToneSampleProvider` with notification-level assumptions baked in.
- **Scope for the engine:**
  - Timing standards: PARIS (default) and the alternative word-length standards (CODEX, etc.) — configurable.
  - Speed: adjustable WPM with NO upper clamp (current: 30 WPM max). CW expert operators and contest regulars routinely run 35-45+ WPM; the engine should support whatever's plausibly decodable.
  - Farnsworth timing: slow char rate with normal inter-char spacing for learners.
  - Single-utterance rendering: accept a sequence of elements (chars + prosigns + explicit word gaps) and emit one continuous waveform with precise PARIS-spec gaps throughout. No back-to-back-utterance artifact.
  - Prosign syntax in string input: bracket notation (`<SK>`, `<BT>`, `<AR>`) that the engine resolves to joined prosigns with no inter-character gap.
  - Envelope shaping: proper attack/release for click-free signals (already partially addressed in BUG-055 fix).
  - Weight / rhythm variance controls for future sending-grade work (CW practice tutor mode).
  - Separate from output: engine produces elements/waveforms, output layer routes to earcon channel (notification) or TX pipeline (on-air) or practice sidetone. Same engine, different destinations.
- **Why this vs piecemeal fixes:** BUG-055, BUG-061, and the eventual CW practice mode all point at the same underlying engine. Doing the engine properly once makes all three land naturally. A piecemeal fix just to tighten "73 SK" spacing wouldn't unlock the future work.
- **Priority:** Medium-High. Foundation for CW practice mode + on-air CW + any future CW feature. Not urgent today but high leverage when scheduled.
- **Status:** Logged. Likely a dedicated sprint post-foundation phase, paired with CW practice mode planning.

### FEATURE: Hide CW message management UI outside CW mode (2026-04-17 design direction, Noel)
- **Context:** Jim-era code carries CW message add/edit/send UI — `CWMessageAddDialog.xaml`, `CWMessageUpdateDialog.xaml`, plus the VB files `CWMessageAdd.vb`, `CWMessageUpdate.vb`, `CWMessages.vb`. These let operators compose and store memorized CW messages for on-air transmission (CQ / call-contact / contest-exchange templates).
- **Noel's design:** these fields are CW-only by nature. When the active slice is in SSB / FM / digital mode, exposing them is clutter for a feature that doesn't apply. Only surface the management UI when the active mode is CW / CWL / CWU. Hide menu entries, access shortcuts, and dialog triggers otherwise.
- **Investigation needed:** confirm no non-CW code path references these dialogs. Grep for call sites, verify they're all mode-gated or gateable. If any are unconditional, either gate them or remove unused code paths.
- **Implementation:** mode-aware visibility on menu items / toolbar entries / hotkeys referencing CW message management. Listen to DemodMode changes and update visibility. Default: hidden; enable when mode is CW variant.
- **Priority:** Low-Medium. Cleanup, not functional. But reduces screen-reader tab-order noise for non-CW operators (most users) who never touch these fields.
- **Status:** Logged.

### FEATURE: Ctrl+Tab action palette expansion (2026-04-17 testing insight)
- **Context:** Sprint 25 Phase 9 added a Ctrl+Tab "Actions" dialog (command palette) at `MainWindow.xaml.cs:2280`. Current items are context-gated: ATU Tune (if hasATU), Start/Stop Tune Carrier (if canTx), Start/Stop Transmit, Speak Status, Cancel. Labels flip based on state ("Start" vs "Stop") which doubles as status readout.
- **Architecture:** `ExecuteActionToolbarItem` is a switch-on-string at `:2317`. Adding a new action is one `list.Items.Add()` + one case in the switch. Context-gating pattern (`hasATU`, `canTx`) is already established for conditional items.
- **Direction:** grow this into JJFlex's primary *command discovery* surface — keyboard-native, state-aware, discoverable via a single hotkey. Obvious candidates for additional entries:
  - DSP: Toggle Legacy NR, RNN, NRS, NRF (each with license/feature gating), Toggle NB, Toggle ANF
  - Audio: Toggle meter tones, Toggle earcon mute, Open Audio Workshop
  - Tuning: Jump to band (sub-palette or quick-pick), Toggle tune debounce
  - Radio: Disconnect, Switch slice, Open RadioInfo / Feature Availability
  - Modes: Cycle mode forward/back, Jump to CW/USB/LSB/digital
  - Braille: Toggle status line, Cycle cell-count profile
  - Settings shortcut, Speak full status (vs current compact Speak Status)
- **UX enhancements to consider** (not required for basic expansion):
  - Recent/favorited items at top
  - Type-to-filter text search (like VS Code Ctrl+Shift+P) for discoverability at scale
  - Grouped sections with spoken group headers for screen-reader navigation
  - Visual polish: icons, grouped separators — accessibility-first, visual second
- **Priority:** Medium. The base is working; growth is pure additive feature work. Good candidate for a dedicated "action palette expansion" mini-sprint after Foundation phase completes. Could also be incremental — add items as adjacent features land.
- **Status:** Logged during Sprint 25 Phase 9 testing.

### BUG-059: Earcon audibility under loud radio audio (2026-04-17 testing, reported by Don + Noel agrees)
- **Symptom:** Earcons (alert tones, toggle feedback) are sometimes unhearable when radio audio is loud. AlertVolume at 100 is not enough headroom to cut through a loud signal on the rig audio channel. Users can miss alerts they should hear.
- **Why simple volume boost isn't enough:** at AlertVolume = 100 we're already at software unity gain. Going higher either distorts or requires raising the digital amplification ceiling. Even max earcon volume can't beat radio audio that's actively loud.
- **Design — three tiers:**
  1. **Tier 1 (cheap, near-term):** raise the AlertVolume ValueFieldControl ceiling above 100 (e.g. to 200) for software amplification. Document distortion tradeoff in the UI label or a tooltip. Users who need raw dB can push past unity.
  2. **Tier 2 (proper fix, sprint-sized):** audio ducking. When an earcon fires, momentarily drop radio audio output by a configurable dB (default ~12) for the ~150-300 ms the earcon plays, then restore. This is what every GPS nav system and car infotainment does for the same reason. Needs an earcon start/stop event hook into the radio-audio channel's gain.
  3. **Tier 3 (orthogonal):** better discoverability of the already-existing `EarconDeviceNumber` separate-device routing. Users can already route earcons to PC speakers while radio audio goes to a different output — but most don't know. Add a "Use separate device for alerts" helper toggle in Settings Audio tab that surfaces this option prominently.
- **Priority:** Medium — real usability gap for the people who care about hearing alerts over loud audio. Tier 1 is a few lines; Tier 2 is a real design pass.
- **Status:** Logged during Sprint 25 Phase 3 testing; deferred to a post-foundation sprint.

### BUG-060: AudioOutputConfig mixes user-scope and per-radio fields (2026-04-17 testing)
- **Symptom (surface):** When no radio is connected, the Frequency Entry Sound dropdown in Settings → Audio tab shows only the three always-on modes — Mechanical and DTMF unlocks are hidden even though TuningHash is persisted at BaseConfigDir. Connecting a radio makes the unlocks reappear. Found during Sprint 25 Phase 7 testing; minimum-viable fix already applied (Settings now loads from BaseConfigDir when disconnected and always saves user-global fields to root on OK).
- **Root cause (architectural):** `AudioOutputConfig` conflates truly user-global preferences (TuningHash, TypingSound, SpeechVerbosity, EarconsEnabled, AlertVolume, MasterVolume, CwNotificationsEnabled, CwSidetoneHz, CwSpeedWpm, TuneDebounceEnabled, TuneDebounceMs, BrailleEnabled, BrailleCellCount, AnnounceSwrAfterTune) with truly per-radio settings (EarconDeviceNumber — device enumeration is machine-local but may legitimately differ between a headset-connected session vs a shack-speakers session on a different radio). One XML file serializes everything; both root and per-radio locations have copies; the load path depends on connection state.
- **Proper fix (later sprint):** split serialization into two files. `userPrefs.xml` at BaseConfigDir holds user-global fields (loaded regardless of connection). `audioConfig.xml` per-radio holds only truly per-radio fields. Migration on first load reads the old single-file format from both locations and splits it. No user-visible regression — just a cleaner separation.
- **Priority:** Medium — today's fix works for the Phase 7 case. But as the config grows, the scope question will keep reappearing (e.g. Sprint 26+ network settings, multi-radio session prefs). Splitting now prevents per-feature special-casing later.
- **Status:** Logged. Minimum fix in commit bundle for today's session. Full split deferred.

### BUG-054: Ctrl+F frequency entry doesn't announce license sub-band crossings (2026-04-15 testing)
- **Symptom:** Type a frequency via Ctrl+F that crosses into a different license class sub-band (e.g. into the extra-only portion of 20 m). Boundary announcement does not fire on Enter. The same crossing via arrow-key tuning DOES announce.
- **Hypothesis:** The boundary-check code compares old vs new frequency (delta-based). Arrow-tuning updates `_lastFreq` before the check; Ctrl+F's set-frequency path likely bypasses or runs after `_lastFreq` has been updated to the new value, making the delta zero.
- **Fix sketch:** centralize the boundary check in a set-frequency helper that both tuning paths call, or invoke the check explicitly from the Ctrl+F Enter handler before `_lastFreq` rolls forward.
- **Priority:** Low — license-safety redundancy (arrow keys still announce), but noisy gap in feedback
- **Status:** Logged — caught during Sprint 25 testing

### BUG-055: CW prosign envelope and timing quality (2026-04-15 testing)
- **Symptom:** Dits sound weak/wrong, dahs don't sound long enough. Root cause was `FadeInOutSampleProvider` misuse that turned every tone into a ~90%-fading envelope. Tones should be click-free sine with proper attack/release.
- **Status:** FIXED in the commit bundle that lands this TODO update. New engine (CwToneSampleProvider + element-batch ICwNotificationOutput + rewritten MorseNotifier) replaces the tone-by-tone + Task.Delay pattern. See `docs/planning/design/cw-keying-design.md`.

### BUG-057: CW prosign cancellation race — only SK fires reliably (2026-04-15 testing of 4.1.16.10)
- **Symptom:** In 4.1.16.10, the CW engine rewrite works (prosign tones sound clean), but on a real connect sequence the user only hears SK (app-close). BT (connected) and AS (slow connect) don't fire. Mode-change CW does work.
- **Hypothesis:** The new engine submits the whole element sequence to the mixer in one call; there's a ~50 ms buffer window before actual audio starts playing. If a subsequent prosign or PlayString fires during that window, its `Cancel()` disposes the CancellableCwProvider before any audio has reached output — the earlier prosign never becomes audible. SK is the only one that works because it fires at app-close with nothing firing after it. The old engine's Task.Delay-driven element-by-element approach didn't have a buffer window, so a second prosign cancelling the first before it played was rarer.
- **Fix options:**
  - (a) Queue prosigns instead of cancel-and-replace — multiple events in quick succession play in sequence (preferred UX).
  - (b) Add a short "minimum-audible" grace period before a sequence is cancellable.
  - (c) Scope `Cancel()` only to `PlayString` (long messages), not to prosign helpers (short, always run to completion).
- **Priority:** High — defeats the whole CW-notification feature for connection events even though the engine itself is fine.
- **Status:** Logged. Sample-accurate engine rewrite shipped 2026-04-15 (`02bc948f`) introduced this race; wasn't caught because single-prosign tests worked.

### BUG-056: CW "speech off" silences navigation along with status (2026-04-15 testing)
- **Symptom:** Setting speech verbosity to Off suppresses everything — including navigation feedback the user needs to operate the app. Separately, "Off" didn't actually silence the initial connect speech, so the dial is incoherent.
- **Priority:** Medium — accessibility regression for operators who want to rely on CW/braille for status while keeping navigation feedback.
- **Fix sketch:** Replace single-dial verbosity with categorized channels (Status / Navigation / Data readout / Hints), each with its own on/off. Three profiles + custom. See "Speech verbosity redesign" in Future Work below.
- **Status:** Logged — design sketched, implementation deferred to Sprint 26 or 27.

### BUG-002: Brief rig audio leak on remote connect (reported by Don, 2026-02-09)
- **Symptom:** Don hears 2-3 seconds of rig audio through local speakers when a remote user connects via SmartLink, before local audio goes silent.
- **Priority:** Low — cosmetic annoyance
- **Status:** Logged for future investigation

### BUG-003: Modern mode tab stops need design (reported by Don, 2026-02-09)
- **Description:** Tab in Modern mode has no intentional design for tab order. Needs purpose-built focus flow.
- **Priority:** Low — Modern mode still under construction
- **Status:** Logged for future sprint

### BUG-004: Crash on shutdown — System.IO.Ports v9.0.0 missing (2026-02-09)
- **Symptom:** App crashes on exit — `FileNotFoundException` for `System.IO.Ports v9.0.0`. Knob thread tries to dispose serial port even when no knob is connected.
- **Fix options:** (a) Downgrade System.IO.Ports to v8.x, (b) Add try/catch in FlexKnob.Dispose(), (c) Skip dispose if no knob was initialized
- **Priority:** Medium — crash dump on every shutdown
- **Status:** Pre-existing, logged for fix

### BUG-013: Duplicate QSO beep not audible (Sprint 6 testing, 2026-02-12)
- **Symptom:** Duplicate warning beep not heard when logging a duplicate QSO. Speech announcement works.
- **Priority:** Low
- **Status:** Logged for investigation

### BUG-016: RNN toggles on radios that don't support it (Sprint 7 testing, 2026-02-14)
- **Symptom:** Neural NR can be toggled on FLEX-6300 which doesn't support it. Needs feature gating.
- **Priority:** Medium
- **Status:** Open

### BUG-023: Connect to Radio while already connected has messy flow (Sprint 7 testing, 2026-02-14)
- **Symptom:** Connecting while already connected causes confusing disconnect/reconnect sequence with "session invalid" errors.
- **Fix:** Add confirmation dialog before disconnecting.
- **Priority:** Medium
- **Status:** Open

## Near-term (next 1–3 sprints)

### High priority — ship ASAP

- [ ] **Announce final SWR after tune** (Don's ask, 2026-04-15). Priority: **HIGH** — Don explicitly asked for this; small bounded scope.
  - After Ctrl+T manual tune releases OR ATU auto-tune completes, speak the settled SWR reading ("SWR 1.3 to 1").
  - Wait ~200 ms after tuner-off before reading so mid-sweep transients don't get announced.
  - Track last SWR via `FlexBase.SWRDataReady` event; detect tune-end via `FlexTunerOn: true→false` transition.
  - Gated by a Notifications-tab checkbox, defaults **on** (low noise — one line per tune operation).
  - In the future verbosity-category redesign, this is a **Status**-class announcement (survives the "speech off, status on" profile).

### Upcoming features
- [x] **Migrate to .NET 10 LTS**: Completed 2026-04-13. All 25 active projects updated from `net8.0-windows` to `net10.0-windows`. Breaking change in .NET 10: new WFO1000 WinForms analyzer requires explicit `DesignerSerializationVisibility` on runtime-only public properties on Form classes (fixed 11 occurrences). Both x64 and x86 Release builds clean, installers generated successfully. ComPortPTT (still on .NET Framework 4.0) not in sln, left alone for now.
- [ ] **TX bandwidth sculpting**: Adjust transmit filter edges from keyboard, mirroring RX filter bracket-key workflow
- [ ] **Editable filter & step presets**: Create, edit, save, load, and share filter/step presets as XML
- [ ] **Compiled help file**: CHM workflow, build integration, context-sensitive F1

### Sprint 8 — Form1 WPF Conversion
- [ ] Convert Form1 from WinForms to WPF Window (kills all interop issues)
- [ ] Eliminate ElementHost — all WPF controls native in WPF Window

### Sprint 9 — Remaining Forms WPF Conversion
- [ ] WPF migration: Command Finder + DefineCommands + PersonalInfo + LogCharacteristics + remaining dialogs

### Sprint 10
- [ ] Slice Menu + Filter Menu (slice-centric operating model)
- [ ] QSO Grid: full filtering + paging

### Sprint 11
- [ ] QRZ per-QSO upload, confirmation status, data import

### Sprint 12
- [ ] Modern menus → WPF Menu control
- [ ] StationLookup full redesign

### Backlog — Open Items
- [ ] "Update Current Operator" menu item — opens PersonalInfo directly for current operator
- [ ] Rename "Operators" to "Edit or Change Operators" in Modern and Logging menus
- [ ] GPS grid from FlexLib — query radio GPS for operator grid square
- [ ] Include slice number in mute/unmute speech ("Muted slice A")
- [ ] Filter presets / filter presets by mode
- [ ] DSP current state display — show on/off state when arrowing through DSP menu items
- [ ] Hotkeys for Modern menu DSP/filter items
- [ ] BUG-015: Fix F6 double-announce "Radio pane" in Logging Mode
- [ ] Focus-on-launch: App doesn't grab focus when launched from Explorer
- [ ] WPF DataGrid cell announcements: NVDA double-reads cell values (fixable with custom AutomationPeer)

## Slice UI & Status
- [ ] Define ActiveSlice rules and announce/earcon behaviors
- [ ] Implement Slice List (next/prev, select)
- [ ] Implement Slice Menu entry point
- [ ] Status Dialog rebuild (BUG-020) — proper accessible WPF dialog with live-updating view

## Keyboard & Discoverability
- [ ] Contextual help: group help via H/?
- [ ] Optional leader key design (Ctrl+J) + 2-step max

## Audio
- [ ] Audio menu / Adjust Rig Audio workflow (TX/RX levels, ALC guidance)
- [ ] **Peak watcher (background audio monitor)**: Runs silently in the background anywhere in JJFlexRadio. Monitors audio levels and alerts via earcon tones or screen reader speech when your audio is too hot, compressor is slamming, or ALC is peaking — no visual UI, just audio/speech feedback. Toggle on/off from Settings or hotkey. Configurable thresholds and announcement frequency so it doesn't nag you during a QSO but catches problems before someone on frequency tells you your audio sounds like a garbage disposal.
- [ ] **Audio test dialog**: The workshop for sculpting your transmit audio. Test and adjust mic gain, compressor, compander, EQ, TX filter width — all with real-time spoken feedback and peak monitoring. Standalone mode (no TX required) so you can dial everything in before you go on the air. This is where operators spend quality time getting their signal right. Needs to expose every audio parameter the Flex offers (and eventually the DSP abstraction layer for other radios).
- [ ] **Off-air self-monitoring**: Use a second receiver (MultiFlex second slice, or a cheap SDR like RTL-SDR) to receive your own transmitted signal off a nearby antenna and analyze it in real-time. Hear exactly what you sound like over the air — not what your mic sounds like, what your *signal* sounds like. On a dual-SCU Flex (6600/6700), TX on Slice A at low power, receive on Slice B via a separate antenna. With future cheap receiver support, anyone could do this with an RTL-SDR dongle and a piece of wire. Feed the received audio into the peak watcher for objective analysis.
- [ ] **Audio chain presets (save/load/share)**: Save the entire TX/RX audio configuration as a shareable preset file — mic gain, compression, EQ, TX filter width, RX volume, AGC settings. Import/export so operators can share “my ragchew voice” or “contest audio” profiles with friends. Same XML-based approach as filter presets.
- [ ] Recording/playback and “parrot” concept (design + feasibility)
- [ ] DAX integration: superseded by JJ Audio interfaces (see SmartLink Independence section)

## CW Audio Engine (Sprint 25 landed; extensions planned)

See `docs/planning/design/cw-keying-design.md` for full architecture.

- [x] **Prosign engine fix** (landed 2026-04-15): replaced tone-by-tone + Task.Delay with element-batch `ICwNotificationOutput` and raised-cosine-envelope `CwToneSampleProvider`. Sample-accurate PARIS timing, click-free sine waves. AS / BT / SK + arbitrary strings.
- [ ] **Farnsworth timing for code-practice use**: element speed at character-speed WPM, gaps stretched to overall-speed WPM. Drops in via an overridable `InterCharMs` / `InterWordMs` on MorseNotifier.
- [ ] **Weighting** (adjustable dit/dah ratio, 2.8:1 to 3.3:1). Operator-preference feature for learning mode.
- [ ] **Code-practice / learning mode**: same engine, no radio involvement. Random letter groups, common-word groups, imported text. Valuable accessible learning tool since there's very little software in this space for blind hams.
- [ ] **On-air CW via PC synthesis** (future sprint, 28+): PC generates audio, streams to radio TX via DAX in SSB mode (CW mode doesn't accept audio — confirmed). Operator hears sidetone from PC in real time; receiver hears it with SmartLink+internet latency.
- [ ] **Iambic keyer** (future, requires on-air path): Mode A and Mode B, pluggable via `IIambicKeyer`. Input adapters for keyboard, gamepad thumbsticks, touchscreen.
- [ ] **CW keying bandwidth configurable**: riseFallMs exposed in Settings. Default 5 ms (ARRL recommendation for ≤30 WPM); power users can tighten for crispness or loosen for cleaner on-air spectrum.

## Session Latency Service (designed; implements in Sprint 26 or 27)

See `docs/planning/design/session-latency.md`.

- [ ] **Per-session RTT and jitter probe**, median + MAD over rolling 20-sample window. Piggybacks on keepalive traffic where possible; falls back to a cheap dedicated probe over the TLS control channel.
- [ ] **Exposed on `IWanSessionOwner.Latency`** (fits the Sprint 26 Phase 1 refactor).
- [ ] **CW keyer consumes** to extend PTT-hold / VOX-tail appropriately.
- [ ] **Multi-radio mixer consumes** (Sprint 28+) to align per-stream PlayoutDelay so concurrent radios are time-coherent at the operator's ears.
- [ ] **Session health watches** RTT spikes and jitter bursts to fire preemptive reconnect and "connection shaky" status.
- [ ] **Status UI displays** live RTT + jitter per session ("round trip 52 ms, jitter 3 ms — connection stable").
- [ ] **Auto-quality tuning** uses initial probe to pick low-bandwidth mode on slow connections.

## Speech Verbosity Redesign (BUG-056 fix path)

Replace the single VerbosityLevel dial with categorized channels:

- [ ] **Categories**: Status (connect/error), Navigation (focus/field feedback), Data readout (freq/mode/meters), Hints/chatty (tooltips/help).
- [ ] **Three predefined profiles** plus Custom:
  - **Full speech** (default) — all categories on, Chatty.
  - **Status and navigation** (CW-assisted mode) — status + navigation on; data readout on but Terse; hints off.
  - **Status only** (true minimal — requires braille for detail) — status on, everything else off.
- [ ] **Per-call categorization**: every `ScreenReaderOutput.Speak` site tagged with `SpeechCategory`. Large touch surface but bounded.
- [ ] **Integrates with CW notifications**: "Speech off + CW on" becomes a coherent operating profile — fluent CW operators hear Morse for events, silence otherwise.

## PC-side Noise Reduction UX

- [ ] **Bundled noise profiles**: ship 2–4 canned Spectral NR profiles (typical 40 m atmospheric noise, urban mains hum, OTH radar sweep) so users can evaluate Spectral NR without waiting for capture-UI work.
- [ ] **Noise profile capture UI**: button in DSP section / Settings to capture the current band's noise floor into a profile that Spectral NR can subtract. Per memory `project_noise_profile_sharing.md`, eventually shareable with metadata (band, antenna, location).
- [ ] **PC NR menu entries**: today PC Neural NR and PC Spectral NR only appear as ScreenFieldsPanel checkboxes and hotkeys. Add menu items under DSP → Noise Reduction so the feature is discoverable via menu, not only via hotkey memorization.
- [ ] **PC NR strength / mode selection**: `RnnEnabled` is binary; `Strength` and `AutoDisableNonVoice` are not exposed. Either as a sub-menu (choose strength levels) or as ValueFieldControls in ScreenFieldsPanel.

## Operator Profile & Band Plans (future sprint)
- [ ] QRZ self-lookup: if operator has a QRZ subscription and is logged in, auto-populate operator data (name, QTH, grid, etc.) from QRZ. Trigger from settings or on first callbook login.
- [ ] License class selection: add license class field to operator profile. Provide a dropdown of license classes based on operator's country (US: Technician/General/Amateur Extra; UK: Foundation/Intermediate/Full; etc.). For countries without a built-in list, allow free-text entry.
- [ ] Band plan enforcement/guidance: look up band plans by ITU region + license class. Show operator which bands/modes they're authorized for. Could highlight out-of-privilege operation. Research: ARRL band plan data, IARU region plans.
- [ ] Internationalization of license classes for popular countries (US, UK, Canada, Germany, Japan, Australia, etc.)

## Ecosystem
- [ ] 4O3A device integration plan (SDK/protocol + device matrix)
- [ ] Tuner Genius support planning

## QSO Grid — Full-Featured Log Viewer (Sprint 8)

Rename "Recent QSOs grid" → "QSO Grid". Default view: filter to recent (preserves current behavior).

- [ ] Filter bar: band, mode, country, date range, callsign, free-text search
- [ ] Apply filter → paged results with accessible announcement: "Loaded N more, showing X of Y"
- [ ] Down arrow at bottom of grid → load next page (10-20 rows)
- [ ] Row-0→1 indexing (WPF DataGrid gives this for free)
- [ ] Configurable default page size (from Sprint 7 Track E setting)
- [ ] Screen reader: announce filter results count, page loads, selected row details
- [ ] Clear filter → return to "recent" default view

## QRZ Deep Integration (Sprint 9)

Per-QSO upload with operator control, plus data import and confirmation features.

### Per-QSO Upload Checkbox
- Add "Upload to QRZ" checkbox as **last field in LogPanel tab order** (after Comments)
- Hotkey: **Alt+Q** jumps directly to checkbox (quick toggle for ragchew QSOs)
- Default state driven by operator settings: "Upload QSOs to QRZ by default" checkbox in PersonalInfo
- If settings checked → checkbox pre-checked on every new QSO
- Operator can uncheck per-QSO (e.g., another QSO with Don — he'll never confirm anyway)
- On Ctrl+W (save): if checked → save locally AND push to QRZ API; if unchecked → save locally only
- Screen reader: "Upload to QRZ, checked" / "Upload to QRZ, unchecked"
- Tab order: Call(Alt+C) → RST Sent → RST Rcvd → Name → QTH → State → Grid → Comments → Upload to QRZ(Alt+Q) → Ctrl+W saves

### QRZ Confirmation
- Check confirmation status from QSO Grid (hotkey on selected QSO)
- Request confirmation if QRZ API supports it
- Bulk confirmation status check (filter unconfirmed QSOs)

### Data Import
- Bulk upload existing log to QRZ Logbook
- Import QSOs from QRZ into local log (sync)
- Settings-driven: enable/disable auto-upload globally

## Multi-Slice Decode & Auto Snap (future — multiple sprints)

Full design docs in `docs/planning/design/multislice-decode/`. Key features:
- Per-slice CW + digital decoding with structured DecodeEvent pipeline
- DecodeAggregator: unified stacked feed (CW / Digital / All tabs)
- Auto Slice Snap policy engine: Trigger → Eligibility → ActionSet → Confirmation
- Five snap levels (0: highlight → 4: contest mode) — never auto-TX
- Keyboard-first navigation: Up/Down signals, Enter to snap, F6 toggle panes, J/Shift+J jump CQs
- Accessibility-first: Tolk for confirmations, UIA live regions for passive, speech modes (off/active/background/events-only)
- Safety: no auto-TX, no focus change during TX, pin override, undo last snap

## Logging Mode Customization (Sprint 9+)

### Customizable Split Logger Field Layout
JJ's vision: logging should be **blazing fast** with keyboard shortcuts. The split logger (Logging Mode) is the speed machine — minimal fields, every one reachable by hotkey. JJ's big logger (Ctrl+Alt+L) has everything.

**Design:**
- Operator selects which fields appear in the split logger from a curated list
- **No "select all"** — the split logger must stay minimal and fast. Cap the max field count
- Each field in JJ's big logger already has a default hotkey (Alt+letter). When a field is pulled into the split logger, **its hotkey comes with it** — inherited, not reassigned
- Hotkeys are Logging-scope (already supported by Sprint 6 KeyScope system)
- Tab order follows selected fields in display order
- Operators can Tab sequentially OR jump directly with hotkeys — whichever is faster

**Speed workflow example:**
Alt+C → W1ABC → Alt+N → Bob → Alt+Q → Space (uncheck QRZ) → Ctrl+W → logged in 5 seconds

**Field picker UI:**
- Accessible list with add/remove/reorder (screen-reader-friendly)
- Shows field name + its hotkey for each available field
- Preview of tab order after changes
- Reset to defaults button

**Implementation notes:**
- Research JJ's existing field hotkey assignments in the big logger (LogEntry form)
- Map each LogEntry field to a Logging-scope hotkey
- Split logger dynamically builds its UI from the operator's field selection
- Store field selection in PersonalData (per-operator)

### Contest Mode
- [ ] Contest designer / contest mode: review JJ's existing contest C# files in JJLogLib (FieldDay.cs, NASprint.cs, SKCCWESLog.cs), assess whether they work and are useful. Consider building a contest designer that lets operators define required exchange fields and contest rules. May be complex — research scope before committing. At minimum, validate existing contest exchange definitions work with LogPanel.

## LogPanel Graduation & Logging Enhancements
- [ ] Graduate LogPanel to full-featured: add record navigation (PageUp/Down browse)
- [ ] Graduate LogPanel: edit existing records (load, modify, update)
- [ ] Graduate LogPanel: search log (find QSOs by criteria)
- [ ] Graduate LogPanel: load more records in Recent QSOs grid (batch/paging)
- [ ] Modern Mode gets embedded LogPanel (split view like Logging Mode)
- [ ] Classic Mode keeps full-screen LogEntry form (JJ's original design)
- [ ] Settings tab for managing Logging Mode preferences (suppress confirmations, etc.)
- [ ] Screen reader verbosity levels (concise vs. verbose announcements for pileup vs. ragchew)
- [ ] Contest-specific exchange validation (Field Day, NA Sprint, etc. — each has different required fields)

## WPF Migration (incremental, ongoing)

**Goal:** Replace WinForms controls with WPF for better native UI Automation, screen reader support, and modern UI capabilities. Migrate incrementally — 2 forms per sprint, new forms always WPF. WinForms shell (Form1) stays until most content is WPF.

**Why:** WinForms uses MSAA accessibility bridge which causes quirks (DataGridView row-0 indexing, menu items needing manual `AccessibleName`, etc.). WPF has native UIA support — controls expose automation properties automatically. JAWS/NVDA work more reliably with WPF.

**Rules going forward:**
- All NEW forms/dialogs → WPF from day one
- Existing forms → convert 2 per sprint alongside feature work
- WPF controls hosted in WinForms via `ElementHost` during transition
- Test each conversion with JAWS + NVDA before marking done

**Migration roadmap (REVISED — Form1 first to kill interop issues):**

| Priority | Form | Why | Sprint |
|----------|------|-----|--------|
| **1** | **Form1 shell → WPF Window** | **Kills all WPF-in-WinForms interop issues** | **8** |
| 2 | Command Finder | Already new, natural WPF fit | 9 |
| 3 | DefineCommands (hotkey settings) | Tabbed UI, benefits from WPF TabControl | 9 |
| 4 | PersonalInfo (settings) | Straightforward form conversion | 9 |
| 5 | LogCharacteristics | Small dialog | 9 |
| 6 | All remaining dialogs | Finish the job | 9 |
| 7 | Modern menus → WPF Menu control | Eliminates AccessibleName workarounds | 12 |

**Acceptance criteria per conversion:**
- [ ] WPF control/window created with proper AutomationProperties
- [ ] Hosted via ElementHost (or standalone WPF Window for dialogs)
- [ ] JAWS announces all controls, grids show 1-based rows
- [ ] NVDA announces all controls, same behavior
- [ ] Tab order logical, focus management correct across WinForms↔WPF boundary
- [ ] No regression in existing functionality

### Backlog — Mini Sprint 24a Remaining Items

**Meter presets:**
- [ ] User-saveable presets — save current slot config as a named preset (e.g., "Don's Tuner")
- [ ] Ability to create new presets from scratch, not just modify existing ones

**Filter help:**
- [ ] Update help text to explain filter edge grab mode (double-tap bracket, then both brackets move that edge)

**Connectivity / stability:**
- [ ] Connection error hang: SSL/SmartLink error makes app unresponsive, requires taskkill — observed twice in one session

### SmartLink Independence — Eliminate SmartSDR Dependency

**Goal:** JJFlexRadio becomes a fully standalone client — no SmartSDR required for any SmartLink operation.

- [ ] **Port forwarding settings (NEXT TO DEVELOP)**: Add TCP/UDP external port configuration to SmartLink settings. FlexLib has `Radio.WanSetForwardedPorts()` — we just need UI. Includes connection test results display (`WanTestConnectionResults`). Don needs this for his 6300.
- [ ] **SmartLink radio registration**: Register/unregister radios with SmartLink directly from JJFlex. FlexLib has `Radio.WanRegisterRadio(owner_token)` and `WanUnregisterRadio()` — uses our existing Auth0 JWT. Eliminates the last reason to open SmartSDR for setup.
- [ ] **JJ Audio interfaces**: Replace DAX dependency with direct FlexLib audio streams. FlexLib has DAXRXAudioStream, DAXTXAudioStream, RXRemoteAudioStream (with OPUS), TXRemoteAudioStream. Create JJFlex-managed audio routing for digital mode programs (fldigi, WSJT-X) — virtual audio devices or direct pipe.
- [ ] **JJ CAT ports**: Replace SDR-CAT dependency with FlexLib-native CAT control. FlexLib has full Slice API (freq, mode, filter) and UsbCatCable virtual COM port support. Create JJFlex-managed virtual serial ports for external programs. Potentially expose Hamlib-compatible interface.
- [ ] **Connection test UI**: Surface WanTestConnectionResults (UPnP TCP/UDP, forwarded TCP/UDP, hole punch capability) as a "Test SmartLink Connection" feature so operators can diagnose connectivity issues without SmartSDR.
- [ ] **Firmware update from JJFlex**: FlexLib has `Radio.SendUpdateFile(filename)` — uploads firmware via TCP port 4995, tracks progress. **Decompile revealed (2026-04-12):** SmartSDR bundles firmware in installer, stores at `C:\ProgramData\FlexRadio Systems\SmartSDR\Updates\`. Files named `FLEX-6x00_v{version}.ssdr` (6000 series) or `FLEX-9600_v{version}.ssdr` (8000/Aurora "BigBend" platform). No download API exists — files ship with the installer. **NOTE: Firmware updates are LAN-only (SmartSDR explicitly blocks over SmartLink).** Implementation phases: (1) Auto-detect firmware files from SmartSDR's ProgramData folder if installed, (2) Manual file browse as fallback, (3) Accessible progress dialog monitoring UpdateStatus/ConnectedState/UpdateFailed properties, (4) Host firmware files on web server (blindhams.network/Netlify initially, or dedicated domain like jjflexible.radio long-term). JJFlex builds filename from `radio.IsBigBend` + `radio.ReqVersion`, downloads only the file needed for that radio platform (~61 MB for 6000 series, ~372 MB for 8000/Aurora). Noel uploads new .ssdr files when Flex releases firmware. (5) Future: investigate Mac client or Flex download page for standalone firmware file source.
- [ ] **License refresh**: Refresh radio feature licenses (SmartLink subscription, Multi-Flex, etc.) directly from JJFlex without SmartSDR. FlexLib exposes `Radio.FeatureLicense` — research how license activation/refresh works on the server side. Eliminates another "go open SmartSDR" moment.
- [ ] **"Box to QSO" setup wizard**: Guided first-run experience — create SmartLink account, discover radio on LAN, register with SmartLink, update firmware if needed, configure network (UPnP or port forwarding), test connection, connect. Zero sighted assistance, zero SmartSDR. The pitch: from opening the Flex box to your first QSO, all in one accessible app.

### Backlog — Don's Feedback Items (2026-03-12)

**HIGH PRIORITY — Slice interaction:**
- [ ] Slice selector field in FreqOut: up/down arrows switch active slice, speaks short slice status on change (letter, freq, mode)
- [ ] Slice operations field in FreqOut: adjacent to slice selector, allows pan/volume/release operations on current slice
- [ ] Slice A/B switching unreliable for Don — needs trace session to diagnose (may be timing, scope, or MultiFlex ownership issue)
- [ ] Slice status speech: when switching slices, speak a concise summary (letter, freq, mode, muted/unmuted)

**HIGH PRIORITY — Status Dialog rebuild (BUG-020):**
- [ ] Rebuild Status Dialog as proper accessible WPF dialog — tab stops, close button, window ownership
- [ ] Display: radio model, connection type, all slice info, meter readings, active preset, tuning mode
- [ ] Should be a live-updating view, not just a snapshot

**HIGH PRIORITY — Key migration:**
- [ ] Move KeyCommands.vb to C# — key conflicts keep accumulating, VB dispatch is hard to audit
- [ ] Key conflict audit: systematic check for duplicate bindings across scopes (Sprint 24 scope)

**MEDIUM PRIORITY — Modern mode tuning UX:**
- [ ] Modern mode frequency: arrow keys move through frequency as a single unit (no char-by-char review), speaks frequency on change
- [ ] Classic vs Modern confusion: consider better onboarding, mode-switch announcement improvements, or unifying the modes

**MEDIUM PRIORITY — Auth Config Externalization (Sprint 25):**
- [ ] Move Auth0 endpoint, client ID, redirect URI out of hardcoded AuthFormWebView2.cs into a config file
- [ ] Allows rotation without rebuild if Flex changes their Auth0 settings (has happened during beta firmware)
- [ ] Sets the pattern for future auth providers (Icom, etc.)

**MEDIUM PRIORITY — Action Toolbar (Sprint 25):**
- [ ] WPF ToolBar with action buttons: Manual Tune, ATU Tune (ATU radios only), Transmit (PTT toggle)
- [ ] Toolbar navigation: Ctrl+Tab from frequency fields lands on toolbar (first item), Ctrl+Tab again moves to next pane. Arrow keys move between buttons within toolbar.
- [ ] Screen reader announces "toolbar, Manual Tune button, 1 of 3"
- [ ] Buttons duplicate existing hotkeys (Ctrl+Shift+T, Ctrl+T, Ctrl+Space) — second path for sighted users and discovery
- [ ] Consider JJ key (Ctrl+J then B?) as direct jump to toolbar
- [ ] Design note: F6/F8 are traditional toolbar jump keys but are taken for band navigation — Ctrl+Tab is the chosen alternative

### Backlog — Frequency Entry Audio Feedback (Sprint 25)

- [ ] Per-keystroke audio feedback during frequency entry (quick-type accumulator and JJ Ctrl+F) — multiple selectable sound modes
- [ ] Confirmation ding on frequency commit (DingTone from Sprint 24 Phase 8A)
- [ ] DTMF tone generator utility — reusable for future repeater control on non-Flex radios
- [ ] Unlockable bonus sound modes with discovery mechanics (details in private planning docs)
- [ ] Visual feedback sequences for sighted users alongside audio feedback
- [ ] Earcon rename: `ConfirmTone()`/`confirm.wav` → `ClunkTone()`/`clunk.wav` (do during Sprint 24 Phase 8A)
- [ ] Settings UI for sound mode selection in Audio tab

### Backlog — FM / Repeater Support

- [ ] PL/CTCSS tone decode — FFT on sub-audible 67-254 Hz range in RX audio, auto-detect and speak the tone. Blind operators can't see it on a spectrum display.
- [ ] PL/CTCSS tone encode for TX — needed for 10m and 6m FM repeaters. Check if FlexLib already exposes tone properties in slice settings.
- [ ] Pairs well with waterfall sprint — FFT infrastructure overlaps
- [ ] SmartSDR already handles repeater offsets but may not expose PL tone settings — investigate

### Backlog — Virtual Keyer / CW Practice Mode

- [ ] Built-in virtual keyer: straight key (spacebar), iambic paddles (shift keys), and bug simulator with realistic mechanical spring character
- [ ] Practice mode: local sidetone + CW decoder for real-time feedback, no radio required — practice anywhere
- [ ] Live TX mode: local and remote (SmartLink) — keyer logic runs locally, sends element timing to radio
- [ ] Real-time CW keying over remote — first-to-market on any radio platform. No other software allows paddle/bug feel over a network connection.
- [ ] Full break-in (QSK) and semi break-in with configurable hang time (TW-2000 style)
- [ ] No VOX required for CW — keyer state IS the PTT (improvement over Jim's design which required VOX)
- [ ] Semi break-in hides network latency for smooth remote operation
- [ ] Configurable WPM, weight, Farnsworth spacing for learners
- [ ] CW decoder reused from multi-slice decode feature

## Accessible Receive Path (Zero to Low Cost Entry)

The cheapest path into accessible ham radio — no transmitter required.

- [ ] **Web SDR integration**: Connect to KiwiSDR, WebSDR, OpenWebRX receivers with full accessible tuning, braille waterfall, and JJFlex's keyboard-first UX. Zero cost, zero hardware — just an internet connection. Research APIs/protocols for each platform (KiwiSDR has a documented WebSocket API). An iOS TestFlight app already proves accessible Web SDR control is viable.
- [ ] **RTL-SDR receive station**: $30 dongle + JJFlex = complete accessible receive station. PC-side DSP (from DSP abstraction layer) provides filtering, NR, AGC. Braille waterfall from IQ data. Full tuning experience. SoapySDR library for device abstraction.
- [ ] **Upgrade funnel**: Free (Web SDR) → $30 (RTL-SDR) → $50-200 (QRP TX) → $1,100 (IC-7300) → $3,000+ (Flex). Same app at every tier, same interface, same muscle memory. Each step adds capability (TX, hardware DSP, more bands) but the core experience is consistent.

## Digital Voice — FreeDV2 via Pi Proxy

- [ ] **FreeDV2 / RADEV2 integration**: Full planning session completed — see `docs/planning/future items from chatgpt brainstorming session.md` EPIC 1. Architecture: SSH-managed Pi node as external vocoder, with future native in-radio support. Includes Node Manager UI (Tools → FreeDV2 Node Manager), runtime backend selection (prefer node vs native), mode status states. Determined to be more than doable.

## Radio Memory Programming (RT Systems Alternative)

- [ ] **Radio memory/channel snapshots**: Read, write, and manage radio memories and channel programming — accessible alternative to RT Systems. RT Systems charges per-radio, is not accessible. Research memory formats for popular rigs (IC-7300, Yaesu FT-991A, etc.). Import/export, backup/restore, share channel plans between operators. Way down the line but fills a real gap.

## Website & Distribution Infrastructure

- [ ] **jjflexible.radio domain**: Register dedicated domain for the project. Host on Netlify (already paying $9/month). Ruby-based site (same stack as blindhams.network). Landing page, download links, release notes, setup guide.
- [ ] **Firmware hosting**: Serve .ssdr firmware files as Netlify assets from jjflexible.radio. Two files per Flex firmware release (~61 MB for 6000 series, ~372 MB for 8000/Aurora). Noel uploads new files when Flex releases firmware. Flex contacted for permission (2026-04-12).
- [ ] **Auto-update check for JJ Flexible**: App checks jjflexible.radio (or GitHub releases API) for new versions on startup. Notify user if update available with download link. Compare current version against latest published version.
- [ ] **Firmware version check**: When connected to a Flex radio, check `radio.Version` vs latest available firmware on jjflexible.radio. Notify user if firmware update available. Offer to download and install (LAN-only).
- [ ] **Donate link**: Add donation link to website — users have been requesting this. Prominent but not pushy.
- [ ] **App distribution**: Serve installers from jjflexible.radio, link to GitHub releases, or both. Consider which is better for discoverability vs reliability.
- [ ] **Fix blindhams.network**: Site currently broken — fix alongside jjflexible.radio spinup (same Ruby/Netlify stack, work with Codex).
- [ ] **Crash report upload**: Crash zip capture already exists (saves locally). Add a "Send Report" button that uploads the crash zip to jjflexible.radio endpoint (Netlify function or simple POST). Ping ntfy.sh to push notification to Noel's phone when a crash report arrives. User-initiated (click to send), not automatic — respects privacy. Add before next release once web endpoint exists.
- [ ] **Premium features infrastructure**: Future — iOS app, Android app, premium features (multi-radio, etc.) with revenue to cover hosting costs.

## Long-term
- [ ] Wideband SDR support: RTL-SDR (RX-only, ~$30), HackRF (TX/RX, ~$350), ADALM-Pluto (TX/RX, ~$200), Airspy, SDRplay. Needs a radio abstraction layer that can talk to FlexLib, SoapySDR, and potentially Hamlib/CAT. This is the accessibility play — getting the entry cost for an accessible SDR station from $3,000+ down to $200-400.
- [ ] **Radio abstraction — clean up `OpenParms` / `Callouts` structure**: When the radio abstraction layer sprint lands, dismantle the current two-class parallel structure (`AllRadios.OpenParms` + `FlexBase.OpenParms`, one shadowing the other). It was the root cause of BUG-058 (Don's 2026-04-16 NRE) — a C#/VB accessibility + shadowing trap that silently routed external callers to a blank stub. Replacement design: single `RadioCallouts` base class with the common delegates (FormatFreq, GotoHome, ConfigDirectory, OperatorName, CWTextReceiver); per-radio subclasses add their own bits (`FlexCallouts` → StationName, License, GetSWRText; `IcomCallouts` → CI-V address; `HamlibCallouts` → whatever). One `Callouts` field on the radio base, typed as the base, always public — no shadowing, ever. Concrete radios downcast when they need their own extensions. Also delete Kenwood-era dead weight from the current `AllRadios.OpenParms` (SendRoutine, SendBytesRoutine, NextValue1Description, RawIO, DirectDataReceiver — unused after the Flex-only cut). See `Radios/FlexBase.cs:6625` for the load-bearing comment explaining why the current field must stay public until this refactor happens.
- [ ] QRP rig support: Research affordable QRP transceivers (QDX, QMX, (tr)uSDX, etc.) as low-cost accessible entry points. These are kit/assembled rigs in the $50-200 range with CAT control.
- [ ] **Icom IC-7300/7300 Mark II support (HIGH DEMAND — 5 user requests)**: The 7300 is a hybrid SDR that outputs spectrum/waterfall data over CI-V protocol. At ~$1,100 it's the most popular HF rig of the last decade and less than half the cheapest Flex. Research CI-V spectrum scope data commands — if we can pull FFT data over USB, we can drive the braille panadapter from it. This could be the first non-Flex radio with accessible waterfall support. Research spike: grab CI-V spectrum data spec, determine resolution and refresh rate, assess feasibility. **Note:** Open source IC-7300 controllers exist — study their CI-V implementation rather than reverse-engineering from scratch.
- [ ] Xiegu radio support: Research Xiegu SDR rigs (G90, G106, X6100). SDR-based internally but unclear how much data is accessible via CAT/serial. X6100 runs Linux which is promising. Need to investigate CAT protocol depth and whether panadapter/waterfall data can be extracted.
- [ ] DSP abstraction layer: Build a JJFlexRadio-side DSP engine (noise reduction, filtering, FFT, AGC) that runs on the PC regardless of radio backend. For Flex: stack on top of hardware DSP for deeper signal extraction. For cheap radios (HackRF, QRP rigs): provide Flex-grade signal processing they don't have natively. Research neural noise reduction models (RNNoise, etc.), FFT libraries, and real-time audio pipeline architecture. Same ScreenFields controls drive both hardware DSP (Flex) and software DSP (everything else) — operator doesn't need to know where the processing runs.
- [ ] Weak signal / Dig Mode presets: One-keystroke DSP profiles optimized for FT8 and other weak signal digital modes — tight filter bandwidth, neural NR tuned for digital, optimized AGC/NB settings. Auto-load when switching to digital modes. Works with both hardware DSP (Flex) and software DSP (abstraction layer).
- [ ] Braille + sonified waterfall "famous interface" implementation
- [ ] Braille meter display: render active meter values on braille line, width-adaptive (20/40/80 char displays). Three hardware configs: (a) standard braille display only — meters share line, cursor routing keys for frequency digit tuning, (b) Dot Pad only — 20-char braille line for text meters plus tactile graphic meter panel, (c) both — standard display for tuning with cursor routing, Dot Pad for meter visualization
- [ ] Multi-radio simultaneous operation (possible premium)
- [ ] Auto-update / update notifications (needs server or web page for version checking; design update delivery mechanism)
- [ ] **Mac port**: FlexLib is .NET 10 LTS, runs on macOS natively. UI layer needs platform-specific approach (Avalonia, MAUI, or native). Dogpark and SmartSDR for Mac are competitors — Justin AI5OS reports Dogpark is usable with VoiceOver, SmartSDR Mac is "not fun." Key advantage: JJFlex would be the only Mac client with full setup wizard (registration, firmware, port forwarding). Keep business logic separated from WinForms/WPF to enable this.
- [ ] **iOS remote client**: Remote radio control from iPhone/iPad. Three connection models: (a) bridge through Mac/Windows JJFlex instance, (b) direct SmartLink connection (SmartLink for iOS proves this works), (c) limited standalone operation. Ties into CW notification / haptic vision work for deaf-blind users. VoiceOver on iOS is mature — accessibility comes almost free if built with standard UIKit/SwiftUI.
- [ ] AllStar (ASL) remote base: Connect JJFlexRadio to AllStar Link nodes, turning a supported radio (Flex for now) into a remote base accessible from the AllStar network. Research ASL protocol, audio routing (DAX/Opus to ASL), PTT integration, and node registration. Big project — needs feasibility study first.
- [ ] Transmit signal sculpting: Adjust TX filter width from the keyboard, similar to how RX filter edges can be adjusted with bracket keys. Let operators narrow or widen their transmitted audio bandwidth for tight band conditions or signal shaping. Research FlexLib TX filter properties (`Slice.TxFilterLow`, `Slice.TxFilterHigh`) and determine safe limits per mode.
- [ ] Braille art on the waterfall: Render callsigns, CQ markers, or ASCII art as braille characters on the panadapter braille display. If sighted hams get to paint their callsigns in the waterfall, blind hams deserve the same fun. Arr. 🏴‍☠️
