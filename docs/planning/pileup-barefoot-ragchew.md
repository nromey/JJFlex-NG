# Sprint 16: Pileup Barefoot Ragchew

## Overview
Bug fixes, dialog wiring, connection tester rework, and cleanup.

## Tracks

| Track | Branch | Worktree | Description |
|-------|--------|----------|-------------|
| A | sprint16/track-a | C:\dev\JJFlex-NG | Connection Tester → RigSelector |
| B | sprint16/track-b | C:\dev\jjflex-16b | Sprint 15 Bug Fixes (FilterPresets, keys, ScreenFields) |
| C | sprint16/track-c | C:\dev\jjflex-16c | Dialog Wiring + Profile Reporter |
| D | sprint16/track-d | C:\dev\jjflex-16d | LiveRegion Cleanup + Dummy Load Mode |

## Execution Order
All 4 tracks are independent — start all simultaneously.

## Merge Plan
- Merge target: Track A (sprint16/track-a)
- Tracks merge in any order as they complete
- No file conflicts expected:
  - Track A: RigSelector + ConnectionTester + globals.vb wiring
  - Track B: MainWindow PreviewKeyDown + ScreenFieldsPanel + FilterPresets wiring
  - Track C: NativeMenuBar menu items (different sections) + new ProfileReporter
  - Track D: NativeMenuBar Transmission section + MainWindow LiveRegion removal + FlexBase DummyLoadMode
- **Potential overlap:** NativeMenuBar.cs is touched by Tracks A, C, and D but in different sections
- After all merges: final merge sprint16/track-a → main

## Status
- [x] Branches created
- [x] Worktrees created
- [x] TRACK-INSTRUCTIONS.md written
- [ ] Track A complete
- [ ] Track B complete
- [ ] Track C complete
- [ ] Track D complete
- [ ] Merges complete
- [ ] Final build verified
- [ ] Agent.md updated
