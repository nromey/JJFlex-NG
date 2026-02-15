# Sprint 11 — Track C: Dead Code Deletion + Cleanup

**Branch:** `sprint11/track-c`
**Directory:** `C:\dev\jjflex-11c`
**Depends on:** Track A must complete first (merges into this branch before work begins)
**Build command:** `dotnet clean JJFlexRadio.sln -c Release -p:Platform=x64 && dotnet build JJFlexRadio.sln -c Release -p:Platform=x64 --verbosity minimal`

## Goal

Delete all dead WinForms code from Radios/ and root. Remove the RadioBoxes project entirely. Update installer files and project references. Verify clean builds on both platforms.

## Commit Strategy

Commit after each phase. Message format: `Sprint 11 Phase 11.X: description`

---

## Phase 11.9: Delete Dead WinForms Files

### Pre-deletion Verification

Before deleting anything, verify each file is truly dead. After Track A merges:
- `Flex6300Filters` should NOT be instantiated anywhere (FlexBase.Start uses WpfFilterAdapter)
- `FlexMemories` should NOT be instantiated anywhere (WpfMemoryManager replaces it)
- All other Radios/ WinForms files are only referenced by Flex6300Filters

Run these greps to verify:
```
grep -r "new Flex6300Filters" --include="*.cs" --include="*.vb"  → should return 0 hits
grep -r "new FlexMemories" --include="*.cs" --include="*.vb"  → should return 0 hits
grep -r "new FlexATUMemories" --include="*.cs" --include="*.vb"  → check
grep -r "new FlexEq" --include="*.cs" --include="*.vb"  → check
grep -r "new FlexTNF" --include="*.cs" --include="*.vb"  → check
grep -r "new TXControls" --include="*.cs" --include="*.vb"  → check
grep -r "new FlexInfo" --include="*.cs" --include="*.vb"  → check
```

### Files to Delete from Radios/

Each of these is a WinForms UserControl or Form with .cs + .Designer.cs + .resx:

| File Set | Lines | Notes |
|----------|-------|-------|
| `Flex6300Filters.cs` + `.Designer.cs` + `.resx` | ~2,800 | Main filter/DSP UI — replaced by WpfFilterAdapter |
| `FlexMemories.cs` + `.Designer.cs` + `.resx` | ~680 | Memory form — replaced by WpfMemoryManager + WPF MemoriesDialog |
| `FlexATUMemories.cs` + `.Designer.cs` + `.resx` | varies | ATU memory form — already delegate-replaced in Sprint 10 |
| `FlexEq.cs` + `.Designer.cs` + `.resx` | varies | Equalizer form — WPF EqualizerDialog exists |
| `FlexTNF.cs` + `.Designer.cs` + `.resx` | varies | TNF control — WPF TNFDialog exists |
| `TXControls.cs` + `.Designer.cs` + `.resx` | varies | TX controls form — WPF TXControlsDialog exists |
| `FlexInfo.cs` + `.Designer.cs` + `.resx` | varies | Radio info form — WPF RadioInfoDialog exists |

**IMPORTANT:** Before deleting FlexTNF, verify PanAdapterManager doesn't reference it. The old Flex6300Filters created `flexTNF = new FlexTNF(rig)` in PanSetup(). After Track A extraction, PanAdapterManager should NOT have this reference — but verify.

### Files to Delete from Root

| File | Notes |
|------|-------|
| `LogPanel.vb` | Old WinForms logging panel — WPF LoggingPanel exists |
| `RadioPane.vb` | Old WinForms radio pane — dead since WPF migration |

Verify these are not referenced:
```
grep -r "LogPanel" --include="*.cs" --include="*.vb" → check (might be referenced in comments)
grep -r "RadioPane" --include="*.cs" --include="*.vb" → check
```

### Delete RadioBoxes Project

RadioBoxes contains WinForms custom controls (Combo, NumberBox, InfoBox, ChekBox, MainBox). After Track A and B remove all live usages:

