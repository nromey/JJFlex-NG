# JJ Radio Scope Inventory

**Status:** Multi-radio Phase 1 deliverable
**Date:** 2026-04-29
**Author:** Claude Opus 4.7 (track/multi-radio)
**Audience:** Noel — input to the multi-radio architecture synthesis (Phase 9)

---

## What this document is

The folding-in of JJ Radio (Jim Shaffer's pre-JJFlex application) into JJ Flexible
inherits a user base whose radios JJ Flexible must support. Before designing the
abstraction that lets JJ Flexible host multiple radio backends, we need a clear,
honest inventory of what JJ Radio actually was: which radios it supported, what it
exposed per radio, and what its abstraction looked like.

The good news: **JJ Radio's abstraction layer is still in our own codebase.** Jim
designed `AllRadios` + `RigCaps` to be multi-radio from the start; the Flex-only
modernization in commit `d8b67758` (2026-01-28) deleted the non-Flex implementations
but left the abstraction shape intact. We are not inheriting a black box — we are
re-extending an architecture our own `Radios/` directory still embodies.

---

## 1. Radios JJ Radio explicitly supported

Reconstructed from:

- The `RigTable` in `AllRadios.cs` before commit `d8b67758` (`git show d8b67758~1:Radios/AllRadios.cs`)
- The deleted radio-implementation files (`Generic.cs`, `Icom*.cs`, `Kenwood*.cs`, `StubRadios.cs`)
- The deleted model-specific UI forms (`TS2000Filters`, `TS590Filters`, `ic9100filters`)
- The Dropbox archive `C:/Users/nrome/Dropbox/JJFlexRadio/old/JJRadio.msi` (predecessor MSI installer, predates the JJFlex rename)

### 1.1 The supported set

| Radio | Class | File(s) before deletion | Lines |
|-------|-------|-------------------------|------:|
| Kenwood TS-2000 | All-mode all-band (HF + VHF + UHF + sat) | `KenwoodTS2000.cs`, `TS2000Filters.cs/.designer.cs/.resx` | 2,293 + 998 + 1,175 + 120 |
| Kenwood TS-590S | HF transceiver | `KenwoodTS590.cs`, `TS590Filters.cs/.designer.cs/.resx` | 2,438 + 1,113 + 1,310 + 120 |
| Kenwood TS-590SG | HF transceiver (TS-590S successor) | `KenwoodTS590SG.cs` (subclass of TS-590) | 111 |
| Icom IC-9100 | All-mode all-band (HF + VHF + UHF + sat) | `Icom9100.cs`, `ic9100filters.cs/.designer.cs/.resx` | 550 + (designer + cs + resx) |
| Flex 6300 | SDR transceiver | `Flex.cs`, `FlexBase.cs`, `Flex6300Filters.cs` | live in tree, expanded |
| Generic CAT (text) | Open-ended terminal | `Generic.cs` class `Generic` | 68 (whole file is two classes) |
| Generic CAT (binary) | Open-ended terminal | `Generic.cs` class `GenericBinary` | (in same file) |
| (Stubs) | Placeholder model entries | `StubRadios.cs` | 54 |

Vendor base classes (shared command constants, transaction parsing):

- `Kenwood.cs` — 2,105 lines, parent of TS-2000 / TS-590 / TS-590SG
- `Icom.cs` — 3,417 lines, parent of IC-9100 (CI-V protocol)
- `IcomIhandler.cs` — 396 lines, low-level CI-V byte handler
- `kenwoodIHandler.cs` — Kenwood text-protocol handler

The deletion commit removed roughly **16,000 lines** of multi-radio code from the
JJFlex tree. JJ Radio was a substantially larger surface than the current
`Radios/` directory shows.

### 1.2 What JJ Radio did NOT support

Notably absent from JJ Radio's RigTable:

- **Yaesu** — no FT-991, FT-857D, FT-450, FTM-* family.
- **Elecraft** — no K3, K4, KX2, KX3.
- **VHF/UHF FM mobiles other than the IC-9100/TS-2000 mode** — no TM-V71A, no
  FTM-400. Doug's TM-V71A is *new ground* for the merged product, not inherited
  legacy.
- **Handhelds** — no D-STAR / DMR / fusion HTs.
- **Receivers** — no AOR, no WinRadio, no SDR-IQ.
- **Icom HF** — no IC-7300, IC-7610, IC-7100, IC-9700. The IC-9100 was the only
  Icom and it was a satellite-class radio.

JJ Radio's radio surface skewed Kenwood-and-Flex with a single Icom satellite radio
on the side. This is consistent with Jim's personal stack and a community of
operators using the same gear.

---

## 2. Feature surface per radio (what JJ Radio exposed)

The operations in JJ Radio's `RigCaps.Caps` enum (still present at
`Radios/RigCaps.cs:22-121`) list the conceptual feature surface the abstraction was
designed for. Not every radio implements all of it — the per-radio `myCaps`
constructor declares which subset applies.

