# Track E — Device opening and rate policy

**Worktree:** `C:\dev\jjflex-e` · **Branch:** `bsr/track-e` · **Model:** Sonnet

**Read first:** `docs/planning/active/barefoot-splatter-ragchew.md`, section
"Track E — Device opening and rate policy" including "Confirmed at the bench
2026-08-16".

## Theme

Which device we open, at what rate, with how many channels. Four tasks that look
separate and are **one decision**.

## The work

**1. The host-API selector — the headline, and it DELETES complexity.**

All four host APIs are already enumerated: MME, DirectSound, WASAPI, WDM-KS
(WDM-KS hidden unless advanced, for a good recorded reason — kernel pins expose
raw hardware endpoints under pristine names, and two operators once selected a
pin to an unplugged jack and transmitted silence). Every physical device appears
**once per API**, and advanced mode appends the API name to the row.

**Today the picker FOLDS those duplicates — and that folding is what silently
chooses the API for you, landing on MME.** MME resamples transparently, so it
reports a tidy 48 kHz regardless of what the hardware is doing, which hides every
rate problem.

**So: add an audio-system selector (WASAPI default), filter the device list to
the chosen API, and DELETE the duplicate-folding rule.** Select the API first and
there are no duplicates left to fold. This is the standard DAW pattern and it is
a *smaller* picker than today's.

Honest trade to preserve in the UI wording: MME is most compatible and forgiving;
WASAPI tells the truth and occasionally refuses. `paWinWasapiAutoConvert` is what
softens that refusal — which is why these were always one decision.

**Decide and record:** one selector governing both input and output (DAW
convention, simpler) or two. Input and output are currently chosen separately.

**2. `Audio.Open` does not log which host API it opened.** It logs device name
and rate, and the name is identical across all four APIs. For a stream where the
API determines whether the rate is genuine or resampled, that is the fact worth
having. Add it.

**3. Mono capture.** The engine opens two channels and cannot upmix, so a mono
device is listed and tagged unusable. Confirmed from the operator's seat: an EVO8
mono endpoint refuses selection with "it needs a stereo device".

Fix: **open at the device's native channel count, duplicate mono to stereo in the
callback, walk half the buffer for mono.** `MicProbe` already does exactly this
duplication — copy the pattern, do not invent one.

**Priority is higher than it looks.** The available workaround is ganging two
interface inputs and panning both to centre, which requires owning a
multi-channel interface. **A single mono USB headset mic has no workaround at
all**, and mono devices are frequently somebody's only microphone.

**4. Two refusal messages, in two vocabularies, neither giving a reason.** The row
tag appends `" — mono, not usable yet"`; selection-time emits a separate "needs a
stereo device". Unify them and say *why*.

**5. Selectable Opus TX rate**, plus the low-resolution DAX IQ stream. Cheap now
that the rate is settled before the codec is built; every model already offers
24 kHz. This is the fallback for a constrained link.

**6. Re-measure before chasing.** "Decoded PC audio too quiet" and "tone monitor
clicks" both predate the rate-negotiation fix, which may have moved both.
**Measuring is the first step, not a preliminary to it.**

**7. ACC/BAL enumeration — VERIFIED ALREADY CORRECT, do not spend time on it.**
`FlexBase.MicSourceList` is `theRadio?.MicInputList?.ToList()` — we pass the
radio's own list through verbatim. If a source appears that the rig lacks, that
is the radio's claim. **Optional papercut:** a help note saying the list comes
from the radio.

## Bench facts you can rely on

- Nothing on the ms-02 runs at 44.1 kHz — every capture endpoint is 48 or 96.
- **96 kHz is the better test trigger than 44.1.** Opus supports 8/12/16/24/48,
  so 96 is equally unsupported and the EVO8 does it from its own control panel.
- Negotiation runs on the Opus TX path, **not** on the Microphone Check, which
  uses `MicProbe` — a separate opener.

## Papercuts you own

Wording and help papercuts in the device dialogs. **A track is not done until its
papercuts are done.**

## Rules

- **Reuse the symbols you find. If you conclude one should move or change
  signature, REPORT it rather than doing it.**
- Build: `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`
- Commit style: `Track E: <description>`.
- **Do not merge, do not push to main, do not touch other worktrees.**

## Done means

Builds clean. An audio-system selector exists with WASAPI as default and the
folding rule removed. `Audio.Open` logs the host API. A mono device is selectable
and works. Opus TX rate is selectable. You have reported the re-measured state of
the quiet-decode and click reports rather than assuming.
