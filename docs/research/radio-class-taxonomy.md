# Radio Class Taxonomy

**Status:** Multi-radio Phase 2 deliverable
**Date:** 2026-04-29
**Author:** Claude Opus 4.7 (track/multi-radio)
**Audience:** Noel — input to the IRadioBackend abstraction design (Phase 4)

---

## What this document is

JJ Radio's abstraction was multi-vendor but single-class — it modelled HF
transceivers and called everything else either "another HF transceiver" (TS-2000,
IC-9100) or "raw terminal" (Generic). For JJ Flexible to honor the inherited
JJ Radio commitment AND broaden into Doug's TM-V71A class AND remain open to
HTs, receivers, and future radio classes, the abstraction needs an **explicit
taxonomy of radio classes** with class membership feeding capability-flag
defaults and UI variation.

This document is that taxonomy. It draws on:

- The `RigCaps.Caps` enum already in the codebase (`Radios/RigCaps.cs`)
- Phase 1's JJ Radio inventory (which classes JJ Radio covered)
- The Hamlib spike's class-aware survey (`hamlib-integration-spike.md`)
- Doug's TM-V71A research and Hamlib's `rigs/kenwood/tmd710.c` (VHF/UHF FM mobile)
- Synthesis findings from `tmv71a-analysis-from-doug.md`
- Live FlexLib surface for the SDR class (`FlexBase.cs`)

It does NOT propose `IRadioBackend` itself yet — that is Phase 4. This document's
job is the class structure that informs Phase 4's interface design.

---

## 1. Class definitions and concrete representatives

Five classes are proposed. Each is anchored in radios real testers operate or
inherited JJ Radio users likely operate, so the abstraction stays grounded.

### 1.1 SDR Transceiver (Flex class)

**Defining attributes:**
- Network-attached (LAN or SmartLink remote)
- Multi-slice / multi-receiver as a first-class concept (not just A/B VFO)
- VITA-49 streaming for audio + panadapter + waterfall + meters
- DAX / virtual audio model
- Feature licenses gate parts of the surface (Diversity, MultiFlex, Aurora-tier features)
- Firmware-update mechanics in-band

**Concrete representatives:**
- FLEX-6300 (Don, Noel) — entry-level, 1 SCU, 2 slices
- FLEX-6400(M), FLEX-6500 — entry/mid HF, 1 SCU, 2-4 slices
- FLEX-6600(M) — mid HF, 2 SCUs, 4 slices, Diversity
- FLEX-6700(R) — flagship HF, 2 SCUs, 8 slices, Diversity
- FLEX-8400(M) (Justin) — 8000-series entry, 1 SCU, 2 slices
- FLEX-8600(M) (Noel, boxed) — 8000-series mid, 2 SCUs, 4 slices
- AU-510(M), AU-520(M) — Aurora 500W variants, classed as 8400/8600 derivatives

**Backend:** `FlexLibBackend` (existing JJF code refactored behind `IRadioBackend`).
There is no Hamlib backend for this class; FlexLib is the only viable path because
Hamlib's `rigs/flexradio/smartsdr.c` is a basic-CAT-only veneer (no streaming, no
MultiFlex, no panadapter).

