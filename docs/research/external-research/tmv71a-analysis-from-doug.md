# Analysis of Doug's TM-V71A Protocol Research

**Status:** Synthesis artifact, decision-quality
**Author:** Claude Opus 4.7 (autonomous Track B run)
**Date:** 2026-04-28 (analysis); source artifact dated 2026-04-17 (Doug's Claude session)
**Source artifact:** `docs/planning/track-b/external-research/v71/TMV71A_CAT_PROTOCOL.md` (Doug's research)
**Companion:** `docs/planning/track-b/external-research/v71/CLAUDE.md` (Doug's project context)
**Cross-reference:** `docs/planning/track-b/hamlib-integration-spike.md` (our Hamlib spike)

---

## Executive Summary

Doug's research is a comprehensive CAT command reference for the **Kenwood TM-V71A** (NOT the older TM-V7A — different radios despite the similar names; the TM-V71A is the 2008 successor to the TM-V7A). Doug ran an independent Claude session focused on documenting the radio's protocol from authoritative community-reverse-engineering sources. The output is a 1100-line protocol reference covering 34 CAT commands, programming-mode binary protocol with memory map, complete reference tables, and known bugs.

**Headline takeaways:**

1. **Validates my Hamlib spike's "shallow non-Flex coverage" claim, with nuance.** Hamlib has `rigs/kenwood/tmd710.c` covering the TM-V71A, but Doug's research shows the *complete protocol* surface is materially richer than what tmd710.c implements. Real upstream contribution opportunity exists.

2. **Concrete Hamlib version constraint surfaced.** Hamlib bug #1767 — `kenwood_transaction()` stripped the `\r` terminator, breaking ALL TM-V71A communication. Fixed in **Hamlib 4.6.4+**. Our Hamlib spike should require this minimum version. Earlier versions just don't work for this radio.

3. **Hardware flow control is non-negotiable for TM-V71A.** RTS/CTS required; without CTS asserted the radio accepts commands silently and never responds. Our Hamlib backend's serial-port configuration must surface this requirement, possibly with an "RTS-CTS loopback workaround" guidance for users with adapters that don't assert CTS.

4. **Two protocol modes** (CAT text-based + Programming mode binary) — more complex than typical HF rigs. Programming mode is needed for bulk memory operations (faster than per-channel CAT). Adds an architectural wrinkle to `IRadioBackend` for radios that distinguish operational CAT from bulk binary memory access.

5. **Confirms VHF/UHF FM mobile as a distinct radio class** with concrete feature surface: memory-primary operation, PL tone (CTCSS) by index, DCS by index, cross-band repeat, built-in TNC, DTMF transmission, FM/NFM/AM only (no SSB/CW), Bands A and B as the operating units, hardware flow control.

6. **Doug as collaborator is a real win.** His research is rigorous, his sources are authoritative, his methodology (multiple cross-validating community sources with the explicit "the protocol was never officially published; this is community documentation" caveat) is sound. He's exactly the kind of contributor who would otherwise have to re-derive this work for any cross-vendor SDR project.

---

## 1. What Doug's Research Covers

### 1.1 The 34 CAT commands

| Category | Commands |
|----------|----------|
| Identification | `AE` (serial/model), `ID` (model), `FV` (firmware), `TY` (region/features) |
| Frequency control | `FO` (VFO frequency + parameters), `UP`/`DW` (step up/down) |
| Mode control | `VM` (VFO/Memory), `CD` (channel/freq mode), `BC` (band/PTT control) |
| Memory | `ME` (memory channel write/read), `MN` (channel name), `MR` (channel select), `CC` (call channel) |
| Squelch | `SQ` (squelch level), `SS` (S-meter squelch), `BY` (busy status) |
| Tone control | `BT` (burst tone), `TT` (transmit tone burst), `LK` (key lock) |
| TX/RX | `TX` (transmit), `RX` (receive), `AS` (TX/RX swap), `PC` (TX power) |
| DTMF | `DM` (DTMF memory), `DT` (DTMF transmit) |
| TNC | `TC` (TNC enable), `TN` (TNC mode) |
| APRS / GPS | `BE` (APRS beacon), `GM`/`GP`/`GT` (GPS — D710G only), `RT` (real-time clock) |
| Display | `BL` (backlight), `MS` (power-on message), `DL` (dual/single band) |
| Configuration | `CS` (callsign), `MU` (menu config — 42 params), `PV` (VFO limits) |
| System | `SR` (reset) |

### 1.2 Programming Mode (binary memory access)

Distinct from CAT mode. Used for bulk memory channel read/write — much faster than per-channel `ME`/`MN` commands. Entry: `0M PROGRAM\r`. Exit: `E`. Read/write blocks via `R <addr_hi> <addr_lo> <length>` and `W <addr_hi> <addr_lo> <length> <data>`. Programming-mode timeout (~5 seconds inactivity) puts radio in PROG ERR state.

Memory map covers:
- 1000 channel records (16 bytes each at 0x1700)
- 1000 channel names (8 bytes each at 0x5800)
- 1000 channel flags (2 bytes each at 0x0E00)
- DTMF memory codes + names
- EchoLink memory codes + names
- Programmable memory (6 PM slots × 512 bytes each)
- Group names, PM names, repeater ID
- Various menu/config bytes

### 1.3 Reference tables

- **Band codes** (A / B / cross-band variants)
- **Step sizes** (5 / 6.25 / 8.33 / 10 / 12.5 / 15 / 20 / 25 / 30 / 50 / 100 kHz)
- **Shift / duplex** codes (simplex / + / −)
- **Modulation modes** (FM / NFM / AM)
- **CTCSS tones** (42 entries by index — note documented LA3QMA-source typo at index 09 vs 19)
- **DCS codes** (104 entries by index)
- **DTMF character map**
- **Programmable key function codes** (23 functions)
- **SQC output sources** (6 modes)
- **APO timeouts** (off / 30 / 60 / 90 / 120 / 180 minutes)

### 1.4 Known issues catalog

11 documented quirks with workarounds:
- No-CTS-silent-radio (the most user-trapping)
- Hamlib `\r` stripping (resolved in 4.6.4+)
- Programming mode timeout
- No cross-tone (different RX/TX CTCSS impossible)
- Frequency field width inconsistency (10 vs 11 digits between sources)
- ME erase requires trailing comma
- DT requires active TX
- TC requires `^C^C^C` first
- SR responses (display unit returns N, send to main)
- RFI lockup (USB-serial adapters during transmit — needs ferrite chokes)
- Programming mode firmware-version mismatch (can brick radio if cross-loading)

---

## 2. Validations and Corrections to Our Hamlib Spike

### 2.1 Validations (claims my spike made that Doug's research confirms)

- **Hamlib has the radio.** `rigs/kenwood/tmd710.c` exists and is the primary implementation; Doug's research cites it as one of his authoritative sources. ✓
- **Hamlib's coverage is shallower than the full protocol surface.** Doug's research extends beyond what tmd710.c implements (specifically: full programming-mode binary protocol, all 42 menu parameters in `MU`, all programmable key codes, full DCS table). ✓
- **Cross-vendor LGPL coverage is real and useful.** Doug treats Hamlib as the working C implementation of choice; his research is *additive* to it, not a competitor. ✓
- **VHF/UHF FM mobile is a distinct radio class** with very different feature surface from HF transceivers. ✓
- **Per-radio config + capability flags are the right architectural pattern.** TM-V71A vs TM-D710 vs TM-D710G have meaningful protocol differences (GPS commands only on D710G, character lengths differ on MN/MS) — exactly the kind of variation per-radio config handles. ✓

### 2.2 Corrections / additions needed in the spike

- **Add Hamlib version constraint.** Spec the Hamlib backend as requiring `>= 4.6.4` minimum. Document the reason (issue #1767 fix). This is a concrete update to the spike's "Hamlib's Windows DLL availability" risk section.
- **Add serial flow control as an architectural concern.** The spike treats Hamlib's serial-port configuration as a black box; Doug's research surfaces that *some radios* (TM-V71A specifically, possibly others) require hardware flow control. Our `HamlibBackend` serial-port configuration UI must expose RTS/CTS as a configurable option per radio profile. Recommend default-on for radios with documented flow-control requirements.
- **Add programming-mode protocol awareness.** Most HF rigs only have CAT. Mobile rigs often have CAT + programming-mode binary protocol. Our `IRadioBackend` interface needs to either (a) hide programming-mode behind bulk memory operations, or (b) expose a "fast bulk channel write" capability that backends implement via whatever mechanism they have. Doug's research strongly suggests (a) is the right abstraction.
- **Sequence TM-V71A as third smoke-test radio** behind TS-2000 (Noel's testbed) and TS-590 (Mark's commitment). Doug as primary tester. The spike should reflect this re-sequencing.
- **Update "What's missing in Hamlib" section.** My spike's claim was hedged ("Hamlib's SmartSDR driver lacks streaming surfaces"). Doug's research adds a concrete second point: Hamlib's TM-V71A driver doesn't implement the full protocol surface. Two examples of "Hamlib has the radio but coverage is shallow" — that's a pattern, not a one-off.

### 2.3 New facts the spike should incorporate

- The TM-V71A's `MU` (menu config) command sets 42 parameters in a single read-modify-write cycle. This is a different architectural shape than the per-parameter setters HF rigs typically expose. Capability flag `BulkMenuConfig` worth considering.
- Memory channels in TM-V71A reach 1000 entries (000-999) with edge-pair channels (200-219) and call channels (220-221). HF rigs typically have 100-200 memory channels. Memory-channel UI must scale.
- Cross-band repeat is a feature unique to mobile rigs of this class. Capability flag `CrossbandRepeat` would gate that UI section.
- Built-in TNC is similarly mobile-class. Capability flag `BuiltInTNC` would surface or hide TNC management UI.

---

## 3. Hamlib Upstream Contribution Opportunities

This is potentially the most exciting outcome of Doug's research. Several gaps in tmd710.c that Doug's reference material could fill:

### 3.1 High-value, low-effort contributions

- **Programming mode binary protocol implementation.** Hamlib's tmd710.c uses CAT mode for everything. Adding programming-mode for bulk operations would dramatically speed up channel-list management. Doug's memory map + protocol documentation is sufficient input.
- **Complete MU parameter coverage.** If tmd710.c doesn't expose all 42 menu parameters via Hamlib's `parm` interface, adding the missing ones is a defined-scope contribution.
- **Programmable key function bindings.** All 23 PF codes documented; Hamlib could expose these as a configurable parm.

### 3.2 Documentation contributions (no code changes)

- **Adding Doug's research itself to Hamlib's documentation.** With Doug's permission and credit, the protocol reference is exactly what Hamlib's `doc/` directory could host. This benefits every Hamlib consumer.
- **Updating Hamlib's TM-V71A driver header comments** with the bug-catalog Doug compiled (CTS requirement, RFI lockup, etc.).

### 3.3 Hardware-test-required contributions

- **TM-D710G GPS commands** (GM, GP, GT) — if Hamlib doesn't have these, adding them requires a TM-D710G in hand.
- **Real-time clock** — RT command. Same constraint.
- **APRS beacon** — BE command. Same constraint.

### 3.4 What we should NOT do

- Don't fork or rewrite tmd710.c. Contribute incrementally upstream. The Hamlib team is responsive (recent commits are within days), and our changes benefit every consumer.
- Don't rush contributions before our integration work has shown specific gaps. Contributions should be driven by JJF's actual usage discovering tmd710.c's limits, not by speculative completeness.

---

## 4. Radio Class Taxonomy — Now With Three Concrete Classes

Doug's TM-V71A research, combined with my Hamlib spike's TS-2000 (all-mode all-band) and TS-590 (HF transceiver) data, gives us enough material to draft the radio class taxonomy.

### 4.1 Class boundaries with concrete representatives

- **HF Transceiver:** TS-590S/SG (Mark), Flex 6000/8000/Aurora (Don/Justin/Noel), IC-7300, FT-991A. Modes: SSB/CW/AM/FM/digital. Single-band-active. Panadapter often present. Memory channels 100-300.
- **All-Mode All-Band:** TS-2000 (Noel), FT-857D, IC-7100. HF + VHF + UHF + sometimes 1.2 GHz. Every mode. Satellite duplex on some. Built-in TNC sometimes. Memory channels 200-500. **Bridges HF and mobile classes — exercises every code path.**
- **VHF/UHF FM Mobile:** TM-V71A (Doug), TM-D710G, FTM-400, IC-2730. FM/NFM/AM only. Two bands (A/B) with cross-band repeat. Memory-primary (1000 channels). Built-in TNC standard. PL tone + DCS by index. Hardware flow control on serial. Programming mode for bulk memory.
- **Handheld Transceiver (HT):** TH-D74 (Kenwood), FT-70D, ID-52. Similar to mobiles but battery-constrained, often with built-in GPS/APRS, smaller memory capacity. Different ergonomic constraints.
- **Receivers Only:** AOR AR-DV1, WinRadio, RFspace SDR-IQ. Receive-only, no TX path, often very wideband.

### 4.2 Capability flag implications

`IRadioBackend` capability flags that vary across classes:

- `SupportsTransmit` — all except receivers
- `Modes` (set of supported modes) — varies dramatically
- `Bands` (set of supported bands)
- `MultipleSimultaneousVFOs` — yes for all-mode all-band, no for most others
- `CrossbandRepeat` — VHF/UHF mobiles only
- `SatelliteDuplex` — all-mode all-band only (sometimes)
- `BuiltInTNC` — VHF/UHF mobiles + some all-mode
- `MemoryChannelCapacity` — int, varies 100 to 1000+
- `MemoryChannelEdgePairs` — boolean, mobiles use this pattern
- `ToneSelectionByIndex` — boolean, mobiles use index, HF often uses direct frequency
- `ProgrammingModeBulk` — boolean, mobiles + some HTs have this
- `RequiresHardwareFlowControl` — boolean, some serial radios require this
- `Panadapter` — most HF + some all-mode
- `MultiSliceCapable` — Flex only
- `SmartLinkRemote` — Flex only
- `VITA49AudioStream` — Flex only
- `DAXChannelCount` — Flex only
- `CWXTextKeyer` — many but not all
- `PLToneEncode` / `PLToneDecode` / `DCSEncode` / `DCSDecode`
- `GPSReceiver` — TM-D710G, some HTs
- `APRSBeacon` — radios with built-in TNC + APRS firmware
- `EchoLinkMemory` — some Kenwood mobiles

### 4.3 What this argues for

A formal radio class taxonomy doc as a sibling to the Hamlib spike. Three to five concrete classes with feature surface differences enumerated. Captures the architectural variability `IRadioBackend` must accommodate. Becomes a reference for future radio additions — when a new radio class surfaces (HT, receiver, etc.), the taxonomy gets updated; when a new radio within an existing class arrives, it slots in cleanly.

I'd argue we draft this taxonomy doc as the next Track B research deliverable, after applying Doug's findings to the Hamlib spike.

---

## 5. Concrete Action Items

In rough priority order:

### 5.1 Immediate (this Track B turn or next)

- **Update Hamlib spike** with: Hamlib 4.6.4+ minimum version constraint, hardware flow control architectural concern, programming-mode bulk-operation capability flag, re-sequenced first-target list (TS-2000 → TS-590 → TM-V71A).
- **Capture Doug's research permanence.** Link from the Hamlib spike to this analysis; link from this analysis to Doug's source artifact. Cross-references stay valid.

### 5.2 Near-term (next Track B session)

- **Draft the radio class taxonomy doc** at `docs/planning/track-b/radio-class-taxonomy.md`. Use TS-2000, TS-590, TM-V71A as concrete representatives across three classes. Enumerate `IRadioBackend` capability flags with class membership.
- **Review attribution preferences with Doug.** Ask whether public attribution in artifacts is fine, or "external contributor" framing is preferred.

### 5.3 Medium-term (architecture work)

- **Cross-platform abstraction layer design.** With three radio classes empirically grounded, the `IRadioBackend` interface design has real material to anchor on. Capability flag set, event surface, lifecycle methods — all informed by the taxonomy.
- **Hamlib backend Phase 2** per the original spike sequencing. TS-2000 first as smoke test; TS-590 second; TM-V71A third.

### 5.4 Long-term (post-implementation)

- **Hamlib upstream contributions.** As JJF integration discovers concrete tmd710.c gaps, file PRs upstream with Doug's research as the documentation source.
- **Documentation contribution to Hamlib.** With Doug's permission, propose adding the protocol reference to Hamlib's `doc/` directory.
- **Reach out to the LA3QMA repo maintainer.** If our integration discovers protocol details not yet in their reverse-engineering archive, contribute back. Same applies to larsks's tm-v71-tools.

---

## 6. Open Questions for Noel

- **Doug's attribution preference?** Public credit in artifacts, or external-contributor framing?
- **Is Doug aware that his research could feed Hamlib upstream contributions?** If yes, is he interested in being credited on those contributions too? If no, would he be open to that conversation?
- ~~**TM-V71A vs TM-V7A naming** — RESOLVED 2026-04-28 evening: Noel confirmed Doug operates the TM-V71A. All artifacts updated to reflect this.~~
- **Should we draft the radio class taxonomy doc next, or apply the Hamlib spike updates first?** Recommendation: spike updates first (smaller, validates the corrections immediately), then taxonomy (larger, builds on the now-corrected spike).

---

## 7. Document Status

**Decision-quality.** Ready for Noel review. Identifies six headline takeaways, validates and corrects the Hamlib spike, opens upstream Hamlib contribution opportunities, and provides material for the radio class taxonomy doc.

**Estimated read time:** 15-20 minutes for full review; 5-7 minutes for executive summary + action items skim.

**Next deliverable proposal:** Apply spike updates (small, ~30 min), then draft radio class taxonomy doc (larger, ~1-2 hours autonomous Track B). Both before any code-touching work begins.
