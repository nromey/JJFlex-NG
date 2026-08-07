# CW Decode Survey — Options for RX Morse-to-Text in JJ Flexible Radio Access

Research pull, 2026-08-07. Fulfils the "CW decode roadmap" research item queued in the
2026-05-04 CW keying design pass (`memory/project_cw_keying_design.md`). Scope: survey
existing decoders (FLDIGI, MRP40, CW Skimmer, open-source and ML decoders, DIY DSP),
assess decode quality / latency / licensing / integration approach for a .NET 10
screen-reader-first Windows app, and recommend a roadmap.

Reminder of the TX/RX split: text-based CW **transmission** already works in JJF for
Flex radios. This document is entirely about the unbuilt RX path — turning received
CW audio into text.

## Where the audio comes from (what we already have)

The integration story is unusually good because the plumbing already exists:

- **Primary tap — the Opus RX stream.** `FlexBase` requests an `RXRemoteAudioStream`
  (48 kHz Opus), decodes it through `JJPortaudio`, and exposes a
  `PostDecodeProcessor` hook (`Radios/FlexBase.cs` around line 348/9205,
  `JJPortaudio/JJPortaudio/AudioStream.cs` line 37) that hands every decoded buffer
  to a `float[]` delegate on the `remoteAudioProc` thread. `JJFlexWpf/RxAudioPipeline.cs`
  already rides this hook for spectral subtraction + RNNoise. A CW decode engine is
  just another consumer on this tap. Crucially, this path works identically for
  **local and SmartLink** connections — Don can test over SmartLink on day one.
  Opus compression is not a problem for CW: a keyed sine tone survives Opus far
  better than it survives the ionosphere.
- **Secondary tap — FlexLib DAX streams, no DAX driver needed.** The vendored FlexLib
  has `DAXRXAudioStream` (`FlexLib_API/FlexLib/DAXRXAudioStream.cs`): uncompressed
  per-slice float audio over VITA-49, consumed in-process — the Windows DAX driver
  and SmartSDR are NOT required. The app stopped using DAX for its main audio path
  ("We're not using DAX any more", `FlexBase.cs:9180`), but as a decoder feed it has
  a unique property: it is **per-slice**. That enables decoding slice B while
  listening to slice A — monitor a CW sked frequency in text while ragchewing on
  phone. Limitations: LAN only (DAX does not traverse SmartLink), and it costs a DAX
  resource on MultiFlex radios.
- **Decoder placement relative to the existing DSP chain.** RNNoise is speech-trained
  and already auto-disables in CW mode (`NoiseReductionProvider.AutoDisableNonVoice`),
  so tapping post-pipeline is acceptable today, but the decoder should tap the **raw**
  buffer (before spectral subtraction / RNNoise) so future voice-oriented DSP can
  never eat the tone. Spectral subtraction in particular could help *or* hurt a
  tone detector; keep the feeds independent.
- **What the decoder must supply itself:** tone-frequency acquisition (the sidetone
  pitch depends on where the user tuned; expect roughly 300–900 Hz) and WPM tracking.
  The radio and FlexLib provide neither for received signals. Narrowing the RX filter
  (already a JJF control) is the user's best pre-filter and costs us nothing.

## Candidate survey

### FLDIGI's CW modem

- **What it is.** The CW mode inside fldigi (w1hkj), GPLv3+. Decoder architecture:
  decimated FFT/sinc low-pass filtering plus a matched filter sized to the tracked
  WPM, with threshold-based mark/space detection and an adaptive speed tracker
  (5–200 WPM claimed). AG1LE (Mauri, the fldigi CW contributor) has documented
  experiments layering Self-Organizing Map classification and a Bayesian decoder on
  top; the Bayesian work largely stayed experimental.
- **Decode quality.** Fair on clean-to-moderate signals with steady machine-sent
  keying. On noisy HF with QSB it is well behind a trained ear, and it is famously
  brittle on hand-sent/bug timing. e04's published comparison (below) places fldigi
  behind both CW Skimmer and their neural decoder on real off-air recordings.
