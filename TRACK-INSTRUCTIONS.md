# Sprint 21 — Track E: Compiled Help File

**Branch:** `sprint21/track-e`
**Worktree:** `C:\dev\jjflex-21e`

---

## Overview

Create a compiled HTML help file (CHM) with F1 context-sensitive wiring. Content written as Markdown, converted to HTML, compiled with `hhc.exe` (HTML Help Workshop). No HelpNDoc — its GUI is inaccessible with screen readers.

The keyboard reference page is the highest-value content for our users — write it first.

**Read the full sprint plan first:** `docs/planning/agile/sprint21-resonant-signal-sculpt.md`

---

## Build & Test

```batch
# Build app (from this worktree directory)
dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64

# Build CHM (requires HTML Help Workshop installed)
"%ProgramFiles(x86)%\HTML Help Workshop\hhc.exe" "docs\help\jjflex-help.hhp"
# NOTE: hhc.exe returns exit code 1 on SUCCESS (yes, really). Exit code 0 means no files compiled.

# Verify
dir docs\help\JJFlexRadio.chm
```

---

## Commit Strategy

Commit after each phase. Message format: `Sprint 21 Track E: <description>`

---

## Toolchain

1. **Write content** as Markdown in `docs/help/md/`
2. **Convert to HTML** using a build script (pandoc if available, or a simple PowerShell/Python script)
3. **Compile with hhc.exe** — already installed at `C:\Program Files (x86)\HTML Help Workshop\hhc.exe`
4. **Output:** `docs/help/JJFlexRadio.chm` — copied to build output, bundled in installer

**Why CHM:**
- Built into Windows — `hh.exe` on every machine
- NVDA and JAWS both navigate CHM (tree TOC, search, index)
- F1 context-sensitive help is one API call: `System.Windows.Forms.Help.ShowHelp(null, path, topic)`
- Single file to distribute
- Searchable and indexed out of the box

---

## Directory Structure to Create

```
docs/help/
├── md/                          # Markdown source files
│   ├── welcome.md
│   ├── getting-started.md
│   ├── screen-reader-setup.md
│   ├── keyboard-reference.md    # HIGHEST PRIORITY — write first
│   ├── operating-modes.md
│   ├── tuning-frequency.md
│   ├── band-navigation.md
│   ├── mode-switching.md
│   ├── filters-dsp.md
│   ├── ptt-transmission.md
│   ├── audio-workshop.md
│   ├── meter-tones.md
│   ├── earcon-explorer.md
│   ├── audio-presets.md
│   ├── tx-sculpting.md
│   ├── leader-key.md
│   ├── logging.md
│   ├── callbook-lookup.md
│   ├── settings-profiles.md
│   ├── connection-troubleshooting.md
│   ├── smartlink-remote.md
│   ├── audio-troubleshooting.md
│   └── known-issues.md
│
├── pages/                       # Generated HTML files (from Markdown)
│   ├── welcome.htm
│   ├── getting-started.htm
│   ├── ... (one .htm per .md)
│
├── jjflex-help.hhp             # HTML Help project file
├── toc.hhc                      # Table of contents (XML format)
├── index.hhk                    # Keyword index (XML format)
├── style.css                    # Shared stylesheet for help pages
├── build-help.bat               # Script to convert MD→HTML and compile CHM
└── JJFlexRadio.chm             # Compiled output (generated, not checked in)
```

---

## New Files to Create

### 1. HTML Help Project File: `docs/help/jjflex-help.hhp`

```ini
[OPTIONS]
Compatibility=1.1 or later
Compiled file=JJFlexRadio.chm
Contents file=toc.hhc
Default topic=pages\welcome.htm
Display compile progress=No
Full-text search=Yes
Index file=index.hhk
Language=0x409 English (United States)
Title=JJ Flexible Radio Access Help

[FILES]
pages\welcome.htm
pages\getting-started.htm
pages\screen-reader-setup.htm
pages\keyboard-reference.htm
pages\operating-modes.htm
pages\tuning-frequency.htm
pages\band-navigation.htm
pages\mode-switching.htm
pages\filters-dsp.htm
pages\ptt-transmission.htm
pages\audio-workshop.htm
pages\meter-tones.htm
pages\earcon-explorer.htm
pages\audio-presets.htm
pages\tx-sculpting.htm
pages\leader-key.htm
pages\logging.htm
pages\callbook-lookup.htm
pages\settings-profiles.htm
pages\connection-troubleshooting.htm
pages\smartlink-remote.htm
pages\audio-troubleshooting.htm
pages\known-issues.htm
```

