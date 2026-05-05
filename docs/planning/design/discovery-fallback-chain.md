---
type: architectural design pull doc — round 2 (locked decisions from round 1 + new additions)
review needed: ACK or revise; final round before locking the design and starting build-now-ship-later
priority: high — directly affects 4.2.0 release-decision and shapes the next outbound build to Don (R6)
draft author: Claude
date: 2026-05-04 (round 2)
related memories: project_flexlib_4218_discovery_investigation.md, project_firmware_install_dependency_strategy.md, project_friction_tax_principle.md, project_no_silent_phone_home.md
---

# Discovery as a chain, not a gate (round 2)

## What's locked from round 1

Round 1 was reviewed and these decisions are locked:

- **Chain-not-gate framing approved** — "I like this approach."
- **Verification timing: confirmed now.** Direct-IP-connect is supported via the established vendor-patch pattern (verified 2026-05-04 against `Radio.cs` lines 197 and 2111). The Radio class is `public`; constructors are `internal`. Vendor-patch FlexLib to expose a public `Radio.CreateFromIp(model, serial, name, ip, version)` factory that wraps the existing internal constructor. Same shape as the existing `SslClientTls12` wrapper and the R5 MMCSS patch. ~10-line patch.
- **4.2.0 release-readiness: ship the chain alongside the R5 root-cause work, not gated on it.** "We need both things available, I think we do the chain if possible." Build R5 fix and chain in parallel; chain is the architectural answer regardless of R5 outcome.
- **Passive diagnostic capture: comfortable with the pattern.** Trace stays local; export only via user-consented "Ask for help" support package per `project_no_silent_phone_home.md`.
- **Phasing OK as drafted.**
- **Build-now-ship-later: hold until this round-2 ACK.** Then spin up `track/discovery-fallback-chain`.

## What's new in round 2

Four substantive additions you raised in chat that warrant capture in the canonical design:

1. **SmartLink-speed motivation** — even on a healthy LAN where UDP discovery works fine, SmartLink-configured users today round-trip through FlexRadio's cloud server when connecting to their own home radio. Rung 1 (cached IP) skips that round-trip entirely. The chain isn't just remediation — it's a startup-latency win for the SmartLink population that was always there waiting to be claimed.
2. **Rung 1b — cached WAN IP** — extending the cached-IP idea to remote/SmartLink connections. If we cached the radio's last-known external IP, we could attempt direct TCP/TLS to its `PublicTlsPort` and skip Auth0 + SmartLink server query + NAT-coordination entirely (or at minimum skip the server query). 5-10x speedup over standard SmartLink in the best case. **Auth-model research required** before locking implementation shape.
3. **Help text for finding radio's IP + network device enumeration** — concrete suggestions for the Manual IP entry rung's help text, and concrete answers on whether we can enumerate LAN devices without router admin access (yes, three ways).
4. **Trace and crash/feedback wiring made explicit** — the chain's per-rung diagnostic data routes through trace persistence (`project_trace_persistence_design.md`) and surfaces in user-triggered "Ask for help" support packages per `project_sprint29_crash_reporter_vision.md`.

## Why this doc exists (motivations — round 2 expanded)

You raised the architectural reframing during the R4-investigation conversation. The core reasoning still holds: SmartLink already works for connecting to Don's radio without UDP discovery succeeding — by definition, since SmartLink uses a known server endpoint and the radio's serial / IP, never a local broadcast. That working WAN-side pattern generalizes to a working LAN-side pattern: **if we have an IP for the radio, we don't need UDP discovery to find it.**

But the more general framing (round 2): **every connection-metadata we've seen before is a hint for skipping work next time.** Cached LAN IP skips UDP discovery. Cached WAN IP skips SmartLink server query (and possibly auth). The radio's last-known network position is information we paid for once and can reuse on every future connect. The discovery fallback chain is the first concrete instance of this cache-the-rendezvous-state pattern.

Four motivations now drive the design:

1. **Resilience** — UDP discovery's failure (today's R4 bug, future variants) becomes non-fatal.
2. **Performance for SmartLink users on LAN** — skip the cloud round-trip when the radio is locally reachable.
3. **Performance for SmartLink users remote** (new in round 2) — skip Auth0 + server query + NAT coordination via cached WAN IP.
4. **Phase D firmware update can succeed even on broken-discovery radios** — the chicken-and-egg dissolves.

## The chain

A connection request walks rungs. The first rung that yields a working radio handle wins. Rungs run concurrently where independent; the first-success-wins-and-cancels-others orchestration is Phase 3 of implementation.

