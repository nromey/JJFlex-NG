# WAN Session Owner — Architecture & Multi-Sprint Vision

**Status:** Living architecture document; sprint-specific execution tracked separately
**Created:** 2026-04-13 (root cause investigation)
**Major revisions:**
- 2026-04-14 — Shape 2 architecture adopted; three design disciplines formalized; multi-radio tabbed vision surfaced and tied in; audio UX scoped (pan, gain, mute, smart-squelch-off-axis, multi-device routing)
- 2026-04-15 — Sprint decomposition finalized (26 connection-fix, 27 networking-config Tiers 1+2, 28+ multi-radio, 29+ Tier 3); non-goals ratified; vision doc restructured as architecture anchor with sprint plans split out

**Authors:** Noel (root cause analysis, architectural direction, accessibility/security/compliance framing, multi-radio product vision), Claude (clean-room pattern translation from SmartSDR, decomposition, interface design synthesis)

**Sprint plans:**
- Sprint 26 (connection fix): `docs/planning/agile/sprint26-ragchew-keepalive-kerchunk.md`
- Sprint 27 (networking config Tiers 1+2): `docs/planning/agile/sprint27-barefoot-openport-hotel.md`
- Sprint 28+ (multi-radio tabbed architecture): not yet planned; see memory `project_multi_radio_tabbed_architecture.md` for vision
- Sprint 29+ (Tier 3 hole-punching): not yet planned; see this doc's Extended Scope section

---

## Overview

JJ Flex has experienced intermittent SmartLink connection reliability issues: connections drop silently mid-session, reconnects feel flaky, users sometimes have to re-enter credentials or restart the app after what looks like a transient network blip.

