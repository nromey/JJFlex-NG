# Sprint 33 Track G — #140, the TX stream is opened without declaring compression

**Worktree:** `C:\dev\jjflex-33g` · **Branch:** `sprint33/track-g`
**Plan:** `docs/planning/active/barefoot-harness-pileup.md`
**Merges into Track A. HIGHEST VALUE TRACK IN THE SPRINT — read why.**

Build your own worktree only:
`dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal`

---

## Why this is the headline

**PC-audio transmit has never worked in modern JJ Flexible.** Don reported it, an
entire arc of work chased it, and this branch is NAMED `honest-tx-audio` after
it. The client was proven clean all the way to the wire: correct devices, healthy
capture, correct Opus profile matching shipping SmartSDR, well-formed VITA
packets from the registered port to the radio, and the radio acknowledging the
stream as OPUS — while the radio's mic meter stayed pinned at the -120 floor.

Three independent witnesses — our source, a decompiled SmartSDR, and AetherSDR's
source — agreed our wire bytes were indistinguishable from working clients.

**Everything downstream was cleared. This is upstream of all of it.**

## The finding

In `FlexLib_API/FlexLib/Radio.cs`, the RX path was fixed properly and the TX path
never was:

- `RequestRXRemoteAudioStream()` at `:6514` is marked
  `[Obsolete("Use RequestRXRemoteAudioStream(bool isCompressed) to explicitly
  specifiy whether to use compression")]`.
- `RequestRXRemoteAudioStream(bool isCompressed)` sends
  `stream create type=remote_audio_rx compression=opus` or `compression=none`.
- **`RequestRemoteAudioTXStream()` at `:6534` sends bare
  `SendCommand("stream create type=remote_audio_tx")`.** No compression
  parameter. No obsolete marker. No overload. Nobody ever came back for it.

**And FlexLib's own protocol comment at `:3724` documents the format as
`type=<remote_audio_rx|remote_audio_tx> compression=<none|opus>`** — so the TX
stream accepts the parameter. It is simply never sent.

## The hypothesis to test

If the radio defaults an unspecified TX stream to `compression=none`, then we
open a raw-PCM stream and proceed to send Opus frames into it. The radio would be
interpreting Opus bytes as PCM samples — noise or nothing.

**That predicts exactly the observed symptom set:** the stream is created
successfully, the radio acknowledges it, our packets are well-formed and
correctly addressed, and the mic meter never moves. Every one of those is
consistent with a compression mismatch, and none of them points at the client.

**Test the hypothesis, do not assume it.** It is a strong candidate, not a
proven cause. Establish what the radio actually does with an unspecified TX
stream before declaring victory.

## What to do

1. **Verify the call path.** Find who calls `RequestRemoteAudioTXStream` in
   `Radios/` and JJFlexWpf, and confirm the Opus encoder is feeding that stream.
2. **Add the overload**, mirroring the RX shape exactly:
   `RequestRemoteAudioTXStream(bool isCompressed)` sending `compression=opus` or
   `compression=none`, with the parameterless version marked `[Obsolete]` in the
   same words. **Consistency with the RX pattern matters** — the next person to
   read this file should find one idiom, not two.
3. **This is a vendor-file change**, so follow `MIGRATION.md` conventions: mark
   it as a JJFlex patch with a comment saying what and why, the same way the
   `GetMeters()` patch was done, so a future FlexLib upgrade does not silently
   drop it.
4. **Test on the radio with Noel.** Key up with PC audio on and watch whether
   MicData tracks voice. This is the moment the whole arc has been waiting for.
5. **If it is NOT the cause**, say so clearly and record what the radio actually
   did with the unspecified stream. A negative result here is genuinely valuable
   — it eliminates the last upstream suspect and sends the hunt back to the wire
   capture with one fewer variable.

## Report upstream either way

**Even if it does not fix our symptom, this is a real FlexLib defect** and worth
reporting to Flex — the same as #137's unpadded amplifier handle. An API that
offers explicit compression on RX and silently defaults it on TX is a trap for
every client author.

## Coordinate — three tracks want the radio

Tracks C and D are also on the 8600. **Yours has priority when they conflict**,
because a build going to Don depends on this answer and theirs do not.

Noel is blind and at the keyboard. Full stop and ask before a run; full stop and
report after. One authorisation covers one run.

**NOEL AUTHORISED THE ENVELOPE, 2026-08-20: 1 watt, short keyings.** He was
offered a zero-watt option and chose 1 watt deliberately, so you have a real
transmit to work with. **No antenna is connected** — keep every keying short, and
do not key more than the question requires.

**Keying is required here and that is expected** — but no antenna is connected,
so keep power low and transmissions short. Plan the minimum set of keyings that
settles the question, ask once, and get them in a single run.

## House rules

- **No tables** in any output, report or doc. Prose or bullets. Screen reader first.
- Do not touch files outside your worktree.
- **Do not commit vendor-derivative material.** Decompiled SmartSDR output and
  extracted firmware are read-only research and never enter git — describe
  findings in your own words.

## Commits

`Sprint 33 Track G: <description>`.

## Completion report

State: whether the hypothesis held; what the radio actually does with a TX stream
that does not declare compression; whether MicData moved on a live keying; the
exact patch and how it is marked for MIGRATION.md; and your recommendation on
reporting it upstream.

---

## AUTHORISATION IS BROKERED — do NOT ask Noel directly

**Decided by Noel, 2026-08-20.** Five tracks want either the radio or the live
desktop, and five agents interrupting him independently would be worse than the
collision the handshake exists to prevent.

**So: when you are ready for a run that needs the radio or drives the UI, STOP
and report "ready for a radio run" (or "ready for a UI run") to the orchestrator,
with exactly what you intend to do and roughly how long it takes.** Do not ask
Noel. Do not proceed on your own initiative.

The orchestrator batches ready tracks, asks Noel once, runs them back to back,
and reports done. You will be told when your run is authorised and when it is
over.

**Priority when tracks contend for the 8600: G first** — a build going to Don
depends on its answer — then C, then D, then K.

**While you wait, keep working.** Do everything that does not need hardware:
build the harness, write the code, reason it through. Arrive at your run with the
maximum settled in advance, because run time is the scarce resource, not compute.