### 2. Table of Contents: `docs/help/toc.hhc`

```html
<!DOCTYPE HTML PUBLIC "-//IETF//DTD HTML//EN">
<HTML>
<HEAD>
<meta name="GENERATOR" content="JJFlex Help Build">
</HEAD>
<BODY>
<OBJECT type="text/site properties">
  <param name="Auto Generated" value="No">
</OBJECT>
<UL>
  <LI><OBJECT type="text/sitemap">
    <param name="Name" value="Welcome">
    <param name="Local" value="pages\welcome.htm">
  </OBJECT>

  <LI><OBJECT type="text/sitemap">
    <param name="Name" value="Getting Started">
  </OBJECT>
  <UL>
    <LI><OBJECT type="text/sitemap">
      <param name="Name" value="Installation & First Connection">
      <param name="Local" value="pages\getting-started.htm">
    </OBJECT>
    <LI><OBJECT type="text/sitemap">
      <param name="Name" value="Screen Reader Setup">
      <param name="Local" value="pages\screen-reader-setup.htm">
    </OBJECT>
  </UL>

  <LI><OBJECT type="text/sitemap">
    <param name="Name" value="Keyboard Reference">
    <param name="Local" value="pages\keyboard-reference.htm">
  </OBJECT>

  <LI><OBJECT type="text/sitemap">
    <param name="Name" value="Operating Modes">
    <param name="Local" value="pages\operating-modes.htm">
  </OBJECT>

  <LI><OBJECT type="text/sitemap">
    <param name="Name" value="Radio Control">
  </OBJECT>
  <UL>
    <LI><OBJECT type="text/sitemap">
      <param name="Name" value="Tuning & Frequency">
      <param name="Local" value="pages\tuning-frequency.htm">
    </OBJECT>
    <LI><OBJECT type="text/sitemap">
      <param name="Name" value="Band Navigation">
      <param name="Local" value="pages\band-navigation.htm">
    </OBJECT>
    <LI><OBJECT type="text/sitemap">
      <param name="Name" value="Mode Switching">
      <param name="Local" value="pages\mode-switching.htm">
    </OBJECT>
    <LI><OBJECT type="text/sitemap">
      <param name="Name" value="Filters & DSP">
      <param name="Local" value="pages\filters-dsp.htm">
    </OBJECT>
  </UL>

  <LI><OBJECT type="text/sitemap">
    <param name="Name" value="Audio & Transmission">
  </OBJECT>
  <UL>
    <LI><OBJECT type="text/sitemap">
      <param name="Name" value="PTT & Transmission">
      <param name="Local" value="pages\ptt-transmission.htm">
    </OBJECT>
    <LI><OBJECT type="text/sitemap">
      <param name="Name" value="Audio Workshop">
      <param name="Local" value="pages\audio-workshop.htm">
    </OBJECT>
    <LI><OBJECT type="text/sitemap">
      <param name="Name" value="Meter Tones">
      <param name="Local" value="pages\meter-tones.htm">
    </OBJECT>
    <LI><OBJECT type="text/sitemap">
      <param name="Name" value="Earcon Explorer">
      <param name="Local" value="pages\earcon-explorer.htm">
    </OBJECT>
    <LI><OBJECT type="text/sitemap">
      <param name="Name" value="Audio Presets">
      <param name="Local" value="pages\audio-presets.htm">
    </OBJECT>
    <LI><OBJECT type="text/sitemap">
      <param name="Name" value="TX Bandwidth Sculpting">
      <param name="Local" value="pages\tx-sculpting.htm">
    </OBJECT>
    <LI><OBJECT type="text/sitemap">
      <param name="Name" value="Leader Key Commands">
      <param name="Local" value="pages\leader-key.htm">
    </OBJECT>
  </UL>

  <LI><OBJECT type="text/sitemap">
    <param name="Name" value="Logging">
  </OBJECT>
  <UL>
    <LI><OBJECT type="text/sitemap">
      <param name="Name" value="Quick Log & QSO Grid">
      <param name="Local" value="pages\logging.htm">
    </OBJECT>
    <LI><OBJECT type="text/sitemap">
      <param name="Name" value="Callbook Lookup">
      <param name="Local" value="pages\callbook-lookup.htm">
    </OBJECT>
  </UL>

  <LI><OBJECT type="text/sitemap">
    <param name="Name" value="Settings & Profiles">
    <param name="Local" value="pages\settings-profiles.htm">
  </OBJECT>

  <LI><OBJECT type="text/sitemap">
    <param name="Name" value="Troubleshooting">
  </OBJECT>
  <UL>
    <LI><OBJECT type="text/sitemap">
      <param name="Name" value="Connection Issues">
      <param name="Local" value="pages\connection-troubleshooting.htm">
    </OBJECT>
    <LI><OBJECT type="text/sitemap">
      <param name="Name" value="SmartLink & Remote">
      <param name="Local" value="pages\smartlink-remote.htm">
    </OBJECT>
    <LI><OBJECT type="text/sitemap">
      <param name="Name" value="Audio Problems">
      <param name="Local" value="pages\audio-troubleshooting.htm">
    </OBJECT>
    <LI><OBJECT type="text/sitemap">
      <param name="Name" value="Known Issues">
      <param name="Local" value="pages\known-issues.htm">
    </OBJECT>
  </UL>
</UL>
</BODY>
</HTML>
```