- **Out-of-process use.** fldigi has a mature XML-RPC server (default
  127.0.0.1:7362); `text.get_rx` style calls let an external app poll decoded text,
  and pyFldigi/fldigi-shell prove the pattern. So yes, usable out-of-process without
  touching its GUI.
- **Why it still loses for us.** Three structural problems. First, audio routing:
  fldigi reads a sound *device*, so feeding it our RX stream means a virtual audio
  cable (VB-Cable) or the FlexRadio DAX driver — exactly the multi-app plumbing
  friction the friction-tax principle exists to kill. Second, accessibility: fldigi
  is FLTK-based, and FLTK exposes essentially nothing to UIA/MSAA — the fldigi UI is
  a black hole for NVDA/JAWS users, so any setup step that requires touching it is a
  non-starter for our audience. Third, it drags a whole digimode suite along for one
  modem. Extracting the GPL C++ modem source into our MIT app is license-incompatible
  for in-process linking; porting the *algorithm* (ideas are not copyrightable, but
  a line-by-line port is a derivative work) is legally murky and not worth it when
  better-quality options exist.

### MRP40 (Polar Electric)

- Commercial Windows sound-card decoder with a good reputation on weak/QRM signals
  among contest and QRQ operators. Closed source, 30-day trial, paid per-user unlock
  code licensed to a single user on a single computer.
- **No API, no text export interface, no automation surface.** It is a GUI end-user
  product. There is nothing to integrate against short of screen-scraping another
  process's window — fragile and absurd for a screen-reader app.
- Verdict: useful as a *quality benchmark* when we evaluate our own engine, not an
  integration candidate. (Same verdict applies to CWGet/DXsoft — shareware,
  GUI-only.)

### CW Skimmer (VE3NEA / Afreet)

- The quality ceiling for classical DSP: multi-channel Bayesian decoding across an
  entire band segment, very sensitive, $75 license after trial, closed source.
- Its only machine interface is a built-in Telnet *cluster* server that emits
  time-stamped callsign spots (frequency, WPM, SNR) — not a continuous decoded-text
  stream for one QSO. Great for a future "who is on the band" feature; wrong shape
  for "read me this QSO."
- Strategically interesting fact: its author, Alex Shovkoplyas VE3NEA, published
  DeepCW (below) under MIT — the classical-DSP champion moved to neural decoding
  himself.

### GGMorse (ggerganov, MIT)

- Small C/C++ real-time decoding library by Georgi Gerganov (of whisper.cpp fame).
  Automatic pitch detection (200–1200 Hz) and automatic speed detection (5–55 WPM),
  CMake build, clean C-style API surface, MIT license. Exactly the shape of thing we
  can P/Invoke the way we already P/Invoke RNNoise (`RNNoise.NET` precedent) and
  ship in `runtimes/win-x64|x86/native/` next to opus and portaudio.
- **Honest quality assessment:** it was built for acoustic device-to-device
  transmission, not HF. On real off-air recordings it trails fldigi and CW Skimmer
  in e04's comparison. The author himself flags the external-project API as rough
  (v0.1.0). Fine for a plumbing spike and clean signals; not the destination engine.

### Small .NET morse libraries (MorseSharp, dmorse, CWLibrary, AudioMorseDecoder…)

- Surveyed and dismissed for the decode path: they are overwhelmingly text↔code
  translators and audio *generators*. The few that attempt audio decode (e.g.
  AudioMorseDecoder with NAudio) are file-based hobby decoders, explicitly
  noise-intolerant, unmaintained. Nothing here beats writing our own DSP loop.

### ML decoders — the interesting shelf

- **DeepCW (VE3NEA, MIT).** Training + validation pipeline in Jupyter/Keras
  (`data_generation`, `model` with `weights.h5` + `training_settings.py`,
  `validation`). It is a research repo, not a product — but it is an **MIT-licensed,
  reproducible training recipe from the CW Skimmer author**. A Keras model exports
  to ONNX mechanically. This is the seed for a license-clean model of our own.
