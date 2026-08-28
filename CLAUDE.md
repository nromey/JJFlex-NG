# JJ Flexible Radio Access

Windows desktop application for controlling FlexRadio transceivers (6000/8000 series). Alternative UI to SmartSDR, created by Jim Shaffer, maintained by Noel Romey (K5NER). Current version: 4.1.16. **FlexLib version depends on which branch you are on, and this matters:**

- **`main` now vendors FlexLib v4.2.20.41343**, as of **2026-08-11**: the long-pending clean fast-forward from the audio arc landed 349 commits and brought `track/flexlib-4220` fully onto main. Main and the track branch no longer diverge on FlexLib.
- **`track/flexlib-4220`** — where 4.2.20 originally landed 2026-08-03 in `b2d75f63` (3-way merge from `track/flexlib-42`, all patches carried). Retained for history; main is now the place to work.
- **Historical:** main sat on **v4.1.5.39794** from 2026-03-18 (`506c2ff9`, for 8000-series/Aurora compatibility) until the 2026-08-11 fast-forward. The 4.2.18 merge was reverted 2026-05-15, restoring 4.1.5 — that revert is why main lagged for so long.

**Check your branch before citing a FlexLib line number or behaviour.** This paragraph has been wrong **four** times: it claimed 4.1.5 unconditionally until 2026-08-09, six days after 4.2.20 landed on the track branch, and a session acted on the wrong version as a result; before that it said "4.1.15 / FlexLib v4.0.1" until 2026-08-03; and it described main as 4.1.5 and 305 commits behind until the 2026-08-11 merge made that false. **Verify rather than trusting it** — `git log --oneline -1 -- FlexLib_API/` and a diff against the branch you are on take seconds.

