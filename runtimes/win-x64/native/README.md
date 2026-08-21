# x64 Native Libraries

This folder holds the x64 native libraries: `libopus.dll` and `portaudio.dll`
(audio), and `prism.dll` (speech and braille — the screen-reader backend).
The same recipes build the x86 set — substitute `Win32` for `x64` and target
`runtimes/win-x86/native/`.

## What is currently shipped — verify, do not trust this heading

- **Opus 1.6.1**
- **PortAudio: master pinned at `a880212` (commit date 2026-08-07)**
- **Prism v0.18.1, pinned at tag commit `d2998e9281806fe1efd3394971c6e44ac11b9e75`**

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

## Where the build trees live

`build-native/` at the repo root holds working clones of both projects. **That
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
