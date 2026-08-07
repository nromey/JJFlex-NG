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
