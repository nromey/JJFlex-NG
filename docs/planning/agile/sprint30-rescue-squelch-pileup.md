# Sprint 30 — Rescue Squelch Pileup

Planned 2026-08-18. Base branch `honest-tx-audio` (all current work lives there; `main` is
behind it). Worktrees `../jjflex-30x`, branches `sprint30/track-x`.

## The theme

The app tells the truth about its state — on connect, in audio, in diagnostics, in help — and
says each true thing exactly once, in the right window.

That sentence is the acceptance test for every track. "Safe" and "silent" are not the same
word; a fallback that nobody is told about is a lie the app tells politely.

## How this plan was built, and why it shrank

Two passes produced it. The first decomposed the backlog into tracks. The second **audited all
95 tasks against the code at HEAD `972e1438`** — and found fourteen tasks marked pending were
already done, eight were half done with a precise remainder, and three had descriptions that
would actively mislead whoever picked them up.

The audit deleted a whole track. Track C (Preset Provenance) was to own #49, #50, #51, #68 and
#44; the first four all shipped during the 2026-08-17 ten-track merge and nobody updated the
store. What was left — #44 — moved to Track B.

The full audit lives at `docs/planning/agile/sprint30-task-audit.md`. Read it before reopening
any task this sprint does not cover; it says which descriptions are trustworthy and which are
stale, and the drift is concentrated exactly where the work has been heaviest.

## Classification: BUILDABLE vs PERCEIVABLE

Every item is one or the other. It is the day's hardest-won distinction and it drives the whole
sprint shape.

- **BUILDABLE** work can be verified by an agent — it compiles, a test passes, a number moves.
  It merges and gets tested once, at the end, in a single sitting.
- **PERCEIVABLE** work can only be verified by an operator's ear. No agent and no sighted
  reviewer can see the failure. It lives in exactly ONE track, which runs live with Noel driving
  NVDA, after the merge train.

The reason this matters: 2026-08-18 spent four iterations getting a single speech coalescer
right, and every hypothesis formed away from the ear was wrong. Speech timing does not survive
being built blind. So it is quarantined, not distributed.

## The tracks

Five background agents (A, B, D, E, G) run concurrently tonight. Track F runs live, later, on
the merged result. There is no Track C — see above. There is no Track H; the repo cleanup it
was to do is already done (11 worktrees removed, 50 branches deleted, 2026-08-18).

### Track A — Rescue Centre (opus) — BUILDABLE

Owns the whole connect experience:

- **Rescue-Centre Home** — a limited Home page when no radio is connected, offering only what
  works offline. Supersedes #90 (Audio Workshop tab-into-controls with no radio) by construction:
  gating happens at the page, not per control. Note the audit's correction — #90 is only HALF
  fixed in code (five checkboxes gated; the value controls still speak confident lies with no
  rig), so A inherits a real remainder, not a clean slate.
- **Licence gating** — Plus-gated features either work or explain themselves. No silent absences.
- **Registration warning on local connect**, with the local-only offer.
- **#85** — a local connect must not announce SmartLink activity it never asked for. The audit
  moved the prime suspect: not `AnnounceLoadedState`, but the auto-started remote pass
  (`AutoStartRemote` at `RigSelectorDialog.xaml.cs:649-656` and `StartRemoteFlow` at 1961-1968,
  both ungated and speaking literal SmartLink language).
- **#79** — learn the connection path from a trend, never overwrite a choice. Accelerant the task
  does not know about: `Radios\ConnectionHistory.cs` already records a 10-entry per-radio ring of
  timestamped path/outcome/duration. The substrate exists; nothing reads it.

**Why one track:** every item is the same experience. Splitting it would put two agents inside
`MainWindow.xaml.cs` and `RigSelectorDialog.xaml.cs` simultaneously — the worst collision
available. One agent, serialized internally.

**Why opus:** three silent-failure minefields. A focus that lands nowhere is invisible in a diff
and catastrophic to the user. Licence gating fails silently in both directions. #79's contract —
learn from a trend, never overwrite a choice — has a failure mode you cannot see in a diff either.

### Track B — Clean Signal (fable) — BUILDABLE

Owns the sound path and the mic-profile design pass:

- **#12** — `paWinWasapiAutoConvert` for the 44.1 kHz shared-mode refusal. Reframed by the audit:
  WASAPI is now the DEFAULT host API, so this sits on the default path rather than an opt-in one.
  But the rate-negotiation ladder (`Audio.cs:236`) may already absorb it. Determine that first;
  only bind the struct if a 44.1-locked device still fails to open.
