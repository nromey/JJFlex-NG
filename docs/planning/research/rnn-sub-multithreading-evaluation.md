# RNN + SUB Multithreading Evaluation

Decision-quality memo for the JJFlexRadio RX audio pipeline. Question: should `RxAudioPipeline.Process()` keep running SpectralSubtraction (SUB) and RNNoise (RNN) sequentially, or split them across cores for concurrent execution?

Pipeline today: `Decoded PCM -> SpectralSubtraction -> RNNoise -> PortAudio queue` in `JJFlexWpf/RxAudioPipeline.cs`, executed on `FlexBase.remoteAudioProc`.

---

## 1. Verdict

Keep the pipeline sequential. Concurrent SUB+RNN is technically feasible but buys nothing on this workload: combined per-frame cost is already ~0.4-0.6 ms on a 2017-era multi-core CPU, the audio frame budget is 10 ms, and concurrent execution would add ring-buffer latency, thread-handoff overhead, and a complete priority-inversion risk for what is at best a sub-millisecond speedup. The right multi-core dimension to use later is **stage isolation** (the waterfall FFT tap on its own thread, recording tap on its own thread), not splitting the two NR stages from each other.

---

## 2. Architectural shape

### 2a. Why sequential is the right call here

Three structural facts make concurrency unattractive:

**Both stages already finish well inside the frame budget.** RNNoise's published complexity is ~40 MFLOPS on 10 ms frames (~17.5 MFLOPS DNN + 7.5 MFLOPS FFT/IFFT + 10 MFLOPS pitch search). Jean-Marc Valin reports the unoptimized C runs ~60x faster than real time on x86 and consumes ~1.3% CPU; the new RNNoise 0.2 SSE4.1/AVX2 build is roughly 4x faster again. SUB here is one 1024-point FFT and one 1024-point IFFT per hop, plus a magnitude/phase reconstruction loop — comfortably cheaper than RNN. A "SUB+RNN" frame on a 2017 i5 lands at ~0.4-0.6 ms wall-clock; the 10 ms frame budget has ~95% headroom even worst case.

**The stages are not symmetric in cost.** The whole point of parallelizing stages is that `max(A, B) < A + B`. Here `B` (RNN) dominates and `A` (SUB) is small relative to `B`, so `max(A, B) ~= B`. The theoretical speedup ceiling is the SUB cost, ~0.1-0.2 ms — which is below the noise floor of Windows scheduler jitter on a non-RT thread.

**Concurrent execution would force a producer/consumer boundary between stages.** Today both stages mutate the same `float[] buffer`. To run them on different threads you have to either (a) double-buffer with a lock-free SPSC ring, paying one extra frame of latency per stage boundary you split, or (b) sub-divide the buffer into smaller chunks, which conflicts with both providers' internal frame requirements (RNN wants exactly 480 samples per call; SUB wants FftSize=1024 with HopSize=512 overlap-add). Adding 10-20 ms of buffering to save sub-millisecond compute is the wrong trade.

### 2b. What concurrent execution would actually look like (for reference)

If we ever did parallelize, the shape would be:

