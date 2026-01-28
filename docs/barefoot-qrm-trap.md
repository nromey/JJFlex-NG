# JJFlexRadio Migration Plan: .NET 8.0 + 64-bit Support

## Goal
Migrate JJFlexRadio from .NET Framework 4.8 (x86) to .NET 8.0 (x64) with clean builds, improved accessibility (UIA enabled by default), and future-proofing for keyboard shortcuts and global keystrokes.

---

## Project Inventory

### Projects Requiring SDK-Style Conversion + .NET 8 Migration
| Project | Current State | Priority |
|---------|--------------|----------|
| `JJTrace\JJTrace.csproj` | Legacy net48 | 1 (leaf) |
| `escapes\escapes.csproj` | Legacy net48 | 1 (leaf) |
| `adif\adif.csproj` | Legacy net48 | 2 |
| `HamBands\HamBands.csproj` | Legacy net48 | 2 |
| `HamQTHLookup\HamQTHLookup.csproj` | Legacy net48 | 2 |
| `JJCountriesDB\JJCountriesDB.csproj` | Legacy net48 | 2 |
| `MsgLib\MsgLib.csproj` | Legacy net48 | 2 |
| `RadioBoxes\RadioBoxes.csproj` | Legacy net48 | 2 |
| `JJMinimalTelnet\JJMinimalTelnet.csproj` | Legacy net48 | 2 |
| `skcc\skcc.csproj` | Legacy net48 | 3 |
| `JJArclusterLib\JJArclusterLib.csproj` | Legacy net48 | 4 |
| `JJLogLib\JJLogLib.csproj` | Legacy net48 | 5 |
| `JJPortaudio\JJPortaudio\JJPortaudio.csproj` | Legacy net48 | 3 |
| `Radios\Radios.csproj` | Legacy net48 | 6 |

### Projects Needing TFM Update Only (Already SDK-Style)
| Project | Current TFM | New TFM |
|---------|-------------|---------|
| `FlexLib_API\FlexLib\FlexLib.csproj` | net462;net8.0-windows | net8.0-windows |
| `FlexLib_API\Util\Util.csproj` | net462;net8.0-windows | net8.0-windows |
| `FlexLib_API\UiWpfFramework\UiWpfFramework.csproj` | net462;net8.0-windows | net8.0-windows |
| `FlexLib_API\Vita\Vita.csproj` | net462;net8.0-windows | net8.0-windows |
| `PortAudioSharp-src-0.19.3\PortAudioSharp\PortAudioSharp.csproj` | net48;net8.0-windows | net8.0-windows |
| `P-Opus-master\POpusCodec.csproj` | net48;net8.0-windows | net8.0-windows |

### Main Application
| Project | Current TFM | New TFM |
|---------|-------------|---------|
| `JJFlexRadio.vbproj` | net48 | net8.0-windows |

---

## Phase 0: JJRadio Legacy Cleanup (Pre-requisite)

Remove non-Flex radio support inherited from JJRadio. This reduces migration scope and eliminates dead code.

### 0.1 Files to Remove from `Radios/`

**Icom radio support:**
- `Radios/Icom.cs` - Base Icom class
- `Radios/Icom9100.cs` - IC-9100 implementation
- `Radios/IcomIhandler.cs` - Icom command handler
- `Radios/ic9100filters.cs` - IC-9100 filter dialog
- `Radios/ic9100filters.Designer.cs`
- `Radios/ic9100filters.resx`

**Kenwood radio support:**
- `Radios/Kenwood.cs` - Base Kenwood class
- `Radios/KenwoodTS2000.cs` - TS-2000 implementation
- `Radios/KenwoodTS590.cs` - TS-590 implementation
- `Radios/KenwoodTS590SG.cs` - TS-590SG implementation
- `Radios/kenwoodIHandler.cs` - Kenwood command handler
- `Radios/TS2000Filters.cs` - TS-2000 filter dialog
- `Radios/TS2000Filters.Designer.cs`
- `Radios/TS2000Filters.resx`
- `Radios/TS590Filters.cs` - TS-590 filter dialog
- `Radios/TS590Filters.Designer.cs`
- `Radios/TS590Filters.resx`

**Generic/Stub radio support:**
- `Radios/Generic.cs` - Generic radio placeholder
- `Radios/StubRadios.cs` - Stub implementations

