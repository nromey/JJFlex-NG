# Sprint 27 — User-Configurable Networking (Tiers 1 + 2 + 3 + Diagnostics)

**Status:** Planning complete — blocked on Sprint 26 completion
**Created:** 2026-04-15 (under max-effort reasoning)
**Major revisions:**
- 2026-04-20 — Tier 3 scope reframed. Original plan deferred Tier 3 to Sprint 29+ because we assumed we'd have to build our own rendezvous server. Noel asked whether Flex already runs the hole-punch infrastructure; confirmed via FlexLib evidence (`WanServer.SendConnectMessageToRadio(serial, HolePunchPort)` + `SendTestConnection` returning `NATHolePunchSupport` boolean) that Flex's SmartLink already coordinates hole-punching end-to-end. Sprint 27 Tier 3 scope narrows from "build rendezvous + STUN + TURN" (infrastructure-heavy) to "accessibly expose Flex's existing hole-punch to users" (client-side UI layer only, zero new server infra). Added as Track F.
- 2026-04-20 — Copy-to-clipboard + save-to-file for `NetworkDiagnosticReport` added to Track D. `ToMarkdown()` method on the type in Track C.

**Authors:** Noel (product direction, accessibility framing, security/compliance framing, Tier 3 scope correction), Claude (track decomposition)
**Parent vision doc:** `docs/planning/hole-punch-lifeline-ragchew.md`
**Parent architecture prerequisite:** `docs/planning/agile/sprint26-ragchew-keepalive-kerchunk.md` (this sprint depends on its WanSessionOwner + SmartLinkSessionCoordinator landing first)
**Branch target:** `sprint27/networking-config` (multi-track; Track A solo, then B/C/E/F parallel, then D integrates)

---

## Sprint goal

Give users accessible control over how JJ Flex reaches their radio behind NAT, across all three networking tiers: manual port specification (Tier 1, user-sovereign), UPnP opt-in (Tier 2, convenience), and UDP hole-punching (Tier 3, restrictive-network). Surface meaningful network diagnostics so users understand what's going wrong when things break. Ship a diagnostic snapshot format (copy-to-clipboard + save-to-markdown) that screen-reader operators can actually use to report issues.

**On Tier 3:** FlexLib + Flex's SmartLink backend already implement end-to-end hole-punching. Flex radios hold a persistent connection to `smartlink.flexradio.com`; FlexLib exposes hole-punch-port coordination on `WanServer.SendConnectMessageToRadio(serial, HolePunchPort)`. The infrastructure works today — it's just not accessibly exposed in SmartSDR's UI. Sprint 27's Tier 3 work is therefore a thin accessibility layer over Flex's existing mechanics, not a parallel infrastructure build. Zero new server-side code on our end.

This sprint is what **unblocks blind users from the SmartSDR dependency** for SmartLink networking. Today a blind user literally cannot configure SmartLink networking — SmartSDR's UI isn't screen-reader friendly, and SmartSDR's port config doesn't even apply to JJ Flex's process anyway (architecturally separate network identity). With Sprint 27, JJ Flex has its own accessible network config.

It also removes a **security / compliance blocker** for organizational users — DISA STIGs, PCI-DSS, HIPAA, and similar require UPnP disabled. Manual port config (Tier 1) is the only tier compatible with those policies, and shipping it as the default (not the "advanced" option) respects the competence of the demographic most likely to run JJ Flex (hams, who are disproportionately sovereign-networking types).

---

## Context

See parent vision doc `docs/planning/hole-punch-lifeline-ragchew.md` for the three-tier networking model, accessibility / security / compliance framing, and why the refactor in Sprint 26 is a prerequisite. In brief:

- Sprint 26 establishes `WanSessionOwner` as a long-lived session owner, stable across radio connections. That stability is what makes "bind network settings to a session" sensible — before Sprint 26, settings would bind to an object that gets recreated on every radio change.
- Three networking tiers: Tier 1 manual port (sovereign, no UPnP), Tier 2 UPnP opt-in (convenience, off by default), Tier 3 UDP hole-punching coordinated by Flex's SmartLink backend (restrictive-network users).
- Default posture: Tier 1 (sovereign), not Tier 2 (auto-magic). This is a product-values choice, not a technical one. Tier 3 is opt-in for users who need it.
- Tier 3's server-side is Flex's responsibility; our role is to expose the client-side controls accessibly and surface NetworkTest's hole-punch probe results in the diagnostic UI.

---

## Scope decision (ratified 2026-04-15, revised 2026-04-20)

**Sprint 27 = Tiers 1 + 2 + 3 + NetworkTest + richer diagnostics + diagnostic snapshot export.**
**Sprint 28 = Tabbed multi-radio + SessionMixer + audio UX (separate concern, prioritized because higher user-visibility and strategic value).**