**Distinguishing capabilities (vs other classes):**
- `MultiSliceCapable`, `MaxSlices`, `MultiFlexClients`
- `SmartLinkRemote`, `VITA49AudioStream`, `DAXChannelCount`
- `DiversityCapable` (2-SCU radios)
- `PanadapterStream`, `WaterfallStream`, `MeterStream`
- `CWXTextKeyer` (text-to-CW via radio's hardware keyer)
- `FirmwareUpdateInBand`
- Feature-license-gated capabilities (LicenseFeatDivEsc, etc.)

### 1.2 HF Transceiver (Kenwood TS-590-class)

**Defining attributes:**
- HF-only (160m through 10m); some include 6m
- Single band active at a time
- All standard HF modes: SSB, CW, AM, FM, AFSK/digital
- Single internal panadapter (some models) — not VITA-49; vendor-specific scope data
- USB or serial CAT
- USB audio (newer models) for digital modes
- Single VFO pair (A/B) with split capability
- Memory channels (typically 100-300)
- Built-in keyer + CW decode (some)

**Concrete representatives:**
- Kenwood TS-590S, TS-590SG (Mark) — pre-FlexLib JJ Radio classic
- Kenwood TS-480 (HF-only mobile variant of the K-protocol family)
- Yaesu FT-991A (HF + VHF + UHF — boundary case to all-mode)
- Icom IC-7300, IC-7610 — Icom CI-V, vendor-specific touchscreen
- Elecraft K3, K4

**Backend:** `HamlibBackend`. Hamlib coverage is deep for this class; Phase 1
showed Jim's `KenwoodTS590.cs` was 2,438 lines, and `rigs/kenwood/ts590.c` is
the canonical replacement.

**Distinguishing capabilities:**
- HF-only band coverage (`Bands` enumerates HF + optional 6m)
- USB audio routing (`UsbAudioInput`, `UsbAudioOutput`)
- Per-band antenna selection (`AntennaPerBand`)
- Per-band TX power (`TxPowerPerBand`)
- Optional internal panadapter (`InternalPanadapter` boolean; `PanadapterStream`
  if exposed via CAT — TS-590S did not, TS-590SG limited)

### 1.3 All-Mode All-Band (TS-2000 class)

**Defining attributes:**
- HF + VHF + UHF (some + 1.2 GHz)
- Every standard mode (SSB / CW / AM / FM / digital)
- Satellite duplex (separate uplink/downlink VFOs, possibly different bands and
  modes, possibly tracked)
- Sometimes built-in TNC for packet
- Often dual receivers (main + sub)
- This class **bridges HF and mobile** — exercises every code path, which is why
  Noel's TS-2000 is the prime testbed (`project_ts2000_cross_class_testbed.md`)

**Concrete representatives:**
- Kenwood TS-2000 (Noel) — HF + VHF + UHF + satellite, with 1.2 GHz on X variant
- Icom IC-9100 — JJ Radio's only Icom; HF + VHF + UHF + satellite
- Icom IC-7100 — modern equivalent, all-mode all-band with detachable head
- Yaesu FT-857D — older mobile-form-factor all-mode all-band

**Backend:** `HamlibBackend`. The TS-2000's Hamlib driver `rigs/kenwood/ts2000.c`
is mature; `rigs/icom/ic9100.c` covers IC-9100.

**Distinguishing capabilities (additive over HF Transceiver class):**
- VHF + UHF band coverage in addition to HF
- `SatelliteDuplexMode` — separate uplink/downlink VFOs
- `SatelliteVfoTracked` — link uplink and downlink frequency changes
- `SatelliteCrossBand` — uplink and downlink on different bands
- `SatelliteCrossMode` — uplink one mode, downlink different
- `MainSubReceiver` — second receiver, sub VFO
- `BuiltInTNC` (sometimes), `PacketBaud1200`, `PacketBaud9600`
- `AmRxOnly` for some Icom models that receive AM but don't transmit it

### 1.4 VHF/UHF FM Mobile (TM-V71A class)

**Defining attributes:**
- Two operating bands (typically labeled A / B), VHF + UHF, both can be active
- FM and NFM only (some + AM RX); no SSB or CW
- Cross-band operation (TX on one band while RX on the other; cross-band repeat)
- Memory-primary operation (1000+ channels typical)
- Tone selection by index (CTCSS table, DCS table) — not by direct frequency
- Built-in TNC (most models)
- DTMF transmission, APRS, sometimes GPS
- Hardware flow control on serial (TM-V71A specifically; possibly others)
- Programming-mode binary protocol for bulk memory operations
- Power presets (High / Mid / Low) rather than continuous power dial
- Receive often broadband (118-1300 MHz) for scanner-like use

**Concrete representatives:**
- Kenwood TM-V71A (Doug) — Hamlib `rigs/kenwood/tmd710.c`
- Kenwood TM-D710G — TM-V71A's APRS+GPS sibling
- Yaesu FTM-400 / FTM-300 / FTM-500
- Icom IC-2730 — dual-band FM mobile
- Icom IC-7100 boundary case (it's all-mode but its FM mobile usage often falls in
  this class)

**Backend:** `HamlibBackend`. Hamlib version constraint applies (`>= 4.6.4` per
the `\r` terminator bug fix documented in `tmv71a-analysis-from-doug.md`).

**Distinguishing capabilities (largely orthogonal to HF):**
- `BandPair` — two operating bands rather than VFO A/B; PTT and CTRL band
  selection are explicit
- `CrossbandRepeat` — radio acts as a relay between its two bands
- `ToneSelectionByIndex` — pick CTCSS / DCS by index into a table, not direct Hz
- `CtcssEncodeOnly` — many mobiles support encode but not decode (or vice versa)
- `MemoryChannelEdgePairs` — channels 200-219 are scan-edge pairs on TM-V71A
- `MemoryNameLength` — 6 chars on TM-V71A, 8 on TM-D710 (per-radio data)
- `PowerPresetEnum` — High / Mid / Low rather than continuous numeric
- `ProgrammingModeBulk` — bulk memory ops use a different protocol mode
- `BuiltInTNC` + `AprsBuiltIn`
- `RequiresHardwareFlowControl` — surfaces to the serial-port config UI
- `GpsBuiltIn` (TM-D710G subset)
- `WideRxScannerMode` — receive outside ham bands

### 1.5 Handheld Transceiver (HT class) — provisional

**Defining attributes:**
- Battery-constrained operation
- Often dual-band VHF/UHF (similar surface to FM mobiles but smaller)
- Some include digital voice modes (D-STAR, DMR, C4FM/Fusion)
- Built-in GPS, APRS on some
- Smaller memory capacity (typically 200-500)
- Often programmed via "cloning cable" + dedicated software (CHIRP, RT Systems);
  CAT control over a cable for live operation is less common
- Audio: built-in speaker / mic; PC audio integration is unusual

**Concrete representatives (provisional — no current testers):**
- Kenwood TH-D74, TH-D75 — D-STAR + APRS + GPS, well-supported in Hamlib
- Yaesu FT-70D, FT-2D — Fusion (C4FM), GPS+APRS
- Icom ID-52, ID-50 — D-STAR
- Anytone AT-D878UV — DMR

**Backend:** `HamlibBackend` for radios with live CAT. Many HTs are
"programming-only" via vendor software; those don't fit `IRadioBackend` directly
but might appear in JJF as a "memory channel editor" surface (separate concern).

**Distinguishing capabilities:**
- All FM mobile capabilities (this class is essentially "FM mobile in a smaller
  form factor"), plus:
- `DigitalVoiceModes` — D-STAR / DMR / C4FM / NXDN
- `BatteryStatus` — battery level read
- `GpsBuiltIn` (most modern HTs)
- `ProgrammingOnly` — flag for radios where live CAT isn't a useful path; UI
  scope reduces to memory editing

**Provisional status:** Until a JJF tester surfaces with an HT, this class is a
placeholder in the taxonomy. The capability set is informed by Hamlib's TH-D74
driver but unverified against a real JJF user.

### 1.6 Receiver Only — provisional

**Defining attributes:**
- No transmit
- Often very wideband (HF through UHF or microwave)
- Sometimes SDR architecture (raw I/Q out), sometimes traditional superhet
- Used for shortwave listening, scanner, surveillance, propagation monitoring
- Usually have rich memory + scan capabilities

**Concrete representatives (provisional — no current testers):**
- AOR AR-DV1, AR5700D
- WinRadio WR-G31DDC, WR-G39DDC
- Icom IC-R8600 — wideband receiver
- RFspace SDR-IQ (SDR-architected)

**Backend:** `HamlibBackend` for AOR and Icom RX-only models.

**Distinguishing capabilities:**
- `SupportsTransmit` is **false** — single boolean cleanly classifies
- Wide RX coverage flags
- Memory channel scan groups
- Often `InternalPanadapter` of some flavor

**Provisional status:** Same as HT class — placeholder until a tester surfaces.
The distinction matters because UI for an RX-only radio doesn't show TX controls,
microphone profiles, ATU memories, etc. Capability-driven UI handles this naturally
once `SupportsTransmit` flows into the right places.

---

## 2. The capability flag set — proposed superset

Combining `RigCaps.Caps` (already in `Radios/RigCaps.cs`) with the new flags this
taxonomy surfaces. Phase 4 will commit to a final shape; this is the working
draft.

### 2.1 Preserved from `RigCaps.Caps` (Jim's enum)

All existing flags carry forward by name. Notable ones used heavily across
classes:

- Lifecycle: `IDGet` (rig identification — read-only)
- Frequency: `FrGet/Set`, `XFGet/Set` (TX freq), `VFOGet/Set`, `RITGet/Set`,
  `TXITGet/Set` (XIT), `AutoModeGet/Set`
- Mode: `ModeGet/Set`, `DMGet/Set` (data mode)
- Filtering: `FSGet/Set`, `FWGet/Set`, `NBGet/Set`, `NFGet/Set`, `NTGet/Set`,
  `BCGet/Set`, `EQRGet/Set`, `EQTGet/Set`
- Levels: `AFGet/Set`, `AGGet/Set`, `AGTimeGet/Set`, `MGGet/Set`, `RFGet/Set`,
  `RAGet/Set`, `PAGet/Set`, `XPGet/Set`, `SQGet/Set`
- Tones: `CTSSFreqGet/Set`, `CTModeGet/Set`, `TOGet/Set`
- CW: `KSGet/Set`, `CWAutoTuneGet/Set`, `CWDelayGet/Set`, `CWDecode`
- VOX: `VDGet/Set`, `VGGet/Set`, `VSGet/Set`
- Antenna / ATU: `ANGet/Set`, `ATGet/Set`, `ManualATGet/Set`, `ATMems`
- Memory: `MemGet/Set`
- Read-only: `SMGet`, `SWRGet`, `ALCGet`, `CLGet/Set` (carrier)
- Display: `LKGet/Set`
- Speech: `SPGet/Set` (speech processor), `SPGuideGet/Set` (radio's own audio guidance)
- TX: `TXMonGet/Set`, `ManualTransmit`
- Misc: `Pan`, `RemoteAudio`

Pair the Get/Set discipline (every settable concept has paired flags) and the
64-bit packed enum approach. The 64-bit limit is starting to matter — RigCaps
already uses 53 bits. New flags need a `Caps2` enum or a switch to a flags-as-
sets approach for the superset.

### 2.2 New flags this taxonomy adds

Streaming (currently absent — these decide whether the panadapter / waterfall /
meter / audio sections render at all):

- `PanadapterStream` (Flex; some HF rigs eventually)
- `WaterfallStream` (Flex)
- `MeterStream` (Flex)
- `AudioStream` (Flex network audio; potential digital-radio paths)
- `IqStream` (SDR-architected receivers)

Multi-receiver / multi-slice:

- `MultiSliceCapable` (Flex)
- `MaxSlices` — int, not boolean
- `MainSubReceiver` (TS-2000 / IC-9100)
- `MultiFlexClients` (Flex MultiFlex)

Satellite operation:

- `SatelliteDuplexMode`
- `SatelliteVfoTracked`
- `SatelliteCrossBand`
- `SatelliteCrossMode`

Mobile-class concerns:

- `BandPair` (A/B explicit bands rather than just VFO A/B — TM-V71A)
- `CrossbandRepeat`
- `ToneSelectionByIndex`
- `MemoryChannelEdgePairs`
- `MemoryNameLength` — int, not boolean
- `PowerPresetEnum` (mobiles: High/Mid/Low) vs continuous power
- `ProgrammingModeBulk`
- `RequiresHardwareFlowControl` (per-radio data flowing to serial config UI)
- `BuiltInTNC`
- `AprsBuiltIn`
- `GpsBuiltIn`
- `DigitalVoiceModes` (HTs)
- `BatteryStatus` (HTs)
- `WideRxScannerMode`

License/feature gating (Flex-specific but generalizable):

- `RequiresLicense` (string or enum reference)
- `FirmwareUpdateInBand`
- `FeatureLicenseDivEsc`, etc. — specific Flex feature licenses

Sanity / class:

- `RadioClass` — enum: `SdrTransceiver` / `HfTransceiver` / `AllModeAllBand` /
  `VhfUhfFmMobile` / `Ht` / `RxOnly`
- `SupportsTransmit` (boolean)
- `Bands` — set of supported amateur bands

The 64-bit `Caps` enum is already saturating; the new design likely uses a
`HashSet<RadioCapability>` or similar enum-with-flags-by-key structure rather than
a single bitfield. Phase 4 makes that call.

### 2.3 The "data not flag" insight

Some things look like flags but are really data:

- `MaxSlices` is an integer, not a boolean
- `MemoryNameLength` is an integer
- `MemoryChannelCount` is an integer
- `Bands` is a set of band identifiers
- `Modes` is a set of mode identifiers
- `BaudRateOptions` is a list

The new abstraction needs both — a flag set for capabilities (booleans about
"can do X?") AND a separate data structure for radio facts (integers, sets, lists
about "how much / which / what shape"). This is hinted at in `RigCaps` already
with `MaxVFO = (int)VFOs.VFOB` as a separate field outside the enum, but the
pattern needs to be intentional in the new design.

---

## 3. Class-to-capability matrix

For each class, the typical capability set. "Per-radio variation" means within
the class some radios have it and some don't — capability flag still gates
behavior, but the class doesn't predict it.

### 3.1 SDR Transceiver (Flex)

**Always present:** `MultiSliceCapable`, `MaxSlices` ≥ 2, `PanadapterStream`,
`WaterfallStream`, `MeterStream`, `AudioStream`, `MeteredCwTextKeyer`,
`InternalKeyer`, `RemoteAudio`, `FirmwareUpdateInBand`.

**Per-radio variation:**
- `DiversityCapable` — only 2-SCU radios (6600/6700/8600/AU-520)
- `MultiFlexClients` — 4+ on 8000-series, 1 on most 6000-series
- License-gated: `LicenseFeatDivEsc`, etc. — ungated by hardware AND by purchased license
- `MaxSlices` varies: 2 (6300/6400/8400), 4 (6500/6600/8600), 8 (6700)

**Bands:** All HF + 6m on most; FLEX-8000 series adds 4m/2m on some configurations.

**Modes:** SSB, CW, AM, FM, DIGU, DIGL, NFM, DFM, SAM (the existing JJF
`RigCaps.ModeTable`).

### 3.2 HF Transceiver (TS-590-class)

**Always present:** `FrGet/Set`, `ModeGet/Set`, `VFOGet/Set` (A/B), `KSGet/Set`,
`SMGet`, `SWRGet`, `XPGet/Set`, `MemGet/Set`, `ManualTransmit`, basic CW keyer.

**Per-radio variation:**
- USB audio routing (newer models: TS-590SG, IC-7300; older models: dedicated audio jack)
- Internal panadapter (IC-7300/7610: yes via separate IF tap; TS-590S: no; TS-590SG:
  partial via USB)
- CW decode (some)
- ATU built-in (most modern; TS-590SG has ATU)
- VOX (most)

**Bands:** 160-10m always; 6m on most modern. Some include 60m WARC bands.

**Modes:** SSB, CW, AM, FM, DIGU, DIGL.

### 3.3 All-Mode All-Band (TS-2000 / IC-9100)

**Always present:** Everything from HF Transceiver class, plus:
- `Bands` includes VHF (2m) + UHF (70cm)
- `SatelliteDuplexMode`
- `MainSubReceiver` (TS-2000 has main + sub; IC-9100 has main + sub-receive on
  another band)

**Per-radio variation:**
- 1.2 GHz (TS-2000X variant; IC-9100 with 1.2 GHz module)
- `SatelliteVfoTracked` — TS-2000 yes; IC-9100 yes; some others no
- `BuiltInTNC` — TS-2000 yes; IC-9100 no
- `CrossbandRepeat` — depends; many all-mode all-band radios support a form of it

**Bands:** HF + 6m + 2m + 70cm; some + 1.2 GHz.

**Modes:** All HF modes plus FM (broader use on VHF/UHF); some + digital voice
modes on newer models.

### 3.4 VHF/UHF FM Mobile (TM-V71A class)

**Always present:** `BandPair` (A/B), `CrossbandRepeat` (most), `ToneSelectionByIndex`,
`MemoryChannelEdgePairs`, `PowerPresetEnum`, broad memory capacity (1000 typical).

**Often present:** `BuiltInTNC`, `AprsBuiltIn`, `RequiresHardwareFlowControl`.

**Per-radio variation:**
- `GpsBuiltIn` — D710G, FTM-400 yes; TM-V71A no; FTM-300 yes
- `DigitalVoiceModes` — Yaesu Fusion, Icom D-STAR mobiles yes; Kenwood mobiles no
- `ProgrammingModeBulk` — TM-V71A/D710 yes; many Yaesu mobiles use a different
  programming protocol; capability flags by radio
- Specific tone tables vary by region (TM-V71A US version vs EU: different DCS
  entries at the same indices)

**Bands:** 2m + 70cm (some + 1.25m on US versions).

**Modes:** FM, NFM, sometimes AM (RX only on most; TX AM on rare exceptions).

### 3.5 HT (provisional)

Same as VHF/UHF FM Mobile, plus:

- `BatteryStatus`
- Probably `DigitalVoiceModes` on most modern HTs
- `ProgrammingOnly` flag for radios that don't really support live CAT

Smaller `MemoryChannelCount` (200-500 vs 1000 on mobiles).

### 3.6 RX-only (provisional)

`SupportsTransmit = false`. Wide RX coverage flags. Often `InternalPanadapter` or
`IqStream`. No mic / TX-power / ATU / VOX capabilities.

---

## 4. Tester-class mapping (concrete)

Per-tester radio-class inventory — this is the JJF testing surface across classes:

- **SDR Transceiver class:**
  - Don — FLEX-6300
  - Justin — FLEX-8400 (Mac, SmartLink inbound)
  - Noel — FLEX-6300 + FLEX-8600 (boxed, awaiting firmware trigger)
- **HF Transceiver class:**
  - Mark — Kenwood TS-590G (post-Hamlib integration)
- **All-Mode All-Band class:**
  - Noel — Kenwood TS-2000 (primary cross-class testbed —
    `project_ts2000_cross_class_testbed.md`)
- **VHF/UHF FM Mobile class:**
  - Doug — Kenwood TM-V71A (post-Hamlib integration)
- **HT class:** none currently — placeholder pending tester
- **RX-only class:** none currently — placeholder pending tester

Five of six classes have a real or committed tester. HT and RX-only stay
provisional in the architecture but the capability flags exist so adding them
later is incremental, not architectural.

---

## 5. Implications for `IRadioBackend`

The taxonomy work surfaces design questions Phase 4 must answer. Posing them
here so Phase 4 has explicit material to address.

### 5.1 Should `IRadioBackend` know its class?

**Two options:**

(a) Yes — `RadioClass` is a property on `IRadioBackend` and consumers can branch
on it directly.

(b) No — only capability flags are queryable; consumers branch on flags only,
and the class is a documentation / config affordance not visible at runtime.

**Recommendation: (b) with class as a metadata hint.** The principle is "the UI
adapts to capabilities, not to class names." A radio that happens to be in class
X but has an unusual feature for that class should still render the unusual
feature's UI. The class is useful for grouping radios in pickers and for default
config; not for runtime branching.

### 5.2 Per-class capability defaults?

When the user adds a TM-V71A to JJF for the first time, do we ship a default
capability flag set for the TM-V71A model, or do we query Hamlib at connect time
and let the radio declare its capabilities?

**Recommendation: query Hamlib at connect time AND ship known per-model defaults
as a fallback.** Hamlib's `rig_get_caps` is the source of truth; static defaults
are documentation and a sanity check. Discrepancies between the two surface real
issues (Hamlib version regressions, firmware variations) we'd want to know about.

### 5.3 Class-driven UI sectioning

UI sections like "Slices", "Panadapter", "ATU memories", "Cross-band repeat",
"GPS / APRS", "Built-in TNC" should each be gated on a capability flag the
backend reports. Not on class. **Class is not in the UI rendering decision tree;
capabilities are.**

This makes the taxonomy a *design-time* tool (deciding which capability flags
exist, how to test them, which radios exemplify them) rather than a *runtime*
dispatch.

### 5.4 Cross-class quirks

Some quirks span class boundaries. Examples:

- Hardware flow control on serial — TM-V71A requires it; some HF radios require
  it; not class-dependent
- Per-band antenna selection — common in HF and all-mode; absent in mobiles
  (which use a single antenna per band)
- Memory-channel naming length — varies model-by-model independent of class

**Implication:** Capability flags must capture what's actually observable per
radio. Class is too coarse a granularity for the UI to dispatch on. The more
specific the capability surface, the cleaner the rendering decisions.

### 5.5 What lives in `IRadioBackend` vs. per-radio config

A key Phase 5 question this section sets up: which capabilities are
backend-exposed (queryable at runtime), and which are configuration the user can
override?

- **Backend-exposed:** physical capabilities (`MaxSlices`, `Bands`,
  `SupportsTransmit`, `BuiltInTNC`)
- **User-config-overridable:** preferences (`RequiresHardwareFlowControl` —
  user can override default if their adapter does it differently;
  `BaudRate` — user-overridable)
- **Per-radio data:** memory channel count and naming length are usually radio
  facts (Hamlib reports them), but per-radio config can hold annotations the
  radio itself doesn't store (memory-slot user labels, antenna labels per port)

Phase 5 will pick this up.

---

## 6. Open design questions for Noel

1. **Is the five-class taxonomy too coarse or too fine?** Could collapse
   "HF Transceiver" and "All-Mode All-Band" into "Traditional Transceiver" with
   `Bands` and `SatelliteCapable` flags doing the differentiation. Could split
   "VHF/UHF FM Mobile" into "Dual-Band Mobile" vs "Single-Band Mobile."
   Recommendation: hold at five for now; refine when concrete TX-class oddities
   surface during implementation.

2. **HT and RX-only as provisional — confirm.** No tester yet; should we drop
   them from the taxonomy until one surfaces, or keep as placeholders to anchor
   the abstraction's openness? Recommendation: keep as placeholders; cost is one
   row in this doc and a few capability flags; benefit is the abstraction's
   openness is encoded in the design, not assumed.

3. **Custom-class users.** If a JJ Radio user surfaces with a radio that doesn't
   fit any class cleanly (e.g., a vintage receiver with an unusual command set,
   or a recent SDR like the IC-705 that bridges SDR and HF), do we promote a new
   class or treat it as a one-off? Recommendation: Hamlib-supported radios go
   straight into HamlibBackend with class membership chosen by the closest fit,
   and the capability flags carry the actual differences. Reserve class
   promotion for when ≥3 representatives of a putative new class exist.

4. **Capability flag storage.** `RigCaps.Caps` is at 53 of 64 bits used. The
   new design probably needs a `HashSet<RadioCapability>` enum-and-set approach
   or a paired `Caps` + `Caps2` enums. Either works; Phase 4 picks one based on
   which integrates more cleanly with the existing `myCaps.HasCap(...)` call
   pattern (which is plausibly preserved).

5. **Whether to expose `RadioClass` as a property at all.** If runtime dispatch
   never branches on it, it's pure documentation. Recommendation: include it
   as a read-only property because JJF logs and traces should record class for
   debugging context; it's not a UI dispatch axis but it is a diagnostic axis.

---

## 7. Forward references

This document feeds:

- **Phase 3 (Hamlib API survey)** — `rig_get_caps` mapping into the capability
  flag set drafted here
- **Phase 4 (IRadioBackend abstraction design)** — capability flag enum + class
  enum + radio-data structure
- **Phase 5 (Per-radio config strategy)** — what's queryable from backend vs.
  what's user-overridable config
- **Phase 6 (Audio routing)** — class membership influences default audio
  routing (Flex network audio vs. mobile sound-card audio)
- **Phase 7 (TS-2000 conformance)** — All-Mode All-Band class is the primary
  testbed
- **Phase 9 (Architecture synthesis)** — taxonomy feeds the radio-class section
  of the synthesis

---

## Document Status

**Phase 2 of 10 complete.** Five-class taxonomy with concrete representatives,
distinguishing capabilities, and tester mapping. Provisional classes (HT,
RX-only) flagged. Capability flag superset drafted as input to Phase 4.

**Estimated read time:** 18-22 minutes for full review; 10 minutes for sections
1, 2, and 5 alone.

**Next deliverable:** Phase 3 Hamlib API survey, drilling into the specific
Hamlib calls and `rig_caps` mapping.
