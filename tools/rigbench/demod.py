"""Demodulate captured DAX IQ to a listenable WAV. Stage 2 of the IQ experiment.

  <python-with-numpy> tools\\rigbench\\demod.py iq-fdxoff.f32 [out.wav]

NUMPY NOTE: this machine's Python is PEP-668 externally managed and has no
numpy. numpy 2.4.6 already exists in the Freight Fate venv, so run this with:

  C:\\dev\\Freight-Fate\\.venv\\Scripts\\python.exe tools\\rigbench\\demod.py ...

Input is raw interleaved float32 IQ in radio units at 48 kHz complex, exactly
as `daxiq_probe.py --record` writes it.

Why the demodulation is this simple: the pan is centred on the transmit
frequency, so the suppressed carrier sits at DC and the complex baseband IS the
analytic signal of the audio. Keeping only the positive-frequency voice band
and taking the real part recovers the audio directly — no mixing, no Hilbert
transform, no carrier recovery.
"""

import sys
import wave

import numpy as np

FS = 48000.0
LO_HZ = 150.0
HI_HZ = 2900.0
CENTER_MHZ = 144.100   # pan centre the capture was taken at


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else "iq-fdxoff.f32"
    dst = sys.argv[2] if len(sys.argv) > 2 else src.rsplit(".", 1)[0] + ".wav"

    # Emulate tuning the receiver elsewhere, in software, on recorded IQ.
    # Tuning LOW must raise the recovered pitch: a real USB demodulator sees the
    # signal further above its carrier. Physics no injected tap can imitate.
    tune_mhz = float(sys.argv[3]) if len(sys.argv) > 3 else CENTER_MHZ
    shift_hz = (CENTER_MHZ - tune_mhz) * 1e6

    raw = np.fromfile(src, dtype="<f4")
    iq = raw[0::2] + 1j * raw[1::2]
    n = iq.size
    print(f"{src}: {n} complex samples, {n / FS:.1f} s at {FS/1000:.0f} kHz")

    # Per-second energy, so we can say where the transmission is.
    print("\n  second   mean dBFS")
    step = int(FS)
    for i in range(0, n - step + 1, step):
        seg = np.abs(iq[i:i + step])
        db = 20 * np.log10(max(seg.mean(), 1e-9) / 32768.0)
        bar = "#" * max(0, int((db + 60) / 2))
        print(f"  {i // step:5d}s   {db:8.2f}  {bar}")

    if shift_hz:
        t = np.arange(n) / FS
        iq = iq * np.exp(2j * np.pi * shift_hz * t)
        print(f"\n  tuned to {tune_mhz:.4f} MHz "
              f"({shift_hz:+.0f} Hz vs capture centre {CENTER_MHZ:.4f}) — "
              f"a real signal must shift pitch by {shift_hz:+.0f} Hz")

    # USB: keep the positive-frequency voice band, take the real part.
    spec = np.fft.fft(iq)
    freqs = np.fft.fftfreq(n, 1.0 / FS)
    spec[~((freqs >= LO_HZ) & (freqs <= HI_HZ))] = 0
    audio = np.real(np.fft.ifft(spec)) * 2.0

    peak = np.abs(audio).max()
    if peak > 0:
        audio = audio / peak * 0.89
    pcm = (audio * 32767).astype("<i2")

    with wave.open(dst, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(int(FS))
        w.writeframes(pcm.tobytes())

    print(f"\nwrote {dst}  ({pcm.size / FS:.1f} s, mono 16-bit {int(FS)} Hz)")


if __name__ == "__main__":
    main()