### Rung 1a — Cached LAN IP (LOCKED, in R6)

If the user has a working JJ Flex install that connected on the same LAN recently, the radio's LAN IP is in `%AppData%\JJFlexRadio\<callsign>_autoConnectV2.xml`. Rung 1a reads that XML, attempts a TCP connection on the standard FlexLib port to the cached IP, and if the radio answers with a FlexLib handshake, we're done. Zero broadcast needed. Zero user prompts. Zero seconds of wait.

### Rung 1b — Cached WAN IP (CANDIDATE, RESEARCH COMPLETE)

For SmartLink-configured users, cache the radio's last-known external IP from the last successful SmartLink session. On future connects, attempt direct TCP/TLS to the radio's `PublicTlsPort` (line 2209 of `Radio.cs` — the constant exists). The cached external IP shortcuts as much of the standard SmartLink negotiation as the protocol allows.

**Auth research result (2026-05-04):** memo at `docs/planning/research/smartlink-auth-direct-connect-feasibility.md`.

**Verdict: constrained case.** The TLS handshake itself is anonymous (`validateCert: false`, no client credentials at TLS layer per `TlsCommandCommunication.cs:26`). But immediately after TLS connect, the radio expects a `wan validate handle=<H>` command where `H` is an opaque per-attempt token issued by the SmartLink server (`WanServer.cs:394-409`, `Radio.cs:2196-2214`). The handle cannot be constructed or cached locally — it has to be fetched from the SmartLink server fresh on each connect.

**What Rung 1b actually saves (constrained case):**
- ~500ms — skip the radio-list refresh (we already know which radios the user owns from local cache, no need to re-query SmartLink for the list)
- variable savings — skip NAT-traversal coordination when the cached WAN IP is reachable directly via port-forward / UPnP at the network layer
- **does NOT skip:** the SmartLink server round-trip for the per-attempt validate handle

Smaller speedup than the best case, but still a measurable win and still aligned with the cache-the-rendezvous-state pattern.

**Live-test upgrade path:** the auth memo flagged a testable question — `wan validate handle=<H>` could be **strictly enforced** (radio rejects subsequent commands if invalid/missing) or **informational/telemetry only** (radio logs the handle but doesn't gate behavior on it). Code reading can't distinguish; only live testing on a real radio with a deliberately-wrong handle resolves it. ~5 minutes of probing on Don's or Justin's radio. If informational-only: verdict upgrades to **best case** and Rung 1b skips the SmartLink server entirely. If strictly enforced: constrained case stands. The probe can happen during R6 testing or as a separate exercise after design ACK.

**Privacy note:** cached WAN IPs are mildly sensitive (can hint at user location). They stay LOCAL ONLY — never in trace exports, never in crash reports, never in Data Provider sync. When user-triggered "Ask for help" support packages are generated, WAN IPs are redacted by default. Add to redaction list when the support-package format is finalized.

### Rung 2 — TCP/4992 subnet probe (LOCKED, Phase 2)

If no cached IP exists (fresh install, new radio, IP changed), walk the local subnet asynchronously. Determine the user's local /24 from the active network interface (read from OS, don't assume `192.168.1.0/24`). Async-TCP-connect to port 4992 on each of the 254 hosts with a ~100ms timeout. Anything that accepts is either a Flex or a port collision; confirm with a tiny FlexLib handshake. Roughly 3-5 seconds total for a typical home subnet, no privileges, no broadcast.

### Rung 3 — UDP discovery (LOCKED, today's path retained)

For completeness and as a fallback for radios on subnets larger than /24 or unusual network setups. If the bug is fixed in a future release, this becomes the fastest path again on networks where it works.

### Rung 4 — SmartLink-as-LAN-fallback (LOCKED, Phase 5)