### 3. Index File: `docs/help/index.hhk`

```html
<!DOCTYPE HTML PUBLIC "-//IETF//DTD HTML//EN">
<HTML>
<HEAD>
<meta name="GENERATOR" content="JJFlex Help Build">
</HEAD>
<BODY>
<UL>
  <LI><OBJECT type="text/sitemap">
    <param name="Name" value="keyboard shortcuts">
    <param name="Local" value="pages\keyboard-reference.htm">
  </OBJECT>
  <LI><OBJECT type="text/sitemap">
    <param name="Name" value="hotkeys">
    <param name="Local" value="pages\keyboard-reference.htm">
  </OBJECT>
  <LI><OBJECT type="text/sitemap">
    <param name="Name" value="leader key">
    <param name="Local" value="pages\leader-key.htm">
  </OBJECT>
  <!-- Add more index entries as content is written -->
</UL>
</BODY>
</HTML>
```

### 4. Shared Stylesheet: `docs/help/style.css`

```css
body {
    font-family: Segoe UI, Tahoma, sans-serif;
    font-size: 14px;
    line-height: 1.6;
    max-width: 800px;
    margin: 0 auto;
    padding: 16px;
    color: #333;
}
h1 { font-size: 24px; border-bottom: 2px solid #0066cc; padding-bottom: 8px; }
h2 { font-size: 20px; margin-top: 24px; }
h3 { font-size: 16px; margin-top: 16px; }
table { border-collapse: collapse; width: 100%; margin: 12px 0; }
th, td { border: 1px solid #ccc; padding: 8px; text-align: left; }
th { background-color: #f0f0f0; }
code { background-color: #f5f5f5; padding: 2px 6px; border-radius: 3px; font-size: 13px; }
kbd { background-color: #eee; border: 1px solid #ccc; border-radius: 3px; padding: 2px 6px; font-size: 13px; }
.tip { background-color: #e8f5e9; border-left: 4px solid #4caf50; padding: 12px; margin: 12px 0; }
.warning { background-color: #fff3e0; border-left: 4px solid #ff9800; padding: 12px; margin: 12px 0; }
```

### 5. Build Script: `docs/help/build-help.bat`

```batch
@echo off
REM Build JJFlex help file
REM Converts Markdown to HTML, then compiles CHM

setlocal

set "HELPDIR=%~dp0"
set "MDDIR=%HELPDIR%md"
set "PAGESDIR=%HELPDIR%pages"
set "HHC=%ProgramFiles(x86)%\HTML Help Workshop\hhc.exe"

REM Check for hhc.exe
if not exist "%HHC%" (
    echo ERROR: HTML Help Workshop not found at %HHC%
    echo Install from: https://web.archive.org/web/2024/https://www.microsoft.com/en-us/download/details.aspx?id=21138
    exit /b 1
)

REM Create pages directory
if not exist "%PAGESDIR%" mkdir "%PAGESDIR%"

REM Convert Markdown to HTML
REM Option 1: Use pandoc if available
where pandoc >nul 2>&1
if %ERRORLEVEL% equ 0 (
    echo Using pandoc for Markdown conversion...
    for %%f in ("%MDDIR%\*.md") do (
        echo   Converting %%~nf.md
        pandoc "%%f" -f markdown -t html --standalone --css=../style.css -o "%PAGESDIR%\%%~nf.htm" --metadata title="%%~nf"
    )
) else (
    echo Pandoc not found. Using simple conversion...
    REM Fallback: PowerShell-based simple Markdown to HTML
    powershell -ExecutionPolicy Bypass -Command ^
        "Get-ChildItem '%MDDIR%\*.md' | ForEach-Object { " ^
        "  $name = $_.BaseName; " ^
        "  $content = Get-Content $_.FullName -Raw; " ^
        "  $html = '<html><head><title>' + $name + '</title><link rel=\"stylesheet\" href=\"../style.css\"></head><body>' + $content + '</body></html>'; " ^
        "  $html | Out-File -Encoding utf8 '%PAGESDIR%\' + $name + '.htm'; " ^
        "  Write-Host '  Converting' $name '.md' " ^
        "}"
    REM Note: Simple fallback doesn't convert Markdown formatting. Install pandoc for proper conversion.
    echo WARNING: Simple conversion does not process Markdown formatting. Install pandoc for best results.
)

REM Compile CHM
echo.
echo Compiling CHM...
"%HHC%" "%HELPDIR%jjflex-help.hhp"

REM hhc.exe returns 1 on success, 0 on failure (yes, really)
if exist "%HELPDIR%JJFlexRadio.chm" (
    echo.
    echo SUCCESS: JJFlexRadio.chm built successfully.
    dir "%HELPDIR%JJFlexRadio.chm"
) else (
    echo.
    echo FAILED: CHM compilation failed.
    exit /b 1
)
```

