# JJ Flex — TODO / Rolling Backlog

Last updated: 2026-03-13

## Open Bugs

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

### Upcoming features
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
- [ ] DAX integration research and decision (use Flex manager vs internal config)

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

## Long-term
- [ ] Wideband SDR support: RTL-SDR (RX-only, ~$30), HackRF (TX/RX, ~$350), ADALM-Pluto (TX/RX, ~$200), Airspy, SDRplay. Needs a radio abstraction layer that can talk to FlexLib, SoapySDR, and potentially Hamlib/CAT. This is the accessibility play — getting the entry cost for an accessible SDR station from $3,000+ down to $200-400.
- [ ] QRP rig support: Research affordable QRP transceivers (QDX, QMX, (tr)uSDX, etc.) as low-cost accessible entry points. These are kit/assembled rigs in the $50-200 range with CAT control.
- [ ] **Icom IC-7300/7300 Mark II support (HIGH DEMAND — 5 user requests)**: The 7300 is a hybrid SDR that outputs spectrum/waterfall data over CI-V protocol. At ~$1,100 it's the most popular HF rig of the last decade and less than half the cheapest Flex. Research CI-V spectrum scope data commands — if we can pull FFT data over USB, we can drive the braille panadapter from it. This could be the first non-Flex radio with accessible waterfall support. Research spike: grab CI-V spectrum data spec, determine resolution and refresh rate, assess feasibility. **Note:** Open source IC-7300 controllers exist — study their CI-V implementation rather than reverse-engineering from scratch.
- [ ] Xiegu radio support: Research Xiegu SDR rigs (G90, G106, X6100). SDR-based internally but unclear how much data is accessible via CAT/serial. X6100 runs Linux which is promising. Need to investigate CAT protocol depth and whether panadapter/waterfall data can be extracted.
- [ ] DSP abstraction layer: Build a JJFlexRadio-side DSP engine (noise reduction, filtering, FFT, AGC) that runs on the PC regardless of radio backend. For Flex: stack on top of hardware DSP for deeper signal extraction. For cheap radios (HackRF, QRP rigs): provide Flex-grade signal processing they don't have natively. Research neural noise reduction models (RNNoise, etc.), FFT libraries, and real-time audio pipeline architecture. Same ScreenFields controls drive both hardware DSP (Flex) and software DSP (everything else) — operator doesn't need to know where the processing runs.
- [ ] Weak signal / Dig Mode presets: One-keystroke DSP profiles optimized for FT8 and other weak signal digital modes — tight filter bandwidth, neural NR tuned for digital, optimized AGC/NB settings. Auto-load when switching to digital modes. Works with both hardware DSP (Flex) and software DSP (abstraction layer).
- [ ] Braille + sonified waterfall "famous interface" implementation
- [ ] Braille meter display: render active meter values on braille line, width-adaptive (20/40/80 char displays). Three hardware configs: (a) standard braille display only — meters share line, cursor routing keys for frequency digit tuning, (b) Dot Pad only — 20-char braille line for text meters plus tactile graphic meter panel, (c) both — standard display for tuning with cursor routing, Dot Pad for meter visualization
- [ ] Multi-radio simultaneous operation (possible premium)
- [ ] Auto-update / update notifications (needs server or web page for version checking; design update delivery mechanism)
- [ ] AllStar (ASL) remote base: Connect JJFlexRadio to AllStar Link nodes, turning a supported radio (Flex for now) into a remote base accessible from the AllStar network. Research ASL protocol, audio routing (DAX/Opus to ASL), PTT integration, and node registration. Big project — needs feasibility study first.
- [ ] Transmit signal sculpting: Adjust TX filter width from the keyboard, similar to how RX filter edges can be adjusted with bracket keys. Let operators narrow or widen their transmitted audio bandwidth for tight band conditions or signal shaping. Research FlexLib TX filter properties (`Slice.TxFilterLow`, `Slice.TxFilterHigh`) and determine safe limits per mode.
- [ ] Braille art on the waterfall: Render callsigns, CQ markers, or ASCII art as braille characters on the panadapter braille display. If sighted hams get to paint their callsigns in the waterfall, blind hams deserve the same fun. Arr. 🏴‍☠️