- **e04's DeepCW web decoder + deepcw-engine.** Browser app
  (web-deep-cw-decoder / cw.e04.workers.dev) with a separately published inference
  engine: `model.onnx` + JSON metadata, **input = 3200 Hz mono PCM**, Python and
  Node examples on onnxruntime. Claims 0.00% character error from 0 down to −4 dB
  SNR at all tested speeds with graceful degradation below, plus published
  comparisons showing it beating CW Skimmer, fldigi, and ggmorse on real YouTube
  off-air recordings. Multi-channel and real-time in a browser — so compute cost is
  trivial for a desktop CPU. **License: AGPL-3.0-only** on the engine (the web repo
  shows no license grant at all). AGPL is fine out-of-process with published source,
  but in-process linking into MIT JJFlex is off the table, and "no license" on the
  web repo means we treat that code as all-rights-reserved.
- **Research lineage** confirming the approach is sound, not a one-off: AG1LE's
  decade of Bayesian/ML Morse posts, a 2024 NATO STO paper hitting ~1.7% character
  error with RNNs on audio, several CNN/LSTM spectrogram decoders on GitHub. The
  consistent finding: learned decoders dominate classical DSP precisely in the
  regime we care about — QSB fades, QRM, and sloppy hand keying.
- **Why this matters for us specifically:** ONNX Runtime
  (`Microsoft.ML.OnnxRuntime`, MIT, first-class C# API, CPU-only is plenty for a
  3.2 kHz single-channel model) makes in-process neural inference a NuGet reference,
  not an adventure. We already ship neural inference on the RX path (RNNoise). The
  DSP model-pack distribution scheme (`memory/project_dsp_model_pack_distribution.md`)
  already contemplates shipping downloadable model files.

### DIY DSP (Goertzel/FFT + adaptive WPM)

- The classical recipe: decimate the 48 kHz tap to ~8 kHz; run a Goertzel bank (or
  small sliding FFT) at and around the detected tone; compare center vs. adjacent
  bins to reject broadband noise; envelope the magnitude; adaptive threshold with
  hysteresis for mark/space; classify element and gap durations against a tracked
  dit clock (running average or simple k-means over recent marks); emit characters
  on inter-character gaps and spaces on word gaps. The k3ng keyer and dozens of
  Arduino projects prove the whole thing fits in a few hundred lines and runs in
  microseconds per block — on a desktop CPU it is free.
- **Quality ceiling is well documented:** clean machine-sent CW at stable speed
  decodes near-perfectly; noisy HF yields "a few clear words in a pile of
  gibberish"; QSB flutters the threshold; hand keying wanders the WPM tracker (one
  documented build tracked a 40 WPM signal that drifted to a perceived 52 under
  noise). This is a fair-weather decoder. But it is 100% ours, MIT-clean, zero
  dependencies, and forces us to build every piece of plumbing and UX the real
  engine will need.

## Licensing summary

- JJFlexRadio is MIT (`LICENSE.txt`). In-process code must be MIT-compatible.
- Compatible in-process: our own DSP (obviously), ggmorse (MIT), ONNX Runtime (MIT),
  VE3NEA DeepCW pipeline and anything we train from it (MIT), RNNoise-style native
  wrappers (BSD).
- Out-of-process only: fldigi (GPLv3, XML-RPC boundary is fine), e04 deepcw-engine
  (AGPL-3.0 — separate process + provide source; also means we cannot fold its
  weights into the app binary, since the model file itself carries the license).
- Not integrable at any boundary: MRP40, CWGet (closed, no interface); CW Skimmer
  (closed; Telnet spots only, usable later for a band-spots feature under its normal
  paid license).

## Latency

- Morse itself sets the floor: a character is only decidable after an
  inter-character gap (3 dits) and a word after 7 dits. At 20 WPM (dit = 60 ms)
  that is ~180 ms past the last element for a character and ~420 ms for a word; at
  35 WPM roughly half that. Everything else is small change: Goertzel block latency
  is single-digit milliseconds; a chunked neural model adds one chunk (order
  100–500 ms). Conclusion: every candidate is comfortably real-time for reading
  along; latency is a non-issue compared to the presentation-layer choices below.
  The only latency-sensitive consumer would be QSK-style interaction, which decode
  does not serve anyway.

