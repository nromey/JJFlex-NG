# RNN Noise Reduction Library Survey

Decision-quality memo for selecting the noise-reduction backend that will support
user-loadable models in the JJFlexRadio RX audio pipeline.

Date: 2026-05-04
Scope: Tier 1 (bundled default) / Tier 2 (optional pack from data.jjflexible.radio)
/ Tier 3 (user-supplied .rnnn or equivalent in a known folder).

---

## 1. Recommendation

**Choose Option B: P/Invoke Xiph's stock RNNoise C library directly.** The deciding
criterion is that the modern Xiph rnnoise.h (v0.2, April 2024) already exposes
`rnnoise_model_from_filename()`, `rnnoise_model_from_file()`, and
`rnnoise_model_from_buffer()` as first-class public API — model selection is a
supported concept in the upstream library, not a fork-only feature, and not a
pattern we have to invent. Pairing that with a hand-written ~300-500 LOC P/Invoke
layer matches our existing `runtimes/` native-DLL pattern (PortAudio, libopus),
keeps the license MIT-compatible (BSD-3-Clause), and preserves the small
dependency footprint that makes JJFlexRadio installer-friendly.

DeepFilterNet is a strong technical product but the integration cost
(Rust-toolchain dependency for self-built binaries, ONNX runtime, ~100MB+ model
artifacts, bit-rotting since Oct 2024) and its model-loading model
(tar.gz-of-ONNX rather than a small swappable weights file) make it the wrong
fit for Tiers 2-3 of our distribution model. It belongs on the watchlist as a
possible *additional* engine post-foundation, not as the primary one.

---

## 2. Library Survey

### 2.1 Xiph RNNoise (upstream, stock)

- **Repo:** https://github.com/xiph/rnnoise
- **License:** BSD-3-Clause (compatible with our MIT)
- **Latest release:** v0.2, April 15 2024. Most recent commit: Feb 22 2026.
  Maintainership pace is episodic but the lead author (jmvalin) is still active.
- **Model selection:** First-class, since v0.2. Public API exposes:
  - `RNNModel *rnnoise_model_from_filename(const char *filename)`
  - `RNNModel *rnnoise_model_from_file(FILE *f)`
  - `RNNModel *rnnoise_model_from_buffer(const void *ptr, int len)`
  - `void rnnoise_model_free(RNNModel *)`
  - `DenoiseState *rnnoise_create(RNNModel *model)` (NULL = embedded default)
  - `float rnnoise_process_frame(DenoiseState *, float *out, const float *in)`
  - `rnnoise_get_frame_size()` returns 480 (10 ms @ 48 kHz)
- **Model file format:** Binary "machine endian" weights blob (`.bin`),
  produced by `dump_weights_blob` from a trained model. Small (single-digit MB,
  often KB-scale). Filename convention is just `*.bin`; we are free to standardize
  on `.rnnn` for our own packaging.
- **.NET integration:** No first-class wrapper — that is the gap we fill. P/Invoke
  surface is small (~10 functions, all opaque-pointer-shaped, no callbacks, no
  threading concerns).
- **Native dependency:** A single small DLL (estimated <500 KB stripped, x64).
  Build is autotools-based; cross-compiling for Windows requires either
  Yellow-Dog-Man's fork build scripts or our own MSVC/MinGW pipeline.
- **Platform coverage:** Source-portable to Windows, Linux, macOS, Android.
  No upstream Windows binary releases — must be built. (See risks section.)
- **Limitations / caveats:**
  - Upstream doesn't ship Windows binaries; we own the build.
  - The newer model-from-file API has had build-flag surprises (PR #263,
    `USE_WEIGHTS_FILE`) — our build needs explicit verification.
  - Single-channel 48 kHz, 10 ms frames — matches what we already feed
    RNNoise.NET, so no pipeline changes.

### 2.2 RNNoise.NET (Yellow-Dog-Man / what we use today)

- **Repo:** https://github.com/Yellow-Dog-Man/RNNoise.Net (note: the
  Neos-Metaverse fork is the original; YDM is the actively-published one).
