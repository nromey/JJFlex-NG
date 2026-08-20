# jjprobe — the UI Automation probe

Drives and observes a running JJ Flexible **from outside the process**, through
the same UI Automation channel a screen reader consumes.

Promoted into the repository by Sprint 33 Track B. Before that it was five
loose PowerShell scripts and a C# walker living in a session scratchpad under
`%LOCALAPPDATA%\Temp`, which is to say: the capability that located the silent
Audio Workshop on 2026-08-19 had the lifespan of a temp folder.

---

## Why it is C# and not PowerShell

PowerShell is what already worked, so this needed a reason.

- **The seam has to be machine-readable.** Track C's radio observer composes
  with this tool — it presses, the radio gets asked whether it did the thing —
  and that needs a JSON contract with stable exit codes, not
  `Write-Output 'NOT FOUND'; exit 1`.
- **The hard part was already C#.** The cross-process control-view walk with
  per-node failure capture, and the focus-event subscriber, came from the
  scratchpad walker. Those are the parts that found the bug.
- **It builds with everything else.** One `dotnet build`, in the solution, with
  the same toolchain — so it cannot rot quietly the way a script does.
- **Types.** Chord parsing, risk classification and snapshot diffing are real
  logic, and they are better with a compiler watching.

The PowerShell `Force()` foregrounding — the `AttachThreadInput` dance — is
carried over verbatim in `Native.cs`. It is P/Invoke either way, and it is the
non-obvious part that made the original probe work.

## Why it has no project reference to the app

A harness that runs inside the app can only tell you what the app *believes*.
The whole class of bug this exists to catch is the one where the app believes
it announced something and nothing reached the outside world.

The single exception is `jjprobe inventory`, which reflects over a **built**
`JJFlexWpf.dll` found in the build directory under test. That is a runtime load,
so the probe still runs against any build including an installed one, and the
plan can never describe a different build from the one being probed.

---

## Build and run

```
dotnet build tools/uia-probe/UiaProbe.csproj -c Debug -p:Platform=x64
tools/uia-probe/bin/x64/Debug/net10.0-windows/jjprobe.exe --help
```

Default process name is `jjflexible`. Every command takes `--pid N` or
`--process NAME`.

## Commands

- `windows` — every top-level window of the process: handle, class, visibility,
  which one is foreground, and its UIA name. Start here; the app keeps several
  HWNDs alive at once and a keystroke sent to the wrong one does nothing while
  reporting success.
- `tree` — dump the control-view subtree: control type, name, automation id,
  class, keyboard-focusable, offscreen, supported patterns. Enumeration
  failures are printed **in place**, because a provider that throws while
  enumerating children is a screen reader hitting a wall at that exact node.
- `focus` — what is focused right now, with its value and toggle state.
- `watch --seconds N` — log every UIA focus-changed event. These are the events
  NVDA announces from.
- `press --chord "..."` — press a chord for real and report what changed. **The
  Tier 3 seam.** See below.
- `act --op invoke|toggle|select|expand|focus|value|listitems` — drive an
  element through its automation pattern. For getting the app into a starting
  position, *not* for verifying key bindings: invoking a button proves the
  button works and proves nothing about the key meant to reach it.
- `inventory` — read `KeyInventory` out of the build under test.
- `unbound` — every registry command shipping with no key, and the reason
  recorded for it.
- `expand` — offline: show what each `KeyDisplay` string expands to, with its
  derivation and risk. No app driving; safe to run any time.
- `altcheck --src DIR` — offline: static scan for the Alt-chord trap.
- `sweep` — press every binding in the inventory and assert something happened.

## The seam: `press`

```
jjprobe press --chord "Ctrl+J, Ctrl+A" --window "JJ Flexible" --json
```

One JSON object on stdout, nothing else. Fields that matter to a caller:

- `settledAtUtc` and `settleMs` — when the app stopped reacting.
- `quiesced` — true when it went quiet on its own; false when it was still
  churning when the maximum wait expired.
- `routed` — what the key dispatcher logged, in order.
- `dispatcherFoundNothing` — true when the chord arrived and no command claimed
  it.
- `spoke` — the utterances the app produced, in order.
- `uiChanges` — windows opened or closed, focus moves, toggle-state changes,
  automation-tree changes.
- `verdict` — `handled`, `unhandled`, `silent`, `not-sent`, or `skipped`.

Exit codes: `0` ok · `1` error · `2` usage · `3` pressed but never settled ·
`4` target window not found · `5` could not bring the window to the foreground ·
`6` refused at the safety gate.

**"Settled" means** no UIA event from the target process *and* no new bytes in
the speech log for `--quiet-ms` consecutive milliseconds, capped at
`--max-settle-ms`. That definition is what makes the tool composable with a
radio-side observer: the radio can only be asked "did you do the thing?" after
the app has finished doing it, and a fixed sleep either wastes the run or races
it.

Chord syntax: modifiers with `+`, sequence steps with `,`. So `Shift+F6` is one
keystroke and `Ctrl+J, V, H` is three.

## The two trace channels

### Routing — the strong one, and it is free

The key dispatcher writes `DoCommand:` and `Leader:` lines **unconditionally at
Info level**, so nothing has to be turned on. Every registry keystroke logs the
key it resolved to, and a keystroke that reaches the dispatcher and finds
nothing logs:

```
DoCommand:key not found:F4, Alt
```

That line is the dead-binding signature in plain text. It separates *the chord
never arrived* from *the chord arrived and nothing was listening* — a
distinction speech cannot make, and one a human at the keyboard cannot hear,
because both sound like silence. The Alt+L failure of 2026-08-13 would have
written one of these on every press.

`verdict: "unhandled"` in a press result means exactly this happened.

