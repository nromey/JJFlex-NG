# Agent Summary

This document captures the recent work performed on the JJ-Flex repository and the current state needed for continuing flexlib integration.

**Repository root:** `C:\dev\JJFlex-NG`
**Worktree (active):** `C:\Users\nrome\.claude-worktrees\JJFlex-NG\cool-mendel`
**Branch:** `cool-mendel`

## 1) Overview
- Primary objectives: Migrate JJFlexRadio from .NET Framework 4.8 to .NET 8, add x64 support
- **Current state: Migration COMPLETE (all 7 phases done)**
- Application now builds for both x64 and x86, targets .NET 8 only

## 2) Technical Foundation
- Solution: `JJFlexRadio.sln` (main solution)
- Languages: mixed VB.NET and C# projects targeting `net8.0-windows`
- Platforms: x64 (primary), x86 (legacy support)
- Packaging: NSIS-based installer with architecture-specific outputs
- FlexLib v4 location: `FlexLib_API/` inside the repo

## 3) Migration Completed (All Phases)

### Phase 0: Legacy Cleanup
- Removed non-Flex radio support (Icom, Kenwood, Generic)
- Simplified codebase to focus on FlexRadio 6000/8000 series

### Phase 0.5: Complete FlexRadio Model Support
- Added FLEX-8400/8400M to RigTable
- Added Aurora AU-510/510M to RigTable
- Updated IsRnnModel() to recognize Aurora AU-520 as RNN-capable

### Phase 1-2: SDK-style Conversion
- All C# projects converted to SDK-style format
- Tier 1 (leaf): JJTrace, escapes
- Tier 2: adif, HamBands, HamQTHLookup, JJCountriesDB, MsgLib, RadioBoxes, JJMinimalTelnet
- Tier 3: skcc, JJPortaudio
- Tier 4-5: JJArclusterLib, JJLogLib
- Tier 6: Radios

### Phase 3: .NET 8 Migration
- All projects updated to target `net8.0-windows` only
- Fixed VB.NET namespace ambiguity (`Windows.Forms` -> `System.Windows.Forms`)
- Added System.IO.Ports NuGet package where needed

### Phase 4: Native DLL Support
- Created `runtimes/win-x86/native/` and `runtimes/win-x64/native/` structure
- Implemented `NativeLoader.vb` for architecture-aware DLL loading
- Integrated into `ApplicationEvents.vb` startup

### Phase 5: WebView2 Migration
- Created `AuthFormWebView2.cs` using WebView2 (Edge/Chromium) for Auth0
- Added factory method to `AuthForm.cs`
- Marked legacy WebBrowser-based AuthForm as `[Obsolete]`
- Added Microsoft.Web.WebView2 NuGet package

### Phase 6: Dual Architecture Build Support
- Updated project files for conditional x64/x86 targeting
- Modified `install.bat` to detect architecture and add suffix to installer name
- Updated `install template.nsi` with MYPROGFILES placeholder
- Installers now named: `Setup JJFlexRadio_[version]_x64.exe` / `_x86.exe`

### Phase 7: Cleanup
- Removed `#if NET48` / `#if NETFRAMEWORK` conditional compilation blocks
- Cleaned up `SslClientTls12.cs` (TLS 1.3|1.2 only)
- Cleaned up `AuthForm.cs` (TLS 1.3|1.2 only)
- Updated documentation (CLAUDE.md, Agent.md)

## 4) Build Status
- **x86 Release**: Builds and creates installer successfully
- **x64 Release**: Builds successfully (requires x64 native DLLs for full functionality)

Build commands:
```batch
# 64-bit build (recommended)
dotnet build JJFlexRadio.sln -c Release -p:Platform=x64

# 32-bit build
dotnet build JJFlexRadio.sln -c Release -p:Platform=x86
```

## 5) Native DLLs Required

For full x64 support, compile 64-bit versions of:
- `portaudio.dll` (PortAudio 19.7.0)
- `libopus.dll` (Opus 1.5.2)

Place in `runtimes/win-x64/native/`. See README.md in that folder for build instructions.