### 0.2 Files to Update

**`Radios/AllRadios.cs`** - Remove non-Flex entries from rig table:
- Remove Icom IC-9100 registration
- Remove Kenwood TS-2000, TS-590, TS-590SG registrations
- Remove Generic radio registration
- Keep only Flex 6000/8000 series entries

**`Radios/Radios.csproj`** - Remove file references (if explicit)

**`RigSelector.vb`** - Remove UI options for non-Flex radios (if present)

### 0.3 Verification Steps

1. Build solution after removals
2. Check for compiler errors referencing removed types
3. Fix any remaining references (likely in AllRadios.cs rig table)
4. Verify Flex radio selection still works in UI
5. Test connection to a Flex radio

### 0.4 Commit

```
chore: remove non-Flex radio support (Icom, Kenwood, Generic)

JJFlexRadio focuses exclusively on FlexRadio 6000/8000 series.
Remove legacy JJRadio multi-radio support to simplify codebase
before .NET 8 migration.

Files removed:
- Icom IC-9100 support
- Kenwood TS-2000/TS-590/TS-590SG support
- Generic radio stubs
```

---

## Phase 0.5: Complete FlexRadio Model Support

Ensure JJFlexRadio supports all current FlexRadio models with correct specifications.

### 0.5.1 Official FlexRadio Model Specifications

| Model | SCUs | Slices | Panadapters | Diversity | Notes |
|-------|------|--------|-------------|-----------|-------|
| FLEX-6300 | 1 | 2 | 2 | No | Entry-level, optional ATU |
| FLEX-6400 | 1 | 2 | 2 | No | 3rd-order preselectors |
| FLEX-6400M | 1 | 2 | 2 | No | + Maestro touchscreen |
| FLEX-6500 | 1 | 4 | 4 | No | 30dB bandpass filters, ATU included |
| FLEX-6600 | 2 | 4 | 4 | Yes | 7th-order filters, full duplex |
| FLEX-6600M | 2 | 4 | 4 | Yes | + Maestro touchscreen |
| FLEX-6700 | 2 | 8 | 8 | Yes | Flagship 6000 series |
| FLEX-6700R | 2 | 8 | 8 | Yes | Rack-mount variant |
| FLEX-8400 | 1 | 2 | 2 | No | 8000 series entry |
| FLEX-8400M | 1 | 2 | 2 | No | + Maestro touchscreen |
| FLEX-8600 | 2 | 4 | 4 | Yes | 8000 series mid-range |
| FLEX-8600M | 2 | 4 | 4 | Yes | + Maestro touchscreen |
| AU-510 | 1 | 2 | 2 | No | Aurora 500W, based on 8400 |
| AU-510M | 1 | 2 | 2 | No | + Maestro touchscreen |
| AU-520 | 2 | 4 | 4 | Yes | Aurora 500W, based on 8600 |
| AU-520M | 2 | 4 | 4 | Yes | + Maestro touchscreen |

**Key corrections from current code:**
- FLEX-8600 has **4 slices** (not 8 as shown in CLAUDE.md)
- FLEX-8400/8400M is **missing** from RigTable
- AU-510/AU-510M is **missing** from RigTable (only AU-520 listed)

### 0.5.2 Files to Update

**`Radios/AllRadios.cs`** - Add missing RIG IDs and RigTable entries:

```csharp
// Add new RIG ID constants (around line 2636)
public const int RIGIDFlex8400 = RIGIDFlex + 14;
public const int RIGIDAurora510 = RIGIDFlex + 15;

// Update RigTable (around line 2706)
public static RigElement[] RigTable =
{
    new RigElement(RIGIDFlex6300, "Flex6300", typeof(FlexRadio), FlexComDefaults),
    new RigElement(RIGIDFlex6400, "Flex6400/6400M", typeof(FlexRadio), FlexComDefaults),
    new RigElement(RIGIDFlex6500, "Flex6500", typeof(FlexRadio), FlexComDefaults),
    new RigElement(RIGIDFlex6600, "Flex6600/6600M", typeof(FlexRadio), FlexComDefaults),
    new RigElement(RIGIDFlex6700, "Flex6700/6700R", typeof(FlexRadio), FlexComDefaults),
    new RigElement(RIGIDFlex8400, "Flex8400/8400M", typeof(FlexRadio), FlexComDefaults),  // NEW
    new RigElement(RIGIDFlex8600, "Flex8600/8600M", typeof(FlexRadio), FlexComDefaults),
    new RigElement(RIGIDAurora510, "Flex Aurora (AU-510/AU-510M)", typeof(FlexRadio), FlexComDefaults),  // NEW
    new RigElement(RIGIDAurora, "Flex Aurora (AU-520/AU-520M)", typeof(FlexRadio), FlexComDefaults),
};
```