- **License:** MIT.
- **Latest release:** 0.1.9, Nov 23 2025 (NuGet). Repo is alive but cadence is
  measured.
- **Model selection:** **Not supported.** The `Denoiser` constructor is
  parameterless and calls `rnnoise_create(IntPtr.Zero)` — embedded default model
  only. No overload, no setter. This is the wall we are hitting.
- **.NET integration:** Drop-in NuGet, sealed `Denoiser` class, fixed 480-sample
  frames, internal buffering for arbitrary chunk sizes.
- **Native dependency:** Bundles a custom-built rnnoise DLL from the YDM rnnoise
  fork (older API surface, possibly pre-`rnnoise_model_from_*`).
- **Why this is the *floor* of our problem:** Even if YDM published an updated
  wrapper exposing model loading, we would inherit their build cadence and their
  choice of underlying rnnoise commit. P/Invoking upstream gives us version
  control over both layers.

### 2.3 rnnoise-nu (GregorR fork)

- **Repo:** https://github.com/GregorR/rnnoise-nu
- **License:** BSD-3-Clause.
- **Latest commit:** September 2018. **Unmaintained** (~7 years stale).
- **Model selection:** Yes — this fork is historically *the* place hams and
  voice-comms folks have grabbed swappable RNN models (`marathon-prescription`,
  `beguiling-drafter`, `somnolent-hogwash`, etc., from the companion
  rnnoise-models repo, also last updated 2018).
- **Why it was historically the answer:** Before upstream Xiph added
  `rnnoise_model_from_filename`, rnnoise-nu was the only way to load a custom
  `.rnnn` at runtime.
- **Why it is no longer the answer:** Upstream has caught up and surpassed it
  feature-wise. Picking rnnoise-nu in 2026 means picking 7-year-stale code that
  diverges from any ongoing security or performance work.
- **The community models (rnnoise-models repo) are still useful** — they were
  trained against the rnnoise-nu format, but the trained-weights concept ports
  forward. We may need a small conversion utility to re-pack them into the
  current upstream binary blob format, or we accept that Tier 3 starts with
  fresh-trained models against the upstream toolchain.

### 2.4 nnnoiseless (Rust port)

- **Repo:** https://github.com/jneem/nnnoiseless
- **License:** BSD-3-Clause.
- **Latest commit:** December 18 2025 (recent maintenance, not feature work).
- **Model selection:** Yes — `RnnModel::from_bytes()`, `RnnModel::from_static_bytes()`,
  and a `--model` CLI flag. Has its own binary weights format (script provided
  to convert RNNoise weights).
- **C API:** Yes — claims compatibility with RNNoise's header.
- **.NET integration story:** No existing .NET wrapper. Same P/Invoke effort as
  Option B, but with the extra cost of pulling Rust into the build toolchain.
- **Why it is not the recommendation:** No win over upstream Xiph for our use
  case. Adopting Rust adds a toolchain dependency without unlocking new capability,
  and ties us to a port that has historically lagged upstream feature work.

### 2.5 DeepFilterNet (Rikorose)

- **Repo:** https://github.com/Rikorose/DeepFilterNet
- **License:** Dual MIT / Apache-2.0 (compatible with our MIT).
- **Latest release:** v0.5.6, Aug 31 2024. Most recent commit: Oct 17 2024.
  No 2025-2026 activity. **Sliding into maintenance mode.**
- **Model selection:** Yes — `df_create(model_path, atten_lim, log_level)`
  takes a tar.gz of an ONNX model on the file system.
- **C API:** Yes (libDF / capi.rs):
  - `df_create()`, `df_get_frame_length()`, `df_process_frame()`,
    `df_set_atten_lim()`, `df_set_post_filter_beta()`, `df_free()` etc.
- **.NET integration:** No existing wrapper. Hand-rolled P/Invoke is doable —
  the surface is similar in size to RNNoise's. The bigger cost is downstream:
  the runtime depends on an ONNX-runtime backend (or PyTorch via Python),
  which dwarfs the rnnoise DLL footprint.
