# JJ Flex — Audio Mixer & Routing (Planning)

Last updated: 2026-01-31
Status: Planning

## Goals
- Make it easy to adjust TX/RX audio (including contest workflows)
- Enable recording/playback of rig audio for self-checks
- Support “play audio through Flex” where possible (SmartSDR-like)
- Respect DAX realities (use Flex tools where appropriate)

## User Needs (from Don + operator feedback)
- “Adjust Rig Audio” should guide users to:
  - find TX enable/MOX pathways
  - set mic/line level, proc, and ALC sensibly
  - prevent being “too hot” (clipping) with helpful feedback
- Ability to record a short TX snippet and play back (parrot-style)
- Ability to route external program audio through Flex (DAX pathways)

## Design Sketch
### Audio Menu
- Adjust Rig Audio…
- Record (toggle) / Playback last clip
- Send playback through Flex (on/off) + level + pan
- Audio Devices… (input/output selection)

### “Parrot” TX check
- Capture last N seconds of transmitted audio
- Optionally mute Flex output during capture
- Play back after transmission ends
- Optional analysis: “a bit hot / too quiet / clipping” (future)

### Devices & Performance
- Support choosing input/output devices (MME/WASAPI/ASIO as feasible; decision pending)
- Consider PortAudio reset behavior after device changes

### DAX
- Research how SmartSDR uses DAX and what JJ Flex can/should manage
- Prefer letting Flex’s DAX manager handle deep config if that’s the stable path

## Acceptance Criteria
- User can enter “Adjust Rig Audio” without hunting through unrelated fields.
- Basic recording/playback works with clear accessible controls.
- Device selection changes are stable and documented.
