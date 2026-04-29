# Hamlib API Survey for JJ Flexible's Needs

**Status:** Multi-radio Phase 3 deliverable
**Date:** 2026-04-29
**Author:** Claude Opus 4.7 (track/multi-radio)
**Audience:** Noel — input to the IRadioBackend abstraction (Phase 4)
**Builds on:** `hamlib-integration-spike.md` (April 28 spike)

---

## What this document is

The April 28 Hamlib integration spike was strategic — license, presence of
Flex support, presence of TS-590 support, broad architectural sequencing. This
document is the operational follow-up: drilling into the specific Hamlib API
surface JJ Flexible needs to call, the data shapes those calls work with, the
P/Invoke / SWIG mechanics that bridge them into .NET, and the gaps between what
Hamlib exposes and what `IRadioBackend` (Phase 4) needs.

Verified against the local Hamlib clone at `c:/dev/hamlib` (commit pulled
2026-04-28). API references cite line numbers in `include/hamlib/rig.h` of that
checkout; verify against the version JJF eventually pins before treating any
specific signature as load-bearing.

---

## 1. Hamlib's surface area at a glance

`include/hamlib/rig.h` is 3,373 lines. It declares ~150 exported functions
covering radios; rotators and amplifiers are in separate headers. The rig API
breaks down into these conceptual groups:

- **Lifecycle** — `rig_init`, `rig_open`, `rig_close`, `rig_cleanup`
- **Frequency / VFO** — `rig_set_freq`, `rig_get_freq`, `rig_set_vfo`,
  `rig_get_vfo`, `rig_get_vfo_info`, `rig_get_vfo_list`
- **Mode / passband** — `rig_set_mode`, `rig_get_mode`, `rig_set_split_mode`,
  `rig_get_split_mode`, `rig_passband_normal/narrow/wide`
- **PTT / DCD** — `rig_set_ptt`, `rig_get_ptt`, `rig_get_dcd` (data carrier
  detect = squelch open)
- **Split operation** — `rig_set_split_freq`, `rig_get_split_freq`,
  `rig_set_split_vfo`, `rig_get_split_vfo`, `rig_set_split_freq_mode`
- **Repeater shift** — `rig_set_rptr_shift`, `rig_set_rptr_offs`
- **CTCSS / DCS tones** — `rig_set_ctcss_tone`, `rig_set_ctcss_sql`,
  `rig_set_dcs_code`, `rig_set_dcs_sql`, plus matching getters
- **RIT / XIT / tuning step** — `rig_set_rit`, `rig_set_xit`, `rig_set_ts`
- **Levels (analog)** — `rig_set_level`, `rig_get_level`, `rig_get_strength`,
  parameterised by `RIG_LEVEL_*` setting (~50 distinct levels)
- **Functions (boolean toggles)** — `rig_set_func`, `rig_get_func`, parameterised
  by `RIG_FUNC_*` (~50 distinct toggles)
- **Memory channels** — `rig_set_mem`, `rig_get_mem`, `rig_set_channel`,
  `rig_get_channel`, with `channel_t` carrying full per-channel data
- **CW** — `rig_send_morse`, `rig_stop_morse`
- **Voice memory** — `rig_send_voice_mem`, `rig_stop_voice_mem`
- **Antenna selection** — `rig_set_ant`, `rig_get_ant`
- **Power state** — `rig_set_powerstat`, `rig_get_powerstat`, `rig_reset`
- **Configuration** — `rig_set_conf`, `rig_get_conf2`, plus the extension
  parameter system (`rig_set_ext_level`, `rig_ext_func_foreach`, etc.) for
  radio-specific controls outside the standard surface
- **Capability introspection** — `rig_get_caps`, `rig_has_get_level`,
  `rig_has_set_level`, `rig_has_get_func`, etc.
- **Spectrum scope** (newer) — set via `RIG_LEVEL_SPECTRUM_*` levels and
  `RIG_FUNC_SPECTRUM` for radios that expose it via Hamlib (Icom IC-7300/7610
  primarily)
- **Callbacks** — `rig_set_freq_callback`, `rig_set_mode_callback`,
  `rig_set_ptt_callback`, `rig_set_pltune_callback`, `rig_set_spectrum_callback`,
  `rig_set_dcd_callback`

The complete picture: **everything JJF currently calls FlexLib for, EXCEPT
streaming surfaces and SmartLink, has a Hamlib equivalent.** The spike's claim
"Hamlib is the basic-CAT layer" was correct in the sense that it does not stream
audio; it is broader than "basic" suggests in every other axis.

---

## 2. The 30-50 calls JJF actually needs

For the curated Path A P/Invoke approach the spike recommended, the working
list is roughly:

### 2.1 Lifecycle (4)
```c
RIG *rig_init(rig_model_t rig_model);              // create handle for a known model
int rig_open(RIG *rig);                            // connect using rig->state config
int rig_close(RIG *rig);                           // disconnect, keep handle
int rig_cleanup(RIG *rig);                         // free handle
```