- **Native dependency size:** Substantially larger than rnnoise — model files
  are tar.gz of full ONNX graphs (DeepFilterNet2/3), tens of MB each, plus the
  ONNX runtime. The "small accessible installer" argument that makes JJFlex
  appealing erodes if we ship a 50-100 MB DSP backend by default.
- **Quality:** Subjectively superior speech enhancement on broadband noise vs
  RNNoise on most published comparisons. Real win at the cost of compute.
- **Limitations / caveats:**
  - Maintenance posture is the largest red flag — no commits in over a year as
    of this memo.
  - Tar.gz-of-ONNX model format is harder to align with our Tier 3 "user drops
    a small file in a folder" UX.
  - Higher CPU than rnnoise; matters for HF radios running on modest hardware.
  - Larger latency budget (20 ms STFT minimum), though still acceptable for
    radio RX.

### 2.6 Honourable mentions (not advancing to the matrix)

- **sysprog21/rnnoise** — STM32-targeted port; not a desktop candidate.
- **Intel/deepfilternet-openvino** — interesting acceleration story but pins
  us to OpenVINO runtime, which is a much bigger dependency than ONNX.
- **DeepFilterNet3-VST3 (Shuichi346)** — VST plugin, not a library we link.
- **AndroidDeepFilterNet (Kaleyra)** — Android-specific.

---

## 3. Decision Matrix

Higher score = better fit for JJFlexRadio. Scale 1 (poor) to 5 (excellent).
Weights reflect the brief's stated priorities: model selection, API stability /
maintenance, license, then cross-platform readiness, then .NET integration ease.

| Criterion (weight)                  | Xiph RNNoise (B) | RNNoise.NET (today) | rnnoise-nu | nnnoiseless | DeepFilterNet |
|-------------------------------------|------------------|---------------------|------------|-------------|---------------|
| Model selection support (x3)        | 5                | 1                   | 5          | 5           | 4             |
| Maintenance / API stability (x2)    | 4                | 3                   | 1          | 3           | 2             |
| License compatibility (x2)          | 5 (BSD-3)        | 5 (MIT)             | 5 (BSD-3)  | 5 (BSD-3)   | 5 (MIT/Ap2)   |
| Cross-platform (Win + future Mac) (x2) | 5             | 4                   | 4          | 5           | 4             |
| .NET integration ease (x1)          | 3 (P/Invoke)     | 5 (drop-in)         | 3          | 2           | 2             |
| **Weighted total (max 50)**         | **44**           | **31**              | **34**     | **37**      | **33**        |

Notes on scoring:

- **RNNoise.NET scores poorly only on model selection** — but that single axis is
  the entire reason this memo exists. Disqualified for the use case.
- **rnnoise-nu** ties on model selection but the 2018-stale codebase is a
  long-term liability. The community model files survive its decline; the
  library itself does not need to.
- **nnnoiseless** is a credible runner-up. Its Rust nature is the only reason it
  drops below upstream Xiph — adding Rust to our build chain has cost we don't
  need to take.
- **DeepFilterNet** trades quality for footprint and a stalling maintenance
  signal. Best technical denoiser of the set on speech, but wrong primary engine.

---

## 4. Recommended Path: Option B (P/Invoke Xiph upstream)

The matrix is consistent with the gut-check: the same stock library that we have
been calling indirectly through RNNoise.NET *already supports* the file-based
model loading we need. The work is the wrapper, not the dependency. We get:

- **Direct control of the rnnoise DLL build** — we pick the commit, we know the
  build flags (in particular `-DUSE_WEIGHTS_FILE` if we want to omit the embedded
  default), and we can refresh independently of any third party's wrapper cadence.
- **License headroom** — BSD-3-Clause adds at most an attribution line to our
  third-party notice; same compatibility class as the libopus and PortAudio
  notices we already carry.