1. **Remove from solution file** (`JJFlexRadio.sln`):
   - Delete the `Project` block for RadioBoxes (GUID: `{C15F2768-3386-4703-AE1D-CC4E59348610}`)
   - Delete all `GlobalSection` entries for this GUID

2. **Remove project reference from Radios.csproj** (line 70):
   ```xml
   <ProjectReference Include="..\RadioBoxes\RadioBoxes.csproj" />
   ```

3. **Remove project reference from JJFlexRadio.vbproj** (line 474):
   ```xml
   <ProjectReference Include="RadioBoxes\RadioBoxes.csproj" />
   ```

4. **Delete the entire directory**: `RadioBoxes/`

### Update Radios.csproj

Remove `<Compile>` items for all deleted files. The Radios.csproj likely uses glob patterns for .cs files, but check — if it has explicit Compile items for the deleted files, remove them.

Also remove `<EmbeddedResource>` items for deleted .resx files.

### Update JJFlexRadio.vbproj

Remove `<Compile>` items for deleted .vb files (LogPanel.vb, RadioPane.vb, etc.).
Remove `<EmbeddedResource>` items for any deleted .resx files.

**REMINDER:** JJFlexRadio.vbproj uses explicit `<Compile Include>` entries — files are NOT auto-discovered. You must manually remove the entries for deleted files, or the build will fail with missing file errors.

### Build Checkpoint

After all deletions: `dotnet clean && dotnet build` for x64. Fix any missing reference errors. The main issues will be:
- `using RadioBoxes;` / `Imports RadioBoxes` statements in remaining files
- References to types from deleted files
- .resx resource references

---

## Phase 11.10: Cleanup

### Remove Dead Using/Imports Statements

Search and remove:
```
grep -r "using RadioBoxes" --include="*.cs"
grep -r "Imports RadioBoxes" --include="*.vb"
grep -r "using System.Windows.Forms" --include="*.cs"  → remove where no longer needed
```

**Known locations for RadioBoxes references:**
- `About.vb` line 57: `RadioBoxes.MainBox.Version.ToString` — replace with hardcoded version or remove
- `Form1.vb` line 15: `Imports RadioBoxes` — deleted by Track B
- `Form1.designer.vb`: Multiple references — deleted by Track B
- `StatusBoxAdapter.vb`: Comments only — update comments
- `MainWindow.xaml.cs`: Comments only — update comments
- `Radios/FlexATUMemories.Designer.cs`: Deleted above
- `Radios/FlexMemories.cs`: Deleted above
- `Radios/Flex6300Filters.cs`: Deleted above

### About.vb Fix

`About.vb` line 57 references `RadioBoxes.MainBox.Version`. After deleting RadioBoxes:
```vb
' OLD:
"RadioBoxes.dll: " & RadioBoxes.MainBox.Version.ToString & vbCrLf &

' NEW: Remove this line entirely, or replace with:
' (RadioBoxes is gone, no version to report)
```

### Update Installer Files

**File: `install.nsi`** (or `install template.nsi`)
- Remove any references to `RadioBoxes.dll`
- Remove install/uninstall entries for deleted files

**File: `deleteList.txt`**
- Add all deleted DLLs to the delete list so old installations clean up

### Build Both Platforms

```batch
dotnet clean JJFlexRadio.sln -c Release -p:Platform=x64
dotnet build JJFlexRadio.sln -c Release -p:Platform=x64 --verbosity minimal

dotnet restore JJFlexRadio.sln -p:Platform=x86
dotnet clean JJFlexRadio.sln -c Release -p:Platform=x86
dotnet build JJFlexRadio.sln -c Release -p:Platform=x86 --verbosity minimal
```

**NOTE:** x86 often needs `dotnet restore` explicitly before build. See memory note about NETSDK1047.

### Verify Installers

