# Sprint 27 — User-Configurable Networking (Tiers 1 + 2 + Diagnostics)

**Status:** Planning complete — blocked on Sprint 26 completion
**Created:** 2026-04-15 (under max-effort reasoning)
**Authors:** Noel (product direction, accessibility framing, security/compliance framing), Claude (track decomposition)
**Parent vision doc:** `docs/planning/hole-punch-lifeline-ragchew.md`
**Parent architecture prerequisite:** `docs/planning/agile/sprint26-ragchew-keepalive-kerchunk.md` (this sprint depends on its WanSessionOwner + SmartLinkSessionCoordinator landing first)
**Branch target:** `sprint27/network-config` (multi-track; Track A solo, then B/C/E parallel, then D integrates)

---

## Sprint goal

Give users control over how JJ Flex reaches their radio behind NAT, via two of the three networking tiers: manual port specification (Tier 1, user-sovereign) and UPnP opt-in (Tier 2, convenience). Surface meaningful network diagnostics so users understand what's going wrong when things break. Tier 3 hole-punching and rendezvous infrastructure are deferred to a later sprint (Sprint 29+) as a focused effort.

This sprint is what **unblocks blind users from the SmartSDR dependency** for SmartLink networking. Today a blind user literally cannot configure SmartLink networking — SmartSDR's UI isn't screen-reader friendly, and SmartSDR's port config doesn't even apply to JJ Flex's process anyway (architecturally separate network identity). With Sprint 27, JJ Flex has its own accessible network config.

It also removes a **security / compliance blocker** for organizational users — DISA STIGs, PCI-DSS, HIPAA, and similar require UPnP disabled. Manual port config (Tier 1) is the only tier compatible with those policies, and shipping it as the default (not the "advanced" option) respects the competence of the demographic most likely to run JJ Flex (hams, who are disproportionately sovereign-networking types).

---

## Context

See parent vision doc `docs/planning/hole-punch-lifeline-ragchew.md` for the three-tier networking model, accessibility / security / compliance framing, and why the refactor in Sprint 26 is a prerequisite. In brief:

- Sprint 26 establishes `WanSessionOwner` as a long-lived session owner, stable across radio connections. That stability is what makes "bind network settings to a session" sensible — before Sprint 26, settings would bind to an object that gets recreated on every radio change.
- Three networking tiers: Tier 1 manual port (sovereign, no UPnP), Tier 2 UPnP opt-in (convenience, off by default), Tier 3 hole-punching / NAT traversal (restrictive-network users).
- Default posture: Tier 1 (sovereign), not Tier 2 (auto-magic). This is a product-values choice, not a technical one.

---

## Scope decision (ratified 2026-04-15)

**Sprint 27 = Tiers 1 + 2 + NetworkTest + richer diagnostics.**
**Sprint 28 = Tabbed multi-radio + SessionMixer + audio UX (separate concern, prioritized because higher user-visibility and strategic value).**
**Sprint 29 or later = Tier 3 hole-punching as its own focused sprint with rendezvous server infrastructure.**

Rationale for splitting Tier 3 out:
- Tier 3 is 2–4 weeks of focused effort including infrastructure (rendezvous server hosting, STUN/TURN decisions, symmetric-NAT failure handling). Bundling with Tiers 1 + 2 means either a giant sprint or a sprint whose Tier 3 track ships half-finished.
- Tier 1 alone serves the sovereign-ham demographic completely (most users).
- Tier 2 alone serves the casual-user demographic completely.
- Tier 3 serves a smaller segment (restrictive-network users) that's legitimately valuable but not blocking for most.
- Splitting lets Tier 3 be its own thing done right, not squeezed.

---

## Track decomposition

Four mandatory tracks (A, B, C, D) and one optional track (E). Track A is the serial foundation; B, C, E run in parallel after A lands; D integrates C's shape. See CLAUDE.md Sprint Lifecycle for worktree + CLI session conventions.

### Track A — Tier 1: Manual port configuration (FOUNDATION, serial, goes first)

**Why serial:** Tracks B, C, D all consume the settings-layer shape Track A establishes. The UI settings model, persistence format, and `IWanSessionOwner`-side binding for port config are Track A's deliverables. Tracks B and C would fight Track A if run in parallel.

**Scope:**

- Settings UI: port number field in the SmartLink section of Settings dialog. Validated input (1024–65535, not reserved, not currently bound by another app). A "test" button that attempts to bind the port and reports success/failure without persisting.
- Persistence: per-SmartLink-account port config. Two accounts can have two different port preferences. Uses existing user-settings storage.
- `IWanSessionOwner` binding: when session constructs its underlying connection, it uses the configured port if set; otherwise falls back to FlexLib default. This may require a small addition to the session constructor or a `ConfigureListener(port)` method on the session.
- In-UI documentation: one-screen explanation of what Tier 1 means, why it's the recommended default, and a pointer to the future help page for router configuration.
- Accessibility: field has a clear `AccessibleName`, "test" button announces results via live-region, documentation is prose (not a diagram).

