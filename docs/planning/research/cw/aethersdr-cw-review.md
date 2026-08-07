# AetherSDR CW Implementation Review

Research memo for the JJFlexRadio CW pipeline rewrite (keyboard iambic/straight/bug keying, remote keying, receive decode).

Source reviewed: `C:\dev\AetherSDR` at commit `3a1f59ea` (upstream 2026-08-05), pulled fresh 2026-08-07 15:57 (fast-forward `3de702dc` → `3a1f59ea`). The pull range contains **no new CW keying or decode work** — the only CW-adjacent change is mic-source narrowing in `PhoneCwApplet` (phone side). Everything below reflects the fresh tree.

## What AetherSDR is, and the license line we must not cross

AetherSDR is a cross-platform (Linux/macOS/Windows) FlexRadio client in C++20/Qt6. It does **not** use FlexLib — it speaks the SmartSDR wire protocol natively, and its comments repeatedly cite FlexLib source lines (`CWX.cs:54-83`, `Radio.cs:8890-8965`, `NetCWStream.AddTXData`) as protocol documentation. That matters twice over for us:

- **License: GPL-3.0.** JJFlexRadio bundles proprietary vendor FlexLib and is not GPL-compatible. We may borrow *architecture, protocol facts, and independently re-implemented ideas*; we may **not** copy code, and per our no-vendor-derivative-commits rule nothing derived from their files goes into our repo verbatim.
- **The exceptions are their bundled third-party libraries with permissive licenses.** The CW decoder is **ggmorse (MIT, Georgi Gerganov, github.com/ggerganov/ggmorse)** — directly usable by us. Their MIDI layer is rtmidi (MIT), not that we need it.
- Best of all: everything they reverse-implemented on the wire, **we already have first-party in C#**. Our bundled FlexLib 4.1.5 contains `FlexLib_API/FlexLib/NetCWStream.cs` and the CW key/PTT methods in `Radio.cs`. AetherSDR is best read as a field-tested *annotation layer* over the FlexLib code we already ship.

## The core architectural idea: two keyers, one truth

This is the single most borrowable pattern in the codebase. The radio's DSP produces the on-air RF from raw key edges; the client runs an **identical, parallel keyer locally whose only job is sidetone**.

- The radio's keyed-audio round trip is 30–100 ms locally and 50–200 ms with jitter over the network — unusable for paddle work at speed. So the client gates a locally generated sine sidetone within one audio callback (~5–10 ms) of the key event.
- The local iambic engine and the radio run at the same WPM, so their element timing stays phase-aligned within about one element. Their comments are explicit that if they drift it does not matter: "sidetone is informational only — the radio produces the actual on-air CW."
- The same pattern is reused for typed CW: a `CwxLocalKeyer` converts the sent text into a dit/dah element schedule locally and gates the same sidetone, so the operator *hears* what the radio's CWX queue is keying. For a screen-reader-first client this is exactly the audible-confirmation channel we want.

## Keying: how paddle and straight key actually work

Protocol facts (all confirmed against radios in their comments):

- The wire accepts only a single key state — `cw key 1` / `cw key 0`. There is **no two-argument dit/dah paddle form**; FlexLib likewise only ever sends single edges. **Iambic timing is the client's job.** The client-side keyer emits per-element key edges and the radio just follows them.
- Straight key is the same command, driven directly by the input edge.
- Break-in semantics: with `cw break_in 1` (QSK) a `cw key 1` itself triggers TX and `break_in_delay` holds the relay between elements. With break-in off, the operator asserts PTT explicitly via `cw ptt 1` / `cw ptt 0` (their "Space PTT" idiom, MOX, or hardware PTT). Their iambic keyer deliberately **never auto-PTTs** — doing so would override break-in-off and force-drop the QSK hang on release. Worth adopting as a rule.

Their `IambicKeyer` (src/core/IambicKeyer.h/.cpp) is a clean reference state machine:

- Modes A and B implemented; mode B via "memory bits" latched when the opposite paddle is squeezed mid-element. Header notes Ultimatic / Bug / Straight *keyer emulation* modes are deferred to a later phase — so full-iambic JJFlex would actually leapfrog them on bug mode.
- Paddle swap, WPM clamped 5–60, unit time = 1200 / WPM ms.
- Two lock-free callbacks: key-down change (gates the sidetone atomically from the worker thread) and paddle event (forwarded to the radio thread via queued invocation).

### Timing discipline — the hard-won lesson

