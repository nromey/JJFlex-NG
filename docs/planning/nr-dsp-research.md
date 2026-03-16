# Noise Reduction & DSP Research

**Status:** Research complete
**Date:** 2026-03-16

## Approach 1: RNNoise (Neural NR) — Sprint 25 Implement

Pre-trained neural network from Xiph.org. Knows "what noise sounds like" from training data.

- **Library:** YellowDogMan.RRNoise.NET (NuGet, updated Nov 2025)
- **Performance:** 10ms latency, ~1-2% CPU per stream
- **Integration:** ISampleProvider decorator in NAudio chain
- **Best for:** General broadband noise, hiss, random noise
- **Caveat:** Trained on speech — may suppress weak CW/digital signals. Must be optional per mode.
- **UI:** Toggle in Audio Workshop or DSP ScreenFields

### Integration Pattern
```csharp
// Insert after RX audio stream, before speaker output
// NOT in earcon/meter chain
public class NoiseReductionProvider : ISampleProvider
{
    // Wraps source provider, passes 480-sample frames through RNNoise
    // Same pattern as ContinuousToneSampleProvider
}
```

## Approach 2: Spectral Subtraction (Trainable NR) — Future Sprint

User samples the noise floor, system builds a profile, subtracts it during operation.
Inspired by Don's suggestion. Better than SmartSDR+ because you can train it on YOUR noise.

- **Library:** MathNet.Numerics for FFT (MIT, NuGet) or FftSharp (pure C#, no deps)
- **Technique:** Overlap-add FFT processing, spectral averaging during sample phase
- **Sample duration:** 1-3 seconds (configurable, default 2s). Longer = drift risk.
- **Best for:** Steady-state noise: power line hum, switching supply RFI, fan noise, specific interference
- **Profiles:** Save/load per band, per antenna, named presets
- **Strength control:** Slider for subtraction aggressiveness (too much = artifacts)
- **Smart sampling:** Discard FFT frames with sudden signal spikes during profiling

### UI Flow
1. "Sample Noise" button in Audio Workshop (speech: "Sampling noise... 3, 2, 1, done")
2. Profile saved with user-chosen name
3. "Active Profiles" list — enable/disable per session
4. Strength slider (0-100%)

## Both Approaches Are Complementary

- RNNoise = always-on general NR (like a noise gate but smarter)
- Spectral subtraction = targeted removal of specific known noise
- Can run both simultaneously in the ISampleProvider chain
- RNNoise first (removes general noise), then spectral subtraction (removes specific residual)

## Recommended NuGet Packages

Add to project:
- **NWaves 0.9.6** — comprehensive DSP (filters, FFT, features, synthesis)
- **MathNet.Filtering 0.7.0** — proven filter coefficient calculation (Butterworth, bilinear transform)
- **FftSharp 2.2.0** — pure C# FFT, no dependencies, good for waterfall/spectrum
- **DtmfDetection** — Goertzel tone detection (CW tools, DTMF generation)

Already have:
- NAudio (audio I/O)
- MathNet.Numerics (implicit via Filtering)

Do NOT add: Intel MKL.NET (overkill, licensing complexity)

## Built-In DSP Features (no VST needed)

### High Priority

**Software AGC**
- ~100 lines of C#, smooths audio, per-mode attack/decay
- Attack: 2-20ms, Decay: 200-1000ms, Hang time configurable
- Mode-specific defaults: CW (attack 20ms, decay 200ms), SSB (attack 10ms, decay 500ms)
- Integrates with FiltersDspControl as ISampleProvider
- Complements rig-side RF Gain

**Auto-Notch Filter (LMS/NLMS Adaptive)**
- Detects and tracks single-frequency interference (carriers, heterodynes)
- LMS algorithm: ~50 lines of core C#, NWaves for supporting math
- Automatically narrows notch around detected frequency
- NOTE: Check if FlexLib exposes TNF (Tracking Notch Filter) API first — use radio's TNF if available, add software TNF as fallback

**Expanded Filter Types**
- Peaking EQ with variable Q via NWaves/MathNet
- Additional notch filter UI in FiltersDspControl
- BiQuad cascaded filters for efficient implementation

**Goertzel CW Tools**
- Pure tone detection for CW signal identification
- Sidetone pitch detection
- Accessible CW tuning aid — audio feedback when tuned to center
- DtmfDetection library on NuGet, adaptable for any tone

### Medium Priority

**Wiener / MMSE Noise Reduction**
- Complements RNNoise for challenging noise profiles
- Per-band gain calculation in frequency domain
- Better for stationary noise than RNNoise
- MathNet.Numerics handles the math

**FFT Spectrum / Waterfall**
- FftSharp: ~200 microseconds for 2048-point FFT
- Sufficient for 10-30 Hz display refresh
- Windowing: Hann (general), Kaiser (narrow signal detection)
- Resolution: sample_rate / fft_size (e.g., 48kHz / 2048 = ~23 Hz bins)
- Basis for both visual waterfall and accessible audio/braille waterfall

### Lower Priority

**Digital Mode Support**
- Option 1 (recommended MVP): call fldigi/WSJT-X as subprocess, return decoded text
- Option 2 (future): native C# PSK/RTTY/MFSK demodulators using NWaves
- Option 3: P/Invoke wrapper around C++ decoder DLL

**IQ Processing**
- Hilbert transform (real → analytic signal)
- Frequency shifting (heterodyning)
- Decimation / interpolation
- Low priority — FlexLib already handles IF filtering and mixing
- Becomes valuable for wideband SDR input or custom IF processing

## Accessible Waterfall / Spectrum

For blind operators, the waterfall is sonified or displayed on braille. The DSP foundation is shared:

**Noise floor estimation** — FFT average, determine baseline
**Signal detection threshold** — user-adjustable sensitivity:
  - Low sensitivity: only strong signals, clean display, minimal noise
  - High sensitivity: weaker signals visible, more detail, more noise
  - Default: auto-calculated from noise floor, user can override
**Temporal smoothing** — average across frames so signals are stable, not flickering
**Signal tracking** — coherent signals hold steady across frames, noise doesn't

This is what turns Jim's raw "acdeipqbasppaaab" braille output into meaningful signal representation. The same FFT + thresholding + smoothing pipeline feeds:
- Visual waterfall (for sighted users)
- Braille waterfall (cleaned characters on refreshable display)
- Audio waterfall (sonified spectrum)
- Signal announcements ("signal at 14.255, strong")

## Performance Budget (single core)

- RNNoise: ~15-20% CPU
- AGC: ~2%
- Filters (FIR/IIR): ~5-10%
- FFT (2048-pt @ 30Hz): ~3-5%
- Total: comfortably under 50%, plenty of headroom
- SIMD via System.Numerics.Vectors can provide 3-5x speedup for FIR/FFT if needed

## VST3 Plugin Hosting (Backlog)

Power-user escape hatch: host third-party VST3 plugins for audio processing.
Users bring their own NR, EQ, compression plugins.
Requires VST3 host wrapper for .NET — research needed.
Built-in DSP covers most users; VST3 is for advanced operators who already own plugins.