## 6) Useful Paths and Artifacts
- Repo root: `C:\dev\JJFlex-NG`
- Worktree: `C:\Users\nrome\.claude-worktrees\JJFlex-NG\cool-mendel`
- FlexLib v4: `FlexLib_API/`
- Migration plan: `docs/barefoot-qrm-trap.md`
- Branch: `cool-mendel`

## 7) Key Files Modified/Created

### New Files
- `NativeLoader.vb` - Architecture-aware native DLL loading
- `Radios/AuthFormWebView2.cs` - WebView2-based Auth0 form
- `runtimes/win-x86/native/` - x86 native DLLs
- `runtimes/win-x64/native/README.md` - x64 build instructions

### Key Modified Files
- `JJFlexRadio.vbproj` - SDK-style, net8.0-windows, dual x64/x86
- `Radios/Radios.csproj` - SDK-style, WebView2 package
- `install.bat` - Architecture detection, suffix naming
- `install template.nsi` - MYPROGFILES placeholder
- `FlexLib_API/FlexLib/SslClientTls12.cs` - Removed conditional compilation
- `CLAUDE.md` - Updated build commands and migration status

## 8) Commits Made (Migration Sessions)
1. `chore: remove non-Flex radio support (Icom, Kenwood, Generic)`
2. `docs: update CLAUDE.md with dotnet build commands`
3. `feat: add FLEX-8400 and Aurora AU-510 to supported radios`
4. `fix: use PowerShell for install.nsi generation`
5. `refactor: convert JJTrace and escapes to SDK-style projects`
6. `refactor: convert remaining C# projects to SDK-style`
7. `chore: delete unused backup and experimental files`
8. `refactor: migrate all projects to net8.0-windows (Phase 3)`
9. `feat: add architecture-aware native DLL loading (Phase 4)`
10. `feat: migrate Auth0 login from WebBrowser to WebView2 (Phase 5)`
11. `feat: support both x86 and x64 builds with arch-specific installers (Phase 6)`
12. `chore: cleanup conditional compilation, update docs (Phase 7)` [pending]

## 9) Accessibility Notes (Quick Guide)
- Keep unsupported controls out of tab order
- Use explicit, ampersand-free labels for menus/tabs
- Set `AccessibleName`/`AccessibleRole` where appropriate
- Show feature gating reasons in Feature Availability view

## 10) Session Notes (2026-01-29)
- **Migration COMPLETE** - All 7 phases finished
- x86 build tested and working (installer created)
- x64 build tested and working (native DLLs compiled)
- Documentation updated

## 11) GitHub Actions & Release Workflow

### CI Workflow (`.github/workflows/windows-build.yml`)
- Triggers on push, PR, and manual dispatch
- Matrix build for x64 and x86
- Uses .NET 8 SDK (`dotnet build`)
- Uploads build artifacts for validation

### Release Workflow (`.github/workflows/release.yml`)
- **Triggers on version tags** (e.g., `v4.1.9`)
- Matrix build for both architectures
- Sets up NSIS via `joncloud/makensis-action@v4.1`
- Builds and uploads installers as artifacts
- Creates GitHub Release with auto-generated notes
- Attaches both installer executables

### Creating Releases
```batch
# 1. Bump version in JJFlexRadio.vbproj
# 2. Commit the change
git commit -am "Bump version to 4.1.X"

# 3. Tag and push
git tag -a v4.1.X -m "Release 4.1.X - description"
git push origin cool-mendel --tags
```

## 12) Native DLL Build Script

`build-native-x64.bat` - Compiles x64 versions of libopus.dll and portaudio.dll:
- Uses VS 2026 CMake tools
- Clones Opus from GitLab, PortAudio from GitHub
- Builds for x64 architecture
- Copies DLLs to `runtimes/win-x64/native/`

## 13) Version History
- **v4.1.9** (2026-01-29) - First release using tag-based workflow
  - .NET 8 migration complete
  - Dual x64/x86 architecture support
  - WebView2 for Auth0
  - GitHub Actions release automation

----

*Generated by the agent to capture migration completion status and release workflow.*