## Screen-reader presentation (the part nobody else has done well)

Every decoder above ships a scrolling visual text box and calls it done. For our
users the presentation layer is half the feature, and it is engine-independent —
build it once, swap engines underneath.

- **Transcript buffer is the primary surface, not speech.** An append-only,
  read-only text control (plain WinForms TextBox multiline is already fully
  navigable with NVDA review commands) reachable by hotkey, holding the last few
  kilobytes of decode with optional timestamps and a copy-to-clipboard action.
  Speech is ephemeral; QSB gibberish means users *will* re-read to reconstruct a
  callsign. The buffer is also the deaf-blind surface via braille review.
- **Live announcements are word-buffered, never per-character.** Emit on word gap.
  Per-character speech at 25 WPM is a firehose no synthesizer survives. Feed
  announcements through the existing announcement infrastructure (centralized
  utterances) as *low-priority, interruptible* speech that never preempts
  user-initiated announcements — the no-silent-keystrokes rule must keep winning.
  When speech falls behind decode (it will, above ~30 WPM), coalesce: drop stale
  words from the speech queue, keep the transcript complete. Verbosity modes per
  the verbosity architecture: off / words / everything, per-radio, default off
  (flexibility principle: togglable, conservative default).
- **Braille routing is the sleeper feature.** Streaming decoded words to a braille
  display region (BrailleElement / multi-braille output vision) gives deaf-blind
  operators direct CW reception — no other CW decoder on the market even considers
  this. The CW notification memory already envisions CW-as-haptics relay; decode
  feeding braille is the same abstraction (message vs. delivery) pointed the other
  direction.
- **Decoder confidence belongs in the UI.** Expose tracked WPM and tone frequency
  as readable status fields (they double as tuning aids: "locked, 22 WPM, 640 Hz"
  tells the user the decoder found the signal). An ML engine's per-character
  confidence can gate speech (speak only confident words; transcript gets
  everything, with a marker convention for low-confidence runs — textual markers,
  never visual-only styling).

## Integration approaches, ranked

1. **In-process C# engine behind an interface.** Define `ICwDecoder` (or similar):
   push `float[]` + sample rate in; events out for character, word, tracked WPM,
   tone frequency, lock state. Both the DIY DSP engine and an ONNX engine implement
   it; the presentation layer neither knows nor cares. This matches every existing
   pattern in the codebase (`PostDecodeProcessor` tap, RNNoise provider, engine
   swap via settings).
2. **P/Invoke native library** (ggmorse) — proven pattern here (RNNoise, opus,
   portaudio), fine for a spike, but ggmorse's HF quality makes it a dead end as
   the shipped engine.
3. **Out-of-process AGPL engine over stdio/localhost** (e04 deepcw-engine) — legally
   clean, and the model-pack distribution scheme could deliver it as an optional
   download, but it drags a Python/Node runtime along and adds a process to manage.
   Interim measure at best; superseded the day we have our own ONNX model.
4. **External tool bridges** (fldigi XML-RPC, CW Skimmer Telnet) — power-user
   options we could document, not product features. fldigi's FLTK UI being
   invisible to screen readers and the virtual-audio-cable requirement disqualify
   it as anything we *recommend* to our audience.

## Recommended roadmap

- **Step 1 — cheapest credible: ship the plumbing with a DIY DSP engine.** Build
  `ICwDecoder` + the Goertzel/adaptive-WPM engine in C# (a few hundred lines, MIT,
  zero deps) on the raw `PostDecodeProcessor` tap, plus the full presentation layer
  (transcript buffer, word-buffered speech, WPM/pitch status, per-radio toggle,
  default off, marked experimental). This is honest as a "practice-quality decoder"
  — it will read W1AW code practice and clean machine-sent CW well, and it gets the
  UX in front of Don and Mark immediately. Every line of it (tap, interface,
  transcript, speech, braille route) is load-bearing for the real engine later.
  Optionally stand up a throwaway ggmorse P/Invoke harness for offline file-based
  comparison, without shipping it.
