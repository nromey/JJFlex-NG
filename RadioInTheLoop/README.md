# RadioInTheLoop - the connect harness that uses a real radio

How to run it, in one breath: open PowerShell in the repo root, set the
variable `JJFLEX_RADIO_IN_THE_LOOP` to your radio's serial number - for
example `$env:JJFLEX_RADIO_IN_THE_LOOP = "0621-1104-6601-1425"` - then run
`dotnet run --project RadioInTheLoop -c Debug -p:Platform=x64`. It will find
your radio, check nobody is on it, connect to it twice as a GUI client named
JJHarness without changing any of its settings, disconnect, verify it left no
trace, and finish in about one to two minutes. The last line of output always
starts with the word RESULT: it says PASS, FAIL, REFUSED, CONNECT FAILED, or
STOPPED, followed by one sentence saying why. Ctrl+C at any point releases
the radio on the way out.

## Why this exists

On 2026-08-30 a connect held the application's UI thread for 45 seconds,
three times, and stalled typing across the whole desktop. It was found by
reading trace files hours later, and the fix could only be verified by a
person trying it. Noel's ruling that night: it should have been testable,
even if the test has to open the radio. This harness is that test.

## What it will not do without you

It refuses, with an explanation, unless every one of these holds:

- The settings tree for the run is throwaway, verified by reading it back
  from the radio layer. Your live configuration is never touched. Nothing
  waives this check.
- A human has declared a radio free for this one run, by setting
  `JJFLEX_RADIO_IN_THE_LOOP` to that radio's serial. Set it by hand, in the
  terminal, for the run. Never set it in a script, an agent brief, or a
  profile - it is a statement by a person about a specific radio at a
  specific moment, and it evaporates with the terminal.
- Discovery actually finds that exact serial. A different radio, however
  available, is refused; the refusal lists what was seen so a mistyped
  serial is a ten-second fix.
- The radio has no GUI clients connected, sampled three times over three
  seconds from the radio's own broadcasts. If you are connected to it with
  JJ Flexible or SmartSDR, disconnect first. Someone connecting mid-run
  stops the run the same way.
- The change-nothing hold armed for that serial and reads back armed, so
  the connect path's normal writes to the radio - TNF, VOX, CW keyer,
  profile selection and creation - are all held. The radio's settings stay
  exactly its owner's.
- The heartbeat instrument passed its own positive control: a deliberately
  planted 400 ms block on the pumped thread must be seen by the watcher
  before any responsiveness verdict is trusted.

Close JJ Flexible before running - only one program on the machine can
listen for radio discovery at a time.

## What it asserts once allowed

- The UI thread stays responsive through a connect. A real message pump with
  a heartbeat stands in for the app's UI thread; Connect and the disconnect
  run on it and their hold time is measured, Start runs on a worker exactly
  as the app runs it while the heartbeat must keep ticking. A 45-second
  block fails loudly with the measured number.
- A connect completes or fails within stated bounds, printed beside every
  measurement. A clean failure is a legitimate outcome, not an error - the
  harness then asserts the aftermath instead.
- A failed connect must not remove a healthy radio from discovery. After a
  failure the radio must stay discoverable past FlexLib's 17-second
  eviction window; after each disconnect a completely fresh discovery
  session must find it again.
- The station-name handshake completes, observed on the radio's own client
  roll call. When it does not, the harness prints what the radio actually
  said - its messages, client add and remove events, and status changes -
  rather than only that something timed out.
- Everything is released on the way out. The second cycle reconnects from a
  fresh API session and refuses to stack on a leaked registration; the
  final sweep verifies no JJHarness client remains on the radio at all.

## What it deliberately does not cover

- SmartLink. This harness is LAN-only: the remote path needs an account,
  tokens, and possibly an interactive sign-in, and the in-app Connection
  Tester (Radios/ConnectionTester.cs, driven from the Connection Tester
  dialog) already exercises SmartLink connect cycles from inside the
  application. The two are complementary on purpose - this one is the
  standalone, guard-gated instrument for the local path and for UI-thread
  honesty; that one is the in-app profiler for the remote path.
- Transmit. There is no code in this project that can key a radio, and the
  change-nothing hold keeps the connect path from touching TX-adjacent
  settings. It connects, observes, and disconnects.
- Audio. No DAX, no remote audio streams, no Opus, no sound of any kind -
  speech is never initialized in this process.
- The application itself. It drives the same Radios-layer entry points the
  app drives (LocalRadios, Connect, Start, Dispose, on the same threads),
  but the app's own selector, walk, and retry ladder above that layer are
  not in the loop.
- Deliberately engineered failures. The failure branch asserts only when a
  connect genuinely fails; nothing here provokes a failure on a real radio
  on purpose.

## Reading the result

Exit code 0 is PASS. 1 is FAIL - at least one assertion failed, and every
failure is repeated just above the RESULT line. 2 is REFUSED - a guard said
no before the radio was touched, or an occupant appeared; the message says
what it wanted and what it found. 3 is ERROR - the harness itself broke.
4 is STOPPED - you pressed Ctrl+C, and the radio was released on the way
out. 5 is CONNECT FAILED, GUARANTEES HELD - the connect failed, which is a
real result worth knowing, and every assertion around it passed.

A full trace of each run is written to a fresh folder under the Windows temp
directory (the path is printed near the top of the output, and again in the
run's settings line), for the runs where the RESULT line is not enough.

To check the harness itself without a radio, run it with `--instrument-only`
(after the `--` separator: `dotnet run --project RadioInTheLoop -c Debug
-p:Platform=x64 -- --instrument-only`). That mode needs no declaration,
touches no network, and only proves the settings isolation and the heartbeat
instrument work on this machine.

## Where this project sits

It is not in JJFlexRadio.sln and it is not a test project - no xunit, no
test SDK - so no `dotnet test` at any scope can discover it, and nothing
that builds everything builds it. It builds and runs only by the explicit
command above. `dotnet test Radios.Tests/Radios.Tests.csproj -c Debug
-p:Platform=x64` is unaffected and stays the everyday suite.