If the user is configured for SmartLink AND none of the LAN-side rungs worked, force the SmartLink path. Slower (round-trip via FlexRadio's server) but works. Useful when LAN is misbehaving but the radio is reachable through cloud relay.

### Rung 5 — Manual IP entry (LOCKED, Phase 4)

The honest "type your radio's IP here" fallback. Surfaces only after Rungs 1-4 fail, with a clear message: "We couldn't find your radio automatically. If you know your radio's IP address, you can enter it here." Help text suggestions (round 2 addition):

- *Look at your radio's front panel — most Flex 6000/8000 radios show their network IP in a Settings or Network menu.*
- *Look at your router's admin page for connected devices, if you have access.*
- *Ask JJ Flexible to show last-known IPs (we'll add a "show recent radio IPs" button that reads `autoConnectV2.xml` and similar files).*
- *Common defaults: home routers usually assign IPs in the 192.168.1.x range.*

## Network device enumeration without router admin

Three approaches that find LAN-connected devices without router DHCP table access:

- **ARP table read.** Windows already maintains a list of recently-seen LAN IP/MAC pairs (`arp -a`). Read this without elevation, filter by FlexRadio's MAC OUI prefix (or any candidate's MAC ranges), find their radios.
- **TCP/4992 subnet probe.** Already covered by Rung 2 — find radios by behavior (port-accept), not by name.
- **mDNS / Zeroconf browse.** Even though Flex doesn't *advertise* via mDNS today, browsing for `_flex._tcp.local`, `_workstation._tcp.local`, etc. is free and forward-compatible if Flex ever adds it.

The ARP-table read is a particularly cheap addition to the chain — could become a Rung 1.5 between cached-IP and subnet-probe, costing essentially zero seconds because it just reads existing state.

## What this does to the 4.2.0 release picture

This is the load-bearing implication. **4.2.0 ships with the fallback chain alongside R5/R6's root-cause work, not gated on either.**

- **With the chain shipped:** UDP discovery's failure is non-fatal. Don's specific bug becomes "Rung 3 silently failed; Rung 1a caught it" — invisible to the user because connection still works. R5/R6 closes the root cause as a separate exercise.
- **The firmware-install-dependency concern (`project_firmware_install_dependency_strategy.md`) dissolves.** Phase D firmware update doesn't depend on the broken UDP path; it uses Rungs 1-2.
- **For SmartLink users (round 2 motivation):** the chain produces measurable startup-latency improvements even when nothing is broken. Always-on win.

## Don as canary

Don's situation is the production scenario in miniature:

- Existing JJ Flex 4.1.x install with `autoConnectV2.xml` containing his radio's cached LAN IP from working sessions
- Radio firmware: 4.1.15-era
- JJ Flex 4.2.0 install would silent-fail Rung 3 (UDP discovery)
- Rung 1a (cached LAN IP) succeeds instantly — `autoConnectV2.xml` already has his radio's IP
- Connection works without ever exercising the broken UDP path

Every Flex user with older firmware faces the same pattern when 4.2.0 ships. **Solving for Don solves for the population.**

A nice second-order benefit: with the chain shipped, we keep Don on 4.2.0 and **passively capture diagnostic data** about which rung won the race each session. Free traces, zero tester friction, ongoing visibility into the underlying bug even after it stops blocking releases. Trace data routes through `project_trace_persistence_design.md`'s manifest-driven archive; surfaces only via user-triggered "Ask for help" support packages. WAN IPs redacted by default.

## Friction-tax framing

This design is a textbook expression of `project_friction_tax_principle.md`. "Make discovery a chain not a gate" means the user never sees the failure mode at all — the app does the right thing across multiple mechanisms instead of asking the user to know which one to use.

The Manual IP fallback (Rung 5) does pay friction tax — but only after the automated rungs fail, and only as the last resort that prevents stuck-ness. That's the right place to spend friction.

## Implementation phasing (locked from round 1, with Rung 1b inserted)

**Phase 1 — Rung 1a (cached LAN IP).** Lowest scope, highest leverage. ~150-300 LOC across `IDiscoveryRung` interface, `CachedIpRung` implementation, FlexLib vendor-patch for `Radio.CreateFromIp` factory, integration with `openTheRadio` / `wpfSelectorProc`. Solves Don's case and the bulk of the user population. **Ships in R6** (alongside the MMCSS patch).

**Phase 1.5 — Rung 1b (cached WAN IP).** Conditional on auth-model research outcome. Best case: ~100 LOC additional. Constrained case: ~150 LOC for the smaller-speedup version. **Ships in R6 alongside Rung 1a per your direction** — wait for Rung 1b shape before shipping R6.

**Phase 2 — Rung 2 (subnet probe).** ~200-400 LOC. Required for fresh installs without cached IP. Targets 4.2.0.

**Phase 3 — Concurrent rung execution.** Make rungs run in parallel, first-success-wins, cancel others. ~100 LOC orchestration. Optimization, not correctness — Phase 1+2 in series is functional.

**Phase 4 — Rung 5 (Manual IP entry).** UI for "type your radio's IP here." ~150 LOC. 4.2.x scope.

**Phase 5 — Rung 4 (SmartLink-as-LAN-fallback).** Logic to fall through to SmartLink when local rungs fail. Modest engineering; SmartLink path already exists. 4.2.x scope.

**Phase 6 — Passive diagnostic capture for Rung 3 (and now per-rung instrumentation).** Log which rung won, which rungs ran, success/fail per rung. Output goes to local trace per `project_trace_persistence_design.md`; surfaces in user-triggered "Ask for help" support packages per `project_sprint29_crash_reporter_vision.md`. WAN IPs redacted. Sprint 29-ish, not blocking.

## R6 build composition (next outbound build to Don)

When Rung 1b shape is known, R6 ships with:

- **R5 MMCSS patch** (already in flexlib-42 worktree) — `MmcssPipelineScheduler.Instance` redirected to `TaskScheduler.Default`
- **FlexLib vendor patch** — public `Radio.CreateFromIp(model, serial, name, ip, version)` factory wrapping the internal constructor at line 2111
- **JJ Flex Phase 1** — `IDiscoveryRung` interface, `CachedIpRung` reading `autoConnectV2.xml`, integration with the connection flow
- **JJ Flex Phase 1.5** — `CachedWanIpRung` (shape pending auth research)
- **Trace instrumentation** — every rung logs which path it took, which won, which fell through
- **NOTES file** — explains the changes to Don in ham-operator language, asks for traces either way

Build label: **R6** — short, sequential, signals "this is where strategy changed from diagnostic-only to diagnostic+remediation."

## What MUST work in the chained model

1. **Connection feels instant when Rung 1a hits.** Don't introduce artificial waits.
2. **Failure messages distinguish "no rung succeeded" from "specific rung failed."**
3. **Friction-tax preserved.** Manual IP entry is the LAST option, surfaced only after automated rungs fail.
4. **No silent phone-home.** Diagnostic capture stays local; export only via user-consented support packages. WAN IPs redacted.
5. **Flexibility principle preserved.** User can configure rung ordering, disable specific rungs, or force "manual IP only." Defaults are conservative; customization is opt-in.

## Cross-references

- `project_flexlib_4218_discovery_investigation.md` — the active bug whose impact this design dissolves
- `project_firmware_install_dependency_strategy.md` — chicken-and-egg problem this design also dissolves
- `project_friction_tax_principle.md` — operational principle this design implements
- `project_no_silent_phone_home.md` — constraint on the diagnostic-capture rung + WAN-IP-redaction rule
- `project_flexibility_principle.md` — user-controllable rung ordering follows this principle
- `project_anti_patterns_from_blindcat.md` — "no in-app explanation when it can't find the radio" is the antipattern this design counter-prescribes
- `project_chained_updater_pattern.md` — generalizable parallel pattern for chained dependent operations
- `project_flexlib_standalone.md` — confirms FlexLib supports direct-IP connect (architectural assumption)
- `project_trace_persistence_design.md` — host for per-rung diagnostic capture
- `project_sprint29_crash_reporter_vision.md` — user-consented export path for diagnostic data

## Open questions for round 2

1. **Rung 1.5 — ARP table read.** Should we add as a separate rung, or fold into Rung 2's pre-step? My lean: fold into Rung 2 as a pre-filter (read ARP first to identify candidate IPs, probe only those). Saves probe time without adding a rung.
**** yes
2. **R6 wait condition.** Per your direction: wait for Rung 1b research before shipping R6. The auth-model agent runs today; expected memo within ~30 minutes. **Confirm: hold R6 until that memo lands, even if it lands tonight before surgery? Or hold all R6 work until after recovery week regardless?****** Yes
**** yes, I ahve to be up probably until like 2 AM to take some meds that I have to take at 2 on tehe dot.  I get artificial sleep tomorrow morning and leave at like 6:20 A.M. We got time.
3. **Final ACK to lock the design and start `track/discovery-fallback-chain`.** Round 1 was substantively approved; round 2 adds Rung 1b, SmartLink motivation, IP-finding help text, network enumeration, and trace wiring. **`**** ACK` from you locks the design as canonical and authorizes the build-now-ship-later track.**
**** ack

## Confirm or revise

`**** ACK` — design is locked, doc moves to `docs/planning/design/discovery-fallback-chain.md` as canonical implementation reference, `track/discovery-fallback-chain` spins up.

`**** ` (specific changes) — apply changes; round 3 if substantial.
**** Ack, read questions
