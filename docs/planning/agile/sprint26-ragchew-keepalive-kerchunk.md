# Sprint 26 — WAN Session Owner Refactor (Connection Fix)

**Status:** Planning complete — ready for Phase 0 audits
**Created:** 2026-04-13 (investigation), 2026-04-14 (Shape 2 decision + audio UX + multi-radio context), 2026-04-15 (sprint plan finalized under max-effort reasoning)
**Authors:** Noel (architecture, root cause analysis, multi-radio vision), Claude (clean-room pattern translation, decomposition)
**Parent vision doc:** `docs/planning/hole-punch-lifeline-ragchew.md`
**Branch target:** `sprint26/connection-fix` (serial, one track, one CLI session — no parallel tracks)

---

## Sprint goal

Ship a reliable SmartLink session lifecycle. Users notice: fewer unexpected disconnects, meaningful status messages when things go wrong, automatic recovery without manual re-auth, no mystery "it just broke" errors. Developers notice: session ownership is no longer coupled to radio connections; the path to future multi-radio, tabbed UI, session-level panning, and eventual multi-process isolation is cleared by design.

This is a **foundation-setting sprint**. The visible user payoff is the reliability improvement; the invisible payoff is every subsequent sprint (Sprint 27 networking config, Sprint 28 tabbed multi-radio) builds on this without fighting the old ownership model.

---

## Context

See parent vision doc `docs/planning/hole-punch-lifeline-ragchew.md` for full root cause analysis and architectural rationale. In brief:

- FlexLib's `WanServer` represents a user's SmartLink session (auth, radio list, SSL to SmartLink backend). It should be long-lived across radio connections.
- Current JJ Flex treats it as a per-radio-connection resource (owned by `FlexBase`, torn down on every radio change).
- We also never subscribed to `WanServer.PropertyChanged("IsConnected")`, so silent session drops go unnoticed until the next user action.
- Together these produce the "intermittent SmartLink reliability" symptoms users report.

The fix is a dedicated session-owner thread that survives radio changes, watches for connection-state changes, and auto-reconnects with exponential backoff. But the Shape of that owner is informed by a broader product vision — see next section.

---

## Architecture — Shape 2 session model

**Decision (2026-04-14, under max-effort reasoning):** JJ Flex's eventual product is browser-style tabbed multi-radio sessions in one window. Audio UX carries session identity via stereo panning + gain ducking + smart-squelch-off-axis (accessibility-critical for blind operators). Eventual per-process isolation is on the roadmap.

That vision ruled out the simpler Shape 1 ("singleton session owner, switch accounts") because hardcoding a singleton now would force a painful migration later when concurrent multi-session lands. Shape 2 is the target:

### Core classes and interfaces

