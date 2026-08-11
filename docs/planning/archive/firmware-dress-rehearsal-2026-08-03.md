# Firmware dress rehearsal — 2026-08-03 evening

The full Tuesday flow, run tonight against the 8600 on the bench. Everything
Tuesday needs happens here at least once: fetch the manifest from JJ Flexible
servers, download the right image, verify it, send it, watch the radio through
the reboot, confirm the new version. Tonight's run also genuinely updates the
8600 from 4.1.3 to 4.2.20, which is the end state we want anyway — matched
4.2.20 library and 4.2.20 firmware.

Build under test: Debug x64 from branch `track/flexlib-4220` (FlexLib 4.2.20
underneath, registration detection included). Launch:

`bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe`

(Builds from before the 2026-08-04 rename on `track/rename-jjflexible` still drop
`JJFlexRadio.exe` at that path — use whichever the tree actually produced.)

## Step 1 — Connect

Connect to the 8600 on the local network as usual.

Shortly after connect, the app now quietly asks the SmartLink server whether
this radio is registered to your account. Two possible outcomes, both
informative:

- If the 8600 is already registered to your account: nothing happens. Silence
  is the correct behavior.
- If it is not registered (or the registration from earlier today did not
  complete): you get a spoken heads-up and a message box titled "Radio not
  registered with SmartLink" pointing at Radio Setup step 2. That is the new
  feature working. OK dismisses it; it will not re-ask this run.

Report which one you got.

## Step 2 — Radio Setup, look at step 2's status

Tools menu, Settings, Radio Setup tab. The registration step used to say "JJ
Flex cannot tell from here whether this radio is already registered." It now
asks SmartLink and tells you the real answer — "already registered to your
account" or "not registered, use Register this radio below." The text may take
a few seconds to upgrade from "Checking with SmartLink..." — that round trip
is real.

## Step 3 — Download firmware from JJ Flexible servers

Still in Radio Setup, the firmware step. Press Alt+D, "Download firmware for
this radio."

What happens under the covers: the app fetches
`https://data.jjflexible.radio/firmware/manifest.json`, picks the image for
your radio's family (the 8600 takes the FLEX-9600 image — 368 MB), downloads
it to a `.partial` file, checks the SHA256 against the manifest, and only then
moves it into place.

What you should experience: a standard progress bar (NVDA progress tones) and
a byte count on screen you can read with the review cursor. No chatter. The
download takes as long as a 368 MB download takes on your connection.

If the SHA check fails, the app refuses the file — that is the design working,
try the download again.

## Step 4 — Send it to the radio

The preflight runs first (connected, local, file verified). Then send.

Timeline to expect, spoken as each phase lands:

- Sending: the transfer to the radio takes a minute or two on LAN.
- Radio restarting: the radio drops off the network and reboots itself. This
  is the long quiet part — several minutes. Do not power anything off.
- Radio returned: it is broadcasting on the network again.
- Verified: its discovery packets now report 4.2.20.41343. Done.

The watcher allows up to 5 minutes for the radio to leave and 15 for it to
come back before it gives up and says so. If it reports "came back unchanged,"
the radio rejected the image — its own bootloader is the final integrity
authority — and nothing is broken; tell me and we look at the trace.

## Step 5 — Reconnect and confirm

Reconnect to the radio. Two things to check:

- The firmware version now reads 4.2.20.41343.
- The version-mismatch note from before is gone — library and firmware now
  match exactly.

Then use the radio for a few minutes. Audio, tuning, slice operations. The
station-name echo already proved instant on this library, but real use is the
test that counts — anything that feels different from this morning's 4.1.5
session is worth saying out loud.

## What Tuesday adds and subtracts

Tuesday with Tony and Don's 6300, the differences are:

- The 6300 takes the FLEX-6x00 image (61 MB, much faster download).
- Don's radio is already registered (SmartLink works today), so step 2 is
  skipped — same as tonight if your 8600 turns out registered.
- The connection is to Tony's LAN, with Tony at the radio if anything needs
  hands. Firmware updating is LAN-only by protocol, which is the whole reason
  Tuesday exists while Tony is still upstate.
- The stepping-stone question is answered (FlexRadio community, researched
  2026-08-03): there is no enforced intermediate version — a 6300 has been
  taken from v2.12.1 straight to v4.x. The real hazard on a cross-major jump
  is stale radio-side state: Flex staff traced post-upgrade crashes to old
  profiles carrying over, and the standing advice is a factory reset after a
  major-version jump. Plan for Tuesday: update first, test; if the radio
  crashes on band changes or acts strangely, factory reset is the fix — with
  Tony at the radio to press the buttons and the knowledge that Don's
  radio-side profiles (TX profile, mic settings) get wiped and need
  re-setting afterward. Do not factory reset preemptively unless problems
  show; it costs Don his stored settings.
- One more known wrinkle from the same research: the update progress bar can
  stick at 100% for a long time and look frozen. It is not bricked — power
  cycle and the radio comes up on the new firmware. Our discovery-polling
  watcher handles this case better than SmartSDR's bar: it reports the radio
  returned and what version it returned with.

## If something goes sideways

Escape cancels any dialog. The radio's bootloader will not flash a bad image —
the worst realistic outcome of a failed attempt is a radio that reboots back
to its old firmware, which the watcher reports honestly. The trace file
captures the whole session either way:
`%AppData%\JJFlexRadio\JJFlexRadioTrace.txt`.