### 6. `JJFlexWpf/HelpLauncher.cs`

F1 context-sensitive help dispatcher.

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;  // For Help.ShowHelp

namespace JJFlexWpf
{
    public static class HelpLauncher
    {
        private static string _helpFilePath;

        private static readonly Dictionary<string, string> ContextMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "FreqDisplay", "pages/tuning-frequency.htm" },
            { "ScreenFieldsDSP", "pages/filters-dsp.htm" },
            { "ScreenFieldsTX", "pages/ptt-transmission.htm" },
            { "ScreenFieldsAudio", "pages/audio-workshop.htm" },
            { "ScreenFieldsReceiver", "pages/filters-dsp.htm" },
            { "ScreenFieldsAntenna", "pages/filters-dsp.htm" },
            { "AudioWorkshop", "pages/audio-workshop.htm" },
            { "EarconExplorer", "pages/earcon-explorer.htm" },
            { "MeterTones", "pages/meter-tones.htm" },
            { "LogPanel", "pages/logging.htm" },
            { "SettingsDialog", "pages/settings-profiles.htm" },
            { "CommandFinder", "pages/keyboard-reference.htm" },
            { "LeaderKey", "pages/leader-key.htm" },
            { "WelcomeDialog", "pages/getting-started.htm" },
            { "ConnectDialog", "pages/connection-troubleshooting.htm" },
        };

        public static void Initialize(string appDirectory)
        {
            _helpFilePath = Path.Combine(appDirectory, "JJFlexRadio.chm");
        }

        public static void ShowHelp(string context = null)
        {
            if (string.IsNullOrEmpty(_helpFilePath) || !File.Exists(_helpFilePath))
            {
                Radios.ScreenReaderOutput.Speak("Help file not found.");
                return;
            }

            string topic = null;
            if (context != null && ContextMap.TryGetValue(context, out var page))
                topic = page;

            try
            {
                if (topic != null)
                    System.Windows.Forms.Help.ShowHelp(null, _helpFilePath, HelpNavigator.Topic, topic);
                else
                    System.Windows.Forms.Help.ShowHelp(null, _helpFilePath);
            }
            catch (Exception ex)
            {
                Radios.ScreenReaderOutput.Speak("Could not open help file.");
                System.Diagnostics.Trace.WriteLine($"HelpLauncher error: {ex.Message}");
            }
        }
    }
}
```

---

## Files to Modify

### 7. `KeyCommands.vb`

**Add F1 handler:**
```vb
' Add new CommandValues:
'   ShowContextHelp

' Add to KeyTable:
lookup(CommandValues.ShowContextHelp, Keys.F1, KeyTypes.Command,
    AddressOf ShowContextHelpHandler, "Open context-sensitive help",
    FunctionGroups.general, KeyScope.Global)

Private Sub ShowContextHelpHandler()
    ' Determine current context from focused control
    Dim context As String = GetCurrentHelpContext()
    HelpLauncher.ShowHelp(context)
End Sub

Private Function GetCurrentHelpContext() As String
    ' Check what's focused and return the appropriate context string
    ' This will need to inspect the active WPF control/dialog
    ' Return Nothing for default (welcome page)
    Return Nothing  ' Start simple, refine as contexts are mapped