### 2.2 Frequency, VFO, mode (8)
```c
int rig_set_freq(RIG *, vfo_t, freq_t);
int rig_get_freq(RIG *, vfo_t, freq_t *);
int rig_set_vfo(RIG *, vfo_t);
int rig_get_vfo(RIG *, vfo_t *);
int rig_set_mode(RIG *, vfo_t, rmode_t, pbwidth_t);
int rig_get_mode(RIG *, vfo_t, rmode_t *, pbwidth_t *);
int rig_get_vfo_info(RIG *, vfo_t, freq_t *, rmode_t *, pbwidth_t *,
                     split_t *, int *satmode);   // composite read
int rig_get_vfo_list(RIG *, char *buf, int buflen);  // which VFOs exist
```

`rig_get_vfo_info` is the right composite read for screen-reader-friendly
"announce frequency + mode + split + satellite" composite phrasing — one CAT
round-trip, all four facts. Significantly faster than four separate calls.

### 2.3 Split operation (4)
```c
int rig_set_split_vfo(RIG *, vfo_t rx_vfo, split_t, vfo_t tx_vfo);
int rig_get_split_vfo(RIG *, vfo_t, split_t *, vfo_t *);
int rig_set_split_freq(RIG *, vfo_t, freq_t tx);
int rig_get_split_freq(RIG *, vfo_t, freq_t *);
```

### 2.4 PTT and DCD (3)
```c
int rig_set_ptt(RIG *, vfo_t, ptt_t);
int rig_get_ptt(RIG *, vfo_t, ptt_t *);
int rig_get_dcd(RIG *, vfo_t, dcd_t *);   // squelch-open status
```

### 2.5 RIT / XIT / repeater shift / step (8)
```c
int rig_set_rit(RIG *, vfo_t, shortfreq_t);
int rig_get_rit(RIG *, vfo_t, shortfreq_t *);
int rig_set_xit(RIG *, vfo_t, shortfreq_t);
int rig_get_xit(RIG *, vfo_t, shortfreq_t *);
int rig_set_rptr_shift(RIG *, vfo_t, rptr_shift_t);
int rig_set_rptr_offs(RIG *, vfo_t, shortfreq_t);
int rig_set_ts(RIG *, vfo_t, shortfreq_t);     // tuning step
int rig_get_ts(RIG *, vfo_t, shortfreq_t *);
```

### 2.6 Tones (CTCSS / DCS) (8)
```c
int rig_set_ctcss_tone(RIG *, vfo_t, tone_t);   // encode tone Hz × 10 (e.g. 1230 = 123.0)
int rig_get_ctcss_tone(RIG *, vfo_t, tone_t *);
int rig_set_ctcss_sql(RIG *, vfo_t, tone_t);    // RX squelch on tone
int rig_get_ctcss_sql(RIG *, vfo_t, tone_t *);
int rig_set_dcs_code(RIG *, vfo_t, tone_t);
int rig_get_dcs_code(RIG *, vfo_t, tone_t *);
int rig_set_dcs_sql(RIG *, vfo_t, tone_t);
int rig_get_dcs_sql(RIG *, vfo_t, tone_t *);
```