**Naming, as of the 2026-08-04 rename:** the shipped executable is `jjflexible.exe` (renamed ahead of the first code-signed release so SmartScreen reputation accrues to the final file identity). Everything else keeps the `JJFlexRadio` name on purpose — AppData (`%AppData%\JJFlexRadio\`), the HKLM registry keys, the install directory (`Program Files\JJFlexRadio`), the Start Menu/desktop shortcut names, `JJFlexRadio.chm`, the solution and project filenames, and the support DLLs (`Radios.dll`, `JJFlexWpf.dll`, …). Existing installs upgrade in place and keep their settings. `RootNamespace` stays `JJRadio`.

## Decision-Making Mindset

You are a pair coder, not a human contractor. Do NOT constrain decisions by human limitations:
- **Never give time estimates** or say "a couple hours of work" — you type at machine speed.
- **Always choose the right solution**, not the fastest to implement. If Option B is cleaner but more code, choose Option B. You are not tired, you are not billing hourly.
- **Don't propose incremental patches** when a proper fix is clearly better. Patching legacy code just to replace it next sprint is wasted work.
- **Parallel tracks are cheap** — the user has compute credit and can run multiple CLI sessions. Don't artificially serialize work to "keep scope small."
- Think in terms of **what's the right architecture** not **what can I get done before lunch**.

## Quick Reference

| Item | Value |
|------|-------|
| Solution | `JJFlexRadio.sln` |
| Build x64 (Release) | `dotnet build JJFlexRadio.sln -c Release -p:Platform=x64` |
| Build x86 (Release) | `dotnet build JJFlexRadio.sln -c Release -p:Platform=x86` |
| Build (Debug) | `dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64` |
| Rebuild (Release) | `dotnet clean JJFlexRadio.sln -c Release -p:Platform=x64 && dotnet build JJFlexRadio.sln -c Release -p:Platform=x64` |
| Installer | Runs automatically after Release build |
| Output x64 | `bin\x64\Release\net10.0-windows\win-x64\jjflexible.exe` |
| Output x86 | `bin\x86\Release\net10.0-windows\win-x86\jjflexible.exe` |
| Installer x64 | `Setup JJFlex_[version]_x64.exe` |
| Installer x86 | `Setup JJFlex_[version]_x86.exe` |

**Note**: Use `dotnet build` (preferred) or `msbuild` if in VS Developer shell. Add `--verbosity minimal` to reduce output noise.

**Important**: Close running JJFlexRadio before building (Radios.dll lock).

## Tech Stack

- **Languages**: VB.NET (main app) + C# (libraries)
- **Framework**: `net10.0-windows` (.NET 10)
- **Platforms**: x64 (primary), x86 (legacy support)
- **UI**: WinForms (primary), WPF (UiWpfFramework)
- **Auth**: WebView2 (Edge/Chromium) for SmartLink Auth0
- **Native deps**: **Opus 1.6.1**, **PortAudio master pinned at `a880212` (2026-08-07)**, **Prism v0.18.1 pinned at `d2998e9281806fe1efd3394971c6e44ac11b9e75` (the tag commit; built 2026-08-21 from a clean tree exactly at the tag)** — architecture-specific in `runtimes/`. **Prism's `prism_version_string()` has the PortAudio trap in a different coat:** it reports CMake's PROJECT_VERSION, stamped at configure time, so a build made past the tag still reports the tag — the DLL shipped until 2026-08-21 said `0.17.3` while being 46 commits newer (`v0.17.3-46-g9ae0ece`) and exporting functions a genuine 0.17.3 could not have. The string is honest ONLY under the build policy in `runtimes/win-x64/native/README.md` (build exactly at the pinned tag, clean tree); **the pinned SHA here is the record, the string is a convenience.** After the 2026-08-21 swap the string reads `0.18.1` on both arches, consistent with the pin. Prism appears in NO project file — it is P/Invoked from `Radios/Speech/PrismNative.cs` and resolved by `NativeLoader.vb`; grepping `*.csproj` for it proves nothing. **PortAudio has no release newer than 19.7.0 (March 2021)**, so we build from master; the pinned SHA is recorded in `build-native/portaudio-pinned-commit.txt` and stamped into the DLLs themselves via `-DPA_GIT_REVISION`, so `Pa_GetVersionText()` reports `"PortAudio V19.7.0-devel, revision a880212"`. **Do not trust the 19.7.0 in that string — upstream never bumped it, and a five-year-old build reports the identical text.** The revision suffix is the only honest identifier; always stamp it when rebuilding (`cmake -DCMAKE_C_FLAGS=/DPA_GIT_REVISION=<sha>`). **Provenance of the old build, confirmed by Noel 2026-08-12:** the DLL we shipped until then was **his own compile**, made after he inherited Jim's code in early 2026 — which is why it read `-devel` (built from a working tree rather than a packaged release) with `revision unknown` (nobody injected the SHA). **The source he compiled sits at commit `147dd72`, whose own date is 2021-04-01 — essentially the 19.7.0 stable release point.** Note the two dates are different things: he compiled it recently, from source that was already five years stale. **That is not a mistake, it is the trap** — PortAudio's newest *release* is 19.7.0 from March 2021, so anyone doing the conventional right thing and taking the latest stable download lands exactly there, while every Windows backend fix since lives unreleased on master. Hence the current policy above: build from a pinned master commit and stamp the revision. This line claimed Opus 1.5.2 until 2026-08-11 and was wrong — the shipped DLLs are 1.6.1 on **both** x64 and x86. **Verify, do not trust this line:** the DLLs carry no Win32 version resource, but both embed a readable version string, so `opus_get_version_string()` / `Pa_GetVersionText()` — or simply searching the binary for `libopus 1.` / `PortAudio V` — gives the truth in seconds. PortAudio reports "revision unknown", meaning it was built without git revision info; if it is ever rebuilt from a master snapshot, record the snapshot date here the way FlexLib's version is pinned above.
- **Installer**: NSIS via `install.bat` + `install template.nsi`

## Project Structure

```
JJFlexRadio.vbproj      Main WinForms app (VB.NET entry point)
Radios/                 Radio abstraction layer (Flex-only)
  FlexBase.cs           Base class for Flex radios
  AllRadios.cs          Rig table and discovery
  Flex6300Filters.cs    Filter/DSP controls UI
FlexLib_API/            Vendor FlexLib v4 + wrappers
  FlexLib/              Core Flex radio API
  Util/                 Utilities (audio, network, etc.)
  Vita/                 VITA-49 protocol implementation
  UiWpfFramework/       WPF MVVM helpers
JJPortaudio/            PortAudio wrapper
P-Opus-master/          Opus codec wrapper
JJLogLib/               Logging library
JJTrace/                Trace/debug utilities
docs/                   Extended documentation
```

## Key Patterns

### TLS Enforcement
Custom TLS 1.2+ wrapper enforces modern TLS without editing vendor FlexLib:
- Wrapper: `FlexLib_API/FlexLib/SslClientTls12.cs`
- App-wide floor: `ApplicationEvents.vb` sets `ServicePointManager.SecurityProtocol`
- **After FlexLib upgrades**: Reapply wrapper per `MIGRATION.md`

### Feature Gating
Use FlexLib APIs to check feature availability before exposing UI:
```csharp
// Check license
if (theRadio.FeatureLicense?.LicenseFeatDivEsc?.FeatureEnabled == true) { ... }

// Check hardware capability
if (theRadio.DiversityIsAllowed) { ... }  // 2-SCU radios only

// Check resources (MultiFlex awareness)
if (theRadio.AvailableSlices >= 2) { ... }
```

### Naming a namespace under `Radios`

**Check `Radios.<X>` against `System.<X>` before you write it.** In VB a
`Radios.<X>` namespace SHADOWS `System.<X>` for every file with `Imports
Radios`, silently — no warning, no error, the wrong type simply gets used, and
the build breaks in a file you never opened. Sprint 32 Track C named its
namespace `Radios.Diagnostics`, the Radios project compiled clean, and the
solution build failed with four errors in `PersonalData.vb`. It renamed to
`Radios.ChainChecks`.

The natural name for a subsystem is very often a `System.` child — Diagnostics,
Threading, Text, IO, Timers, Net, Security, Runtime, Globalization,
Collections, Reflection, Media, Windows, Xml, Linq. (Deliberately unbackticked:
those are words in a warning, not symbols to go and find, and the instruction
sweep rightly reads a backticked name as the latter.) The C# side compiles
either way, so a project-level build tells you nothing.

`Radios.Tests/RadiosNamespaceShadowingTests.cs` now refuses the collision:
it reads the second-level names under `Radios` (committed AND untracked, so a
brand-new file counts) and compares them against every namespace reachable
from the `<Import>` list in `JJFlexRadio.vbproj`. **Nothing collides today** —
verified 2026-08-27 — and the one near-miss, `Radios.Speech` versus
`System.Speech`, is held open only because the VB app does not reference that
package. A second test says so, so the day somebody adds it, a test explains
why rather than `PersonalData.vb`.

### Accessibility Guidelines
- Remove `&` from menu labels (interferes with screen readers)
- Always Set `AccessibleName` and `AccessibleRole` on controls
- Keep unsupported/disabled controls out of tab order
- Use Feature Availability tab to explain why radio features are unavailable due to subscription unavailable or model spec deficiencies

### Changelog Conventions
The changelog (`docs/CHANGELOG.md`) is **user-facing** — it's read by hams, not developers. Write it accordingly:

- **Warm, personal, first-person tone**: "I fixed...", "You can now...", "This one's been on my list forever." The voice is a funny nerdy ham radio developer talking to friends. Read the existing entries for the vibe.
- **No internal jargon**: No track labels (Track A/B/C), sprint numbers, bug IDs (BUG-017), WPF, WinForms, ElementHost, async patterns, AutomationPeer, interop, or framework names. Users don't care about the plumbing.
- **Explain the *what*, not the *how***: "DSP toggles now tell you on or off" — not "Fixed async property pattern using local variable to capture toggled state before FlexLib round-trip."
- **Screen reader details are OK**: Our users *are* screen reader users. "Your screen reader now announces the callsign" is fine. Just don't say "added AutomationProperties.Name to the DataGrid row template."
- **Technical details live in planning docs**: Sprint plans, test matrices, `JJFlex-TODO.md`, and `Agent.md` are the developer record. The changelog is the user record.
- **Bullets report user state, not developer action**: A bullet that starts with a past-tense verb ("Fixed X", "Added Y") reads as a developer log entry. Restructure to article + noun + state ("The X is now fixed", "Y is now available") so the subject is a thing the reader interacts with, not the developer's action on it. This centers the reader's current reality, not the developer's past timeline. Noun-phrase-starting bullets that already describe state ("Slice cycling no longer wraps", "The Status Dialog holds your place") are fine as-is — the rule specifically targets action-verb openers. Bolded label bullets (**SWR after tune now gets announced**, **Crash fix: Callouts NRE**) follow a separate label+em-dash pattern and don't need restructuring.

## Build Notes

### Build Commands
```batch
# Build x64 Release (recommended)
dotnet build JJFlexRadio.sln -c Release -p:Platform=x64

# Build x86 Release (for older 32-bit systems)
dotnet build JJFlexRadio.sln -c Release -p:Platform=x86

# Minimal output (recommended for CI/automation)
dotnet build JJFlexRadio.sln -c Release -p:Platform=x64 --verbosity minimal

# Clean + rebuild (guaranteed fresh output)
dotnet clean JJFlexRadio.sln -c Release -p:Platform=x64 && dotnet build JJFlexRadio.sln -c Release -p:Platform=x64 --verbosity minimal
```

### NEVER run `dotnet test` without naming a project

```batch
dotnet test                          # NO
dotnet test JJFlexRadio.sln          # NO
dotnet test Radios.Tests/Radios.Tests.csproj -c Debug -p:Platform=x64      # yes
dotnet test JJFlexWpf.Tests/JJFlexWpf.Tests.csproj -c Debug -p:Platform=x64  # yes, deliberately
```

At solution scope `dotnet test` builds and runs **every** test project, and
`JJFlexWpf.Tests` **constructs real WPF dialogs on the interactive desktop.**
On 2026-08-25 a background agent ran the bare command and put a stream of
dialogs on Noel's screen while he was working; he had to close the application
to stop it. The same thing happened on 2026-08-20.

A guard now makes `JJFlexWpf.Tests` refuse rather than show windows
(`JJFlexWpf.Tests/Infrastructure/DeskGuard.cs`, commit `62356dc2`), and it
fails closed. Name the project anyway — a guard is a last line, not a licence.

`JJFLEX_TIER1_DESK_FREE=1` lifts the refusal. **It is a declaration by a human
who has stepped away from the machine.** Never set it in a script, never set
it in an agent brief, never set it "to get the tests running."

**WHEN BRIEFING AN AGENT, WRITE THE PROHIBITION, NOT JUST THE GOAL.** The brief
that caused this said "`dotnet test Radios.Tests/Radios.Tests.csproj` must stay
green" — which names success and leaves the dangerous route wide open. A goal
tells an agent what to achieve; only a prohibition tells it which paths are
closed, and an agent takes the shortest route to the goal. That route is
frequently the one nobody thought to forbid.

### WARNING: `--no-incremental` Does NOT Guarantee Fresh Builds

**Do NOT rely on `--no-incremental` to produce fresh binaries.** It only disables incremental *compilation* but the build system can still skip projects entirely if it believes outputs are up-to-date. This means:

- Output files may retain old timestamps
- The NSIS installer post-build step won't run (it only triggers when the project actually compiles)
- You can end up distributing stale binaries

**Always use `dotnet clean` before `dotnet build` when you need fresh output.** Or use `build-installers.bat` which deletes the output folder before building.

### CRITICAL: Verify Build Output After Every Build

**After every build, verify the exe timestamp matches the current time.** Stale binaries have wasted entire testing sessions. Run:

```batch
powershell -Command "(Get-Item 'bin\x64\Release\net10.0-windows\win-x64\jjflexible.exe').LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')"
```

If the timestamp doesn't match the current time, the build did NOT produce a fresh binary. Also note: building the **solution** (`JJFlexRadio.sln`) may skip the main project — always build the **project** directly (`JJFlexRadio.vbproj`) to be safe.

**THIS IS A BUILD-MACHINE CHECK AND IT DOES NOT SURVIVE DELIVERY.** On a
tester's disk the same timestamp means nothing at all: Dropbox re-stamps a
delivered file with the moment it finished syncing on the recipient's machine,
so the date drifts from every other tester's copy of the same build. Don read
his copy as a 2 AM publish; nobody had been awake (#268). Never ask a tester
"what's the date on the file?" and never reason from one.

What a delivered artifact IS good for: **`BUILD-INFO.txt`**, written into the
build tree before zipping so it travels inside the artifact, and the `Built:` /
`Commit:` lines in `NOTES-<version>-debug.txt` and `LATEST.txt`. All three are
produced by `build-debug.bat` and all three say the same thing, because they
are rendered from one identity block stamped with the exe's own last-write
time. The zip filename's 4-part version identifies the build exactly.

### Platforms
- Primary: x64 (64-bit, recommended)
- Legacy: x86 (32-bit, for older systems)
- Framework: `net10.0-windows` only

### Installer Generation
Installer runs automatically as post-build step for Release builds. Creates:
- `Setup JJFlex_[version]_x64.exe` (64-bit installer)
- `Setup JJFlex_[version]_x86.exe` (32-bit installer)

**Note the name is `JJFlex`, not `JJFlexRadio`.** This doc said `JJFlexRadio_` until
2026-08-03; a release script globbing the old pattern finds nothing and silently
ships nothing.

The installer:
- Auto-detects architecture from build output
- Installs to correct Program Files folder (64-bit vs 32-bit)
- Includes architecture suffix in filename

### Self-contained .NET 10 (Sprint 29 Track J onward — 4.2.0+)

`JJFlexRadio.vbproj` sets `<SelfContained>true</SelfContained>` plus
`<PublishReadyToRun>true</PublishReadyToRun>`, so `dotnet build` (not just
`dotnet publish`) drops the .NET runtime alongside the app. This is the
firm ship shape from 4.2.0 onward — no separate .NET install for users.

Expected output shape per Release build (per arch):
- **Raw publish dir:** ~180-190 MB, 364 files
- **Compressed installer (LZMA solid):** ~50-55 MB
- Top-level runtime DLLs you should see: `coreclr.dll`, `clrjit.dll`,
  `hostfxr.dll`, `hostpolicy.dll`, plus WPF natives (`wpfgfx_cor3.dll`,
  `D3DCompiler_47_cor3.dll`)
- Top-level subdirs: `runtimes/`, `help/`, `Resources/`, plus 13
  satellite-resource dirs (`cs/`, `de/`, `es/`, `fr/`, `it/`, `ja/`,
  `ko/`, `pl/`, `pt-BR/`, `ru/`, `tr/`, `zh-Hans/`, `zh-Hant/`)

If the runtime DLLs are missing from publish output, the build is NOT
self-contained — re-check that the vbproj `<SelfContained>` block is
intact and that no per-arch override is suppressing it.

`generate-deletelist.ps1` (called by `install.bat`) walks the publish
output to build the NSIS uninstaller's deleteList — every file gets a
`Delete` line, every top-level subdir gets `RMDir /r`. So new subdirs
introduced by future SDK upgrades clean up automatically.

**Fresh-VM verification (mandatory before public release):** install on
a Windows VM that has never had .NET 10. jjflexible.exe must launch and
display Home without prompting for a runtime install. The first user
install is the load-bearing accessibility test.

### Native DLLs
Architecture-specific native libraries are in:
- `runtimes/win-x64/native/` - 64-bit portaudio.dll, libopus.dll
- `runtimes/win-x86/native/` - 32-bit portaudio.dll, libopus.dll

`NativeLoader.vb` resolves the correct DLLs at runtime. Self-contained
publishing leaves the `runtimes/` tree intact, so the per-arch probe
order works unchanged.

### Known Warnings (safe to ignore)
- CA1416 platform compatibility warnings (Windows-only APIs)
- System.Drawing.Common version conflicts
- WPF assembly resolution warnings

### Security Notes
- TLS 1.2/1.3 enforced via `SslClientTls12.cs` wrapper
- WebView2 replaces legacy IE WebBrowser for Auth0
- DotNetZip being replaced with `System.IO.Compression` (Zip Slip CVE)

## Radio Support

| Model | SCUs | Diversity | Max Slices | Notes |
|-------|------|-----------|------------|-------|
| FLEX-6300 | 1 | No | 2 | Entry-level, optional ATU |
| FLEX-6400(M) | 1 | No | 2 | 3rd-order preselectors |
| FLEX-6500 | 1 | No | 4 | 30dB bandpass filters |
| FLEX-6600(M) | 2 | Yes | 4 | 7th-order filters, full duplex |
| FLEX-6700(R) | 2 | Yes | 8 | Flagship 6000 series |
| FLEX-8400(M) | 1 | No | 2 | 8000 series entry |
| FLEX-8600(M) | 2 | Yes | 4 | 8000 series mid-range |
| AU-510(M) | 1 | No | 2 | Aurora 500W, based on 8400 |
| AU-520(M) | 2 | Yes | 4 | Aurora 500W, based on 8600 |

Detection: Use `theRadio.Model`, `theRadio.DiversityIsAllowed`, `theRadio.MaxSlices` rather than hardcoding.

## Migration Status (.NET 10 + x64) - COMPLETED

**All phases complete:**
- Phase 0: Legacy cleanup (removed Icom, Kenwood, Generic radio support)
- Phase 0.5: Added FLEX-8400 and Aurora AU-510 to RigTable
- Phase 1-2: All C# projects converted to SDK-style
- Phase 3: All projects updated to `net10.0-windows` only
- Phase 4: Native DLL loading with architecture detection (`NativeLoader.vb`)
- Phase 5: WebView2 migration for Auth0 (`AuthFormWebView2.cs`)
- Phase 6: Dual x86/x64 build support with architecture-specific installers
- Phase 7: Cleanup (removed conditional compilation, updated documentation)

## Related Documentation

| File | Description |
|------|-------------|
| `MIGRATION.md` | FlexLib upgrade guide, TLS wrapper notes |
| `docs/CHANGELOG.md` | Version history |
| `C:/dev/jjf-private/planning/` | Product vision, design proposals, sprint plans — **PRIVATE, not in this repo** (2026-08-25) |
| `Agent.md` | Recent work summary (session context) |

## Releases

### Version Bump Checklist (IMPORTANT!)

**Edit `JJFlexRadio.vbproj` only** — update this line:

```xml
<Version>4.1.X</Version>
```

**Do NOT touch `My Project\AssemblyInfo.vb`.** Since the .NET 10 migration, version attributes are SDK-generated from the project file via `GenerateAssemblyInfo`. Only the vbproj's `<Version>` element matters. The 4-part build number (e.g. `4.1.16.42`) is computed automatically at build time from `<Version>` + `git rev-list --count HEAD` + `BUILDNUM_OFFSET`, so you never specify the 4th component by hand.

**Why this changed:** Pre-migration CLAUDE.md required updating both `.vbproj` AND `AssemblyInfo.vb` because the VB attributes overrode the project file. Post-migration, AssemblyInfo.vb no longer carries version attributes — they're SDK-generated. If you see older scripts or docs reference both files for version bumps, they're stale. Only the vbproj is the version source of truth now.

### Building Clean Installers

**Problem:** Incremental builds may use cached binaries with old version numbers. The `--no-incremental` flag does NOT reliably fix this (see warning above).

**Solution:** Always do a clean build when creating release installers:

```batch
# Option 1: Use the build script (recommended)
build-installers.bat

# Option 2: Manual clean build
dotnet clean JJFlexRadio.vbproj -c Release -p:Platform=x64 && dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x64
dotnet clean JJFlexRadio.vbproj -c Release -p:Platform=x86 && dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x86
```

**Verify the version before distributing:**
```batch
powershell -Command "(Get-Item 'bin\x64\Release\net10.0-windows\win-x64\jjflexible.exe').VersionInfo.ProductVersion"
```

### Creating a GitHub Release

1. **Bump version** in both files (see checklist above)

2. **Commit the version bump**:
   ```batch
   git add JJFlexRadio.vbproj
   git commit -m "Bump version to 4.1.X"
   ```

3. **Create and push a tag**:
   ```batch
   git tag -a v4.1.X -m "Release 4.1.X - Brief description"
   git push origin main --tags
   ```

4. **Build installers locally**:
   ```batch
   build-installers.bat
   ```

5. **Create GitHub Release and upload**:
   - Go to GitHub → Releases → "Create a new release"
   - Select your tag, add release notes
   - Upload the x64 and x86 installer .exe files
   - Or use gh CLI:
   ```batch
   gh release create v4.1.X --repo nromey/JJFlex-NG --title "JJFlexRadio 4.1.X" --notes "Release notes here" "Setup JJFlex_4.1.X_x64.exe" "Setup JJFlex_4.1.X_x86.exe"
   ```

   **IMPORTANT gh CLI gotchas:**
   - MUST use `--repo nromey/JJFlex-NG` — gh defaults to `upstream` (KevinSShaffer/JJFlexRadio) otherwise
   - MUST use `--notes` not `--body` (this version of gh)
   - Upload BOTH x64 and x86 installers in the same command
   - Always push tags to `origin` (nromey), not `upstream` (KevinSShaffer)

### Local Build Scripts

| Script | Purpose |
|--------|---------|
| `build-installers.bat` | Clean build + create both x64/x86 installers |
| `build-installers.bat x64` | Build x64 installer only |
| `build-installers.bat x86` | Build x86 installer only |
| `install.bat` | Low-level installer script (called by build-installers.bat) |

### Nightly Debug Builds (end-of-dev-day to private testers)

At the end of a dev session that produced testable changes, stage a Debug build for private testers (currently Don) in the Dropbox folder. This is a deliberate act, not automatic — only run when Noel confirms.

**Channel purpose:**
- **Nightly Debug** = work-in-progress builds, testers accept instability, daily cadence
- **Stable Release** = milestone installers, periodic, goes top-level in Dropbox
- **Public Release** = GitHub Releases + jjflexible.radio (future)

Content flows forward: nightly → stable → public. Nothing skips tiers. See `memory/project_distribution_channels.md` for full model.

**Nightly procedure (run at end of dev day when asked):**

1. Verify a fresh Debug x64 build exists:
   - `dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal`
   - Confirm exe timestamp is current: `powershell -Command "(Get-Item 'bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe').LastWriteTime"`

2. Zip the build folder and archive to the NAS historical tree:
   - **Invariant:** a nightly build IS a Debug build, stamped with the full 4-part version. Release installers go through `build-installers.bat` and land in the same per-version historical tree, just under `installers\` / `x64\` / `x86\` instead of `x64-debug\` — do not conflate Debug and Release subfolders.
   - Filename pattern: `JJFlex_<version>_<arch>_debug.zip` (e.g. `JJFlex_4.1.16.1_x64_debug.zip`), mirroring the Release installer naming.
   - Version comes from the exe's `FileVersion`. `build-debug.bat` computes it automatically from `<Version>` in `JJFlexRadio.vbproj` + `git rev-list --count HEAD` + `BUILDNUM_OFFSET`, and passes it to `dotnet build -p:Version=...`. (For manual builds, use the same formula — see `build-installers.bat`.)
   - **NAS (always, every build — history layer):** `build-debug.bat` copies the zip, NOTES, exe, and pdb to `\\nas.macaw-jazz.ts.net\jjflex\historical\<version>\x64-debug\`. Zip + NOTES are timestamped and never overwrite; exe + pdb refresh per version. Full bisectable build history lives here.
   - **Dropbox (only on `--publish` / tester-broadcast — current layer):** copy
     the new zip + NOTES, **verify they landed by reading them back at the
     destination, and only then delete** any older `JJFlex_*_debug*.zip` and
     `NOTES-*-debug*.txt`. Keeps Dropbox holding only the latest debug — testers
     never have to guess which is current. Rollback comes from NAS history, not
     from Dropbox. `LATEST.txt` names the current pair outright.

     **The order was the reverse of this until 2026-08-26, and the reversal is
     the fix (#230).** Deleting first meant a failed or partial copy left the
     testers with NOTHING, and the script printed "Done." either way — it checked
     neither the copy nor the purge. Copy-verify-purge cannot strand a tester: a
     failed copy leaves yesterday's build in place, which is a worse build but a
     working one. Exit codes 8 and 9 now report a publish that did not happen;
     do not "restore" the old order.
   - All private testers (Don, Justin, etc.) read from the shared Dropbox `debug\` folder.

3. Write a brief `NOTES-YYYYMMDD.txt` next to the zip — plain text, screen-reader friendly:
   - Date and current version from vbproj
   - What changed today (1-3 bullets)
   - Specific things to test
   - Known issues

**Rules:**
- Do NOT bump the version for nightlies — nightlies share the current dev version. The date in the filename disambiguates multiple nightlies that share a version.
- Do NOT auto-publish to public channels (GitHub, website). Only Noel initiates public releases.
- Do NOT ping testers — Noel handles communication with Don and other testers.
- Only run the nightly procedure after Noel confirms. Distribution is a deliberate act.

**Dropbox layout:**
- **The Dropbox root is machine-dependent** — `D:\Dropbox` on the ms-02 (the old hardcoded default, `C:\Users\nrome\Dropbox`, is wrong there). Resolve it from `%LOCALAPPDATA%\Dropbox\info.json` (`personal.path`) before any hand-drop; `build-debug.bat` and `publish-nightly-to-dropbox.ps1` do this themselves as of 2026-08-06. A hardcoded C:\ path on the ms-02 writes to an unsynced dead folder that looks like success.
- `<DropboxRoot>\JJFlexRadio\` — stable installers AND the latest end-of-day "nightly" debug zip (top level)
- `...\debug\` — shared debug nightlies + NOTES-YYYYMMDD.txt (all private testers read from here)
- `...\don\` — Don-specific artifacts (his crash dumps, custom builds, saved configs)
- `...\justin\` — Justin-specific artifacts (as he comes online as a tester)
- `...\old\` — archived previous stables (for rollback)
- `...\crash\` — user-submitted crash dumps

**End-of-day "done developing" workflow (distinct from per-tester `--publish`):**

When Noel says "done developing" or equivalent, that's the seal-the-day trigger. This is separate from the tester-broadcast nightly publish (`build-debug.bat --publish` to the `debug\` subfolder). End-of-day publishes a "nightly" debug snapshot to the Dropbox TOP LEVEL — a single artifact that represents today's state, replacing any prior day's nightly.

**Step 0 — Pre-seal cross-surface sweep (MANDATORY).** The current Claude session may not have observed all of today's work. Other Claude sessions may have run on rarbox, on parallel-track worktrees, on different machines. Memory entries get authored across sessions. Before writing Agent.md's seal entry or the AAR, sweep these surfaces:

- **Memory files modified today — READ ALL OF THEM, don't grep for relevance.** Check mtimes in `C:\Users\nrome\.claude\projects\c--dev-JJFlex-NG\memory\` for files touched today. **Read every memory entry that's new or modified today, every one, even if the title doesn't seem topic-relevant.** Memory cross-references mean unrelated-titled entries often hold load-bearing facts (the 2026-05-08 R2 work was captured in `project_blindhams_data_layer_migration.md`, NOT in a `project_phase_0_*.md` entry — anchoring on title-relevance would miss it). The cost of reading 5-10 today-modified entries is minutes; the cost of missing one is a permanent gap.
- **Every worktree's git log since midnight.** `git log --since=midnight` on main + every active track worktree (`jjflex-braille`, `jjflex-flexlib-42`, `jjflex-multi-radio`, plus any spawned that day). Cherry-pick origins, taeraflops-driven branches, and in-flight feature work all surface here. Do this PER worktree — each has its own HEAD.
- **Other local repos beyond JJFlex-NG.** `ls C:\dev\` and check git log on any sibling repos with today-mtime activity (e.g. `jjf-data`, `jjflexible-connect` (scaffolded 2026-08-03), future `bh-data`, others). These are separate clones, not worktrees of JJFlex-NG, so they don't show up in `git worktree list`. **The 2026-05-08 gap that exposed this sub-rule:** the entire Phase 0 B-E work landed in `nromey/jjf-data` (cloned to `C:\dev\jjf-data`), which never appears under JJFlex-NG; sealing only against JJFlex-NG missed the GitHub Action + R2 sync entirely.
- **The other major projects — Freight Fate and Civ VI Access.** These are full parallel projects, not side repos, and they routinely out-produce JJFlex on a given day. Check **main project roots only, not their worktrees** (`C:\dev\Freight-Fate`, `C:\dev\Civ-vi-access`); Freight Fate alone spawns a dozen `ff-*` worktrees whose work lands in the main repo anyway. For each, report branch, dirty count, and **unpushed commit count** (`git log --branches --not --remotes --oneline`). **The 2026-08-01 gap that exposed this sub-rule:** Freight Fate was sitting on 16 unpushed commits and Civ VI on 45 — months of work existing on exactly one machine — while the seal SOP looked only at JJFlex. The NAS dev mirror preserves `.git`, so mirroring covers the durability; **pushing those repos is Noel's call, not the sealing session's**, because they have outside contributors and pushing is outward-facing.
- **External infrastructure activity.** Look for today-dated changes on rarbox/roarbox/Cloudflare/R2/NAS. Memory entries are the primary record of on-box-Claude work; NAS folder mtimes also reveal activity beyond just the seal backup itself; Dropbox top level + debug/ may have publishes from earlier in the day. Cloudflare dashboard activity (R2 bucket creates, custom-domain hookups, cache rules) is captured indirectly via memory entries authored by the session that did the work — see the all-memory-read rule above.
- **Active planning docs modified today.** `find JJFlex-private/planning/active/ -newermt today` — walkthroughs, runbooks, briefings, agendas. The session that authored these may not be the session sealing the day; their mtimes reveal what work happened. Specifically the 2026-05-08 `phase-0-bcde-walkthrough.md` was sitting in active/ with a same-day mtime explicitly stating what got done; not opening it was part of why the second-pass also missed B-E.
- **For-claude / for-noel deltas.** New pull-docs landed today? Existing ones processed today? Each round-trip reflects decisions that need durable absorption.

**The anchoring failure mode that motivated the "read all" rule:** the reflexive search-for-topic-relevant-artifact pattern finds ONE file and treats it as the answer. The right starting prompt is "show me everything modified today, then synthesize," not "find the artifact about X." See `feedback_anchoring_on_first_relevant_artifact.md` for the lesson + corrective.

The seal entry's "Cross-surface activity" bullet list MUST cover everything found in the sweep, not just what this session directly did. The AAR's per-surface section MUST do the same. **A 5-minute thorough sweep prevents a permanent gap in the durable record.**

1. **Promote latest debug zip to Dropbox top level as nightly:** Run `publish-nightly-to-dropbox.ps1`. Copies the newest debug zip from NAS `<version>\x64-debug\` to Dropbox top level, replacing any existing `JJFlex_*_x64_nightly.zip` and `NOTES-nightly.txt` there. This is the easy-to-find "what's today's build?" artifact, distinct from the `debug\` subfolder tester distribution. **Skip this step on docs/memory/planning-only days** where no new debug build was produced — the prior day's nightly still represents current code state.
1a. **Memory index check (before the memory backup):** check the size of
`C:\Users\nrome\.claude\projects\C--dev-JJFlex-NG\memory\MEMORY.md`. Hard read
limit ~24.4KB, harness warns near 19.5KB; treat **~12KB as the seal threshold**
now that the index is split.

**Structure, as of 2026-08-19.** MEMORY.md is an always-loaded CORE, not a full
index. It holds current state, the rules that fire with no topic cue
(accessibility, description drift, no time estimates, build safety), Noel's
trigger phrases, terminology, user preferences and project history — plus one
pointer line per topic index. The ~200 topic pointers live in
`index_product_identity.md`, `index_radio_hardware.md`, `index_dsp_audio.md`,
`index_build_release.md`, `index_testing.md`, `index_infrastructure.md`,
`index_workflow_process.md`, `index_dev_practices.md` and
`project_closed_history_index.md`.

**A new memory therefore goes in the matching topic index, NOT in MEMORY.md.**
The core only grows when something must fire without a cue. The triage line is:
*topic-triggered knowledge can move one hop; reflex-triggered rules cannot.*

Each pointer line in the core NAMES the contents of its index on purpose. The
recurring failure is not being unable to fetch a memory, it is not realising
one exists — see `feedback_grep_memory_before_asserting.md`. Preserve that
naming when editing a pointer line, or the index stops doing its only job.

**Verify after any restructure:** every file on disk must be reachable from
MEMORY.md or exactly one index, and no link may dangle. Diff the link set
against the previous MEMORY.md before overwriting it — the first attempt on
2026-08-19 silently dropped one entry, caught only by that check.

**Archive sweep — do this every seal, it is where the headroom comes from.**
The index shrinks in proportion to work that FINISHES, but only if closure was
recorded. So:

- When today's work closed something a memory describes, stamp that memory now,
  with a banner at its top: `> **RESOLVED / SHIPPED / SUPERSEDED — <who>,
  <date>: <what changed>**`. Stamp the *reasoning*, not just the fact — see
  `project_as_retry_pathway_regression.md`, filed as a networking regression and
  actually closed by authentication work nobody had connected to it.
- Then sweep: `grep -lin "RESOLVED\|SHIPPED\|SUPERSEDED\|CLOSED" *.md` over the
  memory directory. Anything stamped, whose work nothing live still depends on,
  moves into `project_closed_history_index.md` and out of the core.
- Age is NOT the signal. `project_anti_patterns_from_blindcat.md` is one of the
  oldest entries and is consulted constantly. Closure is the only signal.

Without the stamp this sweep finds nothing: the four entries archived on
2026-08-19 were discovered only because earlier sessions happened to write those
words into them by chance, and anything that closed quietly stayed in the
always-loaded core indefinitely.

Added 2026-08-06 after the index hit the warning threshold; rewritten
2026-08-19 when the flat index reached 18.5KB and was split into a 9.8KB core.
2. **Memory backup — ALL projects, not just JJFlex:** `backup-memory-to-nas.ps1` snapshots **every** per-project Claude memory tree found under `C:\Users\nrome\.claude\projects\`. JJFlex keeps its legacy flat path (`historical\memory\memory-<ts>.zip`) so its dated series stays unbroken; every other project lands at `historical\memory\projects\<slug>\memory-<ts>.zip`. As of 2026-08-01 this picks up **Freight Fate** (`C--dev-Freight-Fate`, ~118 files) and **Civ VI Access** (`c--dev-Civ-vi-access`, ~175 files), neither of which had ever been backed up. Pass `-PrimaryOnly` for the old JJFlex-only behaviour. **Critical:** these trees live under the user profile, so the `C:\dev` mirror in step 3a does NOT cover them — this script and step 2a are their only backup paths. Keep running it even though 2a also sweeps up `memory\`: this one produces the per-project dated series that `memory-<ts>.zip` history depends on.
2a. **Claude Code state backup:** `backup-claude-state-to-nas.ps1` snapshots the whole `C:\Users\nrome\.claude` tree plus `~\.claude.json` to NAS `historical\claude-state\claude-state-<ts>.zip`. Keeps the last 12, prunes older. This is the **session transcripts** — the `.jsonl` files under `.claude\projects\<slug>\` that hold every conversation Claude Code has had, and the only thing `claude --resume` can read. Nothing else backs them up: step 3a mirrors `C:\dev` and these live under the user profile; step 2 takes `memory\` only; git covers none of it. They are also on a retention timer — Claude Code sweeps transcripts older than `cleanupPeriodDays` at startup, and on 2026-08-01 that removed nine June sessions across Civ VI Access and the flashdrive project. `cleanupPeriodDays` is now pinned to **365** in `~\.claude\settings.json`, but retention only widens the window; it is not a backup. Excludes regenerable state (`cache`, `plugins`, `shell-snapshots`) and `.credentials.json` — that is a live OAuth token, and re-auth is one `claude` launch. `file-history\` (the ~150 MB `/rewind` snapshot tree) is opt-in via `-IncludeFileHistory`. Expect ~180 MB compressed from ~415 MB raw.
3. **Private docs backup:** `backup-private-to-nas.ps1` snapshots `C:\Users\nrome\JJFlex-private\` to NAS `historical\private\<date>\`. Captures easter eggs, unlock codes, and other private-docs state.
3a. **Dev directory mirror:** `backup-dev-to-nas.ps1` mirrors `C:\dev` to NAS `historical\dev-mirror\` (single rolling snapshot, overwrites previous). Captures non-git-recoverable material: vendor research clones (smartsdr-extracted, Dot Pad SDK, AetherSDR), per-project `.claude\` state, uncommitted worktree work. Excludes build artifacts (bin/obj/.vs) and dependency caches (node_modules/packages/target). Recovery window is "today only" — git is the time machine for source repos, dated history for memory and private already exists.

> **Invocation note for these (and any other repo-root) .ps1 scripts:** they live at the **repo root** (`C:\dev\JJFlex-NG\`), NOT in a `scripts/` subdirectory. Invoke with PowerShell's call operator: `& "C:\dev\JJFlex-NG\backup-memory-to-nas.ps1"`. Avoid `powershell -File <path>` — when the path is invalid the failure still looks like success, but **not for the reason this line used to give.** Measured 2026-08-27: the wrapper exits **-196608**, not 0. That matters because batch files test it with `if errorlevel 1`, which is a **greater-than-or-equal** test, so a negative exit code sails straight through it. Guard by testing the file exists before calling it, not by checking the exit code afterwards. The same applies to other repo-root .ps1 helpers (build scripts, publish scripts).
3b. **Dependency vulnerability check:** run `dotnet list package --vulnerable
   --include-transitive` from the repo root. Two seconds, and it is the only
   security gate in the seal. Any `NU1902`/`NU1903` line names a package with a
   published advisory — record it in the seal entry and fix it, or say
   explicitly why not. **Freshness is a different question and does NOT belong
   here** — `--outdated` returns twenty-odd packages that move on a scale of
   months, and reading that list daily trains you to ignore it. Freshness is
   Dependabot's job; this step is only about advisories. Added 2026-08-17 after
   SharpCompress 0.40.0 sat in the tree with a known moderate-severity
   vulnerability, surfacing in eighteen projects, unnoticed because nobody ever
   ran the check.
3c. **Memory drift check:** run `& "C:\dev\JJFlex-NG\check-memory-drift.ps1"`.
   Seconds, and it is the only automated defence against the project's dominant
   defect class. It walks every memory entry, extracts the file paths each one
   names, and reports the ones no tree contains. **These are CANDIDATES, not
   errors** — a missing path can mean the entry is stale, or that it describes
   something deliberately removed. Judge, then either fix the entry or stamp it
   `RESOLVED`/`SHIPPED`/`SUPERSEDED` so it stops flagging and the archive sweep
   in 1a can find it.

   **It also checks SYMBOLS now, as of 2026-08-27 (#272)** — backticked
   identifiers in memory entries **and in the task register**, against every
   identifier in the code. Same discipline, same "candidates, not errors", same
   "do not chase the count to zero". A stale symbol in a TASK is the expensive
   one, because tasks are read by agents about to write code: **fix the task in
   `tasks.md` directly.** There is no regeneration step any more — see 3c-bis.

   Its header claimed symbol extraction from the day it was written and had
   none for six days — the tool's own defect class, in the tool. Anyone relying
   on it to catch a rename got a green result for nothing.

   **Two checkers, split by corpus, and neither should grow into the other.**
   This script owns the memory tree and the task register, which live outside
   every repo. `Radios.Tests/IntegrationPassInstructionTests` owns this file
   and `MIGRATION.md`, and is scoped by `git ls-files` on purpose. Do not point
   the drift checker at CLAUDE.md, and do not give the integration pass a walk
   into the user profile.

   Added 2026-08-21. Its first run found three real drifts in minutes, **two of
   them in this file**: the keyboard audit told you to grep `KeyCommands.vb`
   (it is `JJFlexWpf/KeyCommands.cs`, and a literal grep returns nothing, which
   reads as "no key bindings changed" and silently skips the audit); the
   SmartLink section promised a legacy `AuthForm.cs` fallback that was deleted
   in `ba6b2e2b`; and eleven memory entries named `KeyCommands.vb` or
   `publish-daily-to-dropbox.ps1`, neither of which exists.

   **Do not chase the count to zero.** Roughly half the remaining hits are
   entries legitimately naming files in estates this machine does not hold
   (Blind Hams data, rarbox triage) or planning docs that were archived on
   purpose. The number to watch is a NEW entry appearing, not the total.

3c-bis. **The task register is a FILE now, and it is the source of truth.**

   - `C:/dev/jjf-private/planning/active/tasks.md` — open tasks.
   - `C:/dev/jjf-private/planning/active/tasks-archive.md` — closed ones, plus
     the old BUG- number mapping.

   Migrated 2026-08-27. Both are git-tracked in JJFlex-private (**no remote, and
   a `pre-push` hook refuses outright** — it is a private estate, not a repo
   waiting to be published). `C:\dev\jjf-private` is a junction to the same
   tree, so either path reaches it.

   **Edit them by hand. There is nothing to regenerate and nothing to sync.**
   Every task carries a status line of the form
   `> #264 · OPEN · opened 2026-08-26`, and all 327 of them are substring-unique
   within their file — the middle dot right after the number is what makes `#10`
   not match inside `#100`. Closing a task is one literal replacement.

   **THE TASK TOOL IS NOT THE REGISTER, AND IT IS NOW EMPTY.** Emptied
   2026-08-28: it still held all ~300 numbers with statuses frozen at the
   migration, and it is injected into every session, so it read as live. That is
   the drifted-mirror failure the migration existed to end — an agent consulting
   it would have seen `#236` open after it shipped and would not have seen
   `#310`–`#333` at all. One sentinel entry remains, pointing here.

   **Its ids are a separate space and they collide.** The sentinel was
   auto-assigned 310; `#310` in `tasks.md` is an unrelated task. Never read a
   task-tool id as a register number. Use the tool freely for a session's own
   throwaway checklist; **anything that must outlive the terminal goes in
   `tasks.md`.**

   **Numbering never restarted, and must not.** `#1`–`#309` came across from the
   old store unchanged so that every reference in memory entries, `Agent.md`,
   commit messages and code comments still resolves; `#310`+ are new. Renumbering
   would strand all of them, which is exactly what the abandoned `BUG-` scheme
   did — its only mapping now lives at the end of `tasks-archive.md`. **Never
   assign a new `BUG-` number.**

   `export-task-register.ps1` is **SUPERSEDED**; `task-register.md` is left in
   place carrying that stamp so a stray run overwrites the dead file rather than
   the live one. Retiring the script outright is Noel's call, not a sealing
   session's.

   **Why this replaced a generated mirror.** The old check existed because the
   task store lived under the user profile, outside git, and vanished with the
   terminal — so the file was a mirror, and *a drifted mirror looks identical to
   a correct one*. That was not theoretical: `research-queue.md` carried an OPEN
   WORK REGISTER created 2026-08-14 with the instruction "keep this current",
   and nine days later it said 34 open while the store held 77. The same
   reconciliation found the drift also runs the *other* way — a section headed
   "Not yet in the task store" was 11 of 15 already done. **A stale list
   understates progress as readily as it overstates it.**

   **A hand-edited file cannot drift from itself**, which is why the mirror
   check is gone rather than repointed. If a checker is ever wanted here, the
   one with ongoing value validates `tasks.md`'s own invariants — unique status
   lines, no duplicate numbers, heading and status in agreement.

   **The migration read the STORE, not the previous export.** The generated file
   had flattened all 168 closed tasks to one-line subjects; going back to the
   store recovered **4,135 lines of decision record** that a file-to-file
   migration would have dropped without anything noticing.

3d. **AppData config backup:** run `& "C:\dev\JJFlex-NG\backup-appdata-to-nas.ps1"`.
   Snapshots the operator's CONFIGURATION out of `%AppData%\JJFlexRadio\` to
   `historical\appdata\` as a dated zip. About 73 files and 0.07 MB compressed,
   so it costs nothing and can be run freely. Keeps 24 by default.

   **Nothing else covers this directory.** `backup-dev-to-nas.ps1` mirrors
   `C:\dev`; `backup-claude-state-to-nas.ps1` takes `~\.claude`;
   `backup-private-to-nas.ps1` takes JJFlex-private. All three live outside
   `%AppData%`, so until 2026-08-21 the operator's key map, audio config, radio
   entries and 51 connection profiles existed in exactly one place.

   **Added 2026-08-21 after a background agent's worktree build rewrote
   `KeyDefs.xml`.** Every instance shares `%AppData%` regardless of which binary
   started it, so an agent that launches its own build runs that build's
   config migrations against the operator's LIVE settings. Nothing was
   obviously broken, but with no prior copy anywhere, "did that damage
   anything?" could not be answered — and still cannot, for that day.

   Config only by default. `-IncludeDiagnostics` adds `Errors\*.zip` and
   `Traces\`; raw `.dmp` files are NEVER taken — measured 2026-08-21 at 428 to
   516 MB each, and the `.zip` bundle beside them is what a support
   conversation actually reads.

4. **Agent.md update:** Record what happened today and what's next, so the resume path for the next session is clear.
4a. **Rigmeter snapshot in the seal entry.** Rigmeter lives at
   `C:\dev\rigmeter` (extracted Sprint 30 Track G, 2026-08-18) and still
   targets this repo by default. **It has NO GIT REMOTE** — its only copies are
   this machine and the `backup-dev-to-nas.ps1` mirror, so do not treat its
   commits as pushed.

   Two commands, not three:

   ```
   python C:\dev\rigmeter\rigmeter.py today --fun --languages
   python C:\dev\rigmeter\rigmeter.py snapshot
   ```

   `today --fun --languages` replaced a `today` + `all` pair on 2026-08-25.
   Both flags are new that day and both matter here:

   - **`--languages`** breaks the day down by file type, which is what turns a
     net figure into a readable sentence. The 2026-08-25 seal reported net
     **-57,129** — meaningless until the breakdown showed 67,501 lines of
     markdown leaving for private against 18,467 lines of C# arriving. Costs one
     `--numstat` call; available on every span.
   - **`--fun`** now scopes its comparisons to THE SPAN rather than the whole
     codebase, which is the number a daily entry actually wants. Whole-codebase
     fun stats barely move day to day and were noise in a daily seal.
     **Only on `today`, `week` and `month`** — it reads the span's patch text,
     so a year is punishing and project start absurd.

   Run **`all`** only when the grand totals are wanted (authored vs vendor,
   per-project breakdown). It scans the whole tree and is much slower; the daily
   entry rarely needs it.

   **Report BOTH figures the span commands now print, and label which is
   which.** "Work done, summed across every commit" and "Repository size change"
   are different measurements. Until 2026-08-25 every span command printed only
   the snapshot delta under the word "activity", and `start` therefore reported
   **zero deletions across 1,933 commits**; the real figure is over five
   million. Noel found it by playing with the tool. Do not collapse the two back
   into one number.

   `snapshot` writes structured JSON to
   `\\nas.macaw-jazz.ts.net\jjflex\historical\stats\<commit-date>-<short-sha>.json`
   — the durable time-series behind `rigmeter growth --use-snapshots <a> <b>`.
   Falls back to `%LOCALAPPDATA%\rigmeter\snapshots\` if NAS is unreachable;
   reconcile later with `rigmeter snapshot --sync`. Agent.md text is
   human-readable history, the NAS JSON is machine-queryable, and both
   accumulate.

   Paste a condensed version into a "Rigmeter snapshot — end of YYYY-MM-DD"
   subsection at the bottom of today's Agent.md entry. Skip on docs-only days
   where the values would be unchanged from yesterday.

4b. **After-Action Report (AAR).** Write `C:\Users\nrome\JJFlex-private\after-action-reports\YYYY\MM\YYYY-MM-DD.md` capturing the day's cross-surface activity (main repo + each worktree + external infrastructure). **Lives in JJFlex-private, NOT in the public `docs/` tree** — the file routinely names testers by personal/medical context, references internal sequencing, and would leak through `nromey/JJFlex-NG`. JJFlex-private is already backed up to NAS via `backup-private-to-nas.ps1`. Sections: Snapshot, Theme, Per-surface activity, Decisions and scope changes, Rigmeter today (with branch-scope caveat), Setup for tomorrow. Use bulleted lists / prose only — NEVER tables (screen-reader hostile). Closes the gap rigmeter and Agent.md leave on heavy research days where parallel worktrees accumulate thousands of lines of docs while main sees one commit. Skip rule: if every surface was idle AND no external work AND rigmeter today is empty, no file that day. See `memory/project_after_action_reports.md` for the full convention.
5. **Commit the day's changes and push the feature branch to origin.** Stage specific files (never `-A` / `.`), commit with the end-of-day seal message format (`End-of-day seal YYYY-MM-DD: <summary>`), then `git push origin <current-branch>`. Pushing is durability insurance — without it, an unbacked-up local repo loses every un-pushed commit if the machine fails. Push to `origin` (nromey's fork), NEVER `upstream` (KevinSShaffer). Feature-branch pushes are backup moves, not release moves — no merge to main implied.

   **Push EVERY live track branch too, not just the current one.** Sprint track
   branches live in worktrees and are easy to forget precisely because they are
   not checked out where you are standing. One command covers them:
   `git push origin sprintN/track-a sprintN/track-b ...`, then verify each with
   `git rev-parse` local against `origin/`. Do NOT assume merging a track into
   the sprint branch makes pushing it redundant — that assumption is only true
   if the merge actually happened, which is what step 4 of Phase 4 now checks.
   **Added 2026-08-20:** the Sprint 33 seal found two tracks unmerged, and the
   only reason the discovery cost minutes rather than a day's work is that all
   eleven branches had just been pushed.
6. **CLAUDE.md drift check:** If the day's work exposed stale guidance in CLAUDE.md (e.g. referenced a retired script, missed a new workflow), flag for update.

**When a second seal runs on the same calendar date.** Late-night sessions that
cross midnight get sealed under the new date, so a day that starts with a
post-midnight seal and then has a normal working session ends up sealing twice.
That is fine and expected — do not skip the second one. Handle the collisions:

- **Scope the sweep to the delta, not the calendar day.** Diff against the first
  seal's commit (`git diff --shortstat <first-seal-sha>..HEAD`), not against
  midnight. Say which commit you measured from.
- **Agent.md** gets a *new* entry marked as the second seal, with a note at the
  top saying which window it covers and which commit the delta starts at. Don't
  edit the first seal's entry — it was true when written.
- **The AAR is one file per date, not per seal.** Append a `# Part two` section
  to the existing `YYYY-MM-DD.md` rather than overwriting it or inventing a
  `-2` suffix. Add a pointer at the top of the file saying two sessions ran and
  which part is current.
- **Rigmeter `today` spans both seals** because it counts from local midnight.
  Report both numbers — the since-midnight figure and the since-first-seal
  figure — and label which is which. Reporting only the first double-counts the
  earlier session's work.
- **Re-run the private-docs backup after writing the AAR** (true of every seal,
  but easy to miss on the second): step 3 runs early in the sequence and would
  otherwise snapshot the tree without that day's report in it.

**Key distinction — two layers of debug distribution:**
- `build-debug.bat --publish` writes to Dropbox `debug\` subfolder. This is tester distribution — Don, Justin, etc. read from here. Can run multiple times a day if you have testers actively hammering a specific fix.
- `publish-nightly-to-dropbox.ps1` writes to Dropbox TOP LEVEL. This is the end-of-day seal — one artifact per dev day, the "this is where things stand tonight" marker. Not tester-directed; more like a convenient top-level pointer for anyone checking in.

Both can coexist. Tester `--publish` satisfies tester needs; end-of-day nightly satisfies "what's the current state of the dev branch" without hunting through subfolders.

## Common Tasks

### Add new DSP feature
1. Check if FlexLib exposes the property (e.g., `Slice.RNNOn`)
2. Add wrapper property in `FlexBase.cs`
3. Check license with `Radio.FeatureLicense`
4. Add UI control in appropriate Filters form
5. Update Feature Availability tab if gated

### Update FlexLib
1. Copy new FlexLib to `FlexLib_API/`
2. Reapply `SslClientTls12.cs` wrapper
3. Update `TlsCommandCommunication.cs` to use wrapper
4. Verify TLS negotiation in remote connect
5. Update version references

### Testing against a throwaway settings tree (JJFLEX_CONFIG_DIR)

**Set `JJFLEX_CONFIG_DIR` to an absolute path and that run uses it as the whole
settings root instead of `%AppData%\JJFlexRadio`.** For automated runs, for
agents that launch their own build, and for anything where the operator's live
configuration must not be in the blast radius.

**This is a TESTING mechanism. Never tell a user to set it, never surface it in
the UI, and never document it as a feature.** It is deliberately not a setting
and not a UI toggle: it decides *which* settings file to read, so it cannot live
in one; and a persistent "use throwaway settings" switch is a footgun pointed at
the thing it protects. It is per-launch, and the isolation evaporates with the
process.

Measured 2026-08-22, on the same build twelve seconds apart: an ordinary launch
modified **17 files** in the live folder — rewriting `KeyDefs.xml`, rewriting the
8600's per-radio `config.xml`, adding a trace and deleting seven older ones. The
same launch under `JJFLEX_CONFIG_DIR` modified **0 of 702**. That gap is why
yesterday's agent-rewrote-KeyDefs.xml incident was never a freak event.

Guards, because a half-working isolation is worse than none:

- A **relative** path is refused (it would resolve against whatever directory the
  launcher happened to be in) and the refusal is traced.
- A path equal to the **real** settings folder is refused — allowing it would
  report "temporary settings in use" while writing live ones.
- Any refusal falls back to normal and says so. The app never ends up with
  nowhere to read from.
- When it engages, the diagnostic log carries a `ConfigLocation:` line stating
  the tree in use and that the operator's settings are not being touched.

**If you add any new store under the settings root, resolve it from
`Radios.RadioConfig.AppDataRoot` — never from
`Environment.GetFolderPath(SpecialFolder.ApplicationData)` plus `"JJFlexRadio"`.**
A sweep on 2026-08-22 found **nineteen** places doing the latter; every one
worked perfectly and every one was invisible to relocation, which is how the
first isolated run could truthfully report itself isolated while twenty stores
wrote the live folder anyway. Deliberately still independent: `JJFlexUpdater`
(does not reference Radios, never runs in a test), `ImportSetup.vb` (extracts an
operator-chosen zip to the AppData *parent*), and `FlexBase.cs:4078` (points at
FlexRadio's own folder, not ours).

### Trace File Location
- Boot trace: `%AppData%\JJFlexRadio\JJFlexRadioTrace.txt` (enabled when `BootTrace = True` in `globals.vb`)
- Multi-instance: `%AppData%\JJFlexRadio\JJFlexRadio2Trace.txt` (instance 2+)
- User-initiated capture: **Settings → Diagnostics** (Tools → Diagnostics deep-links there). Saved sessions live in the **Saved Diagnostic Logs** window, opened from that tab. `Ctrl+J, Ctrl+D` starts and stops a detailed capture from anywhere, including inside a dialog. **This line has been wrong twice.** It said "Operations → Tracing" until 2026-08-11; it then said "Help → Tracing (`TraceAdmin.vb`)" until Sprint 30 Track D landed 2026-08-19, and that was wrong in both halves — the menu item is now deleted, and it never opened `TraceAdmin.vb` anyway, it opened the WPF `TraceAdminDialog`
- The always-on log is still governed by `BootTrace` in `globals.vb`, but it is **no longer a code-level Boolean with no UI behind it** — as of Sprint 30 it ANDs in the operator's `KeepDiagnosticLog` setting from `diagnosticsConfigV1.xml`, which the Diagnostics tab edits. Find it by symbol, not by line number; the file has grown by hundreds of lines and every line reference in this document's orbit has moved
- Tracing code: `JJTrace\Tracing.cs`

### Debug Remote/SmartLink
1. Check `Radios/AuthFormWebView2.cs` for Auth0 flow (uses WebView2/Edge)
2. **There is no fallback.** The IE-based `AuthForm.cs` was DELETED in `ba6b2e2b`
   ("Delete the IE-based AuthForm; WebView2 has been the real auth path for
   months"). This line claimed it was "kept as fallback, marked `[Obsolete]`"
   until 2026-08-21 — do not go looking for it, and do not plan a fallback path
   around it
3. Verify TLS 1.2+ in network traces
4. See `docs/remote-migration.md` for current state

## Workflow

### Sprint Lifecycle (Standard Operating Procedure)

Every sprint follows this lifecycle. Claude (in the Desktop/orchestrator session) drives planning, setup, and merging. The user spawns CLI sessions to execute tracks.

#### Phase 1: Planning (Claude Desktop + User)
1. **Scope discussion** — User and Claude discuss what the sprint should accomplish
2. **Plan file creation** — Claude writes a detailed sprint plan to
   **`C:\Users\nrome\JJFlex-private\planning\agile\`** (named with ham-radio
   words, e.g. `barefoot-qrm-trap.md`)

   > **PLANNING DOCUMENTS GO IN JJFlex-private, NOT IN THE REPO. RULED BY NOEL
   > 2026-08-25.** `nromey/JJFlex-NG` is a **PUBLIC** repository — verified with
   > `gh repo view`, `"visibility":"PUBLIC"`. Planning documents name testers
   > (Don, Justin, Doug, Patrick, Mark), say where Don's radio physically
   > lives, carry personal and medical context, and record internal sequencing
   > and release strategy. **None of that belongs in a public repo.**
   >
   > This is the SAME reasoning step 4b already applies to the After-Action
   > Report — and that is the lesson: the rule was written down correctly,
   > applied to one file, and never generalised, while 232 other files with
   > identical properties stayed public.
   >
   > Noel's ruling: *"we need to stop all this junk, we can keep a billion
   > lines in private on the nas / hard drive for all I care."* So there is no
   > size discipline to observe in private — only a location one.
   >
   > **History is not retractable and that is accepted.** Noel: *"course
   > someone could rewind and get older copies."* The rule stops FUTURE
   > additions; it does not un-publish what is already pushed. Do not claim
   > otherwise.
   >
   > **STAYS in the repo:** `docs/help/` (a build input — `build-help.bat`
   > converts it to the CHM), `docs/CHANGELOG.md` (the user record),
   > `MIGRATION.md`, `Agent.md`. Moving the existing 70,124 lines out is #260.
3. **Track decomposition** — Claude analyzes the work and splits it into parallel tracks:
   - Identify independent work units (dialogs, features, files that don't overlap)
   - Group into tracks (max 6 concurrent tracks)
   - Identify dependencies between tracks (e.g., "Track A must complete Phase X before B/C can start")
   - Identify merge order (which tracks merge first, any conflict-prone areas)
4. **User approval** — User reviews track split, adjusts if needed

#### Phase 2: Setup (Claude Desktop)
Claude performs ALL setup before the user spawns any CLI sessions:

1. **Create branches** from the current base (usually `main`):
   ```batch
   git checkout -b sprintN/track-a
   ```

2. **Create worktrees** for each parallel track:
   ```batch
   # Track A stays in main repo: C:\dev\JJFlex-NG
   git worktree add ../jjflex-Nb sprintN/track-a -b sprintN/track-b
   git worktree add ../jjflex-Nc sprintN/track-a -b sprintN/track-c
   # ... up to 6 tracks
   ```
   **Naming convention:** `../jjflex-Nx` where N = sprint number, x = track letter.

3. **Write TRACK-INSTRUCTIONS.md** in each worktree root:
   - Complete file list with WinForms source → WPF target mapping (or equivalent)
   - Architecture rules and patterns to follow
   - Build commands specific to that worktree
   - Commit strategy (how often, what message format)
   - Any track-specific notes (dependencies, gotchas, special handling)

4. **Update the plan file** with track assignments, worktree paths, and branch names

5. **Report to user — MUST include execution order AND merge plan:**
   - Number of tracks to run
   - **Execution order** — Claude MUST explicitly tell the user which tracks to start first and which to start later:
     - If tracks have dependencies: specify the order (e.g., "Start Track A first. When Track A reports Phase 9.0 committed, start Tracks B and C.")
     - If all tracks are independent: say "Start all N tracks simultaneously."
     - If mixed: group them (e.g., "Start A and B now. Start C after A completes Step 1.")
   - The directory to `cd` into for each track
   - The exact prompt to type in each CLI session (always just: `Start Sprint N Track X from TRACK-INSTRUCTIONS.md`)
   - **Merge plan** — Claude MUST tell the user the planned merge order if it's non-trivial:
     - Which track is the merge target (usually Track A)
     - Whether merges can happen as tracks complete or must wait for all tracks
     - Any merge order constraints (e.g., "Track B must merge before Track C because C depends on B's DataGrid patterns")
     - If merge order doesn't matter: say "Tracks merge in any order as they complete."
     - This lets the user know what to expect and whether completing Track C before Track B changes anything

#### Phase 3: Execution (two launcher models)

**Model B — orchestrator-spawned background agents (proven 2026-08-07,
now the default).** The orchestrator session spawns every track as a
background subagent (Agent tool, one spawn per track, model per track's
weight), keeps the task list as the user's live progress board, relays
cross-track facts to in-flight agents, and processes completion reports.
The user watches one window instead of N terminals. Same worktrees, same
TRACK-INSTRUCTIONS contract, same merge train — only the launcher
differs. Full pattern and load-bearing practices:
`memory/project_background_agent_fleet_model.md`. Thirteen agents in one
evening is proven scale.

**Model tier order, highest first: Fable, then Opus, then Sonnet, then
Haiku.** Corrected by Noel 2026-08-25, after a track plan assigned Opus
as the ceiling and Sonnet as the floor — both were off by one and every
track had to shift up.

- **Fable** — design, architecture, user-facing prose, anything
  safety-critical, anything shipping to a real user.
- **Opus** — specified work in known files: real code needing care,
  where the judgement is already extracted into a plan or design doc.
- **Sonnet** — genuinely mechanical work.

**The orchestrator window runs Opus on purpose, and that says nothing
about Opus being the top.** Noel: *"We're running this window on Opus,
but that's because we haven't had to run main stuff here on Fable, we
delegate when we need fable."* Conversation, planning and orchestration
happen here; heavy lifting goes out to spawned agents, and that is where
Fable belongs.

Never under-provision to save cost — see
`memory/user_usage_headroom_is_not_the_constraint.md`. The constraints
are the operator's attention, the single desktop, and build collisions.
Detail: `memory/project_model_tiers_and_delegation.md`.

**Model A — user-spawned CLI sessions (the original).** Use when tracks
need live user interaction mid-flight (radio-seat work, testing
conversations) or the user wants to drive a session directly.

User opens Claude CLI sessions (one per track) following the execution order Claude specified.

Each CLI session:
- User `cd`s to the track's worktree directory
- User types: `Start Sprint N Track X from TRACK-INSTRUCTIONS.md`
- CLI reads its own `TRACK-INSTRUCTIONS.md` for full context
- Works independently in its own worktree directory
- Builds and commits within its own branch
- Reports completion when done

**The user reports track completion to Claude Desktop:** "Track A is done" / "Track B is done" etc.

#### Phase 4: Merging (Claude Desktop)
As tracks complete, Claude Desktop handles merges and keeps the user informed:

1. **Merge order** — Claude follows the merge plan communicated in Phase 2. If circumstances change (unexpected conflicts, track completing out of order), Claude informs the user before proceeding with an adjusted merge strategy.
2. **Standard merge process:**
   ```batch
   git checkout sprintN/track-a
   git merge sprintN/track-b --no-ff -m "Merge Track B into Track A"
   # Resolve conflicts if any (Claude Desktop handles this)
   git merge sprintN/track-c --no-ff -m "Merge Track C into Track A"
   ```
3. **Post-merge build verification** — clean build after each merge to catch integration issues. If build fails, Claude fixes conflicts/issues before merging the next track.
4. **CONTAINMENT SWEEP — mandatory before declaring a sprint merged.** Prove every
   track branch is an ancestor of the target. A clean merge is evidence about the
   branches you merged and says NOTHING about one you never invoked:

   ```bash
   for b in b c d e f g h i j k; do
     h=$(git rev-parse sprintN/track-$b)
     git merge-base --is-ancestor $h HEAD \
       && echo "track-$b: contained" || echo "track-$b: NOT CONTAINED"
   done
   ```

   If a track is not contained, `git log --oneline HEAD..sprintN/track-X` says
   whether it legitimately advanced past its merge point or was never merged at
   all — **if the track's FIRST commit is in that list, the merge never ran.**

   **Added 2026-08-20, after Sprint 33 sealed with two of eleven tracks
   unmerged** — Track D (11 commits, 3,085 insertions, including the sprint's
   headline fix) and Track G (442 insertions touching vendored FlexLib). Nothing
   flagged it: no operation failed, the build passed because absent code is
   referenced by nothing, and every track agent genuinely finished and reported
   done. Worst of all, **the two missing files were TEST files, so the suite was
   smaller and greener** — a falling test count reads as success. The gap lives
   between "track finished" and "track landed" and nobody owned that edge.
   See `memory/feedback_verify_merge_containment.md`.
4-bis. **INTEGRATION PASS — duplication, not collision.** The containment sweep
   proves every branch landed. It says nothing about whether two tracks built
   the same thing twice.

   **A clean merge is evidence about CONFLICTS, and duplication does not
   conflict.** Two agents implementing one idea in two files produce no merge
   conflict, no build error, and two working implementations. Nothing fails.

   So once the merge builds, run a pass that reads the RESULT, not the diff:

   - **Concept dedup.** One idea implemented twice; a helper with exactly one
     caller that ought to have several; two vocabularies for one thing.
   - **A blind end-to-end walk of anything an operator touches.** NOT a code
     review — an absence is invisible in a diff. Render the surface, move
     through it in order, and ask at every state: *what can a person do next
     from here, and how would they know?*

   **Added 2026-08-25, after the Fixer Tool's first session with a real
   operator produced fourteen findings in one evening.** Every component was
   individually correct and every automated test passed — the buttons worked,
   the wire contract held, the analyzers were right. Five separate times a
   helper existed and the next author built beside it (`HostApiPhrase`, the
   `speakNow`/`speakDone` pair, the `Sequence`/`RunId` resume semantics, tune
   power read but never displayed, `JJTrace/SessionArchive`). **Every one of
   those pairs sat in DISJOINT files**, so no orchestrator watching file
   assignments could have caught them. The real damage was all in the seams:
   no stage offered a way to the next one, and the skip control was rendered
   on a stage that had already passed — where pressing it silently destroys
   the measurement, which on a transmit stage was paid for with RF.

   **Do not delegate this to the tracks.** Each track's brief is correct and
   local; the defect is precisely that none of them can see the others. This
   is a separate job with the whole merged tree in front of it.
5. **Status updates** — Claude tells the user after each merge completes (e.g., "Track B merged into A, build clean. Waiting for Track C.")
6. **Final merge to main:**
   ```batch
   git checkout main
   git merge sprintN/track-a --no-ff -m "Sprint N: [description]"
   ```

#### Phase 5: Cleanup (Claude Desktop)
1. **Remove worktrees:**
   ```batch
   git worktree remove ../jjflex-Nb
   git worktree remove ../jjflex-Nc
   ```
2. **Delete track branches** (optional, after merge is confirmed good)
3. **Delete TRACK-INSTRUCTIONS.md** files (they're in git history if needed)
4. **Final cleanup phase** (sprint-specific: delete dead code, update docs, etc.)
5. **Clean build** both x64 and x86, verify installers
6. **Update Agent.md** with sprint completion status
7. **Archive sprint plan** to `C:/dev/jjf-private/planning/agile/archive/`
8. **Create test matrix** at `C:/dev/jjf-private/planning/agile/sprintN-test-matrix.md`
9. **Keyboard audit** (required if the sprint touched any key bindings — see below)

#### Keyboard Audit — Definition of Done for key-map changes

Any sprint that adds, removes, or remaps a keyboard binding MUST pass this audit before merging to main. The cost of a missed audit is a shipped hotkey nobody can discover through help — which violates the BlindCat anti-pattern #1 ("no in-app key reference") we specifically exist to avoid.

Audit checklist:

1. **Grep the sprint's diff for key-binding changes.** Search for new or modified
   entries in `JJFlexWpf/KeyCommands.cs` — specifically `BuildKeyTable()`, the
   `_defaultKeys` array, `_unboundNotes`, and `DoLeaderCommand` (where every
   Ctrl+J chord lives; leader chords are a `switch`, NOT registry rows) — plus
   `JJFlexWpf/KeyInventory.cs`'s `LeaderCommands[]`, `KeyScope` on any entry,
   and any Modern/Classic menu builders. Produce a list of affected keys + their
   new meanings.

   **This said "RegisterScope" until 2026-08-26 and NO SUCH SYMBOL EXISTS**
   — deliberately unbackticked, because a backticked name reads as an
   instruction to go and find it, and the symbol checker rightly treats it
   as one —
   not in any `.cs` or `.vb` file, only in this sentence. Scope is a FIELD
   (`KeyScope`, `Radios/KeyCommandTypes.cs:216`, 252 uses in `KeyCommands.cs`),
   never a registration call. Exactly the `KeyCommands.vb` failure again: a
   literal grep returns nothing, which reads as "no key bindings changed" and
   silently skips the audit. Found by a research agent surveying the leader
   layer, and verified before this correction.

   **This said `KeyCommands.vb` until 2026-08-21 and no such file exists** — it
   is C#, and it lives in `JJFlexWpf/`. A literal grep for the old name returns
   nothing, which reads as "no key bindings changed" and silently skips the
   audit. Six memory entries carried the same wrong name; all corrected.

2. **Update `docs/help/md/keyboard-reference.md`.** Every new binding gets a line in the appropriate scope section (Global / Radio / Logging / Home / Home Region sub-sections). Every removed binding gets its line deleted. Every remapped binding has its meaning updated.

3. **Update Command Finder search keywords.** The Command Finder (`Ctrl+/`) must return the new command when searched by name, synonym, or action verb. Check the registration metadata for the affected commands.

4. **Update context-sensitive help (F1).** If the new binding operates on a specific control or field, the F1 help for that control should mention the new hotkey.

5. **Update the changelog.** User-visible key changes get a line in `docs/CHANGELOG.md` under the current in-progress version. Key *removals* need heads-up language since someone somewhere may rely on them.

6. **Verify the CHM build rebuilds the keyboard reference page** so users who installed the previous release see updated help after updating.

7. **PRESS THE KEY.** A keyboard change is not verified by compiling. On
   2026-08-13 an Alt+L binding shipped completely dead, one build after being
   added: the handler tested `e.Key == Key.L`, which is *never* true while Alt
   is held, because WPF reports `Key.System` and puts the real key in
   `e.SystemKey`. It compiled, it reviewed clean, and the chord was simply never
   handled — so the screen reader read the focused control and the key appeared
   to do nothing at all. **Every new or changed binding gets pressed on a real
   build before it is called done.** The same applies to anything that claims to
   move focus: watch where focus actually lands, and listen to what is announced
   when it gets there.

   **Press it under BOTH screen readers whenever the binding involves holding a
   key, repeating a key, or push-to-talk.** JAWS and NVDA do not deliver held
   keys the same way: JAWS synthesises down/up PAIRS roughly 250 ms apart
   (measured 242-272 ms, first pair at the Windows repeat delay of about 512 ms)
   while NVDA passes a genuine hold. Any handler that asks "is the key still
   down" therefore works on one and fails on the other, **and the failure is
   invisible to whichever one you happen to use.** Two bugs of exactly this
   shape surfaced on 2026-08-25 — Freight Fate's hold-to-accelerate, and JJ
   Flexible's push-to-talk unkeying four times a second. Noel runs NVDA daily
   but tests under JAWS when a binding is in this class, so this work is never
   blocked on a tester and must not be sequenced as if it were. Detail:
   `memory/project_jaws_does_not_deliver_held_keys.md`.

   Related trap from the same day: `AutomationProperties.HeadingLevel` does NOT
   give a screen reader's single-letter navigation inside a dialog. `H` and
   friends live in **browse mode** — web pages and documents — while a WPF dialog
   runs in focus mode where `H` types a letter. Section navigation inside a
   dialog needs a real key (F6 / Shift+F6 is the Windows convention, and is what
   the Audio Workshop uses). Heading levels are still worth setting; they are
   just not navigation.

When to skip the audit: sprints that don't touch key bindings (pure UI tweaks, under-the-hood refactors, build-system changes). If in doubt, grep the diff — "did any file named `KeyCommands` or `KeyBinding*` change?" is a fast answer.

Future automation (not blocking — deferred): a build-time pass that introspects the KeyCommands registry, emits a canonical manifest, and fails the build if `keyboard-reference.md` is out of sync. Sprint 29+ candidate if the manual audit proves reliable.

---

### Parallel Track Rules

**CRITICAL: Always use `git worktree` for parallel CLI sessions.** Do NOT just check out different branches in the same working directory — CLI sessions will fight over files, lose changes, and produce checkout races.

**Lesson learned (Sprint 6):** Using branches without worktrees caused CLI sessions to collide — multiple sessions sharing one working directory led to file corruption and build issues. Worktrees are mandatory, not optional.

| Rule | Details |
|------|---------|
| Max concurrent tracks | 6 (practical limit for Claude CLI sessions) |
| Worktree naming | `../jjflex-Nx` (N=sprint, x=track letter a-f) |
| Branch naming | `sprintN/track-x` |
| Track instructions | `TRACK-INSTRUCTIONS.md` in each worktree root |
| CLI prompt format | `Start Sprint N Track X from TRACK-INSTRUCTIONS.md` |
| Build isolation | Each worktree builds independently |
| Commit style | Track-specific prefix: `Sprint N Track X: description` |

### Track Dependency Handling

When tracks have dependencies (e.g., Track A creates a base class that B and C need):
- **Option 1:** Track A completes the shared work first, user reports done, Claude merges into B/C branches, then user starts B/C
- **Option 2:** Dependent tracks create a minimal stub version and note in their instructions that Track A's version is canonical at merge time
- **Option 3:** Serial-then-parallel — Track A runs solo first, then B/C/D/E/F run in parallel after A merges

**Telling a track to reuse a symbol creates an invisible dependency on that
symbol staying put.** Learned 2026-08-12: one track was told to call
`AudioWorkshopDialog.MicAudioVerdict` so it could not grow a second vocabulary
for the same measurement — a good instruction that prevented a real duplication.
Meanwhile another track *moved* that method into a new shared class, which was
also right. Both merged with **zero textual conflict** and the build then failed.
Git cannot see this class of collision, so it will not warn you.

When a track instruction names a symbol to reuse, add: **"reuse X; if you
conclude X should move or change signature, report it instead of doing it."**
And after any multi-track merge, **build before declaring the merge clean** — a
clean `git merge` is not evidence that the result compiles.

### Commits
- Commit and push after completing each phase or significant chunk of work
- No PR required for work on feature branches
- Use descriptive commit messages following existing style

### Plan File Names
Plan files are named with three random ham-radio-flavored words, e.g. `barefoot-qrm-trap`. Use ham radio terms (QRM, QSO, ragchew, barefoot, rig, shack, pileup, splatter, etc.) mixed with random fun words. Keep it lighthearted — this is a ham radio project!

### Test Matrices
Create a separate test matrix file for each sprint: `C:/dev/jjf-private/planning/agile/sprintN-test-matrix.md`. This keeps the test checklist accessible during testing without having to dig through the full sprint plan. Include per-track functional tests, integration tests, and a screen reader matrix (JAWS + NVDA). Archive alongside the sprint plan when done.

### Resuming Work
If a session ends mid-task, tell Claude: "Resume [phase/task name] from `docs/barefoot-qrm-trap.md`"

Claude will:
1. Read the plan and `git status` to see what's done
2. Check `Agent.md` for recent work context
3. Continue from where work left off

To help resumption, Claude should:
- Update `Agent.md` with current progress before ending sessions
- Commit partial work with clear "WIP:" prefix if mid-phase
- Note the current step in commit messages when practical