**`Radios/FlexInfo.cs`** - Update `IsRnnModel()` to include Aurora 520:

```csharp
private bool IsRnnModel(Radio radio)
{
    var model = radio?.Model ?? string.Empty;
    // 8000 series and Aurora AU-520 (based on 8600) support RNN
    return model.StartsWith("FLEX-8", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("AU-52", StringComparison.OrdinalIgnoreCase);
}
```

**`CLAUDE.md`** - Update Radio Support table with correct specs.

### 0.5.3 Model Detection Notes

FlexLib reports the model string via `theRadio.Model`. Detection patterns:
- 6000 series: `FLEX-6300`, `FLEX-6400`, `FLEX-6400M`, `FLEX-6500`, `FLEX-6600`, `FLEX-6600M`, `FLEX-6700`, `FLEX-6700R`
- 8000 series: `FLEX-8400`, `FLEX-8400M`, `FLEX-8600`, `FLEX-8600M`
- Aurora: `AU-510`, `AU-510M`, `AU-520`, `AU-520M`

For hardware capability checks, prefer FlexLib properties over model string parsing:
- `theRadio.DiversityIsAllowed` - True for 2-SCU radios
- `theRadio.MaxSlices` - Maximum slice count
- `theRadio.AvailableSlices` - Currently available slices (MultiFlex aware)

### 0.5.4 Verification Steps

1. Build solution after changes
2. Verify all models appear in rig selector UI
3. If possible, test connection with different radio models
4. Verify Feature Availability tab shows correct diversity support per model

### 0.5.5 Commit

```
feat: add FLEX-8400 and Aurora AU-510 to supported radios

Complete FlexRadio model support:
- Add FLEX-8400/8400M (1 SCU, 2 slices)
- Add Aurora AU-510/AU-510M (1 SCU, 2 slices, 500W)
- Fix documentation: 8600 has 4 slices, not 8
- Update RNN detection for Aurora AU-520

Ref: FlexRadio official specs (flexradio.com/comparison)
```

---

## Phase 1: SDK-Style Conversion (Leaf Projects)

Convert legacy .csproj files to SDK-style while keeping net48 temporarily.

### Files to Modify
- `JJTrace\JJTrace.csproj`
- `escapes\escapes.csproj`

### SDK-Style Template
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <Platforms>x86;x64</Platforms>
    <RootNamespace>{ProjectName}</RootNamespace>
    <AssemblyName>{ProjectName}</AssemblyName>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
  </PropertyGroup>

  <ItemGroup>
    <!-- Keep only necessary framework references -->
    <Reference Include="System.Windows.Forms" Condition="'$(UseWindowsForms)' != 'true'" />
  </ItemGroup>
</Project>
```

---

## Phase 2: SDK-Style Conversion (Dependent Projects)

Convert remaining legacy projects in dependency order.

### Tier 2 (depend on JJTrace/escapes)
- `adif\adif.csproj`
- `HamBands\HamBands.csproj`
- `HamQTHLookup\HamQTHLookup.csproj`
- `JJCountriesDB\JJCountriesDB.csproj`
- `MsgLib\MsgLib.csproj`
- `RadioBoxes\RadioBoxes.csproj`
- `JJMinimalTelnet\JJMinimalTelnet.csproj`

### Tier 3
- `skcc\skcc.csproj`
- `JJPortaudio\JJPortaudio\JJPortaudio.csproj`

### Tier 4-5
- `JJArclusterLib\JJArclusterLib.csproj`
- `JJLogLib\JJLogLib.csproj`

### Tier 6
- `Radios\Radios.csproj`

---

## Phase 3: Update to .NET 8.0 Target

Change all projects to target `net8.0-windows` only.

### FlexLib_API Projects
Update from `net462;net8.0-windows` to `net8.0-windows`:
- `FlexLib_API\FlexLib\FlexLib.csproj`
- `FlexLib_API\Util\Util.csproj`
- `FlexLib_API\UiWpfFramework\UiWpfFramework.csproj`
- `FlexLib_API\Vita\Vita.csproj`

### Audio Libraries
Update from `net48;net8.0-windows` to `net8.0-windows`:
- `PortAudioSharp-src-0.19.3\PortAudioSharp\PortAudioSharp.csproj`
- `P-Opus-master\POpusCodec.csproj`

### All Converted Projects
Change from `net48` to `net8.0-windows`.

---

## Phase 4: Native DLL 64-bit Support

### 4.1 Create Runtime Directory Structure
```
runtimes/
  win-x64/
    native/
      libopus.dll    (from LibSources\opus-1.5.2\build-x64\Release\opus.dll)
      portaudio.dll  (from LibSources\portaudio\build-x64\Release\portaudio_x64.dll, renamed)
