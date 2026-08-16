# Priority Watch — research and design

**Status:** research complete 2026-08-16, nothing built, nothing committed beyond
this file. Written against `honest-tx-audio`, which carries FlexLib
**4.2.20.41343** (same as `main` since the 2026-08-11 fast-forward). Every
`FlexLib_API/` line number below was read on this branch today.

**Feature under study:** the operator nominates a frequency — Noel's example is
the maritime net on 14.300 — and the app watches it for activity while they
operate elsewhere, alerts audibly when something happens, and optionally offers
to move them there. Modelled on priority scan in a scanner, per
`docs/planning/active/barefoot-splatter-ragchew.md` §"Priority watch".

**How to read the confidence markers.** This project treats description drift as
its dominant defect class, so every load-bearing claim below is labelled:

- **VERIFIED** — I read it in the code on this branch and cite file:line.
- **INFERRED** — a conclusion drawn from verified code plus reasoning, where the
  reasoning could be wrong. Each one names what would falsify it.
- **UNKNOWN** — I could not settle it from the repository. Listed again in
  §11 with a bench procedure.

Nothing here is written from memory of how Flex radios work in general. Where
general knowledge is the only source, it is called out as such.

---

## 1. The spectrum path, end to end, as it actually exists

### 1.1 How a panadapter is created

**VERIFIED.** `Radio.RequestPanafall()` sends the literal string
`display panafall create x=100 y=100` (`FlexLib_API/FlexLib/Radio.cs:5489-5492`).
That is the only panadapter-creation helper the vendor library exposes. A
`display pan create` variant is not wrapped.

**VERIFIED.** Radio status of the form `display pan 0x… <k=v>…` is parsed at
`Radio.cs:3358-3392`; it constructs a `Panadapter` for any stream ID not already
known and hands the key/value tail to `Panadapter.StatusUpdate`
(`Panadapter.cs:862-1343`). Note carefully: **that code does not filter on client
handle.** Every panadapter the radio reports — including another MultiFlex
client's — lands in `Radio.PanadapterList`. The app's own filtering happens one
layer up, in `FlexBase.panadapterAdded` via `myClient(pan.ClientHandle)`
(`Radios/FlexBase.cs:6760-6780`). Consequence for this feature in §9.4.

**VERIFIED.** Arbitrary radio commands are reachable from app code without
touching vendor FlexLib: `Radio.SendCommand(string)` is `internal`
(`Radio.cs:4778`) but `Radio.SendReplyCommand(ReplyHandler, string)` is
**public** (`Radio.cs:4798`). So any `display pan …` form the wrapper needs can
be issued from `Radios/` without an edit that `MIGRATION.md` would make us
re-apply on every FlexLib upgrade.

### 1.2 How spectrum data arrives

**VERIFIED.** Two independent streams come out of one panafall, on two VITA
class codes (`FlexLib_API/Vita/VitaFlex.cs:25-26`):

- `SL_VITA_FFT_CLASS = 0x8003` — the panadapter FFT frame. Dispatched at
  `Radio.cs:3011` into a `ConcurrentQueue<VitaFFTPacket>` (`Radio.cs:2765-2775`),
  drained by a dedicated thread `ProcessFFTDataPacket_ThreadFunction`
  (`Radio.cs:2816-2840`, thread created at `Radio.cs:12167-12170`), which looks
  the panadapter up by stream ID and calls `Panadapter.AddData`.
- `SL_VITA_WATERFALL_CLASS = 0x8004` — the waterfall tile. Dispatched at
  `Radio.cs:3020` straight into `ProcessWaterfallDataPacket`
  (`Radio.cs:2842-2856`) → `Waterfall.AddData`.

Both are counted separately in the byte-rate telemetry the vendor library
already maintains: `AvgFFTkbps` (`Radio.cs:1152`) and `AvgWaterfallkbps`
(`Radio.cs:2798`). That telemetry is a ready-made measuring instrument for §11.

**VERIFIED — the FFT packet carries no frequency information.**
`VitaFFTPacket` (`FlexLib_API/Vita/VitaFFTPacket.cs:21-26`) has exactly
`start_bin_index`, `num_bins`, `bin_size`, `frame_index`, `total_bins_in_frame`
and a `ushort[] payload`. There is no centre frequency, no bin bandwidth, no
span. To know what frequency a bin represents you must read the `Panadapter`
object's `CenterFreq` and `Bandwidth` at the moment the frame arrives — which is
a race against any retune in flight.

**VERIFIED — the waterfall tile is self-describing in frequency.**
`WaterfallTile` (`FlexLib_API/Util/WaterfallTile.cs:28-99`) carries
`FrameLowFreq`, `BinBandwidth`, `TotalBinsInFrame`, `FirstBinIndex`,
`LineDurationMS`, `Timecode`, `AutoBlackLevel` and the arrival `DateTime`
alongside `ushort[] Data`. Each tile says for itself where it starts and how wide
each bin is.

This asymmetry is a real architectural finding and neither the brief nor the
plan doc anticipated it. See §5.3.

**VERIFIED — a frame is delivered only when it is complete.**
`Panadapter.AddData` (`Panadapter.cs:764-841`) copies each packet's bins into a
`ushort[_width]` buffer at `start_bin`, accumulates `_frame_bins`, and fires
`OnDataReady` **only** when `_frame_bins == _width` exactly
(`Panadapter.cs:822`). Any packet whose `start_bin + length` exceeds `_width` is
dropped outright (`Panadapter.cs:770-775`). So on a lossy link a wide panadapter
can lose one fragment per frame and deliver *nothing at all* — not a degraded
frame, no frame. This matters enormously for the remote tester and is developed
in §4.4.

The waterfall path reassembles fragments differently: complete-in-one-packet
tiles fire immediately, fragments are held in a dictionary keyed by `Timecode`
and fire when the accumulated `Width` reaches `TotalBinsInFrame`
(`Waterfall.cs:642-660` and `AddFragmentedTile` following). Same
all-or-nothing property, but keyed on a timecode rather than an implicit
current-frame, so out-of-order arrival is handled more gracefully.

### 1.3 The subscription surface a watcher would use

**VERIFIED.** `Panadapter.DataReady` is a plain C# multicast event —
`public delegate void DataReadyEventHandler(Panadapter pan, ushort[] data);`
and `public event DataReadyEventHandler DataReady;`
(`Panadapter.cs:848-854`). `Waterfall.DataReady` is the same shape, delivering
`(Waterfall fall, WaterfallTile tile)` (`Waterfall.cs:662-668`).

**This is the crux of the central question.** These are ordinary events. Any
number of subscribers can attach. The radio has no idea how many there are and
sends exactly the same bytes either way. A watcher that reads bins out of a
frame someone else already asked for imposes **zero** additional cost on the
radio and on the link.

**VERIFIED — the whole frame is handed to every subscriber.** `OnDataReady` passes
`_buf`, the complete `ushort[_width]`, not a slice of it. So a watcher can pick
whatever bins it wants from a frame requested for another purpose, and can also
see the *rest* of the span — which §7.5 turns into the single most valuable
false-alarm defence.

### 1.4 The controls a panadapter exposes

All **VERIFIED** in `FlexLib_API/FlexLib/Panadapter.cs`:

- `Width` → `xpixels` (`:290-308`). Setting it reallocates the local `ushort[]`
  buffer and sends `display pan set 0x… xpixels=N`.