- Single producer thread (today's `remoteAudioProc`) writes decoded PCM into a lock-free SPSC ring.
- Worker thread A reads from ring, runs `SpectralSubtractionProvider.ProcessInPlace`, writes to a second SPSC ring.
- Worker thread B reads from ring 2, runs `NoiseReductionProvider.ProcessInPlace`, writes to a third SPSC ring.
- PortAudio enqueue thread drains ring 3.
- Each ring sized to one frame of slack — the minimum decoupling that lets one stage run while the next one is busy.

This is a **pipeline parallel** topology, not data parallel. It does not let SUB and RNN both work on the same frame at once — they work on consecutive frames at the same wall-clock instant. That is the only useful concurrency shape, because both stages are inherently in-order on a single channel of audio.

Pipeline parallelism's actual win is throughput, not latency: it would let us run more frames per second through the pipeline, which we do not need (real-time audio is bounded by sample rate, not pipeline throughput).

### 2c. The right multi-core dimension to plan for

Future RX-side stages are fan-out, not in-line: the waterfall FFT tap (Sprint 26+ scope) and any recording tap want a copy of the decoded PCM but do not affect the playback path. Those should run on dedicated worker threads with their own SPSC ring drained from a tee point near the top of the pipeline, so a slow waterfall computation never starves PortAudio. That is the multi-core architectural decision worth making — not parallelizing two cheap stages against each other.

---

## 3. Latency budget

Numbers below are best estimates from published RNNoise figures plus the SUB code path in `SpectralSubtractionProvider.ProcessOneFrame`. Treat them as order-of-magnitude, not benchmarks.

| Item | Per 10ms frame |
|---|---|
| RNN (RNNoise 0.1, scalar) on 2017 i5 | ~0.15-0.20 ms |
| RNN (RNNoise 0.2, AVX2) on 2017 i5 | ~0.04-0.06 ms |
| SUB (FFT 1024 + IFFT 1024 + magnitude reconstruction) on 2017 i5 | ~0.10-0.15 ms |
| Sequential SUB then RNN, total | ~0.25-0.35 ms (with RNN 0.2) |
| Concurrent max(SUB, RNN) | ~0.10-0.15 ms |
| Theoretical speedup | 0.15-0.20 ms |

End-to-end latency contributions today:

- Decode buffer: depends on Opus framing, typically 20-40 ms on the network side — already dominant.
- SUB inherent latency: FFT-1024 with HopSize-512 imposes a half-frame group delay (~10-11 ms at 48 kHz) plus overlap-add latency (~21 ms total worst case).
- RNN inherent latency: 480-sample frame plus internal delay = ~13.3 ms (per Valin's published 640-frame actual latency).
- PortAudio playback buffer: typically 10-20 ms.

Total RX path latency budget today: ~60-100 ms. The 0.25-0.35 ms compute cost of running both stages sequentially is irrelevant against the structural latencies. **Going concurrent would save ~0.15 ms of compute and add at minimum one frame (~10 ms) of ring-buffer latency at each split point.** That is a 50-100x latency penalty for a sub-millisecond compute win.

---

## 4. Audio quality observations

This is where the research is most interesting and most relevant to the "preset" question.

**SUB-then-RNN (current order) is the defensible choice.** Spectral subtraction's classic failure mode is musical noise — random tonal artifacts where bins get over-subtracted. RNNoise's per-band gain output is specifically constructed to **suppress musical noise** because gains are smoothed across Bark bands and across time inside the GRU. Running RNN as a post-stage cleans up SUB's artifacts in exactly the way the published Wiener-post-filtering literature recommends ("Musical noise reduction based on spectral subtraction combined with Wiener filtering"). SUB's role is to remove a band-specific stationary noise floor that RNN was never trained for (HF band-specific QRN, antenna self-noise patterns); RNN's role is to clean up speech-time-varying noise plus SUB's artifacts.

**RNN-then-SUB would be worse.** SUB after a non-linear stage is hard to tune because the noise floor RNN leaves behind is not the noise floor SUB sampled. SUB expects approximately stationary additive noise; RNN's residual is signal-dependent.

**There is some functional overlap.** RNNoise's published architecture explicitly documents an internal spectral-subtraction-as-baseline component; that is RNN's *baseline against speech-relevant Bark bands*, not a band-specific noise capture. So SUB and RNN are not completely orthogonal but they are not redundant either — SUB's user-captured profile gives band/antenna-specific information RNN cannot have. This is exactly the asymmetry that makes SUB+RNN useful for ham radio specifically: the ham operator knows what their band sounds like in a way no general-purpose neural net does.

**Combination outcome (from published two-stage hybrid speech-enhancement literature):**

- Hybrid DSP+DNN systems consistently outperform either alone on PESQ/STOI metrics when the DSP stage is appropriately conservative.
- Sequential ordering (DSP first, DNN second) is the dominant published topology.
- Joint training of the two stages (Park et al., RNNoise-Ex) gives the best results, but we are not training; we are composing pre-trained components. So we will get less than the joint-training upper bound.
- The gotcha: **aggressive SUB tuning in front of RNN can degrade RNN output**, because RNN was trained on un-pre-processed noisy speech and unfamiliar pre-processed inputs are out-of-distribution. The current `Strength=0.7` SUB default is on the aggressive end. A combined-on preset should ship with SUB closer to 0.4-0.5 strength to keep RNN's input within a sane envelope.

**Voice artifacts:** RNN at `Strength=0.8` (current default) is well-tolerated for SSB voice. Above 0.9 it starts producing the characteristic "wet" artifact from any DNN denoiser. SUB above 0.8 starts producing musical noise even with the SpectralFloor in place. Combined-on default should not push either knob above its single-stage default.

---

## 5. CPU profile expectation

A typical ham operator PC of 2017-vintage or newer (Intel i5-7xxx / Ryzen 5 1xxx, 4+ cores, ~3 GHz, AVX2) handles SUB+RNN concurrent at well under 5% CPU on one core. This is consistent with Valin's reported 1.3% RNNoise CPU figure and SUB's lower-than-RNN cost.

**Underrun risk is essentially zero on the audio compute side.** The realistic underrun causes on this hardware class are unrelated to NR cost:

- Garbage collector pause on the audio thread (we should be allocation-free in the hot path; SUB allocates `new double[spectrumSize]` in `FinalizeSampling` only, RNN allocates nothing in steady state).
- Network jitter on SmartLink hauling the Opus stream.
- Windows scheduler preemption when the audio thread is not priority-elevated.

Lower-tier hardware (older laptops, Atom-class) will see a couple of percent CPU; still not close to underrun. The current sealed RNNoise.NET wrapper is unoptimized scalar C; the planned migration to xiph upstream v0.2 with AVX2 reduces RNN cost ~4x, which only improves the picture.

---

## 6. Implementation recommendation

### Pipeline topology

**Keep `Process()` sequential.** Do not introduce ring buffers between SUB and RNN. The current code in `RxAudioPipeline.Process` is the right shape:

```
_spectralSub.ProcessInPlace(buffer, 0, count, _channels);
_rnnoise.ProcessInPlace(buffer, 0, count, _channels);
```

### "Coordinated radio-side + app-side NR" preset

The preset (referenced in `project_dsp_controls_design.md`) should treat both stages as a **balanced pair**, not the sum of their single-stage defaults. Suggested settings for the combined-on case:

- `SpectralEnabled = true`, `SpectralStrength = 0.45`, `SpectralFloor = 0.04` (more conservative than the 0.7 / 0.02 single-stage default — leave headroom for RNN).
- `RnnEnabled = true`, `RnnStrength = 0.65` (slightly less wet than the 0.8 single-stage default — combined wetness adds up).
- `RnnAutoDisableNonVoice = true` (unchanged; RNN is speech-trained, SUB still runs).
- Radio-side WNB / NB / NR settings: leave at user preference; the preset is PC-side only. Coordinated-with-radio-side is a separate scope.

When SUB is on alone: keep `Strength = 0.7`, `Floor = 0.02` (current default).
When RNN is on alone: keep `Strength = 0.8` (current default).
The preset's job is to detect "both on" and re-balance, not to force users into one combination.

### UI knobs

The user's mental model should be a single "PC-side noise reduction" expander with two child sections (Spectral / Neural) and a top-level "Combined preset" button that applies the balanced pair above. Per the flexibility principle, do not hide the underlying knobs — the preset is a one-click starting point, not a wall.

### Code-level work this implies

No source changes are recommended from this memo's findings alone, but the preset work (Sprint 30+) should:

1. Add a new `ApplyCoordinatedPreset()` method on `RxAudioPipeline` that sets the four knobs above atomically.
2. Have the UI call it from a single button rather than driving each property separately.
3. Document the preset's "why these numbers" reasoning inline near the method, citing this memo so future maintainers do not re-tune blindly.

---

## 7. Open questions (listening tests only)

The numerical analysis above is reasonably tight; the audio-perceptual decisions are not.

1. **Is `SpectralStrength = 0.45` the right balance point in front of RNN, or should it be 0.35 or 0.55?** Only A/B listening on real noisy band conditions can decide. Don's 6300 + Justin's 8400 on real bands across multiple noise environments (rural QRN, urban switching-PSU hash, daytime atmospheric, nighttime quiet) is the listening matrix.
2. **Does the combined preset sound noticeably better than RNN-alone for typical SSB ragchew conditions?** RNN-alone is the floor; if SUB+RNN combined is not perceptually distinguishable from RNN-alone for most users, the preset becomes a marketing line rather than a real feature, and we should default to "RNN-only" for combined-on users until they explicitly capture a SUB profile.
3. **Does RNN-alone or SUB-alone beat the combined preset on CW or digital modes?** Today RNN auto-disables on CW; that leaves SUB on solo. We should confirm SUB-only is the right shape for CW/digital, not "both off."
4. **What happens with a stale or wrong-band SUB profile in front of RNN?** Out-of-band noise profile + RNN may produce worse audio than no SUB at all. The UI may need a "profile age" or "profile band match" warning when the SUB profile does not match the current band.
5. **Does aggressive radio-side WNB/NB upstream of PC-side SUB+RNN cause SUB's noise profile to mismatch?** Probably yes, because radio-side processing changes the noise floor SUB was capturing. This argues for capturing SUB profiles with the user's intended radio-side settings already on, and re-capturing if they change.

These are all bench-tested in Sprint 30+ when the preset lands. None of them changes the core architectural recommendation: **sequential SUB-then-RNN, no concurrency between the two NR stages.**

---

## Sources

- [RNNoise: Learning Noise Suppression — Jean-Marc Valin](https://jmvalin.ca/demo/rnnoise/) — published architecture, 40 MFLOPS complexity, 1.3% CPU, 60x real-time
- [xiph/rnnoise on GitHub](https://github.com/xiph/rnnoise) — reference implementation, 480-sample frame, RNNoise 0.2 release notes
- [RNNoise 0.2 Released With AVX2 Optimizations — Phoronix](https://www.phoronix.com/news/RNNoise-0.2-Released) — vectorization speedup figures
- [RNNoise-Ex: Hybrid Speech Enhancement System based on RNN and Spectral Features (arXiv 2105.11813)](https://arxiv.org/pdf/2105.11813) — hybrid DSP+DNN joint training
- [Musical noise reduction based on spectral subtraction combined with Wiener filtering — IEEE](https://ieeexplore.ieee.org/document/5521910) — sequential post-filter pattern for SUB cleanup
- [A Hybrid DSP/Deep Learning Approach to Real-Time Full-Band Speech Enhancement (arXiv 1709.08243)](https://arxiv.org/pdf/1709.08243) — RNNoise's own foundational paper
- [Two-stage Deep Learning for Noisy-reverberant Speech Enhancement — PMC](https://pmc.ncbi.nlm.nih.gov/articles/PMC6519714/) — multi-stage topology and joint-training observations
- [How Fast are DSPs? — dspguide.com](https://www.dspguide.com/ch28/6.htm) — pipeline-parallel vs data-parallel framing
- [Multi-threading in a plugin — JUCE forum](https://forum.juce.com/t/multi-threading-in-a-plugin/18374) — practical thread-handoff overhead in audio pipelines
- [Latency-aware Scheduling and Real-Time OS Integration for Deterministic DSP Pipelines](https://www.researchgate.net/publication/395234044) — multicore scheduling jitter constraints