```

### 4.2 Create NativeLoader.cs
New file: `NativeLoader.cs` in main project

```csharp
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace JJRadio
{
    public static class NativeLoader
    {
        public static void Initialize()
        {
            NativeLibrary.SetDllImportResolver(
                typeof(PortAudioSharp.PortAudio).Assembly,
                ResolveNativeLibrary);
            NativeLibrary.SetDllImportResolver(
                typeof(POpusCodec.OpusWrapper).Assembly,
                ResolveNativeLibrary);
        }

        private static IntPtr ResolveNativeLibrary(
            string libraryName,
            Assembly assembly,
            DllImportSearchPath? searchPath)
        {
            string arch = Environment.Is64BitProcess ? "x64" : "x86";
            string basePath = AppContext.BaseDirectory;

            string mappedName = libraryName.ToLowerInvariant() switch
            {
                "portaudio.dll" or "portaudio" => "portaudio.dll",
                "libopus.dll" or "libopus" => "libopus.dll",
                _ => libraryName
            };

            string fullPath = Path.Combine(basePath, "runtimes", $"win-{arch}", "native", mappedName);

            if (File.Exists(fullPath) && NativeLibrary.TryLoad(fullPath, out IntPtr handle))
                return handle;

            return IntPtr.Zero; // Fallback to default search
        }
    }
}
```

### 4.3 Update ApplicationEvents.vb
Add native loader initialization at startup:
```vb
Private Sub MyApplication_Startup(sender As Object, e As ApplicationServices.StartupEventArgs) Handles Me.Startup
    ' Initialize native library resolver FIRST
    NativeLoader.Initialize()

    ' Existing code...
    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 Or SecurityProtocolType.Tls13
    AddHandler System.Windows.Forms.Application.ThreadException, AddressOf CrashReporter.OnThreadException
    AddHandler AppDomain.CurrentDomain.UnhandledException, AddressOf CrashReporter.OnUnhandledException
End Sub
```

### 4.4 Update JJFlexRadio.vbproj
Add native DLL copy items:
```xml
<ItemGroup>
  <Content Include="runtimes\win-x64\native\*.dll">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <Link>%(Filename)%(Extension)</Link>
  </Content>
</ItemGroup>
```

---

## Phase 5: WebView2 Migration for Auth0

### 5.1 Add WebView2 Package to Radios.csproj
```xml
<PackageReference Include="Microsoft.Web.WebView2" Version="1.0.2478.35" />
```

### 5.2 Create AuthFormWebView2.cs
New file: `Radios\AuthFormWebView2.cs`

Replace the WebBrowser control with WebView2:
- Remove IE emulation registry hack (not needed)
- Use `WebView2.EnsureCoreWebView2Async()`
- Handle `NavigationCompleted` event
- Same Auth0 URL construction logic

### 5.3 Update AuthForm.cs
Add factory method:
```csharp
public static Form CreateAuthForm()
{
    return new AuthFormWebView2();
}
```

### 5.4 Update Callers
Change `new AuthForm()` to `AuthForm.CreateAuthForm()` throughout codebase.

---

## Phase 6: Main Application Update

### 6.1 Update JJFlexRadio.vbproj
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <OutputType>WinExe</OutputType>
    <PlatformTarget>x64</PlatformTarget>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <Platforms>x64</Platforms>
    <RootNamespace>JJRadio</RootNamespace>
    <AssemblyName>JJFlexRadio</AssemblyName>
    <!-- ... existing properties ... -->
  </PropertyGroup>
</Project>
```

