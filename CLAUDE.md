# JJFlexRadio

Windows desktop application for controlling FlexRadio transceivers (6000/8000 series). Alternative UI to SmartSDR, created by Jim Shaffer. Current version: 4.1.x, using FlexLib v4.0.1.

## Quick Reference

| Item | Value |
|------|-------|
| Solution | `JJFlexRadio.sln` |
| Build x64 (Release) | `dotnet build JJFlexRadio.sln -c Release -p:Platform=x64` |
| Build x86 (Release) | `dotnet build JJFlexRadio.sln -c Release -p:Platform=x86` |
| Build (Debug) | `dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64` |
| Rebuild (Release) | `dotnet build JJFlexRadio.sln -c Release -p:Platform=x64 --no-incremental` |
| Installer | Runs automatically after Release build |
| Output x64 | `bin\x64\Release\net8.0-windows\win-x64\JJFlexRadio.exe` |
| Output x86 | `bin\x86\Release\net8.0-windows\win-x86\JJFlexRadio.exe` |
| Installer x64 | `Setup JJFlexRadio_[version]_x64.exe` |
| Installer x86 | `Setup JJFlexRadio_[version]_x86.exe` |

**Note**: Use `dotnet build` (preferred) or `msbuild` if in VS Developer shell. Add `--verbosity minimal` to reduce output noise.

**Important**: Close running JJFlexRadio before building (Radios.dll lock).

## Tech Stack

- **Languages**: VB.NET (main app) + C# (libraries)
- **Framework**: `net8.0-windows` (.NET 8)
- **Platforms**: x64 (primary), x86 (legacy support)
- **UI**: WinForms (primary), WPF (UiWpfFramework)
- **Auth**: WebView2 (Edge/Chromium) for SmartLink Auth0
- **Native deps**: Opus 1.5.2, PortAudio 19.7.0 (architecture-specific in `runtimes/`)
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

### Accessibility Guidelines
- Remove `&` from menu labels (interferes with screen readers)
- Always Set `AccessibleName` and `AccessibleRole` on controls
- Keep unsupported/disabled controls out of tab order
- Use Feature Availability tab to explain why radio features are unavailable due to subscription unavailable or model spec deficiencies

### Changelog Conventions
Write entries in personal, first-person explanatory tone (see `docs/CHANGELOG.md`).

## Build Notes

### Build Commands
```batch
# Build x64 Release (recommended)
dotnet build JJFlexRadio.sln -c Release -p:Platform=x64

# Build x86 Release (for older 32-bit systems)
dotnet build JJFlexRadio.sln -c Release -p:Platform=x86

# Minimal output (recommended for CI/automation)
dotnet build JJFlexRadio.sln -c Release -p:Platform=x64 --verbosity minimal
```

### Platforms
- Primary: x64 (64-bit, recommended)
- Legacy: x86 (32-bit, for older systems)
- Framework: `net8.0-windows` only

### Installer Generation
Installer runs automatically as post-build step for Release builds. Creates:
- `Setup JJFlexRadio_[version]_x64.exe` (64-bit installer)
- `Setup JJFlexRadio_[version]_x86.exe` (32-bit installer)

The installer:
- Auto-detects architecture from build output
- Installs to correct Program Files folder (64-bit vs 32-bit)
- Includes architecture suffix in filename

### Native DLLs
Architecture-specific native libraries are in:
- `runtimes/win-x64/native/` - 64-bit portaudio.dll, libopus.dll
- `runtimes/win-x86/native/` - 32-bit portaudio.dll, libopus.dll

`NativeLoader.vb` resolves the correct DLLs at runtime.

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

## Migration Status (.NET 8 + x64) - COMPLETED

**All phases complete:**
- Phase 0: Legacy cleanup (removed Icom, Kenwood, Generic radio support)
- Phase 0.5: Added FLEX-8400 and Aurora AU-510 to RigTable
- Phase 1-2: All C# projects converted to SDK-style
- Phase 3: All projects updated to `net8.0-windows` only
- Phase 4: Native DLL loading with architecture detection (`NativeLoader.vb`)
- Phase 5: WebView2 migration for Auth0 (`AuthFormWebView2.cs`)
- Phase 6: Dual x86/x64 build support with architecture-specific installers
- Phase 7: Cleanup (removed conditional compilation, updated documentation)

