# Sprint 29 Track L — NetworkChangeWatchdog Wiring

**Branch:** `track/flexlib-42` (NOT a new branch — Track B's cascade work merged into here as `512d17ff`)
**Worktree:** `C:\dev\jjflex-flexlib-42`
**Spawned:** 2026-05-09 (evening session)
**Target ship:** 4.2.0
**Target merge:** stays on track/flexlib-42 until 4.2.0 cuts

> **Note:** This file replaces the prior FlexLib 4.2.18 upgrade TRACK-INSTRUCTIONS. That work is done (FlexLib upgrade done, all 8 phases shipped, real-radio audio confirmed). The current scope is the watchdog wiring continuation below.

## Scope summary

Wire the `NetworkChangeWatchdog` class (which landed standalone as commit `6de629b9` on Track B's branch, now merged into `track/flexlib-42` via `512d17ff`) into the cascade entry points so it actually fires on network change events. **The class exists; nothing calls it yet.** Without wiring, it's dead code.

The watchdog's purpose: when the user's network identity changes (Wi-Fi network switch, VPN connect/disconnect, Ethernet plug/unplug, IP change, etc.), the cascade should re-evaluate which rungs are eligible (e.g., a cached LAN IP from one network is invalid on a different network), and possibly auto-retry discovery against the new network identity.

## Where the wiring needs to happen

Read the standalone class first (commit `6de629b9` — `Radios/DiscoveryChain/NetworkChangeWatchdog.cs` or wherever it landed) to understand its public surface. Likely:

- Subscription registration: `NetworkChangeWatchdog.Start()` or constructor + IDisposable
- Event: `NetworkChangeWatchdog.NetworkIdentityChanged` (or similar)
- Probably exposes the new network identity (NLM-style — name + connectivity flags) as event args

Wire it at:

1. **App startup** — register a singleton instance, subscribe to its event in either `ApplicationEvents.vb` or wherever the cascade is initialized. Dispose on app shutdown.

2. **Cascade entry points** — when the watchdog fires:
   - Invalidate the network-identity-bound parts of `RadioConnectionCache` (entries are gated by NLM identity per `72cc0edd Sprint 29 Track B — NLM-style network identity gating for cached rungs`; the cache's gating logic should naturally exclude the now-invalid entries on next read, but verify)
   - Optionally: trigger a re-cascade from the new identity if the user has an in-flight stuck-connection, OR trigger a quiet "network changed; cascade-fresh-on-next-action" state. The latter is probably right for v1 — auto-reconnect on network change is a separate feature with its own consent surface.

3. **TraceSessionContext events** — fire a `network_change_observed` key event so the trace manifest captures network transitions. Useful for forensic correlation.

## Read-first context

Before writing code, skim these:

- The standalone class commit (`6de629b9`): `git show 6de629b9` — see what NetworkChangeWatchdog actually exposes
- `memory/project_trace_persistence_design.md` for `key_events` conventions
- `docs/planning/design/discovery-fallback-chain-v3.md` (referenced by recent Track B commits) — full cascade design context if needed
- The NLM-gating commit (`72cc0edd`) — how the cache identifies "current network" today

## Implementation order

1. **Inspect the standalone class.** Confirm the public surface and what events it exposes. ~15 min of code reading.
2. **Add subscription registration** at app startup (`ApplicationEvents.vb` or wherever JJF initializes networking). ~20 LOC.
3. **Wire the event handler** that responds to network change. v1 logic: log to trace context, mark cache entries invalid for the prior network identity. NO auto-reconnect — that's a separate decision. ~30 LOC.
4. **Add `network_change_observed` key event** to TraceSessionContext when watchdog fires. ~5 LOC.
5. **Test** by toggling Wi-Fi, plugging/unplugging Ethernet, connecting/disconnecting VPN. Verify the trace manifest captures the network change events.
6. **CHANGELOG entry** in the appropriate section (this branch isn't main; the entry will ride with the eventual 4.2.0 merge).

## File touch list (estimated)

- `Radios/DiscoveryChain/NetworkChangeWatchdog.cs` — possibly minor adjustments if the public surface needs tweaks for clean wiring
- `ApplicationEvents.vb` — subscription registration on startup
- `Radios/DiscoveryChain/RadioConnectionCache.cs` — invalidation hook
- `JJTrace/TraceSessionContext.cs` — network_change_observed key event
- `docs/CHANGELOG.md` (on track/flexlib-42) — entry under whatever 4.2 staging section exists

## What MUST NOT regress

1. **The cascade still works without the watchdog.** Adding the watchdog is enhancement; if it fails to register or its event handler throws, the cascade should still operate normally.
2. **No double-event-firing** if the user toggles Wi-Fi rapidly. NLM (Network List Manager) sometimes fires multiple events in quick succession. Consider a small debounce (250-500ms) so we don't thrash the cache invalidation logic.
3. **Disposing the watchdog on app shutdown** — leaving it subscribed across an app exit can cause undefined behavior in NLM and prevent clean process termination.

## Build commands

```batch
dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal
```

## Commit conventions

Every commit prefixed `Sprint 29 Track L:` (or use the existing Track B prefix `Sprint 29 Track B Phase 3 —` since this is a continuation of Phase 3 work). Either is acceptable; consistency within this track matters more than which prefix.

## Success criteria (Definition of Done)

- [ ] `NetworkChangeWatchdog` subscribed at app startup, disposed at shutdown
- [ ] Network change events fire the cache invalidation logic
- [ ] Trace manifest captures `network_change_observed` events when network changes
- [ ] Test: toggle Wi-Fi → trace shows the event → cache entries for prior network are excluded on next cascade
- [ ] Test: cascade still works in the no-network-change case (smoke test)
- [ ] No regressions in existing cascade tests
- [ ] Clean Debug build

## Cross-references

- Standalone class commit: `6de629b9 Sprint 29 Track B Phase 3 — NetworkChangeWatchdog (standalone class)`
- Cache gating: `72cc0edd Sprint 29 Track B — NLM-style network identity gating for cached rungs`
- Phase 2 complete: `e24c72d5 Sprint 29 Track B Phase 2 — Rung 4: SmartLink-as-LAN-fallback (Phase 2 complete)`
- Cascade rollup merge: `512d17ff Merge sprint29/track-b-cascade into track/flexlib-42: Phase 1 + Phase 2 + Phase 3 watchdog standalone`

## Resume hint

> Resume Sprint 29 Track L from TRACK-INSTRUCTIONS.md
