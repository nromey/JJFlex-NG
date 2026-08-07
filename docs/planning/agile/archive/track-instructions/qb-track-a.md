# QB Track A — Small-fixes batch (delegated from the orchestrator)

**Recommended model: Fable.** Mostly well-specified small fixes, but the
stub audit and the earcon stretch item carry judgment. Document calls in
a "Design decisions" section appended here.

## Context

One of the 2026-08-07 queue-burn tracks (plan:
`docs/planning/active/nightowl-pileup-ragchew.md`; queue of record:
`docs/planning/active/research-queue.md`, Track A section). JJ Flex is a
screen-reader-first FlexRadio client for blind hams. Originally the
orchestrator's own batch; delegated to keep the orchestrator window
clean. You work ONLY in this worktree (`C:\dev\jjflex-qb-a`, branch
`qb/track-a`) — never in `C:\dev\JJFlex-NG`.

## Work items

1. **Radio menu maintenance section** (`NativeMenuBar.cs`, Radio menu):
   add a separated group with **Reboot** (call
   `RadioMaintenance.RebootWithConfirmation(rig, onRebootInitiated)` —
   the SAME shared flow as the hotkey and Settings button; MainWindow
   supplies `powerNowOff` via the existing pattern, see
   `NativeMenuBar.cs:1550` for how the settings dialog gets it) and a
   **firmware update** entry (open the existing firmware update surface —
   find its current entry point and reuse; do not build new firmware UI).
   Add other radio-function candidates only if clearly warranted; note
   them in Design decisions.
2. **Lineout keys gate bug** (`KeyCommands.cs:578,584`): the Lineout
   Up/Down handlers refuse to run while PC audio is on (`!rig.PCAudio`),
   but the headphone handlers don't gate, and the outputs are
   independent. Remove the gate; keep the speech honest.
3. **`LocalAudioMute` keep-or-kill** (`FlexBase.cs:7321`): it gangs all
   three outputs and appears to be dead code. Verify no caller exists,
   then delete it (or document why it stays).
4. **Vestigial PlayCwSK wiring** (`MainWindow.xaml.cs:2352-2362`): the
   PowerOn re-wire duplicates the ctor wiring at :110-114 and
   re-introduces the BUG-061 inter-utterance gap pattern. Remove the
   PowerOn copy; verify the 73/SK still plays on close.
5. **Remote re-click 10s timeout** (trace 20260805-163019): re-clicking
   Remote on a live SmartLink session re-sends ReRegister then blocks
   waiting for a fresh radio-list event the server never re-sends (one
   list per TLS session). Fix: when the session is already connected and
   `myRadioList` is populated, satisfy the wait from the cached list
   immediately; treat a later unsolicited list as a refresh. SEAM: touch
   ONLY this wait logic — connect FAILURE classification belongs to
   Track D; the refresh/morph flow shipped 2026-08-06 must not regress.
6. **"Start fresh with SmartLink" button** in the saved-accounts manager:
   clear saved token state for one account (or all) and force a clean
   native sign-in — the button version of "delete
   SmartLinkAccounts.json". Confirmation dialog names what it clears.
   Consider (and note, don't necessarily build) auto-offering after N
   consecutive auth failures.
7. **Menu stub audit** (`NativeMenuBar.cs`, the `AddNotImplemented`
   sites): wire the ones whose implementations already exist — **Station
   Lookup** (:1131) has a registered working command
   (`CommandValues.StationLookup` → `_context.StationLookup()`).
   Assess Operators, Connected Stations, Local PTT On, Band Plans, Log
   Characteristics, Import/Export Log, LOTW Merge: wire only where a
   real implementation exists; otherwise leave the stub and list it in
   your report. **Do NOT touch the Hotkey Editor stub** — Track H
   replaces it with a new surface.
8. **Negative numbers in value fields**
   (`ValueFieldControl.xaml.cs:188`, `HandleNumberEntryKey`): accept
   `Key.OemMinus`/`Key.Subtract` — minus toggles the buffer's sign AND
   can START entry mode, both gated on `_min < 0` (non-negative fields
   reject with the error earcon + speech — no silent keystrokes). Speak
   "minus". Build with Track I's future decimal/dBm extension in mind
   (clean seam in the entry-mode code, no hardcoded integer assumption
   baked deeper than needed).
9. **FiltersDspControl RF Gain bounds** (`FiltersDspControl.xaml.cs:205`)
   hardcodes 0–50; wire to `rig.RFGainMin/RFGainMax/RFGainIncrement`
   like `ScreenFieldsPanel.xaml.cs:204` does. Audit `RadioNumberBox` for
   the same missing-minus gap as item 8.
10. **Optional:** NativeMenuBar guard to skip `RebuildCurrentMenu`
    during teardown (belt-and-suspenders from the 8/05 ActiveSlice
    sweep). Skip if risky.
11. **Stretch (attempt last; OK to report-only):** the connect
    double-beep is the signature sound and must fire on EVERY successful
    connect path — picker local, picker remote, auto-connect. The
    dispatch paths are NOT unified (four parallel paths). Audit each
    path; if a clean shared hook exists, wire it; if not, write up the
    audit in Design decisions for the orchestrator instead of forcing it.

## Ownership boundaries (do not cross)

- `NativeMenuBar.cs` is shared with Track I — you merge BEFORE I, so
  work freely but keep changes surgical. `RigSelectorDialog` is E's.
  SettingsDialog tabs are B/C/F's (the Start-fresh button lives in the
  account manager dialog, not SettingsDialog). Slice mapping internals
  are J's. `ValueFieldControl` decimal entry is I's future work — you do
  minus only.
