# Sprint 21 — Track D: App Rename — JJ Flexible Radio Access

**Branch:** `sprint21/track-d`
**Worktree:** `C:\dev\jjflex-21d`

---

## Overview

Display-name rebrand from "JJFlexRadio" to "JJ Flexible Radio Access". All user-facing text changes. Filenames, internal identifiers, AppData paths, and registry keys stay as `JJFlexRadio` — no migration needed.

**This track merges LAST** to minimize conflicts with feature tracks.

**Read the full sprint plan first:** `docs/planning/agile/sprint21-resonant-signal-sculpt.md`

---

## Build & Test

```batch
# Build (from this worktree directory)
dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64

# Verify timestamp
powershell -Command "(Get-Item 'bin\x64\Debug\net8.0-windows\win-x64\JJFlexRadio.exe').LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')"
```

**IMPORTANT:** Close any running JJFlexRadio before building (Radios.dll lock).

---

## Commit Strategy

Commit after each phase. Message format: `Sprint 21 Track D: <description>`

---

## The Rename Rules

| Changes (user-facing display) | Stays the same (internal/files) |
|---|---|
| Window titles → "JJ Flexible Radio Access" | Assembly name: `JJFlexRadio` |
| Installer welcome text | Exe file: `JJFlexRadio.exe` |
| Installer filename → `Setup JJFlex_4.x.x_x64.exe` | AppData folder: `JJFlexRadio` |
| Welcome speech | Registry keys: `JJFlexRadio` |
| About dialog | Class/namespace names |
| NSIS display name | Solution/project file names |
| Documentation headers | .sln, .vbproj file names |
| Screen reader automation names | |
| QRZ User-Agent → `JJFlexibleRadioAccess` | |

**The short form "JJ Flex" is acceptable in speech** where brevity matters (e.g., "Welcome to JJ Flex" is fine, but window titles should use the full name).

---

## Files to Modify (18 files)

### Phase 1: Core Constants

#### 1. `globals.vb` (line 197)
```vb
' CHANGE FROM:
Friend Const ProgramName = "JJFlexRadio"
' CHANGE TO:
Friend Const ProgramName = "JJ Flexible Radio Access"
```

**CRITICAL:** `ProgramName` cascades to:
- `DocName` (line 198): `ProgramName & "Readme.htm"` — this will break! The filename is `JJFlexRadioReadme.htm`. You need to keep the old filename or rename the file.
  - **Solution:** Change `DocName` to keep using the old filename: `Friend Const DocName = "JJFlexRadioReadme.htm"` (hardcode it instead of deriving from ProgramName)
- Config directory paths (lines 212, 221): Uses `ProgramName` for AppData folder name
  - **Solution:** Add a separate constant for the internal folder name: `Friend Const InternalName = "JJFlexRadio"` and use that for paths
- Trace log names (line 235): Uses `ProgramName` prefix
  - **Solution:** Use `InternalName` for trace filenames too

**Pattern:** Add `Friend Const InternalName = "JJFlexRadio"` and use it wherever file paths, AppData dirs, and registry keys reference the old name. Use `ProgramName` only for display purposes.

#### 2. `My Project\AssemblyInfo.vb`
```vb
' CHANGE FROM:
<Assembly: AssemblyTitle("JJFlexRadio")>
<Assembly: AssemblyProduct("JJFlexRadio")>
' CHANGE TO:
<Assembly: AssemblyTitle("JJ Flexible Radio Access")>
<Assembly: AssemblyProduct("JJ Flexible Radio Access")>
```
**Keep `AssemblyName` as `JJFlexRadio`** (the compiled exe name).

#### 3. `JJFlexRadio.vbproj`
```xml
<!-- DO NOT CHANGE AssemblyName (line 15) -->
<AssemblyName>JJFlexRadio</AssemblyName>

<!-- CHANGE Product display name (add if not present): -->
<Product>JJ Flexible Radio Access</Product>
```

---

### Phase 2: WPF/WinForms Window Titles

#### 4. `JJFlexWpf/MainWindow.xaml` (line 9)
```xml
<!-- CHANGE FROM: -->
AutomationProperties.Name="JJFlexRadio Main Window"
<!-- CHANGE TO: -->
AutomationProperties.Name="JJ Flexible Radio Access Main Window"
```

#### 5. `JJFlexWpf/MainWindow.xaml.cs` (line ~110)
```csharp
// CHANGE FROM:
Radios.ScreenReaderOutput.Speak($"Welcome to JJ Flex, {modeName} mode");
// CHANGE TO:
Radios.ScreenReaderOutput.Speak($"Welcome to JJ Flexible Radio Access, {modeName} mode");
```
Also update the window Title property if set in code.

#### 6. `JJFlexWpf/Dialogs/WelcomeDialog.xaml`
```xml
<!-- Line 5 CHANGE: -->
Title="Welcome to JJ Flexible Radio Access"
<!-- Line 8 CHANGE: -->
AutomationProperties.Name="Welcome to JJ Flexible Radio Access"
<!-- Line 25 CHANGE: -->
AutomationProperties.Name="Quit JJ Flexible Radio Access"
```

#### 7. `BridgeForm.vb` (line 30)
```vb
' CHANGE FROM:
Me.Text = "JJFlexRadio"
' CHANGE TO:
Me.Text = "JJ Flexible Radio Access"
```

---

### Phase 3: Application Events & Speech

#### 8. `ApplicationEvents.vb`
Search for any welcome speech or app name references. Update to use the new display name.

