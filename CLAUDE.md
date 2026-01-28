# JJFlexRadio

Windows desktop application for controlling FlexRadio transceivers (6000/8000 series). Alternative UI to SmartSDR, created by Jim Shaffer. Current version: 4.1.x, using FlexLib v4.0.1.

## Quick Reference

| Item | Value |
|------|-------|
| Solution | `JJFlexRadio.sln` |
| Build (Release) | `msbuild /t:Rebuild /p:Configuration=Release /p:Platform=x86` |
| Build (Debug) | `msbuild /t:Build /p:Configuration=Debug /p:Platform=x86` |
| Installer | Run `install.bat` after Release build |
| Output | `bin\Release\JJFlexRadio [version].exe` |

**Important**: Close running JJFlexRadio before building (Radios.dll lock).

## Tech Stack

- **Languages**: VB.NET (main app) + C# (libraries)
- **Frameworks**: `net48` (legacy) and `net8.0-windows` (modern)
- **UI**: WinForms (primary), WPF (UiWpfFramework)
- **Native deps**: Opus 1.5.2, PortAudio 19.7.0
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
- Set `AccessibleName` and `AccessibleRole` on controls
- Keep unsupported/disabled controls out of tab order
- Use Feature Availability tab to explain why features are unavailable

### Changelog Conventions
Write entries in personal, first-person explanatory tone (see `docs/CHANGELOG.md`).

## Build Notes

### Platforms
- Primary: x86 (Release|net48) for installer
- Also builds: x64, AnyCPU, net8.0-windows

### Installer Generation
```batch
install.bat
```
Generates `install.nsi` from template, builds `deleteList.txt`, runs `makensis`.

### Known Warnings (safe to ignore)
- Mixed target framework warnings (net48/net8)
- System.Drawing.Common version conflicts
- WPF assembly resolution warnings in net8 builds
- Reference unification warnings

### Security Notes
- DotNetZip being replaced with `System.IO.Compression` (Zip Slip CVE)
- Use safe extraction pattern for any zip operations

## Radio Support

| Model | SCUs | Diversity | Max Slices |
|-------|------|-----------|------------|
| FLEX-6300 | 1 | No | 2 |
| FLEX-6400(M) | 1 | No | 2 |
| FLEX-6500 | 1 | No | 4 |
| FLEX-6600(M) | 2 | Yes | 4 |
| FLEX-6700(R) | 2 | Yes | 8 |
| FLEX-8600(M) | 2 | Yes | 8 |

Detection: Use `theRadio.Model`, `theRadio.DiversityIsAllowed`, `theRadio.MaxSlices` rather than hardcoding.

## Related Documentation

| File | Description |
|------|-------------|
| `MIGRATION.md` | FlexLib upgrade guide, TLS wrapper notes |
| `docs/remote-migration.md` | SmartLink/Remote restoration plan |
| `docs/FlexLib-v4.0.1-Missing-Features.md` | Advanced features catalog (Diversity, RNN, etc.) |
| `docs/CHANGELOG.md` | Version history |
| `Agent.md` | Recent work summary |

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
1. Check `AuthForm.cs` for Auth0 flow
2. Verify TLS 1.2+ in network traces
3. See `docs/remote-migration.md` for current state
