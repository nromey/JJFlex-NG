# Task brief: make the DAXIQ probe capture real receiver IQ

**Status: RESOLVED 2026-08-09 (same-day, follow-up session).** The binding was
never broken — the "synthetic test pattern" was real receiver noise misread
(see Findings at the bottom). The probe is rewritten, validated against the
live 8600, and self-owning: it needs nobody. Stage 1 (keying session with
Noel) is GO, with a mandatory pre-flight signal check added to the protocol.

Original brief follows unchanged, for the record.

**Original status:** specified, not started. Queued 2026-08-09 from the
audio-workshop bench session. Suggested model: Fable. Surface:
`tools/rigbench/` plus read-only FlexLib reference. **Needs LAN access to the
8600 at 192.168.50.100.** Does not need the operator, and must never transmit.

## Why this matters

It decides whether **every** Flex can get in-radio audio-check fidelity, or
only full-duplex-capable ones. As of 2026-08-09 the answer is "only full duplex"
(see `audio-workshop-plan.md` §4d), which leaves Don's 6300 and every other
1-SCU radio without an in-radio path. If the receiver's IQ keeps flowing during
transmit even while the audio path is muted, JJFlex can demodulate PC-side and
close that gap. Nobody knows yet, because the instrument does not work.

## The success criterion, and why it needs nobody

**Right now the DAXIQ payload is a repeating synthetic test pattern:**
`-64.0, +64.0, -16.0, +16.0, …` forever. Real receiver IQ — even with no antenna
connected — is a noise floor, which looks nothing like that.

So the whole binding task has an objective, self-checkable finish line: **the
payload stops repeating and starts looking like noise.** No operator, no
keying, no transmitting. Iterate against the live radio and read your own bytes.

Noel's framing, and it is the right order: *"don't you want to make sure that we
can at least receive static and ensure that we're receiving static? Then we can
start keying junk."*

## What already works — do not re-derive this

Verified against a real captured packet, byte by byte:

- LAN VITA data arrives over **UDP 4991** (`Radio.cs:739`, `:15339`).
- Tell the radio our port over TCP: `client udpport <port>`.
- Register over **UDP**, repeated as a keepalive:
  `client udp_register handle=0x<HANDLE>` (`Radio.cs:15317`).
- `stream create type=dax_iq daxiq_channel=1` (`Radio.cs:5512`) returns the
  stream id.
- `display pan set 0x<pan> daxiq_channel=1` (`Panadapter.cs:259`).
- `stream set 0x<id> daxiq_rate=48000`.
- Packets arrive at ~273/sec: type 1 (IFDataWithStream), our stream id, OUI
  `1C2D`, **packet class `0x02E4`** (48 kHz wide IQ), header/class/TSI/TSF
  giving a payload offset of **28**, payload **little-endian float32**. The
  parser in `daxiq_probe.py` is correct — confirmed against a hex dump.
- **A Windows Firewall rule is required.** Inbound UDP to `python.exe` was
  silently dropped, including the radio's own discovery broadcasts. Diagnose by
  binding UDP 4992 and checking whether discovery packets arrive; that is a free
  known-good signal. Noel allowed it via the Windows Security prompt on
  2026-08-09, so it should already be in place on this machine.

## What was ruled out

- **Wrong panadapter.** `0x40000001` is correct: it reports `rxant=XVTB` and
  slice 1 reports `pan=0x40000001`.
- **Binding not applied.** `daxiq_channel=1` is present in the pan's status.
- **Pan centered elsewhere.** The pan was at 146.000 MHz with the signal at
  144.100 — nearly 2 MHz outside a 48 kHz window. Recentering to 144.100 and
  narrowing the bandwidth to 0.048 changed nothing; still the test pattern.
- **Parser error.** Ruled out by the hex dump above.

## Hypotheses, strongest first

1. **We are binding to a panadapter we do not own.** The pan belongs to
   JJFlex's client handle; our raw client created the dax_iq stream under its
   own handle and bound it to someone else's pan. The radio may decline to feed
   another client's pan data and emit a placeholder instead. **Test: create our
   own panadapter under our client** (`display pan create`), tune it to the
   slice frequency, bind DAXIQ to that. This is the most likely explanation and
   should be tried first.
2. **A separate enable flag exists.** `Waterfall.cs:1123` carries a `daxiq`
   status case distinct from `daxiq_channel`. There may be an equivalent set
   command (`display pan set 0x.. daxiq=1`, or on the stream). Grep the
   decompiled SmartSDR at `C:\dev\smartsdr-decompiled-4.1.x\SmartSDR.decompiled.cs`
   for how its own DAX IQ panel sequences these commands — that decompile
   answered the transverter-power question the same way.
3. **Client identity.** Real DAX applications may register with a program name
   or as a GUI client before streams are honoured. Check what FlexLib's own
   DAX path sends at connect that we skip.

## Then, and only then

**Stage 1 — needs one keying session from Noel, no demodulator.** With full
duplex OFF, does IQ energy respond to keying? That single answer decides the
product question. `daxiq_probe.py` already prints per-second energy. **Run the
full-duplex-ON control in the same session** — an instrument that cannot see a
signal we know is present tells you nothing, which is exactly how two runs on
2026-08-09 nearly got recorded as a false negative.

**Stage 2 — only if stage 1 is positive.** numpy SSB demodulation of the
captured IQ to a listenable WAV, as proof-of-concept for a C# implementation.
Do not build this first; if stage 1 is negative it is wasted work.

