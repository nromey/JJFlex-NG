# Nightowl launch card — ten tracks, three steps each

Every track follows the same recipe: open a new terminal window, change to
the track's directory, start Claude with the right model, and type the
start prompt. The start prompt is always the same sentence with the track
letter changed. Launch them in any order — they are fully independent.
Starting Track J early is nice (it merges first) but nothing requires it.

Tell me "Track X is done" as each one reports completion — I handle all
merging. Tracks A (small fixes) and the NAT lab are mine; you launch
nothing for them.

The model command: `claude --model fable` or `claude --model opus` starts
the session on that model directly. If you forget the flag, type `/model
fable` or `/model opus` as your first command inside the session, then
continue.

## Track J — slice identity (merges first)

1. cd C:\dev\jjflex-qb-j
2. claude --model fable
3. Start Queue-Burn Track J from TRACK-INSTRUCTIONS.md

## Track B — Settings, audio surface and device pickers

1. cd C:\dev\jjflex-qb-b
2. claude --model opus
3. Start Queue-Burn Track B from TRACK-INSTRUCTIONS.md

## Track C — per-radio network settings

1. cd C:\dev\jjflex-qb-c
2. claude --model fable
3. Start Queue-Burn Track C from TRACK-INSTRUCTIONS.md

## Track D — connectivity truth and guidance

1. cd C:\dev\jjflex-qb-d
2. claude --model fable
3. Start Queue-Burn Track D from TRACK-INSTRUCTIONS.md

## Track E — selector, roster, dual-homing

1. cd C:\dev\jjflex-qb-e
2. claude --model opus
3. Start Queue-Burn Track E from TRACK-INSTRUCTIONS.md

## Track F — dialog and SmartLink account sweep

1. cd C:\dev\jjflex-qb-f
2. claude --model fable
3. Start Queue-Burn Track F from TRACK-INSTRUCTIONS.md

## Track G — Audio Workshop, hear yourself

1. cd C:\dev\jjflex-qb-g
2. claude --model fable
3. Start Queue-Burn Track G from TRACK-INSTRUCTIONS.md

## Track H — hotkey surface redesign (merges last)

1. cd C:\dev\jjflex-qb-h
2. claude --model fable
3. Start Queue-Burn Track H from TRACK-INSTRUCTIONS.md

## Track I — menu parity and XVTR power

1. cd C:\dev\jjflex-qb-i
2. claude --model fable
3. Start Queue-Burn Track I from TRACK-INSTRUCTIONS.md

## Track K — trace rotation and crash-bundle size policy

1. cd C:\dev\jjflex-qb-k
2. claude --model opus
3. Start Queue-Burn Track K from TRACK-INSTRUCTIONS.md

## After launch

- Each session reads its own TRACK-INSTRUCTIONS.md and works
  independently, committing and pushing to origin as it goes.
- If a session asks a question only you can answer, answer it or relay it
  to me here.
- As tracks report done, tell me and I merge in this order: J first, then
  B and E as they finish, K and G anytime, then F, then C, then D, then my
  Track A batch plus I, then H last.
- When everything lands: clean build, then your guided testing run with an
  Opus session using the doc I will prepare.
