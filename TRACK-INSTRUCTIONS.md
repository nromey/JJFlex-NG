# QB Track K — Trace rotation + crash-bundle size policy

**Recommended model: Opus.** The design is ratified and spec-shaped
(2026-08-07, audio-workshop plan section 4b). If a genuine fork appears,
pick the conservative option and flag it in your completion report.

## Context

One of the 2026-08-07 queue-burn tracks (plan:
`docs/planning/active/nightowl-pileup-ragchew.md`; queue: Track K
section). JJ Flex is a screen-reader-first FlexRadio client. Driver: a
marathon live session grew the ACTIVE `JJFlexRadioTrace.txt` to 11.7 GB —
boot maintenance prunes archives but nothing rotates or caps a live
trace mid-session — and the day's crash bundle is missing its trace
precisely because attaching an 11.7 GB file was impossible. Noel also
saw an unhelpful "couldn't save a stream of that size" dialog, most
plausibly the ~500 MB bundle's upload rejection.

Read first: the trace subsystem — `JJTrace\Tracing.cs`, the archive boot
maintenance (TraceArchiveBootMaintenance), SaveCrash / crash-bundle
assembly, and the bundle upload path. Also `memory`-adjacent context in
the queue's Track K entry.

## Work items (the ratified design)

1. **Size-based live-trace rotation.** At a threshold (~250-500 MB —
   pick within that band, make it a constant with a comment, not a user
   setting for v1): close the active trace file, zip it into the archive
   as a SESSION PART (naming must make parts of one session sort
   together and read as a sequence — e.g. the existing archive name plus
   `part-NN`), and start a fresh live file with a spoken-free, seamless
   handover (tracing must not lose lines across the boundary; a one-line
   "trace continues from part NN" header in the new file is the
   breadcrumb). Long sessions become chains of parts; nothing ever needs
   splitting after the fact.
2. **Retention coherence.** Parts participate in the existing archive
   retention (30-day LZMA window, plain-text 24h convenience copies per
   the existing behavior) without double-counting or orphaning; the 2 GB
   crash-dump cap logic stays separate.
3. **Crash bundles attach the CURRENT part** (the tail — the evidence
   that matters), bounded by construction. If the crash happens moments
   after a rotation, attach the previous part too when it fits the size
   policy; say in the bundle manifest which parts are included and which
   exist but were withheld for size.
4. **Upload size policy.** Report text + trace tail ALWAYS upload; the
   full memory dump only when under the server limit; otherwise the dump
   is HELD LOCALLY and the user hears an honest "your report was sent;
   the large crash file is saved on this computer if support asks for
   it" — never a raw failure dialog. Find the real server limit from the
   receiver config (rarbox FastAPI receiver) rather than guessing;
   hardcode a conservative constant with a comment if it can't be
   queried.
5. **The failure dialog fix.** Whatever produced "couldn't save a stream
   of that size" gets found and replaced with the honest message above —
   speech and dialog text both. Errors never suppress-key.

## Constraints

- Rotation must be safe mid-write under the tracing lock discipline
  Tracing.cs already uses — no lost lines, no torn lines, no deadlock
  with the trace-viewer/archive paths.
- Do not change trace FORMAT or the user-facing Operations → Tracing
  controls (Track A/B territory if ever); this is plumbing.
- Crash-dump pruning (PruneCrashReports) behavior stays as shipped
  except where bundle-part attachment touches it.
- Test with a synthetic fast-growing trace (debug helper that spams
  lines) rather than waiting for an organic 500 MB session; delete the
  helper before completion or gate it behind a debug flag.

## Ownership boundaries

- Yours: `JJTrace\Tracing.cs`, archive/boot maintenance, SaveCrash and
  bundle assembly, the upload path and its messaging.
- NOT yours: everything else. Zero overlap expected with any track; if
  a change wants to leave these files, route it through the orchestrator.

## Build & verify

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
powershell -Command "(Get-Item 'bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe').LastWriteTime"
```
Timestamp must be fresh. Verify: force a rotation with the synthetic
writer; confirm part naming, archive retention, a crash bundle built
mid-chain attaches the current part, and the over-limit path speaks the
honest message.

## Commit style

Commit after each work item: `QB Track K: <what changed>`. Push to
`origin` (never `upstream`). Report completion to Noel when done.
