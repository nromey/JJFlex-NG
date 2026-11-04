# libspecbleach, vendored

`third_party/libspecbleach/` is the complete, unmodified source tree of
libspecbleach at one pinned tag. It is the source of the `specbleach.dll` files
shipped under `runtimes/win-x64/native/` and `runtimes/win-x86/native/`, which
JJ Flexible uses as its NR4 noise reducer, and it is here so that the source of
what we ship is available from the same place as the binaries, as the LGPL
intends.

- Upstream: https://github.com/lucianodato/libspecbleach
- Licence: GNU Lesser General Public License 2.1 or later (the tree's own
  `LICENSE`). PFFFT, compiled into the same DLL, carries its own BSD-style notice
  in `thirdparty/pffft/LICENSE.txt`.
- Tag: `v0.4.1`
- Commit: `ab4ae8b4be74f5c52c5a3386a6d9bbea29fbd129`
- Tree: `f3875ef9dce587672da31dcd3fb660ecce01a44e`
- Taken: 2026-11-02, by Sprint 49 Track C

## How it was taken

From a clone of the upstream repository whose `git rev-parse HEAD` printed the
commit above and whose `git status --short` printed nothing:

```
git -c core.autocrlf=false archive --format=tar ab4ae8b4be74f5c52c5a3386a6d9bbea29fbd129 | tar -x -C third_party/libspecbleach
```

`core.autocrlf=false` matters on Windows: without it `git archive` converts the
text files to CRLF and the copy is no longer byte-identical to the tag. The root
`.gitattributes` marks this folder `-text` for the same reason, so a checkout
of this repository never converts it either. Two files are executable upstream
(`tests/generate_reference_files.sh` and `tests/test_data/Speech.wav`) and were
staged with `git update-index --chmod=+x` so their modes match.

## How to check it has not been touched

The folder's git tree hash must equal the tag's tree hash:

```
git rev-parse HEAD:third_party/libspecbleach
```

must print `f3875ef9dce587672da31dcd3fb660ecce01a44e`, which is what
`git rev-parse v0.4.1^{tree}` prints in an upstream clone. Equal tree hashes
mean every file name, mode and byte is the same, with nothing added and nothing
missing. A recursive diff against a fresh `git -c core.autocrlf=false archive`
of the commit says the same thing more slowly.

## Rules

- **Never edit a file under `third_party/libspecbleach/`.** An edited LGPL
  library is a modified work with its own publishing obligations. That decision
  belongs to the project owner, not to a build fix.
- **No project compiles anything here.** The C sources are never part of a .NET
  build. The DLLs are built from this tree out of tree, into a build folder
  outside the repository; the exact commands are in
  `runtimes/win-x64/native/README.md`, section "libspecbleach".
- **To bump the pin:** take the new tag the same way into a clean folder,
  replace this one entirely, update the tag, commit and tree above, and rebuild
  both DLLs from the new tree with the README's recipe. Releases up to 0.3.1 link
  FFTW, which is GPL, and must never be used.
