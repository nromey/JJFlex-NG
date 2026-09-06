# x64 Native Libraries

This folder holds the x64 native libraries: `libopus.dll` and `portaudio.dll`
(audio), `prism.dll` (speech and braille — the screen-reader backend), and
`nvdaControllerClient.dll` (NV Access's own controller client, used ONLY to
ask NVDA whether an utterance was spoken to the end — see #521).
The same recipes build the x86 set — substitute `Win32` for `x64` and target
`runtimes/win-x86/native/`.

## What is currently shipped — verify, do not trust this heading

- **Opus 1.6.1**
- **PortAudio: master pinned at `a880212` (commit date 2026-08-07)**
- **Prism v0.18.1, pinned at tag commit `d2998e9281806fe1efd3394971c6e44ac11b9e75`**
- **NVDA controller client 2.0, from the NVDA 2026.2 release archive** (not
  built here — downloaded; see its section below for the hashes)

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
