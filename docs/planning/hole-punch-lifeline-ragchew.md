# WAN Session Owner — SmartLink Reliability + Networking Features

**Status:** Planning — investigation complete, implementation pending
**Created:** 2026-04-13 (end of Sprint 25 dev day)
**Authors:** Noel (architecture + root cause analysis), Claude (clean-room pattern translation)

---

## Overview

JJ Flex has experienced intermittent SmartLink connection reliability issues: connections drop silently mid-session, reconnects feel flaky, users sometimes have to re-enter credentials or restart the app after what looks like a transient network blip.

Tonight's investigation (clean-room review of SmartSDR's WAN connection pattern + architectural analysis of our own code) identified **two root causes in our code** that together explain the symptoms, and uncovered an **opportunity to collapse another SmartSDR dependency** (port forwarding / hole-punch configuration) by fixing the architecture correctly.

---

## Root Cause Analysis

### Problem 1: `WanServer` ownership is coupled to `FlexBase`

**Evidence:** `Radios/FlexBase.cs:1033` — `private WanServer wan;`

`WanServer` represents the user's **SmartLink session** (auth state, radio list, SSL connection to the SmartLink backend). Conceptually, this should be a long-lived thing that survives across multiple radio connections within a user's session.

**Our code treats it as a per-radio-connection resource.** Every time a `FlexBase` instance is torn down (radio disconnect, user switches radios, UI state churn), the `WanServer` gets torn down with it. Next radio connection → new `WanServer`, new SSL handshake, new SmartLink registration, new radio list fetch. Wasteful and fragile.

The defensive comment at `FlexBase.cs:1044` (`wan = null; // prevent Dispose from killing it`) is a symptom — someone noticed the lifecycle was broken and added a workaround rather than fixing the ownership model.

### Problem 2: No subscription to `IsConnected` property change on `WanServer`

**Evidence:** Grep across our codebase for `wan.PropertyChanged` or `IsConnected` change subscription on `WanServer` — none found.

FlexLib's `WanServer` raises `PropertyChanged("IsConnected")` when the SmartLink connection state changes (confirmed at `FlexLib_API/FlexLib/WanServer.cs:58`). We currently subscribe to `WanRadioConnectReady` and `WanRadioRadioListReceived`, but not to IsConnected changes.

**Consequence:** when the TCP/SSL connection to SmartLink dies silently (network blip, NAT timeout, server-side close, token expiry), nothing in our code notices. We find out on the next attempt to use `wan`, which may be minutes later. User sees unexpected errors.

### How these compound

- Without Problem 1, a long-lived WanServer could at least stay healthy across radio changes
- Without Problem 2, at least we'd know when connections dropped and could react
- **With both, we rebuild sessions unnecessarily AND miss drops during operation** — giving the user experience of "intermittent reliability"

---

## Behavioral Spec for the Fix

This is the pattern to implement. Derived from clean-room analysis — we are implementing *behavior*, not copying code.

**A dedicated thread owns the WAN session lifecycle:**

```
Loop until shutdown requested:
  If not currently connected to SmartLink:
    Try wan.Connect() inside try/catch
    On success → reset backoff index to 0, set wait primitive to infinite (no timeout)
    On failure → advance backoff index (1s → 5s → 30s, capped at 30s)
  If connected:
    Wait on AutoResetEvent with no timeout (sleep indefinitely)

AutoResetEvent is signaled by:
  - WanServer.PropertyChanged("IsConnected") event with new value false
  - User explicit disconnect request
  - Application shutdown request

On wake: check shutdown flag. If set, exit cleanly. Otherwise loop back to connection check.
```

**Properties of this pattern:**
- Efficient — zero polling when connected
- Resilient — automatic retry with exponential backoff on drops
- Responsive — event-driven wake via FlexLib's IsConnected event
- Clean shutdown — explicit flag, no thread leaks

---

## Extended Scope — Unlocked by the Refactor

Once we own the WAN session properly (instead of letting it churn with FlexBase), several additional capabilities become natural. These aren't separate projects — they fall out of the architecture.

### Port forwarding / external port specification