- **#29** — tone monitor clicks, a 44.1 provider on a 48 kHz path. The statusFlags
  instrumentation landed, so the next repro will finally say whether PortAudio saw glitches.
- **#17** — why the decoded PC-audio stream arrives quiet. Sharper lead from the audit: a stale
  comment claims the Opus path bypasses FlexLib's RXGain scalar, and 330 lines later the code
  sets `RXGain = 50` on that same channel. Check what FlexLib does with that scalar BEFORE
  touching anything else. Coupled default to remember: if the source arrives hotter, the existing
  +12 dB default becomes too loud in the same release.
- **#44 + #94** — mic profiles bound to the device, and the ownership question. **Design pass
  plus PC-side implementation only.** See the fence below.

**Why fable:** concrete, measurable engineering. Not sonnet, because a wrong fix here produces
subtly degraded audio that compiles clean. Not opus, because #17's verification is
instrumentation, not intuition — an agent can measure RMS on a decoded stream.

**The #94 fence, added after the plan was first written.** Task #94 arrived 2026-08-18 and
established that radio ownership CANNOT be derived from SmartLink registration — Noel connected
to Margaret's radio using Margaret's account, so a registration test would have called him the
owner. Ownership has to be a per-radio flag the operator sets. That flag lives in
`Radios\RadioConfig.cs`, which is Track A's file this sprint, and the recommended design is not
ratified. So Track B:

- writes the design doc and routes the open questions to `docs/planning/for-noel/`,
- implements the PC-side half of #44 only — the operator/rig split inside `AudioChainPreset.cs`,
- does NOT touch `RadioConfig.cs`, does NOT implement the ownership flag, and does NOT apply the
  `diag/don-audio-708` mic-profile auto-select (it writes to shared radio state on what may be a
  guest connection — that is precisely what the ownership answer gates).

### Track D — Front Door Diagnostics (opus) — BUILDABLE

Owns the reporting pipeline, which is the tool we will use to debug everything else:

- **#18's real remainder** — not the dialog polish. The ratified `diagnostic-log-surface.md`
  design retires that dialog, and #26's answers unblocked it (capture chord `Ctrl+J, Ctrl+D`,
  default ON, export-on-stop, `KeepDailyTraceLogs` retired). Build the design.
- **#78** — offer the trace at the moment something fails, folded in here.
- **#92** — 2.2 GB in AppData with no retention policy, 1.8 GB of it crash dumps. The design
  tension must not be resolved by pruning hard: the crash reporter's whole value is having the
  dump when support asks. Shape is "keep the most recent N, never delete an unsubmitted one".
  Manual controls belong in the same surface as the tracing explanation.
- **The fictional `_isTracing` state** — `TraceAdminDialog` initializes it false on every open and
  `NativeMenuBar.cs:1648-1665` never passes live `Tracing.On`, so opening mid-trace announces
  "Start tracing" for a trace already running, and pressing it restarts to a new file. Fix it even
  if the dialog is being retired; it survives at least one more release.
- **The duplicate version assembler** — `AboutDialog.xaml.cs:202-229` re-derives FlexLib's version
  independently, the exact failure `DiagnosticSnapshot`'s own doc comment forbids. A small, exact
  instance of the project's dominant defect class, in the component built to fight it.

**Why opus:** #78 is failure-moment UX where every mistake is silent. An offer that fires at the
wrong moment trains the user to dismiss it; one that fails to fire is worse than absent.

**Hard scope fence:** D does NOT edit `MainWindow.xaml.cs` — that file is Track A's for the
duration. Where a hook belongs in A's code, D exposes the event from its own layer and files a
one-line wiring note in its report.

### Track E — Help Where Your Hands Are (fable) — BUILDABLE

Owns help, and it has a blocking first item:

- **#91 FIRST, and nothing else until it lands.** Ctrl+F1 works, but the explanation is ALSO
  announced when you simply tab onto the field, because NVDA reads
  `AutomationProperties.HelpText` as the control's description on focus. The 2026-08-18 change
  moved seventeen long explanations out of `AutomationProperties.Name` into `HelpText` to stop
  them being recited on every focus change — and they are still recited on every focus change.
  Same words, same moment, same cost. The fix is a custom attached property UIA never surfaces
  (`JJFlexHelp.Text`), read only by the Ctrl+F1 handler, with `HelpText` kept as a second source.
  **Building coverage on HelpText would spread the defect across the app** — every control
  gaining an explanation would gain a longer focus announcement at the same time.
- **#84 coverage**, after #91. Currently Settings-only: 15 HelpText attributes plus 5 mic-area
  calls in the Workshop. Also soften the empty case — "No extra explanation for this control"
  currently fires nearly everywhere and reads as broken rather than as "nothing here".
