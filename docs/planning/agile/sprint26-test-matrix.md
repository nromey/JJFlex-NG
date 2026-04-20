# Sprint 26 Test Matrix

Companion file to `sprint26-ragchew-keepalive-kerchunk.md`. Kept separate from the sprint plan so the checklist is easy to open during live testing without dragging in the full plan context.

**Status:** Tests listed, none yet executed. Execute once Sprint 26 + Sprint 27 work merges toward the 4.1.17 release.

**Tester:** Noel primary; Don (WA2IWC) for two-client MultiFlex cases (TM-M1 through TM-M5). Justin (AI5OS) optional for 8000-series / Aurora coverage.

**Hardware coverage:** Don has 6300 inshack over SmartLink. Noel has his own 6000-series and (boxed) 8600. Positive paths on 8000-series-only features defer to 8600 unbox (see memory `project_8600_unbox_firmware_trigger.md`).

## Execution flow

1. Work through TM-1..TM-9 (network + lifecycle + soak). These prove Phase 1-4 work.
2. Work through TM-M1..TM-M5 (MultiFlex). These prove Phase 2.5 work.
3. Work through TM-R1..TM-R6 (Sprint 25 regression pass). These prove nothing in the refactor disturbed shipped behavior.
4. Any fail → log the symptom in Agent.md; fix or defer before 4.1.17 cut.

Pass / fail / blocked notation: fill the `___` after each case.

---

## Network disruption cases

- **TM-1 Airplane mode toggle while connected.** Preconditions: connected to SmartLink, signal solid. Steps: toggle airplane mode on (Windows), wait 30s, toggle off. Expected: status announcement goes "Reconnecting (attempt N)" within ~1s of airplane-on, "Connected" within a few seconds of airplane-off. Screen reader hears both transitions. ___

- **TM-2 VPN disconnect mid-session.** Preconditions: connected to SmartLink through Tailscale. Steps: stop the Tailscale service, wait 60s, restart. Expected: same as TM-1 — clean reconnect, meaningful status announcement. ___