Tones are integers (Hz × 10) at the Hamlib API. The TM-V71A's index-based
tone table (Doug's research) is converted by the backend driver — JJF talks Hz
and Hamlib's tmd710.c maps to the radio's index. Good news: capability flag
`ToneSelectionByIndex` from Phase 2 is **not** something JJF needs to enforce
for Hamlib radios; Hamlib's API is index-agnostic.

### 2.7 Levels (read/write 50+ named knobs) (3)
```c
int rig_set_level(RIG *, vfo_t, setting_t level, value_t val);
int rig_get_level(RIG *, vfo_t, setting_t level, value_t *val);
setting_t rig_has_get_level(RIG *, setting_t);
```

The `setting_t` is a 64-bit flag carrying which level. The `value_t` union is
either int, float, or const char*. This is JJF's mapping target for AF gain, RF
gain, mic gain, AGC, SQL, NR, NB, KEYSPD, RFPOWER, METER selection, and many
more (`include/hamlib/rig.h:1063-1131` list).

The `rig_has_get_level(rig, RIG_LEVEL_NR)` returns the queried level if
supported, 0 if not — this is the introspection path for the capability set the
running radio actually exposes.

### 2.8 Functions (boolean toggles) (3)
```c
int rig_set_func(RIG *, vfo_t, setting_t func, int status);
int rig_get_func(RIG *, vfo_t, setting_t func, int *status);
setting_t rig_has_get_func(RIG *, setting_t);
```

50+ toggles in `rig.h:1261-1315`: NB, COMP, VOX, TONE, TSQL, SBKIN, FBKIN, ANF,
NR, AIP, APF, MON, MN, ARO, LOCK, MUTE, REV, SQL, BC, RIT, AFC, **SATMODE**,
SCOPE, TUNER, XIT, NB2, DUAL_WATCH, DIVERSITY, **SLICE**, TRANSCEIVE,
SPECTRUM, SEND_MORSE, etc.

Notable: `RIG_FUNC_SATMODE` is the Hamlib-level satellite mode toggle (relevant
for TS-2000); `RIG_FUNC_SLICE` is a Flex-specific toggle Hamlib already knows
about; `RIG_FUNC_DIVERSITY` exists across vendors (not just Flex).

### 2.9 Antenna and ATU (2 + tuner via FUNC)
```c
int rig_set_ant(RIG *, vfo_t, ant_t, value_t option);
int rig_get_ant(RIG *, vfo_t, ant_t, value_t *option,
                ant_t *ant_curr, ant_t *ant_tx, ant_t *ant_rx);
```

ATU is `RIG_FUNC_TUNER` (`rig_set_func(rig, vfo, RIG_FUNC_TUNER, 1)` to start).

### 2.10 Memory channels (4)
```c
int rig_set_mem(RIG *, vfo_t, int channel);
int rig_get_mem(RIG *, vfo_t, int *channel);
int rig_set_channel(RIG *, vfo_t, const channel_t *ch);
int rig_get_channel(RIG *, vfo_t, channel_t *ch, int read_only);
```

`channel_t` is a large struct holding frequency, mode, name, tones, antenna,
TX freq, etc. — the per-channel record. For TM-V71A this is the path to
read/write memory channels at the slow CAT rate (the fast bulk path goes
through programming-mode binary, which Hamlib's tmd710.c does not currently
expose — Phase 4 capability flag `ProgrammingModeBulk` flags this gap).

### 2.11 CW (2)
```c
int rig_send_morse(RIG *, vfo_t, const char *msg);
int rig_stop_morse(RIG *, vfo_t);
```

This is the "send these characters as CW" path. Many HF rigs have it.

### 2.12 Power and reset (3)
```c
int rig_set_powerstat(RIG *, powerstat_t);    // turn radio on/off via CAT
int rig_get_powerstat(RIG *, powerstat_t *);
int rig_reset(RIG *, reset_t);                // dangerous; soft / master reset
```

Per `project_flexibility_principle.md` the power-state controls must stay
user-opted-in. Don't ship code that powers the radio off at JJF exit.

### 2.13 Capability introspection (3)
```c
const struct rig_caps *rig_get_caps(rig_model_t);
uint64_t rig_get_caps_int(rig_model_t, enum rig_caps_int_e);
const char *rig_get_caps_cptr(rig_model_t, enum rig_caps_cptr_e);
```

`rig_get_caps` returns the capability descriptor for a given model — a large C
struct with model name, mfg name, version, supported levels/funcs, mode list,
band list, etc. This is the source of truth for "what does radio model X
support" before the user even connects.

### 2.14 Configuration / extension parameters
```c
int rig_set_conf(RIG *, hamlib_token_t, const char *val);
int rig_get_conf2(RIG *, hamlib_token_t, char *val, int val_len);

int rig_set_ext_level(RIG *, vfo_t, hamlib_token_t, value_t);
int rig_get_ext_level(RIG *, vfo_t, hamlib_token_t, value_t *);
int rig_set_ext_func(RIG *, vfo_t, hamlib_token_t, int);
int rig_get_ext_func(RIG *, vfo_t, hamlib_token_t, int *);
hamlib_token_t rig_token_lookup(RIG *, const char *name);
```

The "ext" family is for radio-specific controls outside the standard surface:
CW message memories, microphone profiles, custom DSP modes — anything
vendor/model unique that doesn't fit a generic level or function. JJF would
probably ignore these for Phase 1 of the Hamlib backend and revisit when a
specific tester request surfaces.

The non-ext `rig_set_conf` is for connection configuration (serial port, baud,
data bits, civ-address, etc.) that varies per radio. Use this at radio-creation
time.

---

## 3. Threading and async model

Hamlib's documented model is **synchronous**. Each `rig_*` call blocks until
the radio responds (or times out). The C API is not thread-safe per `rig`
handle — a single rig must be accessed from one thread at a time. Multiple rigs
may be operated concurrently (each on its own thread).

### 3.1 The async layer

For receiving asynchronous radio events (frequency change initiated by the
operator on the radio's panel, mode change, PTT change) Hamlib provides a
**callback** mechanism plus the `RIG_FUNC_TRANSCEIVE` toggle. When transceive
is on (and the radio supports it — many Kenwood, Yaesu, Icom radios do), the
radio sends unsolicited updates via CAT; Hamlib runs a background reader thread
that parses these and invokes the callbacks JJF registered.

Relevant callback registrations:
```c
int rig_set_freq_callback(RIG *, freq_cb_t, rig_ptr_t);
int rig_set_mode_callback(RIG *, mode_cb_t, rig_ptr_t);
int rig_set_ptt_callback(RIG *, ptt_cb_t, rig_ptr_t);
int rig_set_dcd_callback(RIG *, dcd_cb_t, rig_ptr_t);
int rig_set_spectrum_callback(RIG *, spectrum_cb_t, rig_ptr_t);
```

**Implication for `IRadioBackend`:** the FlexLib backend exposes events via .NET
event handlers fired on the main UI thread. The Hamlib backend exposes the
same shape, but internally:

1. Marks `RIG_FUNC_TRANSCEIVE` on if the radio supports it
2. Registers Hamlib callbacks (function pointers from native code)
3. Each callback is invoked on Hamlib's reader thread; the backend marshals
   onto JJF's UI thread (using `SynchronizationContext.Post` or a dedicated
   dispatcher)
4. For radios without transceive, the backend can fall back to polling (a
   timer task that calls `rig_get_freq` etc. and emits events when values
   change)

This makes the IRadioBackend's event model uniform across FlexLib and Hamlib
even though the underlying transport is different.

### 3.2 Polling fallback specifics

For radios that don't support transceive (or have buggy transceive
implementations), the backend polls. Reasonable poll cadence:

- Frequency / mode: 200-500 ms when no operation is in flight
- S-meter: 200 ms when unsquelched, 500 ms when squelched
- PTT state: 100 ms (this is fast-changing and matters for accessibility)
- Levels (AF, RF, SQL): on-demand only (don't poll continuously)

The poll cadence is tunable per radio class — TM-V71A class with hardware flow
control may need slower polling than a TS-590 with USB CAT.

### 3.3 P/Invoke callback marshalling

Native callbacks calling back into managed code is the trickiest P/Invoke
pattern. The standard approach:

1. Define the callback delegate `[UnmanagedFunctionPointer(CallingConvention.Cdecl)]`
2. Hold a managed reference to the delegate so the GC doesn't collect it while
   native code holds the function pointer
3. Marshal the Hamlib `rig_ptr_t` user-data argument using `GCHandle.Alloc`
   for round-tripping object identity
4. Wrap each callback with try/catch — exceptions thrown into native code are
   undefined behavior

This is well-trodden territory in .NET interop; there are no Hamlib-specific
gotchas beyond the general "be careful with native callbacks" rules.

---

## 4. The data shapes

### 4.1 `vfo_t` — Hamlib's VFO concept

`VFOs.txt` in the Hamlib repo documents the VFO model:

```
RIG_VFO_A, RIG_VFO_B            // single-VFO-pair radios
RIG_VFO_MAIN, RIG_VFO_SUB       // dual-receiver radios (TS-2000, IC-9100)
RIG_VFO_MAIN_A, RIG_VFO_MAIN_B, // explicit pairs on dual receivers
RIG_VFO_SUB_A, RIG_VFO_SUB_B
RIG_VFO_C                       // third VFO (IC-910)
RIG_VFO_CURR                    // operate on whichever VFO is currently active
RIG_VFO_VFO                     // alias for the active VFO when in VFO mode
RIG_VFO_MEM                     // memory channel mode
```

For TM-V71A class, Hamlib uses `RIG_VFO_A` and `RIG_VFO_B` to represent the two
operating bands. This is a slight overloading of the VFO concept (Band A and
Band B are operating bands, not just VFOs of a single band), but it works.

**Implication for `IRadioBackend`:** the backend exposes a "slice" abstraction
(per Phase 4 design discussion). For Flex this is real slices (up to 8). For
Hamlib radios it's `vfo_t` mapped to slice number — slice 0 = VFO_A, slice 1 =
VFO_B, slice 2 = VFO_C / VFO_SUB_A as applicable. The backend hides the
mapping; the UI just sees "current slice."

### 4.2 `freq_t`, `shortfreq_t`, `pbwidth_t`

```c
typedef double freq_t;        // frequency in Hz, double-precision
typedef long shortfreq_t;     // smaller frequency (RIT, offset, step)
typedef int pbwidth_t;        // passband width in Hz
typedef int tone_t;           // tone Hz × 10 for CTCSS
```

`freq_t` as a double is unusual but works — Hamlib uses it because some
microwave receivers tune in fractions of a Hz. JJF's marshalling can keep it
as `double` and convert at the UI layer.

### 4.3 `rmode_t` — modes as flags

```c
#define RIG_MODE_NONE   0
#define RIG_MODE_AM     CONSTANT_64BIT_FLAG (0)
#define RIG_MODE_CW     CONSTANT_64BIT_FLAG (1)
#define RIG_MODE_USB    CONSTANT_64BIT_FLAG (2)
#define RIG_MODE_LSB    CONSTANT_64BIT_FLAG (3)
#define RIG_MODE_RTTY   CONSTANT_64BIT_FLAG (4)
#define RIG_MODE_FM     CONSTANT_64BIT_FLAG (5)
#define RIG_MODE_WFM    CONSTANT_64BIT_FLAG (6)
#define RIG_MODE_CWR    CONSTANT_64BIT_FLAG (7)
#define RIG_MODE_RTTYR  CONSTANT_64BIT_FLAG (8)
// ... ~30 modes total
```

JJF's existing mode list (`RigCaps.ModeTable`) is `["LSB", "USB", "CW", "AM",
"FM", "DIGU", "DIGL", "NFM", "DFM", "SAM"]`. The Hamlib mapping is direct
except DIGU/DIGL — Hamlib uses `RIG_MODE_PKTUSB` / `RIG_MODE_PKTLSB` for those.
The backend translation table is straightforward.

### 4.4 `value_t` — the level/parm value union

```c
typedef union {
    signed int i;       // for integer levels
    unsigned int u;     // for unsigned integer levels
    float f;            // for fractional levels (most analog levels)
    char *s;            // for string-valued parms
    const char *cs;     // const-string-valued parms
} value_t;
```

The right `value_t` member to read depends on which level was queried. The
header documents this per-level (e.g., `RIG_LEVEL_AF` is "arg float [0.0 ...
1.0]"). This is a minor P/Invoke nuisance — the .NET wrapper has a small
switch per-level for which union member to access.

### 4.5 `setting_t` — 64-bit flag for level/func selection

`RIG_LEVEL_*` and `RIG_FUNC_*` are 64-bit `setting_t` flag values. Used in
`rig_set_level(rig, vfo, RIG_LEVEL_AF, val)` etc. For introspection,
`rig_has_get_level(rig, RIG_LEVEL_AF)` returns `RIG_LEVEL_AF` if supported, 0
otherwise.

### 4.6 `channel_t` — memory channel record

A large struct — frequency, mode, passband, name, tone settings, antenna,
splits, scan flags. Hamlib parses the radio's binary representation into this
shape. JJF reads/writes via this struct; the per-radio idiosyncrasies stay
inside Hamlib.

### 4.7 `rig_caps` — per-model capability descriptor

The structure returned by `rig_get_caps(rig_model_t)`. It contains:

- Model identification: `rig_model`, `model_name`, `mfg_name`, `version`,
  `copyright`, `status`
- Type: `rig_type` (transceiver, receiver, scanner, etc.)
- Port type: `port_type` (network, serial, USB, USB-RAW)
- Serial defaults: baud, data bits, stop bits, parity, handshake
- Supported lists: `mode_list`, `vfo_list`, `freq_range_list`,
  `tx_range_list`, `tuning_steps[]`, `filters[]`, `attenuator[]`,
  `preamp[]`
- Function/level masks: `has_get_level`, `has_set_level`, `has_get_func`,
  `has_set_func`, `has_get_parm`, `has_set_parm`
- Per-level/func granularity: `level_gran[]`, `parm_gran[]`
- Function pointers: 100+ function pointer slots for per-radio overrides

This is the **source of truth for the capability flag set Phase 2 enumerated.**
The mapping is direct: each `RIG_LEVEL_*` flag in `has_get_level` becomes a
JJF capability flag; each `RIG_FUNC_*` flag in `has_get_func` becomes a JJF
capability flag; the streaming surfaces (Flex-specific) come from the JJF side
because Hamlib doesn't model them.

For a TS-2000, `rig_get_caps(RIG_MODEL_TS2000)` returns the descriptor with
~25 levels supported, ~20 functions, all HF + VHF + UHF bands, all standard
modes, satellite mode flag set, etc. The backend can populate `IRadioBackend`'s
capability flags directly from this introspection — no per-model JJF code
needed for the standard surface.

---

## 5. Audio: NOT Hamlib's job

Hamlib does not handle audio. Period. The library is for CAT control, full stop.
Audio for non-Flex radios must be solved separately:

- Most modern HF transceivers have USB audio CODEC built in (TS-590SG, IC-7300).
  Audio routes through Windows audio devices (WASAPI / ASIO / NAudio).
- Older HF transceivers + most VHF/UHF mobiles use external soundcards plugged
  into the radio's data port (e.g. SignaLink USB, RIGblaster, DigiRig).
- Hamlib has nothing to say about which sound card belongs to which radio — that
  is a JJF UX problem (Phase 6 deliverable).

This is the architectural complement to "FlexLib does audio over the network"
— Hamlib does CAT only. The new design must have a place for "audio routing"
that's separate from `IRadioBackend` because the responsibility is independent.

---

## 6. .NET binding paths — concrete options

### 6.1 The current state of `bindings/csharp/`

Confirmed by directory listing of `c:/dev/hamlib/bindings/csharp/`:

```
multicast/      — only subfolder
  PLAN.txt
  README.txt
  multicast.cs
  multicast.csproj
  test.json