Categories of operation, drawn directly from the enum:

- **Frequency / mode / VFO** — `FrGet/Set`, `XFGet/Set` (transmit freq), `ModeGet/Set`,
  `VFOGet/Set`, `RITGet/Set`, `TXITGet/Set` (XIT), `AutoModeGet/Set`
- **Filtering / DSP** — `FSGet/Set` (filter shift), `FWGet/Set` (filter width),
  `NBGet/Set` (noise blanker), `NFGet/Set` (notch frequency), `NTGet/Set` (notch),
  `BCGet/Set` (beat cancellation), `EQRGet/Set` / `EQTGet/Set` (RX/TX equalizer)
- **Levels** — `AFGet/Set` (AF gain), `AGGet/Set` / `AGTimeGet/Set` (AGC + time),
  `MGGet/Set` (mic), `RFGet/Set` (RF gain), `RAGet/Set` (RF attenuator), `PAGet/Set`
  (preamp), `XPGet/Set` (TX power), `SQGet/Set` (squelch)
- **Tones / FM** — `CTSSFreqGet/Set`, `CTModeGet/Set` (CTCSS), `TOGet/Set` (tone status)
- **CW** — `KSGet/Set` (keying speed), `CWAutoTuneGet/Set`, `CWDelayGet/Set`,
  `CWDecode`
- **VOX** — `VDGet/Set` (delay), `VGGet/Set` (gain), `VSGet/Set` (status)
- **Antenna / ATU** — `ANGet/Set`, `ATGet/Set` (auto), `ManualATGet/Set` (manual),
  `ATMems` (memory)
- **Memory** — `MemGet/Set`
- **Display / read-only** — `IDGet`, `SMGet` (S-meter), `SWRGet`, `ALCGet` (read-only),
  `CLGet/Set` (carrier), `LKGet/Set` (lock)