See `docs/barefoot-qrm-trap.md` for original migration plan.

## Related Documentation

| File | Description |
|------|-------------|
| `MIGRATION.md` | FlexLib upgrade guide, TLS wrapper notes |
| `docs/remote-migration.md` | SmartLink/Remote restoration plan |
| `docs/FlexLib-v4.0.1-Missing-Features.md` | Advanced features catalog (Diversity, RNN, etc.) |
| `docs/CHANGELOG.md` | Version history |
| `docs/barefoot-qrm-trap.md` | .NET 8.0 + 64-bit migration plan |
| `Agent.md` | Recent work summary (session context) |

## Releases

### Version Bump Checklist (IMPORTANT!)

**You MUST update BOTH files when bumping the version:**

1. **`JJFlexRadio.vbproj`** - Update these three lines:
   ```xml
   <Version>4.1.X</Version>
   <AssemblyVersion>4.1.X.0</AssemblyVersion>
   <FileVersion>4.1.X.0</FileVersion>
   ```

2. **`My Project\AssemblyInfo.vb`** - Update these three lines:
   ```vb
   <Assembly: AssemblyVersion("4.1.X.0")>
   <Assembly: AssemblyFileVersion("4.1.X.0")>
   <Assembly: AssemblyInformationalVersion("4.1.X")>
   ```

**Why both?** The AssemblyInfo.vb attributes override the project file settings. If you only update the .vbproj, the compiled exe will have the OLD version number. This has confused multiple AI assistants (Claude, Codex, Gemini) - don't let it confuse you too!

### Building Clean Installers

**Problem:** Incremental builds may use cached binaries with old version numbers.

**Solution:** Always do a clean build when creating release installers:

```batch
# Option 1: Use the build script (recommended)
build-installers.bat

# Option 2: Manual clean build
rmdir /s /q bin\x64\Release bin\x86\Release
dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x64 --no-incremental
dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x86 --no-incremental
```

**Verify the version before distributing:**
```batch
powershell -Command "(Get-Item 'bin\x64\Release\net8.0-windows\win-x64\JJFlexRadio.exe').VersionInfo.ProductVersion"
```

### Creating a GitHub Release

1. **Bump version** in both files (see checklist above)

2. **Commit the version bump**:
   ```batch
   git add JJFlexRadio.vbproj "My Project\AssemblyInfo.vb"
   git commit -m "Bump version to 4.1.X"
   ```

3. **Create and push a tag**:
   ```batch
   git tag -a v4.1.X -m "Release 4.1.X - Brief description"
   git push origin main --tags
   ```

4. **GitHub Actions takes over**:
   - Builds both x64 and x86 installers
   - Creates a GitHub Release with auto-generated notes
   - Uploads both installer executables to the Releases page

### Workflow Files
- `.github/workflows/release.yml` - Tag-triggered release (builds + publishes installers)
- `.github/workflows/windows-build.yml` - CI build on push/PR (validation only)

### Local Build Scripts

| Script | Purpose |
|--------|---------|
| `build-installers.bat` | Clean build + create both x64/x86 installers |
| `build-installers.bat x64` | Build x64 installer only |
| `build-installers.bat x86` | Build x86 installer only |
| `install.bat` | Low-level installer script (called by build-installers.bat) |

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

### Debug Remote/SmartLink
1. Check `AuthFormWebView2.cs` for Auth0 flow (uses WebView2/Edge)
2. Legacy `AuthForm.cs` (IE-based) kept as fallback, marked `[Obsolete]`
3. Verify TLS 1.2+ in network traces
4. See `docs/remote-migration.md` for current state

## Workflow

### Commits
- Commit and push after completing each phase or significant chunk of work
- No PR required for work on feature branches
- Use descriptive commit messages following existing style

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