Investigation (clean-room review of SmartSDR's WAN connection pattern plus architectural analysis of our own code) identified **two root causes in our code** that together explain the symptoms, and uncovered an **opportunity to collapse another SmartSDR dependency** (port forwarding / hole-punch configuration) by fixing the architecture correctly.

The fix is not a patch — it is a small but load-bearing refactor to how JJ Flex owns a SmartLink session. Once ownership is correct, several additional capabilities (user-configurable networking, intelligent diagnostics, future multi-radio, session panning, multi-process isolation) fall out of the architecture rather than requiring separate feature work. This document captures the architecture and the multi-sprint sequence that implements it.

---

## Root Cause Analysis

### Problem 1 — `WanServer` ownership is coupled to `FlexBase`

**Evidence:** `Radios/FlexBase.cs:1033` — `private WanServer wan;`

FlexLib's `WanServer` represents the user's **SmartLink session** — auth state, radio list, SSL connection to the SmartLink backend. Conceptually, this should be a long-lived thing that survives across multiple radio connections within a user's session.

**Our code treats it as a per-radio-connection resource.** Every time a `FlexBase` instance is torn down (radio disconnect, user switches radios, UI state churn), the `WanServer` gets torn down with it. Next radio connection → new `WanServer`, new SSL handshake, new SmartLink registration, new radio list fetch. Wasteful and fragile.

The defensive comment at `FlexBase.cs:1044` (`wan = null; // prevent Dispose from killing it`) is a symptom — someone noticed the lifecycle was broken and added a workaround rather than fixing the ownership model.

### Problem 2 — No subscription to `IsConnected` property change on `WanServer`

**Evidence:** Grep across our codebase for `wan.PropertyChanged` or `IsConnected` change subscription on `WanServer` — none found.

FlexLib's `WanServer` raises `PropertyChanged("IsConnected")` when the SmartLink connection state changes (confirmed at `FlexLib_API/FlexLib/WanServer.cs:58`). We currently subscribe to `WanRadioConnectReady` and `WanRadioRadioListReceived`, but not to `IsConnected` changes.

**Consequence:** when the TCP/SSL connection to SmartLink dies silently (network blip, NAT timeout, server-side close, token expiry), nothing in our code notices. We find out on the next attempt to use `wan`, which may be minutes later. User sees unexpected errors.

### How these compound

- Without Problem 1, a long-lived `WanServer` could at least stay healthy across radio changes.
- Without Problem 2, at least we'd know when connections dropped and could react.
- **With both, we rebuild sessions unnecessarily AND miss drops during operation** — giving the user experience of "intermittent reliability."

---

## Behavioral Spec for the Fix

This is the algorithm. Derived from clean-room analysis — we are implementing *behavior*, not copying code. Sprint 26 Phase 1 implements it.

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
- **Efficient** — zero polling when connected.
- **Resilient** — automatic retry with exponential backoff on drops.
- **Responsive** — event-driven wake via FlexLib's `IsConnected` event.
- **Clean shutdown** — explicit flag, no thread leaks.

---

## Architecture — Shape 2 Session Model

**Decision (2026-04-14, under max-effort reasoning):** The `WanSessionOwner` refactor is not a singleton patch. It is the foundation for a **browser-style tabbed multi-radio architecture** — JJ Flex's eventual product shape, where many concurrent radio sessions live in one main window, each a tab, with per-session panning / volume / device routing and (eventually) per-process isolation.

That vision ruled out a Shape 1 singleton ("one session owner, switch accounts internally") because hardcoding a singleton now would force a painful architectural migration later when concurrent multi-session lands. Shape 2 is the target from the start:

### Core classes and interfaces (populated by Sprint 26 Phase 1)

**`IWanServer`** — our abstraction over FlexLib's `WanServer`. Every call surface JJ Flex reaches for goes through this interface. Buys us four distinct benefits for one abstraction:
- **Testability** — mock implementations exercise the session owner's state machine without a network.
- **Version insulation** — future FlexLib updates don't break us at the API level.
- **Protocol safety** — our `SemaphoreSlim` for call serialization lives here, giving protocol ordering guarantees on top of whatever data-race protection FlexLib provides internally.
- **Tracing layer** — the adapter traces every call before forwarding, so we have a chronological record of every WanServer interaction for diagnostic purposes.

**`IWanSessionOwner`** — the session contract. Exposes state (`IsConnected`, `Status`, `LastError`, `ReconnectAttemptCount`, `AccountId`, `SessionId`), commands (`Connect`, `Disconnect`, `Reset`), data (`AvailableRadios`, audio sink access), and events (state changes, signal-strength threshold crossings).

**`WanSessionOwner : IWanSessionOwner`** — concrete implementation. Owns an `IWanServer` (via `WanServerAdapter`). Owns a dedicated monitor thread implementing the behavioral spec above. Subscribes to `IsConnected` property-change. Emits structured trace lines with session-ID prefix on every state transition.

**`SmartLinkSessionCoordinator`** — top-level manager. Holds `Dictionary<SessionId, IWanSessionOwner>` plus `ActiveSessionId`. Exposes `ActiveSession` (the currently-focused session, or null) and `AllSessions` (the full collection). At Sprint 26 runtime the dictionary only ever holds 0 or 1 entries; in Sprint 28+ it grows as users add tabs. **The coordinator is the single point of access — consumers never cache a specific `IWanSessionOwner` reference.**

**`ISessionAudioSink`** — the audio-output primitive. Properties: `Pan` (stereo position), `Gain` (linear multiplier), `Muted` (bool), `IsFocused` (bool). Method: `Write(samples)`. Sprint 26 ships `DirectPassthroughSink` which ignores all properties and writes to default output. The interface is shaped to carry **all five future audio behaviors** without interface changes (auto-pan on focus, manual pan, mute, smart-squelch-off-axis, multi-device routing).

### Four design disciplines (enforced from Sprint 26 onward)

These are code-review rules that apply everywhere session-aware code is written. They are deliberately inexpensive to follow and deliberately high-value to preserve across sprints.

**D1. No shared mutable state between sessions.** No `static` fields carrying per-session data. All cross-session communication flows through the coordinator. Grep-clean rule at the end of each session-touching sprint: `static` fields holding mutable state that a session touches do not exist outside of fundamentally-global things (logging sinks, app-wide config). This is the single biggest preparation for future multi-process isolation, because shared mutable state is exactly what breaks across process boundaries.

**D2. Session audio output is an injected `ISessionAudioSink`, not a push to a global output.** Each `WanSessionOwner` receives its sink at construction. No direct calls to `PortAudio.DefaultOutput` or equivalent from session code. This is the seam for future per-session panning, volume ducking, multi-device routing, AND future cross-process audio IPC. One interface, four future capabilities unlocked.

**D3. Per-session tracing with session-ID prefix.** Every trace line from within session-aware code includes `[session=<id>]` as a structured field or prefix. At N=1 this looks redundant; at N=3 you'll bless this rule; at multi-process you'll bless it twice. Tracing discipline is a Phase 1 deliverable in Sprint 26, not an afterthought.

**D4. Consumers access sessions only through `coordinator.ActiveSession`.** `FlexBase` and other session-aware code never captures an `IWanSessionOwner` reference into a field. The overhead of a dictionary lookup is 50ns; the cost of a stale captured reference (when future tab-switching rebinds the active session) is enormous. Re-accessing every time is correct; caching is a review-blocker.

---

## Extended Scope — Capability Map

The refactor unlocks several capabilities. Each capability is a user-visible feature that rides on the architecture; each is scheduled to a specific sprint.

### User-configurable networking (three-tier model)

Currently users must run SmartSDR to configure port forwarding for SmartLink. This is *not a workaround that's inconvenient* — it fundamentally doesn't help our app. SmartSDR's port config applies to SmartSDR's process; JJ Flex is a separate process with its own TCP listeners on different ports. Even a sighted user who perfectly configured SmartSDR's settings would still have zero effect on our app's connection. The architecture *requires* port config to happen inside our process, for our process.

**This is both an accessibility blocker AND a security/compliance requirement** — not merely a convenience feature:

- **Accessibility:** SmartSDR's config UI isn't screen-reader friendly. Blind users currently have **no path** to configure SmartLink networking at all. With a proper WAN session owner in our code, we provide the only screen-reader-accessible port config UI that exists for blind hams.
- **Security:** UPnP is actively dangerous — it lets any LAN process open router ports without user consent. Exploited in the wild (Mirai family, webcam hijacks). Security-conscious users disable UPnP at the router level.
- **Compliance:** Multiple regimes effectively require UPnP off — DISA STIGs, PCI-DSS, HIPAA, Section 508-adjacent federal ICT policies. A Flex radio deployed in a military ham club, state EOC AuxComm setup, university research station, or any organization with a real sysadmin is very likely on a UPnP-disabled network. Today those users can't use SmartLink from JJ Flex robustly. After this work they can.

**The three tiers:**

**Tier 1 — Manual port specification (user-sovereign, UPnP-free).** User specifies a port, forwards it on their router themselves, tells JJ Flex to bind there. Works on any network, any security posture, any router. **This is the recommended default**, not the "advanced" option. Respects user competence. Hams are disproportionately exactly the demographic that runs their own servers and takes firewall rules seriously.

**Tier 2 — UPnP auto-config (convenience for casual users).** Optional, off by default, explicit opt-in with a clear warning about automatic port opening. For users who don't want to touch their router and whose network allows UPnP.

**Tier 3 — Hole-punching / NAT traversal via outbound connections only.** For users who can't configure their router AND can't use UPnP (restrictive networks, hotel wifi, cellular, corporate guest wifi). No inbound port forwarding required. Works by having both ends make outbound connections to a rendezvous point.

**Sprint assignment:** Tier 1 + Tier 2 in Sprint 27. Tier 3 in Sprint 29+ as a focused sprint (needs rendezvous server infrastructure, symmetric-NAT handling, STUN/TURN decisions — substantial work that warrants its own dedicated effort rather than being squeezed into a mixed sprint).

### Intelligent disconnect diagnosis

Subscribing to `IsConnected` change gives us the *moment* a connection changes state. Combined with tracking recent state (last successful message, last keepalive, last auth refresh, and — post-Sprint-27 — NetworkTest results), we present meaningful diagnoses rather than generic "disconnected" errors:

- "Connection dropped — attempting automatic reconnection"
- "Authorization expired — please re-enter credentials"
- "Network unreachable — check your internet connection"
- "SmartLink server unavailable — will retry automatically"
- "SmartLink backend unreachable but your LAN is fine — usually a Flex-side issue"
- "NAT type appears to be symmetric — Tier 3 or port forwarding required"
- "UPnP mapping failed; using manual port instead"

This turns a frustrating "it broke" experience into a coherent "here's what happened and what's being done about it."

**Sprint assignment:** Basic (state-machine-driven) messages in Sprint 26 Phase 3. Richer (NetworkTest-informed) messages in Sprint 27 Track D.

### NetworkTest integration

FlexLib exposes a `NetworkTest` method that checks various aspects of network readiness for SmartLink. With a dedicated session owner, we invoke this proactively — before initial connect, periodically during session, on suspected trouble — and surface results intelligently.

**Sprint assignment:** Sprint 27 Track C.

### Tabbed multi-radio architecture

One main window, many concurrent radio sessions as tabs, each with its own `IWanSessionOwner` in the coordinator's dictionary. Browser-style tab strip UI. Per-session UI hosted inside the main window (not separate JJ Flex instances). See memory `project_multi_radio_tabbed_architecture.md` for full vision.

**Sprint assignment:** Sprint 28+.

### Session-level audio UX (pan, gain ducking, mute, smart-squelch-off-axis, multi-device routing)

All five audio behaviors ride on `ISessionAudioSink` primitives. When the user focuses a tab, that session auto-pans to center and rises to full gain; inactive sessions pan off-axis and duck to ~0.6–0.7 gain. A strong signal on an off-axis session temporarily raises its gain so the operator hears "something is happening over there," with pan preserving *which* tab it came from. Operators can manually override pan, mute specific tabs, or route tabs to different audio devices (primary headphones, USB speaker, Bluetooth earbud, etc.). See memory `project_multi_radio_audio_ux.md` for full UX spec.

**Accessibility framing:** For a blind operator, stereo-field position is not a decoration — it *is* the perceptual channel by which radio identity is continuously carried. Screen-reader announcements give you discrete switch events; spatial audio gives you continuous ambient awareness. Both are needed; neither replaces the other.

**Sprint assignment:** Sprint 28+ alongside the tab UI. The `SessionMixer` class that layers these behaviors on top of `ISessionAudioSink` lands in that sprint.

### Multi-process tab isolation

Eventual architectural move — each tab becomes its own OS process, composited into the main window (the Chrome/Firefox pattern). Motivated by isolation (one radio's FlexLib quirk shouldn't crash the others), memory reclaim (heavy-slice sessions stay bounded), and security (native DLL issues contained per-process). The three design disciplines (no shared state, `ISessionAudioSink` as seam, per-session tracing) are the preparation; actual child-process spawning is not.

**Sprint assignment:** Aspirational, Sprint 30+. No current plan; will be re-scoped when Sprint 28 ships and we see whether in-process is sufficient.

---

## Sprint Sequencing

- **Sprint 26 (planned, ready for execution):** Connection fix — `WanSessionOwner` architecture, basic intelligent disconnect diagnosis. Ships the foundation. User-visible payoff: fewer silent drops, meaningful status messages. See sprint plan.
- **Sprint 27 (planned, blocked on Sprint 26 execution):** Networking config Tiers 1 + 2, NetworkTest integration, richer diagnostics. Ships the first real alternative to SmartSDR for network configuration. User-visible payoff: accessible port config, UPnP opt-in with safety rails, diagnostic messages that actually explain failures. See sprint plan.
- **Sprint 28 (not yet planned):** Tabbed multi-radio, `SessionMixer`, audio UX policy layer. Ships the multi-radio product form. User-visible payoff: run N radios at once, each with its own pan/gain/mute/device. See memory `project_multi_radio_tabbed_architecture.md`.
- **Sprint 29+ (not yet planned):** Tier 3 hole-punching as a focused sprint with rendezvous server infrastructure. User-visible payoff: restrictive-network users (hotel wifi, cellular, corporate guest) can use SmartLink from JJ Flex. Not currently prioritized over Sprint 28 because the user segment is smaller.
- **Sprint 30+ (aspirational):** Multi-process tab isolation. Re-scope after Sprint 28 observations.

---

## Risks and Open Questions — dispositions

From the original investigation, six risks were flagged. Under max-effort reasoning (2026-04-15) each was given a disposition:

**Risk 1 — Thread-safety of `WanServer`.** **Disposition:** Sprint 26 Phase 0 audit spike, with pre-committed architectural choice: we always lock at our `WanServerAdapter` boundary regardless of audit outcome. The lock is cheap (WanServer calls are rare); the insurance against future FlexLib changes is valuable.

**Risk 2 — Multi-account SmartLink.** **Disposition:** Design decision locked. Shape 2 (multi-instance session owner with coordinator) from day one, behaviorally N=1 in Sprint 26's UI. Interface discipline means migration to truly concurrent N>1 (Sprint 28 tabs) is mechanical, not architectural.

**Risk 3 — Interaction with Sprint 25 retry logic.** **Disposition:** Sprint 26 Phase 0.2 audit spike (enumerate + classify each existing retry site as session-level, radio-level, or ambiguous); Phase 4 cleanup executes the per-item dispositions. Session-level retries move to `WanSessionOwner`; radio-level retries stay where they are.

**Risk 4 — Testing strategy.** **Disposition:** Folded into Sprint 26 Implementation Outline. `IWanServer` adapter interface from day one enables mock-based unit tests. Phase 5 executes manual matrix + overnight soak. Structured trace logging from day one makes soak-test forensics tractable.

**Risk 5 — Port config UI.** **Disposition:** Sprint 27 Track A — this is where the UI actually lives. Not a Sprint 26 concern.

**Risk 6 — NetworkTest threading.** **Disposition:** Small Sprint 26 Phase 0.3 audit spike (optional — can defer to Sprint 27 Phase 0). Informs Sprint 27 Track C integration details.

---

## Related Work / Prior Context

- **Sprint 25 connection retry work:** `RetryConnect`, 5s abort revert, lightweight retry. These were stop-gap fixes. Once `WanSessionOwner` lands, some of this will simplify or go away (Sprint 26 Phase 4).
- **Sprint 25 SmartLink multi-account:** per-account WebView2 cookie jars, Switch Account button. These are account-level, orthogonal to session-level reliability. The Shape 2 coordinator naturally extends to support multiple accounts if concurrent-multi-account demand surfaces.
- **Justin AI5OS radio connection issue:** "auth works, radio connection fails (network/NAT issue, not our code)." With Sprint 27's Tier 1 + Tier 2 in our own UI, cases like this become addressable from JJ Flex's Settings dialog.
- **Flex firmware access (pending Flex email):** a properly-owned WAN session is likely a prerequisite for a clean firmware-push flow; firmware update requires a stable SmartLink session.
- **Multi-radio tabbed architecture (memory: `project_multi_radio_tabbed_architecture.md`):** the product vision that forced the Shape 2 decision and shaped the three design disciplines.
- **Multi-radio audio UX (memory: `project_multi_radio_audio_ux.md`):** the per-session audio behavior spec that shaped `ISessionAudioSink`'s properties.

---

## Living Decisions Log

Chronological record of architectural decisions. When a decision is revisited or revised, add a new entry rather than editing history.

**2026-04-13 — Root cause identified.** Investigation concluded that Problems 1 and 2 (ownership coupling and missing `IsConnected` subscription) are the root causes of SmartLink reliability symptoms. Clean-room review of SmartSDR's pattern provided the behavioral spec template.

**2026-04-14 — Three-tier networking model adopted.** Sovereign default (Tier 1) preferred over auto-magic default (Tier 2) based on security/compliance/accessibility framing. UPnP stays opt-in with explicit warnings, not default-on.

**2026-04-14 — Shape 2 architecture adopted.** Multi-instance session owner with coordinator, driven by the multi-radio tabbed product vision. Shape 1 singleton would have forced a painful migration later. Shape 2 is behaviorally N=1 in Sprint 26 and extends naturally to N>1 in Sprint 28+.

**2026-04-14 — Four design disciplines formalized.** No shared mutable state between sessions (D1); session audio output via injected `ISessionAudioSink` (D2); per-session tracing with session-ID prefix (D3); consumers access sessions via `coordinator.ActiveSession` never via captured reference (D4). These are code-review rules, enforced from Sprint 26 onward.

**2026-04-14 — Audio UX scoped.** Five behaviors (auto-pan on focus, manual pan override, per-session mute, smart-squelch-off-axis, multi-device routing) layered on `ISessionAudioSink` primitives (Pan, Gain, Muted, IsFocused). Sixth behavior (volume ducking for inactive sessions) added same day. All implementations Sprint 28+; Sprint 26 ships `DirectPassthroughSink` stub.

**2026-04-14 — `install.bat` swaps from `ProductVersion` to `FileVersion`.** SDK-generated `ProductVersion` carries a `+<commit-hash>` source-link suffix that was polluting installer filenames. Clean `FileVersion` used for filenames; hash-bearing `ProductVersion` preserved for crash triage. Not directly related to WAN session work but happened in the same window; noted for context.

**2026-04-15 — Sprint decomposition finalized.** Sprint 26 connection-fix (single track, serial phases 0–5); Sprint 27 networking config Tiers 1+2 (multi-track; Track A foundation, B/C/E parallel, D integrates); Sprint 28+ multi-radio tabbed (separate sprint); Sprint 29+ Tier 3 hole-punching (focused sprint, separate from Sprint 27 to avoid bundling with simpler tiers).

**2026-04-15 — Tier 3 deferred to its own sprint.** Original framing had all three tiers in one sprint. Tier 3 complexity (rendezvous server, STUN/TURN, symmetric-NAT handling) is 2–4 weeks of focused work on its own. Bundling would either produce a giant sprint or ship Tier 3 half-finished. Splitting preserves shippability of Tiers 1+2 while letting Tier 3 be done right.

**2026-04-15 — Vision doc restructured.** Implementation outline extracted to Sprint 26 plan. Per-sprint non-goals extracted to respective sprint plans. This doc is now the architecture anchor and cross-sprint sequencing document, not a single-sprint plan.

**2026-04-20 — R1: `WanServerAdapter` lock primitive swapped from `SemaphoreSlim` to `lock` (Monitor).** Sprint 26 Phase 0.1 audit of FlexLib's `WanServer` found that `PropertyChanged("IsConnected")` raises synchronously on the mutator thread with no dispatcher/sync-context hop (`ObservableObject.cs:31-38`). A non-reentrant primitive at our adapter boundary would deadlock when an event handler on the mutator thread reaches back through the adapter to read state. `lock` (Monitor) is reentrant on the same thread and matches FlexLib's synchronous event model without adding latency. `SemaphoreSlim`'s async capabilities were never used; the swap is pure simplification. Option (b) (post events through a SynchronizationContext) and (c) (discipline-only) considered and rejected — (b) adds latency and requires inventing a sync context in the monitor thread; (c) is brittle to future handlers. Phase 1 unit test `SemaphoreSlim_SerializesConcurrentCalls` renamed to `Lock_SerializesConcurrentCalls`; new test `Lock_IsReentrantOnSameThread` added as the regression guard for this finding.

**2026-04-20 — R2: Symptom 6 handled via snapshot-at-subscribe, not universal announcement rewire.** Sprint 26 Phase 0.4 enumeration found that all JJ-written announcement sites correctly read callsigns from the event `client` payload — no post-change app-global-state reads. The wa2iwc-wrong-callsign-on-disconnect bug almost certainly lives in FlexLib's `Radio.parseGuiClientStatus` (Radio.cs ~13550-13640), which mutates `GUIClient.Station` / `Program` before firing `OnGUIClientRemoved`. Our announcement handler receives a blanked-out client. Fix: `FlexBase` (and eventually the coordinator at Phase 2) maintains a `Dictionary<ClientHandle, (Station, Program)>` populated at `GUIClientAdded` time, updated at `GUIClientUpdated`, and read at `GUIClientRemoved` — ignoring the possibly-blanked event payload. Upgrade-safe (doesn't patch FlexLib), ~15-20 line addition, lives inside Phase 2.5 proper rather than being a separate rewire pass. Phase 2.5 scope narrows from "rewire every announcement through coordinator" to "snapshot at subscribe + route the five other MultiFlex fixes through coordinator."