- **Speech / data modes** — `SPGet/Set` (speech processor), `DMGet/Set` (data mode),
  `SPGuideGet/Set` (radio's own speech guidance)
- **Misc** — `Pan` (panning support), `ManualTransmit`, `RemoteAudio`

This is a **HF-transceiver-shaped feature surface.** It was built around what a
TS-590 / TS-2000 / IC-9100 / Flex-6300 expose to a host program. It does NOT
include:

- VHF/UHF FM mobile primitives — band/PTT-band selection (Kenwood `BC` command
  shape), cross-band repeat, programming-mode bulk memory, hardware flow control
  requirements
- Satellite duplex VFO linking as an explicit concept (the abstraction has VFOs A/B/C/D
  but not "tracked uplink/downlink" semantics)
- Built-in TNC / APRS / GPS as first-class operations
- Radio-class taxonomy hooks (no enum or flag for "this is a mobile" / "this is an HT")

This is an honest finding: Jim's abstraction **was multi-vendor but
single-radio-class** in its mental model. Extending it to honor JJ Radio's user
base alone would not fully cover the radios our own current testers own (Doug's
TM-V71A is outside this surface entirely). The new design has to widen the class
mental model, not just the vendor list.

### 2.1 What the per-model files added

Looking at the line counts, each per-model file is doing real work — these are
not thin shims. Sampling `Kenwood.cs` (vendor base, 2,105 lines) shows ~150 named
command constants (`kcmdAC` through `kcmdXO`), the K-protocol command layer, IF
status decode, mode mapping, memory-channel parsing, etc. The per-model files
(TS-2000 at 2,293 lines, TS-590 at 2,438) override and extend with model-specific
EX-menu numbering, mode tables, frequency limits, and capability flags.

**The 111-line `KenwoodTS590SG.cs`** is a textbook subclass-as-variant: it
inherits TS-590S behavior wholesale and overrides only the things that differ on
the SG revision (different EX numbering, USB-audio specifics). This validates
"per-vendor base + per-model subclass" as a workable extension axis when a vendor
ships multiple closely-related models.

### 2.2 What the per-model UI forms added

The UI mirrored the abstraction: each model had its own `*Filters.cs` form
(`TS2000Filters`, `TS590Filters`, `ic9100filters`, `Flex6300Filters`) with model-
specific filter and DSP controls. The form sizes (cs + designer combined: 2,173
for TS-2000, 2,423 for TS-590, similar for IC-9100) tell us a similar story —
each model carried real UI variation, not a generic "filter dialog" with
parameters.

**Implication for the multi-radio architecture:** per-radio UI variation isn't
a future bridge — it's the historical norm. The Customize Home work
(`project_customize_home_vision.md`) is the right successor for this; we should
not try to build "one filters dialog to rule them all" when Jim's evidence is
that per-model variation is real and necessary.

### 2.3 The `Generic` base — pragmatic but limited

`Generic.cs` defines two extremely thin classes (`Generic` and `GenericBinary`)
that inherit from `AllRadios` and just route raw text or binary bytes into a
`Callouts.DirectDataReceiver` for the user / a script to interpret. The capability
list is *empty* (`capsList = { }`). They're not generic-CAT abstractions — they
are escape hatches for radios JJ Radio didn't model, exposing a terminal-like
interaction surface. We should NOT preserve this pattern; Hamlib's "pick your radio
from a list" model dominates this use case more cleanly.

---

## 3. Tester roster overlap with JJ Radio

Mapping current testers and their radios against JJ Radio's historical support:

- **Don** — FLEX-6300 → covered by JJ Radio (Flex was Jim's primary)
- **Justin** — FLEX-8400 → not covered by JJ Radio (post-dates it; FlexLib only)
- **Noel** — FLEX-6300 + boxed FLEX-8600 → 6300 covered; 8600 is post-JJ-Radio
- **Noel** — Kenwood TS-2000 → **covered by JJ Radio** (KenwoodTS2000.cs, 2,293 lines)
- **Mark** — Kenwood TS-590G → **covered by JJ Radio** (KenwoodTS590SG.cs as
  subclass of TS-590S)
- **Doug** — Kenwood TM-V71A → **NOT covered by JJ Radio** (no VHF/UHF FM mobile
  support in the legacy code)

Counter-intuitively, two of three named non-Flex testers are operating radios JJ
Radio already had a working backend for. Noel's TS-2000 and Mark's TS-590G/SG
both have ~2,300+ line implementations sitting in git history at commit
`d8b67758~1`. Doug's TM-V71A is the genuinely new radio class.

This shapes the porting question for Phase 4:

- **For TS-2000 and TS-590S/SG**, we have a historical reference implementation.
  Hamlib's `rigs/kenwood/ts2000.c` and `rigs/kenwood/ts590.c` are the production
  paths going forward (LGPL, maintained, far broader than JJ Radio's coverage),
  but Jim's code is a useful sanity check for *which* CAT operations he found
  necessary in practice and how he mapped them to the user-facing capability set.
- **For TM-V71A**, the VHF/UHF FM mobile class is genuine new territory. We
  cannot crib from Jim. Doug's protocol research and Hamlib's `rigs/kenwood/tmd710.c`
  are the inputs.

### 3.1 Inherited-but-unsupported users — the unknown

The JJ Radio user base presumably includes operators whose radios are NOT in our
current tester roster:

- IC-9100 owners (full implementation existed in JJ Radio)
- Other Kenwood K-protocol owners who used the TS-2000 / TS-590 path with mods
- Generic/GenericBinary users who hand-rolled support for their own radios

We do not have a list of these users. When such a list surfaces (presumably from
Jim's records, the JJ Radio repo, or active community), it directly informs Phase 4
sequencing. Pre-emptively, the architecture must not paint into a corner that
makes adding support for those radios harder than adding new ones.

---

## 4. JJ Radio's abstraction pattern — what survives

Jim's abstraction (still present in our codebase) is the foundation we extend.
Here is its actual shape:

### 4.1 The class hierarchy

```
AllRadios (abstract base, 2,751 lines today)
├── FlexBase (8,273 lines today — Flex-specific concerns, FlexLib wrapper)
│   └── FlexRadio (15-line concrete entry point, instantiated by RigTable)
├── Kenwood (deleted — 2,105 lines: vendor base, K-protocol command constants)
│   ├── KenwoodTS2000 (deleted — 2,293 lines)
│   └── KenwoodTS590 (deleted — 2,438 lines)
│       └── KenwoodTS590SG (deleted — 111 lines: SG-specific overrides)
├── Icom (deleted — 3,417 lines: CI-V protocol, vendor base)
│   └── Icom9100 (deleted — 550 lines)
└── Generic (deleted — escape hatch for unmodeled radios)
    └── GenericBinary (deleted — binary protocol variant)
```

Note that **`AllRadios` is still the base class of `FlexBase` today.** The
abstraction is not removed — only the non-Flex children of `AllRadios` were
deleted. This means our extension work doesn't have to reconstruct the abstraction;
it has to update and replace it with something better suited to the broader scope.

### 4.2 The capability-flag system (`RigCaps`)

`Radios/RigCaps.cs` defines a 64-bit `Caps` enum with paired Get/Set bits and a
`SetBit` flag. Each radio constructs `myCaps = new RigCaps(Caps[])` listing its
supported operations. Code that wants to check whether the current radio supports
something does:

```csharp
if (myCaps.HasCap(RigCaps.Caps.NRGet)) { ... }
```

This is the **same shape** as the capability-flag set proposed in
`hamlib-integration-spike.md` and the `tmv71a-analysis-from-doug.md` taxonomy.
The new design's flag enum is a superset of `RigCaps.Caps`, not a replacement.
Concretely: keep `RigCaps.Caps` enum names where they exist; add new flags for
mobile-class concerns (`CrossbandRepeat`, `BuiltInTNC`, `ProgrammingModeBulk`,
`RequiresHardwareFlowControl`, `ToneSelectionByIndex`); keep paired Get/Set
discipline.

### 4.3 The rig registration mechanism (`RadioSelection.RigTable`)

```csharp
public static RigElement[] RigTable = {
    new RigElement(RIGIDFlex6300, "Flex6300", typeof(FlexRadio), FlexComDefaults),
    // ...
};
public static AllRadios GetRig(int id) { /* Activator.CreateInstance */ }
```

Each entry holds a stable integer ID, a display name, a CLR `Type` (the
concrete class to `Activator.CreateInstance`), and a `ComDefaults` struct
(serial port settings or "network"). The pattern is sound and survives
unmodified — IRadioBackend's registration table is the same shape, with
the `Type` referring to the new backend implementation class.

### 4.4 Discovery and connection

`AllRadios.RadioDiscoveredEventArgs(name, model, serial)` is the discovery
event shape — three strings. Network radios emit it; serial radios go through
`ManualNetworkRadioInfo` (currently a no-op). For Hamlib radios, "discovery"
means "user picks model + connection params from a list," which fits the
`ManualNetworkRadioInfo` shape minus the network bit. The eventual new design
generalizes the channel: discovery beacon (Flex), Hamlib model picker (CAT), and
manual entry (legacy escape hatch).

### 4.5 Per-radio communication defaults (`ComDefaults`)

```csharp
public class ComDefaults {
    public ComType ComType;   // serial | network
    public int Baud;
    public Parity Parity;
    public int DataBits;
    public int StopBits;
    public Handshake Handshake;
    public bool ExposeBaud;   // can user override
    public bool ExposeCom;    // can user override
}
```

This is the per-radio connection-config shape. For Flex all entries reference a
single `FlexComDefaults` (which is the network variant). For Hamlib radios, each
RigElement gets a `ComDefaults` reflecting that model's serial defaults.

The `ExposeBaud` / `ExposeCom` booleans are an early form of "let the user
override defaults if their setup needs it" — exactly the flexibility-principle
shape (`project_flexibility_principle.md`). The new design preserves this and
extends it (RTS/CTS toggle, civ-address override for Icom CI-V, network address
override).

### 4.6 What's missing from Jim's abstraction

The abstraction was designed for the radios Jim had. It does not include:

- **Audio routing** — JJ Radio assumed the radio owns its audio. Flex's network
  audio model wasn't a natural fit; the audio pipeline grew up parallel to
  `AllRadios` (in `JJPortaudio` and the Flex audio code) rather than through it.
  For VHF/UHF FM mobiles where audio routes via PC sound card, audio routing has
  to enter the abstraction (Phase 6 deliverable).
- **Capability negotiation events** — `CapsChangeEvent` exists, but the enum is
  fixed at radio-construction time. For radios where capabilities change with
  state (TS-2000 in satellite mode vs HF mode exposes different controls),
  dynamic capability flags or per-mode capability overlays would be a refinement.
- **Slice / multi-receiver concept** — `RigCaps.VFOs` is enumerated A/B/C/D
  (max 4). Flex's slice concept (up to 8 on FLEX-6700) and MultiFlex (multiple
  GUI clients per radio) needed expansion that lives in `FlexBase`, not
  `AllRadios`. The new design generalizes "slices" as the unifying concept and
  collapses VFO-only radios to a single slice.
- **Streaming surfaces** — panadapter, waterfall, S-meter stream, audio stream
  are not in `AllRadios`. They live in `FlexBase` and the dedicated managers
  (`PanAdapterManager`, `FlexWaterfall`, etc.). The new design exposes
  optional streaming capabilities in `IRadioBackend` returning null on backends
  that don't have them.
- **Radio class taxonomy** — no enum or flag distinguishes "this is an HF
  transceiver" from "this is a mobile" from "this is an HT." Adding this is
  Phase 2's job.

---

## 5. The `JJRadio.msi` archive

`C:/Users/nrome/Dropbox/JJFlexRadio/old/JJRadio.msi` is the original JJ Radio
installer (predating the JJFlexRadio rename). It is the only artifact in that
folder labeled "JJRadio" — every other installer is `Setup JJFlexRadio_*.exe`.
This confirms JJ Radio shipped publicly under that name before the codebase
became JJFlexRadio.

We have not extracted the MSI to compare its UI surface to the JJFlex tree (the
binaries are .NET assemblies; reflecting them would tell us the same thing the
git history already does, with less fidelity). If specific JJ Radio UI conventions
need to be honored at folding time (e.g. for migration messaging), extracting the
MSI is a separate action when that need surfaces.

---

## 6. Implementation pattern — the "how" of adding a new radio in JJ Radio

For any future Hamlib backend, this is the pattern Jim used and the new design
should preserve / improve. Concretely, to add a new radio to JJ Radio you:

1. **Pick (or create) a vendor base class** under `AllRadios`. For Kenwood you
   inherit from `Kenwood` (which knows the K-protocol). For Icom you inherit from
   `Icom` (which knows CI-V). For a new vendor you inherit from `AllRadios` and
   write the protocol layer.
2. **Write a per-model class** with the radio's specific defaults — frequency
   ranges, mode tables, EX-menu numbering, capability flag list, command-name
   overrides.
3. **Write a per-model UI form** (`<Model>Filters.cs`) for filter/DSP/mode
   controls that vary by radio.
4. **Register in `RadioSelection.RigTable`** with an `RIGID*` constant, display
   name, type reference, and `ComDefaults`.
5. **Implement the abstract overrides on `AllRadios`** — `Open`, `Close`, the
   protocol reader callback (`InterruptHandler` text or `IBytesHandler` binary),
   the VFO operations, etc.

**For the Hamlib-based future**, the pattern collapses dramatically:

1. **No vendor base** — Hamlib provides the protocol layer.
2. **No per-model class** — Hamlib's model registration is the radio's identity.
3. **Per-model UI variation** still happens (some via Customize Home, some via
   capability-flag-driven UI section show/hide, some via per-radio config in
   `radios\<key>\config.xml`).
4. **One registration entry per Hamlib model** — auto-extracted from
   `rig_get_caps` (~hundreds of entries).
5. **One `HamlibBackend` class** implements `IRadioBackend` against libhamlib;
   it is the one C# class needed for any Hamlib-supported radio.

The "no per-model C# class" outcome is the biggest architectural simplification.
JJ Radio needed a 2,400-line per-model class because Jim was rolling the
protocol layer himself per vendor; Hamlib does that part for us.

---

## 7. Honest takeaways — what JJ Radio teaches us

1. **The abstraction shape works.** `AllRadios` + `RigCaps` + `RigTable` is a
   sound multi-radio scaffold. Don't redesign it from zero; extend it.
2. **Capability flags belong in the design from day one.** Jim's `RigCaps.Caps`
   enum is the historical evidence that capability-driven UI is the right pattern
   for diverse rigs. We should land at "every section of UI is capability-gated"
   as a hard rule.
3. **Per-model UI variation is a real cost.** JJ Radio paid 1,000-2,400 lines per
   model in UI code. The new design must let common UI (frequency, mode, PTT)
   live in one place while letting per-radio UI variation live in the right
   per-radio scope (Customize Home, per-radio config, capability-driven
   show/hide).
4. **Vendor coverage skewed toward Kenwood.** Two of three legacy non-Flex
   classes were Kenwood. Hamlib's TS-2000 / TS-590 / TM-V71A coverage is
   excellent here, so JJ Radio's vendor distribution doesn't penalize us — the
   Hamlib-supported set is a strict superset of JJ Radio's.
5. **Radio-class breadth was narrow.** No mobiles, no HTs, no receivers in JJ
   Radio. The new design's class taxonomy work (Phase 2) is genuinely additive,
   not a re-enumeration of legacy.
6. **The escape hatch (`Generic`) was an honest acknowledgement of unmodeled
   radios.** The new design's equivalent is "Hamlib supports this radio model,
   add it to the rig list" — a structurally cleaner answer than "talk raw bytes
   to the user." Don't preserve Generic / GenericBinary.
7. **Per-radio communication defaults are first-class.** `ComDefaults` is small
   but central. The new design preserves and extends it (Hamlib civ-address,
   RTS/CTS, Hamlib version pinning).
8. **JJ Radio's user base, when it surfaces, may add radios to the priority
   queue we haven't yet anticipated.** Architecture must remain receptive — no
   premature commitments to a closed list.

---

## 8. Forward references

This document feeds:

- **Phase 2 (Radio class taxonomy)** — JJ Radio's narrow class coverage motivates
  the explicit taxonomy work.
- **Phase 4 (IRadioBackend abstraction design)** — `RigCaps` shape is preserved
  and extended; `AllRadios` shape is the historical reference; `RigTable` is the
  registration pattern.
- **Phase 5 (Per-radio config strategy)** — `ComDefaults` + `RIGID*` integer ID
  are the historical key; serial-keyed config (per `project_per_radio_config_serial_keyed.md`)
  generalizes from this.
- **Phase 9 (Architecture synthesis)** — the framing "we are extending Jim's
  abstraction, not replacing it" should anchor the synthesis.

---

## Document Status

**Phase 1 of 10 complete.** Single-source synthesis of JJ Radio's scope,
abstraction pattern, and tester overlap. Ready for cross-referencing in
subsequent phases.

**Open question for Noel:** The JJ Radio user base — how do we discover what
radios its current users actually operate? The 2-of-3 tester overlap with
historical support (TS-2000, TS-590) is encouraging; the unknown is the
remainder of the user base. Surfacing that list (via Jim's records, an active
JJ Radio community channel, or an opt-in registration once JJF supports
multi-radio) directly informs Phase 4 sequencing.

**Estimated read time:** 12-15 minutes for full review; 5 minutes for sections
1, 4, and 7 alone.