```

This is **not** SWIG-generated bindings. It is a separate C# helper for
listening to Hamlib's multicast UDP announcements (used by some peer
applications to announce radio state). The actual SWIG-to-C# binding work is
*not started* in the upstream repo. The spike's "near-empty stub" framing
matches reality.

The `bindings/Makefile.am` lists Perl, Python, Tcl, Lua, PHP as binding
targets. C# is conspicuously absent from the build rules — there is no
configured path to generate it. SWIG itself supports C# (the `-csharp` flag),
but Hamlib's autotools build system would need patches.

### 6.2 Path A — hand-rolled P/Invoke (recommended start)

For the ~50 calls in section 2, P/Invoke is straightforward. Approximate
structure:

```csharp
internal static class Hamlib
{
    public const string LibraryName = "libhamlib-5";

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr rig_init(int rig_model);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rig_open(IntPtr rig);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int rig_set_freq(IntPtr rig, int vfo, double freq);

    // ... etc
}
```

Marshalling concerns:

- The `RIG *` opaque pointer is `IntPtr` — never dereferenced from .NET
- `vfo_t` is `int` — fine
- `freq_t` is `double` — fine
- `value_t` (union) — define as `[StructLayout(LayoutKind.Explicit)]` with int
  / float / IntPtr at offset 0; pick member by which level was queried
- `channel_t` — large struct; marshal as `[StructLayout(LayoutKind.Sequential)]`
  matching the C declaration. ABI risk: struct layout drifts between Hamlib
  versions. Mitigation: pin a specific Hamlib version, audit the struct on
  each Hamlib bump
- Strings — Hamlib uses `char *` (UTF-8 in newer versions). Use
  `[MarshalAs(UnmanagedType.LPUTF8Str)]` for strings out, allocate `byte[]`
  buffers and pass pinned pointers for strings in
- Callbacks — `[UnmanagedFunctionPointer(CallingConvention.Cdecl)]` and hold
  managed references to delegates; call `GCHandle.Alloc` for user-data round-trips

Effort: bounded. The ~50-call surface translates to maybe 1000-1500 lines of
P/Invoke + marshalling helpers + a thin idiomatic wrapper class. Fast path to
working code.

### 6.3 Path B — SWIG-generated bindings

Hamlib's `rig.swg` defines the SWIG mapping for the rig API. Generating C#
bindings would invoke:

```bash
swig -csharp -c++ -outdir generated/ -namespace Hamlib rig.swg
```

This would emit `Rig.cs`, `RigCaps.cs`, etc., plus a C++ wrapper layer
(`rig_wrap.cxx`) that gets compiled into a separate native DLL and called from
the generated C#.

**Why we don't recommend this for Phase 1:**

1. The C++ wrapper introduces a second native DLL beyond libhamlib itself.
   That's a build-system hassle.
2. SWIG's C# output is functional but not idiomatic .NET. We'd want a thin
   idiomatic wrapper on top anyway.
3. Hamlib's autotools build system needs patches to add the C# target. That's
   a small upstream PR but adds friction.
4. The empty-stub state of `bindings/csharp/` suggests prior attempts haven't
   pushed through. Worth understanding why before committing.

**Why Path B is the right Phase 4+ direction:**

1. As we add more Hamlib backends or want to expose the full ext-level / ext-
   func surface, hand-rolling becomes maintenance-heavy.
2. SWIG-generated bindings get the full Hamlib surface for free (rotators,
   amplifiers, multicast — anything Hamlib exposes).
3. Upstream contribution path: complete the C# bindings, contribute back. The
   Hamlib team takes maintenance of the SWIG generation; we consume.

The pragmatic plan is Path A first to prove the architecture, Path B once the
volume of bindings makes hand-rolling untenable.

### 6.4 Path C — rigctld subprocess

For completeness: Hamlib ships `rigctld`, a daemon that exposes the C API as a
text-over-TCP protocol. Spawn it as a subprocess, talk to it on localhost:4532.
Each command is a line of text; each response is a line of text.

**Pros:** zero linking, zero P/Invoke. Pure socket I/O.

**Cons:** TCP round-trip per call (much slower than in-process), process
lifecycle management, stderr redirection, dependency on shipping rigctld.

**Recommendation:** keep this in our back pocket as a fallback if Windows-
specific P/Invoke issues surface (for example, if libhamlib's MinGW build
turns out to have ABI incompatibilities we can't easily resolve). Don't pre-
emptively design for it.

### 6.5 Path D — third-party C# bindings (HamlibSharp, etc.)

A web search for "HamlibSharp" or "Hamlib C#" surfaces a few community
attempts on GitHub. None appear active or comprehensive. **Don't depend on a
third-party binding.** Pin our own.

---

## 7. Hamlib version constraints

### 7.1 Minimum: 4.6.4

Per `tmv71a-analysis-from-doug.md`, Hamlib 4.6.4 is the minimum for the
TM-V71A class (issue #1767 fixed `kenwood_transaction()` stripping the `\r`
terminator). Any earlier version doesn't speak TM-V71A at all.

### 7.2 Recommended: latest 4.7.x

Hamlib 4.7.x is the current stable. It includes:

- The TM-V71A fix
- Incremental TS-2000 driver improvements
- Improved spectrum scope support for IC-7300/IC-7610
- `rig_get_vfo_info` composite read (new in 4.6+)

### 7.3 5.0 watch

Hamlib 5.0 is in development trunk. Expected breaking changes:

- ABI stabilization across the rig structs
- API removals for long-deprecated functions
- Possible namespacing changes

JJF should target 4.7.x for the first integration, plan a single-pass
migration to 5.0 when it goes stable. Pin specific versions in the manifest;
don't dynamic-resolve "whatever Hamlib is installed."

### 7.4 Bundling vs. linking dynamically against system Hamlib

The friction-tax principle (`project_friction_tax_principle.md`) says JJF
should not require users to install Hamlib separately. Bundle libhamlib-5.dll
in the JJF installer; load it from the install directory.

License-wise, bundling is fine under LGPL (covered in spike section 2).
End-user replacement of the bundled DLL must remain possible — JJF's install
layout already supports this.

---

## 8. The introspection-driven backend

A clean architectural pattern emerges from the API survey: **the HamlibBackend
populates its capability flags dynamically from `rig_get_caps()` rather than
hardcoding per-model logic.**

```csharp
// Sketch — final shape decided in Phase 4
public class HamlibBackend : IRadioBackend
{
    private readonly IntPtr _rig;
    private readonly RigCapsAdapter _caps;