### 6.2 Update install.bat
Change output directory detection:
```batch
set "OUTDIR=%~1\bin\x64\%cfg%\net8.0-windows\win-x64"
```

### 6.3 Update install template.nsi
```nsi
InstallDir "$PROGRAMFILES64\jjshaffer\MYPGM"
```

---

## Phase 7: Cleanup

### Remove Conditional Compilation
- Remove `#if NET48` / `#if NET8_0_OR_GREATER` blocks
- Keep only .NET 8 code paths

### Delete Legacy Files
- Old 32-bit native DLLs from root output
- Legacy WebBrowser-based AuthForm (if separated)
- net48 build configurations from solution

### Update Documentation
- `CLAUDE.md`: Update build commands for x64
- `MIGRATION.md`: Document completed migration

---

## Critical Files to Modify

| Phase | File | Changes |
|-------|------|---------|
| 0 | `Radios\AllRadios.cs` | Remove non-Flex rig table entries |
| 0 | `Radios\*.cs` (Icom/Kenwood) | Delete 16 files |
| 0.5 | `Radios\AllRadios.cs` | Add FLEX-8400, Aurora AU-510 entries |
| 0.5 | `Radios\FlexInfo.cs` | Update IsRnnModel() for Aurora |
| 0.5 | `CLAUDE.md` | Fix radio specs table |
| 1-2 | All legacy .csproj files | SDK-style conversion |
| 3 | All projects | TFM to net8.0-windows |
| 4 | `NativeLoader.cs` | New file - 64-bit DLL resolver |
| 4 | `ApplicationEvents.vb` | Add NativeLoader.Initialize() |
| 5 | `Radios\AuthFormWebView2.cs` | New file - WebView2 implementation |
| 5 | `Radios\AuthForm.cs` | Add factory method |
| 5 | `Radios\Radios.csproj` | SDK conversion, WebView2 package |
| 6 | `JJFlexRadio.vbproj` | TFM to net8.0-windows, x64 platform |
| 6 | `install.bat` | x64 output paths |
| 6 | `install template.nsi` | PROGRAMFILES64, WebView2 bootstrap |

---

## Verification Plan

### Build Verification
```batch
msbuild /t:Rebuild /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64
```

### Runtime Testing
1. **Startup**: Launch app, verify no native DLL load errors
2. **Audio**: Test PortAudio initialization, Opus encode/decode
3. **SmartLink Auth**: Test WebView2 Auth0 flow end-to-end
4. **Radio Connection**: Connect to local/remote radio
5. **Accessibility**: Test with Windows Narrator, verify UIA works

### Installer Testing
1. Run `install.bat`
2. Verify installer creates correctly
3. Install on clean system
4. Verify 64-bit DLLs in Program Files

---

## WebView2 Runtime Strategy

Use **Evergreen Bootstrap** approach:
- Include `MicrosoftEdgeWebview2Setup.exe` (~2MB) in installer
- Run silently during install: `MicrosoftEdgeWebview2Setup.exe /silent /install`
- Most Windows 10/11 users already have it via Edge (no-op in that case)
- Download from: https://developer.microsoft.com/en-us/microsoft-edge/webview2/

Add to `install template.nsi`:
```nsi
; Install WebView2 runtime if needed
SetOutPath "$INSTDIR"
File "MicrosoftEdgeWebview2Setup.exe"
ExecWait '"$INSTDIR\MicrosoftEdgeWebview2Setup.exe" /silent /install'
Delete "$INSTDIR\MicrosoftEdgeWebview2Setup.exe"
```

---

## Risk Mitigation

| Risk | Mitigation |
|------|------------|
| WebView2 runtime not installed | Evergreen Bootstrap bundled in installer |
| Native DLL load failure | Test on clean VM, verify runtimes folder structure |
| VB.NET app model differences | Test My.Settings, startup/shutdown behavior |
| Build warnings remain | Address SDK-style conversion warnings iteratively |

---

## Benefits After Migration

1. **Performance**: .NET 8 offers 20-40% better throughput
2. **Accessibility**: UIA enabled by default, better screen reader support
3. **Security**: TLS 1.3, modern crypto defaults
4. **Memory**: 64-bit address space for large operations
5. **Maintainability**: Single TFM, cleaner builds
6. **Future**: Foundation for keyboard shortcuts, global hooks
