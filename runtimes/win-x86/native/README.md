# x86 Native Libraries

This folder holds the 32-bit native libraries: `libopus.dll` and
`portaudio.dll` (audio), `prism.dll` (speech and braille), and
`nvdaControllerClient.dll` (NV Access's controller client, for asking NVDA
whether an utterance was spoken to the end — #521; downloaded, not built, LGPL
text beside it), and `deepfilter.dll` with `DeepFilterNet3_onnx.tar.gz` and its
two licence files (DeepFilterNet3 noise reduction, #731; built and benched,
not yet selectable). This paragraph
named Tolk and its bridge DLLs (`SAAPI32.dll`, `nvdaControllerClient32.dll`,
`dolapi32.dll`) until 2026-08-21 — Tolk was removed 2026-08-17 and Prism
replaced the lot; those files no longer exist here.

## The build recipe lives with the x64 pair

**See `runtimes/win-x64/native/README.md`.** It carries the pinned versions, the
exact clone, checkout and cmake lines, both rename traps, and why PortAudio is
built from a master commit rather than a release. **Deliberately not duplicated
here** — two copies of a build recipe drift, and this project has spent real
time this month on documentation that no longer matched the thing it described.

**To build 32-bit, use that recipe with two substitutions:**

- `-A Win32` instead of `-A x64`
- output into `build-x86` and copy the results into **this** folder

**DeepFilterNet is a Rust build, not cmake**, so neither substitution applies:
the x64 recipe already builds both targets in one loop, and the x86 library is
`target/i686-pc-windows-msvc/release-lto/df.dll`, copied here as
`deepfilter.dll`. The model archive and both licence files are the same bytes
in both folders.

Everything else — the pinned Opus tag, the pinned PortAudio commit, the
mandatory `-DPA_GIT_REVISION` stamp, and the `opus.dll` to `libopus.dll` rename
— is identical.

## Keep the two architectures at the same versions

**Both DLLs are shipped from the same source revision, and they must stay in
step.** It is easy to rebuild one architecture and forget the other, and the
result is an x86 install quietly running different audio code from x64.

Verify by reading the version strings straight out of the binaries — they are
embedded, and the binary is the authority:

- `libopus 1.6.1`
- `PortAudio V19.7.0-devel, revision a880212`
- Prism: `0.18.1` (via `prism_version_string()`, or grep the DLL for `0.18.`)
- NVDA controller client: no version string at all. Identify it by its
  exports — `nvdaController_speakText` present (the positive control),
  `nvdaController_speakSsml` present, `nvdaController_isSpeaking` ABSENT for
  the 2026.2 client — and by SHA-256
  `96295979A25AB1C8DDC9B6AAFFDD81ED72C9504880850D49E99F1DB96055A7D0`
  (215,192 bytes), from the archive recorded in the x64 README.
- DeepFilterNet: no version string either. Same exports as x64 (positive
  control `df_process_frame`; `df_next_log_msg` must be ABSENT), `tract-core-0.19.16`
  named in the binary, and SHA-256
  `12278781334765ECF08A0F3530F24DDFE76D0513BD941DB84EDC94D4F2E7AA54`
  (10,491,904 bytes). The model must hash to
  `C94D91F70911001C946E0FABB4AA9ADC37045F45A03B56008CB0C8244CB63616` in both
  folders, or the engine refuses it.

Both architectures were confirmed matching on 2026-08-11 (audio pair),
2026-08-21 (Prism) and 2026-09-06 (NVDA client, both hashed against the
official 2026.2 archive). **Ignore the `19.7.0`** in PortAudio's string; upstream
never bumped that constant, so it is identical on a current build and a
five-year-old one. Only the `revision` suffix identifies anything. **Prism's
string has the same trap in a different coat** — it reports the CMake project
version, which goes stale the moment anyone builds past the tag; see the x64
README's Prism section for the incident and the rule (build exactly at the
pinned tag, record the SHA).