    public HamlibBackend(int rigModel, ConnectionConfig conf)
    {
        _rig = Hamlib.rig_init(rigModel);
        ApplyConfig(conf);  // rig_set_conf calls
        Hamlib.rig_open(_rig);
        _caps = LoadCaps(rigModel);
    }

    private RigCapsAdapter LoadCaps(int rigModel)
    {
        var capsPtr = Hamlib.rig_get_caps(rigModel);
        var caps = Marshal.PtrToStructure<rig_caps>(capsPtr);

        var flags = new HashSet<RadioCapability>();

        if ((caps.has_get_level & RIG_LEVEL_AF) != 0) flags.Add(RadioCapability.AfGet);
        if ((caps.has_set_level & RIG_LEVEL_AF) != 0) flags.Add(RadioCapability.AfSet);
        if ((caps.has_get_level & RIG_LEVEL_NR) != 0) flags.Add(RadioCapability.NrLevelGet);
        // ... 50+ more

        if ((caps.has_get_func & RIG_FUNC_TUNER) != 0) flags.Add(RadioCapability.AtuAuto);
        if ((caps.has_get_func & RIG_FUNC_SATMODE) != 0) flags.Add(RadioCapability.SatelliteMode);
        // ... 50+ more

        return new RigCapsAdapter(flags, caps);
    }
}
```

This means:

- Adding a new Hamlib radio model usually requires **zero JJF code changes** —
  the `RigTable` entry plus existing `HamlibBackend` covers it
- New radios get the capability flags Hamlib reports for them; UI sections
  show/hide automatically
- Hamlib version bumps that add levels/functions surface automatically as new
  capability flags (assuming the JJF-side enum has been extended to include
  them)

There will be exceptions — radio-specific quirks `rig_get_caps` doesn't fully
capture. For these, Phase 5's per-radio config provides the override surface.

---

## 9. Gaps between Hamlib and IRadioBackend (preview for Phase 4)

Identifying what Phase 4 has to handle that Hamlib alone doesn't cover:

### 9.1 Streaming surfaces — not in Hamlib

`PanadapterStream`, `WaterfallStream`, `MeterStream`, `AudioStream`,
`IqStream` are Flex-specific (or SDR-specific generally). The HamlibBackend
returns `null` / `Hidden` for these capabilities. Phase 4's interface design
must allow optional streaming surfaces.

### 9.2 SmartLink remote — not in Hamlib

Same answer: Flex-specific, FlexLibBackend implements, HamlibBackend returns
"not supported." `IsSmartLinkRemote` capability flag on the backend.

### 9.3 MultiFlex multi-client — not in Hamlib

Same answer.

### 9.4 Programming-mode bulk operations (TM-V71A) — not in current Hamlib

Hamlib's tmd710.c does CAT only, not programming-mode. Slow channel-list
read/write only. Capability flag `ProgrammingModeBulk` is false on TM-V71A
through HamlibBackend until upstream Hamlib gains the support OR JJF adds a
parallel path. Don't optimize for this in Phase 1.

### 9.5 Per-band antenna labeling — partially in Hamlib

Hamlib has `rig_set_ant` but the per-port user-friendly names (the antenna-
definition memory captured in `project_per_radio_config_serial_keyed.md`)
live in JJF's per-radio config. The backend exposes "antenna 1, antenna 2,
antenna 3 are valid"; the user-config layer maps those to names like
"hex beam" / "wire dipole".

### 9.6 ATU memories with names

Hamlib has `RIG_FUNC_TUNER` for "start auto-tune" but no concept of named ATU
memories. Per-radio config layer holds these.

### 9.7 Feature licenses (Flex-specific)

`LicenseFeatDivEsc` etc. are Flex-specific concepts. HamlibBackend has no
equivalent. `RequiresLicense` capability set is null for Hamlib radios.

### 9.8 Radio class metadata

Hamlib's `rig_caps.rig_type` enum has values like `RIG_TYPE_TRANSCEIVER`,
`RIG_TYPE_HANDHELD`, `RIG_TYPE_MOBILE`, `RIG_TYPE_RECEIVER` — close to but not
the same as Phase 2's class taxonomy. The HamlibBackend can use this hint plus
band/mode introspection to assign a class, but it's an heuristic.

---

## 10. Concrete API call examples for the testbed radios

### 10.1 TS-2000 connect + announce composite state (HF mode)

```csharp
var rig = Hamlib.rig_init(RIG_MODEL_TS2000);  // 1011
Hamlib.rig_set_conf(rig, conf_token("rig_pathname"), "COM3");
Hamlib.rig_set_conf(rig, conf_token("serial_speed"), "9600");
Hamlib.rig_open(rig);