Currently users must run SmartSDR to configure port forwarding for SmartLink, and SmartSDR's settings only affect SmartSDR's connection — not ours. Users have had to choose between UPnP (which doesn't work on many networks) or running SmartSDR separately for configuration. Accessibility problem (SmartSDR config UI is not screen-reader friendly) AND a workflow problem.

With a proper WAN session owner in our code, **we implement port config directly.** User can specify external port in our UI (screen-reader accessible). No dependency on SmartSDR. No UPnP reliance.

### Hole-punching / NAT traversal

Similarly, NAT hole-punch logic (for users without port forwarding capability) can be implemented alongside our WAN session owner. Currently this behavior is entirely inside SmartSDR or absent.

### NetworkTest integration

FlexLib exposes a `NetworkTest` method that checks various aspects of network readiness for SmartLink. With a dedicated session owner, we can invoke this proactively — before initial connect, periodically during session, on suspected trouble — and surface results to users intelligently.

### Intelligent disconnect diagnosis

Subscribing to `IsConnected` change gives us the *moment* a connection changes state. Combined with tracking recent state (last successful message, last keepalive, last auth refresh), we can present meaningful diagnoses rather than generic "disconnected" errors:
- "Connection dropped — attempting automatic reconnection"
- "Authorization expired — please re-enter credentials"
- "Network unreachable — check your internet connection"
- "SmartLink server unavailable — will retry automatically"

This turns a frustrating "it broke" experience into a coherent "here's what happened and what's being done about it."

---

## Implementation Outline

### New class: `WanSessionOwner` (name TBD)

Lives in `Radios/` or a new `SmartLink/` folder. Responsibilities:
- Owns the `WanServer` instance for the user's entire SmartLink session
- Owns the monitor thread that implements the behavioral spec above
- Subscribes to `WanServer.PropertyChanged` for IsConnected changes
- Exposes events / properties the UI can bind to (session state, last error, reconnect attempt count)
- Provides methods for explicit connect / disconnect / reset

Lifetime: created when user initiates SmartLink sign-in. Disposed when user signs out of SmartLink OR app exits. **Survives across multiple radio connections.**

### `FlexBase` changes

- Remove `private WanServer wan;` — FlexBase no longer owns WanServer
- Add reference/injection of `WanSessionOwner` (or just access a singleton/service locator)
- Remove WanServer creation/teardown code in `ConnectToSmartLink`
- Read radio list and connection info from the session owner instead of from the private wan field
- Delete defensive `wan = null` hygiene code (no longer needed once ownership is correct)

### Event wiring

- `WanSessionOwner` subscribes to `WanServer.PropertyChanged` on construction
- Event handler filters for `IsConnected` changes
- On transition to false: signal the monitor thread's AutoResetEvent
- On transition to true: no-op (monitor thread will see state next time it wakes, or is already sleeping-forever while connected)

### Shutdown coordination

- `WanSessionOwner` exposes `Shutdown()` method
- Sets `_shutdownRequested = true`
- Signals the AutoResetEvent
- Joins the monitor thread with timeout
- Disposes the WanServer

Called from app shutdown handler AND on explicit user sign-out.

---

## Risks and Open Questions

1. **Thread-safety of WanServer.** FlexLib's `WanServer` may or may not be thread-safe for concurrent access. We need to audit — if multiple threads call methods on it simultaneously (our monitor thread + UI threads reading state), we may need explicit locking.

2. **Multi-account SmartLink (Sprint 25 addition).** We now support multiple SmartLink accounts. Does the session owner need to handle one-at-a-time or concurrent? Probably one-at-a-time for now, but design should not preclude concurrent later.

3. **Interaction with existing retry logic.** Sprint 25 Phase X added `RetryConnect` and auto-reconnect logic at the `FlexBase` level. Some or all of that becomes obsolete once the session owner handles its own reconnection. Need to identify what to delete, what to keep for radio-level (as opposed to session-level) reconnect.

4. **Testing strategy.** Reliability improvements are hard to test without inducing real network failures. Consider:
   - Unit tests of the `WanSessionOwner` with a mocked `IWanServer` interface
   - Manual test plan: simulate network drops (airplane mode toggle, VPN disconnect)
   - Long-running soak test: leave connected overnight, check for spontaneous drops the next day

5. **Port config UI.** External port specification requires UI — where does it live? Probably in Settings, alongside SmartLink account management. Not blocked on the architecture refactor but needs its own design.

6. **NetworkTest threading.** Does `NetworkTest` block? If so, where to call it without stalling the UI?

---

## Related Work / Prior Context

- **Sprint 25 connection retry work:** `RetryConnect`, 5s abort revert, lightweight retry. These were stop-gap fixes. Once WanSessionOwner lands, some of this can simplify or go away.
- **Sprint 25 SmartLink multi-account:** per-account WebView2 cookie jars, Switch Account button. These are account-level, orthogonal to session-level reliability.
- **Justin AI5OS radio connection issue:** "auth works, radio connection fails (network/NAT issue, not our code)." With port config and hole-punching in our own code, cases like this become addressable from our UI.
- **Flex firmware access (pending Flex email):** a properly-owned WAN session is also likely a prerequisite for clean firmware push flow — firmware update requires a stable SmartLink session.

---

## Non-goals for the initial implementation

- **Do not** break existing SmartLink connection flow on rollout. Behavior must be at least as reliable from user perspective as current code.
- **Do not** implement port config UI in the same PR as the ownership refactor. Ship the architecture first, add port config in a follow-up.
- **Do not** implement NetworkTest integration in the same PR. Same reason.

The goal is: ship a clean architectural refactor that passes existing reliability behavior and gives us a home to build the network features into. The network features themselves are follow-up work.

---

## Next session action

Open this doc, review the spec, decide implementation approach, create a sprint plan with phases. Candidate first phase: `WanSessionOwner` class with monitor thread, thread lifecycle, signal/wait behavior — without moving FlexBase yet. Verify the thread behavior in isolation, then integrate with FlexBase in a second phase.