- **Tier 1/2/3 packaging fit** — the binary blob format is small (KB to single MB),
  shipping a default model with the installer (Tier 1), an optional pack from
  data.jjflexible.radio (Tier 2), and a `%AppData%\JJFlexRadio\rnn-models\`
  drop-in folder (Tier 3) all fall out naturally.
- **Cross-platform readiness** — when SwiftUI Mac comes online, the same C
  library cross-compiles cleanly; we replace the P/Invoke marshalling layer with
  the Swift equivalent rather than re-shopping a denoiser.

---

## 5. Implementation Outline

### 5.1 Native dependency packaging

- Add `rnnoise.dll` (x64) and `rnnoise.dll` (x86) to `runtimes/win-x64/native/`
  and `runtimes/win-x86/native/`, alongside `portaudio.dll` and `libopus.dll`.
  `NativeLoader.vb` already resolves architecture-specific DLLs at runtime;
  rnnoise just slots in.
- Build process: stand up an internal `tools/build-rnnoise/` directory with
  build scripts targeting MSVC for both archs. Pin to a specific upstream
  commit hash; document it in `MIGRATION.md` style. Build with
  `-DUSE_WEIGHTS_FILE` or without per the model-bundling decision (see 5.3).
- Strip exports to the public RNNoise API only.

### 5.2 Wrapper class shape

Replace the sealed `RNNoise.NET` dependency with a project-internal wrapper
under `JJPortaudio/` or a new `JJDsp/` library:

```csharp
public sealed class RnnoiseDenoiser : IDisposable {
    public RnnoiseDenoiser(string modelPath = null);   // null => embedded default
    public static RnnnoiseModel LoadModel(string path);   // shared models across instances
    public RnnoiseDenoiser(RnnnoiseModel sharedModel);
    public int FrameSize { get; }                          // 480
    public float ProcessFrame(Span<float> output, ReadOnlySpan<float> input);
    public void Dispose();
}

