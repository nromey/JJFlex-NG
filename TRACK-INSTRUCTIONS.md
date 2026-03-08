# Sprint 21 — Track C: TX Sculpting Keyboard + Leader Key System + Command Finder Extension

**Branch:** `sprint21/track-c`
**Worktree:** `C:\dev\jjflex-21c`

---

## Overview

Build the leader key system (Ctrl+J → second key), TX filter keyboard sculpting (bracket keys with Ctrl+Shift/Ctrl+Alt modifiers), add leader key commands to the existing Command Finder, and fix BUG-004. This track is all about keyboard efficiency — giving blind operators fast, two-keystroke access to every DSP toggle and audio shortcut.

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

Commit after each phase. Message format: `Sprint 21 Track C: <description>`

---

## No New Files

All changes are in existing files. The Command Finder already exists and works — we're extending it.

---

## Files to Modify

### 1. `JJFlexWpf/EarconPlayer.cs` (678 lines)

**Add leader key earcon methods.** These are named methods that play specific tone patterns:

```csharp
/// <summary>Rising "bink" — entering leader mode</summary>
public static void LeaderEnterTone()
{
    PlayChirp(400, 600, 80, 0.3f);  // Rising chirp, 80ms
}

/// <summary>Rising sequence — feature toggled ON</summary>
public static void FeatureOnTone()
{
    // Three-step rising: 300→500→800 Hz, 50ms each
    PlayTone(300, 50, 0.25f);
    // Schedule next tones with slight delay
    // Or use a compound chirp: PlayChirp(300, 800, 150, 0.3f)
}

/// <summary>Falling sequence — feature toggled OFF</summary>
public static void FeatureOffTone()
{
    // Three-step falling: 800→500→300 Hz, 50ms each
    PlayChirp(800, 300, 150, 0.3f);  // Falling chirp
}

/// <summary>Dull buzz — invalid leader key</summary>
public static void LeaderInvalidTone()
{
    // 200 Hz square-ish wave, 100ms
    // Use a low-frequency tone with slightly higher volume for "buzz" feel
    PlayTone(200, 100, 0.4f);
}

/// <summary>Soft descending — leader key cancelled</summary>
public static void LeaderCancelTone()
{
    PlayChirp(500, 300, 150, 0.2f);
}

/// <summary>Double chime — help requested</summary>
public static void LeaderHelpTone()
{
    PlayTone(800, 80, 0.25f);
    // Brief gap then second tone
    PlayTone(1000, 80, 0.25f);
}
```

**Implementation note:** For multi-step tones (FeatureOnTone with 3 steps), you have two options:
1. **Simple:** Use a single chirp that sweeps the range (sounds similar enough)
2. **Precise:** Create a compound sample provider that plays discrete steps (more work but crisper)

Start with option 1 (chirps) and refine if the user wants discrete steps.

**Existing methods to reference:**
- `PlayTone(int freq, int durationMs, float volume)` — single sine wave
- `PlayChirp(int startHz, int endHz, int durationMs, float volume)` — frequency sweep
- `FilterEdgeMoveTone()`, `FilterEdgeEnterTone()` — existing filter feedback tones (reuse for TX filter)

### 2. `KeyCommands.vb` (~700+ lines)

This is the **primary file** for this track. Three major additions:

#### A. Leader Key State Machine

Add at the module/class level:
```vb
Private leaderKeyActive As Boolean = False
' NO TIMEOUT — cancel with Escape only (matches JAWS layered keystroke behavior)
```

**Modify `DoCommand()` to intercept leader key:**
```vb
Function DoCommand(ByVal k As Keys) As Boolean
    ' === LEADER KEY DISPATCH ===
    If leaderKeyActive Then
        leaderKeyActive = False

        If k = Keys.Escape Then
            EarconPlayer.LeaderCancelTone()
            Radios.ScreenReaderOutput.Speak("Cancelled", True)
            Return True
        End If

        Return DoLeaderCommand(k)
    End If

    ' Check for leader key trigger (Ctrl+J)
    If k = (Keys.J Or Keys.Control) Then
        leaderKeyActive = True
        EarconPlayer.LeaderEnterTone()
        Radios.ScreenReaderOutput.Speak("J", True)  ' Short announcement
        Return True
    End If

    ' === NORMAL DISPATCH (existing code) ===
    ' ... existing DoCommand logic ...
End Function
```

**No timeout.** The leader key stays active until the user presses a second key or Escape. This matches JAWS layered keystroke behavior where you press the modifier, then the next key at your own pace.

#### B. Leader Key Dictionary and Dispatch