**Original plan (2026-04-15) deferred Tier 3 to Sprint 29+** because we assumed we'd need to build rendezvous + STUN + TURN infrastructure ourselves.

**Revised understanding (2026-04-20):** Flex already operates the hole-punch infrastructure end-to-end via SmartLink. FlexLib's `WanServer.SendConnectMessageToRadio(serial, HolePunchPort)` + `SendTestConnection`'s `NATHolePunchSupport` boolean are evidence the stack exists and is queryable. What's missing isn't the network mechanics — it's accessible user-facing controls to opt in/out and visibility into the probe results.

Given that reframe:
- Tier 3 client-side work is roughly the size of a single track (opt-in toggle + fallback wiring + NetworkTest result surface), not a dedicated sprint.
- No server infrastructure ownership required on our side for Tier 3 specifically. A rendezvous server of our own would be redundant with Flex's — we don't own the radio firmware that would talk to such a server.
- All three tiers ship together, giving the 4.1.17 release a coherent networking story rather than an "almost done, but hole-punch comes in Sprint 29" gap.
- Users who today can only configure networking via the SmartSDR GUI gain accessible JJ Flex-native equivalents for all three tiers in a single release.

**VPS hosting (jjflexible.radio, firmware, auto-updater manifest, crash report receiver, blindhams.network relocation, blindhams solar compute) is a separate infrastructure track Noel is provisioning in parallel (Hetzner CCX13 or equivalent). Explicitly NOT gated on Tier 3 — the VPS serves other workloads; Tier 3 runs on Flex's infrastructure.**

---

## Track decomposition

Five mandatory tracks (A, B, C, D, F) and one optional track (E). Track A is the serial foundation; B, C, E, F run in parallel after A lands; D integrates C's shape. See CLAUDE.md Sprint Lifecycle for worktree + CLI session conventions.

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
- Structured result type: `NetworkDiagnosticReport` with fields for each subtest, pass/fail, optional detail string, timestamp. Persisted to trace log; exposed to Track D's UI. Includes a `ToMarkdown()` method that renders the full report as a plain-readable markdown document (format spec in Track D's copy-to-clipboard / save-to-file deliverables) — kept in Track C so the data type owns its own serialization.
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
  - "NAT type appears to be symmetric — hole-punch (Tier 3) may fail; manual port forwarding is the reliable path."
  - "Network appears healthy but authentication failed — please sign in again."
  - "UPnP mapping failed; using manual port instead."
  - "Connection dropped and network is unreachable — check your internet."
