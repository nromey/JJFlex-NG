# Laptop handoff — 2026-08-13 morning

Read this on the laptop. It covers getting the build there, what changed, and
what to do with it. The tests themselves are in the companion file,
`2026-08-13-laptop-mic-and-headset-tests.md`.

If you are running a Claude session on the laptop, hand it this file — it is
written to work as context for either of you.

## Build identity

**Version 4.1.16.829, Debug, x64.** Built on the dev box the morning of
2026-08-13, from branch `honest-tx-audio`.

Self-contained: the .NET runtime ships inside it, so the laptop needs no
runtime install. Unzip and run.

## Getting it onto the laptop

The zip is on the NAS, which is the archive every debug build lands in
automatically:

    \\nas.macaw-jazz.ts.net\jjflex\historical\4.1.16.829\x64-debug\

Inside that folder:

- `JJFlex_4.1.16.829_x64_debug_20260813-0501.zip` — the build, 75.7 MB
- `NOTES-4.1.16.829-debug_20260813-0501.txt` — the short operator-facing summary
- `jjflexible.exe` and `jjflexible.pdb` — loose, for symbolicating a crash

The zip and NOTES carry a timestamp because they are never overwritten — if a
second build lands under this same version today, both copies survive and the
timestamp is what tells them apart. The loose exe and pdb do get refreshed.

**This was deliberately NOT published to Dropbox.** Dropbox `debug\` is the
tester broadcast channel — that is Don's copy, and pushing to it is a decision
you make, not a side effect of a build. Say the word and I will publish; the
build is ready either way.

On the laptop: open the NAS path, copy the zip somewhere local, unzip it, run
`jjflexible.exe` from the unzipped folder. Do not run it from inside the zip.

If the NAS is not reachable from the laptop, check Tailscale is up. That is the
usual cause and it is quicker to check than to work around.

## What is in this build that has never met a microphone

Everything below shipped over the last two days and has been exercised only by
the compiler. That is the whole reason for this session.

- The Audio Workshop's "This Computer" section, and the walk-through ordering
  of the whole TX Audio tab
- The Microphone Check, including Windows microphone-privacy detection
- Device folding in the picker — duplicates collapsed, unplugged devices
  flagged
- Both audio numbers together, peak in dBFS and loudness in LUFS
- Noise-floor detection and its "the room is close behind your voice" note
- The preset toolbar, with save and load that are actually wired to storage

## This morning's fix, and why it changes what you should watch for

The Opus encoder was being built from the sample rate JJ Flex *asked* the
device for. The check of whether the device would actually accept that rate
happened later, on a different thread, and quietly changed the rate without
telling anything downstream.

So a microphone that refused 48 kHz got a stream running at its own rate,
feeding a codec still convinced it was 48 kHz. Nothing failed and nothing was
logged. The audio just came out roughly eight percent wrong, forever — which
sounds like a **regular, rhythmic stutter**, not like a broken feature. That is
precisely why it survived: it sounds like a network problem.

Three consequences for your testing:

1. **The rate now gets settled first.** Everything derived from it follows the
   device rather than the request.
2. **Opus has no 44.1 kHz mode at all.** So if Windows has a device at 44.1
   kHz, that device genuinely cannot carry transmit audio — this is not
   something the app can paper over today. The Microphone Check now says so up
   front. Making 44.1 kHz devices work is a separate piece of work, already on
   the list.
3. **A microphone that will not open now announces itself once**, keeps receive
   audio running, and stops retrying. Before, it said nothing at all: you would
   key up, transmit silence, and be told nothing was wrong.

## The single most valuable thing you can tell me

**What rate is your USB headset running at?**

Start the Microphone Check and read the first line. If it says only
"Microphone check running. Listening." you are at 48 kHz and clear. If it goes
on to mention 44.1 kHz, we have learned something important before you keyed
up, which is the entire point of the check existing.

Everything else in the test file can follow from there.

## Reporting back

**Report to the session on the dev box, not to a Claude on the laptop.** That
session made this morning's fix, knows what each test is actually probing, and
has the repo and the build in front of it — a laptop session would start from
this document alone, which is a summary rather than the reasoning. The laptop
is the device under test; it does not need to be where the work happens.

Both this file and the test file are on the NAS alongside the build, so the
round-trip is: annotate the test file in the `**** ` slots on the laptop, save
it back to the NAS folder, and the dev-box session reads it from there.

Or just come back to the desk and say "walk me through these" and I will run
them one at a time in chat — same tests, no file at all. That is usually
faster for anything fiddly.

If something crashes, the trace is at
`%AppData%\JJFlexRadio\JJFlexRadioTrace.txt`, and the loose `.pdb` on the NAS
next to the zip is what symbolicates it.

## Not in this build

- Transverter work is not fleshed out
- Noise gate and RNNoise are not here yet
- The two-numbers help page still opens with its draft marker, on purpose —
  it has not had a human pass