- `Height` → `ypixels` (`:310-324`).
- `CenterFreq` → `center`, in MHz, with a reply handler that corrects the local
  value if the radio disagrees (`:338-372`).
- `Bandwidth` → `bandwidth`, in MHz, clamped locally to `MinBandwidth` /
  `MaxBandwidth` (`:386-412`), both of which are read-only and arrive from the
  radio (`:374-384`, parsed at `:1070-1113`).
- `LowDbm` / `HighDbm` → `min_dbm` / `max_dbm`, clamped at -180 and +20
  respectively (`:442-504`).
- `FPS` → `fps` (`:506-519`).
- `Average` (`:550-563`) and `WeightedAverage` (`:576-589`) — radio-side
  smoothing applied before the data reaches us.
- `WNBOn` / `WNBLevel` — the wideband noise blanker, per panadapter
  (`:113-156`).
- `RXAnt` (`:167-180`) and `RFGain` (`:182-195`) — per panadapter, and both
  change the absolute level of every bin.
- `DAXIQChannel` → `daxiq_channel` (`:250-263`).
- `ClickTuneRequest(double freqMHz)` → `slice m <freq> pan=0x…` (`:856-859`).
- `Close()` → `display pan remove 0x…` (`:745-756`).

**VERIFIED — the waterfall's bin count is not independently settable.** The
`Waterfall.Width` setter's `SendCommand` line is commented out in the vendor
source (`Waterfall.cs:245`), as is the `ypixels` one (`:261`). Waterfall bin
count therefore follows the parent panadapter's `xpixels`. The waterfall does
have its own `FallLineDurationMs` → `line_duration` (`:529-541`),
`FallBlackLevel` → `black_level` (`:544-557`), `FallColorGain` → `color_gain`
(`:559-572`) and `AutoBlackLevelEnable` → `auto_black` (`:574-588`).

### 1.5 What the application already does with all this

**VERIFIED and important.** The app is *already* running the whole spectrum
pipeline in every session:

- `FlexBase.panadapterAdded` sets `pan.Width = 5000` on **every** panadapter
  belonging to this client, unconditionally (`Radios/FlexBase.cs:6769`).
- `FlexBase.NewSlice()` calls `theRadio.RequestPanafall()` and then waits for
  both a slice and a matching panadapter count
  (`FlexBase.cs:10819-10859`) — so the app runs one panafall per slice.
- `PanAdapterManager` (`Radios/PanAdapterManager.cs`) drives the *active* slice's
  panadapter: `Width = brailleWidth*50 + brailleWidth` (2040 bins for the default
  40 cells), `Height = 700`, `FPS = 1`, `LowDbm = -121`, `HighDbm = -21`, and
  subscribes both `DataReady` events (`:250-262`, constants at `:32-37`).
- `FlexWaterfall` (`Radios/FlexWaterfall.cs`) turns waterfall tiles into the
  braille line, taking the max within each cell and autoscaling between the
  frame's own floor and a `swampThreshold` of 25000 which the comment says was
  "set experimentally for about s9" (`:117-121`, `:257-351`).
- It is live in the WPF app: `WpfFilterAdapter` constructs the manager
  (`Radios/WpfFilterAdapter.cs:38`).

**VERIFIED — "Show panadapter" does not stop any of it.** The setting only sets
`PanadapterPanel.Visibility` (`JJFlexWpf/MainWindow.xaml.cs:604-607`,
config field `JJFlexWpf/AudioOutputConfig.cs:167`). The stream keeps flowing.

**Description-drift note, worth fixing separately.**
`docs/help/md/panadapter-visibility.md` tells users that with the panel hidden
the app "is still receiving all of the IQ data from the radio". It is not
receiving IQ — it is receiving radio-computed FFT frames and waterfall tiles.
IQ is a different, far more expensive stream (§4.2) that the app does not open
at all. The user-facing conclusion of that sentence is right; the mechanism named
is wrong, and this is exactly the class of sentence that later gets quoted as
fact.

**VERIFIED — a probable standing bandwidth leak.** `Width = 5000` is set on every
one of this client's panadapters, and only the *active* slice's panadapter is
subsequently narrowed to 2040 bins at 1 fps by `panParameterSetup`. A second
slice's panadapter therefore sits at 5000 bins at whatever FPS the radio
defaults to, feeding an event with no subscribers. 5000 bins is 10,000 payload
bytes per frame; the byte rate is that times the default FPS. I could not
determine the default FPS from the repository — see §11, item 5 — so I will not
put a number on it, but the shape is: **the app may be paying a meaningful
per-extra-slice spectrum cost today for data nobody consumes.** This is worth
measuring before priority watch is designed around "spectrum is flowing
anyway", because it may be flowing far more expensively than intended.

---

## 2. The resource ledger

### 2.1 Panadapters

**VERIFIED.** `max_panadapters` and `available_panadapters` arrive in the
discovery packet (`FlexLib_API/FlexLib/Discovery.cs:549-560` and `:432-443`),
are copied onto the `Radio` in `API.cs:186-187`, and surface as
`Radio.MaxPanadapters` (`Radio.cs:15053-15065`) and
`Radio.AvailablePanadapters` (`Radio.cs:15067-15079`). A live-updating count
also arrives as a `panadapters=` key in radio status and lands in
`Radio.PanadaptersRemaining` (`Radio.cs:4306-4317`, property at `:5976-5993`).
The app already traces it (`FlexBase.cs:5737-5743`).

**The radio declares its own limit; we read it. There is no static table of
panadapter counts per model in `ModelInfo.cs`** — I checked, and `ModelInfo`
carries `MaxDaxIqChannels`, `DaxIqSampleRates` and `SliceList` but no
panadapter field (`ModelInfo.cs:31-51`). So the per-model panadapter number can
only be known at runtime, from a connected radio.

### 2.2 Slices

**VERIFIED.** `max_slices` / `available_slices` arrive by the same discovery
route (`Discovery.cs:562-573`, `:445-456`), copied at `API.cs:188-189`, exposed
as `Radio.MaxSlices` / `Radio.AvailableSlices` (`Radio.cs:15096`, `:15082`).
`ModelInfo.SliceList` additionally names the letters each model supports
(e.g. `{"A","B"}` for the FLEX-6300, `ModelInfo.cs:109`).

**VERIFIED — a bare slice costs a panadapter, a slice on an existing panadapter
does not.** The vendor library has two overloads, and its own doc comments state
the difference: `RequestSlice()` with no arguments sends `slice create` and is
documented as "Creates a new Slice on the radio **on a new Panadapter**"
(`Radio.cs:5603-5609`), while `RequestSlice(Panadapter pan, …)` builds
`slice create pan=0x<streamID> …` (`Radio.cs:5591-5601`). So two slices can share
one panadapter, which is how SmartSDR does it.

### 2.3 DAX IQ

**VERIFIED.** `MaxDaxIqChannels` is a static per-model field
(`ModelInfo.cs:44`). Reading the table: **2 channels** on FLEX-6300 (`:104`),
6400 (`:127`), 6400M (`:149`), 8400 (`:287`), 8400M (`:312`), AU-510 (`:561`),
AU-510M (`:586`) and the `DEFAULT` fallback (`:81`); **4 channels** on 6500
(`:172`), 6600 (`:195`), 6600M (`:218`), 6700 (`:241`), 6700R (`:264`), 8600
(`:337`), 8600M (`:362`), AU-520 (`:611`), AU-520M (`:636`).

