"""DAXIQ probe — capture and characterize the receiver's IQ stream.

  python tools\\rigbench\\daxiq_probe.py [--seconds N] [--freq MHz] [--rxant P]
                                         [--rfgain N] [--rate R] [--record F]
                                         [--host IP]

Validated against the 8600 on 2026-08-09 (see daxiq-instrument-task.md for the
full findings). The short version:

  * The probe connects as its OWN GUI client (`client gui`) and uses a pan it
    owns — persistence usually hands us one; otherwise we create a panafall.
    It therefore needs nobody: no JJFlex, no SmartSDR, no operator.
  * If a GUI seat is not available (MultiFlex seats full), it falls back to a
    plain client. The radio auto-associates the stream with the resident GUI
    client, which was proven to deliver real data too.
  * The payload is little-endian float32 at full scale 32768 (FlexLib divides
    by 32768 — VitaIFDataPacket.cs ONE_OVER_ZERO_DBFS). At a quiet noise
    floor the samples are coarsely quantized (LSB = 16.0, ~11 effective
    bits), which makes an eyeballed hex dump look like a synthetic repeating
    pattern. It is not one. The classifier below settles it properly:
    autocorrelation over candidate periods, alphabet size, preamp response.
  * The radio self-reports the stream's health in its status line:
    `stream 0x.. type=dax_iq .. pan=0x.. active=1 payload_endian=little`.
    This probe prints that line — trust it over guesswork.

Per second it prints IQ level in dBFS and the strongest spectral peak in the
48 kHz window. Before any keying experiment, FIRST confirm the signal is
visible here (peak well above the floor) — a keying run without that check
can only produce a false negative.

Plumbing, learned from FlexLib and confirmed on the wire:
  * VITA data arrives over UDP 4991 on the LAN path (Radio.cs:739, :15339)
  * the radio must be told our port      -> TCP  `client udpport <port>`
  * and we must register, over UDP       -> UDP  `client udp_register handle=0x..`
    (Radio.cs:15317; repeated as a keepalive)
  * `stream create type=dax_iq daxiq_channel=N`   (Radio.cs:5512)
  * `display pan set 0x<pan> daxiq_channel=N`     (Panadapter.cs:259)
  * IQ payload is float32 LE, packet class 0x02E3..0x02E6 by rate (VitaFlex.cs)

THIS SCRIPT NEVER TRANSMITS. Keying stays with the operator's hand mic.
"""

import argparse
import cmath
import math
import socket
import struct
import sys
import threading
import time

from flexwire import FlexWire, DEFAULT_RADIO

VITA_PORT = 4991
WIDE_CLASSES = {0x02E3: 24000, 0x02E4: 48000, 0x02E5: 96000, 0x02E6: 192000}
FULL_SCALE = 32768.0            # FlexLib ONE_OVER_ZERO_DBFS is 1/32768
FFT_N = 4096


def parse_vita(data):
    """Return (packet_class, payload_offset, payload_len) or None."""
    if len(data) < 8:
        return None
    word0 = struct.unpack_from(">I", data, 0)[0]
    pkt_type = word0 >> 28
    has_class = bool(word0 & 0x08000000)
    has_trailer = bool(word0 & 0x04000000)
    tsi = (word0 >> 22) & 0x03
    tsf = (word0 >> 20) & 0x03
    packet_size_words = word0 & 0xFFFF

    idx = 4
    if pkt_type in (0x01, 0x03):          # IFDataWithStream / ExtDataWithStream
        idx += 4
    pkt_class = None
    if has_class:
        idx += 4                           # OUI
        pkt_class = struct.unpack_from(">I", data, idx)[0] & 0xFFFF
        idx += 4
    if tsi:
        idx += 4
    if tsf:
        idx += 8

    total = packet_size_words * 4
    payload_len = total - idx - (4 if has_trailer else 0)
    if payload_len <= 0 or idx + payload_len > len(data):
        payload_len = len(data) - idx - (4 if has_trailer else 0)
    if payload_len <= 0:
        return None
    return pkt_class, idx, payload_len


def fft(x):
    """Iterative radix-2 FFT over a list of complex numbers (len power of 2)."""
    n = len(x)
    if n & (n - 1):
        raise ValueError("fft size must be a power of two")
    j = 0
    x = list(x)
    for i in range(1, n):                 # bit-reversal permutation
        bit = n >> 1
        while j & bit:
            j ^= bit
            bit >>= 1
        j |= bit
        if i < j:
            x[i], x[j] = x[j], x[i]
    size = 2
    while size <= n:
        half = size // 2
        step = cmath.exp(-2j * math.pi / size)
        for start in range(0, n, size):
            w = 1.0 + 0j
            for k in range(start, start + half):
                t = w * x[k + half]
                x[k + half] = x[k] - t
                x[k] = x[k] + t
                w *= step
        size *= 2
    return x


