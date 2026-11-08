# x64 Native Libraries

This folder holds the x64 native libraries: `libopus.dll` and `portaudio.dll`
(audio), `prism.dll` (speech and braille — the screen-reader backend),
`nvdaControllerClient.dll` (NV Access's own controller client, used ONLY to
ask NVDA whether an utterance was spoken to the end — see #521), and
`deepfilter.dll` with its model archive (DeepFilterNet3 noise reduction, built
and benched but not yet selectable — see #731).
`rnnoise.dll` is RNNoise noise reduction, the engine the app runs, and
`rnnoise_vad011.dll` is RNNoise v0.1.1's voice detector on its own (#873; nothing
calls it yet); each has its `.COPYING.txt` beside it.
`specbleach.dll` is libspecbleach, NR4 (LGPL 2.1, built from the source vendored
in `third_party/libspecbleach/`, not yet selectable).
The same recipes build the x86 set — substitute `Win32` for `x64` and target
`runtimes/win-x86/native/`.

## What is currently shipped — verify, do not trust this heading

- **RNNoise, upstream pinned at commit `70f1d256acd4b34a572f999a05c87bf00b67730d`** (the 0.2 line; no release is newer than `v0.2`, and the tag is a different model), with the model tarball its `model_version` names (both architectures built here; no version string)
- **RNNoise v0.1.1's voice detector, `rnnoise_vad011.dll`**, from tag `v0.1.1` at `6cbfd53eb348a8d394e0757b4025c6ded34eb2b6` plus our `native/rnvad011/rnvad011.c` (both architectures built here)
- **Opus 1.6.1**
- **PortAudio: master pinned at `a880212` (commit date 2026-08-07)**
- **Prism v0.18.1, pinned at tag commit `d2998e9281806fe1efd3394971c6e44ac11b9e75`**
- **NVDA controller client 2.0, from the NVDA 2026.2 release archive** (not
  built here — downloaded; see its section below for the hashes)
- **DeepFilterNet v0.5.6, pinned at tag commit `978576aa8400552a4ce9730838c635aa30db5e61`**,
  C API only, plus the upstream `DeepFilterNet3_onnx.tar.gz` model from the
  same commit. No version string of its own; see its section for the hashes
- **libspecbleach v0.4.1, pinned at tag commit `ab4ae8b4be74f5c52c5a3386a6d9bbea29fbd129`**
  (`specbleach.dll`, NR4), built here from the vendored source in
  `third_party/libspecbleach/`. Its string says `libspecbleach 0.4.1`; trust
  the SHA, not the string (see its section)

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

### RNNoise (rnnoise.dll) — built here from upstream

RNNoise is Xiph's BSD-style speech denoiser. We build both architectures
because the retired package shipped the same x64 binary in its x86 folder
(#739).

**What ships, and why a commit rather than a tag.** Noel ruled on 2026-11-08
(#873) to take "the latest" RNNoise. The newest release tag is still `v0.2`
(April 2024), but `v0.2` and current upstream are **different models**: after
the tag, upstream added skip connections (the voice output reads every layer,
not the last GRU), a new training label and new weights (model of 2025-01-29).
So the engine is the **PortAudio precedent**: no newer release, so a pinned
commit with its SHA recorded here.

- Repository `https://github.com/xiph/rnnoise`, commit
  `70f1d256acd4b34a572f999a05c87bf00b67730d` (2025-02-22, "Remove outdated
  TRAINING-README"), 22 commits past `v0.2`. Verified 2026-11-08 that Xiph's
  canonical GitLab has `main` and `HEAD` at this same SHA:
  `git ls-remote https://gitlab.xiph.org/xiph/rnnoise.git`.
- **The weights are not in git.** The pin's `model_version` file holds
  `0a8755f8e2d834eff6a54714ecc7d75f9932e845df35f8b59bc52a7cfe6e8b37`, and its
  `download_model.sh` fetches
  `https://media.xiph.org/rnnoise/models/rnnoise_data-<model_version>.tar.gz`
  and checks the tarball's SHA-256 against that same value. Ours matched:
  `rnnoise_data-0a8755f8e2d834eff6a54714ecc7d75f9932e845df35f8b59bc52a7cfe6e8b37.tar.gz`,
  58,603,099 bytes, SHA-256 `0A8755F8E2D834EFF6A54714ECC7D75F9932E845DF35F8B59BC52A7CFE6E8B37`.
  Only `src/rnnoise_data.c` and `src/rnnoise_data.h` are used (the default
  model; the tarball also holds a "little" model and two PyTorch checkpoints).
- **The recipe extracts the weights OUTSIDE the clone**, into
  `build-native/rnnoise-70f1d256-model/`, so the source tree stays clean and the
  clean-tree check stays meaningful.

Exact build run 2026-11-08, from the repository root, with the upstream clone
at `build-native/rnnoise-70f1d256` and the tarball at `build-native/models/`:

```powershell
$nativeRoot = 'C:\dev\jjflex-49r\build-native'
$sourceRoot = Join-Path $nativeRoot 'rnnoise-70f1d256'
$modelTar = Join-Path $nativeRoot 'models\rnnoise_data-0a8755f8e2d834eff6a54714ecc7d75f9932e845df35f8b59bc52a7cfe6e8b37.tar.gz'
if ((git -C $sourceRoot rev-parse HEAD) -ne '70f1d256acd4b34a572f999a05c87bf00b67730d') { throw 'Wrong RNNoise pin' }
if (git -C $sourceRoot status --porcelain --ignored) { throw 'Dirty RNNoise source' }
if ((Get-FileHash $modelTar -Algorithm SHA256).Hash -ne '0A8755F8E2D834EFF6A54714ECC7D75F9932E845DF35F8B59BC52A7CFE6E8B37') { throw 'Wrong RNNoise model' }
$modelRoot = Join-Path $nativeRoot 'rnnoise-70f1d256-model'
Remove-Item -Recurse -Force $modelRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $modelRoot | Out-Null
tar -xzf $modelTar -C $modelRoot src/rnnoise_data.c src/rnnoise_data.h
$modelSrc = Join-Path $modelRoot 'src'
$compiler = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\Llvm\x64\bin\clang-cl.exe'
$msvcRoot = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231'
$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
$includes = @('/imsvc' + $msvcRoot + '\include', '/imsvc' + $sdkRoot + '\Include\10.0.26100.0\ucrt', '/imsvc' + $sdkRoot + '\Include\10.0.26100.0\shared', '/imsvc' + $sdkRoot + '\Include\10.0.26100.0\um')
$sources = @('denoise','rnn','pitch','kiss_fft','celt_lpc','nnet','nnet_default','parse_lpcnet_weights','rnnoise_tables') | ForEach-Object { Join-Path $sourceRoot "src\$_.c" }
$sources += Join-Path $modelSrc 'rnnoise_data.c'
$common = @('/nologo','/O2','/MT','/fp:precise','/DWIN32','/DRNNOISE_BUILD','/DDLL_EXPORT','/D_USE_MATH_DEFINES','/DDISABLE_DEBUG_FLOAT',"/I$sourceRoot\include","/I$sourceRoot\src","/I$modelSrc")
foreach ($nativeArch in @('x64','x86')) {
    $outputRoot = Join-Path $nativeRoot "rnnoise-70f1d256-$nativeArch"
    Remove-Item -Recurse -Force $outputRoot -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
    Push-Location $outputRoot
    try {
        $target = if ($nativeArch -eq 'x64') { 'x86_64-pc-windows-msvc' } else { 'i686-pc-windows-msvc' }
        & $compiler "--target=$target" /LD @common @includes @sources /link /DEBUG:NONE /INCREMENTAL:NO /Brepro "/LIBPATH:$msvcRoot\lib\$nativeArch" "/LIBPATH:$sdkRoot\Lib\10.0.26100.0\ucrt\$nativeArch" "/LIBPATH:$sdkRoot\Lib\10.0.26100.0\um\$nativeArch" /OUT:rnnoise.dll
        if ($LASTEXITCODE -ne 0) { throw "Native build $nativeArch failed: $LASTEXITCODE" }
    } finally { Pop-Location }
}
```

Toolchain as for v0.1.1 below: clang 22.1.3 (LLVM
`e9846648fd6183ee6d8cbdb4502213fcf902a211`), MSVC libraries 14.51.36231,
Windows SDK 10.0.26100.0. What differs from the v0.1.1 recipe, and why:

- **Sources** are `RNNOISE_SOURCES` from the pin's `Makefile.am` (unchanged
  from `v0.2`): the 0.2 line adds the Opus DNN code (`nnet`, `nnet_default`,
  `parse_lpcnet_weights`, `rnnoise_tables`), and `rnnoise_data.c` comes from
  the model tarball. `rnn_reader.c` is gone.
- **`DISABLE_DEBUG_FLOAT`** is what upstream's `configure.ac` defines by
  default (`--enable-dnn-debug-float` is off): the GRUs use their 8-bit weights.
- **No run-time CPU dispatch; the generic path.** Upstream's x86 dispatch
  (`src/x86/`, enabled by `--enable-x86-rtcd`) is **off by default** upstream,
  and we leave it off, so no RNNoise or detector code uses an instruction
  beyond SSE2. (The x64 DLLs do contain AVX, FMA and AVX-512 instructions, all
  inside the statically linked Microsoft C runtime's math and memory routines.
  The runtime picks them only after checking CPUID, OSXSAVE and XGETBV, so
  they are safe. Codex disassembled them and traced each one to its runtime
  object, 2026-11-08. The x86 DLLs contain none.)
  Without `-mavx2` the SIMD header `vec_avx.h` compiles its SSE2 branch; clang
  predefines `__SSE2__` for both targets, and SSE2 is the floor of x64 and of
  every Windows that runs .NET 10. The compiler's own pragma message says so:
  "Only SSE and SSE2 are available". Three reasons:
  - Upstream's dispatcher (`src/x86/x86cpu.c` at the pin) tests the CPUID bits
    for AVX, FMA and AVX2 but **never checks that the operating system has
    enabled the AVX register state** (OSXSAVE and XGETBV). A machine with AVX
    turned off in the OS (some virtual machines, `bcdedit` with XSAVE disabled)
    would be dispatched to AVX code and crash. That is the "assumes AVX2
    without checking" case, even with detection on.
  - One RNNoise code path means the same arithmetic on every machine, which
    the tests and any support conversation can rely on. Not proven: identical
    output bit for bit, because the runtime's math routines still choose a
    path by CPU.
  - The cost is affordable. Measured 2026-11-08 on this machine (Intel Core
    Ultra 9 285HX), median of nine runs of 3,000 frames, one thread pinned:
    generic **133 µs per 10 ms frame on a performance core, 145 on an
    efficiency core, x64; 136 and 153, x86**, about 1.5 percent of one core per
    channel. A scratch build WITH upstream's dispatch measured 81 to 90 µs on
    both architectures, so dispatch would save about 40 percent if it is ever
    wanted; adding the missing XGETBV check would be the condition.
- **`/Brepro`** makes the link reproducible: the PE header timestamp is a hash
  of the output, not the time. Two clean rebuilds gave byte-identical DLLs on
  both architectures. (The v0.1.1 note below, "a linker timestamp can change
  hashes", is why it was added.)

Copy `build-native/rnnoise-70f1d256-x64/rnnoise.dll` to this folder and the
x86 counterpart to `runtimes/win-x86/native/`. Write the pin's `COPYING` beside
each as `rnnoise.COPYING.txt` **from git's blob, not from the working tree**
(`git -C build-native/rnnoise-70f1d256 cat-file blob 70f1d256acd4b34a572f999a05c87bf00b67730d:COPYING`),
because this machine's `core.autocrlf` rewrites checkouts.

Shipped SHA-256 and sizes:

- x64 DLL: `827DD730DDDD0076C0034066B0297595383924A156D90373014B42A716B90F35`, 3,741,184 bytes, machine `0x8664`.
- x86 DLL: `B47148D17EF3518B2433C69C9481C49F82F5D71204E69F165606D12EBC2D9CE9`, 3,705,344 bytes, machine `0x14C`.
- `rnnoise.COPYING.txt` (both): `45D37CA1CDB278C088E1AA85E0E65CA3A534ED86A28DCC96CA16810248A61D35`,
  1,612 bytes with LF endings, byte-identical to upstream's blob `59d7d09c`.
  Read in full 2026-11-08: the three-clause BSD licence, the same terms as
  v0.1.1's. Only the copyright lines differ: it adds "2024" to Jean-Marc
  Valin's years and adds "Copyright (c) 2023 Amazon".
- Both DLLs depend only on `KERNEL32.dll` (`dumpbin /dependents`), and contain
  no `.pdb` reference or builder path.

The complete undecorated export list, both architectures: `rnnoise_create`,
`rnnoise_destroy`, `rnnoise_get_frame_size`, `rnnoise_get_size`,
`rnnoise_init`, `rnnoise_model_free`, `rnnoise_model_from_buffer`,
`rnnoise_model_from_file`, `rnnoise_model_from_filename`,
`rnnoise_process_frame`. Positive control: the four `RnnoiseEngine.cs` binds
(`rnnoise_create`, `rnnoise_destroy`, `rnnoise_get_frame_size`,
`rnnoise_process_frame`) are present; `rnnoise_get_frame_size` still compiles
to `mov eax,480; ret`, which `RnnoiseBindingTests` patches. The binding is
unchanged: cdecl, output pointer before input, 480 samples at 48 kHz,
16-bit-range floats, return value the voice probability.

**What an operator can notice.**

- **The delay doubles, 480 to 960 samples (10 to 20 ms).** The 0.2 line adds
  one frame of look-ahead (upstream `84fe83b`, "Adding one extra frame of
  delay", before the tag): the gains for a frame are applied to the previous
  one. `RnnoiseEngine.LatencySamples` now says 960 and `EngineLatencyTests`
  measures it on real speech. The bank and the transmit stage align their dry
  signal by that figure, so the blend stays aligned; the delay the app reports
  for RNNoise doubles with it.
- **The voice probability for frame n describes frame n minus 1**, for the
  same reason. Compare it against v0.1.1's only after shifting it.
- Frame size is unchanged (480). CPU per frame about 1.4 to 1.6 times v0.1.1's.

**Model comparison before adopting it.** The same inputs through v0.1.1, the
`v0.2` tag and `70f1d256`, each DLL in its own process, fully wet: 12
speech-in-noise mixes, 3 SSB-shaped mixes, 4 off-air SSB minutes, a band-noise
sample and 2 CW takes. The figures are in
`JJFlex-private/planning/for-claude/2026-11-08-sprint49-track-r-report.md` and
the renders in `JJFlex-private/planning/for-noel/2026-11-08-rnnoise-02-listen/`
(never in the repository). **One result to know before relying on it: on CW the
new model nearly erases the keyed signal**, where v0.1.1 cost it about 10 dB.

**Swapping to the `v0.2` tag instead is one DLL and this section.** The same
recipe with `$sourceRoot` at a clean checkout of commit
`904a876dce1f9ab8860c0a5000ed151f9f6eef58` (tag `v0.2`) and the model
`rnnoise_data-0b50c45.tar.gz`, 22,270,507 bytes, SHA-256
`4AC81C5C0884EC4BD5907026AAAE16209B7B76CD9D7F71AF582094A2F98F4B43`. **The tag
names no hash for its model**: its `download_model.sh` only downloads and
untars (the checksum arrived after the tag, in `2e3c812`), and its
`model_version` reads `0b50c45`, which is not a hash of the tarball. The figure
above was checked against an independent record instead, the Nixpkgs pin for
that model (`sha256-SsgcXAiE7EvVkHAmqq4WIJt7ds2df3GvWCCUovmPS0M=`, the same
digest in base64). Built 2026-11-08 for the comparison only, not shipped: x64
`E9884CB7E33E5897C7FB95495ED137419CB515B038AE3B28338ED3AF2095B249`
(1,597,440 bytes), x86
`BE387A960A14D5E5E627424B780FE65FD7C728FD0EF3E7A4A4A78B57C8D2C2C4`
(1,561,600 bytes). Same exports, same licence text, same 960-sample delay.

To bump: place a clean checkout at the new full pin, read `Makefile.am`,
`configure.ac` (its defaults) and `include/rnnoise.h` again, take the model the
pin's own `model_version` names and check its hash, rebuild both architectures,
replace both DLLs and licences, update pin, recipe and hashes here, measure the
delay (`EngineLatencyTests`), and run the RNNoise tests on both architectures.
Compare model output deliberately before adopting a new model, on real
material as well as mixes, and on CW.

### The v0.1.1 voice detector (rnnoise_vad011.dll) — built here from upstream plus one file of ours

**Why it exists.** Ruled by Noel 2026-11-08 13:41 (#873): keep v0.1.1's
voice-activity branch as its own detector, whatever the engine becomes and
whether or not anything uses it yet. In v0.1.1 the voice probability has a
private branch (`input_dense`, then `vad_gru`, then `vad_output`) that
`compute_rnn` runs before, and that never reads, the noise and denoise GRUs
(`src/rnn.c` lines 165 to 167 at the pin), so it can run alone on the same
weights. **Nothing in the app calls it yet**; `RnnoiseVoiceDetectorTests` holds
that.

**What is built.** Upstream at the v0.1.1 pin, unmodified, plus our
`native/rnvad011/rnvad011.c`. That file `#include`s upstream's `denoise.c` and
`rnn.c` (their feature analysis and layer kernels are `static`), and exports
four functions of its own: `rnvad011_create`, `rnvad011_destroy`,
`rnvad011_frame_size` and `rnvad011_process_frame` (the high-pass, the feature
analysis and the three voice layers; it returns the probability and skips the
noise and denoise GRUs, the gains, the pitch filter and the synthesis). A fifth,
`rnvad011_reference_frame`, is upstream's whole `rnnoise_process_frame` under
our name, there so the tests can hold the detector to the full frame without a
second v0.1.1 library in the repository. `RNNOISE_BUILD` and `DLL_EXPORT` are
left undefined, so **none of upstream's `rnnoise_*` functions is exported**.

Exact build run 2026-11-08, with a clean checkout at
`build-native/rnnoise-v011`:

```powershell
$nativeRoot = 'C:\dev\jjflex-49r\build-native'
$sourceRoot = Join-Path $nativeRoot 'rnnoise-v011'
if ((git -C $sourceRoot rev-parse HEAD) -ne '6cbfd53eb348a8d394e0757b4025c6ded34eb2b6') { throw 'Wrong RNNoise pin' }
if (git -C $sourceRoot status --porcelain --ignored) { throw 'Dirty RNNoise source' }
$compiler = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\Llvm\x64\bin\clang-cl.exe'
$msvcRoot = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231'
$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
$includes = @('/imsvc' + $msvcRoot + '\include', '/imsvc' + $sdkRoot + '\Include\10.0.26100.0\ucrt', '/imsvc' + $sdkRoot + '\Include\10.0.26100.0\shared', '/imsvc' + $sdkRoot + '\Include\10.0.26100.0\um')
$sources = @('C:\dev\jjflex-49r\native\rnvad011\rnvad011.c', (Join-Path $sourceRoot 'src\rnn_data.c')) + (@('pitch','kiss_fft','celt_lpc') | ForEach-Object { Join-Path $sourceRoot "src\$_.c" })
foreach ($nativeArch in @('x64','x86')) {
    $outputRoot = Join-Path $nativeRoot "rnnoise-vad011-$nativeArch"
    Remove-Item -Recurse -Force $outputRoot -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
    Push-Location $outputRoot
    try {
        $target = if ($nativeArch -eq 'x64') { 'x86_64-pc-windows-msvc' } else { 'i686-pc-windows-msvc' }
        & $compiler "--target=$target" /nologo /LD /O2 /MT /fp:precise /DWIN32 /D_USE_MATH_DEFINES "/I$sourceRoot\include" "/I$sourceRoot\src" @includes @sources /link /DEBUG:NONE /INCREMENTAL:NO /Brepro "/LIBPATH:$msvcRoot\lib\$nativeArch" "/LIBPATH:$sdkRoot\Lib\10.0.26100.0\ucrt\$nativeArch" "/LIBPATH:$sdkRoot\Lib\10.0.26100.0\um\$nativeArch" /OUT:rnnoise_vad011.dll
        if ($LASTEXITCODE -ne 0) { throw "Native build $nativeArch failed: $LASTEXITCODE" }
    } finally { Pop-Location }
}
```

Copy `build-native/rnnoise-vad011-x64/rnnoise_vad011.dll` here and the x86
counterpart to `runtimes/win-x86/native/`. Its licence is the **v0.1.1**
`COPYING`, from git's blob
(`git -C build-native/rnnoise-v011 cat-file blob 6cbfd53eb348a8d394e0757b4025c6ded34eb2b6:COPYING`),
beside each as `rnnoise_vad011.COPYING.txt`.

Shipped SHA-256 and sizes:

- x64 DLL: `3DF015C8385286E135CF128A6B4757F6688AE484AF0BEE058D9022829A469637`, 260,096 bytes, machine `0x8664`.
- x86 DLL: `47617A180A54AA07D4378FE7C2091F44952553BC1257DC0016DD1B197D098BCF`, 224,256 bytes, machine `0x14C`.
- `rnnoise_vad011.COPYING.txt` (both): `E2F59FF41D9D03ADC3DCF3DEFF170F8C8CF4A6EB4A9B174762A7656D23200FFA`,
  1,581 bytes, LF; byte-identical to upstream's blob `01ea4b1a` and to the
  `rnnoise.COPYING.txt` that shipped with v0.1.1. Three-clause BSD.
- Depends only on `KERNEL32.dll`; no `.pdb` reference or builder path. Two
  clean rebuilds were byte-identical.

**The two libraries' exports, side by side** (`dumpbin /exports`, both
architectures alike; `RnnoiseVoiceDetectorTests` reads both tables and fails
on any shared name):

- `rnnoise.dll` (the engine): `rnnoise_create`, `rnnoise_destroy`,
  `rnnoise_get_frame_size`, `rnnoise_get_size`, `rnnoise_init`,
  `rnnoise_model_free`, `rnnoise_model_from_buffer`, `rnnoise_model_from_file`,
  `rnnoise_model_from_filename`, `rnnoise_process_frame`.
- `rnnoise_vad011.dll` (the detector): `rnvad011_create`, `rnvad011_destroy`,
  `rnvad011_frame_size`, `rnvad011_process_frame`, `rnvad011_reference_frame`.
- **No name in common, and none of the detector's begins with `rnnoise_`.**

**Positive control.** On 20 s of noisy speech (the reference voice with seeded
noise), the detector's probability equals the full v0.1.1 frame's return value
**bit for bit on all 2,000 frames**, 1,362 of them above 0.5, on x64 and x86.
The full frame in turn reproduces, by SHA-256 of its probabilities and of its
audio, the v0.1.1 `rnnoise.dll` this folder shipped until 2026-11-08
(`97C0940D...` and `48EF2397...`, digests recorded from that DLL in a separate
run; both architectures gave the same digests). Each half is shown able to
fail: one input sample changed by one 16-bit step on the detector's side
breaks the first; one `vad_output` weight patched in a temporary copy breaks
the second. A scratch build of `rnvad011.c` with one feature nudged by 1e-4
made the committed tests fail, and the shipped build passes. Over the 22
comparison files (104,728 frames) the detector also matched v0.1.1's full
frame on every frame.

**Cost**, same machine and method as above: the detector **23 µs per frame on
a performance core and 26 on an efficiency core, x64; 24 and 27, x86**, against
96 and 75 (x64), 98 and 79 (x86) for the full v0.1.1 frame. About a quarter to
a third of the full frame, because the feature analysis (FFTs and the pitch
search) still runs; under 0.3 percent of one core.

The C# wrapper is `JJFlexWpf/RnnoiseVoiceDetector.cs`. It loads this file by
absolute path, the way `RnnoiseEngine.cs` loads `rnnoise.dll`; there is no
`NativeLoader.vb` locator for it yet, because nothing in the app constructs one.

### RNNoise v0.1.1 (rnnoise.dll until 2026-11-08) — history

**Not shipped as `rnnoise.dll` since 2026-11-08.** Kept as the record of the build that
shipped from 2026-11-02 (Sprint 49 Track D) until Track R moved the engine to the 0.2
line. The v0.1.1 model still ships, inside `rnnoise_vad011.dll` above, and the tests
hold that library's full frame to this build's output by digest.

RNNoise is Xiph's BSD-style speech denoiser. We build both architectures
because the retired package shipped the same x64 binary in its x86 folder
(#739). The model was the classic one: repository
`https://github.com/xiph/rnnoise`, tag `v0.1.1`, full commit
`6cbfd53eb348a8d394e0757b4025c6ded34eb2b6`. Its weights are `src/rnn_data.c`
in git; no model download is needed. Build only at that SHA, with a clean
tree. Sources below are `librnnoise_la_SOURCES` from its `Makefile.am`.

Exact build run 2026-11-02, from the repository root, after placing the
upstream checkout at `build-native/rnnoise`:

```powershell
$nativeRoot = 'C:\dev\jjflex-49d\build-native'
$sourceRoot = Join-Path $nativeRoot 'rnnoise'
if ((git -C $sourceRoot rev-parse HEAD) -ne '6cbfd53eb348a8d394e0757b4025c6ded34eb2b6') { throw 'Wrong RNNoise pin' }
if (git -C $sourceRoot status --porcelain) { throw 'Dirty RNNoise source' }
$compiler = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\Llvm\x64\bin\clang-cl.exe'
$msvcRoot = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.51.36231'
$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
$includes = @('/imsvc' + $msvcRoot + '\include', '/imsvc' + $sdkRoot + '\Include\10.0.26100.0\ucrt', '/imsvc' + $sdkRoot + '\Include\10.0.26100.0\shared', '/imsvc' + $sdkRoot + '\Include\10.0.26100.0\um')
$sources = @('denoise','rnn','rnn_data','rnn_reader','pitch','kiss_fft','celt_lpc') | ForEach-Object { Join-Path $sourceRoot "src\$_.c" }
foreach ($nativeArch in @('x64','x86')) {
    $outputRoot = Join-Path $nativeRoot "rnnoise-$nativeArch"
    New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
    Push-Location $outputRoot
    try {
        $target = if ($nativeArch -eq 'x64') { 'x86_64-pc-windows-msvc' } else { 'i686-pc-windows-msvc' }
        & $compiler "--target=$target" /nologo /LD /O2 /MT /fp:precise /DWIN32 /DRNNOISE_BUILD /DDLL_EXPORT /D_USE_MATH_DEFINES "/I$sourceRoot\include" "/I$sourceRoot\src" @includes @sources /link /DEBUG:NONE /INCREMENTAL:NO "/LIBPATH:$msvcRoot\lib\$nativeArch" "/LIBPATH:$sdkRoot\Lib\10.0.26100.0\ucrt\$nativeArch" "/LIBPATH:$sdkRoot\Lib\10.0.26100.0\um\$nativeArch" /OUT:rnnoise.dll
        if ($LASTEXITCODE -ne 0) { throw "Native build $nativeArch failed: $LASTEXITCODE" }
    } finally { Pop-Location }
}
```

Compiler: clang 22.1.3, LLVM commit
`e9846648fd6183ee6d8cbdb4502213fcf902a211`, MSVC libraries 14.51.36231,
Windows SDK 10.0.26100.0. The MSBuild ClangCL toolset is not required.

- `--target` selects genuine x64 or x86 code; `/LD` makes a DLL.
- `/O2 /fp:precise` optimizes without fast-math changes. clang-cl accepts
  the upstream variable-length arrays that Microsoft's C compiler rejects.
- `/MT` links the C runtime statically. Both binaries depend only on
  `KERNEL32.dll`, verified with `dumpbin /dependents`.
- `WIN32`, `RNNOISE_BUILD`, and `DLL_EXPORT` enable the exports in the pinned
  header; `_WIN32` alone does not. `_USE_MATH_DEFINES` exposes `M_PI`.
- Include and library paths explicitly select the installed toolchain.
- `/DEBUG:NONE /INCREMENTAL:NO` avoids a PDB path and incremental link state.
  Neither DLL contains a `.pdb` reference or the builder's user-profile path.

Copy `build-native/rnnoise-x64/rnnoise.dll` to this folder and the x86
counterpart to `runtimes/win-x86/native/`. Copy upstream's unmodified
`COPYING` beside each as `rnnoise.COPYING.txt`.

SHA-256 and sizes as shipped then:

- x64 DLL: `97C0940D963B549D5DC97F1A165AAD8A6EA4815BEA9774FF8004DFE265DE57F6`, 293,888 bytes.
- x86 DLL: `48EF23974BDD824A805CEB6E55DAF4C8DB7D12C37C121C5793EC3F71FD527F82`, 254,976 bytes.
- `rnnoise.COPYING.txt` (both): `E2F59FF41D9D03ADC3DCF3DEFF170F8C8CF4A6EB4A9B174762A7656D23200FFA`, 1,581 bytes, as git stores and checks it out (`.gitattributes` sets `eol=lf`); byte-identical to upstream's own `COPYING` blob `01ea4b1a`. A Windows checkout that converts it to CRLF reads 1,612 bytes and a different hash.

There is no version function or embedded version string. The full source
SHA is the record; machine field, DLL hash and exports identify a copy.
Read the PE offset at byte 0x3c, then the two-byte machine field at that
offset plus 4: x86 is `0x14C`, x64 is `0x8664`. Positive control: the retired
package's **x86** DLL reads `0x8664` (SHA-256
`12E19BF7A18D13E092A5FBE5A7C5B2081F5E7B56F6D77AEAB5837335F44CEEDF`).

Run `dumpbin /exports` on both files. Positive control:
`rnnoise_process_frame` must be present, along with `rnnoise_create`.
The complete undecorated export list is `rnnoise_create`, `rnnoise_destroy`,
`rnnoise_get_frame_size`, `rnnoise_get_size`, `rnnoise_init`,
`rnnoise_model_free`, `rnnoise_model_from_file`, `rnnoise_process_frame`.
The binding is cdecl, output pointer before input, 480 samples at 48 kHz,
16-bit-range floats. The process return value is voice probability.
The export list alone cannot distinguish our pin from the old package.

Model comparison before retiring the package: 300 identical 480-sample
frames through each x64 DLL in **separate processes**, at equal 16-bit
input level, gave largest absolute sample difference **0.0** over 144,000
samples. This confirms the classic model is unchanged for that input.

To bump: place a clean checkout at the new full pin, read `Makefile.am`
and `include/rnnoise.h` again, check whether weights now need a separate
download (v0.2 and later do), rebuild both architectures, replace both DLLs
and licences, update pin, recipe and hashes here, and run the RNNoise tests
on both architectures. Compare model output deliberately before adopting a
new model. A linker timestamp can change hashes on an otherwise equal rebuild.

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

### libspecbleach (specbleach.dll) — NR4, built here from vendored source

**What it is.** libspecbleach is Luciano Dato's single-channel spectral
denoiser, from `https://github.com/lucianodato/libspecbleach`, licensed **LGPL
2.1**. It is JJ Flexible's **NR4**: an adaptive spectral subtractor that keeps
estimating the noise continuously, with no capture step. As of Sprint 49 Track C
it is built, tested and wrapped as an engine (`JJFlexWpf/SpecbleachEngine.cs`),
but **nothing an operator can reach selects it**, and the release installer
still leaves it out (`install template.nsi`, the `specbleach.*` exclusion) until
the operator surface lands.

**Loaded by absolute path, and replaceable.** The engine loads
`runtimes\win-{arch}\native\specbleach.dll` by its full path (located by
`NativeLoader.vb`) and binds its functions by name; it is never a `DllImport`
and never found through the Windows search order. As the LGPL intends, an
operator may replace this file with their own build of the library: the engine
traces the version it finds and does not refuse a different one, and the
parameter block carries a size check (below) that makes an incompatible layout
fail safely rather than read the wrong memory.

Three files, identical layout in both architecture folders:

- `specbleach.dll`, the library. CMake's `OUTPUT_NAME` is `specbleach`, so MSVC
  produces this name; it is not renamed.
- `LICENSE-libspecbleach.txt`, the tag's `LICENSE` (LGPL 2.1), unchanged.
- `LICENSE-libspecbleach-pffft.txt`, the tag's `thirdparty/pffft/LICENSE.txt`,
  unchanged. PFFFT is the FFT library compiled into the DLL, under its own
  BSD-style notice.

The licence files are named `LICENSE-...` so that the installer's
`specbleach.*` exclusion does not catch them when it is lifted.

**Pinned at tag `v0.4.1`, commit `ab4ae8b4be74f5c52c5a3386a6d9bbea29fbd129`.**
**The source offer: the exact tree of that tag is in this repository**, at
`third_party/libspecbleach/`, and the DLLs are built from it. Its git tree hash
equals the tag's (`f3875ef9dce587672da31dcd3fb660ecce01a44e`); how it was taken
and how to check it is in `third_party/VENDORED-libspecbleach.md`. Nothing in
that folder is ever edited: an edited LGPL library is a modified work with
publishing obligations of its own.

**Why 0.4.0 or later, and never an older copy.** Up to 0.3.1 libspecbleach
links FFTW, which is GPL; 0.4.0 replaced it with the vendored PFFFT
(`CHANGELOG.md`, "PFFFT Migration"). At this tag nothing compiled includes
`fftw3.h` or calls an `fftw_` function: a case-insensitive search of the tree
for `fftw` finds only two places, the changelog and a comment in
`src/shared/spectral_processor.h`. **An older copy that links FFTW exists on
the machine that built this, and must never be used, copied or built from**:
it would put GPL code into JJ Flexible's process. It also has the old API
(`specbleach_initialize` and friends), which this binding does not speak.

**Exact build, as run 2026-11-02** with Visual Studio 2026 Community, MSVC
19.51.36256 (toolset 14.51.36231), using the CMake that ships inside Visual
Studio (4.3.1-msvc1), from PowerShell:

```powershell
$cm  = 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$src = '<repo>\third_party\libspecbleach'          # the vendored tree; build OUT of it
foreach ($pair in @(@('x64','x64'), @('Win32','x86'))) {
  $bld = "<a folder outside the repo>\build-jjflex-$($pair[1])"
  & $cm --fresh -S $src -B $bld -G "Visual Studio 18 2026" -A $pair[0] `
     -DBUILD_SHARED_LIBS=ON -DENABLE_AVX=OFF -DENABLE_TESTS=OFF -DENABLE_EXAMPLES=OFF `
     -DENABLE_SANITIZERS=OFF -DENABLE_COVERAGE=OFF -DSPECBLEACH_BUILD_EXTRAS=OFF -DSPECBLEACH_INSTALL=OFF `
     "-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded" "-DCMAKE_C_FLAGS_INIT=/experimental:c11atomics"
  & $cm --build $bld --config Release
}
# Copy build-jjflex-x64\Release\specbleach.dll here, and build-jjflex-x86\Release\specbleach.dll
# to ..\..\win-x86\native\. Copy LICENSE and thirdparty\pffft\LICENSE.txt from the vendored
# tree beside each, as LICENSE-libspecbleach.txt and LICENSE-libspecbleach-pffft.txt.
```

No source file was changed; every adjustment is a flag on the command line.
Why each one:

- **MSVC, not clang-cl.** Upstream builds only on Linux in CI, so MSVC was
  unproven; it builds this tag cleanly with the two flags below. clang-cl would
  NOT have: it defines `__x86_64__`, which makes `nlm_filter.c` reference the
  AVX kernel that `-DENABLE_AVX=OFF` leaves out of the build.
- **`/experimental:c11atomics`.** Three files include `<stdatomic.h>`, which
  MSVC rejects with "C atomic support is not enabled" unless this flag is given.
  `CMAKE_C_FLAGS_INIT` adds it to CMake's defaults rather than replacing them.
- **`-DENABLE_AVX=OFF`, on both architectures.** Only the 2D smoother uses the
  AVX kernel, and the low-latency mode NR4 runs (with temporal smoothing) never
  reaches that smoother, so AVX buys nothing; and an AVX instruction on a
  processor without AVX ends the whole process. With the option off the AVX
  source file is not compiled, and under MSVC the run-time switch to it is
  compiled out as well (its guard tests GCC and clang macros).
  **Checked in the binary:** the x86 DLL contains no `ymm` register at all
  (`dumpbin /disasm`). The x64 DLL contains 101, and a link map
  (`/MAP`, on an otherwise identical scratch build) puts every one of them in
  the Visual C++ runtime's own `memcpy` and `memset`, which choose their AVX
  paths only after a processor check at start-up. None is libspecbleach's.
- **`CMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded`** links the C runtime in
  statically, as `portaudio.dll` is built, so the DLL needs no Visual C++
  redistributable. Its only import is `KERNEL32.dll` on both architectures
  (`dumpbin /dependents`), which also proves no FFTW is linked.
- **Tests, examples, sanitizers, coverage, the extras layer and the install
  step are all off**; only the library is built.

**Record of what is shipped:**

- `specbleach.dll` x64: machine `8664`, SHA-256
  `E8D4831D667D27C358B70437258FEB5DCD115C2D97A34052BD1D37E9723BFB68`, 267,264 bytes
- `specbleach.dll` x86: machine `14C`, SHA-256
  `4409D2C1FA0E8F69F707F52425CBF2BFB33DAC8B60D2E01BA342EBFFE2E3CC86`, 222,208 bytes
- `LICENSE-libspecbleach.txt` (both folders):
  `20C17D8B8C48A600800DFD14F95D5CB9FF47066A9641DDEAB48DC54AEC96E331`, LF line endings
- `LICENSE-libspecbleach-pffft.txt` (both folders):
  `6C99FCE38C991FF104307ECEBE20FF014B5E1F5D9E3426DA292DA8417C7A78CD`, LF line endings

Both DLLs export the 25 functions of `include/specbleach_denoiser.h` and
`include/specbleach_version.h` under their plain C names
(`dumpbin /exports`), on x86 as well as x64. A rebuild will not reproduce the
DLL hashes bit for bit (the linker stamps a time).

**The version string is the Prism trap again.**
`specbleach_get_version_string()` returns `libspecbleach 0.4.1`, but that text
is a macro in a header (`SPECBLEACH_VERSION_STRING`), so any build made from a
tree past the tag still says `0.4.1`. **The commit SHA above is the record**;
the string is honest only because this DLL is built from the vendored tree
exactly at the tag.

**The parameter block is the dangerous part of the binding, and it is
guarded.** `SpecbleachDenoiserParameters` mixes four-byte enums, floats,
one-byte C `bool`s and a pointer, so its size differs by architecture: 88 bytes
on x64 (the pointer at offset 64) and 76 on x86 (the pointer at 60).
`specbleach_denoiser_load_parameters` takes the caller's size and refuses any
other with `false`; the engine treats that `false` as a hard failure.
`SpecbleachEngineTests` pins the layout field by field, by value, on both
architectures.

**To bump it:** take the new tag into `third_party/libspecbleach/` as
`VENDORED-libspecbleach.md` describes, re-read `include/specbleach_denoiser.h`
against the binding (field order of the parameter block above all), rebuild
both architectures with the recipe, replace all three files in BOTH folders,
update every hash and the SHA here, and run `SpecbleachEngineTests` on x64 and
x86.

## Where the build trees live

`build-native/` at the repo root holds working clones of both projects.
DeepFilterNet's clone is kept outside the repo entirely
(`C:\dev\deepfilternet-build` on the machine that built it); like
`build-native/`, it is a convenience and not a record. **That
directory is gitignored**, so nothing in it is part of the repository — it is a
local convenience, not a record. libspecbleach is the exception that proves
the rule: its source IS kept, in `third_party/libspecbleach/`, because the LGPL
asks for it, and it builds out of that tree into a folder outside the repo.
**This file is the record.** Anything future
rebuilds need to know belongs here, not in a note inside `build-native/`.

Deliberately not a git submodule: we ship binaries rather than source, rebuilds
happen about once every few years given PortAudio's release cadence, and a
submodule would tax every clone forever to serve that. The version stamped
inside the DLL also identifies a shipped artifact in the field, which a
submodule cannot.

## Prebuilt binaries

FLAC: `libFLAC.dll` for x64 and x86 comes from Xiph's `flac-1.5.0-win.zip`,
SHA-256 `53f1500f0d6e7c61379d7fee50d4a9f7f504c650009506d9ba015530d76c0dde`.
The binaries have no Windows version resource; both embed `reference libFLAC 1.5.0`.
The New BSD licence is shipped beside each DLL as `libFLAC.COPYING.Xiph.txt`.
Only the library DLL is shipped; the zip's command-line tools are not.

- Opus: https://opus-codec.org/downloads/ — or vcpkg, whose port tracks 1.6.1.
- PortAudio: **do not use vcpkg or the stable tarball**; both are 19.7.0. Build
  from the pinned master commit above.