- **#73** — the DSP controls finally explain what they do and how to set them. Rides #91's
  mechanism. The prose can be written in parallel with #91.
- **#39 + #43 merged** — build the per-category earcon controls the help already promises, and
  make `audio-earcon-control.md` true. Reality has drifted FURTHER from the page than the task
  says: line 22's "Settings > Audio > Earcons" path does not exist, per-category controls exist
  nowhere, `EarconPlayer.cs` has no enum at all (~60 flat static methods, one global gate), and
  line 11's "lasts for your current session" is wrong twice over — quick-mute persists via an
  immediate config save AND shutdown capture. Hidden second decision: **should a mute silently
  outlive the session?** That one needs Noel; route it, don't guess.
- **#55** — the master test list in for-noel format, whose first output is the script for this
  sprint's own final test pass.

**Why fable:** a large sweep whose judgement is prose a blind operator will hear hundreds of
times. Not sonnet — bad help text compiles, ships, and quietly teaches wrong mental models.

**E merges LAST by design.** Its final phase runs after A and D land: the orchestrator hands it
the list of newly arrived controls, and E sweeps help onto them. An E that merges early has done
half its job.

**Help-doc monopoly:** for this sprint, `docs\help\md\` belongs to E alone. A, B and D ship doc
content as notes in their reports; E integrates. Four potential doc conflicts become zero.

### Track G — Rigmeter Unbolted (sonnet) — BUILDABLE

- **#41** — blame-based provenance. The queue's "NOT started" is wrong: an `authors` subcommand
  ships. What is missing is load-bearing — the blame call passes neither `-w` nor `-C`, so
  reformats and moves reassign lines to whoever touched them last, and this codebase had a
  whole-tree .NET 10 migration. Add `-w -C` (consider `-C -C`), then re-check the numbers before
  anyone quotes them.
- **#42** — extract rigmeter into its own repository at `C:\dev\rigmeter`.

**Why sonnet:** well-specified, cheap to verify, and its failure mode is loud and harmless — a
stats tool disagreeing with itself. The NAS snapshot path and JSON format must not change; the
time series depends on them.

**Zero collision with any other track**, by construction. It fills a concurrency slot with
something that can never break the merge train.

### Track F — The Operator's Ear (opus, live Model A session) — PERCEIVABLE

Runs LAST, live, on a fresh branch off the integrated result, with Noel driving NVDA.

- **#58** — CW announce fires on slice population. The garble half is FIXED (single-reader
  Channel FIFO). What remains: the connect-storm — FlexLib's initial property sync raises
  DemodMode changes per slice during connect, which is how four announcements happened. Under the
  new serialized player that would now play four announcements IN FULL rather than garbling them,
  which is arguably worse. Needs one radio session to confirm. Also: the player completes on
  computed duration plus 50 ms rather than observing the mixer drain, and the 1500 ms pre-teardown
  window is shorter than a 15 WPM farewell, so a slow 73 can still lose its tail.
- **The chatty connection-path combo** in the radio picker.
- **#70** — repeat-last-message becomes a short history. `_lastMessage` is still a single string;
  the coalescer's `_lastByKey` is per-key dedup state cleared on urgent flush, unusable as history.
- **#89, optional and protocol-bound.** Four failed attempts is a stop signal, not a challenge.
  Protocol: establish the known-good commit or prove none exists; instrument focus events so the
  failure is observed, not guessed; ONE attempt, verified by NVDA on the spot; on failure write
  findings and stop. **Noel may veto the attempt entirely.** One cheap piece of evidence available
  first: temporarily start the `IdentityExpander` expanded and press the key — that discriminates
  the leading suspect in one build without touching navigation code.

**Speech-core quarantine:** F is the ONLY track permitted to edit `Radios\Speech\*`,
`ScreenReaderOutput.cs`, or announcement timing anywhere. Every other track that wants an
announcement changed writes it in its report instead.

## What is deferred, and what would unblock it

- **#93 (and the ~44 connect-cluster QUEUE conversions).** The survey that classified them QUEUE
  did not know that a screen reader flushes its queue on any window change, so it is evidence of
  nothing across window boundaries. Commit `972e1438` records the disconnect announcement being
  tried as Interrupt, then Queue, then Interrupt again — the operator heard the new window's title
  and nothing else, every time. The pattern that works is handing the utterance to the ARRIVING
  window (fold it into the title, as `PendingDisconnectLead` does). **Unblocked by:** a re-survey
  applying that rule, which should wait until Rescue-Centre Home lands, because Rescue Home changes
  which window boundaries exist during connect. Doing it now means doing it twice.
- **#65 — externalize strings to JSON.** Would collide with A, D, E and F simultaneously. Also
  gets cheaper after this sprint, because #71 and #76 settled wording that would otherwise be
  externalized and immediately changed. Solo sprint, off the merged result. The partitioned-store
  design is already recorded in the task, including the precision that partitioning does not speed
  up lookup once loaded — the argument is about what loads when.
- **#83 — the five kept warning categories, 486 sites.** Hundreds of mechanical edits across the
  same files five tracks are editing is merge poison, and S2486 (empty catch) fixes are behaviour
  changes wearing a hygiene costume. Solo hygiene sprint.
- **#57 — bandwidth adaptation.** Split by the audit: the selectable Opus TX rate SHIPPED; the
  low-res DAX IQ half has zero app-side code and is a fresh build. Its verification is a WAN test
  at a remote station, which cannot happen in one sitting.
- **#10, #27, #59, #95 — bench-gated.** #56 is not a track, it is the unblock: a scheduled bench
  day at the 8600. #59 rides along (the audit exonerated the app — nothing in JJ Flex creates a
  slice on connect — so the check is "are four slices resident BEFORE JJ Flex connects?", ten
  minutes at the bench, hours of guessing from a chair). #95 (register/unregister end to end) joins
  them, and carries its own hazard worth testing deliberately: if SmartLink registration is
  exclusive, registering a friend's radio would silently EVICT them.
- **#21 — orphan-process field test.** Not development; a line item in the final test pass, with a
  helper script so a blind operator can count processes without Task Manager spelunking.
- **#90 — superseded by Track A**, not deferred. Recorded so nobody builds per-control gating that
  the Rescue-Home design deletes.

## File collisions — every file two or more tracks touch

- **`JJFlexWpf\MainWindow.xaml.cs` — the hub, and the sprint's most dangerous file.** Track A
  exclusively during the parallel phase. D is fenced out. E adds help to rescue-Home controls only
  in its post-merge phase. F touches announcements in it only during the live session. Any track
  that finds itself "just quickly" editing this file stops and reports.
- **`Radios\FlexBase.cs` — A and B, different regions.** A: REM ON plumbing, discovery/name.
  B: the RX/Opus decode path for #17. B merges first (surgical); A resolves at its own merge —
  A runs on opus and is the right party to do that resolution.
- **`Radios\RadioConfig.cs` — A only.** B is fenced out by the #94 clause above.
- **`JJFlexWpf\Dialogs\RigSelectorDialog.xaml` / `.xaml.cs` — A and F.** Resolved by sequencing:
  F starts after A merges. No concurrent editing ever happens.
- **`JJFlexWpf\Dialogs\SettingsDialog.xaml` and partial classes — A and E**, the highest textual
  conflict risk. A writes its own help inline, copying the existing pattern rather than waiting
  for E. A merges first; E's final phase sweeps and resolves.
- **`JJFlexWpf\Dialogs\AudioWorkshopDialog.xaml` / `.xaml.cs` — B and E.** B merges first.
- **`globals.vb` — D (trace flags), possibly E (earcon defaults).** Single-line scale, named so
  neither is surprised.
- **`JJFlexWpf\KeyCommands.cs` — E (#91's handler) and F (repeat history).** F is sequenced last.
  If ANY track adds or changes a binding, the CLAUDE.md keyboard audit applies in full — including
  PRESS THE KEY on a real build. An Alt+L binding shipped completely dead on 2026-08-13 because it
  compiled and reviewed clean and was simply never pressed.
- **`docs\help\md\*` — E alone**, per the monopoly rule.
- **`docs\CHANGELOG.md` — no track edits it.** Tracks put changelog lines in their reports; the
  orchestrator writes it once at sprint close, in the house voice.

## Merge order, with reasoning

Every merge is followed by a **clean build** and a symbol-presence check. A conflict-free merge
proves nothing — 2026-08-17 landed two tracks with zero textual conflict and a broken build,
because one track moved a symbol another was told to reuse. Git cannot see that class of collision.

1. **G (rigmeter)** — touches nothing anyone else touches; deleting `tools\rigmeter\` early means
   no later track can accidentally depend on it.
2. **B (clean signal)** — small and surgical; landing its `FlexBase` change early leaves the one
   hard resolution to A, the strongest reasoner in the fleet.
3. **A (rescue centre)** — the biggest diff and the hub file. Everything downstream needs it landed.
4. **D (diagnostics)** — after A, so its failure hooks are wired against the connect flow as A
   actually reshaped it, not as it used to be.
5. **E (help)** — last buildable by design; its final phase documents what A and D just landed.
6. **F (the live session)** — not part of the train. It is what the train was clearing the track for.

Then: integration back to `honest-tx-audio`, clean Release builds both arches, installer
verification.

## The single test pass — one sitting, NVDA, final build

Track E delivers this as a guided for-noel session script (#55's first output). The F live session
happens FIRST in the sitting — it produces the final build — then the acceptance pass runs on it.

- **Cold start, no radio.** Rescue Home arrives; its title carries the not-connected state; focus
  lands somewhere real; Tab cycles exactly the offered buttons and nothing else. Audio Workshop
  from the rescue page: no radio controls reachable, including the VALUE controls (#90's real
  remainder). Ctrl+F1 across Settings, Workshop and the diagnostics surface: help is present, and
  tabbing onto those same controls does NOT recite it (that is #91's acceptance test). About: every
  version read aloud and checked — FlexLib 4.2.20.41343, Opus 1.6.1, PortAudio revision `a880212`
  (the string will still say 19.7.0-devel; the revision is the honest part). Open the diagnostics
  surface cold: can you say, without coaching, what it does and what to press when something
  breaks? That sentence is #18's acceptance test.
- **Process hygiene.** Ten launch/exit cycles, then the process-count check reads zero strays (#21).
- **Local connect, the 8600.** Nothing about SmartLink is announced (#85). The local-only offer
  appears once, reads clearly, and the choice sticks. Full Home replaces the rescue page. Plus-gated
  features each either work or explain themselves. REM ON toggles and speaks its state.
- **Sound path.** Default device says what it is and which host API nominated it. PC audio connects
  on the 44.1 kHz device that used to refuse (#12). Tone monitor: no clicks (#29). PC-audio
  loudness sits at a sane level against local audio — B's report states the measured before/after,
  so ear and numbers can be compared (#17).
- **Presets and profiles.** Corrupt preset file: the app SAYS it fell back. Export, re-import,
  confirm schema version, TX EQ, recorded input device. Mic profile with a different device
  attached: the mismatch is announced, not absorbed (#44).
- **Diagnostics at the moment of failure.** Force a failure per D's scripted repro: the offer
  appears in its own titled window, is Escape-closable, produces a usable trace (#78). Check the
  AppData total is reported honestly and the retention controls work (#92).
- **The ear items on the final build.** CW keying not garbled on connect (#58). One useful utterance
  per connection-path option. Repeat-last walks back through history and returns (#70).
- **Installer.** Fresh install on the never-had-.NET VM; launch to the rescue Home.

## Open questions for the owner

Batched here rather than asked mid-flight. Tracks proceed on the stated assumption and report.

- **Rescue Home scope** — is the button set exactly Connect, Settings, Audio Workshop, Help, Exit?
  And does the rescue page appear when a connected radio is LOST mid-session, or only before first
  connect? *Assumption taken: startup only. The mid-session case is a window transition during
  operation, with all the #86 window-flush lessons applying, and it materially changes A's design.*
- **Licence gating UX** — hidden, or disabled-with-reason via the Feature Availability pattern?
  *Assumption: disabled-with-reason, per CLAUDE.md's accessibility guidance.* And on a purely local
  connect with no SmartLink account, does `FeatureLicense` populate at all — if it cannot, what
  should Plus-gated features say?
- **Local-only** — a per-radio app setting keyed by serial, or something radio-side? *Assumption:
  app-side.*
- **#79's threshold** — how many consecutive successful connects constitute a trend worth
  prefilling? *Proposal: three, and the learned value only ever prefills; a stored explicit choice
  always wins.*
- **Earcon authority** — is `audio-earcon-control.md` the spec to build to, or does the promised
  category list need revising first? *Assumption taken: the doc is SUSPECT. E verifies each
  promised control against reality and reports rather than building faithfully to a wrong list.*
- **Quick-mute persistence** — should an earcon mute silently outlive the session? It does today,
  and the help page says it does not.
- **Mic-profile ownership (#94)** — ratify or amend: ownership is a per-radio flag the operator
  sets, seeded but never determined by registration; "save preset" and "write to the radio" get
  two different verbs; the `diag/don-audio-708` auto-select applies only on radios marked yours.
- **#70 sizing** — how deep a history (proposal: ten), and on what keys?
- **The bench day (#56)** — inside this sprint's window, or after? It gates #10, #27, #59 and #95,
  none of which block the sprint.
- **#89** — may Track F spend live-session minutes on the one protocol-bound attempt, or does it
  stay parked?