- **TM-3 `clumsy`-injected latency + packet loss.** Tool: [Clumsy](https://jagt.github.io/clumsy/). Steps: run Clumsy with filter `outbound and (tcp and tcp.DstPort == 443)`, enable 500ms lag and 5% drop. Observe for 5 minutes. Expected: session stays connected; SMeter / radio state updates may feel sluggish but no spurious drops. ___

- **TM-4 Token artificial invalidation.** Steps: connected state; edit the stored JWT in `%AppData%\JJFlexRadio\` to an invalid value; trigger a re-registration (disconnect/reconnect a radio). Expected: status announcement says "Authorization expired — please sign in again". Sign-in UI comes up. ___

## Lifecycle cases

- **TM-5 Sign-in/sign-out cycle, 10 iterations.** Steps: sign in, see radio list, sign out, repeat 10 times. Expected: every cycle completes without accumulating zombie threads (check Process Explorer — JJ Flex thread count before and after should be the same). ___

- **TM-6 Account switch via coordinator.** Preconditions: two SmartLink accounts saved. Steps: sign in to Account A, switch to Account B, switch back. Expected: only one session exists in the coordinator at a time (Shape-2 at N=1 behavior); status announcements reflect the switches. ___

- **TM-7 App shutdown while connected.** Steps: connect; close the app. Expected: app exits within 2 seconds. Process Explorer's orphan-thread check shows no lingering WanSessionOwner monitor. Trace file shows clean shutdown sequence with `[session=...] Dispose` and `monitor thread exit` lines. ___

- **TM-8 App shutdown while reconnecting.** Steps: induce a disconnect (airplane mode); while status shows "Reconnecting", close the app. Expected: app exits within 2 seconds; in-flight reconnect is cancelled cleanly; trace shows cancellation path. ___

## Soak test

- **TM-9 Overnight soak.** Procedure:
  1. Enable verbose tracing: set `BootTrace = True` in `globals.vb` OR enable via Operations → Tracing UI.
  2. Note the trace file baseline: open `%AppData%\JJFlexRadio\JJFlexRadioTrace.txt` and record its size + the last line's timestamp.
  3. Note memory baseline: Task Manager → Details tab → right-click JJFlexRadio.exe → "Working set (memory)". Record.
  4. Connect to SmartLink; leave the app running overnight (minimum 8 hours, ideally 12+).
  5. Next morning: check status announcement — expected "Connected" (or "Reconnecting" if the network blipped during the night, which is acceptable as long as the status is accurate).
  6. Check Task Manager memory — expected: within ~10% of baseline. Growth >10% suggests a leak.
  7. Open the trace file, scan for: unexpected Reconnecting cycles (count them), any Exception entries, any gaps >60s with no entries (suggests hang).
  8. Document findings below: number of reconnect cycles, any exceptions, memory delta. ___

## MultiFlex cases (BUG-062 regression cases)

Requires Don (WA2IWC) on his 6300 over SmartLink. Noel and Don both connect as MultiFlex clients to the same radio.

- **TM-M1 Two-client connect.** Steps: Don connects first; Noel connects second. Expected: both clients see each other in MultiFlex Clients dialog with correct owned-slice letters. Client list updates live when each joins (Symptom 3 prework fix). ___

- **TM-M2 Slice inventory accuracy.** Steps: Don creates a slice on his client; Noel checks MultiFlex Clients dialog. Expected: Noel sees Don's owned slice in the dialog within ~1s of creation. `NewSlice` capacity check on Noel's side reflects the new total slice count without stale-cache effects. (Symptom 2 — needs investigation, may defer to Phase 5.5 if fails.) ___

- **TM-M3 Connect timeout behavior.** Steps: simulate connect initiation under multi-client conditions (exact repro TBD during testing). Expected: if handshake stalls, the session monitor's backoff kicks in and Status transitions through Reconnecting. No silent 45-second hangs. (Symptom 4 — investigate.) ___

- **TM-M4 Kick permission.** Steps: Don (primary) clicks Disconnect on Noel's row; expected succeeds. Noel (guest) clicks Disconnect on Don's row; expected refused. (Symptom 5 — investigate.) ___

- **TM-M5 Callsign-correct announcements.** Steps: Don disconnects (either via kick or clean disconnect). Expected: Noel hears "Don" (or wa2iwc's configured Station) announce disconnect — NOT the wrong callsign. (Symptom 6 — snapshot-at-subscribe fix, should be clean.) ___

## Sprint 25 regression pass

These behaviors shipped in Sprint 25 and must still work after the Sprint 26 plumbing rewrite.

- **TM-R1 CW prosign bookending.** Cold launch → hear AS on "Connecting" speech. Connect completes → hear BT. Mode change (Alt+C from USB) → hear "CW" speech + Morse "CW" parallel. Alt+F4 exit → hear "73" or "73 de JJF" + SK prosign (callsign form depends on WPM). ___

- **TM-R2 Braille status line (Focus 40 or equivalent).** Tab to frequency field at home position — expect compact radio status on braille display (frequency + mode + S-meter). Tab away — braille yielded back to NVDA. Tab back — status resumes within ~1s. ___

- **TM-R3 NR gating on 6300 (via Don's radio).** Ctrl+J R / Ctrl+J S / Ctrl+J Shift+N each announce "not available on this radio." ANF (Ctrl+J A) works normally. DSP menu / ScreenFieldsPanel hide the three 8000-series-only items. ___

- **TM-R4 DSP refresh on mode change.** Enable Legacy NR on USB → change mode to CW → check DSP panel. NR state reflects correct on/off. This tests that the FlexBase DemodMode workaround survived the refactor. ___

- **TM-R5 RX audio pipeline (Phase 20 RNN + spectral sub).** On 8000-series hardware: verify Neural NR and Spectral NR each produce audible noise-floor reduction on an active voice slice. Deferred pending Noel's 8600 unbox or Justin's 8400 SmartLink access. ___

- **TM-R6 Mode-key deconfliction (Sprint 25 slip-in).** Alt+A = AM, Alt+F = FM, Alt+D = DIGU, Alt+Shift+D = DIGL all fire. Alt+O = Audio menu, Alt+E = Filter menu, Alt+Shift+X = DX Cluster. Unchanged: Alt+U/L/C = USB/LSB/CW, Alt+M / Alt+Shift+M cycle modes. ___

## Per-phase functional spot-checks

Each phase landed with specific deliverables. Quick sanity checks:

- **Phase 1 unit tests:** `dotnet test Radios.Tests/Radios.Tests.csproj` — all 14 pass in under 2s. ___

- **Phase 1 SmartLinkSessionHarness:** `dotnet run --project tools/SmartLinkSessionHarness/SmartLinkSessionHarness.csproj` — REPL prompts, `help` lists commands, `connect` + JWT walks through status transitions without crashing. ___

- **Phase 2 smoke:** sign in to SmartLink → see radio list → connect to radio → disconnect → sign out — indistinguishable from pre-refactor behavior. ___

- **Phase 3 announcements:** disconnect mid-session; "Reconnecting" announcement fires; reconnect; "Connected" announcement fires. Verbosity is Terse — brief, non-disruptive. ___

- **Phase 4 cleanup:** grep FlexBase.cs for `\bwan\.` and `PreserveWanForRetry` — expect zero hits. ___

---

## Open items surfaced during testing

*(Populate as the matrix runs. Each open item gets: TM reference, one-sentence symptom, disposition — fix now / defer to 4.1.17 cut / defer to later sprint.)*

- 
