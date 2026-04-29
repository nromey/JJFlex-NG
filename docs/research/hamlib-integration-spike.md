# Hamlib Integration Spike — Cross-Vendor Radio Support for JJ Flex

**Status:** Track B research deliverable, decision-quality
**Author:** Claude Opus 4.7 (autonomous Track B run)
**Date:** 2026-04-28
**Audience:** Noel (project lead) for cross-platform / multi-radio sequencing

---

## Executive Summary

Hamlib is the de facto open-source library for ham radio CAT control across vendors. This spike answers four practical questions: can JJF use it cleanly from .NET, what does Flex look like through Hamlib, what does Mark's TS-590 look like through Hamlib, and where does it fit in our architecture.

**Headline conclusions:**

1. **License is fine — Hamlib is LGPL v2.1**, which means JJF can link the Hamlib shared library from a closed/proprietary application without GPL contamination. The GPL only applies to bundled tools (rigctld, rigctl) which we wouldn't link statically anyway. This was the single biggest strategic risk and it lands favorably.

2. **Hamlib is complementary to FlexLib, not a replacement.** Hamlib's SmartSDR driver (`rigs/flexradio/smartsdr.c`) supports `set_freq`, `get_freq`, `set_mode`, `get_mode`, `set_ptt`, `get_ptt`, `send_morse`, `stop_morse` — basic CAT only. No SmartLink, no VITA-49 audio/panadapter streaming, no multi-slice, no CWX, no DAX, no APD, no diversity. For Flex radios, FlexLib stays the right backend because it gives us the differentiated features. For non-Flex radios, Hamlib is the *only* backend — there's nothing else.

3. **The .NET binding situation requires real work.** The repo has a `bindings/csharp/` directory but it's a near-empty stub (just a `multicast` subfolder). We'd need to either auto-generate via SWIG from the `rig.swg` definition or hand-write a P/Invoke layer. SWIG is the better long-term path; P/Invoke for a curated subset of operations is the faster short-term path.

4. **Mark's TS-590 is fully supported.** Both `RIG_MODEL_TS590S` (original) and `RIG_MODEL_TS590SG` (newer "G" revision) are in `rigs/kenwood/ts590.c` with deep feature coverage including the EX-menu items that distinguish the SG from the S. The commitment to Mark is technically achievable.

5. **The architecture lands cleanly:** define a `IRadioBackend` interface in JJF, implement `FlexLibBackend` (existing radio code refactored behind the interface), implement `HamlibBackend` for everything Hamlib can speak. The JJF UI layer doesn't see which backend is in play. Per-radio config (per `project_per_radio_config_serial_keyed.md`) chooses the backend at connection time.

6. **Sequencing:** Cross-platform abstraction layer (Track B) lays the groundwork by separating radio comms from UI. Hamlib-specific work then layers on top. Realistic phasing: spike → abstraction layer → Hamlib backend → first non-Flex radio (TS-590) → broaden to other Hamlib radios incrementally as testers surface.

---

## 1. What Hamlib Is

Hamlib (Ham Radio Control Library) is an open-source library for controlling amateur radio transceivers, receivers, rotators, and amplifiers via their CAT (Computer Aided Transceiver) interfaces. It abstracts the per-vendor command differences behind a uniform C API.

Key facts (verified from local clone at `c:\dev\Hamlib`):

- **First released:** 2000
- **Original authors:** Frank Singleton (VK3FCS), Stephane Fillod
- **Current maintenance:** "The Hamlib Group 2000-2025"
- **Current version:** 5.0.0~git (development trunk; current released is 4.7.x)
- **Repository activity:** Active — recent commits on the trunk are within days of inspection
- **License:** GPL v2 (tools) + LGPL v2.1 (library); see section 9 for analysis
- **Language:** C
- **Supported devices:** Hundreds of radios, dozens of rotators, several amplifiers
- **Vendor coverage:** Icom, Kenwood, Yaesu, FlexRadio, Elecraft, AOR, Drake, Alinco, AnyTone, Elad, JRC, Tentec, TenTec, Uniden, WinRadio, plus 15+ more