def top_spectral_peak(iq_pairs, rate):
    """FFT the most recent FFT_N complex samples; return (offset_hz, db_above_median)."""
    if len(iq_pairs) < FFT_N:
        return None
    seg = iq_pairs[-FFT_N:]
    mags = [abs(v) ** 2 for v in fft(seg)]
    # fftshift ordering: negative freqs second half first
    shifted = mags[FFT_N // 2:] + mags[:FFT_N // 2]
    med = sorted(shifted)[FFT_N // 2]
    best_i = max(range(FFT_N), key=shifted.__getitem__)
    offset = (best_i - FFT_N // 2) * rate / FFT_N
    db = 10 * math.log10((shifted[best_i] + 1e-30) / (med + 1e-30))
    return offset, db


def classify(samples):
    """Repeating pattern vs noise, from raw (unscaled) interleaved floats."""
    if len(samples) < 4096:
        return "NO DATA - nothing to classify"
    win = samples[:16384]
    alphabet = len(set(win))
    nonzero = [abs(v) for v in win if v]
    qstep = min(nonzero) if nonzero else 0.0
    mean = sum(win) / len(win)
    x = [v - mean for v in win]
    denom = sum(v * v for v in x)
    best_r, best_period = 0.0, 0
    if denom > 0:
        for period in range(2, 512):
            r = sum(x[i] * x[i + period] for i in range(len(x) - period)) / denom
            if r > best_r:
                best_r, best_period = r, period
    if best_r > 0.95:
        return (f"REPEATING PATTERN (period {best_period}, r={best_r:.3f}) - "
                "synthetic, not receiver data")
    return (f"NOISE-LIKE (max autocorr r={best_r:.3f}, alphabet {alphabet}, "
            f"LSB {qstep:g}) - real receiver IQ, coarsely quantized")


def main():
    ap = argparse.ArgumentParser(
        description="Characterize the DAX IQ stream. Never transmits.")
    ap.add_argument("--seconds", type=int, default=30)
    ap.add_argument("--freq", type=float, default=None,
                    help="pan center in MHz (default: leave the pan alone)")
    ap.add_argument("--rxant", default=None,
                    help="antenna port, e.g. ANT1/ANT2/XVTA/XVTB")
    ap.add_argument("--rfgain", type=int, default=None)
    ap.add_argument("--rate", type=int, default=48000,
                    choices=sorted(WIDE_CLASSES.values()))
    ap.add_argument("--record", default=None,
                    help="write raw interleaved float32 IQ (radio units) here")
    ap.add_argument("--host", default=DEFAULT_RADIO)
    args = ap.parse_args()

    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    udp.bind(("0.0.0.0", 0))
    local_port = udp.getsockname()[1]
    udp.settimeout(0.2)

    record = open(args.record, "wb") if args.record else None

    with FlexWire(args.host) as wire:
        print(f"Connected. handle={wire.handle}, our UDP port={local_port}")
        for sub in ("client all", "pan all", "slice all", "daxiq all"):
            wire.send(f"sub {sub}")

        # Own our seat: GUI client if a seat is free, plain client otherwise.
        code, client_id = wire.send("client gui")
        gui = code == "0"
        if gui:
            wire.send("client program RigBench")
            wire.send("client station RigBench")
            print(f"  GUI client, id={client_id}")
        else:
            print(f"  client gui refused (code={code}) — plain-client fallback; "
                  "the radio will associate us with the resident GUI client")

        print("  client udpport ->", wire.send(f"client udpport {local_port}")[0])

        stop = threading.Event()

        def keepalive():
            msg = f"client udp_register handle=0x{wire.handle}".encode()
            while not stop.is_set():
                try:
                    udp.sendto(msg, (args.host, VITA_PORT))
                except OSError:
                    pass
                stop.wait(2.0)

        threading.Thread(target=keepalive, daemon=True).start()

        # Find a pan we may use: ours if GUI (persistence restores one fast),
        # anyone's in the fallback case.
        pan_id, created = None, False
        deadline = time.time() + 4.0
        while time.time() < deadline and pan_id is None:
            for line in wire.drain_status(0.5):
                body = line.split("|", 1)[-1]
                if not body.startswith("display pan 0x"):
                    continue
                if gui and "client_handle=0x" + wire.handle not in body:
                    continue
                if "client_handle=0x" in body:
                    pan_id = body.split()[2]
                    break
        if pan_id is None and gui:
            code, msg = wire.send("display panafall create x=800 y=400")
            if code == "0":
                pan_id = msg.split(",")[0].strip()
                created = True
        if pan_id is None:
            print("  NO PANADAPTER available — cannot bind DAXIQ, giving up")
            return 1
        print(f"  panadapter = {pan_id} (created={created}, ours={gui})")

        if gui:
            wire.send(f"display pan set {pan_id} xpixels=50 ypixels=20 fps=1")
        if args.freq is not None:
            print(f"  tune {args.freq:.6f} MHz ->",
                  wire.send(f"display pan set {pan_id} center={args.freq:.6f}")[0])
        if args.rxant:
            print(f"  rxant {args.rxant} ->",
                  wire.send(f"display pan set {pan_id} rxant={args.rxant}")[0])
        if args.rfgain is not None:
            print(f"  rfgain {args.rfgain} ->",
                  wire.send(f"display pan set {pan_id} rfgain={args.rfgain}")[0])

        code, msg = wire.send("stream create type=dax_iq daxiq_channel=1")
        print(f"  stream create -> code={code} id={msg!r}")
        sid = msg.strip()
        sid = sid if sid.startswith("0x") else "0x" + sid
        print("  bind pan ->",
              wire.send(f"display pan set {pan_id} daxiq_channel=1")[0])
        print("  set rate ->",
              wire.send(f"stream set {sid} daxiq_rate={args.rate}")[0])

        # The radio's own verdict on the stream: pan=, active=, endianness.
        seen = None
        for line in wire.drain_status(1.5):
            body = line.split("|", 1)[-1]
            if body.startswith(f"stream {sid}"):
                seen = body
        print(f"  radio says: {seen or 'no stream status arrived (!)'}")
        if seen and "active=1" not in seen:
            print("  WARNING: stream not active — data below may be placeholder")

        print(f"\nCapturing {args.seconds}s at {args.rate} Hz. dBFS is relative "
              f"to full scale {FULL_SCALE:g}.")
        print("  time   pkts    samples   mean dBFS    peak dBFS   top spectral peak")

        t0 = time.time()
        bucket_start = t0
        pkts = n_samps = 0
        energy = peak = 0.0
        recent_pairs = []                  # complex, for the per-second FFT
        head_raw = []                      # unscaled, for the classifier
        while time.time() - t0 < args.seconds:
            try:
                data, _ = udp.recvfrom(16384)
            except socket.timeout:
                data = None

            if data:
                parsed = parse_vita(data)
                if parsed and parsed[0] in WIDE_CLASSES:
                    _, off, plen = parsed
                    n = plen // 4
                    vals = struct.unpack_from(f"<{n}f", data, off)
                    if record:
                        record.write(struct.pack(f"<{n}f", *vals))
                    if len(head_raw) < 16384:
                        head_raw.extend(vals)
                    pkts += 1
                    n_samps += n
                    for i in range(0, n - 1, 2):
                        c = complex(vals[i], vals[i + 1]) / FULL_SCALE
                        a = abs(c)
                        energy += a
                        if a > peak:
                            peak = a
                        recent_pairs.append(c)
                    if len(recent_pairs) > FFT_N:
                        recent_pairs = recent_pairs[-FFT_N:]

            now = time.time()
            if now - bucket_start >= 1.0:
                pairs = n_samps // 2
                mean = energy / pairs if pairs else 0.0
                mean_db = 20 * math.log10(mean) if mean > 0 else float("-inf")
                peak_db = 20 * math.log10(peak) if peak > 0 else float("-inf")
                pk = top_spectral_peak(recent_pairs, args.rate)
                pk_txt = (f"{pk[0]:+8.0f} Hz {pk[1]:5.1f} dB" if pk else "-")
                print(f"  {now - t0:5.0f}s {pkts:5d} {n_samps:10d}   "
                      f"{mean_db:8.2f}    {peak_db:8.2f}   {pk_txt}")
                bucket_start = now
                pkts = n_samps = 0
                energy = peak = 0.0

        print("\n  payload verdict:", classify(head_raw))

        print("\n  tearing down stream")
        print("  stream remove ->", wire.send(f"stream remove {sid}")[0])
        if created:
            print("  pan remove ->", wire.send(f"display pan remove {pan_id}")[0])
        stop.set()
    if record:
        record.close()
        print(f"  raw IQ written to {args.record}")
    udp.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