double freq;
int mode, width, split, satmode;
Hamlib.rig_get_vfo_info(rig, RIG_VFO_A, out freq, out mode, out width, out split, out satmode);
// Speak: "VFO A, 14.225 MHz, USB, split off, sat off"
```

### 10.2 TS-2000 satellite mode operation

```csharp
Hamlib.rig_set_func(rig, RIG_VFO_CURR, RIG_FUNC_SATMODE, 1);
Hamlib.rig_set_freq(rig, RIG_VFO_MAIN, 145850000);   // 145.850 MHz uplink
Hamlib.rig_set_freq(rig, RIG_VFO_SUB,  435870000);   // 435.870 MHz downlink
Hamlib.rig_set_mode(rig, RIG_VFO_MAIN, RIG_MODE_USB, 2700);
Hamlib.rig_set_mode(rig, RIG_VFO_SUB,  RIG_MODE_LSB, 2700);
```

### 10.3 TM-V71A connect + read frequency on Band B

```csharp
var rig = Hamlib.rig_init(RIG_MODEL_TMD710);  // TM-V71A uses TMD710 model id
Hamlib.rig_set_conf(rig, conf_token("rig_pathname"), "COM4");
Hamlib.rig_set_conf(rig, conf_token("serial_speed"), "57600");
Hamlib.rig_set_conf(rig, conf_token("serial_handshake"), "Hardware");
Hamlib.rig_open(rig);