## Standing constraint

Everything in `tools/rigbench/` refuses to transmit by construction, and that
invariant is deliberate — keying stays with the operator's hand mic. Preserve
it in anything added here.

## Findings — 2026-08-09 instrument session

The success criterion ("payload stops repeating and starts looking like
noise") was met — by discovering it had been met all along.

### The premise was wrong: the payload was never a synthetic pattern

The DAX IQ payload is coarsely quantized: every sample is a multiple of 16.0
against a full scale of 32768 (FlexLib divides by 32768 —
`VitaIFDataPacket.cs`, `ONE_OVER_ZERO_DBFS`). At a quiet noise floor the
sample alphabet is ~25 values, so an eyeballed hex dump reads as "-64, +64,
-16, +16, repeating." It is not repeating. Three independent proofs, all
gathered with no operator and no transmission:

- **Autocorrelation.** Best period-correlation over lags 2..512 is r = 0.16
  to 0.18. A genuine repeating pattern scores above 0.95.
- **Preamp tracking.** Mean level follows rfgain (23.6 at rfgain=0 rising to
  47.8 at rfgain=32). A synthetic generator cannot know the preamp setting.
- **Frequency-dependent floor.** The broadband floor at 14.075 MHz sits ~6 dB
  above the floor at 14.990 MHz — atmospheric band noise behaves this way; a
  placeholder would not.

Corroborating detail from the original logs (`iq-run.txt`, `iq-fdx-on.txt`):
per-second means vary (48.20-48.72) and peaks vary (224-288). A deterministic
pattern at a fixed packet rate would give identical statistics every second.

### The hypotheses, resolved

1. **Pan ownership (H1): not the issue.** A two-client experiment (GUI client
   A owning the pan, plain client B creating the stream, exactly like the
   original probe) delivered real noise to B *without* any bind. The radio
   auto-associates an unbound client's dax_iq stream with the resident GUI
   client — the stream status shows `client_gui_handle=` filled in unasked.
2. **Separate enable flag (H2): does not exist.** The `daxiq` key in
   `Waterfall.cs:1123` is an ignored status key, not a settable flag.
3. **Client identity (H3): not required on the LAN path.** `client bind
   client_id=<guid>` works and is harmless, but unbound delivery was proven.

### What the radio self-reports (trust this, not eyeballs)

`sub daxiq all` + creating the stream yields a status line that answers every
question the original session guessed at:

    stream 0x20000000 type=dax_iq daxiq_channel=1 pan=0x40000000 slice=0x0
      endpoint_type=Display daxiq_rate=48000 client_handle=0x.. 
      client_gui_handle=0x.. active=1 ip=.. payload_endian=little

`active=1` means the radio believes it is feeding real pan data.
`payload_endian=little` confirms the parse. The rewritten probe prints this
line on every run.

### Why the original keying run saw nothing (the real stage-1 blocker)

Nothing was wrong with the IQ path. The keyed 2m signal never made it into
the DAXIQ window: no port on the bench 8600 currently hears any off-air
signal. A six-port sweep (ANT1, ANT2, RX_A, RX_B, XVTA, XVTB) at 14.075 MHz
showed identical featureless floors and no carriers — no WWV on 15 MHz, no
FT8 stripe on 14.074. **No antenna (or live transverter RX path) is connected
to the bench radio.** The 144.1 MHz transverter attempt also had the pan's
`xvtr=` field empty, so the transverter mapping was likely never engaged.

### Stage 1 protocol, refined (needs Noel, one session)

1. Get any real signal into the receiver: an antenna on ANT1/ANT2, or the 2m
   transverter RX path confirmed working with an XVTR profile selected.
2. Run `python tools\rigbench\daxiq_probe.py --freq <MHz> --seconds 120` and
   watch the "top spectral peak" column. **Do not key until a stable carrier
   shows at a fixed offset, well above ~15 dB.** Pure noise shows a
   random-walking peak at 11-13 dB (order statistics of 4096 FFT bins) — that
   means NOT VISIBLE, and keying would only produce another false negative.
3. With the carrier confirmed: full-duplex OFF, key mid-capture, watch
   whether the carrier and floor survive. Then repeat with full-duplex ON as
   the control, same session. (Radio currently has `full_duplex_enabled=1` —
   remember to set it per run.)
4. Only if stage 1 is positive: stage 2, numpy SSB demod of `--record` output
   to a listenable WAV. The probe's `--record file.iq` writes raw interleaved
   float32 in radio units for exactly this.

### Instrument changes (rewritten `daxiq_probe.py`)

- Connects as its **own GUI client** (`client gui`, program/station
  "RigBench") and uses a pan it owns — persistence hands one over on connect;
  otherwise it creates a panafall. Works on an idle radio, needs nobody.
  Falls back to plain-client + resident pan if MultiFlex seats are full.
- Prints the radio's own stream status (`active=`, `pan=`, endianness) as a
  self-check, and warns if `active=0`.
- Reports levels in dBFS (FlexLib-conformant 32768 full scale).
- Per-second top spectral peak (pure-Python 4096-point FFT, no numpy) — the
  pre-flight visibility check for keying sessions.
- End-of-run verdict: autocorrelation repeat detector + alphabet size +
  quantization step, so a hex-dump misread cannot happen again.
- `--freq/--rxant/--rfgain/--rate/--record/--host/--seconds` flags; still
  never transmits, by construction.