End Function
```

**Note:** Check if F1 is already bound to something. The existing `CommandValues.ContextHelp` (Ctrl+/) opens the Command Finder. F1 should open CHM help — these are separate actions.

### 8. `JJFlexWpf/NativeMenuBar.cs`

**Add Help menu items:**
```csharp
AddWired(helpPopup, "Help Topics\tF1", () => HelpLauncher.ShowHelp());
AddWired(helpPopup, "Keyboard Reference", () => HelpLauncher.ShowHelp("CommandFinder"));
```

### 9. `JJFlexRadio.vbproj`

**Include CHM in build output:**
```xml
<ItemGroup>
    <None Include="docs\help\JJFlexRadio.chm" CopyToOutputDirectory="PreserveNewest" Link="JJFlexRadio.chm" Condition="Exists('docs\help\JJFlexRadio.chm')" />
</ItemGroup>
```

The `Condition` ensures the build doesn't fail if the CHM hasn't been compiled yet.

### 10. `install template.nsi`

**Include CHM in installer:**
```nsi
; In the Files section:
File "JJFlexRadio.chm"

; In the Uninstall section:
Delete "$INSTDIR\JJFlexRadio.chm"
```

### 11. `ApplicationEvents.vb`

**Initialize HelpLauncher at startup:**
```vb
' After EarconPlayer.Initialize()
HelpLauncher.Initialize(Application.StartupPath)
```

---

## Markdown Content Writing Guidelines

**Audience:** Blind ham radio operators using NVDA or JAWS screen readers. Write conversationally in first person ("You can..." / "Press Ctrl+J to..."). Same warm tone as the changelog.

**Format rules:**
- Use headings (##, ###) for navigation — screen readers use H key to jump between headings
- Use tables for keyboard reference (screen readers read tables well when structured correctly)
- Keep paragraphs short (3-4 sentences max)
- Use `kbd` tags for keys in HTML: `<kbd>Ctrl+J</kbd>` then `<kbd>N</kbd>`
- Don't use images — they add nothing for our users

**Keyboard reference page structure (highest priority):**

```markdown
# Keyboard Reference

## Global Hotkeys (work everywhere)

| Key | Action |
|-----|--------|
| F1 | Open help |
| Ctrl+/ | Command Finder |
| Ctrl+J | Leader key (then press second key) |
| Ctrl+Shift+1-5 | Jump to ScreenFields category |
| Escape | Cancel / close dialog |

## Leader Key Commands (Ctrl+J, then...)

| Key | Action |
|-----|--------|
| N | Toggle Noise Reduction |
| B | Toggle Noise Blanker |
| ... etc |

## Modern Mode

| Key | Action |
|-----|--------|
| Up/Down | Tune frequency |
| ... etc |

## Classic Mode
... etc
```

**Populate keyboard tables from `KeyCommands.vb`** — read the KeyTable to get accurate bindings.

---

## Phase Order

1. **Create directory structure** — `docs/help/md/`, `docs/help/pages/`
2. **Write keyboard reference** (highest value for screen reader users) — read KeyCommands.vb for accurate bindings
3. **Write getting started / first connection guide**
4. **Write core feature documentation** (tuning, filters, PTT, modes)
5. **Create HHP, HHC, HHK files** + stylesheet + build script
6. **Test CHM compilation** — verify hhc.exe produces valid CHM
7. **HelpLauncher.cs** — F1 dispatcher with context mapping
8. **Wire F1** in KeyCommands.vb + Help menu in NativeMenuBar
9. **Integrate CHM** into build output (.vbproj) and installer (.nsi)
10. **Write Sprint 21 feature docs** (audio workshop, meter tones, leader keys, earcon explorer, TX sculpting)
11. **Test with NVDA + JAWS** — navigate TOC, search, F1 from various contexts

Build and commit after each phase or logical group.

---

## Gotchas

1. **hhc.exe returns 1 on success.** Don't check for exit code 0 — that means failure. Check for the existence of the output CHM file instead.
2. **CHM security zone.** Windows may block CHM files downloaded from the internet. Since we compile locally and include in the installer, this shouldn't be an issue. But if testing a CHM from a network path, right-click → Properties → Unblock.
3. **pandoc may not be installed.** The build script has a fallback, but pandoc produces much better HTML. Install via `winget install pandoc` or `choco install pandoc`.
4. **HTML paths use backslashes** in .hhp and .hhc files (Windows convention for CHM).
5. **Topic paths in HelpLauncher** use forward slashes — `Help.ShowHelp()` handles conversion.
6. **Don't check in the compiled CHM.** It's a binary that changes every build. Add `docs/help/JJFlexRadio.chm` to `.gitignore`.
7. **F1 key conflict.** Check if F1 is already bound in KeyCommands.vb. If it's bound to something else, we may need to move that binding.