Sample rates track the same split: the 2-channel models offer
`{24000, 48000, 96000}` and the 4-channel models add `192000`
(`ModelInfo.cs:110` versus `:178`, and the same pattern down the table).

**VERIFIED — and this is the correction that matters — DAX IQ does not sidestep
the panadapter limit. It sits on top of one.** Three independent pieces of
evidence:

1. The vendor's own comment on the stream status format:
   `stream <streamid> type=dax_iq daxiq_channel=<channel> pan=<panadapter> rate=<rate> client_handle=<handle>`
   (`Radio.cs:3725`).
2. `DAXIQStream` has a `Panadapter Pan` property populated from the `pan=` key
   of that status (`FlexLib_API/FlexLib/DAXIQStream.cs:38-42`, `:138-153`).
3. `Panadapter` itself carries `DAXIQChannel`, which sends
   `display pan set 0x… daxiq_channel=N` (`Panadapter.cs:250-263`), and the radio
   offers `Radio.FindPanByDAXIQChannel` to go the other way
   (`Radio.cs:5964-5974`).

The in-repo probe agrees: `tools/rigbench/daxiq_probe.py` takes its own GUI seat
and either reuses a panadapter it owns or creates a panafall, *then* issues
`stream create type=dax_iq daxiq_channel=1` (probe header lines 10-11 and
`:249-270`), and `memory/project_daxiq_iq_findings.md` records the same.

So the resource ordering is the opposite of "IQ is the unconstrained path":

- Reading bins from a panadapter that already exists — **free**.
- A new panadapter — **one of `MaxPanadapters`**.
- DAX IQ — **one of `MaxPanadapters` PLUS one of 2 or 4 IQ channels.**

---

## 3. The central question, answered

> Can a watcher read spectrum data from an existing panadapter/stream without
> claiming a new scarce resource?

**Yes. The hypothesis holds — the scarce resource is consumed per span, not per
watcher — and it is verified in code rather than assumed.** `DataReady` is a
multicast event handing every subscriber the entire frame
(`Panadapter.cs:848-854`), so three watch frequencies inside one 200 kHz span
cost exactly what one costs, and one outside every existing span must claim a
panadapter (or a panadapter plus an IQ channel).

But three corrections sharpen it, and the third is the one that decides the
architecture.

### Correction 1 — DAX IQ is strictly more expensive, not an escape hatch

Established above in §2.3. The plan doc already corrected "IQ is 16 channels" to
"2 or 4"; the further correction is that an IQ watcher pays the panadapter cost
*as well*. There is no configuration in which DAX IQ costs less than a
panadapter. It should be struck from the options list for this feature entirely
(§4.2 gives the bandwidth reason as well).

### Correction 2 — the unit of cost is span **times bins times frame rate**, and the last two are shared mutable state

A span is not a fixed price. `Width` (`xpixels`) and `FPS` are properties of the
`Panadapter` object, singular, shared by every subscriber
(`Panadapter.cs:290-308`, `:506-519`). Two watchers on one source that need
different bin widths or different cadences do not each get what they asked for —
the panadapter gets one setting, and someone is either under-served or the link
pays for the stricter requirement on everyone's behalf.

So "one more watcher in the same span is free" is true only when that watcher's
resolution and cadence requirements are already met. A CW watcher wanting 10 Hz
bins joining an SSB watcher happy with 100 Hz bins is not free — it is a
tenfold increase in that source's byte rate. The broker in §8 exists precisely to
make that negotiation explicit and reportable rather than accidental.

### Correction 3 — "an existing span" is not stable, because the only span the app owns follows the operator's tuning

**VERIFIED.** The single panadapter the app actively manages is
`rig.Panadapter`, defined as `theRadio?.ActiveSlice?.Panadapter`
(`FlexBase.cs:6725-6731`). `PanAdapterManager.RXFreqChange` → `FreqChange`
re-derives the display segment from the current RX frequency and, whenever the
segment changes, calls `panParameterSetup()` which rewrites that panadapter's
`CenterFreq` and `Bandwidth` (`PanAdapterManager.cs:161-210`, `:250-257`).

A watcher riding that panadapter therefore **stops watching the instant the
operator tunes to another band, and nothing tells anyone.** That is the exact
failure mode the whole feature exists to avoid: a priority watch that silently
isn't watching is worse than no priority watch, because the operator has stopped
listening for the net themselves.

