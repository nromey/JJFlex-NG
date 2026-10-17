# x64 Native Libraries

This folder holds the x64 native libraries: `libopus.dll` and `portaudio.dll`
(audio), `prism.dll` (speech and braille — the screen-reader backend),
`nvdaControllerClient.dll` (NV Access's own controller client, used ONLY to
ask NVDA whether an utterance was spoken to the end — see #521), and
`deepfilter.dll` with its model archive (DeepFilterNet3 noise reduction, built
and benched but not yet selectable — see #731).
The same recipes build the x86 set — substitute `Win32` for `x64` and target
`runtimes/win-x86/native/`.

## What is currently shipped — verify, do not trust this heading

- **Opus 1.6.1**
- **PortAudio: master pinned at `a880212` (commit date 2026-08-07)**
- **Prism v0.18.1, pinned at tag commit `d2998e9281806fe1efd3394971c6e44ac11b9e75`**
- **NVDA controller client 2.0, from the NVDA 2026.2 release archive** (not
  built here — downloaded; see its section below for the hashes)
- **DeepFilterNet v0.5.6, pinned at tag commit `978576aa8400552a4ce9730838c635aa30db5e61`**,
  C API only, plus the upstream `DeepFilterNet3_onnx.tar.gz` model from the
  same commit. No version string of its own; see its section for the hashes

**Both DLLs embed a readable version string, so the binary is the authority.**
`opus_get_version_string()` returns `libopus 1.6.1`, and `Pa_GetVersionText()`
returns `PortAudio V19.7.0-devel, revision a880212`. Searching the DLL for
`libopus 1.` or `PortAudio V` answers the question in seconds. Neither carries a
Win32 VERSIONINFO resource, so Windows file properties show nothing useful.

**The `19.7.0` in PortAudio's string is meaningless — ignore it.** Upstream
never bumped that constant, so a five-year-old build and a current one report
the identical text. **The `revision` suffix is the only honest identifier**,
which is why it must be stamped on every rebuild (see below).

## Why PortAudio is built from master rather than a release

**PortAudio's newest release is 19.7.0 from March 2021.** Master has moved
roughly 255 commits past it, including every Windows-backend fix we care about —
the WASAPI Realtek "Mono" driver workaround (`09b7731`), WASAPI mono I/O
(`c121482`), WDM-KS buffer position alignment (`2f61007`), and a WMME bounded
timeout replacing infinite loops (`ba486a3`). **vcpkg is pinned at 19.7 too**,
so it is no help.

This makes "use the latest stable release" the *wrong* instinct for this one
library — doing the conventional right thing lands you on 2021 code. That is
exactly how the pre-2026-08-11 build came to be five years stale.

## Building from source

### Opus (libopus.dll)

```bash
git clone https://gitlab.xiph.org/xiph/opus.git
cd opus
git checkout v1.6.1           # <-- the pinned tag; update this line when you bump it
cmake -S . -B build-x64 -A x64 -DCMAKE_BUILD_TYPE=Release -DOPUS_BUILD_SHARED_LIBRARY=ON
cmake --build build-x64 --config Release
# Copy build-x64/Release/opus.dll here AS libopus.dll  (note the rename)
```

**Unlike PortAudio, Opus pins to a real tag** — it releases normally, so
`v1.6.1` is a genuine release rather than a snapshot, and no revision stamp is
needed because `opus_get_version_string()` already reports `libopus 1.6.1` from
the tag itself. Do not omit the checkout: without it you build master, and the
DLL will then claim a version nobody chose.

**Why 1.6.1 specifically:** it fixes reversed math and an integer overflow in
`compute_stereo_width()`, plus a stereo overflow in `tone_detect()`. Those run
in the encoder's stereo-analysis pass under `OPUS_APPLICATION_AUDIO` — exactly
the transmit profile this app uses (stereo, super-wideband, 10 ms frames, about
70 kbps, chosen to match SmartSDR). They are correctness bugs in the path that
carries the operator's voice.

**`OPUS_BUILD_SHARED_LIBRARY` defaults OFF** — omit it and you get a static
`.lib`, not a DLL. The output is named `opus.dll` and must be renamed to
`libopus.dll` to match the `DllImport` declarations.

### PortAudio (portaudio.dll)

```bash
git clone https://github.com/PortAudio/portaudio.git
cd portaudio
git checkout a880212          # <-- the pinned commit; update this line when you bump it
cmake -S . -B build-x64 -A x64 -DPA_BUILD_SHARED_LIBS=ON \
      -DPA_USE_WASAPI=ON -DPA_USE_WDMKS=ON -DPA_USE_WMME=ON -DPA_USE_DS=ON \
      -DCMAKE_C_FLAGS=/DPA_GIT_REVISION=a880212
cmake --build build-x64 --config Release
# Copy build-x64/Release/portaudio.dll here (no rename needed)
```

**`-DPA_GIT_REVISION` is not optional.** `PA_GIT_REVISION` defaults to
`unknown`, and without it the resulting DLL is indistinguishable from any other
PortAudio build ever made. Stamp the same SHA you checked out, and **update the
pinned commit in this file in the same change** so the record and the binary
agree.

CMake ships with Visual Studio if it is not on PATH — look under
`Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe`.

### Prism (prism.dll)

```bash
git clone https://github.com/ethindp/prism.git
cd prism
git checkout v0.18.1          # <-- the pinned tag; update this line AND the SHA above when you bump it
cmake -S . -B build-x64 -A x64
cmake --build build-x64 --config Release
# Copy build-x64/Release/prism.dll here (no rename needed)
```

**Build EXACTLY at the tag, from a clean tree — this is Prism's version of the
PortAudio trap.** `prism_version_string()` returns CMake's `PROJECT_VERSION`,
stamped at configure time, so a build made from a working tree past the tag
still reports the tag. The DLL shipped until 2026-08-21 said `0.17.3` while
actually being 46 commits newer (`v0.17.3-46-g9ae0ece`) — it even exported
functions a genuine 0.17.3 could not have. The embedded string is honest ONLY
when the checkout is exactly the tag, which is why the full commit SHA is
recorded in the shipped-versions list above and in CLAUDE.md: **the SHA is the
record, the string is a convenience.** Verify the tree is clean
(`git status --short` prints nothing) before building.

**Why v0.18.1 specifically:** commit `b1446f4` (in v0.18.0) fixes the NVDA
backend leaking its RPC binding, which made every `initialize()` after the
first FAIL — the bug that made screen-reader recovery (#167) impossible on the
old DLL. An operator whose NVDA started late or restarted could never be
reconnected to it.

**After a Prism upgrade, verify against `include/prism.h`:** the `PrismConfig`
struct layout, the `PrismError` enum members, and the backend id constants —
all mirrored in `Radios/Speech/PrismNative.cs`, and all capable of failing at
runtime rather than compile time. The struct doc comment there records the
crash a layout mismatch produces.

### NVDA controller client (nvdaControllerClient.dll) — downloaded, not built

**This one is NOT compiled here and must not be.** NV Access ships it
pre-built with every NVDA release, it is LGPL 2.1, and we use it unmodified —
so the obligation is exactly the one PortAudio and Opus already carry: ship
the licence text beside the binary (`nvdaControllerClient.LICENSE.txt`, in
this folder), change nothing. Building it ourselves would need `midl.exe` and
a C toolchain the build does not otherwise have, and would make one of our
own binaries LGPL-derived. The route decision is recorded in
`JJFlex-private/planning/active/speech-completion-route-recommendation.md`.

**Why it exists at all.** Prism speaks to NVDA through `speakText`, which is
fire-and-forget: the app hands NVDA twelve seconds of speech in one
millisecond and never learns which of it was said. The ledger that rescues
speech an interrupt destroyed therefore rescued speech the operator had
already heard (#521, #554). NVDA's `nvdaController_speakSsml`, called
synchronously, returns `0` when the utterance was spoken to the end and
`1223` when something cancelled it, and calls back for every `<mark>` reached
along the way. Prism as pinned reaches none of that, so this DLL is P/Invoked
alongside Prism, behind a capability bit (`PrismScreenReader.CompletionChannel`),
until Prism grows the feature upstream.

**Record of what is shipped:**

- Source: `https://download.nvaccess.org/releases/2026.2/nvda_2026.2_controllerClient.zip`
- Archive SHA-256: `510736F021AEFA33378076FA342A4524B586BC1C6B096DB9479AF42B28AAC649` (4,795,290 bytes), downloaded and hashed 2026-09-06
- `x64/nvdaControllerClient.dll`: SHA-256 `598B7EC3DC469814F571275929F676CE73834C469FBDB359A06FD4DB4E0FC866`, 263,832 bytes
- `x86/nvdaControllerClient.dll`: SHA-256 `96295979A25AB1C8DDC9B6AAFFDD81ED72C9504880850D49E99F1DB96055A7D0`, 215,192 bytes
- Client API version 2.0 (introduced in NVDA 2024.1). Interfaces: `NvdaController` v1.0 and `NvdaController2` v1.0. On NVDA older than 2024.1 the v2 calls return `1717` (RPC_S_UNKNOWN_IF), which the app treats as "channel absent" and falls back to Prism.

**The byte-search recipe — run it on any copy before trusting it.** The DLL
carries no version resource, but its export names are plain ASCII in the
binary. `strings` is NOT installed on this machine; `grep -c -a` on the file
works:

- `nvdaController_speakText` — **must be present.** This is the POSITIVE
  CONTROL: every genuine client since 2006 exports it. If a search finds
  neither this nor `speakSsml`, the search is broken, not the DLL. That exact
  false negative happened on 2026-09-05 and again on 2026-09-06.
- `nvdaController_speakSsml` — must be present. Absent means the pre-2024.1
  client, which cannot ask the question (#541: thirty such copies were swept
  out of four build trees on 2026-09-05, last written 2026-02-22).
- `nvdaController_isSpeaking` — must be ABSENT for 2026.2. It arrives with
  `NvdaController3` in NVDA 2026.3. When the pin moves to a client that has
  it, update this line, because its presence is how you tell the two apart.

**The app loads this DLL by ABSOLUTE PATH, deliberately.** `Radios/Speech/NvdaControllerClient.cs`
calls `NativeLibrary.Load` on the full path under this folder and binds every
entry point by name from that handle. It is never resolved through
`NativeLoader.vb`'s name mapping and never through the Windows search order,
because search order is what would find a stray pre-2024.1 copy beside the
exe and fail at runtime with a missing entry point while you were reasoning
about RPC. Keeping the DLL out of `NativeLoader` is a decision, not an
omission.

**To bump it:** download the `*_controllerClient.zip` for the NVDA release
you want, record the archive hash, copy `x64/` and `x86/nvdaControllerClient.dll`
here and into `win-x86`, run the recipe above on both, and update the three
lines that name what should be present and absent.

### DeepFilterNet3 (deepfilter.dll, DeepFilterNet3_onnx.tar.gz) — built here from upstream

**What is shipped, and why it exists.** DeepFilterNet3 is a full-band 48 kHz
speech denoiser by Hendrik Schröter et al. (FAU Erlangen-Nürnberg), from
`https://github.com/Rikorose/DeepFilterNet`. Its `libDF` crate has a C API that
runs the model with `tract`, a pure-Rust ONNX engine, so **no ONNX Runtime is
involved.** It is the candidate second PC-side receive noise reducer (#731). As
of Sprint 48 Track G it is built, tested and benched but **nothing an operator
can reach selects it**; RNNoise is still the only engine the app runs.

Four files, identical layout in both architecture folders:

- `deepfilter.dll` — the C API library. Built as `df.dll` (the crate's library
  name is `df`) and **renamed**, because a two-letter DLL name is a collision
  waiting to happen.
- `DeepFilterNet3_onnx.tar.gz` — upstream's model archive, byte-for-byte from
  `models/` at the pinned commit (three ONNX graphs and a `config.ini`).
- `deepfilter.LICENSE-MIT.txt` and `deepfilter.LICENSE-APACHE.txt` — upstream's
  `LICENSE-MIT` and `LICENSE-APACHE`, renamed so they plainly belong to this
  library. The code is MIT OR Apache-2.0 at our option.

**Pinned at tag `v0.5.6`, commit `978576aa8400552a4ce9730838c635aa30db5e61`
(2023-08-31)**, the newest release tag and the one carrying the final
DeepFilterNet3 model (`models: Update final DeepFilterNet3 model`, `84d57ec`,
2023-05-23). Upstream's `main` is 89 commits past the tag and its C API differs:
`df_create` there takes a third `log_level` argument and log-message functions
exist. **Do not take the binding from `main` or from any other project's copy of
the header.** The binding in `JJFlexWpf/DeepFilterNet3Engine.cs` is written from
`libDF/src/capi.rs` at this tag:

- `df_create(const char *path, float atten_lim)` returns the state pointer
- `df_get_frame_length(state)` returns `size_t` (480 for this model)
- `df_set_atten_lim(state, float db)`, `df_set_post_filter_beta(state, float beta)`
- `df_process_frame(state, float *in, float *out)` returns the frame's local SNR in dB; mono, floats in [-1, 1]
- `df_free(state)`
- `extern "C"`, which is **cdecl** on x86

**The model must be given as a path; there is no embedded default you can
reach.** The `capi` feature does switch on `default-model`, which
`include_bytes!` the archive into `DfParams::default()`, but the C API never
calls that: `df_create` always does `CStr::from_ptr(path)` then
`DfParams::new(path)`. **A NULL path is undefined behaviour, not "use the
default"**, at this tag and on `main` alike. And because nothing reachable calls
`default()`, the linker drops the embedded copy: searching the built DLL for the
archive's bytes finds nothing (checked 2026-10-17). So the archive ships beside
the DLL and the engine passes its path.

**A bad model path ends the process.** The C API reports every failure with
`.expect(...)`, and the build profile sets `panic = "abort"`. So the engine
hashes the archive and refuses anything that is not exactly the pinned file
before it calls `df_create` at all. **When you bump the pin, update
`DeepFilterNet3Engine.ModelSha256` in the same change**, or the engine will
correctly refuse the new model and DeepFilterNet3 will be unavailable.

**Exact build, as run 2026-10-17** with `rustc 1.98.0 (88d9e12ae 2026-08-18)`
and `cargo 1.98.0`:

```bash
git clone https://github.com/Rikorose/DeepFilterNet.git deepfilternet-build
cd deepfilternet-build
git checkout v0.5.6          # <-- the pinned tag; update this line AND the SHA above when you bump it
# The tag's Cargo.lock pins time 0.3.28, which no longer compiles on current
# Rust (E0282 in time's format_description parser). time is only a BUILD
# dependency here (liquid, used by tract-linalg's code generator), so this
# changes no shipped code. It is the only change to the tree:
cargo update -p time --precise 0.3.36
rustup target add i686-pc-windows-msvc     # once, for the x86 build
for t in x86_64-pc-windows-msvc i686-pc-windows-msvc; do
  RUSTFLAGS="-C target-feature=+crt-static --remap-path-prefix=C:\\Users\\<you>\\.cargo=/cargo --remap-path-prefix=C:\\path\\to\\deepfilternet-build=/deepfilternet" \
  cargo rustc --profile release-lto --locked -p deep_filter --lib --crate-type cdylib \
              --no-default-features --features capi --target $t
done
# Copy target/x86_64-pc-windows-msvc/release-lto/df.dll here AS deepfilter.dll,
# and target/i686-pc-windows-msvc/release-lto/df.dll to ../../win-x86/native/ the same way.
# Copy models/DeepFilterNet3_onnx.tar.gz, LICENSE-MIT and LICENSE-APACHE beside each.
```

Why each flag:

- **`cargo rustc --crate-type cdylib`.** At the tag `libDF` declares no crate
  type, so a plain `cargo build` makes only an `rlib`. Upstream's own C-API
  workflow (`.github/workflows/build_capi.yml`) uses `cargo-c`'s `cinstall`;
  this produces the same library without installing `cargo-c`.
- **`--profile release-lto`** is the profile upstream's C-API workflow uses: fat
  LTO, stripped, and `panic = "abort"` (see above).
- **`+crt-static`** matches upstream's `--crt-static` for MSVC: the C runtime
  is linked in, so the DLL needs no Visual C++ redistributable. Its only imports
  are Windows system DLLs (`KERNEL32`, `ADVAPI32`, `ntdll`, `bcrypt`,
  `bcryptprimitives`, `api-ms-win-core-synch-l1-2-0`), checked with
  `dumpbin /dependents`.
- **`--remap-path-prefix`**, given both paths in the Windows form the compiler
  sees (not Git Bash's `/c/...` form, which matches nothing), keeps the build
  machine's paths out of the panic
  messages compiled into the binary. Without it the DLL carries the builder's
  user-profile path dozens of times, and this repository is public.
- **`--no-default-features --features capi`** leaves out the Vorbis and FLAC
  decoders the default feature set adds; the C API reads no audio files.

**Record of what is shipped** (SHA-256):

- `deepfilter.dll` x64: `4305FDE2109F60002E02DF3FEFC11F1E20FA99195E29FD2BD06940B45D54D51F`, 12,769,280 bytes
- `deepfilter.dll` x86: `12278781334765ECF08A0F3530F24DDFE76D0513BD941DB84EDC94D4F2E7AA54`, 10,491,904 bytes
- `DeepFilterNet3_onnx.tar.gz` (both folders): `C94D91F70911001C946E0FABB4AA9ADC37045F45A03B56008CB0C8244CB63616`, 7,983,136 bytes
- `deepfilter.LICENSE-MIT.txt`: `24E6BB09C928AF8D8E56268082F87413247CE36B39DD5D33ADD2F9893968065E`
  with LF line endings, as upstream's git stores it
- `deepfilter.LICENSE-APACHE.txt`: `1EAEE808C5FB6B4E895BA30425285A5CDC5DD25BBA2CD230F264C2200C331AEC`
  with LF line endings

A rebuild is not guaranteed to reproduce the DLL hashes bit for bit (the
linker stamps a timestamp). The model hash is upstream's file and must match
exactly. **The two licence hashes are of the LF form**, which is what both this
repository and upstream store; a Windows checkout converts them to CRLF and then
hashes differently, so compare `git show HEAD:<path>` rather than the working
copy.

**How to tell what a copy is: it has no version string, so do not invent
one.** The crate never compiles its own version into the binary (searching for
`0.5.6` finds nothing) and there is no Win32 version resource. Identify a copy by:

- **Exports** (`dumpbin /exports`): exactly `df_create`, `df_free`,
  `df_get_frame_length`, `df_process_frame`, `df_process_frame_raw`,
  `df_set_atten_lim`, `df_set_post_filter_beta`. **The POSITIVE CONTROL is
  `df_process_frame`**: every build of this API exports it, so a search that
  finds it proves the search works. A copy that ALSO exports `df_next_log_msg`
  was built from upstream `main`, not from this tag, and takes a different
  `df_create`.
- **The ONNX engine it links:** the binary names its `tract` crates by version;
  `grep -a -o "tract-core-0\.[0-9.]*"` reads `tract-core-0.19.16` for this pin.
  Upstream `main` uses tract 0.21.
- **The SHA-256 above.**

**The model's licence is NOT settled — confirm before any public release.**
Upstream's `README.md`, section *License*, says: *"All code in this repository
is dual-licensed under either: MIT License … Apache License, Version 2.0 … at
your option."* It says **code**. The model archives sit in the same repository
under `models/`, and there is **no separate licence file for them**: none in
`models/`, none inside `DeepFilterNet3_onnx.tar.gz` (its four entries are
`enc.onnx`, `erb_dec.onnx`, `df_dec.onnx` and `config.ini`), and no licence
statement in `config.ini`. Read 2026-10-17 at the pinned commit. The natural
reading is that the repository's dual licence covers the trained weights too,
but that is an inference, not a statement upstream has made, and the weights
were trained on third-party speech and noise corpora whose own terms nobody has
checked here. **Ask the author, or get a short legal read, before this ships in
a public release.**

**To bump it:** check out the new tag, record its full SHA here, re-read
`libDF/src/capi.rs` against the binding list above (the signatures moved
between this tag and `main`), rebuild both targets with the recipe, replace all
four files in BOTH folders, update every hash in this section and
`DeepFilterNet3Engine.ModelSha256`, and run `DeepFilterNet3EngineTests`.

## Where the build trees live

`build-native/` at the repo root holds working clones of both projects.
DeepFilterNet's clone is kept outside the repo entirely
(`C:\dev\deepfilternet-build` on the machine that built it); like
`build-native/`, it is a convenience and not a record. **That
directory is gitignored**, so nothing in it is part of the repository — it is a
local convenience, not a record. **This file is the record.** Anything future
rebuilds need to know belongs here, not in a note inside `build-native/`.

Deliberately not a git submodule: we ship binaries rather than source, rebuilds
happen about once every few years given PortAudio's release cadence, and a
submodule would tax every clone forever to serve that. The version stamped
inside the DLL also identifies a shipped artifact in the field, which a
submodule cannot.

## Prebuilt binaries

- Opus: https://opus-codec.org/downloads/ — or vcpkg, whose port tracks 1.6.1.
- PortAudio: **do not use vcpkg or the stable tarball**; both are 19.7.0. Build
  from the pinned master commit above.