### 1.1 Two interfaces

- **C API (libhamlib)** — the canonical interface. Direct in-process function calls. Synchronous request/response model. This is what we'd target from .NET via P/Invoke or SWIG.
- **rigctld (network daemon)** — runs as a subprocess, exposes the same operations over TCP. Used by clients that don't want to or can't link C. Higher latency, but useful for cross-process scenarios.

For JJF, the C API is preferred. rigctld becomes a fallback if Windows-specific linking issues surface (unlikely but possible).

### 1.2 Repository layout (relevant subset)

- `rigs/` — radio backend drivers (one C file per radio family or vendor)
- `src/` — core library: rig.c, mem.c, conf.c, etc.
- `include/hamlib/` — public headers
- `bindings/` — language bindings via SWIG (Python, Perl, Tcl, Lua, PHP, plus stubs for C# and VB)
- `tests/` — rigctl, rigctld, test harnesses
- `c++/` — C++ wrapper class
- `doc/` — documentation, dox-generated API reference

---

## 2. License Posture (the strategic linchpin)

The repository ships two licenses:

- **`COPYING`** — GNU GPL v2. Applies to **the tools** (rigctl, rigctld, etc.) and any code that links the library statically into a tool.
- **`COPYING.LIB`** — GNU LGPL v2.1. Applies to **the library itself** (libhamlib).

Per the LGPL, we can link the Hamlib shared library from a closed/proprietary or differently-licensed application without inheriting the LGPL. Specifically:

- **Dynamic linking (DLL/.so) is freely permitted.** JJF's installer can ship `libhamlib-5.dll` alongside `JJFlexRadio.exe`. No license contamination.
- **Static linking** has more constraints (LGPL requires the user be able to relink), but we'd dynamic-link anyway.
- **End users have rights** under LGPL: they can replace our shipped Hamlib DLL with a modified version. We must not block that. (We don't anyway — JJF's install layout is open.)
- **Modifications to Hamlib itself** must be LGPL-licensed. If we patch a backend driver, that patch is LGPL and ideally upstreamed.
- **Dynamic-linked-from-our-app code we write** stays under whatever license we choose for JJF.

The GPL portion (rigctld) is irrelevant to us if we use the library directly. If we ever shell out to `rigctld` as a subprocess, that's an operational dependency (we run a separate process and talk over TCP), not a license dependency — same logical pattern as JJF talking to Flex's SmartLink server. GPL governs *code distribution*, not *who you talk to over a socket*.

**Verdict:** Hamlib is unambiguously safe for JJF to use under the LGPL terms via dynamic linking. This was the single biggest strategic risk and it's resolved favorably.

---

## 3. Flex Support in Hamlib — What's Actually There

Two separate paths exist, and they're worth distinguishing because they offer different feature sets:

### 3.1 The native SmartSDR driver (`rigs/flexradio/smartsdr.c`)

Written by Steve Conklin (AI4QR). Network-based (port_type `RIG_PORT_NETWORK`). Status: `RIG_STATUS_STABLE`. License stamp: LGPL. Version date: 20240814.0.

Operations exposed:
- `rig_init`, `rig_open`, `rig_close`, `rig_cleanup` — lifecycle
- `set_freq`, `get_freq` — frequency
- `set_mode`, `get_mode` — mode (with passband/filter)
- `set_ptt`, `get_ptt` — push-to-talk
- `send_morse`, `stop_morse` — CW transmission via the radio's keyer

Operations NOT exposed (commented out in caps):
- `reset` — radio reset
- `set_level`, `set_func` — DSP controls

What's structurally absent (no implementation exists):
- SmartLink remote authentication and tunneling
- VITA-49 audio streaming
- Panadapter / waterfall data
- Multi-slice support beyond basic VFO A/B
- CWX (text-to-CW with the radio's hardware keyer)
- DAX virtual audio
- APD / SmartSignal
- Diversity mode
- ATU control
- TX profiles, microphone profiles, global profiles
- Memory channels (with metadata)
- Spots, panadapter markers
- Feature license interrogation

Read this list against everything JJF currently does, and the gap is enormous. **Hamlib's SmartSDR driver is a basic CAT layer over a sophisticated radio.** That's not a criticism of the driver — it's the appropriate scope for cross-vendor abstraction. But it confirms that Hamlib is not a FlexLib replacement for Flex radios.

### 3.2 The Kenwood-emulation Flex driver (`rigs/kenwood/flex.c`, `flex6xxx.c`)

Flex radios speak a Kenwood K3-emulation CAT protocol on a TCP port for legacy CAT compatibility. Hamlib has a separate driver path that talks this CAT protocol instead of the native SmartSDR API. Documented in `rigs/kenwood/README.flex` (by Steve Conklin AI4QR).

Operations supported via CAT:
- Frequency (FA, FB)
- Mode (MD, ZZMD, ZZME)
- VFO selection (FR, FT)
- IF status (IF, ZZIF)
- Keying speed (KS)
- Filter slope (SH, SL)
- TX/RX state (TX, RX, ZZTX)
- Slice transmit flag (ZZSW)

Operations NOT supported (per the README's "Not supported" list):
- RF power (PC) — yes, this *is* missing in the Kenwood emulation
- Most level controls beyond keying speed and filter slope

This driver is for users who want CAT-only control (e.g., logger integration via legacy CAT) and don't need the full feature set. Less relevant to JJF since we're already at the SmartSDR-API level via FlexLib.

### 3.3 What this means for JJF strategically

For Flex radios, **FlexLib remains the right backend.** Hamlib gives us nothing FlexLib doesn't already give us better. The SmartSDR driver in Hamlib is useful to other applications that don't want a Flex-specific dependency, but JJF is purpose-built for Flex (and growing beyond) — we want every Flex feature.

For non-Flex radios, **Hamlib is the only viable backend.** We're not going to write our own Kenwood/Icom/Yaesu drivers from scratch. Hamlib already exists, has 25 years of community testing, supports more radios than any other library.

So the architecture isn't "switch to Hamlib." It's "add Hamlib alongside FlexLib for radios FlexLib doesn't reach."

---

## 4. TS-590 Support — Mark's Commitment

The TS-590 is supported in two model variants:

- `RIG_MODEL_TS590S` — original TS-590S (2010 release, sold for years)
- `RIG_MODEL_TS590SG` — TS-590SG (2014 revision with USB audio, improved DSP, different EX-menu numbering)

Both are in `rigs/kenwood/ts590.c`. Driver depth visible from a quick scan:

- Full mode + filter coverage
- USB audio level controls (`RIG_LEVEL_USB_AF_INPUT`, `RIG_LEVEL_USB_AF`)
- EX-menu items (model-discriminated — TS-590S uses different EX numbers than TS-590SG)
- RF power, AF gain, RF gain
- CW keyer
- Memory channels with names
- Split, dual VFO
- Antenna selection

This is a deep driver, not a token implementation. Mark's TS-590G (whether S or SG variant) gets a real radio experience through Hamlib.

What Mark would NOT get that Don gets through FlexLib:
- SmartLink remote (TS-590 doesn't have that — different radio class)
- Network-based DAX (TS-590 has USB audio, different paradigm)
- Panadapter (TS-590 has its own internal panadapter; we'd render its data differently than Flex's VITA-49 stream)
- The accessibility-rich JJF UI patterns we've built around Flex slice management — we'd need TS-590-shaped equivalents

What Mark WOULD get:
- Frequency, mode, filter control
- PTT, MOX, CW keying
- Memory channels with names
- USB audio routing for digital modes
- Per-radio config (Customize Home, action labels, etc.) — same JJF accessibility infrastructure as Flex

The friction-tax principle applies: Mark gets the same JJF accessibility quality on his TS-590 as you get on your Flex. The radio behaves differently (different feature surface) but the *interaction model* is the same.

---

## 5. Wrapping Hamlib From .NET

Three viable paths, each with tradeoffs:

### 5.1 Path A: Hand-rolled P/Invoke for a curated subset

Pick the 30-50 Hamlib functions JJF actually needs, write `[DllImport]` declarations for each, marshal the structs by hand. This is the fastest path to a working integration.

Pros:
- Quickest to first call (a couple of days work)
- Full control over marshalling and lifetime
- Can customize the surface to match JJF's idioms
- Doesn't require SWIG toolchain in our build

Cons:
- Manual maintenance — when Hamlib adds a function, we add a P/Invoke
- Risk of marshalling bugs (struct layout, calling conventions, string handling)
- Error-prone for callbacks (Hamlib's callback support requires careful function-pointer marshalling)

### 5.2 Path B: SWIG-generated bindings

Hamlib has SWIG definition files (`bindings/rig.swg`, `bindings/hamlib.swg`). SWIG can auto-generate C# wrappers from these. The empty `bindings/csharp/` directory in the repo suggests this was started but never finished.

Pros:
- Comprehensive coverage — every Hamlib function gets a binding automatically
- Maintenance-free as Hamlib evolves (rebuild the bindings)
- Generated wrappers are tested patterns, less marshalling-bug-prone
- Path to upstream contribution — we could finish the C# binding and contribute it back, benefiting the broader Hamlib community

Cons:
- SWIG toolchain becomes a build dependency
- Learning curve on SWIG itself
- Generated wrappers may not match .NET idioms perfectly (require a thin idiomatic layer on top)
- The empty `bindings/csharp/` situation suggests prior attempts hit obstacles — we'd need to understand why

### 5.3 Path C: Run rigctld as a subprocess and talk TCP

Skip the linking question entirely by spawning `rigctld.exe` as a subprocess and talking its TCP protocol from .NET. Hamlib already exposes everything via rigctld's text-based protocol.

Pros:
- Zero linking concerns
- rigctld's protocol is simple and stable
- Clean process boundary (rigctld crash doesn't crash JJF)
- License-irrelevant (rigctld is GPL but we're talking to it, not linking it)

Cons:
- Process management overhead (lifecycle, error handling, output parsing)
- Latency: TCP round-trip per operation vs in-process function call
- Extra dependency — rigctld must be shipped or installed
- Async patterns more complex than in-process

### 5.4 Recommendation

**Start with Path A (hand-rolled P/Invoke for curated subset)**, plan to migrate to **Path B (SWIG-generated) when surface area grows**. Use **Path C (rigctld subprocess) as a fallback** if specific Windows-linking issues surface (unlikely but possible).

The reason for this sequencing:

- Path A gets us a working backend in days, not weeks. We can prove the architecture (radio abstraction layer + Hamlib backend = TS-590 working in JJF) on a small surface.
- Once architecture is proven, Path B becomes worth the SWIG investment because we want broad Hamlib coverage (Icom, Yaesu, Kenwood broadly) and hand-rolling 500+ P/Invokes is not the right use of time.
- Contributing a complete C# binding upstream after our SWIG work pays a goodwill dividend in the Hamlib community and locks in long-term maintenance leverage (Hamlib maintains it; we consume).

---

## 6. Architecture Proposal — The Radio Abstraction Layer

The end-state architecture has three layers:

### 6.1 The interface

Define `IRadioBackend` in JJF (C# interface). Operations:

- Connection lifecycle: `ConnectAsync`, `DisconnectAsync`
- State queries: `Frequency`, `Mode`, `FilterBandwidth`, `PttState`, `RxAntenna`, `TxAntenna`, `IsTransmitting`, etc.
- State setters: `SetFrequencyAsync`, `SetModeAsync`, etc.
- Capabilities: `SupportsMultiSlice`, `SupportsSmartLink`, `SupportsPanadapter`, `MaxSlices`, etc.
- Events: `FrequencyChanged`, `ModeChanged`, `TransmitStateChanged`, etc.
- Streaming surfaces (Flex-only initially): `PanadapterStream`, `WaterfallStream`, `MeterStream`

The interface has explicit capability flags so the UI can adapt. A JJF UI that asks "show panadapter" gets `null`/`Hidden` from a backend that doesn't support it; the UI knows to hide that section.

### 6.2 Two backends initially

**`FlexLibBackend`** — wraps the existing JJF radio code (Radios/FlexBase.cs and friends, plus FlexLib). Implements all of `IRadioBackend` including the streaming surfaces. Capabilities: full Flex feature set.

**`HamlibBackend`** — new code, wraps Hamlib via P/Invoke. Implements the basic CAT subset of `IRadioBackend`. Capabilities: vendor-agnostic CAT; no streaming surfaces (returns null/hidden).

### 6.3 Backend selection at connect time

Per `project_per_radio_config_serial_keyed.md`, radio-state config lives in `radios\<serial>\config.xml`. Add a `<backend>FlexLib</backend>` or `<backend>Hamlib</backend>` element. The connection path reads it, instantiates the right backend, hands the user-facing UI an `IRadioBackend`.

For new radios, the discovery path picks the backend:

- Flex radios discovered via SmartSDR's discovery beacon → `FlexLibBackend`
- Other radios added manually (user picks model from Hamlib's rig list, configures port/baud) → `HamlibBackend`

### 6.4 What the UI layer changes

Today the UI is bound directly to FlexLib's `Radio` object via `theRadio` references throughout. The refactor:

- Replace `theRadio` with `currentBackend` (typed `IRadioBackend`)
- Update bindings to flow through the interface
- Hide UI sections whose underlying capability is `false` on the current backend
- Some Flex-specific UI stays exactly as-is, just accessed through the interface (no behavior change)

This is the cross-platform abstraction layer Track B item. Hamlib integration is downstream of it.

### 6.5 Per-radio UI variation

JJF's "Customize Home" vision (Sprint 30+ per memory) becomes the right place to express per-radio UI differences. Mark's TS-590 Home looks different from a Flex-6300 Home because the underlying capabilities differ. The Customize Home framework already needs to handle per-radio config; this is one more axis.

For shared UI (frequency, mode, PTT, basic memory), the experience is identical across backends. For Flex-specific UI (multi-slice, SmartLink, panadapter, CWX, DAX), the sections only render when the backend supports them.

---

## 7. What Multi-Radio JJF Looks Like — Three Concrete Scenarios

### 7.1 Scenario: Don's FLEX-6300 (today's path, no change)

- User starts JJF
- Discovery beacon finds `6300inshack` on local network
- Connection picker shows "FLEX-6300 6300inshack via direct"
- Connect → `FlexLibBackend` instantiated
- Full Flex UI: multi-slice, panadapter, SmartLink remote, CWX, DAX, all of it
- Per-radio config loaded from `radios\<serial>\config.xml`
- Behavior is identical to today

### 7.2 Scenario: Mark's Kenwood TS-590SG (new path)

- Mark starts JJF for the first time on his system
- He goes to Settings → Radios → Add Radio
- Picks "Kenwood TS-590SG" from the Hamlib radio list (~hundreds entries, alphabetical, searchable)
- Configures serial port (COM3, 115200 baud, 8N1) — Hamlib backend takes serial
- Names the radio ("TS-590 Shack")
- Saves config
- Connect → `HamlibBackend` instantiated, talks to the radio over the COM port
- JJF UI: frequency, mode, filter, PTT, memory channels — works fully
- Multi-slice, SmartLink, panadapter sections — hidden (TS-590 doesn't support them)
- Mark gets the same JJF accessibility infrastructure (verbosity, hotkey scopes, action labels, screen reader announcements) as Flex users

### 7.3 Scenario: A Yaesu FT-991A user (later)

- Same as Mark's path but Yaesu is selected
- Capability set is different (FT-991A has different mode list, different memory model, etc.)
- JJF UI adapts via the capability flags
- New radio class? Existing JJF accessibility-quality. No code per radio model — just config.

---

## 8. What's Missing in Hamlib That We'd Want

For deep Flex coverage, Hamlib's SmartSDR driver lacks the streaming surfaces (panadapter, audio, meters). Could we contribute that back?

**Probably not, and probably shouldn't.** Hamlib's design philosophy is "vendor-agnostic CAT abstraction." Streaming raw I/Q data, VITA-49 packets, panadapter waterfall tiles — those are genuinely vendor-specific concerns that don't fit Hamlib's abstraction. Forcing them into Hamlib would distort the design.

The right model: Flex-specific features stay in FlexLib (which is open-source and we already use). Cross-vendor CAT goes through Hamlib. The radio abstraction layer in JJF consumes both.

What we *could* contribute back to Hamlib:

- **Bug fixes for the SmartSDR driver** if we hit any during integration
- **Completion of the C# bindings** (currently empty stub) — we'd need them anyway, contributing benefits the community
- **Improvements to the Kenwood-emulation Flex driver** if specific interop issues surface

These are upstream-friendly contributions that don't fork the architecture.

For non-Flex radios where Hamlib has gaps (e.g., a TS-590 feature we want that isn't there), the contribution-back path is cleaner because Hamlib's coverage ambition fully includes those features.

---

## 9. License Compatibility — Final Word

Restating section 2 with operational precision:

- **JJF dynamically links libhamlib** (the LGPL library). License-clean.
- **JJF must ship libhamlib's source** if requested by an end user. This is satisfied by pointing at the upstream Hamlib repo (LGPL allows this).
- **JJF must allow end users to substitute their own libhamlib build.** This is satisfied by JJF's open install layout (no enforced binary signing of the DLL).
- **JJF's own code stays under whatever license JJF picks** (no LGPL contamination from dynamic linking).
- **rigctld is GPL but irrelevant** if we use the library directly.
- **If JJF ever spawns rigctld as a subprocess**, that's an operational dependency, not a license dependency. Same logic as JJF talking to SmartLink's server.

A user shipping a JJF installer + libhamlib.dll satisfies LGPL by having the Hamlib source available upstream and the DLL replaceable in the install directory. Both are no-cost compliance for us.

---

## 10. Sequencing Proposal

Realistic phasing within the broader Track B queue:

### Phase 1: Cross-platform abstraction layer (Track B prerequisite)

Define `IRadioBackend`, refactor existing FlexLib code behind it, ensure JJF UI consumes the interface. **No Hamlib code yet** — this is the precondition.

Effort: medium. Touches a lot of files but mechanically. Verifiable by existing app continuing to work.

### Phase 2: Hamlib P/Invoke layer + minimal HamlibBackend

Hand-rolled P/Invoke for the ~30 Hamlib functions JJF needs for basic radio operation. Implement `HamlibBackend` against this surface. Pick TS-590 (or similar) as the smoke-test radio.

Effort: ~1-2 sprint-weeks. Hamlib is well-documented; binding effort is bounded.

### Phase 3: First non-Flex radio integration (TS-590)

Mark connects his TS-590 to JJF. Iteration on UI variation, capability flag handling, per-radio config.

Effort: variable — depends on what surfaces during testing. Mark is the first non-Flex user, his feedback drives iteration.

### Phase 4: Broader Hamlib coverage (Icom, Yaesu, etc.)

As testers surface for other radio classes. SWIG-based bindings (Path B from section 5) likely become worthwhile here.

Effort: per-radio, variable. Each new radio class is hours-to-days of testing + UI variation work, not weeks.

### Phase 5: Upstream contributions back to Hamlib

C# binding completion, bug fixes we hit, possibly a generic capability-flag enrichment that benefits any client.

Effort: ongoing, low-priority background work.

### What this DOES NOT change

- 4.1.17 release: no Hamlib involvement. Foundation hardening for Flex.
- 4.2.0 release (FlexLib upgrade): no Hamlib involvement. Flex-specific.
- Sprint 28-29 work: Flex-only.

Hamlib enters as a Sprint 30+ track, parallel to the other Sprint 30 work (Customize Home, verbosity architecture, NVDA app module). It's not on the critical path for any single near-term release.

---

## 11. Open Decisions Requiring Noel Input

Before Phase 2 implementation begins:

1. **P/Invoke first, SWIG later** — confirm or override. Recommendation: yes, P/Invoke first (faster, smaller surface, prove architecture).

2. **Which TS-590 variant for Mark's smoke test?** TS-590S vs TS-590SG. He may have either. Confirm with Mark at appropriate moment.

3. **Bundle libhamlib.dll in the JJF installer or require user-installed Hamlib?** Recommendation: bundle. Friction-tax principle says user shouldn't install dependencies. Distribution is fine under LGPL.

4. **Which Hamlib version to target?** Recommendation: 4.7.x stable for first integration. Move to 5.0 when it releases stable.

5. **rigctld fallback path — implement or skip?** Recommendation: skip initially. Add only if Phase 2 surfaces a concrete Windows linking issue.

6. **C# binding upstream contribution timing?** Recommendation: only after Phase 4 (we have enough integration experience to contribute meaningful binding code, not minimal stubs).

7. **How to discover Hamlib radios in JJF?** No discovery beacon (those are vendor-specific). User picks from a list. Recommendation: JJF's "Add Radio" UI shows Hamlib's full radio list (auto-extracted from Hamlib at install or runtime), user picks vendor + model, configures connection parameters per radio type (serial COM + baud / network IP+port / USB).

8. **What happens if a user has BOTH a Flex AND a Hamlib radio?** Recommendation: per-radio config keyed by serial number (already the architectural direction per memory). Connection picker shows both. User picks one. JJF supports multiple radios but only one *active* at a time initially. Multi-active-radio support is its own architectural arc, deferred.

---

## 12. Risks and Constraints

### 12.1 Hamlib's Windows DLL availability

Hamlib publishes Windows binaries (the project's GitHub releases include `.zip` packages with prebuilt DLLs and headers). Risk: build flavor compatibility (mingw vs MSVC). Defense: test early, pick consistent toolchain, fall back to building Hamlib from source in our build pipeline if upstream binaries don't satisfy.

### 12.2 P/Invoke struct layout drift

Hamlib's `struct rig` and `struct rig_caps` are large and evolve across versions. Our P/Invoke struct definitions could drift. Defense: target a specific Hamlib version, regenerate P/Invoke definitions on each version bump, automated tests that exercise enough of the structure to catch drift.

### 12.3 Threading model mismatch

Hamlib is largely synchronous; JJF UI is async. We'd run Hamlib calls on a dedicated worker thread and post events to UI. This is the same pattern we use for FlexLib so the wiring is familiar.

### 12.4 Error handling surface

Hamlib uses `int` return codes (0 = success, negative = various errors). We'd need a clean translation from Hamlib's error codes to JJF's error model (which feeds the SsdrErrors-style enum we'd want for accessible error messaging).

### 12.5 The empty bindings/csharp situation

Why is it empty? Possible reasons:

- A previous attempt hit obstacles and was abandoned
- SWIG-to-C# was never completed because no maintainer prioritized it
- Licensing concerns about C# binding distribution (unlikely — LGPL is fine)
- Build system complexity (probably the most likely)

We'd need to investigate before committing to SWIG path. P/Invoke is unaffected by this.

### 12.6 Multi-vendor testing surface

Once we support Hamlib radios, our QA surface explodes. Each radio model has its own quirks. Defense: capability flags flow from Hamlib's `rig_caps` struct, the UI adapts automatically, and we accept that *our testing* skews toward radios our testers actually own. Other radios get "supported per Hamlib capabilities, untested by JJF directly." Document this honestly.

### 12.7 Hamlib's pace of change

Hamlib evolves with active development. Each minor version may add functions, deprecate others, change struct layouts (ABI compatibility is *not* a strong Hamlib promise). Defense: pin to a specific version range in the manifest, test before bumping, document our current Hamlib version in a visible place (about dialog, settings).

---

## 13. What This Spike Did Not Cover

Deferred to follow-up Track B research:

- **rigctld TCP protocol detail** — only relevant if we adopt Path C
- **SWIG-to-C# binding completion specifics** — Phase 4+ concern
- **rotator and amplifier support** — Hamlib supports these too (might be interesting for shack control); separate research
- **Other AT-relevant CAT control nuances** — how do non-Flex radios announce mode changes, frequency changes, PTT? Each has its own quirks. We'd discover these per radio, not in advance.
- **Audio interfaces for non-Flex radios** — JJF currently relies on Flex's network audio (DAX, VITA-49). Mark's TS-590 has USB audio (different architecture). The audio routing question is its own design exercise.
- **Custom Hamlib backend driver development** — if a user has a radio Hamlib doesn't support, the right answer is "write a Hamlib backend and contribute it." Out of scope for this spike.

---

## 14. Sources and References

Verified during this spike:

- Local clone at `c:\dev\Hamlib` (commit `5d0d5df3c` as of pull on 2026-04-28)
- `configure.ac` — version 5.0.0~git
- `COPYING` (GPL v2 for tools), `COPYING.LIB` (LGPL v2.1 for library)
- `rigs/flexradio/smartsdr_caps.h` — SmartSDR driver capabilities
- `rigs/flexradio/smartsdr.c` — SmartSDR driver implementation (function pointer table)
- `rigs/kenwood/README.flex` — Kenwood-emulation Flex driver documentation
- `rigs/kenwood/ts590.c` — TS-590S/SG driver (via grep verification)
- `bindings/csharp/` — empty stub (only `multicast` subdirectory)
- `bindings/README.python` — SWIG binding generation patterns (reference for C# path)
- `AUTHORS`, `THANKS` — provenance and contributors

External references (not loaded during spike, verify currency before citing):

- Hamlib upstream: github.com/Hamlib/Hamlib
- Hamlib documentation: hamlib.sourceforge.net
- LGPL v2.1 full text: gnu.org/licenses/old-licenses/lgpl-2.1
- SWIG documentation: swig.org/doc.html
- .NET P/Invoke patterns: learn.microsoft.com/en-us/dotnet/standard/native-interop/

Project memory cross-references:

- `project_kenwood_590g_commitment.md` — Mark's TS-590G commitment context
- `project_per_radio_config_serial_keyed.md` — per-radio config architecture
- `project_customize_home_vision.md` — UI variation surface
- `project_csharp_accessibility_moat.md` — framework rationale (relevant to keeping the .NET UI as Hamlib's consumer)

---

## Document Status

**Decision-quality.** Ready for Noel review. Phase 1 (cross-platform abstraction) is a Track B prerequisite that lives in its own future research/spec. Phase 2 (Hamlib P/Invoke + HamlibBackend) is the first Hamlib-specific work item, gated on Phase 1 progress.

**Estimated read time:** 25-35 minutes for full review; 8-10 minutes for executive summary + sequencing + open decisions.

**Strategic posture:** Hamlib is a high-confidence cross-vendor CAT layer. Its license is favorable, its code is mature, its Flex support is shallow but appropriate, its TS-590 support is deep, and its integration cost is bounded. The biggest investment is the cross-platform abstraction layer (which is independently valuable), not the Hamlib piece itself. Mark's commitment is achievable. Future radios beyond his become incremental work, not architectural work.

**Track B queue position:** Cross-platform abstraction layer first (independent), then Hamlib backend (Phase 2 above), then per-radio integration as testers surface.