```vb
Private Function DoLeaderCommand(ByVal k As Keys) As Boolean
    Select Case k
        ' DSP Toggles
        Case Keys.N
            ToggleLeaderDSP("Noise Reduction",
                Function() Rig.NR, Sub(v) Rig.NR = v)
        Case Keys.B
            ToggleLeaderDSP("Noise Blanker",
                Function() Rig.NB, Sub(v) Rig.NB = v)
        Case Keys.W
            ToggleLeaderDSP("Wideband NB",
                Function() Rig.WNB, Sub(v) Rig.WNB = v)
        Case Keys.R
            ToggleLeaderDSP("Neural NR",
                Function() Rig.NeuralNoiseReduction, Sub(v) Rig.NeuralNoiseReduction = v)
        Case Keys.S
            ToggleLeaderDSP("Spectral NR",
                Function() Rig.SpectralNoiseReduction, Sub(v) Rig.SpectralNoiseReduction = v)
        Case Keys.A
            ToggleLeaderDSP("Auto Notch",
                Function() Rig.ANF, Sub(v) Rig.ANF = v)
        Case Keys.P
            ToggleLeaderDSP("Audio Peak Filter",
                Function() Rig.APF, Sub(v) Rig.APF = v)

        ' Meter/Audio
        Case Keys.M
            ToggleLeaderBool("Meter Tones",
                Function() MeterToneEngine.Enabled, Sub(v) MeterToneEngine.Enabled = v)
        Case Keys.T
            ' Open Audio Workshop
            EarconPlayer.Beep()
            WpfMainWindow.Dispatcher.Invoke(Sub() AudioWorkshopDialog.ShowOrFocus(0))
        Case Keys.E
            ' Open Earcon Explorer (Audio Workshop tab 3)
            EarconPlayer.Beep()
            WpfMainWindow.Dispatcher.Invoke(Sub() AudioWorkshopDialog.ShowOrFocus(2))
        Case Keys.F
            ' Speak TX filter width
            SpeakTXFilterWidth()

        ' Help
        Case Keys.Oem2  ' ? key (Shift+/)
            LeaderKeyHelp()
        Case Keys.H
            LeaderKeyHelp()

        Case Else
            EarconPlayer.LeaderInvalidTone()
            Radios.ScreenReaderOutput.Speak("Unknown command. Press H for help.", True)
    End Select
    Return True  ' Always consume the key in leader mode
End Function

Private Sub ToggleLeaderDSP(label As String, getter As Func(Of FlexBase.OffOnValues), setter As Action(Of FlexBase.OffOnValues))
    If Rig Is Nothing Then
        EarconPlayer.LeaderInvalidTone()
        Radios.ScreenReaderOutput.Speak("No radio connected")
        Return
    End If
    Dim current = getter()
    Dim newVal = Rig.ToggleOffOn(current)
    setter(newVal)
    If newVal = FlexBase.OffOnValues.on Then
        EarconPlayer.FeatureOnTone()
        Radios.ScreenReaderOutput.Speak(label & " on")
    Else
        EarconPlayer.FeatureOffTone()
        Radios.ScreenReaderOutput.Speak(label & " off")
    End If
End Sub

Private Sub ToggleLeaderBool(label As String, getter As Func(Of Boolean), setter As Action(Of Boolean))
    Dim current = getter()
    setter(Not current)
    If Not current Then
        EarconPlayer.FeatureOnTone()
        Radios.ScreenReaderOutput.Speak(label & " on")
    Else
        EarconPlayer.FeatureOffTone()
        Radios.ScreenReaderOutput.Speak(label & " off")
    End If
End Sub

Private Sub SpeakTXFilterWidth()
    If Rig Is Nothing Then
        Radios.ScreenReaderOutput.Speak("No radio connected")
        Return
    End If
    Dim low = Rig.TXFilterLow
    Dim high = Rig.TXFilterHigh
    Dim width = high - low
    Dim widthKHz = (width / 1000.0).ToString("F1")
    Radios.ScreenReaderOutput.Speak($"TX filter {low} to {high}, {widthKHz} kilohertz")
End Sub

Private Sub LeaderKeyHelp()
    EarconPlayer.LeaderHelpTone()
    Dim help = "Leader key commands: " &
        "N noise reduction, B noise blanker, W wideband NB, " &
        "R neural NR, S spectral NR, A auto notch, P audio peak filter, " &
        "M meter tones, T audio workshop, E earcon explorer, " &
        "F speak TX filter. Escape to cancel."
    Radios.ScreenReaderOutput.Speak(help)
End Sub
```

**IMPORTANT:** The DSP property names (NR, NB, WNB, NeuralNoiseReduction, SpectralNoiseReduction, ANF, APF) — verify the exact FlexBase property names before coding. Read FlexBase.cs to confirm. The pattern uses `FlexBase.OffOnValues` for all DSP toggles.

