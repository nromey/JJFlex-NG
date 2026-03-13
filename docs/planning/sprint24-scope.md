# Sprint 24 Scope — Collected During Testing Session (2026-03-12)

This is the raw scope list for Sprint 24 planning. Needs decomposition into tracks/phases.

## Slice Rework
- Slice selector field in FreqOut (up/down to switch slices with short status)
- Slice operations field (volume, pan, mute per slice)
- Fix slice count not updating after RemoveSlice (says "Maximum slices reached" with 1 slice)
- JJ Shift+M hotkey for mute/unmute current slice
- Don's A/B switching issues (may need trace session to diagnose)
- Slice creation/release speech improvements
- Stale slice menu data fix

## Audio Architecture
- Split NAudio into separate WaveOutEvent channels: alerts, meters, waterfall (future)
- Master Sound Volume — scales all app-generated audio together
- Per-channel volume sliders: Alert Volume, Meter Volume, Waterfall Volume (future)
- Per-channel audio device assignment (each channel can target different output device)
- Radio volume control (wrapping FlexLib Rig.SetVFOVolume or equivalent)
- "Same as Alerts" device default for meters/waterfall (simple until user configures)
- Audio Settings tab in Settings dialog with all controls

## Verbosity Engine
- Speech verbosity cycle: Off / Terse / Chatty
- Tones toggle: Beeps on / Beeps off
- Tag Speak() calls with verbosity level — only speak if current setting allows
- Terse examples: "JJ", "NR Level 5", "Mute on"
- Chatty examples: "JJ key", "NR Level 5, arrows to change", "Mute on, slice A audio muted"
- Hotkey for cycling verbosity (Ctrl+Alt+V? — check conflicts)
- Hotkey or setting for tones on/off

## Status Dialog Rebuild
- Rebuild as accessible WPF dialog
- ListBox-based (like About dialog fix) or WebView2
- Replace disabled Ctrl+Alt+S with working dialog
- Consider what info belongs here vs Ctrl+Shift+S speech

## Audio Workshop Fix
- Tab navigation broken — only Load Preset button reachable
- Escape doesn't close the dialog
- Deep investigation needed into why WPF TabControl doesn't work

## Key Migration
- KeyCommands.vb → C# (originally pledged for Sprint 24)
- Conflict audit across all scopes
- Clean up key scope hierarchy (Radio, Global, etc.)

## Quick Wins
- NR Level minimum: 0 → 1 (level 0 kills all audio)
- 60m mode advisory: warn if wrong mode for 60m segment (CW/DIGU/DIGL only in digi segment)

## Deferred to Sprint 25
- Speech catalog file: extract all Speak() strings to XML/JSON message file with per-verbosity variants
- Message editor dialog for user customization
- .resx alignment for future localization

## Notes from Discussion
- Don (WA2IWC) confirmed help files work well
- Don's .ssdr_cfg profile: binary/encrypted, FlexLib has import API (SendDBImportFile), radio handles decryption
- Profile import/export already works via FlexDB.Import()/FlexDB.Export()
- User wants to maximize coding effort — has compute credits and tokens available
- Sprint 24 was originally pledged as VB-to-C# migration but scope expanded significantly