- No new key bindings.

## Build & verify

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
powershell -Command "(Get-Item 'bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe').LastWriteTime"
```
Timestamp must be fresh after every build. Every change speaks; Escape
closes any dialog you add; errors never get suppress keys.

## Commit style

Commit after each work item: `QB Track A: <what changed>`. Push to
`origin` (never `upstream`). Report completion with your Design
decisions section.

## Design decisions (appended by QB Track A, 2026-08-07)

1. **Radio menu maintenance section (item 1).** Two entries only — Reboot
   Radio and Update Radio Firmware — in their own separated group above
   Exit. "Update Radio Firmware" opens Settings pre-selected on the Radio
   Setup tab, whose step 3 IS the existing firmware updater; no new
   firmware UI was built and no deep-link-to-groupbox mechanism invented
   (SelectTabByHeader is the existing deep-link granularity). No further
   maintenance candidates added: rename lands here via Track F, the Radio
   menu already carries Profiles/MultiFlex/auto-connect, and Track I's
   menu-parity audit is the right owner for anything else.
2. **Lineout gate + speech (item 2).** Gate removed. "Keep the speech
   honest" was interpreted as: these handlers were entirely silent (the
   old gate silently ate the keystroke, and even the working headphone
   pair said nothing), so all four headphone/lineout handlers now compute
   the clamped target locally and speak it ("Line out 45") — matching the
   menu AdjustValue pattern, and computing locally because the FlexBase
   setters are async-queued so an immediate read-back would speak the
   stale value. AudioGain handlers were left as-is (out of item scope).
3. **LocalAudioMute (item 3): killed.** No live caller anywhere; both
   references were already commented out. Also removed its private
   `maintainAudio` flag (write-only once the method died) and the
   empty-bodied `if` in the LineoutMute case that read it. A short
   comment marks the grave to stop future archaeology.
4. **PlayCwSK (item 4).** Removed the entire four-delegate PowerOn
   re-wire (AS/BT/SK/Mode), not just PlayCwSK — all four duplicated the
   ctor wiring, and the SK copy was the pre-BUG-061 gappy version that
   silently replaced the clean single-utterance one on every connect.
   The BT connected-prosign block stays (it fires a sound, it doesn't
   wire delegates). Close-path verification is by inspection:
   ApplicationEvents.vb:395 and FlexBase.cs:1530 both invoke the static
   delegate the ctor wired; no code path nulls it.
5. **Remote re-click (item 5).** The 2026-08-06 fix already shortened the
   wait to 2s-with-cached-fallback; this item's delta is IMMEDIATE
   satisfaction. Key call: the discriminator is whether the TLS session
   was already connected BEFORE this call (captured ahead of
   session.Connect()), not whether it is connected at wait time — a
   fresh session's one-and-only list is still in flight and deserves the
   full wait, while a pre-existing session's list arrived long ago and
   will never be re-sent. Unsolicited lists still land through the
   persistent WanRadioRadioListRecieved subscription (the 8/06
   refresh/morph flow), untouched. Failure classification untouched
   (Track D seam respected).
6. **Start Fresh with SmartLink (item 6).** Clears tokens for ALL
   accounts via a new `ResetAllSignIns()` on the SHARED manager (the
   2026-08-06 private-instance lesson), then routes through the existing
   NewLoginRequested door so the clean native sign-in and the follow-up
   list refresh reuse the tested loop. Deliberately does NOT delete the
   accounts file: with native sign-in, clearing tokens IS the complete
   reset (ratified in ResetAccountSignIn's design), and deleting would
   destroy per-account port/mode settings Don's and Noel's radios need.
   Per-account clearing already existed (Reset Sign-In), so "one or all"
   is covered by the pair. The button hides in the connect-flow picker
   (globals.vb) which doesn't wire the callback — the account MANAGER is
   its home per spec. Auto-offering Start Fresh after N consecutive auth
   failures: NOT built; recommend wiring a failure counter in setupRemote's
   ConnectFailed path once Track D's auth-vs-not classification lands,
   since counting non-auth failures would offer the wrong medicine.
7. **Stub audit (item 7).** Wired: Station Lookup (menu routes through
   ExecuteCommandCallback so menu/hotkey/Command Finder share one
   dispatch and its no-radio guard), Operators (new
   ShowOperatorsCallback → the VB Lister form over PersonalData — the
   same live surface first-run uses; ConfigEvent handles operator
   changes), Connected Stations (the never-referenced WPF
   ShowStationNamesDialog + FlexBase.Stations; empty list speaks instead
   of silently self-closing), Local PTT On (FlexBase.LocalPTT, a
   set-true-only claim — shown as a checked item, re-click speaks
   "already on"), Band Plans (the never-referenced WPF ShowBandsDialog +
   HamBands.Bands, mirroring the old ShowBands form's query logic;
   works with no radio; result text is plain lines, no tabs/columns).
   Left stubbed: Log Characteristics, Import Log, Export Log, LOTW Merge
   — the WinForms forms exist but their app-side wiring
   (LogCharacteristicsForHotkey etc.) is itself stubbed pending the
   logging-mode phase ("Phase 9.5"), so there is no working
   implementation to wire to. Manage CW Messages: not in my list, left
   alone. Hotkey Editor: untouched per Track H ownership. Honesty fix:
   AddNotImplemented used to speak "{item}, not yet connected to radio"
   — a lie that sent users hunting connection problems; it now says
   "{item} is not yet implemented in this version." (This changes what
   the remaining stubs, including Hotkey Editor until Track H lands,
   say — deliberate.)
8. **Minus entry (item 8).** Minus (unshifted OemMinus, or NumPad
   Subtract with or without shift — Shift+OemMinus is underscore and
   stays untouched) toggles the buffer sign and can start entry mode,
   both gated on `_min < 0`. Rejection on non-negative fields uses
   LeaderInvalidTone (the app's invalid-input earcon; no dedicated error
   earcon exists) + "{label} does not go below {min}". Toggle-off speaks
   "positive" so the state change is never silent. Track I seam: sign
   handling is its own `ToggleBufferSign()` step inside
   HandleNumberEntryKey, positioned as a "buffer edit" alongside future
   decimal-point handling; entry-start went through a new
   `BeginNumberEntry(firstKey)` that takes any starting key. A bare "-"
   confirmed with Enter parses invalid, hitting the existing
   "Invalid, cancelled" path.
9. **RF Gain bounds + RadioNumberBox (item 9).** Finding: FiltersDspControl
   (and RadioNumberBox, used only by it) is ORPHANED — no instantiation
   anywhere outside its own files; it is the Sprint 8 WPF replacement for
   Flex6300Filters that never got wired in. Defaults corrected to
   FlexBase's real defaults (-10 to +30, step 10) and a public
   `SetRFGainRange(min, max, increment)` seam added for whoever revives
   it (it cannot reference FlexBase itself — documented circular-ref
   rule). RadioNumberBox minus audit: NO minus gap — it is a plain
   TextBox, signed entry always worked. Real adjacent bug found and
   fixed: typed entries confirmed with Enter went to the rig UNCLAMPED
   (the "overshoots the ceiling" half of Noel's report); UpdateBoxAndRig
   now clamps to LowValue/HighValue, honoring the unlimited-high
   convention (HighValue at or below LowValue).
10. **Teardown guard (item 10): built, not skipped.** `_disposed` flag
    checked in ApplyUIMode (which RebuildCurrentMenu funnels through).
    Also unhooked ConnectionStateChanged in Dispose — it was subscribed
    alongside SliceCountChanged but never removed, a genuine leak that
    was itself a path to post-dispose rebuilds.
11. **Connect double-beep (item 11, stretch): wired, audit follows.**
    All four dispatch paths (picker local, picker remote, auto-connect,
    remote reconnect) converge on the rig power event, through
    MainWindow.PowerStatusHandler into PowerNowOn — the same reasoning
    that moved the BT prosign there ("the semantically correct moment:
    radio is up"). Today's sounds at that moment: Connecting-modal phase
    tones (skip any phase under 500ms, so fast local connects are
    silent) and the BT prosign (default-off CwNotificationsEnabled). So
    a default-config LAN connect completed in total silence. New
    `EarconPlayer.ConnectSuccessTone()` = the signature double-beep
    (same 750 Hz pitch and cadence as phase 2, slightly louder at 0.5)
    fires in PowerNowOn gated on the off-to-on transition so re-raised
    power events cannot double-fire. Accepted overlap: a slow remote
    connect can hear the phase-2 counting pair during progress AND the
    success pair at arrival — progress vs arrival, distinct moments;
    flagged for orchestrator review rather than inventing a new sound
    that would not be "the" signature.

## Needs Noel

- **Success-earcon shape:** the double-beep is implemented as the
  phase-2 750 Hz pair at slightly higher volume. If Noel wants a
  distinct arrival voice (for example a rising pair), it is a one-line
  change in ConnectSuccessTone.
- **Stub-speech wording:** remaining stubs now say "... is not yet
  implemented in this version." — confirm the phrasing suits the app's
  voice.
- **Start Fresh auto-offer:** deferred until Track D's ConnectFailed
  auth classification lands (see decision 6).