Both of their keyers run on a **dedicated worker thread using absolute steady-clock deadlines** (`sleep_until` / interruptible condition-variable waits), drift-corrected against a run epoch so a late wake is followed by a shorter wait. They abandoned Qt event-loop timers after shipping the bug: under panadapter paint plus VITA-49 burst load, the GUI loop coalesced timer firings and audibly clipped CW elements (their issue #3623; "QTimer's jitter is too high for CW"). Two separate keyers independently converged on this design.

Direct translation for us: never time CW elements with `System.Windows.Forms.Timer` or `DispatcherTimer`. Use a dedicated thread, `Stopwatch`-based absolute deadlines, and a high-resolution waitable timer (or `timeBeginPeriod` awareness) on Windows. Element edges then go out via FlexLib's netCW path, and the sidetone gate is flipped through a lock-free atomic read by the audio callback.

## Transport: the NetCW stream (low-latency and remote keying)

This is the part most relevant to our remote-keying goal, and our FlexLib already implements it (`NetCWStream.cs`) — AetherSDR documents *how it behaves*:

- On connect the client issues `stream create netcw` and records the returned stream id. If the radio refuses (older firmware), every key command falls back to TCP as `cw key immediate 0/1`.
- With the stream up, each key edge is sent over **UDP** as a VITA-49 ExtDataWithStream packet (FlexRadio OUI 0x1C2D, class 0x534C.03E3) whose payload is the ASCII command decorated with timing metadata: `cw key 1 time=0x<hex> index=<N> client_handle=0x<H>`.
- `time` is a **16-bit relative millisecond clock**, not an epoch — reset after a 3 s idle gap, with 0x0000 accepted by the radio as a resync marker. `index` is a monotonically increasing dedup counter.
- Each edge is transmitted as **four redundant UDP copies at 0/5/10/15 ms**, plus the same decorated command once over TCP as a reliable backstop. The radio dedupes on the ASCII `index=N`. Subtle trap they hit: each UDP copy must carry a **unique VITA packet_count** — identical counts get discarded as duplicates by the radio's VITA layer before the dedup logic ever sees them (FlexLib increments packet_count per send, which is why FlexLib works).
- Hex fields are formatted uppercase; they observed the radio's `client_handle` parsing to be case-sensitive.

The redundancy-plus-TCP-backstop design is precisely what makes remote (WAN) keying tolerable: a lost UDP edge is covered within 15 ms, and TCP guarantees eventual consistency so a key-up can never be permanently lost (stuck carrier). Since our FlexLib does this for us, our job is mainly to *verify* our FlexLib version behaves this way and route all key edges through it rather than plain TCP commands.

## Input sources — everything funnels to three momentary actions

All physical inputs converge on three app-level momentary states — straight key, left paddle, right paddle — and from there into the local keyer (when running) or a straight-key fallback:

- **Keyboard.** Registered as bindable shortcut actions with **null handlers**, processed instead by an application-level event filter so both KeyPress and KeyRelease edges are seen; auto-repeat events are discarded. Standout detail: **modifier-tolerant release**. If a combo binding (say Ctrl+.) is released modifier-first, the release event's sequence no longer matches the binding and a naive implementation leaves TX keyed. Their filter matches the base key of any *active* CW momentary action on release and releases it — "fail safe to RX." Any keyboard keying we build must replicate this, plus their blanket cancel path (TX-cancel clears all key/paddle state, resets the keyer, silences the sidetone, and sends key-up and PTT-off).
- **Keying actions are marked as TX-capable at registration** (`keysTx` flag; their `TxKeyingMarker` property is the widget-level equivalent) so the automation bridge refuses to invoke them without an explicit allow-TX environment gate. Cheap, worthwhile safety pattern for anything scriptable.
- **MIDI.** rtmidi bindings for cwkey/cwdit/cwdah (value > 0.5 = pressed) feed the same three setters. A cheap USB MIDI footswitch or keyer becomes a paddle.
- **Serial control lines.** CTS/DSR/DCD are user-assignable as straight key, dit, dah, or PTT inputs with per-line polarity and debounce; a physical paddle wired to a USB-serial adapter's control pins keys the PC directly. On Windows they run a dedicated pin-watcher thread. This is a low-cost, high-value input path for hams with real paddles and remote radios.

Every input event carries a **trace id and source timestamp** threaded through the whole chain (input → keyer → netcw send, with per-hop latency logging under a dedicated `aether.cw` log category). When a tester says "my keying stutters," this is how they diagnose it. Strongly recommend building the same instrumentation into our pipeline from day one.

## Sidetone generation

`CwSidetoneGenerator` (src/core/CwSidetoneGenerator.h/.cpp):

- Pure sine with a **raised-cosine amplitude ramp** (configurable 0–50 ms, default 5 ms) through an Idle → RampUp → Sustain → RampDown state machine — no clicks, proper CW envelope.
- All parameters (`enabled`, pitch 100–4000 Hz, volume, stereo pan, shaping) are atomics so the UI thread adjusts them without locking the audio thread; the key gate itself is a lock-free atomic flipped straight from the keyer worker.
- Mixed additively into the RX output stream inside the audio callback, **or** routed to a dedicated low-latency sink: they abstract a `CwSidetoneSinkBackend` with a Qt Multimedia implementation (cross-platform but ~50 ms OS-mixer tax) and a **PortAudio direct-callback implementation at sub-5 ms**. We already ship JJPortaudio — the low-latency path is naturally ours.
- The generator **follows the radio's own settings in lockstep** — sidetone on/off, `cw pitch`, monitor gain (`transmit set mon_gain_cw=`), monitor pan (`mon_pan_cw=`) — with the radio authoritative. One set of knobs, two renderers.

## Typed CW (their CWX layer) — hazards our legacy code likely shares

Wire vocabulary: `cwx send "text" <block>` with spaces DEL-encoded as 0x7F, `cwx macro save/send N` (12 macros), `cwx erase N`, `cwx clear`, `cwx wpm`, `cwx delay`, `cwx qsk_enabled`. Per-word speed modifiers (`+`/`-` prefixes) are expanded client-side into interleaved `cwx wpm` and `cwx send` segments, restoring base WPM afterward — including swallowing the radio's echo of each transient wpm change so the UI speed control doesn't flicker.

Two hard-won findings worth stealing outright:

- **The `cwx queue=` status the protocol implies is never actually sent by firmware** (observed on FLEX-6500 fw 4.2.20). Their replacement: the reply to `cwx send` carries `radio_index`, which is the **insertion-start (first-char) queue position** of the batch, so the batch's last character sits at `radio_index + nChars - 1`; they watch `cwx sent=` climb to that value to detect queue drain. An epoch counter invalidates stale replies after ESC/clear/disconnect.
- **Stuck-TX hazard:** with `sync_cwx=1`, after the CWX buffer drains the radio still requires an explicit `xmit 0` from the client — otherwise it holds TX for the full hardware interlock timeout (~60 seconds). Their drain watch releases MOX exactly once per armed batch, using a latch that survives QSK interlock flicker mid-macro. If our inherited typed-CW code keys via CWX, this failure mode is worth auditing for immediately.

UX details that translate well to a screen-reader client: Escape always aborts sending (matches our stuck-modal rule); per-message history with a sent-character counter advanced by `cwx sent=` status (we could speak progress instead of painting bubbles); live mode sends each typed character immediately, routed through the reply path so the drain watch stays armed even in a typing-only session.

## CW decode

Client-side, via **ggmorse** (MIT — usable by us directly; C++ with a small API, feed PCM in, get characters plus estimated pitch/speed out):

- `CwDecoder` wraps ggmorse on a worker thread with a 4-second mono int16 ring buffer at 24 kHz. Auto-estimates pitch and speed, with UI lock buttons and configurable pitch/speed ranges to stop the estimator wandering; a per-character "cost" (confidence) value gates garbage below a threshold.
- **Two decoder instances.** The RX instance is fed from a normalized RX demod audio bus (deliberately bound once to the model layer, not to the per-connection stream object, so reconnects and backend swaps can't silently kill the feed — they shipped that bug and left the lesson in a comment). The feed is gated live on the enable toggle rather than by connect/disconnect churn.
- **TX self-decode is the sleeper accessibility feature.** The sidetone generator has a sample-tap mirror; those samples (the operator's exact keyed envelope) feed a second decoder whose pitch and speed are *forced* to the known configured values (`setKnownParameters`) rather than estimated. Result: the app decodes what the operator actually keyed — paddle, straight key, or CWX — and can display or, in our case, speak it back. For a blind CW operator this is verification that what went on the air is what they meant.
- `CwCallsignSpotter` sits on the decoded text stream: rolling 160-character window, fires only when the callsign after "DE" appears **twice in a row** (a single garbled character correctly suppresses the spot), 120 s per-call re-spot suppression, 3 s settle timer. Simple, robust, and directly reusable as a spoken "station identified" event.

## Radio-level CW settings vocabulary used

For completeness, the radio-level commands they drive (all present in our FlexLib as properties): `cw wpm`, `cw pitch`, `cw break_in`, `cw break_in_delay`, `cw sidetone`, `cw iambic`, `cw mode <0|1>` (iambic A/B), `cw swap`, `cw cwl_enabled`, `transmit set mon_gain_cw= / mon_pan_cw=`, plus `slice auto_tune` for CW autotune.

## Recommendations for the JJFlex rewrite

- **Adopt the dual-keyer architecture wholesale** (as a pattern): local iambic/straight engine on a dedicated thread → lock-free sidetone gate + per-element key edges through FlexLib's netCW path. Read our own `NetCWStream.cs` and `Radio.cs` CW methods first — they are the canonical C# implementation of everything AetherSDR reverse-built.
- **Timing on a dedicated thread with absolute deadlines**; treat their QTimer post-mortem as a standing warning against any UI-thread or event-loop timer in the element scheduler.
- **Fail-safe release rules:** modifier-tolerant key release, a single cancel path that clears everything, no auto-PTT from the keyer, drain-release for typed CW (60 s stuck-TX hazard), and TX-keying markers on any automatable control.
- **Sidetone through JJPortaudio** with raised-cosine shaping and radio-lockstep pitch/gain; keep the sample tap so TX self-decode comes almost free.
- **Use ggmorse for decode** (MIT; P/Invoke wrapper or port) with the two-instance RX + TX-self-decode layout, pitch/speed locks, and the doubled-callsign spotter as a speech event.
- **Instrument from day one:** trace ids and per-hop latency logging across input → keyer → wire, under a dedicated trace category — it is the only way to debug remote keying complaints.
- **License discipline:** patterns and protocol facts only from AetherSDR (GPL-3.0); no code, no derived files in-repo. ggmorse and rtmidi are MIT and fair game.
