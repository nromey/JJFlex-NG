---
type: design pull doc — RNN + spectral subtraction controls + user-loadable models
review needed: read, annotate with `**** ` per for-noel protocol
priority: high — addresses your concert-night observation that RNN defaults are weak and SUB has no capture path
draft author: Claude
date: 2026-05-03
---

# DSP controls — RNN + Spectral Subtraction + user-loadable models

## Why this doc exists

You raised this on 2026-05-03 morning:

> *"We need to add noise reduction and subtractive spectrum knobs and configuration. Right now, defaults are weak at best and subtractive doesn't work because we have no recordings and no way to record. Probably need a plan to implement that."*

After spelunking the Phase 20 audio pipeline, the situation is more nuanced than "defaults are weak / SUB doesn't work." **A lot is already implemented.** What's missing is the user-facing surface and the user-extensible RNN model selection. This doc inventories the actual gap and proposes the plan.

## Current state — what's already in the code

Quoting from `JJFlexWpf/RxAudioPipeline.cs` (Phase 20, 2026-04-13). The pipeline chains:

```
Decoded PCM → SpectralSubtraction → RNNoise → PortAudio queue
```

Both stages are implemented. Their public knobs (already wired through `RxAudioPipeline`):

**RNNoise (`NoiseReductionProvider.cs`):**

- `RnnEnabled` (on/off)
- `RnnStrength` (0.0-1.0 wet/dry mix, default 0.8)
- `RnnAutoDisableNonVoice` (auto-off in CW/digital modes, default true)
- Auto-mode-tracked via `SetCurrentMode()`

**Spectral Subtraction (`SpectralSubtractionProvider.cs`):**

- `SpectralEnabled` (on/off)
- `SpectralStrength` (0.0-1.0 aggressiveness, default 0.7)
- `SpectralFloor` (0.0-1.0 musical-noise prevention, default 0.02)
- `StartNoiseSampling(durationSeconds=2)` — **default 2s, NOT 3 per your decision**
- `CancelNoiseSampling()`
- `LoadNoiseProfile(filePath)`
- `SaveNoiseProfile(filePath, name, band, antenna)`
- `ClearNoiseProfile()`
- Live state: `IsNoiseSampling`, `NoiseSamplingProgress`, `HasNoiseProfile`, `NoiseProfileName`

**UI surface (`Controls/ScreenFieldsPanel.xaml.cs`):**

The DspContent in ScreenFields has exactly two controls — `_pcRnnCheck` and `_pcSpectralCheck`. Both are simple on/off toggles. **There is no UI for:**