The consequence: for v1, **a watch must own a span, not borrow one.** The
free-ride optimisation ("this watch happens to fall inside the operator's
current display segment, so cost nothing") is only safe once there is a spectrum
source whose span is not slaved to the operator's VFO. When the recording /
waterfall work lands such a source, the broker in §8 will pick it up
automatically, because watchers ask for coverage rather than naming a
panadapter.

---

## 4. Panadapter versus DAX IQ versus slice S-meter

Three candidate sources, not two. The third is not in the brief and I think it is
the right answer for part of the feature.

### 4.1 Panadapter FFT / waterfall — the recommended source

The radio computes the FFT and sends display-scale data.

**Byte rate, computed from VERIFIED structure.** The FFT payload is one `ushort`
per bin: `VitaFFTPacket` allocates `payload = new ushort[num_bins]` and copies
`num_bins * bin_size` bytes into it (`VitaFFTPacket.cs:58-61`). So the frame
payload is `Width × 2` bytes, and the stream is that times `FPS`, plus VITA and
UDP headers per packet.

Worked figures, all payload-only:

- The app's current per-extra-slice panadapter at 5000 bins: 10,000 bytes per
  frame. At 10 fps that is 100 kB/s (800 kbit/s); at 30 fps, 300 kB/s
  (2.4 Mbit/s). The multiplier is unknown until the default FPS is measured.
- The app's active-slice panadapter at 2040 bins and 1 fps: 4.1 kB/s, about
  33 kbit/s.
- A purpose-built watch source at 256 bins and 2 fps: **1.0 kB/s, about
  8 kbit/s.**
- The same at 128 bins and 1 fps: 256 B/s, about 2 kbit/s.

A watcher is cheap by two to three orders of magnitude compared to what the app
already spends, provided the watch source is sized for watching rather than for
display.

Waterfall tiles cost about the same per frame (one `ushort` per bin,
`WaterfallTile.Data`), at a cadence set by `line_duration` rather than `fps`.

**Cost to the radio:** one panadapter. **Cost to our CPU:** negligible — a peak
over a few dozen bins per frame.

### 4.2 DAX IQ — reject for this feature

Raw complex samples, which we would FFT ourselves.

**Byte rate, computed from VERIFIED structure.** `VitaIFDataPacket.payload` is
`float[]` and the wide IQ classes are 24/48/96/192 kHz
(`VitaFlex.cs:30-33`, and `tools/rigbench/daxiq_probe.py:55` maps the same class
codes to the same rates). At 4 bytes per float and two floats per complex
sample, the *lowest* offered rate of 24 kHz is **192 kB/s, about 1.5 Mbit/s**.
48 kHz is 3.1 Mbit/s; 192 kHz is 12.3 Mbit/s.

That is 150 to 1500 times the cost of a fit-for-purpose panadapter watch source,
for a measurement — "is there energy here" — that does not need sample-level
fidelity. Add that it consumes a panadapter anyway (§2.3) and one of only 2 or 4
IQ channels, and there is no configuration in which it wins.

**Reject.** DAX IQ remains the right tool for the audio-arc instrument work it
was opened for (`docs/planning/active/daxiq-instrument-task.md`); it is the wrong
tool for a watcher.

### 4.3 The slice S-meter — the right source for "let me hear it"

**VERIFIED.** Every slice publishes a calibrated level: the per-slice meter named
`LEVEL` is hooked in `Slice.AddMeter` and forwarded as
`Slice.SMeterDataReady(float)` (`FlexLib_API/FlexLib/Slice.cs:1922-1940`, event
at `:3332-3337`). FlexLib subscribes to all meters at connect
(`Radio.cs:2347`, `sub meter all`), so the packets are already flowing; a meter
stream is a handful of bytes per update.

**VERIFIED — the app currently throws away non-active slices' S-meter values.**
`FlexBase.sMeter_t.sMeterData` gates on `s.Active` before reporting
(`FlexBase.cs:7186-7196`). A watcher must subscribe to `Slice.SMeterDataReady`
itself rather than expecting `FlexBase._SMeter` to carry it. That is fine — it is
another multicast event.

What this buys that spectrum cannot: a **real receiver** on the watch frequency,
with the right mode and filter, whose level is in dBm from the radio's own
calibration, and whose audio can be routed to us over DAX RX audio (which, per
the plan doc's own correction, is *not* the scarce kind of DAX). "Alert me, and
let me listen without moving" becomes possible.

What it costs: **one slice.** On a FLEX-6300 with two slices — which is Don's
radio — a watcher would halve the receiver count. That is far too expensive to
be the default. On a 6600 or 8600 with four, or a 6700 with eight, it is
comfortable.

**Recommendation: spectrum-derived by default; slice-backed as an explicitly
chosen upgrade, with the cost stated in the UI before it is taken.** The slice
can be attached to an existing panadapter (`RequestSlice(pan, …)`,
`Radio.cs:5591`) so it need not also claim a panadapter.

### 4.4 Does the answer differ over SmartLink versus LAN?

**Yes, and more sharply than raw bandwidth suggests.**

The bandwidth argument alone already rules out DAX IQ for a remote operator: at
1.5 Mbit/s minimum, a 24 kHz IQ stream would compete directly with the Opus
audio the operator actually cares about.

But the decisive point is the **all-or-nothing frame assembly** verified in
§1.2. A 5000-bin frame is 10,000 payload bytes, which cannot fit one datagram
and is therefore split across roughly seven packets at a 1500-byte MTU. If one
of those is lost, `_frame_bins` never reaches `_width` and `DataReady` never
fires for that frame (`Panadapter.cs:822`). At a few percent packet loss, a
seven-fragment frame completes maybe 70-80% of the time; the loss compounds
against the fragment count.

A 256-bin frame is 512 payload bytes — **one datagram**. It either arrives or it
does not, and a missed frame costs one sample interval rather than desynchronised
reassembly.

So the remote answer is not merely "use the panadapter", it is **"use a narrow
panadapter, sized so a frame fits in one datagram"**. That is a design
constraint I would not have derived from bandwidth reasoning, and it points at
a hard ceiling: keep watch-source `Width` at or below roughly 700 bins so the
payload stays under a typical MTU. 256 is a comfortable default with margin for
VITA and UDP headers and any tunnel encapsulation SmartLink adds.

This also raises a question about the app's *current* behaviour on a remote
link: the active-slice panadapter at 2040 bins is 4,080 payload bytes, about
three fragments. Whether Don's braille panadapter line is quietly dropping
frames today is measurable and worth measuring (§11, item 7).

---

## 5. Resolution and accuracy

### 5.1 Frequency resolution is ours to choose, within an unknown floor

**VERIFIED.** Bin width is `Bandwidth / Width`, both of which we set
(`Panadapter.cs:386-412`, `:290-308`), bounded below by the radio-reported
`MinBandwidth` (`:374-384`). For the waterfall path the bin width is delivered
per tile as `WaterfallTile.BinBandwidth` (`WaterfallTile.cs:52`), so it does not
have to be computed at all.

Worked numbers at a 256-bin watch source:

- 200 kHz span → 781 Hz per bin. An SSB signal (about 2.4 kHz) occupies three
  bins; a CW signal occupies part of one.
- 50 kHz span → 195 Hz per bin. SSB spans twelve bins, CW about two.
- 20 kHz span → 78 Hz per bin. SSB about thirty, CW about three.
- 10 kHz span → 39 Hz per bin. CW about six.

### 5.2 Is that enough to watch one SSB or CW signal without being swamped by neighbours?

**For SSB, comfortably — but only at the narrower spans.** On a crowded 20 metre
band, adjacent SSB signals sit 3 kHz apart at best. At 781 Hz per bin the
neighbours are only four bins away, and the FFT's own spectral leakage plus the
radio's smoothing will spread a strong adjacent signal into the watch band. At
195 Hz per bin the neighbour is fifteen bins away with room to separate.

**Recommendation: a watch source should span at most about 50 kHz.** That still
comfortably satisfies the plan doc's example of several watch frequencies
sharing one span — a maritime net, a calling frequency and a DX window inside
the same 50 kHz is a realistic grouping — while giving each watch enough bins to
be selective.

**For CW, the honest answer is "marginal, and it is the wrong tool".** A watcher
whose job is "is the net active" is an energy detector, and energy detection at
three bins is fragile: a nearby strong carrier, a birdie, or the operator's own
sidetone leakage all look like energy. If watching a CW frequency turns out to
matter, the slice-backed watcher (§4.3) is the right answer for it, because the
radio's own CW filter does the selectivity for us.

**A second accuracy consideration, VERIFIED and easy to miss:** the panadapter
applies radio-side smoothing before we ever see the data — `Average`
(`Panadapter.cs:550-563`) and `WeightedAverage` (`:576-589`). Smoothing is a
low-pass on the level, so it directly interacts with the dwell timing in §7.
A watch source should set these deterministically rather than inheriting
whatever the display wanted, and I would start with averaging **off** so the
detector's own dwell logic is the only time constant in the loop.

### 5.3 Amplitude accuracy — the one genuinely unresolved question

**UNKNOWN, and it is the most load-bearing gap in this document.** I could not
determine from the repository what the `ushort` bin values mean.

What I can say:

**INFERRED (moderate-to-strong confidence): the panadapter FFT bins are
display-scaled into the `ypixels` range, between `min_dbm` and `max_dbm`.** The
reasoning is that there is no other purpose for telling the radio `ypixels`
(`Panadapter.cs:310-324`) alongside `min_dbm` and `max_dbm` (`:442-504`) — the
radio is being asked to do the scaling so the client can plot directly. If so,
`dBm = HighDbm − (value / (Height−1)) × (HighDbm − LowDbm)`, and with the app's
current `Height = 700` over a 100 dB window that is 0.14 dB per step, which is
ample.

**What falsifies it:** change `Height` and see whether the bin values rescale.
If they do not, they are an absolute intensity and the dBm window is a client-side
concern only. §11 item 1.

**Countervailing VERIFIED evidence that the mapping is at least not obvious:**
`FlexWaterfall` treats 25000 as "about S9" (`FlexWaterfall.cs:119-121`) and the
vendor's own synthetic test generator produces noise in the 13000-16000 range and
signals up to 50000 (`Waterfall.cs:61-82`). Those are waterfall-path numbers, not
FFT-path numbers, and the two paths need not share a scale — but nothing in the
repo states either scale.

**Why this matters more than it looks.** If bin values are pixel-scaled against
`min_dbm`/`max_dbm`, then anyone who changes the panadapter's dBm window
silently rescales every watcher's thresholds. A watch source must therefore
**own** its `LowDbm`/`HighDbm` and never share a panadapter whose dBm window
another feature adjusts. If instead the values are absolute, sharing is safe.
The architecture in §8 assumes the pessimistic case, because assuming the
pessimistic case costs nothing and assuming the optimistic case costs a class of
bug that only appears when two features are used together.

**The design consequence that survives either answer, and should be adopted
regardless: express thresholds in dB above a tracked noise floor, never in
absolute dBm.** Three VERIFIED reasons. `Panadapter.RFGain` (`:182-195`) and
`RXAnt` (`:167-180`) both change every bin's absolute level. `WNBOn` /
`WNBLevel` (`:113-156`) change the floor. And band noise itself moves tens of dB
between a quiet morning and an evening thunderstorm. A relative threshold
survives all of that; an absolute one needs re-tuning every time anything
changes, which means in practice it will be wrong.

The one thing a relative threshold cannot do is report a meaningful absolute
signal level to the operator ("S7"). If that is wanted — and for a meter-style
readout it probably is — it needs the calibration in §11 item 3, or the
slice-backed watcher, which gets calibrated dBm for free.

### 5.4 The waterfall path is the safer source, and this was not expected

Restating §1.2's asymmetry as a recommendation, because it is actionable:

- The FFT frame arrives as a bare `ushort[]` with no frequency metadata
  (`VitaFFTPacket.cs:21-26`). To map bin to frequency the watcher must read
  `Panadapter.CenterFreq` and `Bandwidth` at handler time — values that another
  thread may be rewriting.
- The waterfall tile carries `FrameLowFreq` and `BinBandwidth` in the data
  itself (`WaterfallTile.cs:28`, `:52`).

**The app has already been bitten by exactly this race.** `PanAdapterManager`'s
waterfall handler compares the tile's `CenterFreq` against the expected segment
centre and re-runs the whole parameter setup when they disagree, guarded by a
`centerChangeSent` latch to avoid a loop (`PanAdapterManager.cs:387-402`). And
`FlexWaterfall.Write` rejects out-of-range tiles using the tile's own frequency
fields (`FlexWaterfall.cs:213-232`). Both are workarounds for data arriving that
describes a span the client has already moved on from.

A watcher gets this wrong in the worst possible way: it would evaluate a
threshold against bins from a *different frequency* and either alert on the
wrong signal or miss the right one, with no symptom other than being wrong.

**Recommendation: the watcher consumes `Waterfall.DataReady` tiles, and
validates each tile's `FrameLowFreq` and `BinBandwidth` against what it asked
for, discarding non-matching tiles rather than trusting them.** The FFT path is
usable as a fallback but requires the watcher to carry its own generation
counter — the pattern `PanAdapterManager` already uses (`:289-292`, `:378-385`).

Two riders. First, `WaterfallTile.AutoBlackLevel` (`WaterfallTile.cs:83`) is the
radio's own noise-floor estimate delivered alongside the data, which would be a
gift for a detector — but whether it is populated when `auto_black` is disabled
is UNKNOWN (§11 item 8). Second, a panafall gives us **both** streams and I found
no way in the vendor library to disable one, so a watch source pays for both
unless `FPS = 0` or `line_duration = 0` turns one off — also UNKNOWN (§11
item 6). If neither can be silenced, the watch source's cost is roughly double
the single-stream figures in §4.1, which at 256 bins is still about 2 kB/s and
still negligible.

---

## 6. Does watching require a slice?

**No, for the spectrum-derived watcher. VERIFIED.**

A panadapter is created by `display panafall create` (`Radio.cs:5489`) and
delivers data through its own stream. Nothing in `Panadapter` requires a slice:
`Panadapter.CheckReady` explicitly handles the case of a panadapter with no
associated waterfall and, separately, iterates slices only to notify any that
happen to reference this panadapter's stream ID (`Panadapter.cs:1351-1387`).
Slices reference panadapters, not the reverse.

**The FLEX-6300 arithmetic, which is the case that decides the default.** Two
slices total (`ModelInfo.SliceList = {"A","B"}`, `ModelInfo.cs:109`). A
slice-based watcher takes one of those two, halving the receiver count — exactly
the objection in the brief, and it is real. A spectrum-derived watcher takes
none. That is the whole argument for making spectrum the default.

**But one caveat I could not close.** `FlexBase.NewSlice()` calls only
`RequestPanafall()` and then waits for a slice to appear *and* for panadapter
count to equal slice count (`FlexBase.cs:10835-10842`). That strongly suggests
the radio auto-creates a slice when a GUI client creates a panafall. If true, a
watch panadapter would cost a slice as a side effect, which would demolish the
FLEX-6300 case entirely.

**INFERRED (low confidence, and the stakes are high): this is more likely a
SmartSDR-style persistence or GUI-client behaviour than a hard property of
`display panafall create`.** `tools/rigbench/daxiq_probe.py` creates a panafall
and works with the resulting panadapter without any mention of a slice appearing
(`:249-256`). But the probe does not assert the absence of one either.

**This is §11 item 4 and it should be settled first, before anything is built,
because it changes whether the feature is viable on a 2-slice radio at all.**
If a panafall does drag a slice with it, the fallbacks are: send a raw
`display pan create` via the public `SendReplyCommand` (`Radio.cs:4798`) and see
whether that behaves differently; or accept the slice cost and make the FLEX-6300
a slice-backed-watcher platform, where at least the operator gets audio for the
slice they spent.

---

## 7. Detection logic

### 7.1 What the app has today — nothing reusable

**VERIFIED.** The existing scan (`scan.vb:266-303`, `MemoryScan.vb`) is a timer
that steps `RigControl.Frequency` by an increment and wraps at the end
(`ScanTimer_Tick`, `scan.vb:297-303`). There is **no detection of any kind** — no
squelch stop, no level test, no pause-on-signal — and **no memory of where the
operator was** before the scan started. `StartLinearScan` overwrites the VFO
immediately (`scan.vb:294`).

Priority watch is not an extension of this. It is a different feature, and the
existing scan is chiefly useful as a catalogue of the hazards to avoid: it moves
the radio without recording a return point, and it cannot tell you why it
stopped.

### 7.2 What real scanners do

From long-established scanner practice — this paragraph is **general knowledge,
not code-verified**, and the numbers below are starting points to be tuned, not
findings:

- **Squelch with hysteresis.** The level that opens the squelch sits above the
  level that closes it, typically by a couple of dB, so a signal sitting exactly
  at threshold does not chatter.
- **Priority sampling interval.** A priority channel is checked at a fixed
  cadence — on consumer scanners, commonly about every two seconds — by briefly
  interrupting whatever is being monitored.
- **Scan delay / hang time.** After a signal drops the scanner holds for a
  couple of seconds before resuming, so the reply in a conversation is not
  missed.
- **Lockout.** Frequencies that produce constant false stops (birdies, pagers,
  data carriers) can be excluded.

All four map cleanly onto this feature, and the operator already knows all four
by name. Use their names.

### 7.3 The detection statistic

**Do not threshold on the raw bin value. Threshold on the excess over a tracked
noise floor, in dB.** The reasons are in §5.3: gain, antenna, noise blanker and
band conditions all move the absolute level, and possibly so does the dBm
window.

Concretely, per frame:

1. Take the bins covering the watch bandwidth (mode-dependent: about 2.7 kHz for
   SSB, about 500 Hz for CW, about 12 kHz for FM).
2. The watch statistic is the **peak** of those bins. Peak rather than mean,
   because a narrow signal inside a wide watch band is diluted by averaging.
3. The noise floor is a **low percentile** — the 10th — of the bins in the rest
   of the source's span, excluding every watch band and a guard interval around
   each. Percentile rather than mean, so a strong neighbour does not drag the
   floor up.
4. The reported value is `peak − floor`, in whatever the value unit turns out to
   be (§5.3), converted to dB once §11 item 1 settles the scale.
5. Update the floor estimate only while the watch is **idle**, so a long
   transmission cannot slowly raise the floor and mute the detector against
   itself.

### 7.4 The state machine and starting values

Four states: **Idle → Rising → Active → Falling → Idle**.

Starting values, offered as defaults to be tuned rather than as answers:

- **Sample cadence: 2 Hz.** Fast enough to catch a call-up, slow enough that the
  watch source costs almost nothing. A scanner's two-second priority interval is
  the floor of what is acceptable; twice a second is better and cheaper here
  because we are not interrupting anything to do it.
- **Open threshold: 6 dB above the tracked floor.** Below about 4 dB, ordinary
  noise excursions trip it; above about 10 dB, a weak but perfectly readable
  station is missed, which for a maritime net is the whole point.
- **Close threshold: 3 dB.** Three dB of hysteresis. This is the single most
  important parameter for not being irritating.
- **Attack dwell: 3 consecutive samples above the open threshold, about 1.5 s.**
  A static crash is one frame. A key click is one frame. A station saying a
  callsign is seconds. This is the parameter that separates a good watcher from
  one that cries wolf.
- **Release dwell (hang): 6 consecutive samples below the close threshold, about
  3 s.** SSB syllable gaps run 0.3 to 0.8 s and inter-over gaps a second or two;
  3 s rides over both so a single conversation produces one alert, not thirty.
- **Re-alert suppression: 30 s after an Active → Idle transition.** A net that
  runs for an hour should ping once when it starts, not once per over.
- **Floor tracker: 10th percentile over a 60 s sliding window,** updated only in
  Idle.

Every one of these is per-watch and persisted, because a maritime net and a
2 metre calling frequency want different answers.

### 7.5 The false-alarm defence that falls out for free

**Wideband-event rejection.** Because `DataReady` hands us the *whole* frame
(§1.3), the detector can compare the watch band's rise against the median rise
across the rest of the span in the same frame. A lightning crash, an AGC pump, a
preamp or antenna change, or the operator's own transmitter all raise everything
at once; a station on the watch frequency raises one narrow region.

**Require a differential — the watch band must rise at least, say, 4 dB more
than the span median — and the great majority of false alerts disappear.** This
costs one extra pass over an array we already have, and it is only possible
because the spectrum source hands over the neighbourhood rather than a single
number. It is the strongest single argument for the spectrum-derived watcher
over a slice S-meter, which sees only its own passband and cannot tell a static
crash from a caller.

**TX blanking.** Suspend detection while `FlexBase.Transmit` is true
(`FlexBase.cs:7208`) and for about a second afterwards. On a half-duplex 1-SCU
radio like Don's 6300 there is no receive path during transmit anyway; on a
full-duplex 2-SCU radio the operator's own signal will be all over the display.
Either way the samples are worthless and must not reach the floor tracker.

**Lockout.** Persist a per-watch "ignore" so a birdie or a permanent data
carrier can be excluded without deleting the watch.

### 7.6 What to tune against

**Not live guesses at the radio.** Radio time is the perishable resource
(`memory/feedback_batch_findings_then_fan_out.md`) and threshold tuning needs
hours of the same signal, replayed.

Capture the raw bin frames for one watch source over a full day on a real net
frequency — a quiet morning, the net itself, an evening pileup, and ideally a
thunderstorm — to a file, and tune the state machine offline against the
recording. The recording is also a regression test: any future change to the
detector can be replayed against it and compared. This is the same instinct
behind `tools/rigbench/` and it is worth building the capture before building
the detector, because the detector without it is guesswork with a radio attached.

A specific caution from `memory/project_daxiq_iq_findings.md`: **the bench 8600
has no antenna connected.** A watch tuned against its floor is tuned against
nothing. Either connect an antenna or do the capture on Don's 6300, which has a
real antenna and a real band.

### 7.7 Speaking the result

Per `docs/planning/active/kerchunk-sidetone-pileup.md`, and it fits this feature
unusually well:

- **Timbre identifies which watch fired.** Each watch references a named voice
  from the same voice library the meters use — a *reference*, not an enum, per
  the standing rule at the top of `barefoot-splatter-ragchew.md`.
- **Pitch carries the value.** For a watch, the value is dB above floor. That
  makes a watch a **frequency-domain meter** — precisely the third meter category
  Track D says the data model must allow for (`barefoot-splatter-ragchew.md`
  §"Three meter categories"). The two features should share one model.
- **Pan enhances, never load-bearing.** Patrick's axis.
- **The open is the alert; the close is quieter and optional.** Two distinct
  earcons, not one played twice.
- **A watch is both an event source and a continuous value.** Opening fires an
  earcon (`JJFlexWpf/EarconPlayer.cs`); riding the level continuously is a meter
  tone (`JJFlexWpf/MeterToneEngine.cs`). Most operators will want the earcon
  only; someone tracking a band opening will want the tone. Support both, default
  to the earcon.
- **Speech on demand always; speech on open optional and off by default.**
  "Maritime net, 14.300, active, 12 dB." Interruptible, and never spoken over an
  incoming signal the operator is straining to hear.
- **Alerts must survive band noise.** A pure sine gets lost in broadband noise —
  `memory/project_earcon_audibility_rf_environment.md`. The watch alert is
  exactly the case that memory was written about, since by construction it fires
  while the operator is listening to a band.

---

## 8. Recommended architecture

Seven pieces, none of them inside `FlexLib_API/`. The vendor `Panadapter` and
`Waterfall` classes are used exactly as shipped — their public setters and their
`DataReady` events are sufficient, and `SendReplyCommand` (`Radio.cs:4798`) is
public for anything not wrapped. Nothing here needs re-applying after a FlexLib
upgrade.

**1. `SpectrumSource`** (new, `Radios/`). Wraps one `Panadapter` plus its
`Waterfall`. Owns `Width`, `FPS`, `CenterFreq`, `Bandwidth`, `LowDbm`,
`HighDbm`, `Average`, `WeightedAverage` — deterministically set, never
inherited. Exposes `Covers(centreHz, widthHz)`, `BinWidthHz`, and an event
delivering a validated frame together with its low frequency, bin width and
timestamp. Discards tiles whose `FrameLowFreq` / `BinBandwidth` do not match
what was asked for (§5.4). Reference-counted.

**2. `SpectrumSourceBroker`** (new). The **only** thing in the app that creates
or closes watch panadapters. Takes coverage requests — centre, required span,
required bin width, required cadence — merges compatible ones onto a shared
source, and decides reuse versus new. Three obligations:

- Publishes `WouldClaimNewPanadapter(request)` **before** anything is committed,
  so the UI can say so. This is the plan doc's explicit requirement: the moment a
  watch frequency falls outside what is already streaming is the moment it starts
  costing a scarce resource, and silently claiming the last panadapter is
  discovered later, in a worse mood.
- Refuses gracefully at the limit, using `Radio.AvailablePanadapters` /
  `Radio.PanadaptersRemaining` (`Radio.cs:15067`, `:5976`), and says why. The vendor library also aggregates
  FFT packet health across all panadapters at `Radio.cs:8952-8959`, which is a
  ready-made health signal for the source set.
- Filters `Radio.PanadapterList` by `ClientHandle` before considering any
  panadapter reusable — see §9.4.

**3. `FrequencyWatch`** (new). Pure data, one per watch: name, centre, mode-derived
bandwidth, thresholds, dwell counts, re-alert suppression, voice reference, QSY
policy, enabled. Persisted per radio serial
(`memory/project_per_radio_config_serial_keyed.md`) — a watch list is
radio-specific, since the frequency that matters on the 8600 in the shack is not
the one that matters on the 6300 at Tony's.

**4. `WatchDetector`** (new). The state machine of §7.4, the floor tracker, the
wideband-differential rejection of §7.5, and TX blanking. Emits `WatchOpened`,
`WatchClosed`, `LevelChanged`. Deliberately has no knowledge of panadapters — it
consumes frames and emits events, so it can be unit-tested against a recorded
capture with no radio present. **That testability is the main reason to draw the
boundary here.**

**5. `WatchAlerter`** (new). Maps detector events to earcons, meter tones and
speech. Serialises alerts with a minimum gap, enforces the concurrency cap of
§9.3, and honours the existing advisory-suppression and speech-triage
conventions.

**6. `WatchQsyController`** (new). Captures the return state, performs the move,
owns the refusals, owns the return action. §9 in full.

**7. UI.** A watch list in the same idiom Track D settles for meters — Space
toggles enabled, Enter opens properties, Delete deletes with a confirm,
Shift+F10 / Applications for the context menu — and rows that speak state, not
just name: *"Maritime net, 14.300, watching, idle, bell."* Plus a `Ctrl+J`
leader sub-layer (`memory/project_ctrl_j_leader_command_layer.md`, noted there as
underused), and a Feature Availability line explaining when watching is
unavailable because no panadapter is spare.

**Why a broker rather than letting each watch own a panadapter.** Three reasons,
each verified above: the "several watchers in one span are free" property only
pays out if something merges them (§3); `Width` and `FPS` are shared mutable
state that must be negotiated rather than fought over (§3, correction 2); and
the accounting the UI must report is a property of the source set, not of any
one watch (§2.1). A broker also means that when the recording or waterfall work
later provides a wide stable span, every existing watch silently becomes free —
because watches ask for coverage, not for a named panadapter.

---

## 9. Auto-QSY

Opt-in and announced, per the plan doc and
`memory/project_no_silent_keystrokes_rule.md`. Three modes, and the default is
the least surprising:

- **Alert only** (default) — the watch says something happened, and does not
  touch the radio.
- **Alert then offer** — a spoken prompt with a key to accept, timing out and
  cancelling itself. The offer must say the frequency it would move to.
- **Auto-move** — explicitly armed per watch, announced *as it happens*, with the
  return key named in the announcement itself: *"Moving to 14.300. Press
  such-and-such to return to 7.185."*

### 9.1 What must be remembered to return

Capture **before** the move, and treat the capture as durable until explicitly
consumed or dropped:

- The RX slice's `Freq` (`Slice.cs:358`), `DemodMode` (`:287`), filter low and
  high, `RXAnt` (`:185`), `AGCMode` (`:1346`), and its audio gain (`:731`).
- `RITOn` / `RITFreq` and `XITOn` / `XITFreq` — all four exist and are already
  mirrored into `FlexBase` (`FlexBase.cs:5967-5999`).
- Which VFO position was RX and which was TX (`FlexBase.RXVFO`,
  `FlexBase._TXVFO` at `:7760`), captured **by slice identity, not by position**
  — the codebase already learned this lesson the hard way and the comments say
  so (`FlexBase.cs:10828-10833`, `PanAdapterManager.cs:417-419`).
- Whether the app had moved the display panadapter, and to where — otherwise the
  braille panadapter comes back on the wrong segment.

### 9.2 The hazards, and the refusals

**Never move the TX slice.** Move the RX slice only.
`FlexBase.RXFrequency`'s setter writes the RX slice (`FlexBase.cs:8007`), and
`ShowingXmitFrequency` / `TXVFO` exist precisely because RX and TX can be
different slices. If the operator is in split — working a DX station split up
2 — moving TX would transmit on the net frequency. That is the worst possible
outcome of this feature and it must be structurally impossible, not merely
avoided by a check.

**Never QSY while transmitting.** `FlexBase.Transmit` (`:7208`). Refuse, say so,
and re-offer when the transmission ends.

**RIT and XIT must be handled explicitly, not ignored.** If RIT is on with a
−600 Hz offset and the radio moves to the net frequency, the operator is
listening 600 Hz off and has no idea why. Two acceptable behaviours: clear RIT
for the destination and restore it on return, announcing both; or refuse the QSY
while RIT is on and say why. Silently carrying the offset across is not
acceptable. I lean to clear-and-restore with an announcement, because refusing
makes the feature useless to anyone who leaves RIT on.

**Do not fight the operator.** If they tune manually after an auto-QSY, the
return point should be dropped — or at minimum demoted and announced — rather
than lurking to yank them somewhere unexpected later.

**Do not QSY during an ATU tune,** and do not QSY into a band the current TX
profile cannot support if the move would leave the operator able to key up there.

### 9.3 Multiple watchers — does it scale, and what breaks first

The architecture scales fine in watcher *count* and poorly in distinct *spans*.
In order of what breaks:

**First — the human ear, well before the radio.** Two watches firing at once is
fine; five is noise. The kerchunk doc is honest that people reliably distinguish
maybe five to seven timbres and a similar number of modulation rates, and that
whether three voices stay distinguishable *under speech* is an open empirical
question. **Cap simultaneously-audible watch alerts at three, serialise the rest
into a queue with a minimum gap, and say "and two more" rather than playing
them.** This is the real limit and it should be designed for from the first
version, not discovered.

**Second — distinct spans.** Watches on different bands cannot share a source, so
each band needs its own panadapter, and `MaxPanadapters` bites. On a FLEX-6300
with the app already running one panadapter per slice, the spare capacity is
small. The broker must report this in operator language: not "no panadapters
available" but *"watching 14.300 needs a spectrum window this radio does not
have free; close a receiver or remove another watch."*

**Third — resolution and cadence negotiation** on a shared source (§3,
correction 2). A CW watch joining an SSB watch multiplies that source's byte
rate.

**Fourth — frame completion on a lossy link** (§4.4), which worsens with span
width, not with watcher count.

**CPU is nowhere on this list.** Twenty watches at 2 Hz over a 256-bin frame is
arithmetic noise.

### 9.4 An integration hazard worth writing down now

**VERIFIED.** `Radio.PanadapterList` contains **every** panadapter the radio
reports, including other MultiFlex clients', because the status parser does not
filter on client handle (`Radio.cs:3358-3392`). Only the app's own layer filters
(`FlexBase.cs:6762`).

A broker that scans `PanadapterList` for a panadapter whose span already covers
a watch frequency will therefore happily select a **foreign client's**
panadapter. Whether the radio even unicasts that panadapter's FFT data to us is
UNKNOWN — most likely it does not — so the watch would sit there receiving
nothing and reporting "idle" forever. That is a silent failure of exactly the
kind this feature must not have.

**Always filter by `ClientHandle` before considering a panadapter reusable.**

### 9.5 Two more integration hazards in current code

**A watch panadapter would break `NewSlice`'s completion test.** `NewSlice`
waits for `mySliceAdded && (MyNumPanadapters == MyNumSlices)`
(`FlexBase.cs:10838-10842`), and `MyNumPanadapters` counts every panadapter
belonging to this client (`FlexBase.cs:6782-6791`, populated at `:6771-6776`). A
watch panadapter makes that equality permanently false, so adding a slice would
time out and log "counts don't match" (`:10855`) on every attempt. **The watch
source must be excluded from `myPanAdapters`, or that invariant must be
rewritten to compare only slice-bound panadapters.** This is the kind of
cross-feature collision `git merge` cannot see —
`barefoot-splatter-ragchew.md`'s own lesson about reusing a symbol.

**`FlexBase.panadapterAdded` would stamp `Width = 5000` on the watch source**
(`FlexBase.cs:6769`), undoing the narrow sizing that §4.4 says is the whole point
on a remote link. Same fix: the watch source must not go through that path.

---

## 10. Open questions I could not resolve from the repository

1. **The meaning and units of the panadapter FFT bin values** (§5.3). The most
   load-bearing gap. Decides whether thresholds can be expressed in dB, and
   whether a watch source must own its dBm window.
2. **Whether the waterfall tile scale is the same as the FFT scale.** The two
   paths have different plausible scalings and nothing states either.
3. **Whether `display panafall create` also creates a slice** (§6). Decides
   whether the feature is viable at all on a 2-slice FLEX-6300.
4. **Whether a slice can be tuned outside its parent panadapter's span,** and
   what the radio does about it. Relevant to the slice-backed watcher sharing an
   existing panadapter.
5. **`MinBandwidth` per model** — read-only from the radio (`Panadapter.cs:374`),
   so the narrowest usable watch span is a runtime fact.
6. **The maximum legal `xpixels`** and whether `FPS = 0` is legal. A poll-only
   panadapter would be the ideal watch source; a hard minimum FPS sets a floor
   on cost.
7. **Whether the FFT and waterfall streams can be enabled independently.** If
   not, every watch source pays for both.
8. **Whether `WaterfallTile.AutoBlackLevel` is populated when `auto_black` is
   off.** If it is, the radio hands us a noise-floor estimate for free and §7.3
   gets simpler.
9. **The radio's default `FPS` on a freshly created panadapter,** which decides
   the size of the suspected bandwidth leak in §1.5.
10. **Whether MultiFlex lets a non-owning client receive another client's
    panadapter data.** Almost certainly no; §9.4 assumes no.

---

## 11. What needs a radio present to settle

A bench sitting, in this order. Items 1-4 gate the design; the rest gate tuning
and sizing. The bench 8600 has **no antenna connected**
(`memory/project_daxiq_iq_findings.md`), so anything requiring a real signal must
either get one connected or run on Don's 6300.

**1. The bin scale — `ypixels` test.** Create a panadapter with known
`LowDbm`, `HighDbm` and `Height`. Log raw bin values. Change `Height` from 700
to 100 and log again on an unchanged signal. If the values rescale
proportionally, the bins are pixel-scaled and dBm is derivable from
`LowDbm`/`HighDbm`/`Height`. If they do not, they are absolute. Settles §5.3 and
open question 1.

**2. The dBm window test.** Same setup, change `LowDbm`/`HighDbm` and leave
`Height` alone. Whether the values move determines whether a watch source may
share a panadapter with anything that adjusts the display window. Settles the
pessimistic assumption baked into §8.

**3. Absolute calibration.** With a signal present, compare bin values at a known
frequency against `Slice.SMeterDataReady` for a slice on the same frequency
(`Slice.cs:3332`, dBm from the radio's own calibration). Gives the constant that
lets a spectrum watch report an S-meter reading rather than only a relative dB.
Needs an antenna or a signal generator.

**4. Does a panafall drag a slice?** On an idle radio, note the slice count, send
`display panafall create`, wait, note it again. Then repeat with a raw
`display pan create` via `SendReplyCommand`. Settles §6 and open question 3, and
this one should be done **first**, because a "yes" changes the whole shape of the
feature on 2-slice radios.

**5. Default `FPS` and the achievable minimum.** Read `Panadapter.FPS` on a fresh
panadapter before anything sets it. Then try 1, and try 0. Settles open
questions 6 and 9, and sizes the suspected leak in §1.5.

**6. Stream independence.** With a panafall open, watch `Radio.AvgFFTkbps`
(`Radio.cs:1152`) and `Radio.AvgWaterfallkbps` (`:2798`) while setting `FPS = 0`
and separately `line_duration = 0`. Determines whether a watch source pays for
one stream or two. Open question 7.

**7. Frame completion versus width, on Don's link.** This is the remote test and
it is the one that most changes the design if it comes out badly. Open a
panadapter at `Width = 5000` and count `FFTPacketErrorCount` versus
`FFTPacketTotalCount` (`Panadapter.cs:715-742`, both already exposed and already
aggregated across all panadapters at `Radio.cs:8952-8959`) over several minutes. Repeat at 2040 and
at 256. Predicted: completion improves markedly as the frame drops below one
MTU. Also worth running on the LAN 8600 as a control, so a bad remote number can
be attributed to the link rather than the code.

**8. `AutoBlackLevel` with `auto_black` off.** Log `WaterfallTile.AutoBlackLevel`
(`WaterfallTile.cs:83`) with `AutoBlackLevelEnable` false. Open question 8.

**9. Resource counts on both radios.** Read `MaxPanadapters`,
`AvailablePanadapters`, `MaxSlices`, `AvailableSlices` and `PanadaptersRemaining`
on the 8600 and on Don's 6300, with and without a second slice open. Gives the
real headroom figures the UI has to report, and there is no static table to fall
back on (§2.1).

**10. `MinBandwidth`.** Read it on both radios. Sets the narrowest watch span,
which §5.2 wants at around 50 kHz.

**11. The tuning capture.** Once 1-3 are settled, record raw frames from one
50 kHz watch source over a full day on a real net frequency, including an
evening storm if one obliges. Tune §7.4's thresholds offline against it and keep
it as the detector's regression fixture. This is the item that most benefits
from being started early, since it takes calendar time rather than bench time.

---

## 12. Summary of what this changes about the plan as written

The plan doc's "Priority watch" section is right in its central claim and right
about the three scanner lessons. Five things it should absorb:

- **DAX IQ should come off the options list entirely.** It costs a panadapter
  *and* an IQ channel, and 1.5 Mbit/s at its cheapest. Verified in §2.3 and §4.2.
- **A third source exists and is the right answer for part of the feature:** a
  slice-backed watcher, which costs a slice but gives calibrated dBm and audio.
  Spectrum by default, slice as an announced upgrade. §4.3.
- **"An existing span" is not stable today.** The only managed panadapter follows
  the operator's VFO, so a borrowed span silently stops watching on QSY. A watch
  must own a span until some other feature provides a stable one. §3,
  correction 3.
- **The waterfall tile is a better source than the FFT frame,** because it is
  self-describing in frequency and the FFT frame is not. §5.4.
- **Narrow the watch source until a frame fits one datagram.** All-or-nothing
  frame assembly means a wide panadapter on a lossy link delivers nothing rather
  than something degraded — which for a remote-only tester is the difference
  between the feature working and appearing to work. §4.4.

And one thing to check that is not about this feature at all: the app sets
`Width = 5000` on every panadapter it owns and only narrows the active one, so
there may be a standing bandwidth cost for data with no subscribers. §1.5, bench
item 5.