internal static class Native {
    [DllImport("rnnoise")]
    internal static extern IntPtr rnnoise_model_from_filename(string filename);
    [DllImport("rnnoise")]
    internal static extern IntPtr rnnoise_create(IntPtr model);
    [DllImport("rnnoise")]
    internal static extern float rnnoise_process_frame(IntPtr st, float[] outBuf, float[] inBuf);
    // ...remaining ~10 entries
}
```

Total budget: ~300-500 LOC including frame-buffering helper to match the
arbitrary-chunk interface RNNoise.NET currently provides.

### 5.3 Model file format and discovery

- **File extension:** Adopt `.rnnn` for our distribution. Internally these are
  the upstream binary weights blob (`weights_blob.bin`-shaped bytes). The
  extension is ours to brand; users get a recognizable, ham-radio-flavored token.
- **Discovery order at runtime:**
  1. User-selected path (settings dialog, per-radio config).
  2. `%AppData%\JJFlexRadio\rnn-models\*.rnnn` (Tier 3 user drops).
  3. `%ProgramFiles%\JJFlexRadio\rnn-models\*.rnnn` (Tier 1/2 installed).
  4. Embedded default if no file found (only if we build *without*
     `USE_WEIGHTS_FILE`).
- **Recommendation on `USE_WEIGHTS_FILE`:** Build *with* the embedded default
  baked in. Keeps "no model installed" from being a hard failure mode and
  matches the friction-tax principle (app does the right thing without user
  setup).
- **Model metadata sidecar:** Pair each `.rnnn` with a tiny `.json` carrying
  display name, author, training-noise-class label, and a free-text description.
  Drives the user-facing model picker without parsing weights.

### 5.4 Cross-platform considerations

- macOS (future SwiftUI client): same C library compiles to a `.dylib` cleanly;
  the P/Invoke layer is replaced by Swift's C interop. Model files are bit-identical.
- Linux (not currently a target, but trivially in reach): `.so` build from the
  same source tree.
- The `.rnnn` file format is machine-endian per upstream's note. All target
  platforms are little-endian (x86_64, arm64, Apple Silicon), so portability is
  not an issue in practice; document the constraint.

### 5.5 Migration from RNNoise.NET

- Hot-swap `Denoiser` → `RnnoiseDenoiser` in the RX audio pipeline (Sprint 25
  Phase 20 wiring).
- Remove `YellowDogMan.RRNoise.NET` NuGet reference; remove its bundled
  `rnnoise.dll` from any output paths it injected; add ours.
- Keep RNNoise.NET-equivalent behavior under the hood when no custom model is
  selected — the user sees no regression.
- Verify VITA-49 frame timing unchanged (still 480 samples / 10 ms @ 48 kHz).

### 5.6 Effort estimate (time-honestly: scoped, not clock-honestly)

This is not a question of human hours; it is a question of where the work lives.

- Native build pipeline (one-time): a few sessions; tools/build-rnnoise scripts,
  CI consideration deferred.
- P/Invoke wrapper: one focused session.
- Model picker UI + per-radio config wiring: one focused session.
- Tier 2 distribution (data.jjflexible.radio model pack publishing): one
  focused session, gated on Data Provider being live (already on the
  foundation-phase critical path).

---

## 6. Risks and Open Questions

1. **Existing community models (rnnoise-models repo, 2018) may not load
   directly into upstream's modern blob format.** If the binary format
   diverged, we need either (a) a small conversion utility on our build
   pipeline or (b) accept that the Tier 1/2 catalogue starts with newly
   trained models. Investigate before committing the model picker UI.

2. **Building rnnoise on Windows from source.** Upstream is autotools-only.
   Yellow-Dog-Man's rnnoise fork has Windows build scripts but is downstream
   of upstream; we may want to inherit those scripts but rebase onto current
   upstream. Cost is one build-engineering session — trivial for a project
   already shipping native libopus and PortAudio.

3. **`USE_WEIGHTS_FILE` build flag has had stability issues** (PR #263 and
   adjacent). Verify our chosen upstream commit builds cleanly with the
   flag combination we want.

4. **Performance at the FlexRadio sample rate path.** Currently we run rnnoise
   in the RX pipeline post-Opus decode at 24 kHz upsampled to 48 kHz. Custom
   models may have different CPU profiles than the embedded default. Smoke-test
   on the lowest-spec target hardware (older Intel laptops favored by the
   accessibility-focused user base) before publishing Tier 2.

5. **DeepFilterNet stall is a leading indicator, not a stop sign.** If the
   project picks back up in 2026 with renewed activity, we should re-evaluate
   it as a *secondary* engine: user picks RNNoise (fast, light) or DeepFilterNet
   (heavier, better on broadband speech-band noise). The wrapper architecture
   we ship for RNNoise should not preclude adding a sibling DeepFilterNet
   wrapper later. Keep the audio-pipeline interface engine-agnostic
   (`IDenoiserStage` or similar).

6. **License notice maintenance.** BSD-3-Clause requires retention of copyright
   and disclaimer in distributions. Add to the third-party notices file
   alongside libopus and PortAudio. Already a known-good pattern for this project.

7. **Code-signing implications.** Once the code-signing cert milestone lands,
   the rnnoise.dll we ship should be signed alongside the main exe. Standard
   Authenticode treatment; no special handling needed.

8. **Don and Justin already have RNNoise active in their RX pipelines.** Any
   transition needs to default-no-op for them (embedded model = today's
   behavior). User-loadable models become a *new opt-in*, not a forced change.
   Aligns with the flexibility principle.

---

## Appendix A: Source list

- Xiph RNNoise repo and README (https://github.com/xiph/rnnoise) — confirmed
  v0.2 release April 2024, model-from-file APIs, BSD-3-Clause.
- Xiph rnnoise.h header (public C API) — full function list captured above.
- Yellow-Dog-Man / RNNoise.Net (https://github.com/Yellow-Dog-Man/RNNoise.Net)
  — confirmed parameterless `Denoiser()` constructor passing IntPtr.Zero,
  no model-loading overload.
- GregorR / rnnoise-nu (https://github.com/GregorR/rnnoise-nu) — confirmed
  last commit September 2018; companion rnnoise-models likewise.
- jneem / nnnoiseless (https://github.com/jneem/nnnoiseless) — confirmed
  active through Dec 2025, Rust port with C API.
- Rikorose / DeepFilterNet (https://github.com/Rikorose/DeepFilterNet) —
  confirmed v0.5.6 August 2024, last commit October 2024, dual MIT/Apache-2.0,
  C API in libDF/src/capi.rs.
