# x86 Native Libraries

This folder holds the 32-bit native audio libraries, `libopus.dll` and
`portaudio.dll`, plus the screen-reader bridges (`Tolk.dll`, `SAAPI32.dll`,
`nvdaControllerClient32.dll`, `dolapi32.dll`).

## The build recipe lives with the x64 pair

**See `runtimes/win-x64/native/README.md`.** It carries the pinned versions, the
exact clone, checkout and cmake lines, both rename traps, and why PortAudio is
built from a master commit rather than a release. **Deliberately not duplicated
here** — two copies of a build recipe drift, and this project has spent real
time this month on documentation that no longer matched the thing it described.

**To build 32-bit, use that recipe with two substitutions:**

- `-A Win32` instead of `-A x64`
- output into `build-x86` and copy the results into **this** folder

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

Both architectures were confirmed matching on 2026-08-11. **Ignore the
`19.7.0`** in PortAudio's string; upstream never bumped that constant, so it is
identical on a current build and a five-year-old one. Only the `revision`
suffix identifies anything.