After clean build, verify installer .exe files were generated:
- `Setup JJFlexRadio_*_x64.exe`
- `Setup JJFlexRadio_*_x86.exe`

### Update Documentation

**File: `docs/CHANGELOG.md`**
Add Sprint 11 entry following the project's changelog conventions (warm, first-person, no jargon):
- Mention the dead code removal (~X,000 lines)
- Mention the app is now fully WPF
- Don't mention WinForms, RadioBoxes, adapters, interfaces, or technical plumbing

**File: `Agent.md`**
Update with Sprint 11 completion status.

### Archive Sprint Plan

Copy `docs/planning/frolicking-forging-map.md` to `docs/planning/agile/archive/frolicking-forging-map.md`.

### Build Checkpoint

Full clean build both platforms. Installers generated. 0 errors. No references to dead code remain.

---

## Verification Checklist

Run these after all work is complete:

```bash
# No references to dead types
grep -r "Flex6300Filters" --include="*.cs" --include="*.vb"  # 0 hits (except IFilterControl.cs comments, if any)
grep -r "RadioBoxes" --include="*.cs" --include="*.vb"       # 0 hits (except comments)
grep -r "FlexMemories" --include="*.cs" --include="*.vb"     # 0 hits (except IMemoryManager.cs comments, if any)
grep -r "LogPanel" --include="*.cs" --include="*.vb"         # 0 hits
grep -r "RadioPane" --include="*.cs" --include="*.vb"        # 0 hits

# RadioBoxes.dll absent from output
ls bin/x64/Release/net8.0-windows/win-x64/RadioBoxes.dll  # should not exist

# Both installers present
ls "Setup JJFlexRadio_*_x64.exe"
ls "Setup JJFlexRadio_*_x86.exe"
```

---

## Files Deleted

| File | Lines Removed |
|------|---------------|
| `Radios/Flex6300Filters.cs` | ~2,800 |
| `Radios/Flex6300Filters.Designer.cs` | (designer) |
| `Radios/Flex6300Filters.resx` | (resources) |
| `Radios/FlexMemories.cs` | ~680 |
| `Radios/FlexMemories.Designer.cs` | (designer) |
| `Radios/FlexMemories.resx` | (resources) |
| `Radios/FlexATUMemories.cs` | varies |
| `Radios/FlexATUMemories.Designer.cs` | (designer) |
| `Radios/FlexATUMemories.resx` | (resources) |
| `Radios/FlexEq.cs` | varies |
| `Radios/FlexEq.Designer.cs` | (designer) |
| `Radios/FlexEq.resx` | (resources) |
| `Radios/FlexTNF.cs` | varies |
| `Radios/FlexTNF.Designer.cs` | (designer) |
| `Radios/FlexTNF.resx` | (resources) |
| `Radios/TXControls.cs` | varies |
| `Radios/TXControls.Designer.cs` | (designer) |
| `Radios/TXControls.resx` | (resources) |
| `Radios/FlexInfo.cs` | varies |
| `Radios/FlexInfo.Designer.cs` | (designer) |
| `Radios/FlexInfo.resx` | (resources) |
| `LogPanel.vb` | varies |
| `RadioPane.vb` | varies |
| `RadioBoxes/*` (entire project) | ~2,000+ |

**Estimated total removal:** ~8,000-13,000 lines

## Files Modified

| File | Change |
|------|--------|
| `JJFlexRadio.sln` | Remove RadioBoxes project |
| `JJFlexRadio.vbproj` | Remove RadioBoxes reference, deleted file entries |
| `Radios/Radios.csproj` | Remove RadioBoxes reference, deleted file entries |
| `About.vb` | Remove RadioBoxes.MainBox.Version reference |
| `StatusBoxAdapter.vb` | Update comments |
| `deleteList.txt` | Add RadioBoxes.dll |
| `install.nsi` / template | Remove RadioBoxes references |
| `docs/CHANGELOG.md` | Add Sprint 11 entry |
| `Agent.md` | Update status |