**2026-04-20 — R3: Symptom 3 (client list doesn't propagate) shipped as 4.1.17 slip-in ahead of Sprint 26.** Phase 0.4 confirmed `MultiFlexDialog.RefreshClientList` (`JJFlexWpf/Dialogs/MultiFlexDialog.xaml.cs:53-68`) is not subscribed to `GUIClientAdded` / `GUIClientRemoved` events — it only fires on dialog open and user-initiated disconnect. Fix is ~20 lines: subscribe on dialog open, unsubscribe on close, `RefreshClientList()` on the dispatcher from each handler. Because the fix is narrow, single-file, and Don directly benefits during his two-client testing with Noel, it ships as a 4.1.17 release independently of Sprint 26's coordinator refactor. Sprint 26 Phase 2.5 inherits a fixed Symptom 3 and verifies the slip-in survives the coordinator migration. Flow: `fix/multiflex-client-list-autorefresh` branch cut from main → fix + version bump to 4.1.17 → tag `v4.1.17` → build installers → merge to main → publish. Sprint 26's `sprint26/connection-fix` branch later rebases on the post-4.1.17 main.

---

## Cross-Sprint Architectural Non-Goals

These apply across the whole multi-sprint arc and are documented here (rather than in individual sprint plans) because they describe what the *architecture* doesn't try to be, not what any specific sprint doesn't ship.

- **Not a full replacement for SmartSDR.** JJ Flex continues to be an alternative UI, not a full re-implementation of SmartSDR's capabilities. We implement what our users (especially blind users) need; we are not in a feature-parity race.
- **Not a generic multi-vendor abstraction.** The `WanSessionOwner` / `IWanServer` interfaces are specific to FlexLib's SmartLink model. Future Hamlib / Icom / Kenwood support may need a different abstraction; we do not pre-design for that here. The radio-abstraction layer is a separate, explicit project.
- **Not a networking library.** We use FlexLib's `WanServer`, UPnP libraries, and (later) STUN/TURN libraries as appropriate. We do not re-implement protocols.
- **Not a distributed system.** Even with multi-process tab isolation (aspirational, Sprint 30+), JJ Flex is a single-user, single-machine application. No server-side component; no cloud dependency; no shared state between users. The rendezvous server that Sprint 29+ will require for Tier 3 is the only server-side piece, and it's stateless (connection brokering only).

---

## Architecture quality checks

End-of-each-session-touching-sprint verification:

- **No `static` fields carrying per-session mutable state** (enforces D1). Grep for `static [\w]+ [_]?[\w]+\s*[;=]` and audit each hit for session relevance.
- **No direct calls to `PortAudio.DefaultOutput` (or equivalent) from session code** (enforces D2). Grep-check session-aware files.
- **All session-aware trace calls include session-ID** (enforces D3). Grep for trace-method calls in session code; verify each either has the session-ID field or is in code paths where session context is unambiguous (e.g., coordinator-level).
- **No captured `IWanSessionOwner` references in consumer fields** (enforces D4). Grep for `IWanSessionOwner ` as a field declaration; zero hits expected.

These are not tests per se — they're end-of-sprint inspections that confirm the disciplines held under the pressure of real implementation.

---

## When this document should be updated

- **When a new sprint plan is created** that implements architecture from this doc — add a link in Sprint Sequencing.
- **When an architectural decision is revisited** — add an entry to the Living Decisions Log; do NOT edit earlier entries.
- **When a capability ships** that was previously in the capability map — annotate it as "Shipped in Sprint N" rather than rewriting.
- **When a risk disposition changes** — revisit the Risks section; Living Decisions Log entry explaining why.
- **When the sprint sequencing is re-prioritized** — update Sprint Sequencing + Living Decisions Log.

This doc is architecture, not plan. It should accrete history rather than be continuously rewritten.