- RNN strength knob (the wet/dry mix is hardcoded to 0.8 default; user can't adjust)
- RNN auto-disable-non-voice toggle (always on by default; not user-controllable)
- SUB strength knob (default 0.7; not adjustable)
- SUB floor knob (default 0.02; not adjustable)
- **SUB noise sampling/capture trigger** — no hotkey (`KeyCommands.cs` doesn't reference `StartNoiseSampling`), no button, no menu item
- Profile load/save/clear
- Profile picker (which profile is currently loaded)

**This is the gap.** The DSP engine is roughly 80% built; the user-facing surface is roughly 20% built. Your "weak defaults / SUB doesn't work" complaint is correct from a user's perspective: the user can't reach the controls that would make it work.

## Strategic positioning frame

Per `project_rm_noise_strategic_positioning.md` (saved 2026-05-03):

JJF's competitive position vs Flex's fixed-model NR and vs RM Noise's server-side ML — JJF can deliver RM-Noise-quality results without server dependency by combining user-extensible RNN model selection + user-captured SUB profiles, all client-side. This pull doc operationalizes that positioning.

**Lead the changelog with this when it ships:** *"JJ Flexible's noise reduction now lets you choose your model — bundled default plus a downloadable models pack plus drop-in support for community-trained models. And spectral subtraction now does what its name says: capture the noise from your shack, subtract it from your audio. No server, no subscription, runs entirely on your computer."*

## Scope inventory

### Pillar 1 — RNN improvements

#### 1a. RNN knobs in UI

Surface the existing properties in DspContent (or in the Audio expander):

- Strength slider (0-100%, default 80%, per-Mode persistence optional)
- Auto-disable-non-voice toggle (per-mode behavior)

Both controls already-functional under the hood; just need XAML + view-model bindings + accessibility metadata + KeyCommands hotkey for strength up/down.

**Engineering scope:** ~150 LOC across XAML and view-model code.

#### 1b. RNN model selection

The current `NoiseReductionProvider` uses `RNNoise.NET` (the YellowDogMan wrapper, per `project_dsp_model_pack_distribution.md` and `nr-dsp-research.md`). The `Denoiser()` constructor takes no model file path — the wrapper hides which model it loads internally. **To support user-loadable models, we need a different RNN integration path.**

Three options:

- **Option A — Find an RNNoise.NET fork or alternative wrapper that supports model file loading.** Lowest effort if one exists. May not exist as a NuGet package.
- **Option B — P/Invoke RNNoise's native API directly.** RNNoise (the C library from Xiph) supports `rnnoise_create(model)` where `model` is a `RNNModel*` loaded from a `.rnnn` file. Higher engineering cost: P/Invoke layer, marshaling, native DLL bundling per architecture (x64/x86). But gives us full control.
- **Option C — Switch to a different denoiser library that supports model selection.** rnnoise-nu or DeepFilterNet are candidates. Each has its own API and license; would need evaluation.

My lean: **Option B** is the most flexible. RNNoise's C API is small and stable. We'd ship `rnnoise.dll` (x64) and `rnnoise.dll` (x86) under `runtimes/` per the existing native-DLL pattern. Native interop work is bounded.

**Engineering scope:** ~300-500 LOC for the P/Invoke layer + wrapper class. Plus build-system updates to bundle native DLLs.

#### 1c. Three-tier model distribution

Per `project_dsp_model_pack_distribution.md`:

- **Tier 1 — Bundled default.** Ship a curated default model in JJF installer. RNNoise standard model (~85KB) is the obvious starting point; could ship better community-trained alternatives if research surfaces them.
- **Tier 2 — Optional model pack from `data.jjflexible.radio`.** Curated bundle of additional models. JJF UI exposes "Get more models..." action that downloads + installs the pack. Updates via chained-updater pattern.
- **Tier 3 — User-supplied models.** Drop-in folder under `%AppData%\JJFlexRadio\models\` (or similar). JJF model picker auto-discovers files and offers them.

**Engineering scope:** ~200-400 LOC for model-folder discovery, model-picker UI, download-and-extract flow for Tier 2. Model pack hosting is on `nromey/jjf-data` (per `project_jjf_data_repo.md`); R2 zero-egress (per `project_data_provider_hosting.md`) means free at scale.

#### 1d. Web research — published RNN models worth bundling

Open research item: spelunk the RNNoise community for published custom-trained models. Candidates I'm aware of:

- **rnnoise-nu** — fork with multiple custom-trained models (voice in noise, music suppression, etc.). License compatible.
- **GregorR's rnnoise-models** — collection on GitHub.
- **Stock RNNoise** from Xiph — the default that ships in RNNoise.NET. Speech-trained on a generic corpus.

I haven't done the deep survey yet. Should be ~30 minutes web research + license check + listening tests when implementation opens. Result: ship 2-3 curated models in the Tier 2 pack.

### Pillar 2 — SUB user-facing surface

The engine is built. The UI isn't. Items:

#### 2a. Update capture default + range

Per `project_sub_capture_window_decision.md`:

- Default: 2 seconds → **3 seconds**
- Range: 1-3 seconds → **1-5 seconds**

Code change: `RxAudioPipeline.StartNoiseSampling(int durationSeconds = 2)` → `= 3` and update the SpectralSubtractionProvider's range validation if it has one.

**Engineering scope:** ~5 LOC.

#### 2b. SUB capture UX

Add to JJF UI:

- **"Capture noise profile" button** — visible in the DSP expander or Audio Workshop. When pressed, plays a confirmation tone, samples for the duration setting, plays a completion tone, displays "Profile captured ([N] seconds)". If sampling is canceled or the audio is degenerate (clipping, silence), surface the error.
- **Hotkey** — wire `StartNoiseSampling` to a keystroke (suggestion: `Ctrl+Shift+N` or similar — needs the keyboard-audit checklist per CLAUDE.md). Per `project_short_action_labels_vocabulary.md`, command name should be something like "capture spectral noise profile" with short label "capture noise."
- **Capture-duration setting** — numeric field (1.0-5.0, 0.1s granularity, default 3.0). Lives under "advanced" expander since most users won't touch it.

**Engineering scope:** ~200-300 LOC across XAML + view-model + KeyCommands.

#### 2c. SUB profile management UX

- **Profile picker dropdown** — shows the currently loaded profile name (or "no profile"). Dropdown lists available profiles from the profiles folder.
- **Save profile button** — prompts for name + optional band/antenna metadata, saves to file. Or saves directly with auto-generated name (`<callsign>-<band>-<date>.jjnr` per `project_noise_profile_sharing.md` line 21 conventions).
- **Load profile** — click in the dropdown.
- **Clear profile** — explicit clear action.
- **Profile metadata display** — when a profile is loaded, show its band/antenna metadata so the user knows what context it was captured in.

**Engineering scope:** ~250-400 LOC across XAML + view-model.

#### 2d. SUB knobs in UI

- Strength slider (0-100%, default 70%)
- Floor slider (0-100%, default 2% — needs labeled unit since "0.02" is opaque)

**Engineering scope:** ~100 LOC.

### Pillar 3 — Model distribution mechanism (Tier 2)

#### 3a. Model pack discovery + download

JFF queries `data.jjflexible.radio/models/manifest.json` (or similar) for available model packs. UI exposes "Get more models..." action. Selecting "install" downloads the pack zip, extracts to the models folder, refreshes the picker.

**Engineering scope:** ~300-500 LOC. Uses existing networking patterns (HttpClient, JsonSerializer), no new infrastructure.

#### 3b. Chained-updater integration

Model packs become a "second-instance candidate" per `project_chained_updater_pattern.md`. When JJF performs its update check (against the data provider), it also checks for model-pack updates. If a newer pack version is available, prompt the user to download.

**Engineering scope:** ~100 LOC integration into existing update flow (assumes update flow exists by the time this lands; otherwise comes with the updater work).

## Out of scope for this pull doc

- **In-app RNN training.** GPU/time prohibitive. Per your "Justin lesson" — per-noise-type training is a misunderstanding of how RNNoise generalizes; avoid the bad pattern. Pre-trained models only.
- **Sidecar training-audio capture tool.** A separate utility that lets power users record audio for community RNN training. Discussed in chat 2026-05-03; deferred to its own pull doc. Possibly unnecessary if community-trained models cover the need.
- **Coordinated radio-side + app-side NR.** A "Noise Reduction: Off / Light / Aggressive" preset that intelligently composes Flex's radio-side NR with JJF's app-side pipeline. Genuinely useful for accessibility (one knob does the right thing) but more design surface than this pull doc covers. Sprint 30+ candidate.
- **VST3 plugin hosting.** Backlog item from `nr-dsp-research.md`.
- **Software AGC, auto-notch, etc.** Other DSP features in `nr-dsp-research.md`. Each merits its own scoping when prioritized.

## Sequencing + LOC estimates

Total estimated LOC: ~1500-2500 across roughly 12-15 files (XAML, view-models, KeyCommands, p/invoke layer, model discovery, Tier 2 pack). Mostly UI work; engine is already there. Native-RNNoise interop is the largest single new piece.

Suggested phasing:

- **Phase 1** — Pillar 2 (SUB UX) + Pillar 1a (RNN knobs in UI). Ships the user-facing controls for what's already implemented. **Highest leverage** because it makes existing functionality reachable. Could land in 4.2.x.
- **Phase 2** — Pillar 1b (RNN model selection via P/Invoke) + Pillar 1c (model folder + picker). Adds the model-extensibility differentiator. Mid-effort engineering.
- **Phase 3** — Pillar 3 (model pack distribution + chained-updater integration). Ties into the update infrastructure.
- **Phase 4** — Web research → curate the model pack → ship it.

`build-now-ship-later` per `project_build_now_ship_later.md`: spin up `track/dsp-controls` after your ACK on this doc. Phases 1-2 land independently; Phase 3 has a dependency on update infrastructure being ready.

## Open questions for your read

1. **RNN integration path — Option A/B/C?** I lean Option B (P/Invoke native RNNoise) for full control. You comfortable with that engineering scope, or prefer Option A if a wrapper exists?
2. **Default RNN strength.** Currently 0.8. You said "defaults are weak" — meaning the model is too gentle, OR the strength wet/dry mix is wrong, OR both? If just the model, Phase 2 fixes it (better model). If the strength default, easy adjust to 1.0 (full processing). My lean: keep strength default 0.8 but ship a better default model in Phase 2 — that's where "weak" actually fixes.
3. **SUB profile metadata mandatory or optional?** When user saves a profile, should band + antenna be required fields, or just suggested? My lean: optional but strongly suggested (default to current radio's reported band; user can tab through to skip).
4. **Capture hotkey — proposing `Ctrl+Shift+N`.** Per CLAUDE.md keyboard-audit rule, this needs to be checked against existing bindings. OK to research and propose a final binding in implementation, or want to pick now?
5. **Web research timing for community RNN models.** Now (before implementation track opens), or during Phase 2? My lean: now, ~30 min, results inform what we ship in Tier 1 default + Tier 2 pack.
6. **Phase 1 priority.** Highest leverage, but does it ship in 4.2.0 or 4.2.x? I'd say 4.2.x — 4.2.0 is gated on the FlexLib 4.2.18 fix and shouldn't accumulate scope. But if a tester (Don especially) is bottlenecked on missing SUB UX, sliding it forward may be worth it.

## Cross-references

- `project_rm_noise_strategic_positioning.md` — strategic frame
- `project_sub_capture_window_decision.md` — 1-5s/default 3s
- `project_dsp_model_pack_distribution.md` — three-tier model distribution
- `project_no_silent_phone_home.md` — runtime-local-only constraint
- `project_jjflex_data_provider.md` + `project_jjf_data_repo.md` — Tier 2 hosting
- `project_data_provider_hosting.md` — R2 economics
- `project_chained_updater_pattern.md` — Tier 2 update mechanism
- `project_friction_tax_principle.md` — default-install-works
- `project_short_action_labels_vocabulary.md` — capture command label
- `project_noise_profile_sharing.md` — profile-sharing community vision
- `docs/planning/nr-dsp-research.md` — Phase 20 antecedent
- `JJFlexWpf/RxAudioPipeline.cs` — existing pipeline
- `JJFlexWpf/NoiseReductionProvider.cs` — existing RNNoise integration
- `JJFlexWpf/SpectralSubtractionProvider.cs` — existing SUB implementation
- `JJFlexWpf/Controls/ScreenFieldsPanel.xaml.cs` — existing UI surface (the gap)