**Feature gating (BUG-016 partial):** In each DSP toggle, check if the feature is available:
```vb
Case Keys.R  ' Neural NR
    If Rig IsNot Nothing AndAlso Not Rig.NeuralNRAvailable Then
        EarconPlayer.LeaderInvalidTone()
        Radios.ScreenReaderOutput.Speak("Neural NR not available on this radio")
        Return True
    End If
    ToggleLeaderDSP("Neural NR", ...)
```

#### C. TX Filter Keyboard Shortcuts

**Add new CommandValues:**
- `TXFilterLowDown`
- `TXFilterLowUp`
- `TXFilterHighDown`
- `TXFilterHighUp`
- `SpeakTXFilter`

**Add to KeyTable:**
```vb
' TX Filter sculpting
lookup(CommandValues.TXFilterLowDown, Keys.OemOpenBrackets Or Keys.Control Or Keys.Shift,
    KeyTypes.Command, AddressOf TXFilterLowDownHandler, "Nudge TX filter low edge down",
    FunctionGroups.audio, KeyScope.Radio)

lookup(CommandValues.TXFilterLowUp, Keys.OemCloseBrackets Or Keys.Control Or Keys.Shift,
    KeyTypes.Command, AddressOf TXFilterLowUpHandler, "Nudge TX filter low edge up",
    FunctionGroups.audio, KeyScope.Radio)

lookup(CommandValues.TXFilterHighDown, Keys.OemOpenBrackets Or Keys.Control Or Keys.Alt,
    KeyTypes.Command, AddressOf TXFilterHighDownHandler, "Nudge TX filter high edge down",
    FunctionGroups.audio, KeyScope.Radio)

lookup(CommandValues.TXFilterHighUp, Keys.OemCloseBrackets Or Keys.Control Or Keys.Alt,
    KeyTypes.Command, AddressOf TXFilterHighUpHandler, "Nudge TX filter high edge up",
    FunctionGroups.audio, KeyScope.Radio)

lookup(CommandValues.SpeakTXFilter, Keys.F Or Keys.Control Or Keys.Shift,
    KeyTypes.Command, AddressOf SpeakTXFilterHandler, "Speak TX filter width",
    FunctionGroups.audio, KeyScope.Radio)
```

**Handlers (can be in KeyCommands.vb or delegated to FreqOutHandlers.cs):**
```vb
Private Sub TXFilterLowDownHandler()
    If Rig Is Nothing Then Return
    Dim newLow = Math.Max(0, Rig.TXFilterLow - 50)
    Rig.TXFilterLow = newLow
    EarconPlayer.FilterEdgeMoveTone()  ' Reuse existing filter edge tone
    Radios.ScreenReaderOutput.Speak("TX low " & newLow.ToString())
End Sub
' ... similar for other directions
```

**Boundary checks:** Ensure TXFilterLow never crosses TXFilterHigh (maintain 50 Hz minimum spacing). FlexBase already has dynamic bounds: `TXFilterLowMax = TXFilterHigh - 50` and `TXFilterHighMin = TXFilterLow + 50`.

### 3. `JJFlexWpf/FreqOutHandlers.cs` (1806 lines)

**Optional:** If TX filter handlers are complex enough, add them here instead of KeyCommands.vb. Follow the RX filter bracket pattern:

The existing RX filter adjustment uses double-tap detection, edge mode, squeeze/pull. TX filter is simpler — just straight nudge with no edge mode (keep it simple for v1).

If you do add handlers here, create methods like:
```csharp
public void NudgeTXFilterLow(int step)  // +50 or -50
public void NudgeTXFilterHigh(int step)
```

### 4. `ApplicationEvents.vb` (~200 lines)

**Extend `GetCommandFinderItemsCallback` to include leader key commands:**

Find the existing callback (around line 124+). It returns a `List(Of CommandFinderItem)`. Add leader key entries:

