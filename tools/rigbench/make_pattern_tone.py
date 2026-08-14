"""Generate the patterned test tone for the RF truth test.

  python tools\\rigbench\\make_pattern_tone.py [out.wav]

Three 2-second tone bursts separated by 1-second silences, with a 1-second
lead-in so keying settles before the first burst. The point of the pattern:
if the forward-power meter pulses in this exact rhythm, the audio chain is
driving the RF — a steady reading can be argued with, a rhythm cannot.

700 Hz (inside any SSB transmit passband) at -10 dBFS, 48 kHz mono 16-bit.
Each burst gets 10 ms raised-cosine edges — a sine that starts at full
amplitude is a key click on the air.

Stdlib only, by design; runs anywhere Python does.
"""

import math
import struct
import sys
import wave

RATE = 48000
FREQ = 700.0
LEVEL_DBFS = -10.0
BURST_S = 2.0
GAP_S = 1.0
BURSTS = 3
LEAD_S = 1.0
TAIL_S = 0.5
RAMP_S = 0.010

DEFAULT_OUT = r"C:\temp\tone-pattern-700.wav"


def burst(seconds):
    amp = 10.0 ** (LEVEL_DBFS / 20.0)
    n = int(seconds * RATE)
    ramp = int(RAMP_S * RATE)
    for i in range(n):
        env = 1.0
        if i < ramp:
            env = 0.5 - 0.5 * math.cos(math.pi * i / ramp)
        elif i >= n - ramp:
            env = 0.5 - 0.5 * math.cos(math.pi * (n - 1 - i) / ramp)
        yield amp * env * math.sin(2.0 * math.pi * FREQ * i / RATE)


def silence(seconds):
    for _ in range(int(seconds * RATE)):
        yield 0.0


def main():
    out = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_OUT
    samples = []
    samples.extend(silence(LEAD_S))
    for b in range(BURSTS):
        samples.extend(burst(BURST_S))
        samples.extend(silence(GAP_S if b < BURSTS - 1 else TAIL_S))

    with wave.open(out, "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(RATE)
        wav.writeframes(
            b"".join(struct.pack("<h", int(s * 32767)) for s in samples)
        )

    total = len(samples) / RATE
    print(f"{out}: {total:.1f} s, {BURSTS} bursts of {BURST_S:.0f} s at "
          f"{FREQ:.0f} Hz, {LEVEL_DBFS:.0f} dBFS, gaps {GAP_S:.0f} s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