**`IWanServer`** — our abstraction over FlexLib's `WanServer`. Every call surface FlexBase reaches for goes through this interface. Buys us:
- Testability (mock implementations for unit tests of the session owner's state machine).
- Version insulation (future FlexLib updates don't break us at the API level).
- Protocol safety (our `SemaphoreSlim` for call serialization lives here).
- Tracing layer (the adapter traces every call before forwarding).

**`IWanSessionOwner`** — the session contract. Exposes:
- State: `IsConnected`, `Status`, `LastError`, `ReconnectAttemptCount`, `AccountId`, `SessionId`.
- Commands: `Connect()`, `Disconnect()`, `Reset()`.
- Data: `AvailableRadios`, audio sink access (`AudioSink` of type `ISessionAudioSink`).
- Events: state changes, signal-strength threshold crossings.

**`WanSessionOwner : IWanSessionOwner`** — concrete implementation. Owns an `IWanServer` (via `WanServerAdapter`). Owns a dedicated monitor thread implementing the behavioral spec from the vision doc. Subscribes to `IsConnected` PropertyChanged. Emits structured trace lines with session ID prefix on every state transition.

**`SmartLinkSessionCoordinator`** — top-level manager of sessions. Holds `Dictionary<SessionId, IWanSessionOwner>` plus `ActiveSessionId`. Exposes `ActiveSession` (the currently-focused session or null) and `AllSessions` (the full collection). At Sprint 26 runtime the dictionary only ever has 0 or 1 entries; in Sprint 28+ it grows as users add tabs.

**`ISessionAudioSink`** — the audio output primitive. Properties: `Pan` (stereo position −1..+1), `Gain` (linear multiplier), `Muted` (bool), `IsFocused` (bool). Method: `Write(samples)`. Sprint 26 implementation (`DirectPassthroughSink`) ignores all properties and writes samples to the default audio output. The interface carries enough primitives for all five future audio behaviors (auto-pan on focus, manual pan, mute, smart-squelch-off-axis, multi-device routing) so none of them requires an interface change in Sprint 28+.

### Three design disciplines (enforced from Sprint 26 onward)

These are not features — they're code-review rules that apply everywhere session-aware code is written.

**D1. No shared mutable state between sessions.** No `static` fields carrying per-session data. All cross-session communication flows through the coordinator. Grep-clean rule at sprint end: `static` fields holding mutable state that a session touches do not exist outside of fundamentally-global things (logging sinks, config). This is the single biggest preparation for future multi-process, because shared mutable state is exactly what breaks across process boundaries.

**D2. Session audio output is an injected `ISessionAudioSink`, not a push to a global output.** Each `WanSessionOwner` receives its sink at construction (or retrieves it from the coordinator's audio-factory). No direct calls to `PortAudio.DefaultOutput` or equivalent from session code. This is the seam for future per-session panning, volume ducking, multi-device routing, AND future cross-process audio IPC.

**D3. Per-session tracing with session ID prefix.** Every trace line from within session-aware code includes `[session=<id>]` as a structured field or prefix. At N=1 this looks redundant; at N=3 you'll bless this rule; at multi-process you'll bless it twice. Tracing discipline is a Phase 1 deliverable, not an afterthought.

**D4. FlexBase and other consumers access sessions only through `coordinator.ActiveSession`.** Never capture an `IWanSessionOwner` reference into a field. The overhead of a dictionary lookup (50ns) is nil; the cost of a stale captured reference (when future tab-switching rebinds the active session) is enormous. Re-accessing every time is correct; caching is a review-blocker.

### Concrete `IWanServer` surface (derived from FlexBase's actual usage)

A grep of `Radios/FlexBase.cs` against `WanServer`, `wan\.`, and related patterns enumerated exactly what FlexBase reaches into on `WanServer`. The `IWanServer` interface must expose these and only these — anything else is a different abstraction layer's concern:

**Properties:**
- `IsConnected` (bool, read-only, raises `PropertyChanged` on change)

**Methods:**
- `Connect()` — initiate SmartLink session connect (`FlexBase.cs:1522`)
- `Disconnect()` — tear down session cleanly (`FlexBase.cs:916, 1512, 1721`)
- `SendRegisterApplicationMessageToServer(string programName, string platform, string jwt)` — register app with SmartLink backend after auth (`FlexBase.cs:1533, 1610`)
- `SendConnectMessageToRadio(string serial, int flags)` — ask SmartLink to broker a radio connection (`FlexBase.cs:1624`). The `0` flags parameter should be preserved in our interface with a clear name or default.

**Events:**
- `WanRadioConnectReady` (handle + serial) — fired when SmartLink has brokered a radio connection (`FlexBase.cs:1518`)
- `WanApplicationRegistrationInvalid` — fired when app registration is rejected (bad JWT, etc.) (`FlexBase.cs:1519`)
- `WanRadioRadioListReceived` (List<Radio>) — fired when SmartLink sends the available-radios list. **Note:** in FlexLib this is currently a `static` event named `WanRadioRadioListRecieved` (misspelled "Recieved"). Our `IWanServer` interface spells it correctly; the `WanServerAdapter` handles the typo at the FlexLib boundary. This is a good example of the adapter's version-insulation benefit.

**What FlexBase uses that is NOT on `IWanServer`:**

- `wanConnectionHandle` — FlexBase's own string field, not WanServer's. Stays on FlexBase (or moves to radio-connection scope).
- `PreserveWanForRetry()` / `RestoreWanFromRetry(WanServer)` — Sprint 25 retry band-aids (`FlexBase.cs:1041, 1051`). **These are Phase 4 cleanup targets** — they exist because WanServer ownership was wrong; once ownership is correct, they're unnecessary.

This minimal surface is the entire integration contract. A `MockWanServer` implementation for unit tests needs to simulate only these five method calls and three events.

---

## Phase 0 — Audit spikes

Small, bounded investigations with one-paragraph findings each. None produces production code. Findings land in this file under "Phase 0 Findings" once runs complete.

### 0.1 — Thread-safety audit of FlexLib's `WanServer`

**Query:** grep `FlexLib_API/FlexLib/WanServer.cs` and adjacent files for `lock (`, `Monitor.Enter`, `SemaphoreSlim`, `Interlocked`, `[MethodImpl(MethodImplOptions.Synchronized)]`. Read `Connect()`, `Disconnect()`, `SendCommand()` (or equivalent entry points) for guard patterns. Check how `PropertyChanged` is raised (synchronous on mutator thread? dispatched?).

**Output:** one paragraph stating what we found AND restating the architectural commitment (always lock at our boundary via `WanServerAdapter`'s `SemaphoreSlim`, regardless of what FlexLib does internally — the lock is cheap and buys us protocol serialization even if data races are already handled).

**Time:** ~30 minutes.

### 0.2 — Enumerate + classify Sprint 25 retry code

**Query:** grep `Radios/`, `FlexLib_API/FlexLib/` wrappers, and call sites for `RetryConnect`, `_reconnectTimer`, `Reconnect*`, `Task.Run.*Connect`, `StartReconnect`. For every hit produce a bulleted line: file, line, one-sentence description of behavior, classification as **session-level / radio-level / ambiguous**, and proposed Phase 4 disposition (delete / keep / discuss).

**Output:** bulleted list (no tables — screen-reader friendly), one bullet per hit.

**Time:** ~45 minutes.

### 0.3 — NetworkTest method signature (optional / deferrable to Sprint 27 Phase 0)

**Query:** read the FlexLib `NetworkTest` declaration. Is it synchronous-blocking, `Task`-returning, or event-raising on completion? Does it take a timeout? What does it actually test?

**Output:** one sentence describing the shape, plus a note on where the eventual invocation should live (monitor thread? Task.Run from UI?). Affects Sprint 27 architecture more than Sprint 26; can be done now or deferred.

**Time:** ~10 minutes if done.

### Phase 0 exit criterion

Three paragraphs written into this document under "Phase 0 Findings" (below). Noel has scanned them. No code has changed. Phase 1 commences.

### Findings-induced replanning protocol

Phase 0 is information-gathering. Most of the time it will confirm the planned Phase 1–5 shape and we proceed. But Phase 0 exists because we cannot rule out the possibility of a finding that **invalidates a later-phase decision**. Examples of findings that would trigger replanning:

- **0.1 thread-safety surprise**: FlexLib calls `PropertyChanged` synchronously on the mutator thread, and our event handler blocks, which could deadlock with our `SemaphoreSlim`. The "always lock" commitment still holds, but the *shape* of locking (reentrant? queued dispatch?) may need adjustment before Phase 1 builds on it.
- **0.2 ambiguous retry hit**: a Sprint 25 retry site touches both session and radio concerns in a way that neither "session-level" nor "radio-level" cleanly describes. Phase 4 cannot execute against an ambiguity.
- **0.3 NetworkTest shape surprise**: `NetworkTest` is fully synchronous and blocks for 30+ seconds. Sprint 27 Track C's architecture would need redesign around background invocation.

**Protocol when a finding invalidates a plan decision:**

1. **Stop Phase 1 work immediately.** Do not build on a foundation the finding has shifted.
2. **Write the finding into Phase 0 Findings** with the full details, not just the one-paragraph summary.
3. **Update the relevant plan section** — typically Phase 1 design, a non-goal, or an exit criterion.
4. **Add a Living Decisions Log entry** in `hole-punch-lifeline-ragchew.md` explaining the reversal: what we thought, what we found, what we're doing instead.
5. **Resume only after the plan reflects the new reality.** Do not patch around findings in code; patch the plan first, then execute against the patched plan.

This protocol costs ~1 hour if invoked (maybe zero times, maybe once this sprint). It saves the days-to-weeks of "why is the code fighting us" debugging that happens when teams execute against invalidated plans.

---

## Phase 1 — `WanSessionOwner` in isolation

Largest phase. All new scaffolding gets built; **nothing in the rest of the codebase uses it yet.** FlexBase still holds its private `WanServer` field; SmartLink still works exactly as today from the user's perspective. This phase proves the new architecture on its own before any migration risk touches production paths.

### Phase 1 deliverables

**Interfaces (new folder — candidate: `Radios/SmartLink/` or `JJFlexWpf/SmartLink/`):**

- `IWanServer`
- `IWanSessionOwner`
- `ISessionAudioSink`

**Implementations:**

- `WanServerAdapter : IWanServer` — thin shell that forwards to FlexLib's `WanServer`. Holds the `SemaphoreSlim` that serializes all calls. Also the natural home for call-level tracing.
- `WanSessionOwner : IWanSessionOwner` — owns the `IWanServer`, owns the monitor thread, implements behavioral spec (backoff, AutoResetEvent, signal-triggered wake, clean shutdown). Emits structured traces on every state transition.
- `SmartLinkSessionCoordinator` — holds `Dictionary<SessionId, IWanSessionOwner>`, exposes `ActiveSession` and account-switch semantics.
- `DirectPassthroughSink : ISessionAudioSink` — Sprint 26 audio stub. Ignores Pan/Gain/Muted/IsFocused; writes to default output.

**Test scaffolding:**

- `MockWanServer : IWanServer` in a test project — simulates `IsConnected` toggles, forced disconnects, exceptions on `Connect()`, injected delays, event sequencing.
- Unit tests of `WanSessionOwner` against `MockWanServer`. **Concrete test cases** (each is a named test method):
  - `Connect_Succeeds_EmitsConnectedState` — mock Connect returns without throwing, IsConnected goes true → session reports Status=Connected, fires event.
  - `Connect_Throws_EntersBackoff_AttemptsAgainAfter1s` — mock Connect throws, session waits 1s, retries; verify backoff index is 0 → 1s.
  - `Connect_ThrowsRepeatedly_BackoffProgresses_1s_5s_30s_30s` — verify exponential backoff schedule matches spec, caps at 30s.
  - `Connected_Then_IsConnectedGoesFalse_TriggersImmediateReconnect` — establish connection, flip IsConnected via mock → AutoResetEvent fires, session attempts reconnect within a tight tolerance.
  - `Connected_Then_ExplicitDisconnect_DoesNotTriggerReconnect` — establish, call session.Disconnect(), verify session does NOT loop back to reconnect logic.
  - `Shutdown_While_Connected_CleanlyExitsThread` — session is connected and sleeping, call Dispose/Shutdown, monitor thread exits within 1s, no thread-still-alive leak.
  - `Shutdown_While_InBackoff_CleanlyExitsThread` — session is mid-backoff wait, call Shutdown, thread wakes and exits cleanly without completing the backoff wait.
  - `Shutdown_While_ConnectInFlight_CleanlyExitsThread` — mock Connect is blocked (injected delay), call Shutdown during it, verify thread exits cleanly when Connect completes.
  - `IsConnectedRace_DoesNotCauseDoubleConnect` — IsConnected flips false→true→false in rapid succession; session doesn't try to Connect twice.
  - `SemaphoreSlim_SerializesConcurrentCalls` — two threads call session operations simultaneously; verify they serialize via the WanServerAdapter's semaphore (no interleaving).
  - `Status_Property_ReflectsStateTransitions` — verify `Status` enum goes through the expected states: `Disconnected → Connecting → Connected → Reconnecting → Connected → Disconnected` across an induced drop cycle.
  - `LastError_Populated_OnConnectFailure` — mock Connect throws with specific exception; session's `LastError` field holds it for UI consumption.
  - `ReconnectAttemptCount_IncrementsPerRetry_ResetsOnSuccess` — verify counter math matches UI expectations.

Total: 13 unit tests, all runnable offline against the mock, all sub-second. They exercise the concurrency behavior of the monitor thread specifically.

**Integration harness:**

- **Location:** a small console app project alongside the main solution. Candidate name: `SmartLinkSessionHarness` in its own `.csproj` under a `harness\` or `tools\` folder. Builds to `harness\bin\<platform>\<config>\SmartLinkSessionHarness.exe`.
- **Shape:** constructs a real `WanServerAdapter` wrapping an actual FlexLib `WanServer`, a real `WanSessionOwner`, a real `SmartLinkSessionCoordinator`, and prompts the user for SmartLink credentials (command line args or interactive prompt).
- **Interactive commands:** the harness exposes a simple REPL: `connect`, `disconnect`, `status`, `drop` (simulate drop by forcing IsConnected false), `shutdown`, `list` (show AvailableRadios). Each command exercises one path in the session owner.
- **Used for:** proving end-to-end correctness against real SmartLink before Phase 2 migration touches FlexBase. Also useful later as a diagnostic tool if we need to reproduce a field issue without the JJ Flex UI in the way.
- **Not shipped** — harness stays in the repo but is not bundled in the installer. A `.gitignore` exception; not a production artifact.

**Signal-strength observable:**

- Session wraps FlexLib's `Slice.SMeter` (or equivalent) with a threshold-crossing detector. Emits events when signal crosses a configurable threshold. Sprint 26 doesn't use these events for anything; Sprint 28's mixer will subscribe to them for smart-squelch-off-axis.

### Phase 1 exit criterion

All interfaces defined, all implementations land in new files under the new folder, unit tests pass, integration harness demonstrates connect/disconnect/reconnect works in isolation against real SmartLink, structured traces visible in the trace file for every expected event. FlexBase and production paths are untouched.

---

## Phase 2 — FlexBase migration

Where production paths migrate. Contained refactor — touches `FlexBase.cs` and a handful of call sites, nothing else.

### Phase 2 deliverables

- Delete `private WanServer wan;` from `FlexBase` (line 1033).
- Delete `private string wanConnectionHandle;` OR relocate to radio-connection scope if it's still needed at that level (line 1034). Decide per audit during Phase 2.
- Delete `private bool WanRadioConnectReadyReceived` state field — the coordinator/session owner handles this now (line 1035).
- Add coordinator reference (constructor injection or a service-locator pattern — consistent with existing JJ Flex patterns; see Q26.1).
- Rewrite the SmartLink sign-in flow to ask the coordinator: "create a session for this account, activate it." Coordinator handles session construction; FlexBase just reads state afterward.
- Rewrite radio-list access to read from `coordinator.ActiveSession.AvailableRadios`.
- Wire audio output: when FlexBase has samples for the current radio's slices, it writes to `coordinator.ActiveSession.AudioSink.Write(samples)` (which is the direct-passthrough sink in Sprint 26).
- Delete defensive `wan = null` hygiene code in `Dispose` — no longer needed; coordinator owns session lifecycle, not FlexBase.
- Apply discipline **D4**: verify no captured references to `IWanSessionOwner` exist. Every access goes through `coordinator.ActiveSession`.

### Concrete FlexBase migration map (targets, line refs in current code)

Each row is a before/after. Line numbers are from `Radios/FlexBase.cs` as of 2026-04-15; they may shift during Phase 2, but these are the landmarks to start from.

**Field deletions (lines 1033–1035):**
- `private WanServer wan;` → removed; access via `coordinator.ActiveSession` per D4.
- `private string wanConnectionHandle;` → removed if truly FlexBase-scoped radio-connection handle; relocate to the right scope during Phase 2.
- `private bool WanRadioConnectReadyReceived` → removed; the session owner's `WanRadioConnectReady` event is consumed by whichever code path currently needs it, without a boolean flag.

**`ConnectToSmartLink` method (around line 1511–1533):**
- Before: creates a new `WanServer`, subscribes events, calls `Connect()`, sends register message, subscribes to static radio-list event.
- After: `await coordinator.EnsureSessionForAccount(accountId)`, which idempotently creates or activates a session owner for that account. If the session is already connected, no-op. If not, session owner's monitor thread handles the connect + retries. Subscription to `WanRadioRadioListReceived` moves into the session owner (it owns the event wiring); FlexBase consumes `coordinator.ActiveSession.AvailableRadios` as an observable property or listens to a session-owner-exposed event.
- `wan.SendRegisterApplicationMessageToServer(programName, platform, jwt)` (line 1533) → moves into the session owner's post-connect hook. FlexBase doesn't call this directly anymore.

**Re-registration after auth refresh (around line 1608–1610):**
- Before: checks `wan != null && wan.IsConnected`, calls `SendRegisterApplicationMessageToServer`.
- After: `if (coordinator.ActiveSession?.IsConnected == true) coordinator.ActiveSession.ReRegister(jwt);` where `ReRegister` is a session-owner method that encapsulates the `SendRegister...` call behind our abstraction.

**Connect to specific radio (around line 1622–1625):**
- Before: sets `WanRadioConnectReadyReceived = false`, calls `wan.SendConnectMessageToRadio(r.Serial, 0)`, awaits the flag to flip via the handler.
- After: `await coordinator.ActiveSession.ConnectToRadio(r.Serial)` — the session owner exposes a `ConnectToRadio(serial)` method that internally calls the adapter's `SendConnectMessageToRadio` and waits for `WanRadioConnectReady` with timeout. The flag-and-poll pattern goes away; awaiting a Task<bool> or similar is cleaner.

**Disconnect sites (lines 916–917, 1512, 1721–1722, 6696):**
- Before: `wan.Disconnect(); wan = null;`
- After: most of these go away entirely. FlexBase doesn't own the session lifecycle anymore. The one case that still matters is "user explicitly signs out of SmartLink" — which calls `coordinator.DisconnectSession(sessionId)` or similar, not a FlexBase-local disposal.

**`PreserveWanForRetry` / `RestoreWanFromRetry` (lines 1041–1053):**
- Before: Sprint 25 band-aid methods that preserve the WanServer across a `FlexBase` teardown.
- After: **DELETE IN PHASE 4.** Once ownership is correct, these are unnecessary. Flagged in Phase 0.2 enumeration as session-level retry code.

**Final `wan = null` at line 6696:**
- Also a Sprint 25 band-aid. Phase 4 deletes.

### Phase 2 commit strategy

Suggested commit boundaries (each commit independently bisectable):

1. "Sprint 26 Phase 2: Add coordinator reference to FlexBase, no behavior change yet" — wire the reference without using it; compile-clean.
2. "Sprint 26 Phase 2: Migrate ConnectToSmartLink to coordinator" — the main method flips; old `wan` field is still present but unused.
3. "Sprint 26 Phase 2: Migrate radio list reads to coordinator.ActiveSession" — separate because it touches different call sites.
4. "Sprint 26 Phase 2: Migrate ConnectToRadio through session owner" — the Task-based refactor.
5. "Sprint 26 Phase 2: Remove unused wan field from FlexBase" — the deletion commit. Safe now because nothing references the field.

Each commit passes the existing smoke test. Bisect-friendly if something regresses.

### Phase 2 exit criterion

- User-facing smoke test: sign in to SmartLink, see radio list, connect to a radio, disconnect, sign out — indistinguishable from pre-refactor behavior.
- No regression in existing integration tests (if any).
- A reflection or grep check confirms no `WanServer` instance is created by `FlexBase` directly.
- Sprint 25 retry code is **still present** (not deleted yet — that's Phase 4).

---

## Phase 3 — Intelligent disconnect diagnosis UI (basic)

The user-visible payoff. This lands the "minimal rich" version of disconnect diagnosis; Sprint 27 Track D will extend it with NetworkTest-informed richer messages.

### Phase 3 deliverables

- Status bar (or equivalent surface) component that binds to `IWanSessionOwner.Status`, `LastError`, `ReconnectAttemptCount`.
- Human-readable messages for basic state transitions: "Connected", "Reconnecting (attempt 2)", "Authorization expired — please sign in again", "Connection dropped — attempting automatic reconnection".
- Message dictionary / switch in a single file (not scattered). Future Sprint 27 Track D extends this dictionary with NetworkTest-informed messages.
- Screen-reader announcement via live-region pattern (consistent with existing JJ Flex practice) when status changes.
- Session owner's `Status` property is an enum (not a raw string) so messages are decoupled from state transitions.

### Phase 3 exit criterion

- Induce a disconnect via airplane mode toggle: status bar shows "Reconnecting…" then "Connected" when network returns. Screen reader announces both transitions.
- Artificially invalidate token (local config edit): status shows "Authorization expired — please sign in again." Click sign-in, re-auth works.
- No generic "disconnected" messages remain in the SmartLink flow.

---

## Phase 4 — Retire Sprint 25 retry band-aids

Open the Phase 0.2 findings list. Walk down each bullet. Execute the disposition. Verify no regressions.

### Phase 4 deliverables

- For each **session-level** Phase 0.2 hit: delete the code from its original site.
- For each **radio-level** hit: leave it (legitimate separate concern — radio-level reconnect is not SmartLink-session reconnect).
- For **ambiguous** hits: classify now that the architecture is in place. Act on the classification.
- Re-run Phase 2's smoke tests to confirm no regressions.

### Phase 4 exit criterion

The codebase has exactly one session-level retry mechanism: `WanSessionOwner`'s monitor thread. Sprint 25's session-level band-aids are gone. Radio-level retry (if any existed) is intact. All Phase 2 tests still pass.

---

## Phase 5 — Soak test + test matrix

Ship with confidence by running the finished work against the real world.

### Phase 5 deliverables

Write `docs/planning/agile/sprint26-test-matrix.md` (new file) with manual test cases. Each case has: preconditions, steps, expected result, actual result (filled after run), pass/fail. Cases to include:

**Network disruption cases:**
- **TM-1 Airplane mode toggle while connected.** Preconditions: connected to SmartLink, signal solid. Steps: toggle airplane mode on (Windows), wait 30s, toggle off. Expected: status goes "Reconnecting (attempt N)" within ~1s of airplane-on, "Connected" within a few seconds of airplane-off. Screen reader announces both transitions.
- **TM-2 VPN disconnect mid-session.** Preconditions: connected to SmartLink through a VPN (Tailscale or similar). Steps: stop the VPN service, wait 60s, restart the VPN. Expected: same as TM-1 — clean reconnect, meaningful status.
- **TM-3 `clumsy`-injected latency and packet loss.** Tool: [Clumsy](https://jagt.github.io/clumsy/) (Windows packet shaper). Steps: run Clumsy with filter `outbound and (tcp and tcp.DstPort == 443)` (SmartLink uses HTTPS), enable lag with 500ms and drop with 5%. Observe for 5 minutes. Expected: session stays connected; SMeter / radio state updates may feel sluggish but no spurious drops.
- **TM-4 Token artificial invalidation.** Steps: connected state; edit the stored JWT in `%AppData%\JJFlexRadio\` to an invalid value; trigger a re-registration (disconnect/reconnect a radio). Expected: status shows "Authorization expired — please sign in again," sign-in UI comes up.

**Lifecycle cases:**
- **TM-5 Sign-in/sign-out cycle, 10 iterations.** Steps: sign in, see radio list, sign out, repeat 10x. Expected: every cycle completes without accumulating zombie threads (check Process Explorer for JJ Flex thread count before and after — should be the same).
- **TM-6 Account switch via coordinator.** Steps: sign in to Account A, switch to Account B, switch back. Expected: coordinator's `ActiveSessionId` reflects the switches; only one session exists at a time (Shape 2 at N=1 behavior).
- **TM-7 App shutdown while connected.** Steps: connect, close the app. Expected: app exits within 2 seconds; no hanging thread reported by Process Explorer's orphan-check; trace file shows clean shutdown sequence with `Disposing session` lines.
- **TM-8 App shutdown while reconnecting.** Steps: induce a disconnect (airplane mode), while status shows "Reconnecting", close the app. Expected: app exits within 2 seconds; in-flight reconnect is cleanly cancelled; trace shows cancellation path.

**Soak test:**
- **TM-9 Overnight soak.** Procedure:
  1. Enable verbose tracing: set `BootTrace = True` in `globals.vb` OR enable via Operations → Tracing UI (`TraceAdmin.vb`).
  2. Note the trace file baseline: open `%AppData%\JJFlexRadio\JJFlexRadioTrace.txt` and record its size + the last line's timestamp.
  3. Note memory baseline: Task Manager → Details tab → right-click JJFlexRadio.exe → "Working set (memory)." Record the value.
  4. Connect to SmartLink; leave the app running overnight (minimum 8 hours, ideally 12+).
  5. Next morning: check status bar — expected: "Connected" (or "Reconnecting" if the network blipped during the night, which is acceptable as long as the status is accurate).
  6. Check Task Manager memory — expected: within ~10% of baseline (small growth is normal for any .NET app; >10% growth suggests a leak).
  7. Open the trace file, scan for: unexpected `Reconnecting` cycles (how many?), any `Exception` entries, any gaps >60s with no entries (suggests hang).
  8. Document findings in the test matrix: number of reconnect cycles, any exceptions, memory delta.

Execute TM-1 through TM-8 as manual cases, recording pass/fail in the matrix. TM-9 runs overnight and is reviewed next morning.

### Phase 5 exit criterion

- Test matrix `sprint26-test-matrix.md` is green (TM-1 through TM-8 all pass).
- Soak test TM-9 passes: app still connected next morning; reconnect cycles explicable (zero or tied to documented network events); no exceptions in trace; memory within 10% of baseline.
- Sprint 26 is shippable — merge `sprint26/connection-fix` to `main` via a `--no-ff` merge commit with a summary of what changed and any notable soak observations.

---

## Commit strategy (sprint-wide)

Sprint 26 is serial and single-track, so commits are on `sprint26/connection-fix` branch directly. Per CLAUDE.md: commit after each phase completes, or after each significant chunk within a phase. No need for a PR on this branch; merge to main happens after Phase 5 soak test passes.

**Phase-level commit targets:**

- **Phase 0 end:** one commit, "Sprint 26 Phase 0: Audit findings" — updates this plan doc with the findings paragraphs, no code change.
- **Phase 1 end:** multiple commits as the code lands incrementally:
  - "Sprint 26 Phase 1: Define IWanServer, IWanSessionOwner, ISessionAudioSink interfaces"
  - "Sprint 26 Phase 1: Implement WanServerAdapter with SemaphoreSlim and tracing"
  - "Sprint 26 Phase 1: Implement WanSessionOwner monitor thread and state machine"
  - "Sprint 26 Phase 1: Implement SmartLinkSessionCoordinator"
  - "Sprint 26 Phase 1: Implement DirectPassthroughSink audio stub"
  - "Sprint 26 Phase 1: Add MockWanServer + 13 unit tests covering state machine"
  - "Sprint 26 Phase 1: Add SmartLinkSessionHarness console harness for integration testing"
- **Phase 2 end:** 5 commits per the migration map above, each bisect-safe.
- **Phase 3 end:** 2–3 commits:
  - "Sprint 26 Phase 3: Add status-bar binding to IWanSessionOwner state"
  - "Sprint 26 Phase 3: Add message dictionary for state transitions"
  - "Sprint 26 Phase 3: Live-region screen-reader announcements on state change"
- **Phase 4 end:** 1 commit per session-level retry site deleted, grouped by file if sites are adjacent. "Sprint 26 Phase 4: Remove session-level retry band-aids from <file>"
- **Phase 5 end:** 1 commit, "Sprint 26 Phase 5: Test matrix green; soak test passed overnight" — updates the test-matrix file; no code.

**Don't batch commits across phases.** Phase boundaries are verification boundaries; a commit that crosses them is harder to review and harder to bisect.

## Rollback strategy

If Sprint 26 hits trouble that can't be resolved in-session:

**Within a phase:** `git reset --hard` to the last phase-end commit. Lose in-flight work; regain a known-good state.

**Between phases:** the phase-end commits are each a stable checkpoint. `git checkout <phase-end-sha>` recovers. No phase depends on a future phase's code, so reverting Phase N+1 doesn't break Phase N.

**After merge to main (catastrophic case):** revert the entire Sprint 26 merge commit. Because Sprint 26's code is additive (new files under `Radios/SmartLink/` or `JJFlexWpf/SmartLink/`) plus a contained FlexBase migration, the revert is clean. The `WanServer` field being *removed* from FlexBase is the one thing to pay attention to — the revert restores it, and Sprint 25's retry band-aids (which were deleted in Phase 4) also come back. That's exactly what we want in a rollback.

**Soak-test failure without clear cause:** re-run the soak from a clean JJ Flex instance with elevated trace verbosity. Phase 1's structured tracing makes post-hoc forensics possible; if traces don't show the issue, the trace layer itself needs to be enriched before another soak run.

## Non-goals for Sprint 26

**NG-1:** Do not break existing SmartLink flow on rollout. User-perceived regression is a ship-blocker.

**NG-2:** Do not implement port config UI. (Sprint 27 Track A.)

**NG-3:** Do not implement NetworkTest integration beyond a one-line Phase 0.3 signature check. (Sprint 27 Track C.)

**NG-4:** Do not build the tab strip UI. Sprint 26 maintains single-radio, single-session UI. (Sprint 28+.)

**NG-5:** Do not implement `SessionMixer` or any audio policy (pan, gain, mute, auto-duck, smart-squelch). The `ISessionAudioSink` interface exists with the five properties; the only implementation is `DirectPassthroughSink` which ignores all properties. (Sprint 28+.)

**NG-6:** Do not implement multi-process isolation. The three disciplines prepare for it; no child-process code exists in Sprint 26. (Aspirational, Sprint 30+.)

**NG-7:** Do not implement multi-device audio routing. Each session writes to one default output via `DirectPassthroughSink`. (Sprint 28+.)

**NG-8:** Do not run Phase 4 deletions before Phase 2 integration is proven. Sprint 25's retry band-aids stay in place until the new architecture demonstrably works end-to-end. Deleting early means no rollback path if a bug surfaces.

**NG-9:** Do not capture `IWanSessionOwner` references in consumers. Every access goes through `coordinator.ActiveSession`. Code caching an owner reference into a field is a review-blocker.

**NG-10:** Do not refactor non-SmartLink connection paths. Direct-IP and local-network radio connections already work and are out of scope.

**NG-11:** Do not retroactively update `CLAUDE.md`'s Release Process inside Sprint 26. The version-bump / NAS-publish changes from 2026-04-14 land in `CLAUDE.md` as a separate, intentional refresh.

---

## Open questions for Sprint 26

These are design decisions likely resolved in-flight during Phase 1, flagged here so they aren't surprises.

**Q26.1 — Coordinator construction pattern.** Is `SmartLinkSessionCoordinator` a singleton (service-locator style), a DI-injected dependency, or a property on the main app shell? Existing JJ Flex patterns vary — pick one consistent with where FlexBase currently gets dependencies. Likely answered by reading `ApplicationEvents.vb` and following the path.

**Q26.2 — Shutdown ordering during app exit.** When the app closes, the coordinator's sessions each need to cleanly shut down their monitor threads before `WanServer` dispose. Who drives this? Probably the main shutdown handler, but sequencing with other app-exit work (save state, flush logs) needs a concrete order.

**Q26.3 — Backoff schedule constants.** Vision doc says `1s → 5s → 30s, capped at 30s`. Is that aggressive enough for SmartLink? Too aggressive? Configure-before-ship vs ship-and-tune. Recommendation: ship with these values, make them internal-configurable for Phase 5 soak tuning, expose to user config in a later sprint if needed.

**Q26.4 — Structured trace format.** Plain text with `[session=abc123]` prefix? Structured (JSON)? The existing trace format in `JJTrace` is the answer — match it. If it's not structured, the prefix approach works fine.

**Q26.5 — Signal-strength observable granularity.** Per-slice or per-session? FlexLib exposes SMeter per-slice. For Sprint 28's smart-squelch-off-axis, per-slice is probably right (so a slice on radio A can alert while radio B stays quiet). Sprint 26 wraps but doesn't use — pick per-slice for future flexibility.

**Q26.6 — `SessionId` format.** GUID, sequence number, account-derived hash? Consistent with existing JJ Flex ID patterns. Probably GUID.

---

## Phase 0 Findings

*(Populated during Phase 0 execution. Currently empty.)*

### 0.1 Thread-safety audit results

*(To be filled after audit runs.)*

### 0.2 Sprint 25 retry code enumeration

*(To be filled after audit runs. Format: bulleted list, one bullet per hit.)*

### 0.3 NetworkTest signature

*(Optional; to be filled if audit runs, otherwise deferred to Sprint 27.)*

---

## Sprint 26 exit criteria

Sprint is done when:

1. Phase 0 findings are documented.
2. Phase 1 code lands, unit tests pass, integration harness demonstrates correctness in isolation.
3. Phase 2 migration is merged, user-facing SmartLink behavior is indistinguishable from pre-refactor.
4. Phase 3 UI ships with basic intelligent-disconnect messages and screen-reader announcements.
5. Phase 4 cleanup verifies exactly one session-level retry mechanism exists (WanSessionOwner's monitor thread).
6. Phase 5 soak test passes overnight with stable memory and no unexpected disconnects or exceptions.
7. Test matrix in `sprint26-test-matrix.md` is green.
8. No regressions in existing SmartLink behavior from the user's perspective.

When these are all true, Sprint 26 is shippable. Archive this plan to `docs/planning/agile/archive/`; promote any outstanding open questions to Sprint 27 or vision-doc backlog.

---

## Next session action

Begin Phase 0.1 (thread-safety audit). Grep FlexLib sources, read WanServer core methods, write the one-paragraph finding under the Phase 0 Findings section above. Then 0.2 (Sprint 25 retry enumeration). Then optionally 0.3. Phase 1 commences after findings are written.