```vb
' In GetCommandFinderItemsCallback, after existing command population:

' Leader key commands
items.Add(New CommandFinderItem() With {
    .Description = "Toggle Noise Reduction",
    .KeyDisplay = "Ctrl+J, N",
    .Scope = "Radio",
    .Group = "DSP",
    .Keywords = New String() {"NR", "noise", "reduction", "leader"},
    .Tag = CommandValues.LeaderNR  ' Or a delegate
})

items.Add(New CommandFinderItem() With {
    .Description = "Toggle Noise Blanker",
    .KeyDisplay = "Ctrl+J, B",
    .Scope = "Radio",
    .Group = "DSP",
    .Keywords = New String() {"NB", "noise", "blanker", "leader"},
    .Tag = CommandValues.LeaderNB
})

' ... repeat for all leader key bindings:
' W=Wideband NB, R=Neural NR, S=Spectral NR, A=Auto Notch, P=APF
' M=Meter Tones, T=Audio Workshop, E=Earcon Explorer, F=TX Filter

items.Add(New CommandFinderItem() With {
    .Description = "Speak TX Filter Width",
    .KeyDisplay = "Ctrl+J, F",
    .Scope = "Radio",
    .Group = "audio",
    .Keywords = New String() {"TX", "filter", "bandwidth", "width", "sculpt"},
    .Tag = CommandValues.SpeakTXFilter
})

items.Add(New CommandFinderItem() With {
    .Description = "Leader Key Help",
    .KeyDisplay = "Ctrl+J, H",
    .Scope = "Global",
    .Group = "general",
    .Keywords = New String() {"leader", "help", "commands", "list"},
    .Tag = CommandValues.LeaderHelp
})
```

**Tag handling:** The `ExecuteCommand` callback receives the Tag. If you use CommandValues as tags, the execution handler calls `Commands.DoCommand()` with that command. Alternatively, use Action delegates as tags and invoke them directly. Check how the existing Command Finder executes commands to match the pattern.

### 5. `JJFlexWpf/NativeMenuBar.cs` (1455 lines)

**Show leader key bindings in menu text** for DSP toggles that have leader key equivalents:

Find existing DSP toggle menu items and append leader key hints to their labels:
```csharp
// Change from:
AddChecked(dspPopup, "Noise Reduction", ...);
// To:
AddChecked(dspPopup, "Noise Reduction\tCtrl+J, N", ...);
```

Do this for: NR (N), NB (B), WNB (W), Neural NR (R), Spectral NR (S), ANF (A), APF (P).

**Also add Command Finder to Help menu** if not already there:
```csharp
AddWired(helpPopup, "Command Finder\tCtrl+/", () =>
{
    // Open command finder dialog
});
```

### 6. `JJFlexWpf/MainWindow.xaml.cs`

**Minimal change:** Ensure leader key visual feedback works. The leader key state is in KeyCommands.vb, but if there's a status bar or visual indicator, update it here.

### 7. BUG-004: FlexKnob.Dispose() crash

**File:** Find `FlexKnob` (likely in the main project or Radios). Guard the `Dispose()` method:

```vb
Protected Overrides Sub Dispose(disposing As Boolean)
    Try
        If disposing Then
            ' existing dispose code
        End If
    Catch ex As Exception
        ' System.IO.Ports v9.0.0 can throw on shutdown
        System.Diagnostics.Trace.WriteLine($"FlexKnob.Dispose error (harmless): {ex.Message}")
    End Try
    MyBase.Dispose(disposing)
End Sub
```

Search for `FlexKnob` in the codebase to find the exact file and current Dispose implementation.

---

## Phase Order

1. **Leader key earcons** in EarconPlayer (6 new methods)
2. **Leader key state machine** in DoCommand() — Ctrl+J trigger, no timeout, Escape cancel
3. **Leader key dictionary** + DSP toggle bindings with on/off earcons + speech
4. **Leader key help** (Ctrl+J → H or ?)
5. **Add leader key commands to Command Finder** registry in ApplicationEvents.vb
6. **TX filter nudge handlers** (4 directions + speak width)
7. **TX filter keyboard shortcuts** (Ctrl+Shift/Alt + brackets)
8. **TX filter earcon feedback** (reuse FilterEdgeMoveTone)
9. **Menu text updates** to show leader key bindings
10. **Feature gating** — leader key DSP toggles respect license/capability (BUG-016 partial)
11. **BUG-004** — guard FlexKnob.Dispose()

Build and commit after each phase or logical group.

---

## Key Design Decisions

1. **No timeout on leader key.** Cancel with Escape only. This matches JAWS layered keystroke behavior.
2. **Earcons fire before speech.** The earcon gives instant feedback (< 5ms), speech follows for confirmation.
3. **Always consume the second key.** Even invalid keys are consumed (with error earcon) — never let a leader-mode keypress fall through to normal dispatch.
4. **Reuse existing earcons for TX filter.** FilterEdgeMoveTone() from Sprint 19 is perfect.
5. **Simple TX filter nudge.** No edge mode for TX filter (unlike RX). Just straight ±50 Hz per keypress.

---

## Accessibility Notes

- Leader key "J" announcement is short and non-blocking (interrupt mode)
- Feature toggle earcons are distinct: rising = on, falling = off — operators learn the pattern quickly
- Invalid key buzz is clearly different from valid tones — no confusion
- Help command speaks full list of available commands
- TX filter speak includes both edges AND computed width in kHz
- All Command Finder entries have Keywords for fuzzy search