**Deliverable:** users can set a port, save it per-account, and JJ Flex uses it for that account's SmartLink session. No other tier is implemented yet.

**Exit criterion:** manual port config works end-to-end against a real SmartLink account. Port persists across app restarts. Invalid inputs produce clear errors announced by screen reader. The "test" button's success/failure is announced. Track B, C, D can now begin.

### Track B — Tier 2: UPnP opt-in (parallel after A)

**Scope:**

- UPnP library choice: spike at start of track to evaluate options — `Open.NAT` (active?), `Mono.Nat` (mature but status?), native Windows `UPnPNAT` via COM interop (zero-dep). Decision written to the plan before coding starts.
- UPnP client integration: on session start, if Tier 2 is selected, attempt UPnP mapping for the configured port. On session end, release the mapping. On Tier 2 toggle-off, release any pending mapping.
- Settings UI: checkbox "Use UPnP to automatically configure my router (off by default)" with a prominent warning paragraph below explaining UPnP security implications (any LAN process can open ports, exploited in the wild, disabled by many organizational policies).
- Fallback: if UPnP fails (router doesn't support it, disabled at router, mapping refused, timeout), log failure clearly via Track D's diagnostic surface and fall back to Tier 1 behavior (use the manually-configured port, without UPnP mapping). Never silent-fail.
- Settings validation: Tier 2 requires a port set in Tier 1 first. UI enforces this — toggle is disabled until a port is configured.

**Deliverable:** UPnP-using users can opt in with an explicit warning; failure modes are explicit and fall back gracefully to Tier 1.

**Exit criterion:** UPnP toggle lights up mapping on a UPnP-enabled router; toggle off releases mapping; failure on a non-UPnP router surfaces "UPnP unavailable — using Tier 1 manual port" in Track D's diagnostic surface; no crashes or silent failures.

### Track C — NetworkTest integration (parallel after A)

**Scope:**

- Wrap FlexLib's `NetworkTest` in a session-owned wrapper. Shape determined by Sprint 26 Phase 0.3 audit (sync / async / event-driven). If that audit was deferred, it runs at the start of this track.
- Invocation points: (a) initial session connect, (b) on-demand via a "Test network" button in Settings, (c) automatically after an unexplained disconnect when the session owner's reconnect logic suspects network trouble.
- Structured result type: `NetworkDiagnosticReport` with fields for each subtest, pass/fail, optional detail string, timestamp. Persisted to trace log; exposed to Track D's UI.
- Threading: if blocking, call from `WanSessionOwner`'s monitor thread (already off UI thread). If async, subscribe to completion. If event-driven, subscribe to completion event and update `NetworkDiagnosticReport` on callback.
- Caching policy: cache result for 5 minutes if passed, 30 seconds if failed, to avoid hammering during a flaky period. Invalidation on explicit user trigger (clicking "Test network" again).

**Deliverable:** the app has a machine-readable understanding of the user's network state. This knowledge drives Track D's messages.

**Exit criterion:** NetworkTest runs on connect and on-demand; results are logged; Track D can consume them.

**UX design note (captured 2026-04-15):** the NetworkTest section of the Network tab must NOT conflate network capability with radio configuration. Validated by two user voices (Don and Noel) who both run UPnP-capable networks but prefer manual port forwarding for the radio specifically — so the case of "UPnP available AND radio on manual" is the *normal* case, not the exception, and the UI must not read as a warning or inconsistency when that's what's happening. Shape the Network tab as two clearly-separated sections:

- **Section 1 — Current radio configuration:** what the radio is actually doing. E.g. "Radio listens on port 4992 (TCP and UDP), manual forwarding" or "Radio uses UPnP-mapped port 54218" or (future Tier 3) "Radio uses automatic negotiation." One canonical line, comes from the radio's reported state via `FlexBase.PortForwardingEnabled` / `PortForwardingTcpPort` etc.
- **Section 2 — Network capability (NetworkTest results):** what the user's network supports. "UPnP: available / unavailable / disabled", "Manual forward to port 4992: reachable / unreachable", "NAT type: full-cone / symmetric / etc.". These report capability, with no implication about which path the radio chose.
- **Explicit bridging copy between the two sections:** one line like *"These are your network's capabilities. Your radio's actual configuration is shown above and isn't affected by these test results."* Kills the false-inconsistency read when both "UPnP: available" and "Radio on manual" are true simultaneously.
- **Don't nag about non-radio platform state** (see memory `feedback_dont_duplicate_platform_warnings.md`). If the user's router has UPnP enabled or disabled, that's their business. NetworkTest reports what matters for *this radio*, not the user's general network hygiene.

### Track D — Richer disconnect diagnostics (serial after C shape settles)

**Why serial after C:** Track D extends Sprint 26's Phase 3 basic status UI with NetworkTest-informed messages. Needs C's `NetworkDiagnosticReport` shape settled before the message dictionary can be extended.

**Scope:**

- Extend the message dictionary / switch from Sprint 26 Phase 3 with NetworkTest-informed messages:
  - "SmartLink backend unreachable but your LAN is fine — this is usually a Flex-side issue; retrying automatically."
  - "NAT type appears to be symmetric — Tier 3 (when available) or port forwarding may be required."
  - "Network appears healthy but authentication failed — please sign in again."
  - "UPnP mapping failed; using manual port instead."
  - "Connection dropped and network is unreachable — check your internet."
- Help-link affordances: each failure class links to a local markdown doc in `docs\help\networking\` (or similar). Help docs can be minimal stubs in this sprint; full prose lands in Track E.
- Screen-reader announcement: live-region announces new diagnostic messages when they change (not just silent status-bar update).
- Configurable verbosity: a setting ("Verbose network diagnostics" or similar) that toggles between short-form user-facing messages and longer-form debug messages with timestamps and raw diagnostic data. Off by default; advanced users turn it on.

**Deliverable:** users understand *why* a connection is failing, not just *that* it failed.

**Exit criterion:** induced failure modes (airplane mode, invalid port, UPnP on a UPnP-disabled router, SmartLink DNS blocked in hosts file) each produce a distinct, accurate user-facing message. Screen reader announces each. Help links open the local markdown doc.

### Track E — Documentation + help pages (optional / parallel)

**Scope:**

- Write `docs\help\networking\tier1-manual-port.md` explaining manual port config, how to forward a port on common routers (ASUS, Netgear, Linksys, TP-Link, UniFi, Mikrotik), the recommended port range to choose from, troubleshooting.
- Write `docs\help\networking\tier2-upnp.md` explaining what UPnP is, when it's appropriate, the security concerns, how to disable it at the router.
- Write `docs\help\networking\diagnostics.md` explaining each diagnostic message class from Track D, common causes, typical fixes.
- Accessibility: pages are prose with bulleted lists. No tables-for-comparisons (prose + bullets instead). No ASCII diagrams. Links within the doc set use relative paths.
- Copy these to the app's output directory (CopyToOutputDirectory=PreserveNewest) so Settings' help links resolve locally.

**Deliverable:** users have the information they need to pick a tier intelligently and diagnose issues themselves.

**Exit criterion:** three help docs exist, linked from Settings and from Track D's diagnostic messages. Screen-reader-friendly (plain markdown, no tables, no images).

---

## Phase ordering within Sprint 27

**Phase 1 (serial, ~2–3 days):** Track A alone. Foundation settles. End-of-phase: manual port config works, saved per-account, bound to session.

**Phase 2 (parallel, ~1–2 weeks):** Tracks B + C + E in three worktrees, three CLI sessions (see CLAUDE.md Sprint Lifecycle). Tracks B and C are independent; Track E is independent of everything.

**Phase 3 (serial, ~3–5 days):** Track D integrates the output of Phase 2 (consumes Track C's `NetworkDiagnosticReport`, extends Phase 3 status UI from Sprint 26, pulls help-link targets from Track E). Merge-week equivalent.

**Phase 4 (serial, ~2–3 days):** Test matrix, manual test pass, soak test (overnight with Tier 1 + Tier 2 both exercised), release readiness.

Total: ~3 weeks of calendar time, peaking at three parallel tracks in Phase 2.

---

## Non-goals for Sprint 27

**NG-1:** No Tier 3 hole-punching. (Sprint 29+.)

**NG-2:** No rendezvous server infrastructure, no STUN/TURN implementation. (Sprint 29+ infrastructure work.)

**NG-3:** No tab UI. (Sprint 28.)

**NG-4:** No `SessionMixer`, no audio policy (pan, gain, mute, duck, smart-squelch). (Sprint 28.)

**NG-5:** No string localization of diagnostic messages. Strings live in a resource file so localization is possible later without refactoring, but only English ships in Sprint 27.

**NG-6:** No external help-page hosting (jjflexible.radio). Sprint 27 ships in-app markdown in `docs\help\networking\` with local file links only. External hosting is a separate effort if/when jjflexible.radio materializes.

**NG-7:** No changes to existing non-SmartLink connection paths (direct-IP, local-network radio connections). Out of scope.

**NG-8:** No retroactive port config for previously-configured SmartLink accounts beyond a sensible default fallback. Existing accounts get the FlexLib-default port until the user explicitly configures one.

---

## Open questions for Sprint 27

**Q27.1 — UPnP library choice.** `Open.NAT` vs `Mono.Nat` vs native `UPnPNAT` COM. Quick evaluation spike at start of Track B. Criteria: active maintenance, NuGet availability, dependency footprint, screen-reader-irrelevant (no UI).

**Q27.2 — Port range validation for well-known services.** 1024–65535 is the technical range. Do we also warn users if they pick common ports (3389 RDP, 5900 VNC, 8080 HTTP, 22 SSH — wait, 22 < 1024) that could conflict with other services they run? Small UX consideration. Recommendation: warn on a small blocklist of very-common ports, don't prevent selection.

**Q27.3 — NetworkTest caching specifics.** Proposed: 5 min cache on pass, 30 sec on fail. Invalidation on explicit user "Test network" button click. Are those values right? Adjust in Phase 4 based on soak test observations.

**Q27.4 — Diagnostic message localization structure.** Use .resx resource files with a `NetworkDiagnosticStrings.resx`? Or a custom dictionary pattern? Match whatever existing JJ Flex does for other user-facing strings. If JJ Flex currently has no localization structure, use .resx as the seam for future localization.

**Q27.5 — Help-page link protocol.** Settings opens `docs\help\networking\tier1-manual-port.md` — via what mechanism? Notepad? An embedded markdown viewer? An Explorer-opens-file pattern? Recommendation: Explorer `start` on the file path; user's default markdown viewer (or Notepad) handles it. Simpler than embedding a viewer.

**Q27.6 — UPnP mapping lifetime.** UPnP port mappings usually have a configurable lease time. Do we set a short lease (1 hour?) and renew, or a long lease (24 hours? indefinite?) and only clean up on session end? Trade-off: short lease means cleaner state if the app crashes; long lease means fewer renewal round-trips. Recommendation: 1-hour lease with auto-renew while session is active; explicit release on clean session end.

**Q27.7 — Interaction with Sprint 28 per-account port config when tabs land.** Sprint 28 will let users have multiple concurrent sessions (tabs). Each could use a different port. Does Sprint 27's settings model already support this, or does Sprint 28 need to extend it? Recommendation: Sprint 27's per-account port config naturally extends — each session has its own account, its own port. Sprint 28 just creates multiple sessions concurrently; each reads its configured port. No changes needed to Sprint 27's data model.

---

## Test focus for Sprint 27

Will expand into `docs/planning/agile/sprint27-test-matrix.md` during Phase 4. Outline:

- Tier 1: valid port → works; invalid port (out of range, below 1024, in use by another app) → clear error; per-account persistence across restarts.
- Tier 2: UPnP on supporting router → mapping succeeds, traffic works; UPnP on non-supporting router → graceful fallback with diagnostic message; toggle off → mapping released.
- NetworkTest: runs on connect; runs on-demand button click; caches correctly (passed result reused within 5 min); failed result re-tested within 30 sec; background invocation on disconnect doesn't stall UI.
- Track D messages: each distinct failure mode produces the expected message class; live-region announcement on state change; help links open correctly.
- Soak: overnight with Tier 1 + Tier 2 active; verify UPnP lease renewal; verify no memory leaks.

---

## Sprint 27 exit criteria

1. Track A: manual port config works end-to-end, per-account, persisted across restarts.
2. Track B: UPnP opt-in works on supporting routers; falls back gracefully on non-supporting; security warning is visible and accurate.
3. Track C: NetworkTest integrated; results consumed by Track D; caching policy working.
4. Track D: richer diagnostic messages ship; each failure mode produces a distinct, accurate, screen-reader-announced message.
5. Track E (if done): three help docs in `docs\help\networking\`, linked from Settings and diagnostic messages.
6. Test matrix `sprint27-test-matrix.md` green.
7. Overnight soak test passes with both Tier 1 and Tier 2 exercised.
8. Users report (or at minimum, could report based on UI evidence) that they understand their network state after using JJ Flex.

---

## Next session action

Sprint 27 commences after Sprint 26 ships. Track A starts solo; Tracks B, C, E fan out after Track A lands; Track D integrates at the end. Sprint 28 (tabbed multi-radio) can begin planning in parallel with Sprint 27 execution since it depends on Sprint 26 foundations, not Sprint 27.
