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

---

## Design decisions

**Threshold: 256 MB, the low end of the ratified 250-500 MB band.** The
cost of a part gets paid twice — LZMA compression of the closed part on a
background worker, and Deflate of the current part into a crash bundle at
crash time. 256 MB of trace text lands around 15-25 MB deflated, which
fits inside the 45 MB upload budget; 500 MB would not reliably. It is a
constant with a comment, not a user setting, per the instructions. It is
settable at runtime (`Tracing.RotationThresholdBytes`) solely so a test
harness can force rotation without writing a quarter of a gigabyte.

**A custom listener rather than a listener swap — this was the load-bearing
call.** The obvious implementation (remove the TextWriterTraceListener,
rename the file, add a new one) leaves a window in which
`System.Diagnostics.Trace` has zero listeners. JJFlexWpf calls
`Trace.WriteLine` directly in dozens of places and never goes through
`Tracing.TraceLine`, so those lines would evaporate — silently, in the
evidence path, which is the worst place for silence.
`RotatingTraceListener` owns the FileStream itself and swaps it inside its
own lock, so the rotation is atomic from every writer's point of view.
Verified: 4 threads, 12,000 lines, 8 rotations, zero lost, with half the
lines written via raw `Trace.WriteLine`.

**Lock order.** The listener's `_sync` is innermost. `Trace`'s global lock
is always taken first (`Trace.WriteLine` → listener), never after. The
listener never calls back into `Tracing` tracing methods while holding
`_sync`; the part-closed callback only records a path and queues a task.
No deadlock path with the archive or viewer code.

**Live trace opens `FileShare.ReadWrite` (was `FileShare.None`).** This
started as a prerequisite for attaching the current part to a crash
bundle, but it is a fix in its own right: `File.Create` meant nothing —
not Notepad, not a screen reader, not the crash bundler — could read the
trace of the session currently running. That is half of why the
2026-08-07 bundle had no trace.

**Part filenames freeze the outcome tag at first rotation.** A session's
outcome legitimately changes as it runs (unknown at boot → success on
connect → clean_exit at the end). Taking the tag live would name one
session's parts `...-unknown-part-001.zip` and `...-clean_exit-part-003.zip`,
which do not sort next to each other — defeating the whole point of the
part naming. The frozen tag is a browsing convenience; each part's
manifest entry still carries its true outcome, and the manifest is what
queries read. Part numbers are `D3` so part 100 sorts after part 099.

**Compression runs on a single serialized background chain.** LZMA on a
256 MB text file is minutes of CPU, and a marathon session closes several
parts. Doing it inline would stall the writing thread; doing it in
parallel would put several LZMA compressors on the box at once during a
session that is already heavy. Clean exit waits up to 30s for the queue
to drain, bounded so exit never hangs.

**Retention coherence via shared boot_time.** Every part of a session
carries the session's boot time, so `PruneOlderThan` ages a whole chain
out as a unit — no orphans, no partial chains. Sizes are per part, so
nothing double-counts. The 2 GB crash-dump cap is untouched and stays
separate, as instructed.

**Orphan parts get adopted at next boot.** If the app dies between a
rotation and its background compression, the plain-text part survives but
has no manifest entry, and the 24h plain-text sweep would eventually
delete unread evidence. `ArchiveLeftoverTraceChains` runs before the
sweep, groups leftovers by the boot stamp in their names, and archives
them as one killed chain — with the still-present live trace adopted as
that chain's final part. `SessionArchive.IsSourceArchived` (backed by a
new `source_name` manifest field) makes it idempotent across boots.

**Known limit, accepted:** a chain adopted at boot gets a fresh
`session_id`, because the original was never persisted. Grouping still
works on `boot_time` plus the shared archive stem, which is what a human
browsing the folder uses. Persisting session ids across process death was
not worth a sidecar file for this case.

**Two bundles, not one.** The LOCAL bundle keeps everything including the
dump — it is the complete evidence and it costs nothing to keep. The
UPLOAD copy is built to a budget in priority order (report text → current
trace tail → previous part → archived traces while they fit → manifest,
which has reserved headroom so it always fits). This is what makes
"report text and trace tail ALWAYS upload" true by construction rather
than by hope. The reduced copy is deleted after the attempt so the Errors
folder doesn't hold two copies of the same evidence.

**Server limit: hardcoded 45 MB, cited, not guessed.** The receiver's real
limit is 50 MB, enforced twice — nginx `client_max_body_size 50M` and a
FastAPI-layer check, per `docs/planning/active/rarbox-claude-F3-G-briefing.md`
and `rarbox-setup-runbook-for-claude.md` section F5. There is no endpoint
that reports it (`/healthz` returns status only), so the instructions'
fallback applies: a conservative constant with a comment. 5 MB of headroom
covers multipart framing and the Cloudflare proxy in front. **If the
receiver's limit moves, `UploadMaxBytes` has to move with it** — there is
no negotiation.

**The failure dialog, found.** "Couldn't save a stream of that size" is
not our string. `Tracing.ErrMessageTrace(ex)` defaults to `msg:true`, and
that overload ends in `MessageBox.Show(ex.Message, "Exception")` — raw
framework text in a modal box. Every trace-archive failure path called it.
Archiving an 11.7 GB trace throws, so the user got a bare size message
*and* `ArchiveSession` returned null, which meant the source was never
renamed out of the way — one call explains both the dialog and the
missing trace. Trace-subsystem housekeeping now uses `ErrTraceOnly`: the
error is still recorded in full with its stack trace, it just stops
popping a modal nobody can act on. Size outcomes in the upload path speak
plainly instead, leading with what the user got.

**What was deliberately NOT changed.** `GetConfigInfo`'s settings-folder
failure keeps its modal — that is a real startup failure the user must
see. `TraceAdmin` / the Archive Browser keep theirs — those are the trace
UI surface, explicitly out of scope per the constraints. Trace format,
`PruneCrashReports` behaviour, and Operations → Tracing are untouched.

**One scope call worth flagging:** `DebugInfo.GetDebugInfo` (Send Debug
Info) had no `Try/Catch` at all and zips whatever is in AppData — the same
size exposure, in bundle-assembly code, so it was treated as in scope. It
now traces the real exception, speaks, and shows text the user can act on.
Its residual risk is noted below.

**Test helper: deleted by construction.** Two harnesses were built in the
session scratchpad, not the repo — one driving `JJTrace` directly (28
checks: rotation, no lost lines, part naming, manifest, retention), one
loading the compiled `jjflexible.dll` by reflection to exercise the real
`CrashReporter` bundle helpers (24 checks: live-file attach, tail
truncation, upload reduction, manifest contents). Nothing to delete from
the tree and no debug flag left behind.

### Follow-ups (not blocking, not done here)

- `DebugInfo.GetDebugInfo` zips all of `BaseConfigDir`, which now includes
  the 30-day trace archive. Rotation caps the single biggest contributor,
  but the archive directory can still be large. Bounding it needs a
  size-aware `ZipUtils.AddDirectoryToArchive`, which is shared code no
  track owns — route through the orchestrator.
- `JJTraceListener.cs` and `Tracing - Copy.cs` in `JJTrace/` are dead
  (the former compiled but unreferenced, the latter not compiled at all).
  `RotatingTraceListener` supersedes the former. Left alone to keep this
  diff to the change at hand.
