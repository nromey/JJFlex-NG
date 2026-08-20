# Sprint 32 Track D — Amplifier support

**Worktree:** `C:\dev\jjflex-32d` · **Branch:** `sprint32/track-d`
**Branched off Track A's Phase 1** (`9954c395`), so the meter inventory is
already in your tree. **Full design:** `docs/planning/active/elmer-meter-pileup.md`,
section "Track D". Read that first.

## There is nothing to wait for

Noel asked 4O3A directly for developer material on 2026-08-19. Their answer: **it
is all in FlexLib and they have no code to give.** Driver downloads exist, but no
SDK and no samples. That is a green light — no NDA, no SDK request, no follow-up.

It also means **FlexLib is the entire contract, with no spec behind it.** What
the hardware publishes at runtime is the only authority, which makes your trace
capture the actual documentation rather than a nice-to-have.

## What Track A already built for you — the partitioning is DONE

`Meter.Source` already tags every meter by originating device, and Track A's
inventory already groups on it. **You do not need a new concept.**

`Radios.MeterGroup` exposes `Source`, `SourceIndex`, `Meters`, `Label`
("This radio", "Slice N", "Amplifier or tuner 0xNNNNNNNN") and **`Handle`,
formatted `0x%08X` — Track A formatted it that way deliberately so it matches
`Amplifier.Handle` and `Tuner.Handle` and you can join on it.**

Also available: `FlexBase.MeterInventory.ForHandle(string handle)` and
`ForSource(string source, int sourceIndex)`.

**Contract notes:** BIND to `InventoryChanged`, never sample once — amp meters
will arrive *after* the radio's own, so a construction-time snapshot will miss
them entirely. Events fire on FlexLib's meter thread; marshal before WPF.

## D1. Wire the amplifier

`FlexLib_API/FlexLib/Amplifier.cs` gives: `Handle`, `IP`, `Port`, `Model`,
`SerialNumber`, `Ant`, `State` (PowerUp / SelfCheck / Standby / Idle /
TransmitA / TransmitB / Fault), `IsOperate`, `OutputConfiguredForAntenna(ant)`,
`FindMeterByIndex`, `FindMeterByName`, `MeterAdded` / `MeterRemoved` events, and
its own `List<Meter>`.

Command: `amplifier set <handle> operate=0/1`. Subscription: `sub amplifier all`.

On `Radio`: `AmplifierList`, `ActiveAmplifier`, `FindAmplifierByHandle`,
`EinterlockAmplifierHandlesCsv`, `FindMetersByAmplifier(Amplifier)`.

## D2. DO NOT conflate two different amplifiers

`HAAPI.cs` is the 8000-series **built-in** amp: `AmpMode`, `AmpFrequency`,
`AmpModuleGain`, `AmpXmitState`, `AmpIsSelected`, `AmplifierFault` event;
subscriptions `sub ha_api amplifier` and `sub ha_api fault`.

**Noel's 8600 has HAAPI whether or not an external amp is attached.** An external
4O3A amp is a separate concept on a separate path. Getting these confused will
produce a UI that claims an amplifier exists on every 8000-series radio.

## D3. NOT a bug — do not re-raise

`FindMetersByTuner` filters on `SOURCE_AMPLIFIER`. **That is correct**: the TGXL
piggybacks on the amplifier status stream. This is recorded in
`4o3a-integration.md` and has been re-derived repeatedly by successive sessions.
**Read that file before flagging anything in this area as a vendor defect.**

## D4. Tuner — scaffold only, do not guess

`Tuner.cs` is complete: `Handle`, `SerialNumber`, `Version`, `Nickname`, `Model`,
`OneByThree`, `State` (Standby / Operate / Bypass / Fault), `IsOperate`,
`IsBypass`, `AutoTune()`, `RelayC1`/`RelayC2`/`RelayL`, `PttA`/`PttB`,
`Dhcp`/`IP`/`Netmask`/`Gateway`/`Port`, `PortAAnt`/`PortBAnt`, its own
`List<Meter>` with `AddMeter`/`RemoveMeter`. Commands:
`tgxl set handle=... mode/bypass=...`, `tgxl autotune handle=...`.

Both `Amplifier` and `Tuner` carry meter machinery Flex built deliberately, so
the tuner almost certainly publishes meters. **But there is no TGXL on site** —
Noel plans to order one from DXE by end of month. Scaffold read-only if cheap;
**do not invent behaviour you cannot observe.**

## D5. Deliverable: a for-noel bench procedure

Verification requires moving the amplifier near the radio, on 120V and network,
to discover what meters it actually publishes. **Building is not blocked;
verification is.**

Write the procedure to `docs/planning/for-noel/` in the established briefing
format — numbered steps, `**** ` annotation slots for his answers, prose and
bullets only, **no tables**. It should be runnable as one session rather than an
improvised evening.

**Add one item that costs about ninety seconds while he is already at the radio
with a client connected:** observe what `profile autosave "<state>"` actually
does. `Radio.AutoSaveProfile` exists in FlexLib, JJ Flexible never calls it, its
semantics are radio-side and undocumented, and Track H needs the answer. Reading
our source proves nothing — our method is one line that sends a command.

## You own these files

The amplifier and tuner integration (new files preferred), and any Workshop tab
partial you add. **Do not edit `FlexBase`'s meter section — Track A's, and done.**
**Do not edit `MetersPanel`, `MeterToneEngine` or `AudioOutputConfig` — Track B
owns those and is retiring the old enum.**

## Rules that apply to every track this sprint

- **Reuse the symbols you are told to reuse. If you conclude one should MOVE or
  CHANGE SIGNATURE, report it — do not do it.** A clean `git merge` with zero
  textual conflict still broke the build in Sprint 30 for exactly this reason.
- **NO tables, diagrams or ASCII art** in anything you write, including the bench
  procedure. Prose or bullets. The primary user is blind and uses NVDA.
- **Verify builds by the `N Error(s)` summary line**, never by grepping for the
  word "error". Expect ~609 pre-existing warnings.
- Commit per logical chunk with `Sprint 32 Track D: <description>`.
- Do not merge anything into your branch. The orchestrator runs the merge train.

## Build

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
```

Close any running JJFlexRadio first — `Radios.dll` locks.

## Definition of done

Amplifier wired and reachable, HAAPI kept distinct from an external amp, tuner
scaffolded without invented behaviour, amp meters joining the inventory by
handle, the for-noel bench procedure written, clean x64 build. **Report
everything you could not verify without the hardware, and everything you had to
assume.**