---

### Phase 4: Installer & Build Scripts

#### 9. `install template.nsi`
The NSIS template uses `MYPGM` placeholder which is replaced by `install.bat`. The key changes:

- Line 42 welcome message: Update the descriptive text
- Any hardcoded display name references (search for "JJFlexRadio" in the template)
- The `Name` directive (line 11) uses `MYPGM` — this is replaced at build time

**The NSIS display name** (shown in Add/Remove Programs) should be "JJ Flexible Radio Access".

#### 10. `install.bat`
Find all references to `JJFlexRadio` and determine which are:
- **Display names** (change to new name)
- **File paths/exe names** (keep as `JJFlexRadio`)

The installer output filename should change to `Setup JJFlex_VERSION_ARCH.exe` format (shorter, cleaner).

#### 11. `build-installers.bat`
Same as install.bat — update display references, keep file path references.

---

### Phase 5: Other Code References

#### 12. `JJFlexWpf/NativeMenuBar.cs`
Find the About dialog handler. Update the product name displayed:
```csharp
// Search for "About" or ProductName references
// Change display text to "JJ Flexible Radio Access"
```

#### 13. `QrzLookup/QrzCallbookLookup.cs` (line 158)
```csharp
// CHANGE FROM:
private const string AgentName = "JJFlexRadio";
// CHANGE TO:
private const string AgentName = "JJFlexibleRadioAccess";
```
No spaces in User-Agent — it's a technical identifier.

#### 14. `CrashReporter.vb`
Multiple references (lines 26, 46, 47, 48, 52, 61):
```vb
' Update display strings to "JJ Flexible Radio Access"
' BUT keep file paths using the internal name (JJFlexRadio)
' Line 26: Path.Combine(..., "JJFlexRadio", "Errors") — KEEP (AppData path)
' Line 46-48: Dialog text — CHANGE to new name
' Line 52: Filename "JJFlexRadio-crash.txt" — KEEP (internal file)
' Line 61: Report header — CHANGE to new name
```

---

### Phase 6: Documentation

#### 15. `README.md`
Update title and description to use "JJ Flexible Radio Access".

#### 16. `CLAUDE.md`
Update the header: `# JJ Flexible Radio Access` (was `# JJFlexRadio`).
Update references throughout. Keep build commands and file paths as-is.

#### 17. `docs/CHANGELOG.md`
- Update the header to use the new name
- Add a note in the 4.1.16 section about the rename:
  > "I've renamed the app to JJ Flexible Radio Access. The name reflects where we're headed — flexible radio control that puts accessibility first. Your settings, profiles, and everything else are exactly where you left them. No migration needed."

#### 18. `JJFlexRadioReadme.htm`
55+ occurrences of "JJFlexRadio". Global search and replace:
- `JJFlexRadio` → `JJ Flexible Radio Access` (in display text)
- Keep any filename references as-is (the .htm file itself keeps its old name)

---

## Phase Order

1. **globals.vb** — Add `InternalName` constant, change `ProgramName`, fix `DocName` and path derivations
2. **AssemblyInfo.vb + .vbproj** — Update product display name
3. **WPF/WinForms window titles** — MainWindow, WelcomeDialog, BridgeForm automation names
4. **Installer scripts** — install template, install.bat, build-installers.bat
5. **Other code** — NativeMenuBar about, QRZ User-Agent, CrashReporter
6. **Documentation** — README, CLAUDE.md, CHANGELOG, JJFlexRadioReadme.htm
7. **Build and verify** — x64 Debug build, check window title, welcome speech
8. **Release build test** — Clean build x64 Release, verify installer name and display name

---

## Testing Checklist

After all changes:
- [ ] Window title says "JJ Flexible Radio Access"
- [ ] Welcome speech says "Welcome to JJ Flexible Radio Access"
- [ ] About dialog shows "JJ Flexible Radio Access"
- [ ] WelcomeDialog title and Quit button use new name
- [ ] Crash dialog (if triggered) shows new name
- [ ] AppData folder is still `%AppData%\JJFlexRadio` (unchanged)
- [ ] Config files load from old location (no migration)
- [ ] Trace files use old naming (no confusion)
- [ ] Installer display name is correct
- [ ] Add/Remove Programs shows "JJ Flexible Radio Access"
- [ ] NVDA reads "JJ Flexible Radio Access Main Window" as automation name
- [ ] Screen reader announces new name in welcome speech
- [ ] JJFlexRadioReadme.htm opens and shows new name in content

---

## Gotchas

1. **ProgramName cascades.** Don't just change the constant — trace every usage. Many places derive paths from it. Add `InternalName` for file/path purposes.
2. **DocName derivation.** Currently `ProgramName & "Readme.htm"`. If ProgramName changes, DocName becomes "JJ Flexible Radio AccessReadme.htm" which is wrong. Hardcode it.
3. **Installer filename vs display name.** The .exe filename should be short (`Setup JJFlex_4.1.16_x64.exe`), but the NSIS display name (visible in wizard) should be the full name.
4. **No spaces in QRZ User-Agent.** Use `JJFlexibleRadioAccess` (no spaces, camelCase-ish).
5. **CrashReporter paths vs display.** Keep AppData paths as `JJFlexRadio`, only change user-facing dialog text.
6. **This track merges LAST.** Other tracks may add menu items, dialog titles, etc. that still say "JJFlexRadio". When merging, search for any remaining old-name references in files touched by other tracks.