- Help-link affordances: each failure class links to a local markdown doc in `docs\help\networking\` (or similar). Help docs can be minimal stubs in this sprint; full prose lands in Track E.
- Screen-reader announcement: live-region announces new diagnostic messages when they change (not just silent status-bar update).
- Configurable verbosity: a setting ("Verbose network diagnostics" or similar) that toggles between short-form user-facing messages and longer-form debug messages with timestamps and raw diagnostic data. Off by default; advanced users turn it on.
- **Copy to clipboard.** A button on the diagnostic panel that calls `Clipboard.SetText(report.ToMarkdown())`. Matches the About dialog's "Copy All to Clipboard" precedent (Sprint 24). Enables screen-reader users to paste a diagnostic snapshot into an email, forum post, or bug report without transcribing from speech.
- **Save to file.** A sibling button that writes the same markdown to a user-chosen file path via standard Save dialog. Default filename: `JJFlex-NetworkDiagnostic-YYYY-MM-DD-HHmm.md`. Lets users attach a diagnostic file to an email or GitHub issue.
- **`NetworkDiagnosticReport.ToMarkdown()` format.** Plain-readable markdown (no tables — Noel's memory preference). H1 title with timestamp + radio nickname + session ID. H2 section per subtest group (UPnP, Manual port forward, NAT, SmartLink backend, Auth). Bulleted list under each H2 with result: `- **UPnP TCP forward:** yes (port 4992)` / `- **Manual TCP 4992 reachable:** no — timeout after 3s`. Human-readable as plain text even without a markdown renderer; parses cleanly when pasted into anything that does render markdown. Format is part of Track C's `NetworkDiagnosticReport` deliverable; Track D's buttons invoke it.

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

### Track F — Tier 3: Accessible hole-punch exposure (parallel after A)

**Why parallel-after-A:** depends on Track A's port-config data model (hole-punch still uses a port) but doesn't conflict with B/C/D/E deliverables. Can run concurrently with B/C/E.

**Background:** FlexLib + Flex's SmartLink backend already implement UDP hole-punching end-to-end. Flex radios maintain a persistent connection to `smartlink.flexradio.com`; `WanServer.SendConnectMessageToRadio(serial, HolePunchPort)` passes a hole-punch port that Flex's server uses to coordinate the punch between radio and client. `SendTestConnection` returns an explicit `NATHolePunchSupport` boolean confirming probe-time support. Tier 3 already works on the network; the gap is accessibility. SmartSDR exposes Tier 3 selection; it's not accessible to screen-reader users.

**Scope:**

- **Settings UI:** "Connection mode" accessible radio group (or equivalent) in the Network tab with three options. Each with a one-sentence explanation.
  - *Manual port forward only (Tier 1)* — you configure your router; JJ Flex uses the port you set. Sovereign default.
  - *Manual + UPnP (Tier 1 + 2)* — JJ Flex asks your router to open the port automatically via UPnP. Security warning attached.
  - *Allow automatic hole-punch (Tier 1 + 2 + 3)* — if direct port routing isn't working, let Flex's SmartLink coordinate a UDP hole-punch. Works through most home NAT; may fail on symmetric-NAT networks (some corporate / mobile-carrier configurations). Opt-in with explanation.
- **Binding to `WanSessionOwner`:** the selected mode maps to the `HolePunchPort` argument on `WanServer.SendConnectMessageToRadio`. Mode 1: pass 0 (no hole-punch). Mode 2: pass 0 (UPnP-mapped port does the work). Mode 3: pass the configured port; SmartLink coordinates the punch.
- **Fallback logic:** when hole-punch fails (symmetric NAT on either side, UDP blocked, SmartLink timeout), Track D's diagnostic UI reports the failure mode AND the app falls back silently to the next tier down (Tier 3 → Tier 2 if UPnP enabled → Tier 1). No user action required; status-line announcement reports which tier actually succeeded.
- **NetworkTest surface:** `NATHolePunchSupport` boolean from NetworkTest gets a visible home — the diagnostic panel (Track D) shows "Hole-punch support: yes / no" alongside UPnP-TCP / UPnP-UDP / manual-forwarded-TCP / manual-forwarded-UDP. Markdown export (via `NetworkDiagnosticReport.ToMarkdown()`) includes it.
- **Accessibility:** mode selection is a true accessible radio group (not a dropdown) so screen readers announce all three options on focus; Left/Right arrow navigation; mode change announces via live-region ("Hole-punch enabled" / etc.).
- **Security framing:** the "Allow automatic hole-punch" mode includes a one-paragraph explanation that enabling this means JJ Flex cooperates with Flex's SmartLink to open a temporary UDP flow through your router to the radio. Less persistent than a manual port forward; still requires trust in Flex's SmartLink coordination. Organizational users who have manual-forwarding-only policies can stay on Tier 1.

**Deliverable:** users can opt into Flex's hole-punch end-to-end from JJ Flex without ever touching SmartSDR. NetworkTest's hole-punch probe result is exposed in both the diagnostic panel and the markdown export. Fallback logic handles symmetric-NAT failures gracefully.

**Exit criterion:** the Settings mode selector works across Tier 1/2/3; hole-punch succeeds on the same networks where SmartSDR's equivalent option succeeds; induced failure (symmetric-NAT simulation or UDP block) falls back cleanly to Tier 2 or Tier 1 and announces which tier succeeded; NetworkTest's hole-punch probe result is accurately surfaced in Track D's diagnostic UI.

---

## Phase ordering within Sprint 27

**Phase 1 (serial, foundation):** Track A alone. Foundation settles. End-of-phase: manual port config works, saved per-account, bound to session. Small scope — settings UI + persistence + `IWanSessionOwner` binding.

**Phase 2 (parallel, fan-out):** Tracks B + C + E + F in four worktrees, four CLI sessions (see CLAUDE.md Sprint Lifecycle). Tracks B and C are independent; Track E is independent of everything; Track F depends on Track A's port model but doesn't conflict with B/C/E. Four parallel workstreams. Moderate scope per track (UPnP client, NetworkTest wrapper + report, help docs, Tier 3 accessibility layer).

**Phase 3 (serial, integration):** Track D integrates Phase 2 output (consumes Track C's `NetworkDiagnosticReport`, extends Phase 3 status UI from Sprint 26, surfaces Track F's tier-selection state, pulls help-link targets from Track E, adds copy-to-clipboard + save-to-file buttons). The richest UI phase of the sprint.

**Phase 4 (serial, verification):** Test matrix, manual test pass, soak test (overnight with all three tiers exercised), release readiness.

**Sizing note:** time estimates deliberately omitted. Sprint scope is measured in architectural newness (one new settings UI, one UPnP client, one NetworkTest wrapper, one diagnostic format, one Tier 3 binding, help docs) + decision checkpoints (UPnP library choice, hole-punch failure UX, diagnostic-verbosity default) + external dependencies (one UPnP library, FlexLib's hole-punch API surface). Total scope is bigger than Sprint 26 but mostly additive, with clear per-track boundaries that prevent cross-contamination during parallel execution.

---

## Non-goals for Sprint 27

**NG-1:** No JJFlex-operated rendezvous server. Tier 3 uses Flex's SmartLink backend exclusively; our work is the accessibility client layer only. (If we ever want to build a fallback rendezvous — e.g., for SmartLink-outage disaster recovery — that's a separate later sprint with infrastructure ownership implications.)

**NG-2:** No STUN or TURN implementation on our side. Flex's SmartLink handles NAT traversal coordination; we pass through the `HolePunchPort` and consume the probe results.

**NG-3:** No tab UI. (Sprint 28.)

**NG-4:** No `SessionMixer`, no audio policy (pan, gain, mute, duck, smart-squelch). (Sprint 28.)

**NG-5:** No string localization of diagnostic messages. Strings live in a resource file so localization is possible later without refactoring, but only English ships in Sprint 27.

**NG-6:** No external help-page hosting (jjflexible.radio) in-sprint. Sprint 27 ships in-app markdown in `docs\help\networking\` with local file links only. jjflexible.radio hosting is a parallel infrastructure track (Noel's VPS provisioning work) that may catch up with the release or land shortly after.

**NG-7:** No changes to existing non-SmartLink connection paths (direct-IP, local-network radio connections). Out of scope.

**NG-8:** No retroactive port config for previously-configured SmartLink accounts beyond a sensible default fallback. Existing accounts get the FlexLib-default port until the user explicitly configures one.

**NG-9:** No changes to Flex's hole-punch protocol, NAT detection algorithms, or backend coordination. We surface what Flex exposes; we don't reimplement or optimize.

---

## Open questions for Sprint 27

**Q27.1 — UPnP library choice.** ~~`Open.NAT` vs `Mono.Nat` vs native `UPnPNAT` COM.~~ **Resolved 2026-04-20: native Windows UPnPNAT COM (`HNetCfg.NATUPnP` ProgID, `IUPnPNAT` / `IStaticPortMappingCollection` interfaces).** Rationale: (a) zero NuGet dependency avoids installer-footprint cost and licence-vetting overhead; (b) aligns with JJFlex's Windows-only posture (no value to a cross-platform library we can't deploy); (c) `UPnPNAT` COM API is effectively frozen-stable since Windows XP, lower maintenance risk than `Open.NAT` (stagnant since ~2017) or `Mono.Nat` (intermittently maintained); (d) JJFlex already uses COM interop elsewhere (WebView2, Win32 HMENU) so the pattern is not foreign. Implementation via `Type.GetTypeFromProgID` + `dynamic` dispatch so no Primary Interop Assembly is required.

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
3. Track C: NetworkTest integrated; results consumed by Track D; `NetworkDiagnosticReport.ToMarkdown()` produces plain-readable output; caching policy working.
4. Track D: richer diagnostic messages ship; each failure mode produces a distinct, accurate, screen-reader-announced message; copy-to-clipboard and save-to-file buttons produce usable markdown.
5. Track E (if done): three help docs in `docs\help\networking\`, linked from Settings and diagnostic messages.
6. Track F: Tier 3 hole-punch toggle in Settings works; hole-punch succeeds on the same networks SmartSDR's equivalent succeeds on; graceful fallback to Tier 2/1 on hole-punch failure; NetworkTest's `NATHolePunchSupport` result visible in the diagnostic panel and markdown export.
7. Test matrix `sprint27-test-matrix.md` green.
8. Overnight soak test passes with Tier 1, Tier 2, and Tier 3 all exercised.
9. Users report (or at minimum, could report based on UI evidence) that they understand their network state after using JJ Flex — including which tier is active and why.

---

## Phase A.0 findings (2026-04-20, pre-coding audit)

Before starting Track A coding, a pre-design audit was run against the current codebase. Key findings shifted the scope of A.2 and A.3 from the original plan text:

**1. The Network tab already exists.** `JJFlexWpf/Dialogs/SettingsDialog.xaml` lines 310-367 already contain a Network tab with `PortForwardEnabledCheck`, `PortForwardTcpBox`/`PortForwardUdpBox` fields (default 4992), a separate-ports advanced toggle, and an "Apply to connected radio" button. The code-behind already validates 1024–65535, reads current state via `RefreshNetworkTabFromRig()`, and applies via `FlexBase.SetSmartLinkPortForwarding()`. The live-region status element (`NetworkCurrentStateText`) is also present. Track A's UI work is therefore *wiring and extending*, not scaffolding.

**2. FlexLib exposes port config post-connect only — there is no pre-connect port-injection API.** The port is radio-side firmware state applied via `Radio.WanSetForwardedPorts(bool enabled, int tcpPort, int udpPort)` (FlexLib) wrapped by `FlexBase.SetSmartLinkPortForwarding(...)` (Radios/FlexBase.cs:249). `WanServer`'s session-construction path and `WanServer.SendConnectMessageToRadio` do not accept a listen-port argument. This is an architectural correction to the plan's original Track A scope line "when session constructs its underlying connection, it uses the configured port..." which assumed a ctor-injection path that doesn't exist. **Revised Track A binding:** a post-connect hook in FlexBase (near the `WANConnectionHandle` assignment around line 1669, or inside `openTheRadio`) reads the active account's saved port preference and calls `SetSmartLinkPortForwarding(true, port, port)` once the radio is connected, logging to trace and announcing via the existing live region. No changes to `IWanSessionOwner` or `IWanServer` interfaces are required for Track A.

**3. SmartLink account persistence lives in `SmartLinkAccountManager.cs`.** Accounts serialize to `%AppData%\JJFlexRadio\SmartLinkAccounts.json` (DPAPI-encrypted tokens, plain metadata). The `SmartLinkAccount` class at the bottom of the file holds `Email` + `FriendlyName` + token fields. **Track A adds a nullable `int? ConfiguredListenPort` field** to `SmartLinkAccount` plus helper methods on `SmartLinkAccountManager` (`GetConfiguredPort(email)` / `SetConfiguredPort(email, port)`). Null = fall back to FlexLib's default behavior, satisfying NG-8 (existing accounts don't retroactively change behavior).

**4. The `SmartLinkSessionCoordinator.EnsureSessionForAccount(accountId)` path already keys by email and is ready for per-account lookups.** The `accountId` plumbed into `WanSessionOwner.AccountId` is the Auth0 profile email, matching `SmartLinkAccount.Email`. No new identity scheme is needed.

**5. Test button scope clarification.** The plan's original phrasing "a 'test' button that attempts to bind the port" assumed a client-side listen port. Because the port Track A configures is actually the *radio's* firmware listen port, there is nothing local for a Test button to bind. Track A's Test button does *local numeric validation only* — range check (1024–65535) plus a small blocklist of commonly-conflicting well-known ports (3389 RDP, 5900 VNC, 8080 HTTP-alt) — and announces the result to the existing live region without persisting. *Actual reachability testing from the user's public IP to the radio's forwarded port* is Track C's NetworkTest responsibility, not Track A's. Updating plan: Test button is local-validate-only in Sprint 27 Track A.

---

## Phase 1 complete (Track A, 2026-04-20)

Track A landed on `sprint27/networking-config` in four commits:

- **A.0 (`28329f23`)** — pre-design audit findings added to this plan; scope of A.2 and A.3 revised based on what the codebase already provides (Network tab already existed; FlexLib has no pre-connect port API; port applies post-connect via `Radio.WanSetForwardedPorts`).
- **A.1 (`f9ba1e40`)** — `SmartLinkAccount.ConfiguredListenPort` (nullable int), `StoredAccount` field + JSON round-trip (now internal for tests), `SmartLinkAccountManager.GetConfiguredPort` / `SetConfiguredPort` / `IsValidPort`. 13 new unit tests, all 27 `Radios.Tests` tests green.
- **A.2 (`a66012f3`)** — `FlexBase.ApplyAccountPortPreferenceIfAny()` invoked from the post-connect success branch of `Connect(serial, lowBW)` for `RemoteRig`-only. Silent no-op when no account, no preference, or radio already matches.
- **A.3 (`b49cb750`)** — `FlexBase.SaveCurrentAccountListenPort` + `HasCurrentSmartLinkAccount` + `CurrentSmartLinkAccountEmail`. Existing Network tab extended in place: Tier 1 framing prose, `Apply` now also saves preference to account, new `Test port` button for local validation (range + common-conflict blocklist — 3389/5900/8080 — warnings only). All announcements route through the existing `NetworkCurrentStateText` live region + `ScreenReaderOutput.Speak`.

**A.4 verification rollup:**
- Full solution Debug x64 build clean (0 errors, ~1400 warnings — existing / non-regressions).
- `Radios.Tests` full suite: 27/27 passed.
- `build-debug.bat` produced `4.1.16.79` → NAS `historical\4.1.16.79\x64-debug\` (zip + NOTES + exe + pdb). Dropbox untouched (internal verification only).
- No smoke test against a live SmartLink radio yet — deferred until Noel has time to connect Don's 6300 or his own radio. Auto-apply-on-connect is trace-instrumented so any mis-application will be visible in the trace log.

**Exit criterion (Phase 1):** met on the code side. Manual smoke confirmation against a real SmartLink connection is outstanding but does not block subsequent tracks from starting. Auto-apply is a safe idempotent no-op when no preference is set.

**Execution-mode correction (2026-04-20):** Sprint 27 runs **serial, single-branch, no worktrees** — Noel's explicit decision at the top of the sprint. The "parallel after A" phrasing earlier in this document is a vestige of CLAUDE.md's default sprint-lifecycle template; it does not describe how this sprint actually runs. Each track lands in commits on `sprint27/networking-config`, in sequence, before the next starts.

**Serial track order (Track A complete; remaining order):**

1. **Track C** — `NetworkTest` wrapper + `NetworkDiagnosticReport` (defines types Track D consumes).
2. **Track B** — UPnP opt-in.
3. **Track F** — Tier 3 accessible hole-punch.
4. **Track E** — help docs (prose-only, no code dependencies).
5. **Track D** — rich disconnect diagnostics + copy-to-clipboard + save-to-file (integrator; must land last because it consumes outputs from B, C, and F).

---

## Phase C.0 findings (2026-04-20, pre-coding audit of FlexLib NetworkTest)

Audit of `FlexLib_API/FlexLib/WanServer.cs` + `WanTestConnectionResults.cs` + `SslClient.cs`:

**1. API surface.** `WanServer.SendTestConnection(string serial)` (WanServer.cs:591–604) is the invocation. Returns void. Results arrive via the `WanServer.TestConnectionResultsReceived` event (WanServer.cs:584) with a `WanTestConnectionResults` payload. No dedicated NetworkTest class; the functionality is inlined on `WanServer`.

**2. Invocation shape.** Fire-and-forget event-driven. `SendTestConnection` writes the command string to SmartLink and returns immediately. Not `Task`-returning, not async/await-shaped, no result parameter on the call. Caller must subscribe to the event before invoking to avoid losing the result.

**3. Result fields — only 5 booleans.** `WanTestConnectionResults` (WanTestConnectionResults.cs:20–24) exposes: `upnp_tcp_port_working`, `upnp_udp_port_working`, `forward_tcp_port_working`, `forward_udp_port_working`, `nat_supports_hole_punch` — plus `radio_serial` for identification. **No NAT-type classification, no UPnP failure modes, no SmartLink-backend reachability as a distinct signal, no auth-validity signal, no public-IP discovery.** Public IP is a separate property (`WanServer.SslClientPublicIp`) populated from the `"application info"` message path, not from NetworkTest.

**4. Threading.** `TestConnectionResultsReceived` fires on `SslClient`'s background listener thread (SslClient.cs:54–133). No dispatcher marshalling. Consumers must marshal to the UI thread themselves before touching WPF controls.

**5. Error surface.** Implicit and sparse. If SmartLink is unreachable when `SendTestConnection` is called, `WanServer` silently returns (logs Debug only, no event fires, no exception). No timeout in the SDK itself — if SmartLink never responds, the event never fires. Missing/malformed response fields default to false via `TryParse` in the parser. Callers must enforce their own timeout policy.

**6. Precondition.** Must be authenticated + connected to SmartLink (`IsConnected == true`), but the radio session need not be active. That means post-disconnect diagnosis (plan scenario c) is feasible as long as SmartLink itself is still reachable.

---

## Plan scope correction (Track C + Track D ToMarkdown format)

The plan's original Track D `ToMarkdown()` spec lists five H2 sections: "UPnP, Manual port forward, NAT, SmartLink backend, Auth". **Three are fillable** from FlexLib's NetworkTest surface; **two are not**:

- **UPnP section** — `upnp_tcp_port_working` + `upnp_udp_port_working`. Fillable.
- **Manual port forward section** — `forward_tcp_port_working` + `forward_udp_port_working`. Fillable.
- **NAT section** — only `nat_supports_hole_punch` (binary). No NAT-type classification. Fillable as a single bullet.
- **SmartLink backend section** — *not available* from NetworkTest. SmartLink reachability is implicit: if NetworkTest completes, the backend is reachable; if it times out, it isn't. Track C will represent this via a single "Overall" header-bullet showing the timestamp + whether the probe got a response at all, rather than a separate H2 section.
- **Auth section** — *not available* from NetworkTest. Auth validity is signaled elsewhere (`WanApplicationRegistrationInvalid` event). Track C will *not* include an Auth section in `ToMarkdown` — auth health is orthogonal to network health and belongs to a different signal path.

Track D's message dictionary ("Network appears healthy but authentication failed — please sign in again") will use the existing `WanApplicationRegistrationInvalid` event for the auth branch, not NetworkTest. This is a scope clarification, not a scope reduction — the user-facing diagnostic richness is preserved by routing different signals to the right places.

---

## Track C complete (2026-04-20)

Track C landed in four commits on `sprint27/networking-config` (serial execution after Track A):

- **C.0 (`0f01c8c5`)** — FlexLib NetworkTest API audit. Key findings: event-driven fire-and-forget surface (`WanServer.SendTestConnection` + `TestConnectionResultsReceived`), five-boolean result payload (`WanTestConnectionResults`), no NAT-type classification, no backend-reachability or auth signals, event fires on SSL listener thread, no SDK-enforced timeout. Plan's Track D ToMarkdown sections narrowed from 5 to 3 fillable groups.
- **C.1 (`768f488e`)** — `NetworkDiagnosticReport` DTO (FlexLib-free) with five nullable-bool subtest fields + timestamp + serial + error-detail slot. `ToMarkdown()` renders plain-readable markdown per Track D's format spec: H1 title with metadata, H2 per subtest group (UPnP / Manual port forward / NAT), bulleted entries. Error-state collapses to short form. 9 tests including an explicit no-markdown-table accessibility invariant.
- **C.2 (`59558bbc`)** — `NetworkTestRunner` async-friendly facade: `RunAsync(serial, forceRefresh, timeout, ct)` returns `Task<NetworkDiagnosticReport>`. TTL cache (5 min pass / 30 sec fail), concurrent-call dedup, caller-enforceable timeout with late-event-still-updates-cache semantics, `ReportReady` event, `InvalidateCache` for user-initiated refresh, `Dispose` unsubscribes. Extended `IWanServer` + `WanServerAdapter` + `MockWanServer` with the new method and event. 13 tests covering cache, dedup, timeout, cancellation, late event, invalidation, Dispose.
- **C.3 (`59311c8a`)** — Invocation points wired: (a) `FlexBase.Connect` post-connect branch kicks a fire-and-forget probe via `KickPostConnectNetworkTest`, so the cache is warm before the user interacts. (b) New Settings "Test _network" button next to the Track A "Te_st port" button, with its own `NetworkDiagnosticResultText` live region + one-line yes/no/unknown summary. `WanSessionOwner` owns a `NetworkTestRunner` internally; `IWanSessionOwner` grows three thin pass-through members (`RunNetworkDiagnosticAsync`, `GetLastNetworkReport`, `NetworkReportReady`). Scenario (c) post-disconnect heuristic deferred to Track D where it fits alongside the richer diagnostic UI.

**C.4 verification rollup:**

- Full solution Debug x64 build clean (0 errors, ~1400 warnings — non-regressions).
- `Radios.Tests` full suite: 49/49 passed (27 pre-existing + 22 new from C.1 and C.2).
- `build-debug.bat` produced `4.1.16.85` → NAS `historical\4.1.16.85\x64-debug\` (zip + NOTES + exe + pdb). Dropbox untouched.

**Exit criterion (Track C):** met on the code side. NetworkTest runs on connect (scenario a) and on-demand (scenario b); results are logged; caching policy verified by unit tests; `NetworkDiagnosticReport.ToMarkdown()` produces clean plain-readable markdown. Track D will consume the report type in its message dictionary and copy/save buttons.

---

## Track B complete (2026-04-20)

Track B landed in five commits on `sprint27/networking-config`:

- **B.0 (`8893cd7d`)** — library decision: native Windows UPnPNAT COM (`HNetCfg.NATUPnP`, reflection-dispatched). Zero NuGet dependency, stable API since Windows XP, aligns with JJFlex's Windows-only posture. Open.NAT and Mono.Nat rejected on maintenance risk.
- **B.1 (`1730cc88`)** — `UPnPPortMapper` public class with an internal `IUPnPBackend` seam + `ComUPnPBackend` production impl. Reflection (not `dynamic`) so no Microsoft.CSharp reference needed. Public surface: `IsAvailable`, `TryAddMapping`, `TryRemoveMapping`, `TryGetExternalIpAddress` (returns null in v1). Every op funnels through a `SafeCall<T>` that swallows all exceptions to bool/null and traces them. 15 tests including a case-regression guard on the `TCP`/`UDP` string mapping.
- **B.2 (`6172e6d0`)** — `SmartLinkAccount.UPnPEnabled` (bool, default false) + JSON round-trip; `SmartLinkAccountManager.{Get,Set}UPnPEnabled(email)`; `FlexBase.CurrentAccountUPnPEnabled` + `SaveCurrentAccountUPnPEnabled`. Settings Network tab gains a Tier 2 block below Tier 1 with checkbox, italic security-warning prose, and a `RecomputeUPnPCheckboxEnablement` helper that gates the checkbox on `PortForwardEnabledCheck` being ON + a valid Tier 1 port parsing. Apply button extended to save both Tier 1 and Tier 2 preferences together. 5 new persistence tests (64 total green).
- **B.3 (`d58d6780`)** — `FlexBase.ApplyAccountUPnPPreferenceIfAny()` in the Connect success branch (after A.2 auto-apply, before C.3 NetworkTest kick); `ReleaseUPnPMappingsIfAny()` in Disconnect before `theRadio = null`. Private-IP gate (`IsPrivateIPv4`) skips UPnP attempts when the radio's IP is not RFC1918 — the roaming case where JJ Flex on a hotel LAN would punch holes in the wrong router. Per-port success tracking so Disconnect only removes mappings we actually added.

**B.4 verification rollup:**

- Full solution Debug x64 build clean (0 errors).
- `Radios.Tests` full suite: 64/64 passed (49 pre-existing + 15 new from B.1 + some B.2 coverage already counted there).
- `build-debug.bat` produced `4.1.16.90` → NAS `historical\4.1.16.90\x64-debug\`. Dropbox untouched.

**Exit criterion (Track B):** the code-side pieces are all in place. The UPnPPortMapper has exhaustive unit coverage for its wrapper / input validation / exception handling. The real UPnP path — a router that speaks UPnP, actually accepting a mapping, SmartLink traffic successfully reaching the radio through it — is inherently untestable without live hardware. B.4 smoke test against Noel's router will confirm (a) UPnP attempts run on a cold connect, (b) mapping appears in the router admin UI, (c) Disconnect cleans up the mapping, (d) roaming (non-RFC1918 radio IP) silently skips the attempt.

---

## Track F complete (2026-04-20)

Track F landed in three commits on `sprint27/networking-config` (serial after Track B):

- **F.0 (`941f774c`)** — replaced Track B.2's `UPnPEnabled` bool with a three-state `SmartLinkConnectionMode` enum (`ManualPortForwardOnly`, `ManualPlusUpnp`, `AutomaticHolePunch`). Cumulative tier semantics — enum ordinals are monotonic so callers can gate with `mode >= ManualPlusUpnp` / `mode >= AutomaticHolePunch`. JSON serialization uses readable string names. `SmartLinkAccountManager.Get/SetConnectionMode`, `FlexBase.CurrentAccountConnectionMode` + `SaveCurrentAccountConnectionMode`, `ApplyAccountUPnPPreferenceIfAny` retargeted to check `mode >= ManualPlusUpnp`. 7 updated/new tests (including ordinal-monotonicity pin + JSON-string-name invariant).
- **F.1 (`f9e7beba`)** — replaced the Tier 2 single checkbox with a true three-option accessible radio group (GroupBox + three RadioButtons in a shared GroupName). Each option has its own per-item explanation inline. `RecomputeConnectionModeAvailability` gates Tier 2/3 on Tier 1 port validity + falls back to Tier 1 when the current selection is no longer valid. Mode-change announcement via `ScreenReaderOutput.Speak`, suppressed during programmatic loads (`_suppressConnectionModeAnnouncements`).
- **F.2 (`ceacc03c`)** — renamed the historical `flags` int on `IWanServer.SendConnectMessageToRadio` to `holePunchPort` (its actual semantic). Added optional `holePunchPort` parameter (default 0) on `IWanSessionOwner.ConnectToRadio`. `FlexBase.sendRemoteConnect` now computes the hole-punch port from `_currentAccount.ConnectionMode`: `AutomaticHolePunch` + configured port → that port, otherwise 0. `WanSessionOwner` passes it through to `_wan.SendConnectMessageToRadio` instead of the hardcoded 0. `MockWanServer` records `LastSendConnectHolePunchPort` for future Tier-3 dispatch tests.

**F.3 verification rollup:**

- Full solution Debug x64 build clean (0 errors).
- `Radios.Tests` full suite: 64/64 passed.
- `build-debug.bat` produced `4.1.16.94` → NAS `historical\4.1.16.94\x64-debug\`. Dropbox untouched.

**Exit criterion (Track F):** accessible Tier 1/2/3 selector lives in Settings with screen-reader-friendly semantics; `holePunchPort` flows end-to-end from Settings selection → account persistence → session connect → FlexLib `SendConnectMessageToRadio`; Tier 2 UPnP gating is mode-derived. The "automatic fallback Tier 3 → Tier 2 → Tier 1 on failure" path is handled by FlexLib/SmartLink on the server side; JJ Flex doesn't need to reimplement it.

**Zero new server-side infrastructure** on our end — the whole track is a client-side accessibility layer over Flex's existing hole-punch coordination, consistent with NG-1 and NG-2. "We don't implement UPnP, we ask Windows' service. We don't implement hole-punch, we advertise a port so Flex's server can coordinate it." (Noel, 2026-04-20.)

---

## Next session action

Sprint 27 Track F complete. Next track in the serial order is **Track E** (help docs, prose-only — `tier1-manual-port.md`, `tier2-upnp.md`, `diagnostics.md`). After E comes **Track D** — the integrator: rich disconnect diagnostic messages, post-disconnect NetworkTest auto-run (scenario c deferred from C.3), copy-to-clipboard + save-to-file buttons for `NetworkDiagnosticReport.ToMarkdown()`. D must land last because it consumes outputs from B/C/F.
