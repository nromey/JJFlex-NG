# Queue-burn ensemble — 2026-08-07 (CLOSED)

**Archived 2026-08-23** out of `active/research-queue.md`, which had reached 890
lines with roughly 60 percent of it superseded or closed. Nothing here is live.

This is the record of the evening eleven background-agent tracks plus the NAT lab
ran and merged the same day — the run that established the fleet model now
described in `memory/project_background_agent_fleet_model.md`. Kept because the
sequencing and the decisions are worth reading before running another ensemble,
not because anything in it is outstanding.

---

## Decisions captured 2026-08-07 (overnight)

- **Reboot gets a second home on the Radio menu** (Noel's call; Radio Setup step 7 stays). The Radio menu grows a maintenance section: Reboot, firmware update entry, plus other radio-function candidates at Claude's judgment (rename lands there via Track F's Radio Setup work). → Track A.
- **CW notifications default stays FALSE.** Noel: not everyone does CW; his podcasts will demonstrate the checkbox. The grouping fix (CW-enable beside the Alert-device combo) still ships. → Track B.
- **Build 4.1.16.536 published to testers' `debug\`** (2026-08-07, replaces 517) after Noel confirmed the LAN ghost sweep live — radios appear on discovery and are removed when powered off. Major coding today; testers get the next drop when we're ready. Don is the only regular tester right now.
- **rarbox WireGuard NAT lab: GO.** Noel authorized execution 2026-08-07; rarbox is in approve mode (expect at most one approval prompt). Read `memory/project_rarbox_hardening.md` before any command. Orchestrator runs it while tracks code.
- **Confirmed live by Noel 2026-08-07:** Use Now (Alt+U) session override; auto-connect startup announcement (verified earlier at the radio); LAN radio add/remove in the selector. The selector slice is fully verified except the two waits-on-circumstance items in Blocked below.
- **Signing + CI track ratified (2026-08-07 evening) — next infra slot, parallel to the CW arc.** Two steps: (1) local signing hook in `build-installers.bat` — sign jjflexible.exe + both NSIS installers against Trusted Signing profile `romeycert` (account already provisioned; reference the working pipeline at `C:\dev\Civ-vi-access\.github\workflows\release.yml`); (2) GitHub Actions release workflow for `nromey/JJFlex-NG` adapted from that file — tag push → build both arches → sign → verify → publish. Noel's part: add the federated credential / copy the secrets from the Civ VI setup, one `az login`, and the Certificate Profile Signer role grant if his user lacks it. Strategic frame: this plus the updater is the JJFlex self-sufficiency work that precedes the eventual all-in-on-Connect lock (see `project_jjflexible_connect.md`). Track M's install manifest doubles as the delta-updater's file-hash schema — the updater arc inherits it.


## Queue-burn ensemble — LANDED 2026-08-07 evening

Eleven track agents (Fable: A/C/D/F/G/H/I/J; Opus: B/E/K) plus the NAT-lab
agent, all spawned as background subagents from the orchestrator session,
all completed the same evening. Merge order as executed: J, B, K, E, F, C,
D, A, I, G, H — clean Debug x64 build verified at every landing. Branch
head after the train: `f7ff5716` on `track/flexlib-4220`, pushed. Per-track
design decisions live in `docs/planning/agile/archive/track-instructions/
qb-track-*.md`. The NAT lab is live on rarbox (port-restricted active);
run report at `docs/planning/active/natlab-run-report.md`.

**Consolidated Needs Noel (taste calls and live verifications, none
blocking):**

- Guided testing run covers most live verifications — doc at
  `docs/planning/active/nightowl-guided-testing.md`.
- Track A: connect success earcon shape (classic double vs distinct rising
  arrival — one-line change); stub-speech phrasing check.
- Track B: relax the stereo-only mic filter (mono USB headsets are common
  blind-op hardware; needs one live mono-capture test, then a one-line
  change); menu-vs-key level step mismatch (menu 10, keys 5); AudioSetup
  registered `KeyScope.Radio` so Command Finder hides it with no radio.
- Track C: want a machine-wide "never hole punch from this computer"
  policy on top of per-radio? Legacy account punch fallback retirement
  timing.
- Track D: refused-vs-timeout wording against Don's real router failure;
  speak-connection-summary hotkey proposal (needs keyboard-audit yes/no);
  should the identity card get a non-Settings quick surface?
- Track E: Alt+P accelerator sign-off; favorites-first vs live-first
  ordering taste; dual-homed live test (8600 on LAN while
  SmartLink-registered — NAT loopback unknown until tried).
- Track F: hosted forgot-password link live test; wording approval on new
  confirm labels.
- Track G: -10 dBm drive de-overload question (the open clean-demodulation
  item) — **Don's answers 2026-08-08 supply an eyewitness recipe pointing
  the same way: the demo he saw drove sub-watt via a defined XVTR band, not
  integer watts, and possibly used a separate RX port**; ears-slice level
  telemetry — plumb it or accept fixed drive; temp XVTR band auto-creation
  yes/no — **effectively answered YES by the same datapoint, pending the
  levelled-loopback rerun (plan §6 item 1b)**; record/play timing over
  SmartLink with Don; **new: RX-bandwidth-matched-to-TX before recording,
  Don's stated condition for the record tier (plan §6 item 1a)**.
- Track H: optional default chords for MemoryScan / SpeakFrequency (now
  honest-unbound); NVDA rebind-persist-reset loop is in the guided doc.
- Track I: gate "No Transmit Slice" behind a confirmation? XVTR end-to-end
  needs a real transverter. NVDA pass on new menu mnemonics.
- Track J: miss-announcement wording ("Slice D is in use by another
  station") when next on the air.

**Post-merge follow-ups (Claude-side, queued):**

- Fold Track I's TXSlice field keys + four Command Finder rows into
  KeyInventory (in-code markers say where); the keyboard-reference TXSlice
  section is already added.
- Wire Track D's NetworkIdentityCard into the selector detail area — Track
  E left grid Row 4 empty for it.
- Wire D's failure advice into AutoConnectFailedDialog body text (D left it
  as Track A territory).
- Start-fresh auto-offer after N consecutive AUTH failures (now possible —
  D's AuthFailed classification landed).
- Bound `DebugInfo.GetDebugInfo`'s 30-day archive zip (needs a size-aware
  ZipUtils; shared code).
- Delete dead code: `JJTraceListener.cs`, `Tracing - Copy.cs`,
  `FlexBase.SliceState(int)`; decide fate of orphaned FiltersDspControl /
  RadioNumberBox (Sprint 8 archaeology, per Track A).
- RadioInfoDialog's Feature Availability tab is dead UI
  (`ShowRadioInfoDialog` never assigned) — wire or remove.
- Track G changelog entry at ship time (deliberately deferred).
- `UploadMaxBytes` hardcoded to the receiver's 50 MB limit — move together
  if the receiver's limit moves.


## Queued — assigned to queue-burn tracks — ALL SHIPPED 2026-08-07

Every entry below landed with its track (see the ensemble section at the
top). Full specs and design decisions live in the archived
`qb-track-*.md` instruction files. Kept for one seal cycle as the
queue-of-record, then prunable.

**Track A (orchestrator lane, main worktree — small fixes):**
- Radio menu maintenance section: Reboot (decided above), firmware update entry, candidates per judgment.
- ~~TX-slice hidden door~~ → **moved to Track I** (it IS menu-parity work: Transmit submenu mirroring Selection, slice-page TX field, Command Finder registration — letters per Track J's post-fix semantics). ~~Field-char enumerability~~ → **moved to Track H** (the `?` speaks-this-field's-keys handler, generated from the same table as the key manifest so doc and speech can't drift).
- **Radio-side reverts of a user setting are SILENT — announce them (Noel, live at the radio 2026-08-09).** Symptom as experienced: "I selected mode USB on the slice and it did nothing." Trace `JJFlexRadioTrace-20260809-151748.txt` shows it twice, reproducibly: `91047 DemodMode:slice 0 USB` → `96070 DemodMode:slice 0 FM` (5s later), then `119631 USB` → `123707 FM` (4s later). The revert was RADIO-side — band persistence re-applying the stored 2m settings (FM + ANT 1) with `band_persistence_enabled=1` — and the same mechanism was also silently reverting the RX/TX antenna back to ANT 1 during the transverter setup, which is what made that look like a transverter-band problem. **The app OBSERVED the revert** (those `DemodMode:` lines are inside `mainLoop:RXDemodMode`, i.e. the read path) **and said nothing.** No-silent-keystrokes in its subtler form: the keystroke was not silent, its REVERSAL was. Fix shape: when a slice property the user just set changes back underneath them within a short window, speak it ("mode back to FM, the radio's band settings changed it") rather than letting the control read as broken. Generalizes past mode — antenna, filter, anything band persistence owns. Noel reports hitting this before without being able to pin it down, so it is a recurring confusion, not a one-off. Related: whether JJFlex should surface `band_persistence_enabled` at all (it is radio-global, currently reachable nowhere in the app, and its effects are invisible).
- Lineout Up/Down key handlers refuse to run while PC audio is on (`KeyCommands.cs:578,584` gate on `!rig.PCAudio`) — headphone handlers don't, outputs are independent; the gate looks wrong.
- `LocalAudioMute` (`FlexBase.cs:7321`) gangs all three outputs and is dead code — keep or kill.
- Vestigial duplicate PlayCwSK wiring at `MainWindow.xaml.cs:2352-2362` (PowerOn re-wire) duplicates ctor wiring at :110-114, re-introduces the BUG-061 gap pattern — remove the PowerOn copy.
- Remote re-click on a live SmartLink session times out 10s waiting for a list the server never resends (trace 20260805-163019) — satisfy the wait from `myRadioList` when session already connected; treat later unsolicited list as refresh.
- "Start fresh with SmartLink" button in the account manager — clear token state per account (or all), force clean sign-in; the button version of delete-SmartLinkAccounts.json Noel talked Don through by hand. Consider auto-offering after N consecutive auth failures.
- **Negative numbers unenterable in value fields (Noel live find, 2026-08-07, RX RF gain).** `ValueFieldControl.HandleNumberEntryKey` (`ValueFieldControl.xaml.cs:188`) accepts only digits/Backspace/Enter/Escape — no `OemMinus`/`Subtract` — and entry mode only STARTS on a digit, so "-8" can't begin. Fix: minus toggles the buffer's sign and can start entry, gated on `_min < 0` (non-negative fields reject with earcon + speech, no-silent-keystrokes); speak "minus". Workaround meanwhile: arrows reach negatives fine (bounds are radio-correct in ScreenFields). **Sibling bug, same session:** `FiltersDspControl` RF Gain box hardcodes 0–50 (`FiltersDspControl.xaml.cs:205`) — can't reach negative gain at all on that surface and overshoots the ceiling; wire it to `RFGainMin/Max/Increment` like ScreenFieldsPanel:204. Audit `RadioNumberBox` for the same minus gap. Cross-ref Track I: XVTR dBm entry needs minus AND decimal on this same control — build the minus fix with the decimal extension in mind.
- **Menu stub audit (Noel live find, 2026-08-07: "Alt+T → Hotkey Editor says it's not wired").** Tools → Hotkey Editor is an `AddNotImplemented` stub (`NativeMenuBar.cs:1138`) left over from the native-menu migration — but the editor EXISTS (`SetupKeysDialog`, shipped Sprint 7, still reachable via Help → Key Assignments → Update). Wire the menu item to it. While there: (a) the three Help "Key Assignments" variants (:1297-1299) all open the same dialog with no sort mode — honor the variants or collapse them; (b) sweep ALL `AddNotImplemented` stubs for ones whose implementations already exist — Station Lookup (:1131) is a stub while `CommandValues.StationLookup` is a registered working command; check Operators, Connected Stations, Local PTT On, Band Plans, Log Characteristics, Import/Export Log, LOTW Merge; (c) verify whether SetupKeysDialog is the Sprint 7 tabbed editor (Global/Radio/Logging tabs + conflict detection per changelog :730) or an older key-action mapper — if the tabbed editor got lost, that's a bigger rebuild, scope it separately.
- Optional: NativeMenuBar guard to skip RebuildCurrentMenu during teardown (belt-and-suspenders from the ActiveSlice sweep).
- Stretch: connect double-beep on EVERY successful connect path (picker local / picker remote / auto-connect) — the signature sound (memory `project_connect_earcon_signature_sound.md`); dispatch paths are not unified, audit each.

**Track B (Settings → Audio surface + device pickers):**
- "Radio Outputs" group (radio-connected only): headphone level + line out level as set-once sliders (FlexBase wrappers exist, 0–100, `FlexBase.cs:7332-7357`), plus the mutes (`HeadphoneMute`/`LineoutMute`/`FrontSpeakerMute`), live-apply. Field driver: on a non-M radio, software is the only volume knob that exists.
- PC Audio checkbox in the audio settings — inspectable/override surface, not required setup; reflect the live state (PC audio auto-enables on remote connect, `FlexBase.cs` ~9875); saved "off" must not silently fight the auto-enable.
- Rebuild the audio device picker (old C2 item 16): the legacy twice-in-sequence `devList` WinForms dialogs are unusable by ear and gate ALL audio on a fresh install. One surface: radio input/output, alert device, CW output; arrow-readable; current selection announced; system default marked; also reachable from Settings' audio section; EnsureAudioDevicesConfigured offers the picker in words.
- Group the CW-enable checkbox with the Alert-device combo (default stays FALSE per decision above). Device-missing fallback: fall back to system default WITH a spoken note, never silent.
- "Radio outputs at zero/muted" visibility affordance + the "why is my radio silent" advisory ladder — **first rung is CONNECTED state**: a Flex makes no audio, including at the physical jacks, until a client connects (Noel's silent-headphone mystery, solved 2026-08-06).
- Help topic: `docs/help/md/audio-troubleshooting.md` + a getting-started line for operators migrating from conventional rigs ("radio on but silent? Connect first"). Noel wants to voice this in recorded documentation later.
- Multiple-doors principle, ratified: every audio setting also appears in the audio settings surface.

**Track C (per-radio network settings, serial-keyed):**
- Per-radio profile in the existing serial-keyed store (`radios\<serial>\config.xml`): mode = Auto | ForwardOnly | HolePunch (Auto follows radio-reported `fwdTcp/fwdUdp/punch` flags — zero config, friction-tax) + optional fixed punch port.
- Editable OFFLINE from the known-radios list (kills the connect-first chicken-and-egg, SettingsDialog.xaml.cs:375); punch selectable with no forward config (kills the backwards gate at :469); `ConfiguredListenPort` double-duty resolved (one field, two meanings today, line 610 vs punch box).
- Account-level fields demote to legacy defaults; per-radio wins; `sendRemoteConnect` consults per-radio → account → radio-reported.
- Folds in the "disable hole punch" option: ForwardOnly mode skips doomed punch attempts and fails fast into guidance instead of 30s of silent grinding.
- Real-world driver: Don's radio needs forward mode, Noel's 8600 needs punch + fixed port; per-account settings can't describe both.
- Interim unblocker (still valid): hand-edit `%AppData%\JJFlexRadio\SmartLinkAccounts.json` (app closed): `"connectionMode": 2` + `"configuredListenPort": 40420`.

**Track D (connectivity truth & guidance):**
- Surface `test_connection` results we already collect (fwdTcp/fwdUdp/upnpTcp/upnpUdp/holePunch — ground truth from OUTSIDE) on connect failure: "the radio reports its forwarded TCP port is not reachable — check the router rule." Don's traces read fwdTcp=False for hours while we guessed. Caveat: never auto-run the probe on a punched session (it's at minimum useless and was once suspected of killing them; the f842e93f skip-gate stays).
- Distinguish refused (<200ms — router answered, nothing behind the rule) from timed out (packets never arrived); different advice, currently both "open failed."
- Generate the router rule from radio-reported values: external ports the radio advertises (`public_tls_port`/`public_udp_port`), internal fixed (TCP 4994 / UDP 4993 for the SmartLink path), LAN IP from discovery. Nobody's memory gets a vote (`feedback_never_assert_config_values_from_memory.md`).
- "No RX antenna" is a misleading message when the audio path never came up — now that `failureReason` is populated, say the real thing.
- Network identity card, read side (old C2 item 10): IP, serial, model, firmware, public IP/forwarded-port status — tabbable and arrow-readable, picker detail area and/or Status dialog; works for remote radios too (`Radio.ParseNetParamsStatus`, Radio.cs:6914). Write side (static IP controls) stays settings-parity work, NOT this track.
- setupRemote's ConnectFailed treats every failure as auth-shaped and prescribes an interactive login; classify by session status / failure class — non-auth failures must not summon a sign-in form.
- User-initiated Settings "Test network" (`RunNetworkDiagnosticAsync`) would kill a live punched session — needs warn/defer/detach, not a silent gate.

**Track E (selector, roster, dual-homing):**
- Favorite radios / known-radios roster: enumerate the serial-keyed store + last-seen/via-which-account metadata; selector marks rows live/offline via RadioFound/RadioRemoved; favorites sort first; offline favorite can offer "wait for it" once camping ships. Pairs with set-connected-radio-as-default.
- **Dual-homing with path CHOICE (expanded by Noel 2026-08-07):** a radio that is both local and SmartLink-registered always presents as "local" today (LAN wins the row, WAN identity never shows). Surface both homes per radio AND let the user choose the connection path — "connect via SmartLink even though it's local." Three payoffs: users learn both paths exist, Noel can test WAN behavior (ghost sweep, punch) from the comfy chair, and it's the honest UI for the roster.
- Per-account radio-list cache as fast paint, not authority: paint cached list immediately on account switch, live fetch in parallel, replace + announce "radio list updated"; provenance beats TTL ("last known radios for <account>, refreshing"); never connect from cache without a refresh in flight; extend `radioConnectionCacheV1.xml`, don't add a second store.
- LAN vs Remote in each row's accessible name ("FLEX-8600, local network" vs "6300inshack, remote via SmartLink") — old C2 item 13 addendum.
- Old C2 item 6: empty-list "no radios found yet" announcement collides with discovery landing right after — only announce if still empty after discovery has had a real chance.
- Old C2 item 7: state-driven SmartLink account button (zero accounts → "Sign in to SmartLink"; one → "SmartLink Account"; two+ → "Switch Account"); fix the unconditional "Account updated" speech on cancel.
- Old C2 item 14: arrowing off the top of the radio list escapes to the auto-connect checkbox — arrows must stay inside the list (DirectionalNavigation Contained), Tab is the way out.
- Old C2 item 15: say which SmartLink account is active — speak on Remote press ("Connecting to SmartLink as …"), readable text near the account button, accessible name carries it.

**Track F (dialog & SmartLink account sweep — C2 revival):**
Ledger carried forward from the archived C2 instructions (`docs/planning/agile/archive/track-instructions/track-dialog-sweep-C2.md`) with statuses updated: items 2/4 DONE (merged), 12 SHIPPED as Use Now, 11 narrowed by native login (WebView2 is now MFA-fallback only), 16 → Track B, 6/7/14/15 → Track E. Remaining in F:
- Item 1: the ~94-site MessageBox.Show sweep (judgment per site; AdvisoryDialog for advisories; errors never get suppress keys).
- Item 3: GPS status dialog arrowability (LiveStatusTextBox).
- Item 5: radio rename field (FlexLib `Radio.Nickname` setter works, persists radio-side, flows through discovery, works over SmartLink) — Radio Setup GroupBox + FlexBase setter + auto-connect display-name refresh. Urgency context: the unnamed 8600 row is what Noel mis-picked aiming for Don's 6300.
- Item 5b: ConfirmActionDialog warnings unreadable — highest-stakes text in the flow (keying guidance, do-not-power-off); give it the AdvisoryDialog read-only reviewable treatment. Second sighting during the firmware run.
- Item 8: native SmartLink signup + forgot password (hosted page's signup half-works then reports failure; SmartSDR posts `dbconnections/signup` / `change_password` natively — endpoints and error mapping in the archived ledger; reference script `smartlink-signup.ps1` in the 2026-08-04 scratchpad). Test the hosted page's forgot-password link too.
- Item 8a: propagate a mid-session sign-in to the live connection (load account into live FlexBase, re-run SuggestRegistrationIfUnregisteredAsync; today's only recourse is restarting the app).
- Item 13: "not registered" advisory must name the account it checked and handle registered-elsewhere.
- Item 17: "See the message" dead-end sweep — any spoken string that refers the user to text they must go find is a bug; speak the reason itself. Plus: affordances should announce as unavailable when remote, not fail-then-explain.
- Startup speech ordering policy: while the advisory chain is active, main-window bring-up speech queues behind it (welcome line + focus-driven slice speech are separate un-parked paths); check how Tab reaches the main window behind a modal.
- NVDA lessons to carry into every dialog touched: blank lines need a single space (degenerate UIA range re-reads neighbors); every IsDefault button needs explicit AccessKey/AcceleratorKey (else "carriage return"); arrow through every line of every converted dialog under NVDA.

**Track J (slice identity — position vs letter; promoted from Track A 2026-08-07):**
The bug cluster moves here from the blocked list — nothing about it is blocked; the code-read is high-confidence and Noel's trace (when it lands) only confirms the trigger sequence. Scope: make the LETTER the identity — keep `mySlices` sorted by radio slice index (or map by letter), so position and letter can never disagree; then audit every positional consumer: `VFOToSlice`/`SliceToVFO` (`FlexBase.cs:6446/6458`), the direct-select `ch - 'A'` (`FreqOutHandlers.cs:1292`), `JumpToSlice`'s fabricated `(char)('A'+index)` letter (`KeyCommands.cs:2152`), RXVFO/TXVFO stale-position risk across slice removal (`FlexBase.cs:10294-10360` restore paths), and `ReleaseAllExtraSlices` keeping position-RXVFO (`FlexBase.cs:7263` — Noel's lived keeps-slice-A experience). Verify the mode-menu symptom dies (menu → `Rig.Mode` → `theRadio.ActiveSlice.DemodMode` lands on the slice the user means). Add the Slice menu Selection labels as the regression canary (they show true letters in position order). Model: Fable — core correctness with subtle event-ordering semantics (sliceAdded arrival order, another client's churn). Merge early; no other track touches these regions.

**Track K (trace rotation + crash-bundle size policy — design ratified 2026-08-07, plan section 4b):**
Driver: the live `JJFlexRadioTrace.txt` hit 11.7 GB in one marathon session; today's crash bundle is missing its trace because a whole-file attach was impossible. Ratified design: (1) size-based rotation — at ~250-500 MB close the active file, zip into the archive as a session PART, start fresh; long sessions become chains of parts; (2) crash bundles attach the CURRENT part (the tail is the evidence), bounded by construction; (3) upload size policy — report text + trace tail always; full memory dump only under the server limit, else held locally with a spoken "saved here if support asks" (the "couldn't save a stream of that size" dialog was most plausibly the ~500 MB upload rejection — an honest "saved fine, too big to auto-send" message is half the fix). Files: `JJTrace/Tracing.cs`, TraceArchiveBootMaintenance, SaveCrash/bundle assembly, upload path. Model: Opus — the design is ratified and spec-shaped. No overlap with any other track.

**Track I (menu-parity audit + XVTR-aware power control — routed from the audio session 2026-08-07, plan section 4a):**
App-wide UI architecture, bigger than the audio track (Phil2's routing note, ratified by Noel's "pass to Phil the first"). (1) **Menu-parity audit:** every actionable ScreenFields control (transmit, receive, antenna sections) gets an addressable menu path with accelerators — Alt+R → T → P should walk to a Power dialog. Part add-missing (power has NO menu path anywhere), part make-findable (TX/RX antenna submenus already exist in NativeMenuBar ~685-707 and Noel has never met them), part verify-every-menu-mode-builds-them (four un-unified dispatch paths, per memory). (2) **XVTR-aware power control:** the Power dialog and the ScreenFields power field switch to dBm/decimal entry (`Xvtr.MaxPower` semantics, -10.0..+10.0 dBm) when the selected TX antenna is a transverter port; integer watts otherwise (`Radio.RFPower` is int — fractional watts impossible on the main control, which is why 1W is the loopback floor). Check the power field accepts typed digits. Model: Fable. Worktree cut at launch.

**Track H (hotkey surface redesign + key coverage audit — Noel-directed 2026-08-07):**
Live falsification: Help → Key Assignments → Update → SetupKeysDialog cannot actually change a key. Diagnosis: ShowKeysDialog/SetupKeysDialog are Jim's legacy key-action system, predating (and likely orphaned from) Sprint 23's unified KeyCommands v5 dispatch; the Sprint 7 "tabbed hotkey editor" (changelog :730) also predates v5. Redesign, don't rewire: ONE Keys surface backed by the KeyCommands registry — views by scope/alphabetical/function-group; real editing (press-new-key capture, conflict detection that names the collision + steal/cancel, live rebind, unbind, per-key and global reset-to-default); field-level character keys shown as read-only rows (not rebindable, but enumerated — pairs with the `?` speak-field-keys idea); Tools → Hotkey Editor and ONE Help → Key Assignments both open it (edit vs view mode — multiple doors, one room; the three duplicate Help variants collapse). Deliverable: generated canonical key manifest (registry + field-handler introspection) reconciled against keyboard-reference.md — the CLAUDE.md keyboard-audit automation seed. Verify-then-delete the legacy dialog pair (check their keyFile isn't still consumed; migrate if it is). **Plus the no-shadow audit (from the Ctrl+Shift+W incident): no control-local handler may shadow a Global-scope chord — every bound key speaks its TRUE action in every state; G fixes the specific workshop shadow, H sweeps for the class.** Model: Fable. Merge after A and G (absorbs their registrations). Worktree cut at launch.
**Slice add/release keys are poorly chosen — Noel, 2026-08-09, at the radio ("gotta be better keys to use like del for delete"); explicitly deferred, not now.** Today: `.` creates a slice, `,` releases the current one, `Shift+,` releases all but the first (`FreqOutHandlers.cs:1084,1099`; `keyboard-reference.md:107,155,171`). Punctuation keys carry no mnemonic relationship to add/remove and are easy to hit by accident next to the number row. **Constraint the redesign must respect: `Delete` is ALREADY BOUND — it clears the transmit slice, a soft TX lockout (`keyboard-reference.md:261`, Delete or Backspace).** So the obvious "Del deletes a slice" mapping collides with a safety-adjacent command and cannot be taken without relocating that first. `Insert` is ruled out independently: it is NVDA's default modifier key, so a bare Insert binding is hostile to the primary screen reader. Whatever replaces them needs the same three-way shape (create / release one / release all but first) and a changelog heads-up, since removals need warning per the keyboard-audit SOP.

**Track G (Audio Workshop — hear yourself): ALL THREE PHASES BUILDABLE (2026-08-07 marathon complete).**
Spec is the living plan file `docs/planning/active/audio-workshop-plan.md` (mox-parrot-sidetone) — read sections 4/4b/6 for the day's verified results and the final RF-with-overload model. Phase 1: Audio Check session (MOX via `PttSafetyController` lock path; low-power default ON; two-stage Escape; **key-up announcement is SAFETY-CRITICAL, not polish** — software wire-keying left the operator transmitting unaware; safety line speaks frequency + power; remote-DAF advisory), mode-aware monitor **for phone modes only — the CW-monitor half is DEFERRED behind the CW pipeline rewrite (Noel, 2026-08-07; see wave 2)**, TX-source awareness aimed at the ACTIVE source (MicGain targets the SELECTED input — verified; surface/set the mic source: **source coherence is the precondition for every honest measurement**; RCA hardware keying can't be software-unkeyed — `source=` is the diagnostic), help rewrite, Command Finder registration, Ctrl+Shift+W shadow fix, MicInput="PC" investigation (`FlexBase.cs:9147`). Phase 2 (UNBLOCKED — record semantics fully verified live): record/play wrappers (`RecordOn`/`PlayOn`/`PlayEnabled`); auto-play-on-unkey default (performed live: unkey → playback in ~1s); **check recorder state before re-arming** (a live re-arm race nearly wiped takes); 120s buffer cap, ring-like retention, two-take A/B workflow viable; fidelity labeled honestly — the recording carries the FULL processing chain (verified by A/B with processor cranked). Phase 3: Loopback Check button per the verified recipe — BUT with the final model's honesty: it's genuine port-to-port RF into a **massively overloaded receiver**; today's yield is "a simulacrum — basically splatter" (Noel's ratified framing) proving audio present/processed/roughly right, NOT a faithful off-air listen; **new hard requirement: manage coupling level** (dBm-precision XVTR drive into the receiver's linear range, plausibly auto-calibrated against S-meter/overload) — whether that upgrades it to clean demodulation is an OPEN question, not a promise; SDR-on-a-real-antenna is elevated to the first-class ground-truth tier in help/positioning. Plus the crash fix pair (plan 4b): TX-getter family null-guards (`MicGain` FlexBase.cs:7839 + boost/bias/compander/processor/filter-edge/monitor-gain siblings — missed by the 8/5 sweep) and stop `_meterTimer` when the RIG dies (the singleton outlives the radio; today only dialog-close stops it). 1-SCU: record-during-mute PROVED ground truth under FDX-off simulation; Don's 6300 is now a likely-confirm guided experiment (his radio gets the final word).

