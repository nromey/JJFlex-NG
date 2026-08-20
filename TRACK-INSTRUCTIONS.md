# Sprint 32 Track B — Rebuild the meters panel

**Worktree:** `C:\dev\jjflex-32b` · **Branch:** `sprint32/track-b`
**Branched off Track A's Phase 1** (`9954c395`), so the meter inventory is
already in your tree. **Full design:** `docs/planning/active/elmer-meter-pileup.md`,
section "Track B". Read that first.

## These five tasks are ONE job

#129, #124, #131, #126, #127. Each independently rewrites or touches the same
433-line `MetersPanel.xaml.cs`, and #129's root fix is the structural change the
others need. Doing them separately means rewriting the file twice.

## What Track A already built for you — USE IT, do not rebuild it

On `FlexBase`:
- `MeterInventory MeterInventory { get; }` — never null, built in the
  constructor, live from the first connect. **This is the property you want.**
- `ImmutableList<Meter> RadioMeters` — snapshot, empty when disconnected.
- `event MeterDataDel MeterData` — signature
  `void (object sender, Meter meter, float value)`. Every reading of every meter,
  identity intact.
- `event EventHandler MeterInventoryChanged` — the SET changed.

`Radios.MeterInventory`: `event EventHandler InventoryChanged`;
`IReadOnlyList<MeterReading> All`; `IReadOnlyList<MeterGroup> Groups` (ordered
radio, then slices by index, then amps and tuners); `int Count`;
`MeterReading Find(string name)` (case-insensitive); `ForHandle(string)`;
`ForSource(string source, int sourceIndex)`; `ToText()`.

`Radios.MeterReading`: `Index`, `Name`, `Description`, `Source` (upper-cased
`SLC`/`AMP`/`HAAPI`), `SourceIndex`, `Units`, `Low`, `High`, `Value`,
`HasReading`, `UpdateCount`, `LastUpdateUtc`, `Age`, `IsStale(TimeSpan)`,
`ValueText()`.

`Radios.MeterGroup`: `Source`, `SourceIndex`, `Meters`, `Label` ("This radio",
"Slice N", "Amplifier or tuner 0xNNNNNNNN"), `Handle` (formatted `0x%08X`).

**TWO CONTRACT NOTES FROM TRACK A, both load-bearing:**

1. **BIND to `InventoryChanged`. Do NOT sample once.** FlexLib raises nothing
   when a meter appears and the list grows during registration — an early
   snapshot catches eleven meters with the TX-side ones still to arrive. This is
   the same defect as #129, one layer down.
2. **`MeterData` and `InventoryChanged` fire on FlexLib's meter thread, NOT the
   UI thread.** Marshal before touching WPF. `All` and `Groups` are replaced
   wholesale rather than mutated, so you can iterate one without locking.

## B1. #129 — the root fix

`BuildSlotControls()` is called once in the constructor and never again, so slots
added to the engine later exist with **no controls at all**. Noel added a slot,
got slot 5, and could see nothing else.

**Make the panel a LIVE VIEW over the engine's slot collection.** Not a
constructor snapshot. This is the structural change everything else here rests on.

## B2. #124 — the model move

Off `MeterToneEngine.MeterSource` (the 8-value enum) and the parallel hardcoded
string array in `MetersPanel.xaml.cs:31`, onto `MeterDefinition` /
`MeterSourceRef` with a **string key**. `MeterSlot`'s own doc comment already
concedes this: *"new code should use Definition directly."* The bridge was built
and never crossed.

Populate the source picker from `FlexBase.MeterInventory`. **A hundred entries in
a combo is its own accessibility problem** — follow the #62 device-picker
precedent: a "common meters" default and an "all meters" mode, grouped by
`MeterGroup.Label`.

**You are the ONLY track permitted to retire `Radios.MeterType` and
`JJFlexWpf.MeterSource`.** Track A deliberately left them as a shim. Retire them
once nothing reads them, and **say so explicitly in your completion report** so
the merge knows the shim is gone.

## B3. Config migration — the highest-risk item in the sprint

`AudioOutputConfig` persists the meter source **as an integer**. Existing users
have slots saved as ints; without a migration, every operator's meter tones
silently repoint to whatever now sits at that ordinal. Same class as #34, the
PortAudio device-index bug.

**Write the migration FIRST, and test it against a real pre-existing
`audioConfig.xml`** — not a synthetic one. `%AppData%\JJFlexRadio\`.

## B4. The slot redesign — Noel's words

*"Making it so that you have tabs to go through all slots is not efficient, so
you'd need a combo to select a tone and then modify / enable / do whatever with
it. Also would allow for del key / remove yes/no query."*

A slot selector combo, one set of controls that retarget to the selected slot,
Delete with a confirm.

## B5. #131 — the runaway test tone

The Test button's stop timer only sets `slot.ToneProvider.Active = false` when
`!MeterToneEngine.Enabled` — but the only route into the panel, Ctrl+M,
**enables** meters. The stop condition is guaranteed false and the tone never
stops. **Stop unconditionally on expiry.**

## B6. #126 — Ctrl+M does two jobs

It shows the panel AND turns meter tones on. Separate them; the panel needs a way
in that does not change audio state. **This touches key bindings, so the
CLAUDE.md keyboard audit applies — including pressing the key on a real build.**
Track G owns `KeyCommands` generally; flag the change in your report so the merge
knows to look.

## B7. #127 — the missing earcon

The meters expander is the only one on Home with no expand/collapse earcon. Wire
`PlayExpand` / `PlayCollapse`.

## B8. Pan resolution

Three values (Left / Center / Right) are not enough. Noel: *"we need that to be
slider or have more values though if we have more items."* Continuous, or at
minimum five to seven positions.

## You own these files

`MetersPanel.xaml.cs`, `MeterToneEngine.cs`, the meter section of
`AudioOutputConfig.cs`. **Do not edit `FlexBase`'s meter section — that is Track
A's**, and it is done.

## Rules that apply to every track this sprint

- **Reuse the symbols you are told to reuse. If you conclude one should MOVE or
  CHANGE SIGNATURE, report it — do not do it.** A clean `git merge` with zero
  textual conflict still broke the build in Sprint 30 for exactly this reason.
- **NO tables, diagrams or ASCII art** in anything you write. Prose or bullets.
  The primary user is blind and uses NVDA.
- **Verify builds by the `N Error(s)` summary line**, never by grepping for the
  word "error" — that matches warning prose. Expect ~609 pre-existing warnings.
- Commit per logical chunk with `Sprint 32 Track B: <description>`.
- Do not merge anything into your branch. The orchestrator runs the merge train.

## Build

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
```

Close any running JJFlexRadio first — `Radios.dll` locks.

## Definition of done

Panel is a live view; picker driven by the real inventory with a common/all
split; **config migration written first and tested against a real file**; slot
combo with Delete-and-confirm; test tone stops; Ctrl+M split; expander earcon;
finer pan; enum and parallel array deleted; clean x64 build. **Report explicitly
whether you retired `MeterType`/`MeterSource`.**