### Speech — needed, because half the key map never reaches the dispatcher

Most of JJ Flexible's keys change nothing visible. They **speak**. Pressing `M`
mutes the slice and says so, and no state anywhere in the automation tree
records it.

Worse, the JJ Flexible Home display is a single custom-peer text box that
deliberately publishes **no TextPattern and no ValuePattern**, so NVDA stays
quiet and the app does its own speaking. The "fields" are caret positions inside
it. Focus never moves between them. No amount of automation-tree inspection can
tell you which field you are on.

So the probe reads the app's own trace file, where `ScreenReaderOutput` logs
every utterance as `ScreenReaderOutput: Spoke '...'` at Verbose level. That
makes speech observable from outside without changing a line of app code.

**Precondition:** the `Spoke` lines are Verbose and the default level is Info.
Measured on this machine: every trace file at Info contains **zero**
`ScreenReaderOutput` lines. A detailed capture raises the level, so `sweep`
starts one with `Ctrl+J, Ctrl+D` and then *reads the log back* to confirm
Verbose lines are actually appearing — not merely that the chord did something,
which any side effect would satisfy. Without that check a whole sweep can report
"no observable effect" for every key and be measuring nothing but its own
misconfiguration, which is why the report says up front whether each channel was
live.

## Which commands can take your keyboard, and which provably cannot

Only two commands inject synthetic input: `press` and `sweep`. Everything else
— `windows`, `tree`, `focus`, `watch`, `inventory`, `unbound`, `expand`,
`altcheck`, and `sweep --dry-run` — only observes.

That is enforced rather than asserted. `Native.SendKeyEvent` is the single
injection point, it is called from exactly one file, and it returns immediately
unless `Native.InjectionArmed` has been set, which only the two typing commands
do. The distinction is worth the four lines because of how Windows draws it:

- **Focus is per message queue.** `SetFocus` can only target a window created by
  the calling thread, and off-foreground it changes that thread's own focus
  record and nothing else. It cannot reach the operator's keyboard.
- **`SendInput` injects into the FOREGROUND queue.** It really can take the
  keyboard, which is exactly why it is gated.

These are not the same risk and should not be argued as though they were.

Note what this replaced: `ReleaseAllModifiers` used to run from `Main`'s finally
block on *every* invocation, so a read-only command would have sent a keyup for
any modifier that happened to be down. On an idle desktop that sends nothing —
but "usually sends nothing" is a weaker claim than "cannot inject", and only the
second one is worth making to someone deciding whether to grant a permission.

### Why `SendInput` cannot be swapped for something quieter

`PostMessage(WM_KEYDOWN)` needs no foreground and no permission, and it is not a
substitute here. WPF reads modifier state from the real keyboard
(`Keyboard.Modifiers`), so a chord delivered by `PostMessage` arrives with no
modifiers attached and a `Ctrl+J` test would silently pass as a bare `J`.

More fundamentally, it would answer a different question. The bug class under
test is *the key the operator physically presses does not reach the handler*.
`PostMessage` proves that a `WM_KEYDOWN` carrying vk=L runs the handler — which
was never in doubt on 2026-08-13. What failed that day was the translation from
a physical keystroke into `e.Key`, and only real input injection exercises that
translation.

## Safety

This tool types on a real desktop belonging to an operator who cannot see the
screen, in an application whose keys can key a transmitter.

- **Modifiers are always released.** In a `finally` on every path, after every
  chord, and from a `ProcessExit` and `Ctrl+C` handler. A stuck `Ctrl` or `Alt`
  is silent and makes every subsequent keystroke on the machine wrong.
- **Foregrounding is checked, not assumed.** If the target window does not
  actually reach the foreground the keystroke is *not sent*, and the result says
  so. Firing it anyway would type into whatever the operator was using.
- **Risky chords need naming.** `sweep` presses only chords classified `safe`
  unless `--risk` says otherwise. `transmits` covers push-to-talk, transmit
  lock, the CW message slots and the TX test tone; `mutates` covers creating and
  releasing slices and anything else the operator would have to put back.
  Skipped chords are listed in the report, because a silent exclusion reads as
  coverage.
- **Transmitting needs a clearance, not a flag.** `--risk transmits` is not
  enough. A transmitting chord also requires `--transmit-clearance FILE`: JSON
  carrying `issuedUtc`, `ceilingWatts`, `measuredWatts`, `validForMs`, written
  by something that can read the radio's power **back** — and refused if it is
  stale or over the ceiling. Raised by Track G on 2026-08-20:
  `FlexBase.setupFromScratch()` sets `RFPower = 100` unconditionally, so a
  harness keying a radio that has been reset, or one it has never seen, can find
  itself at full power with nothing having asked for it. A ceiling you *set* is
  a wish. A ceiling you *read back immediately before keying* is a ceiling. The
  probe has no radio connection by design, which is precisely why the vouch has
  to come from the other side of the seam and cannot be waved through here.
- **The sweep halts rather than guessing.** If a key opens something and Escape
  does not bring the app back, it stops — every later result would have been
  measured against the wrong window.

**It is also audible.** The sweep works by making the app speak, so a run is a
few minutes of continuous speech. That is a reason to schedule it, not a reason
to suppress it: suppressing speech would also destroy the observation channel.

## What it does not do

- It does not open dialogs to test their local keys. Audio Workshop, Settings,
  the logging pane and the value-field expanders are reported as not reached
  rather than pressed into Home and written up as dead.
- `altcheck` is a line-window heuristic, not a compiler. It finds the *shape* of
  the bug. Only pressing the key proves the chord arrives.
- A clean sweep is evidence, not proof. It says every key it could reach did
  something observable — not that it did the *right* thing. Composing with
  Track C's radio observer is what closes that gap.