- **Step 2 — build the benchmark before the better engine.** Assemble a scoring
  corpus: W1AW code-practice archives (known text = free ground truth), plus
  recorded off-air contest/QSO audio with hand transcripts, plus synthesized CW
  swept across SNR/QSB/QRM/keying-style axes (VE3NEA's MIT data-generation notebook
  does exactly this). Score character error rate per engine per SNR. This corpus is
  what makes "our decoder got better" a fact instead of a feeling, and it is how we
  benchmark against MRP40/CW Skimmer without integrating them.
- **Step 3 — long-term bet: our own neural engine, in-process via ONNX Runtime.**
  Start from VE3NEA's MIT training pipeline, adopt e04's proven deployment shape
  (small model, 3.2 kHz mono input, chunked streaming inference), train with
  aggressive QSB/QRM/hand-keying augmentation, export Keras→ONNX, run it behind
  `ICwDecoder` with `Microsoft.ML.OnnxRuntime` (CPU). Distribute weights through
  the model-pack channel. Training a model this small is CPU-feasible (roarbox's 40
  cores are sitting right there). Result: MIT-clean end to end, no extra process,
  no extra runtime, and — on the published evidence — decode quality at or above CW
  Skimmer's on single-channel QSOs, inside a screen-reader-first client. Nobody
  else in the market has that combination.
- **Explicitly not on the roadmap:** MRP40/CWGet integration (no interface),
  fldigi as a dependency (accessibility + friction), extracting GPL modem code
  in-process (license), CW Skimmer Telnet (park until a band-spots feature wants
  it).

## Sources

- fldigi CW configuration and capabilities: http://www.w1hkj.com/FldigiHelp/cw_configuration_page.html and https://www.w1hkj.org/FldigiHelp/cw_configuration_page.html
- fldigi XML-RPC: https://www.w1hkj.org/FldigiHelp/xmlrpc_control_page.html , https://sourceforge.net/p/fldigi/wiki/api_for_xmlrpc_socket_services/ , https://github.com/KM4YRI/pyFldigi
- AG1LE decoder research (matched filter, SOM, Bayesian): http://ag1le.blogspot.com/2012/05/fldigi-matched-filter-and-som-decoder.html , http://ag1le.blogspot.com/2014/07/new-morse-decoder-part-6.html , https://ag1le.blogspot.com/2012/06/ultimate-morse-code-decoder.html
- MRP40: https://www.polar-electric.com/Morse/MRP40-EN/ , http://www.polar-electric.com/Morse/MRP40-EN/LoadPRM-EN.html
- CW Skimmer: https://en.wikipedia.org/wiki/CW_Skimmer , https://www.reversebeacon.net/genn.php?a=skimmer , http://dxatlas.com/CWSkimmer/Files/CwSkimmer.pdf
- GGMorse: https://github.com/ggerganov/ggmorse
- VE3NEA DeepCW (MIT training pipeline): https://github.com/VE3NEA/DeepCW
- e04 DeepCW web decoder and engine (AGPL-3.0): https://github.com/e04/web-deep-cw-decoder , https://github.com/e04/deepcw-engine , https://cw.e04.workers.dev/
- NATO STO RNN Morse transcription paper: https://www.sto.nato.int/document/human-like-morse-code-decoding-using-machine-learning/
- Goertzel CW decoder builds: https://fletch.scot/radio/goertzel.html , https://github.com/k3ng/k3ng_cw_keyer/wiki/385-Feature:-CW-Decoder , https://yu2zz.wordpress.com/2026/02/22/dsp-morse-decoder-trainer-with-arduino/
- .NET morse libraries (dismissed for decode): https://github.com/p6laris/MorseSharp , https://github.com/855309/dmorse , https://github.com/jstoddard/CWLibrary , https://git.starbeamrainbowlabs.com/sbrl/AudioMorseDecoder