double freq;
Hamlib.rig_get_freq(rig, RIG_VFO_B, out freq);
// Speak: "Band B, 446.000 MHz"
```

### 10.4 TS-590 set RF gain

```csharp
var val = new value_t { f = 0.75f };
Hamlib.rig_set_level(rig, RIG_VFO_CURR, RIG_LEVEL_RF, val);
```

### 10.5 Capability check before showing a UI section

```csharp
// In the JJF UI layer, asking the backend
if (backend.HasCapability(RadioCapability.SatelliteMode))
{
    ShowSatellitePanel();
}
else
{
    HideSatellitePanel();
}
```

---

## 11. Open questions for Phase 4

1. **`vfo_t` mapping to slice number.** The backend maps Hamlib VFOs to a
   uniform "slice 0, slice 1, ..." model. For dual-receiver radios (TS-2000
   main + sub), is slice 1 the sub receiver or the second VFO of the main?
   Recommendation: slice 0 = MAIN_A, slice 1 = MAIN_B, slice 2 = SUB_A, slice 3
   = SUB_B for dual-receiver radios. Document the mapping per radio.

2. **Hamlib model ID stability.** `rig_model_t` integers like
   `RIG_MODEL_TS2000=1011` are the API contract. They're stable across Hamlib
   versions in practice but not guaranteed. Capture the model ID in per-radio
   config alongside the human-readable model name.

3. **Failure model — what does `int rig_*` return on failure?** Hamlib's
   error codes in `rig.h:140-180` are negative integers (`RIG_OK=0`,
   `-RIG_EINVAL`, `-RIG_ECONF`, `-RIG_ENOMEM`, `-RIG_ENIMPL`, `-RIG_ETIMEOUT`,
   `-RIG_EIO`, `-RIG_EINTERNAL`, `-RIG_EPROTO`, `-RIG_ERJCTED`, `-RIG_ETRUNC`,
   `-RIG_ENAVAIL`, `-RIG_ENTARGET`). Need a translation layer mapping these to
   JJF's error model (which feeds accessible error messages). The
   ConnectionProfiler's existing error taxonomy is the integration target.

4. **Per-vfo vs `RIG_VFO_CURR`.** Many calls take a `vfo_t`. Hamlib's
   `RIG_VFO_CURR` means "operate on whatever VFO is currently active." JJF's
   slice abstraction names a slice explicitly. Default to passing the explicit
   slice's VFO; use `RIG_VFO_CURR` only when specifically meaningful (e.g.,
   typing "what's the current frequency" without prior slice selection).

5. **Polling vs transceive default.** Should the Hamlib backend default to
   transceive enabled (when supported) and fall back to polling, or always
   poll? Recommendation: transceive when supported, with the caveat that some
   buggy implementations cause stuck states and we add a per-radio config flag
   to disable transceive if needed (`UseHamlibTransceive` boolean).

6. **CW message keying — Hamlib's `rig_send_morse` vs JJF's CW system.** JJF
   has a CW processor for screen-reader CW notifications and CWX-style hardware
   keying. For Hamlib radios, `rig_send_morse` provides hardware keying on
   radios that support it. The capability flag `HardwareCwKeyer` gates the
   "send via radio" path; the JJF CW pipeline (audio-side) is always available
   regardless.

---

## 12. Forward references

This document feeds:

- **Phase 4 (IRadioBackend abstraction design)** — the API surface in section
  2 is the "calls JJF needs" target; the gaps in section 9 are the surface
  Phase 4 must explicitly model
- **Phase 5 (Per-radio config strategy)** — section 4.7 (`rig_caps`
  introspection) plus per-radio overrides for things Hamlib doesn't capture
  (antenna names, ATU memories, transceive toggle)
- **Phase 6 (Audio routing for non-Flex)** — section 5 explicitly notes
  audio is out of scope for Hamlib; Phase 6 is the JJF-side answer
- **Phase 7 (TS-2000 conformance)** — section 10's TS-2000 examples are the
  starting point for the conformance test list
- **Phase 9 (Architecture synthesis)** — Hamlib's role as the CAT layer for
  non-Flex radios with FlexLib retained for SDR concerns

---

## Document Status

**Phase 3 of 10 complete.** API surface enumerated, P/Invoke pattern sketched,
introspection-driven capability mapping proposed, threading model documented,
gaps to `IRadioBackend` flagged for Phase 4.

**Estimated read time:** 25-30 minutes for full review; 12 minutes for
sections 2, 8, and 9 alone.

**Next deliverable:** Phase 4 IRadioBackend abstraction design — the
deliverable that consumes JJ Radio inventory + class taxonomy + this Hamlib
survey and proposes the unifying interface.
