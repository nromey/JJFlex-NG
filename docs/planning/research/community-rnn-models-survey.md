# Community RNNoise Models Survey

**Status:** decision-quality input for Tier 1 / Tier 2 model pack contents
**Context:** Option B locked (P/Invoke Xiph upstream RNNoise v0.2). Model file format
is the standard binary "machine endian" weights produced by Xiph's training pipeline,
loaded via `rnnoise_model_from_file()`. The widely circulated `.rnnn` files (bd, cb,
lq, mp, sh, std) are the same format under a different extension — they predate the
format change in v0.2, so any pre-v0.2 community model needs a re-dump or compatibility
verification before it can be loaded by an Option-B build.
**Date:** 2026-05-04
**Author:** research agent

---

## 1. Recommendation

**Tier 1 (bundled default):** Ship Xiph upstream's stock v0.2 model. It is the only
candidate trained on a current, publicly documented corpus, BSD-3-Clause licensed,
and guaranteed format-compatible with the v0.2 runtime we are linking. Community
ham-specific models do not exist, and the most widely deployed alternative
(GregorR's `bd`/`cb`) is from 2018, format-aged, and trained on Shakespeare plus
laugh tracks — a worse generic fit than the upstream model, not a better one.

**Tier 2 (curated pack):** Three GregorR models — `bd` (beguiling-drafter),
`lq` (leavened-quisling), and `sh` (somnolent-hogwash) — covering "voice in quiet
shack," "voice in noisy shack," and "speech-only" respectively. All three need
format re-validation against v0.2 before pack inclusion; if re-validation fails,
Tier 2 should ship empty at 4.2.0 and revisit when an updated community trainer
emerges, rather than ship known-broken artifacts.

---

## 2. Tier 1 candidate analysis

### Xiph upstream v0.2 stock model

- **Source:** https://github.com/xiph/rnnoise (release v0.2, 2024-04-15; mirror at gitlab.xiph.org/xiph/rnnoise)
- **License:** BSD-3-Clause (compatible with MIT)
- **Training corpus:** Publicly available datasets only — clean speech from Xiph's
  concatenated `tts_speech_48k.sw` (TTS-derived multilingual speech), plus
  background and foreground noise files from `media.xiph.org/rnnoise/data/`,
  augmented with optional room impulse responses.
- **File size:** ~85 KB historical for v0.1.x `.rnnn`; v0.2 binary weights blob
  format is similar order of magnitude (low hundreds of KB at most). The actual
  v0.2 release ships compiled-in weights by default rather than as a separate
  blob — to ship it as a Tier-1 file we either dump it via `dump_weights_blob`
  or extract the compiled weights at build time.
- **Strengths:** Trained on the most diverse and most recent corpus of any
  candidate. SSE4.1/AVX2 optimizations and runtime CPU detection were tuned
  against this exact model. It is the model the upstream maintainers actively
  test against, so future bug fixes and runtime improvements stay aligned.
- **Weaknesses for ham audio:** Trained on conversational TTS-style speech, not
  HF-radio-conditioned voice. QRZ forum sentiment (multiple users) is that
  RNNoise "was never trained for this type of noise" — atmospheric noise,
  lightning crashes, and HF RFI are out of distribution. SSB voice that is
  already band-limited and AGC-compressed may also confuse the model. These
  caveats apply to every RNNoise model on the market, not just Xiph's — there
  is no ham-trained RNNoise model in public circulation.
- **Maintenance:** Active. v0.2 is the current release; the Xiph project is the
  reference implementation we are linking against.
- **Caveats:** Two operational concerns. First, the v0.2 format is not
  byte-compatible with pre-v0.2 `.rnnn` files; we should publish a short
  developer note explaining which `.rnnn` files load and which don't. Second,
  upstream's guidance states "exact results vary based on data mixing, training
  duration, and random seeds" — meaning even the upstream maintainers don't
  publish reproducible quality benchmarks. Quality is "what shipped."

### Why not a community alternative for Tier 1

- No public RNNoise model is trained on ham radio audio. None.
- The most popular community model (`bd.rnnn` from GregorR) is from 2018,
  trained on Shakespeare's Hamlet plus a Czech UN Human Rights Declaration plus
  15 laugh samples from Freesound. That corpus is not better-fitted to ham
  voice than Xiph's TTS corpus; it is more idiosyncratic.
- A 2018 model also predates the v0.2 binary format change, so it requires
  re-dumping or compatibility shimming before it loads at all.
- The combination "less-current corpus + format-shim risk + identical
  HF-out-of-distribution problem" makes any 2018 community model a worse Tier-1
  default than upstream's current stock.

---

## 3. Tier 2 candidate analysis

All three Tier 2 candidates come from GregorR/rnnoise-models (the canonical
community collection). Same-day mirror exists at richardpl/arnndn-models for
FFmpeg users. All six GregorR models share the same overall corpus shape; what
differs is the signal-vs-noise mix the model was tuned for. Filenames and
abbreviations: `bd` = beguiling-drafter, `cb` = conjoined-burgers, `lq` =
leavened-quisling, `mp` = marathon-prescription, `sh` = somnolent-hogwash. (`orig`
appeared in early documentation but is not present as a separate model file; it
maps to the upstream Xiph default.)

Ranked by ham-radio-operator value:

### 3.1 `bd.rnnn` — beguiling-drafter ("quiet shack voice")

- **Intent:** "Voice in a reasonable recording environment. Fans, AC, computers,
  etc."
- **Signal corpus:** Seven literary/UN-declaration voice samples (Hamlet, UN
  Human Rights in Chinese and Czech, Hunting of the Snark, Teacup Club, war
  letters, McGill TSP speech) plus 15 cough samples and 15 laugh samples from
  Freesound. The "voice" framing includes non-speech vocalizations.
- **Noise corpus:** rnnoise_contributions, restricted to "other" and "none"
  categories.
- **File size:** 300 KB
- **Operator-facing description:** "For a typical ham shack with computer fans,
  HVAC, and the occasional cough or chair creak. Preserves voice character
  including non-speech sounds. Conservative on impulse noise."
- **Caveats:** Smaller noise palette than `lq`; will not aggressively chase
  loud transient noise. Older corpus (2018), no HF RFI in training data.

### 3.2 `lq.rnnn` — leavened-quisling ("noisy shack voice")

- **Intent:** "Voice in a noisy recording environment."
- **Signal corpus:** Same seven voice files as `bd`, plus 15 cough and 13 laugh
  samples.
- **Noise corpus:** Full rnnoise_contributions (broader than `bd`).
- **File size:** 297 KB
- **Operator-facing description:** "For shacks with substantial background noise
  — fans plus appliances plus household activity. More aggressive than the
  default; may dampen quiet speech segments more than `bd` does."
- **Caveats:** Aggressiveness is a tradeoff. Operators chasing weak DX may
  prefer Xiph stock or `bd`. Older corpus, no HF-specific noise.

### 3.3 `sh.rnnn` — somnolent-hogwash ("speech-only, quiet")

- **Intent:** "Speech in a reasonable recording environment. Fans, AC,
  computers, etc."
- **Signal corpus:** Voice files only — no cough/laugh samples. Distinguishes
  "speech" from "voice" by excluding non-speech vocalizations.
- **Noise corpus:** rnnoise_contributions "other" and "none" categories.
- **File size:** 298 KB
- **Operator-facing description:** "Speech-tuned variant for nets, traffic
  handling, and scheduled QSOs where you want maximum speech intelligibility
  and don't care about preserving non-speech sounds. May treat coughs or laughs
  as noise to be removed."
- **Caveats:** Will likely suppress non-speech voice elements; not a good fit
  for casual ragchew where natural conversational sounds matter. Same 2018
  corpus and HF caveats.

### 3.4 Optional fourth slot — `cb.rnnn` (conjoined-burgers)

- **Intent:** Same general bucket as `bd` but for "recording" use rather than
  live voice — slightly different signal/noise mix in training.
- **File size:** 300 KB
- **Why it ranks below the three above:** Operator-facing differentiation from
  `bd` is hard to communicate in a UI. Recommend holding it for a v2 pack
  unless listening tests show distinct behavior.

### 3.5 Optional fifth slot — `mp.rnnn` (marathon-prescription)

- **Intent:** "General use in a noisy recording environment" — broadest bucket,
  general signal plus general noise. Includes five royalty-free music tracks
  from Incompetech in the training mix.
- **File size:** 297 KB
- **Why it ranks lowest:** Music-aware training is interesting on paper, but
  ham operators rarely want music-passing behavior — they want voice-passing
  behavior. The music tracks may also weaken voice-only tuning. Recommend
  excluding from Tier 2 unless an operator explicitly asks.

### Tier 2 license note

GregorR's README states: "With the exception of the tools/ directory and this
file, none of this work is creative and thus none of it is subject to copyright."
This is a deliberate copyright disclaimer rather than a recognized open-source
license. For Tier 2 distribution we should treat it as public domain by author
disclaimer, document the source in the pack manifest, and link upstream. This
is workable for a download pack; it would be borderline for Tier 1 because the
disclaimer relies on a US-specific copyright theory that does not bind cleanly
internationally — another reason to keep Tier 1 on the BSD-licensed Xiph stock.

---

## 4. Models considered but NOT recommended

- **rnnoise-nu (jagger2048 mirror)** — fetch returned 404; the original
  `GregorR/rnnoise-nu` repo is the live reference and is on BSD-3-Clause but
  ships no models of its own (it points to GregorR/rnnoise-models). No new
  models live in the `nu` lineage. Confirmed dead at 2018.
- **richardpl/arnndn-models** — repackages `bd/cb/lq/mp/sh/std` for FFmpeg's
  `arnndn` filter. Same models as GregorR, no new training. README is two
  sentences. Archived 2024-06-06. Use as a secondary mirror only; nothing
  unique here.
- **Hugging Face (niobures/RNNoise)** — also a repackage of the same six
  GregorR/Xiph files, plus ~170-byte README that adds no new information.
  Useful as a stable hosting endpoint but contains no novel models.
- **GregorR `mp.rnnn` and `cb.rnnn`** — covered in Tier 2 ranking above; held
  out of the recommended three because their differentiation is hard to surface
  in a UI for hams.
- **DeepFilterNet / SepFormer / other neural denoisers** — these surface in
  RNNoise comparison literature but they do not produce `.rnnn` format weights.
  Different runtimes, different model sizes, different licensing (DeepFilterNet
  is Apache-2.0 but ships ONNX, not RNNoise weights). Out of scope for the
  Option-B pipeline; revisit if a future Option-C pivot considers a multi-model
  framework.
- **noise-suppression-for-voice (werman) / le9endary RNNoise / OBS RNNoise
  plugin** — these are application wrappers around RNNoise, not new models.
  They ship the Xiph stock weights compiled in.
- **simon987 mirror of rnnoise-models** — direct copy of GregorR's set. Useful
  if GregorR's repo ever disappears, otherwise no new content.

---

## 5. Open questions (require listening tests to resolve)

- **Format compatibility of 2018 `.rnnn` files against v0.2 runtime.** The v0.2
  release notes state the model format changed since v0.1.1. The GregorR `.rnnn`
  files were dumped by the 2018-era pipeline. We need to confirm whether
  `rnnoise_model_from_file()` in v0.2 will load them as-is, will load them
  through a compat path, or will reject them entirely. If they're rejected, the
  Tier 2 pack ships empty at 4.2.0 — there is no other source of community
  models. Recommend a 30-minute spike: try loading `bd.rnnn` against the v0.2
  build and check the return value.
- **Subjective quality on actual ham audio.** None of the candidate models has
  been trained on or tested against HF SSB voice. The QRZ forum sentiment
  ranges from "works on AM" to "destroys weak signals." We need a small A/B
  listening matrix — Don's 6300 with a recorded noisy SSB clip, Justin's 8400
  ditto — running stock-vs-bd-vs-lq-vs-sh and capturing operator preference.
  Without that, "Tier 2 ranking" is a paper guess.
- **Default-aggressive vs default-conservative.** RNNoise has no `mix` knob; a
  given model is as aggressive as it is. If `lq` proves too aggressive on weak
  signals, our Tier 1 default might want to be `bd` rather than Xiph stock
  even given the format and corpus caveats. Listening test answers this.
- **Per-mode defaults.** Should SSB voice and AM broadcast pick different
  defaults? FlexLib exposes the operating mode; we could route to a different
  Tier 1 model per mode without surfacing the choice to the user. Worth a
  Sprint 30+ feature note.
- **Training a JJF-specific model.** Long-tail — if Tier 2 listening tests
  conclude "none of these are good enough for HF," the right answer is to
  collect a small ham-corpus and dump our own model via Xiph's training
  pipeline. That is post-foundation-phase scope, not a 4.2.0 deliverable, but
  the audio collection step (asking testers to capture 10 minutes of typical
  band noise each) could start any time.

---

## Sources

- [Xiph RNNoise repo (GitHub mirror)](https://github.com/xiph/rnnoise)
- [Xiph RNNoise v0.2 release](https://github.com/xiph/rnnoise/releases/tag/v0.2)
- [Xiph RNNoise canonical home (GitLab)](https://gitlab.xiph.org/xiph/rnnoise)
- [GregorR/rnnoise-models](https://github.com/GregorR/rnnoise-models)
- [GregorR/rnnoise-nu](https://github.com/GregorR/rnnoise-nu)
- [richardpl/arnndn-models](https://github.com/richardpl/arnndn-models)
- [niobures/RNNoise (Hugging Face mirror)](https://huggingface.co/niobures/RNNoise)
- [simon987 rnnoise-models mirror](https://git.simon987.net/simon987/rnnoise-models)
- [QRZ forums RNNoise plugin thread](https://forums.qrz.com/index.php?threads/rnnoise-ai-noise-suppression-plug-in.780773/)
- [TheModernHam noise-suppression survey](https://themodernham.com/noise-suppression-ai-and-ham-radio-a-perfect-mix/)
