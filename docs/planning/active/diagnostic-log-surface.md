# The Diagnostic Log Surface (breadcrumb-squelch-ragchew)

**Status:** BUILT — Sprint 30 Track D, 2026-08-18, branch `sprint30/track-d`.
**Read section 12 first.** It records what was built, what was decided where
this document was silent, where the implementation deliberately departs from the
text below, and three facts this document had wrong.

**Design date:** 2026-08-11. **Author:** trace-surface design pass, from Noel's
four findings at the radio 2026-08-11 (research-queue entry "THE TRACE/LOG
SURFACE"). **Design branch of record:** `design/trace-surface`.

This is the front door of the reporting pipeline. Crash reports, feedback
bundles and problem reports all carry the trace file as their payload. Noel —
a blind expert user who knows this codebase — could not tell what the current
dialog's controls did. Until an operator can confidently answer "what is being
recorded, at what detail, and where did it go," the pipeline is broken before
any backend exists.

---

## 1. What the code actually does today (read before touching anything)

The mental model the current UI implies — "tracing is a thing you start" — is
false three different ways. Facts, verified in this worktree 2026-08-11:

- **There is exactly ONE trace stream.** `JJTrace.Tracing` holds a single
  listener and a single global `TraceSwitch`. The "boot trace" and a manual
  trace are not two things running side by side; starting a manual trace
  silently *replaces* the always-running one as the destination. Any design
  promising two independent simultaneous traces is promising something the
  plumbing does not do and does not need to do.

- **The thing that actually runs has no UI.** `BootTrace` (`globals.vb:115`)
  is set unconditionally in `GetConfigInfo` (`globals.vb:863`):
  `BootTrace = (Not Debugger.IsAttached)`. Outside a debugger it is always
  true. It opens `%AppData%\JJFlexRadio\JJFlexRadioTrace.txt` at level Info,
  with rotation, per-session LZMA archiving, outcome tagging, 30-day archive
  retention and a 1-day plain-text window (`globals.vb:253` onward). No
  setting exists behind any of it.

- **The dialog Noel hit is the WPF `TraceAdminDialog`**
  (`JJFlexWpf/Dialogs/TraceAdminDialog.xaml`), opened from **Help → Tracing**
  in `NativeMenuBar.BuildHelpPopup` (`NativeMenuBar.cs:1605`). It is broken in
  four distinct ways:
  - Its `AutomationProperties.Name` is hardcoded `"Start or stop tracing"` and
    never updated, so a screen reader hears the same words in both states —
    the exact defect Noel reported. (The visual `Content` does flip
    Start/Stop; the accessible name does not.)
  - `_isTracing` initializes to `false` every time the dialog opens, ignoring
    the boot trace that is actually running. The dialog literally does not
    know tracing is on.
  - Its `StartTracing` lambda bypasses the session-archive layer: it never
    calls `ArchiveCurrentTraceSession` / `BeginNewTraceSession` and never sets
    `LastUserTraceFile`. Consequences: the boot trace file is closed without
    being archived, the leftover `JJFlexRadioTrace.txt` is found at next boot
    and **falsely tagged `killed`**, and the manual trace never gets a
    manifest entry at all — it is invisible to the Trace Browser and to any
    future bundle picker.
  - It defaults the manual file to `Documents\JJRadioTrace.txt` — outside the
    AppData ecosystem, so nothing rotates, archives, prunes or bundles it,
    and it still carries Jim's pre-rename "JJRadio" name.

- **The Trace Browser is unreachable.** The WinForms `TraceAdmin` form gained
  an "Archive Browser" tab in Sprint 29 Track H (find / filter / sort /
  View / Copy Path / Export / Delete / Prune — most of what a report needs).
  **Nothing in the codebase instantiates that form anymore.** Both Classic and
  Modern menus route Help → Tracing to the WPF dialog. Track H's test-matrix
  checklist is entirely unticked because no tester could ever have reached it.
  This design makes it reachable again; its checklist folds into this track's
  test plan.

- **There is a third, dead trace path.** `CurrentOp.KeepDailyTraceLogs`
  (`PersonalData.vb:152`, default False) drives `StartDailyTraceIfEnabled`
  (`globals.vb:368`), which on power-on replaces the boot trace with a
  date-stamped daily file. No UI has ever set that field. It is dead weight
  and this design retires it (section 8).

One more mechanical fact for the implementer: `Tracing.TraceLine(str)` (the
no-level overload) writes unconditionally whenever tracing is on. Level
filtering only applies to the `TraceLine(str, lvl)` overload. So the detail
setting shapes the leveled calls only; a later audit can migrate stragglers.
Not blocking.

---

## 2. Naming — the decision

**The user-facing name is "Diagnostics" for the surface and "the diagnostic
log" for the file.** Not "log" bare, not "logging," not "trace."

Reasoning, so this doesn't get relitigated: Noel is technically right that
these are logs — continuously running, level-filtered, read after the fact;
Jim's "trace" came from .NET's `TraceLevel` enum, which ordinary logging uses
all the time. But in amateur radio "log" means the QSO logbook, and JJFlex
already has a Logging feature, a Logging UI mode, and a Logging key scope.
A blind user hearing "log" in a menu will reach for contacts. "Diagnostic
log" is honest about what the thing is, cannot be confused with contacts, and
matches the already-ratified Sprint 29 **Settings → Diagnostics** tab
direction (Noel, 2026-05-02: keep the tab AND a menu item that deep-links to
it). "Diagnostics" alone names the surface; "diagnostic log" names the
artifact inside it.

### Strings that change (complete inventory)

- Help menu item **"Tracing"** (`NativeMenuBar.cs:1605`) — **removed**. A new
  **Tools → "Diagnostics"** item deep-links to Settings → Diagnostics via the
  existing `ShowSettingsDialog("Diagnostics")` pattern (same mechanism as
  "Configure Radio" → Radios tab). Tools is the Modern/Classic operations
  menu; there is no Operations menu in the native menu bar, which is why
  CLAUDE.md's old "Operations → Tracing" claim was wrong.
- WPF `TraceAdminDialog` — **retired entirely** (title "Select Trace Level",
  the state-blind "Start or stop tracing" button, the file-name box, the
  five-item level list all go away).
- WinForms `TraceAdmin` window — title changes from "Tracing" to **"Saved
  Diagnostic Logs"**; its "Tracing" tab is deleted; the "Archive Browser" tab
  content becomes the window's whole body. Tab strip goes away if the browser
  is the only content.
- New Settings tab: header **"Diagnostics"**, `AutomationProperties.Name`
  "Diagnostics, diagnostic log and problem reporting settings".
- All spoken announcements in this surface say "diagnostic log" / "detailed
  capture" (exact utterances in section 5).
- New help page `docs/help/md/diagnostic-log.md`; cross-references from
  `connection-troubleshooting.md` and `audio-troubleshooting.md`; changelog
  entry in user voice.

### Names that do NOT change

- Internal APIs: `JJTrace` namespace, `Tracing`, `Tracing.TraceLine`,
  `TraceLevel`, `TheSwitch`, `SessionArchive`, `TraceSession*`,
  `RotatingTraceListener`. Every call site keeps compiling untouched.
- Class/file names: `TraceAdmin.vb` and partials keep their names (the
  window *title* changes; the type does not).
- **On-disk file names**: `JJFlexRadioTrace.txt`, `JJFlexRadio2Trace.txt`,
  part/stamp naming, the `Traces\` archive tree, `manifest.json`. Testers
  know these names, the crash bundler and rotation code parse them, and
  CLAUDE.md's Trace File Location section stays true. The UI bridges the
  gap by *saying the path out loud* (section 4), not by renaming files.
- Planning docs and memory entries may keep saying "trace" internally.

---

## 3. The shape — one Settings tab, one browser window, no third dialog

**Settings → Diagnostics** is the configuration surface. The repurposed
**Saved Diagnostic Logs** window is the archive surface. The WPF
TraceAdminDialog is deleted, not renamed — every one of its behaviors is
either wrong (session bypass, state blindness) or absorbed (level, start/stop).

The operator's model, and the model every control reinforces:

> JJ Flex always keeps a diagnostic log of what it and you are doing. You can
> choose how much detail it records. When you are hunting a specific problem,
> you can start a **detailed capture**, reproduce the problem, and stop it —
> the capture becomes a saved session you can find and export. Nothing ever
> leaves your computer unless you send it yourself.

That resolves "one setting versus two": there is **one log** with a
**standing detail level**, and **capture is a temporary elevation of that same
log**, not a second thing. "The one that is always running" and "the one I
just started" are the same stream; the capture merely marks off a session at
maximum detail. This matches the single-listener plumbing exactly, so the UI
never promises what the code can't do.

### Settings → Diagnostics tab layout (in tab order)

**Status line (read-only text, first in tab order).** One sentence that
answers everything at a glance, e.g.:
"Diagnostic log is on at normal detail, running since 7:52 PM. No capture in
progress." Or: "Detailed capture in progress, started 8:14 PM." Or:
"Diagnostic log is off." Screen-reader reachable, refreshed on every state
change while the tab is open.

**Group: Diagnostic log**
- Checkbox **"Keep a diagnostic log (recommended)"** — default ON. This is
  the missing `BootTrace` control. Unchecking speaks a consequence, not just
  a state: "Diagnostic log off. If something goes wrong, JJ Flex will have no
  record to show you or the developer." Takes effect immediately (closes and
  archives the current session with a `clean_exit` outcome detail of "user
  turned diagnostic log off") and persists for the next launch.
- Radio pair **"Detail level"**: **"Normal (recommended)"** and
  **"Detailed"**. Exactly two choices — see section 6 for why the five-level
  list dies. Each choice carries one plain sentence of help text (also its
  `AccessibleDescription`):
  - Normal: "Records connections, errors, and the actions you take. You will
    never notice the cost."
  - Detailed: "Records nearly everything the app does. Files grow fast. Use
    it when the developer asks, or use a detailed capture instead."
- Static text + two buttons for **where the file lands, said out loud**
  (section 4): the path sentence, **"Copy log file path"**, and
  **"Open log folder"**.

**Group: Detailed capture**
- One button, state-honest in both `Content` and `AutomationProperties.Name`:
  idle → **"Start detailed capture"**; running → **"Stop detailed capture
  (started 8:14 PM)"**. No file picker, no level picker — see section 6.
- One sentence of static help: "A capture records everything at maximum
  detail while you reproduce a problem, then saves it as its own session in
  Saved Diagnostic Logs."

**Group: Saved diagnostic logs**
- Button **"Browse saved logs..."** — opens the repurposed WinForms browser
  window (this is what makes Track H reachable).
- Button **"Save a problem report bundle..."** — invokes the existing
  `DebugInfo.GetDebugInfo` collector (today reachable only through Command
  Finder as GatherDebug, with no default key). This is the interim one-act
  "get everything to the developer" path until the feedback dialog ships.
- Static text: the retention sentence — "Each session is compressed and kept
  for 30 days, then removed automatically. Yesterday's sessions stay readable
  as plain text for one day."
- Privacy note (verbatim, section 7).

### The Saved Diagnostic Logs window (repurposed `TraceAdmin`)

Keep everything Track H built: filter row (date range, outcome, text search),
sortable list, detail panel, View Trace / Copy Path / Export Selected /
Delete Selected / Prune Now, all speech-confirmed. Changes:

- Delete the "Tracing" tab (its function moved to Settings). With one tab
  left, remove the TabControl and promote the browser content to the form.
- Title and `Text` become "Saved Diagnostic Logs".
- Row/detail formatting moves toward the human phrasing the feedback picker
  will need (already close): "Tonight at 8:47 PM, about 12 minutes, ended
  normally". The manifest has boot time, duration and outcome; this is a
  formatting job, not new plumbing.
- Opened from Settings → Diagnostics and nowhere else. No separate menu item;
  two entrances to the same archive would recreate the discoverability mess
  this design is cleaning up. (The Tools → Diagnostics deep-link lands one
  button away.)

---

## 4. Where the file lands, said out loud

An operator asked for "the trace" must be able to produce it without sighted
help and without knowing `%AppData%` folklore. Three mechanisms, all cheap:

- The Diagnostics tab shows and speaks the real, resolved path — not the
  `%AppData%` template. Example text: "The live log is
  C:\Users\don\AppData\Roaming\JJFlexRadio\JJFlexRadioTrace.txt. Older
  sessions are in the Traces folder next to it." Instance 2+ automatically
  shows its own `JJFlexRadio2Trace.txt` because the text is generated from
  `BootTraceFileName`, not hardcoded.
- **"Copy log file path"** puts the live file's full path on the clipboard
  and speaks "Path copied." (The browser's Copy Path already does this for
  archived sessions.)
- **"Open log folder"** opens Explorer at `BaseConfigDir` with speech
  confirmation.
- Stopping a capture speaks where it went and offers the next act (section 5).

---

## 5. State-honest controls and exact utterances

Every control announces its state and every action speaks its outcome
(no-silent-keystrokes). The specific fixes:

- The capture button's `Content` AND accessible name change together per
  state. The Track H pattern in `TraceAdmin.Browser.vb` (every action speaks)
  is the house style; the WPF dialog's frozen `AutomationProperties.Name` is
  the anti-pattern being deleted.
- Start capture: speak "Detailed capture started. Reproduce the problem, then
  stop the capture from this button or the Diagnostics tab."
- Stop capture: speak "Capture saved: tonight at 8:14 PM, about 6 minutes."
  Then focus lands on an **"Export this capture..."** button (added next to
  the capture button, visible only right after a stop) so the common next act
  — get the file somewhere sendable — is one keystroke, not a browse through
  the archive. Export reuses the browser's Export code path.
- Toggling the log or its detail level: speak the new state ("Diagnostic log
  on, normal detail").
- The status line re-announces on tab entry, so opening Settings →
  Diagnostics always starts with orientation, never a control soup.
- If a capture is running when the app exits, it archives with the normal
  clean-exit path; if the app dies, the existing killed-session adoption at
  next boot preserves it. Nothing special to build — one more reason capture
  stays inside the standard session machinery.

Capture must also work without the Settings dialog open, because the dialog
may be part of the problem being captured:

- **Command Finder registration**: "Start detailed capture" / "Stop detailed
  capture" (one command, state-aware label), synonyms: capture, trace,
  diagnostic, record, bug, log. Also register "Diagnostics settings" and
  "Saved diagnostic logs".
- **Leader chord `Ctrl+J, Ctrl+D`** toggles capture with the same spoken
  confirmations. (Was `Ctrl+J, D` in the original draft; plain D is already
  bound to tuning speech debounce — see the answered question 1.) The leader
  layer is the ratified place for new commands and is currently underused
  (audio/DSP only). The keyboard audit applies: `keyboard-reference.md`,
  Command Finder metadata, F1 help, changelog.

---

## 6. Levels — explained once, then mostly removed

The five-item list ("Off, Error, Warning, Info, Verbose") dies. Decisions:

- **"Off" is not a level.** It was index 0 of the `TraceLevel` enum leaking
  into UI — "tracing on at level Off" is incoherent. On/off is the checkbox.
- **Error-only and Warning-only are not offered.** A user who picks them
  produces traces that cannot answer "what did the user do before the error"
  — exactly the reconstruction the pipeline needs. (Same call as the
  2026-04-28 Diagnostics-tab design: don't let users under-log themselves
  into unreportable bugs.)
- **Normal = Info** (today's boot default — behavior unchanged for everyone).
  **Detailed = Verbose.** Two words, one sentence of plain language each
  (section 3). Internally these map straight onto `TraceLevel`; the enum and
  every `TraceLine(..., lvl)` call site are untouched.
- **Capture has no level choice at all — it is always Verbose.** A capture
  exists to hand the developer maximum evidence; offering less is a trap.
  This is also exactly the semantics the feedback dialog's "detailed (mad
  man) trace" toggle needs, so the capture API is the mad-man implementation
  (section 9).
- "Independent levels for auto and manual," Noel's ask, lands as: the
  standing level is a **persisted setting**; the capture level is **fixed at
  maximum and scoped to the capture** — start saves the standing level,
  applies Verbose, begins a fresh session; stop archives the session,
  restores the standing level, and the always-on log keeps running. The
  operator gets the independence they asked for without two switches to
  misunderstand. (Today, stopping a manual trace turns tracing off entirely
  and the machine flies unrecorded until relaunch — that gap closes here.)

---

## 7. Privacy — what is in the file and what consent means

What a diagnostic log can contain (verified against a real trace during the
2026-04-28 investigation): the operator's callsign and operator names, QSO
partner callsigns from logging activity, the SmartLink account email,
truncated JWT fragments and Auth0 PKCE values, WAN and LAN IP addresses,
radio serial numbers and station nicknames, frequencies and settings —
in short, who you are, where you connect from, and what your station did.

Rules this surface enforces:

- **Nothing leaves the machine, ever, without a per-event user action.**
  There is no auto-send in this design and none may be added to it
  (`project_no_silent_phone_home.md` governs). The log is local; the archive
  is local; retention (30-day archive, 1-day plain text) is the privacy
  floor.
- The tab carries this note verbatim: *"The diagnostic log stays on this
  computer. It can include your callsign, contacted callsigns, your SmartLink
  email, network addresses, and your radio's serial number. When you export a
  session or save a problem report, you are choosing to share whatever that
  session recorded — review it first if that matters to you. Nothing is ever
  sent automatically."*
- Export and bundle actions speak size before writing ("Two sessions, about
  3 megabytes") so an upload never surprises anyone — the same rule the
  feedback picker memo sets.
- Redacted export (strip emails, JWTs, IPs, callsigns) stays Sprint 30+ and
  is out of scope here; the manifest already carries the metadata needed to
  build it later without re-parsing traces.

---

## 8. Repairs and removals folded into the implementation track

These are correctness items, not polish; they ride along because the track is
already in every one of these files:

1. **Delete `TraceAdminDialog.xaml`/`.xaml.cs`** and its `BuildHelpPopup`
   wiring. This alone fixes: the frozen accessible name, the state-blind
   `_isTracing`, the session-archive bypass, the false `killed` tag on the
   next boot's leftover sweep, and the invisible manual traces.
2. **All start/stop/redirect flows go through the session-aware path**
   (`ArchiveCurrentTraceSession` → `Tracing` changes →
   `BeginNewTraceSession`), which `TraceAdmin.vb:56-85` already models
   correctly. Nothing may ever flip `Tracing.On` without settling the
   session first.
3. **Retire `KeepDailyTraceLogs` and `StartDailyTraceIfEnabled`.** No UI ever
   set the field (default False), so migration risk is nil; the always-on
   log with per-session archiving IS the daily-trace idea, done properly.
   Keep `ArchiveOldDailyTraces`' file-pattern sweep for one release so any
   hand-edited straggler's old daily files still get archived, then remove.
   (Open question 4 gives Noel the veto.)
4. **New persisted config, app-level not per-operator:**
   `DiagnosticsConfigV1` (XML, `BaseConfigDir\diagnosticsConfigV1.xml`,
   same serialization pattern as `AutoConnectConfig`): `KeepDiagnosticLog`
   (bool, default true), `DetailLevel` (string, "Normal"/"Detailed", default
   "Normal"). App-level because `GetConfigInfo` opens the boot trace before
   any operator is selected — a per-operator setting cannot govern boot.
   Absent file = defaults = exactly today's behavior, so first-run and
   upgrade are no-ops.
5. `GetConfigInfo` reads the config where `BootTrace = (Not
   Debugger.IsAttached)` sits today; the debugger guard remains an AND-term
   so attach-time behavior is unchanged.
6. The capture API lives beside the globals trace helpers as two Friend subs
   (`StartDetailedCapture(reason As String)` / `StopDetailedCapture()`),
   called by the Settings tab, the Command Finder command, the leader chord,
   and — later — the feedback dialog's toggle. One implementation, four
   callers. Capture sessions get an outcome detail marking them as captures
   so the browser and future bundle picker can label them ("Detailed capture,
   tonight at 8:14 PM...").

Out of scope for this track: the feedback/crash dialogs themselves, the
rarbox receiver, redaction, NAS mirroring, any change to `JJTrace` internals
beyond what the capture API needs (expected: none — sessions, levels and
rotation already do everything required).

---

## 9. What the reporting pipeline gets from this surface

The queued pipeline (crash trigger + user-initiated feedback, one bundle
format, per-event consent, rarbox receiver) needs four things from this
surface, and this design supplies each:

- **A trustworthy always-on record** — the log can no longer be silently
  killed by a stopped manual trace, misfiled to Documents, or mislabeled
  `killed`; and the operator can finally see that it exists and is on.
- **A one-checkbox maximum-detail reproduce mode** — the capture API is the
  "mad man trace" toggle's implementation; the feedback dialog will call
  `StartDetailedCapture("feedback session")` instead of walking a tester
  through six steps.
- **A human-language session inventory** — the browser's manifest-backed
  list ("Tonight at 8:47 PM, about 12 minutes, ended normally") is the same
  data the feedback dialog's session picker will present; the formatting
  helper should be written once, shared.
- **A consent posture already in the UI** — the privacy note, spoken sizes,
  and export-is-a-choice framing mean the send dialog adds a destination,
  not a new philosophy. What it must never do: send anything without a
  per-event action, include sessions the user didn't tick, or hide the size.

Sequencing: this track lands before the R2/rarbox report-storage work, per
the research-queue note — the front door gets fixed before the building
behind it goes up.

---

## 10. Docs, help and test obligations

- New help page `docs/help/md/diagnostic-log.md`: what the log is, the two
  detail levels in plain language, capture walkthrough, where files live,
  the privacy paragraph, how to export for the developer. F1 from the
  Diagnostics tab lands here.
- Cross-link from `connection-troubleshooting.md` and
  `audio-troubleshooting.md` ("if the developer asks for a diagnostic log:
  ...").
- Changelog, user voice, e.g.: "The diagnostic log now has a real home.
  Settings → Diagnostics shows what JJ Flex is recording, lets you pick how
  much detail, and gives you one button to capture a problem in the act.
  The saved-logs browser finally shows up too — find any session from the
  last month, hear when it ran and how it ended, and export it to send me."
  (Noel reviews all user-facing prose.)
- CLAUDE.md: update the Trace File Location note's UI pointer (Operations →
  Tracing is stale twice over) to "Settings → Diagnostics (Tools →
  Diagnostics deep-links there)".
- Test matrix: new file per house convention, folding in the entire unticked
  Track H checklist (`sprint29-test-matrix.md:104`) now that the browser is
  reachable, plus: state honesty of the capture button under NVDA and JAWS,
  boot honoring `KeepDiagnosticLog=false`, capture start/stop restoring the
  standing level, no false `killed` entry after a capture cycle, path
  copy/open, and the export-after-capture flow.
- Keyboard audit for `Ctrl+J, Ctrl+D` (accepted 2026-08-16 with the letter
  change — see section 11).

---

## 11. Open questions — ANSWERED 2026-08-16 (small-fixes sweep, per task #26)

Noel assigned these to the sweep in `barefoot-splatter-ragchew.md` rather than
taking them back for review; answers below unblock cutting the implementation
track. Anything Noel wants different surfaces naturally at that track's review.

1. **The leader chord: yes — but NOT `Ctrl+J, D`, which is already taken.**
   A fact this design missed: `Ctrl+J, D` has been bound to "Toggle tuning
   speech debounce" since before this document was written (it appears in the
   May 2026 leader-help audit dump, and in `KeyInventory.cs` today). `Ctrl+J,
   C` is Toggle Compander, and `Ctrl+J, Shift+D` sits inside the
   Shift+A–Shift+H slice-jump range. **Use `Ctrl+J, Ctrl+D`** — it keeps the
   D-for-diagnostics mnemonic, has in-layer precedent (`Ctrl+J, Ctrl+F`
   enters a frequency), and collides with nothing. The implementing track
   owes the full keyboard audit for it, including pressing the chord on a
   real build.
2. **Default ON — confirmed.** It is effectively always on today
   (`BootTrace = (Not Debugger.IsAttached)`), so ON changes nobody's
   behavior; an off-by-default log defeats the reporting pipeline; and the
   privacy posture is held by the local-only rule plus the tab's verbatim
   note, not by the default.
3. **Export-now — confirmed as designed.** "Export this capture..." is the
   right single next act; when the send-to-developer flow ships it replaces
   that button rather than adding a second one beside it.
4. **Retire `KeepDailyTraceLogs` immediately.** No tester is known to
   hand-edit operator XML for daily traces: the field has defaulted False
   with no UI ever writing it, Don consumes prebuilt Dropbox builds and
   works the app from the operator's seat, and Justin does not run JJ
   Flexible at all. Keep the already-specified one-release
   `ArchiveOldDailyTraces` file-pattern sweep as cheap insurance, then
   remove it.

---

## 12. What was built — Sprint 30 Track D, 2026-08-18

Every numbered item in sections 1-11 landed except where noted below. This
section is the record of the decisions taken where the design was silent, the
places the implementation deliberately departs from the text, and the facts the
design got wrong.

### Where the implementation departs from this document, and why

**Per-level help text is visible body text, not `AccessibleDescription`**
(§3 asked for both). This document predates the 2026-08-18 HelpText finding:
NVDA reads `AutomationProperties.HelpText` as a control's description on EVERY
focus, so an explanation parked there is recited every single time the operator
tabs past the control. Section 3's two sentences would have been read on every
pass through the radio pair. They are now static text under each radio button —
read once as dialog body when the tab opens, and still there to be re-read.

**The storage figures are behind a "Measure now" button, not automatic.** A
recursive walk of a 2.2 GB tree on tab entry stalls the dialog, and a stall is
indistinguishable from a hang to somebody who cannot see a spinner.

**The WPF `TraceAdminDialog` was corrected and kept, not deleted** (§8.1 said
delete). Nothing opens it — the Help → Tracing entry is gone — but the file
stays one release as a fallback, the way `AuthForm` was kept when WebView2
replaced it. This sprint merges five tracks and the Settings dialog is the
surface most likely to need backing out. It is corrected, not merely retained:
live `Tracing.On` instead of an assumed false, session-aware start and stop, and
the live log as its default file instead of `Documents\JJRadioTrace.txt`. Its
header comment carries the instruction to delete it once 4.1.17 has shipped.
Routed to Noel for a ruling.

### Decisions taken where this document was silent

**A `DiagnosticsBridge` delegate table is the seam between the surface and the
plumbing.** JJFlexWpf is referenced BY the VB project, so the WPF surface cannot
call the trace plumbing by name. Every previous answer to that problem in this
codebase was to re-implement the plumbing on the UI side — which is precisely
how `TraceAdminDialog` came to start traces that bypassed the session archive.
One seam, populated once at startup by `WireDiagnosticsBridge` in `globals.vb`.

**Nothing in the surface caches log state.** The status line reads through the
bridge on every refresh and re-reads on a `StateChanged` notification. Caching
state is the specific mechanism by which the old dialog spent months announcing
a state that was fiction.

**Crash-report retention: keep newest 3, and a verdict is required before
deletion.** §8 did not cover crash dumps at all; task #92 did. A `.verdict`
sidecar records "sent" or "dismissed" beside each bundle. Beyond the newest N, a
bundle is removed only once it has a verdict AND is past the age window or the
folder cap. An unresolved bundle survives all of that for 7 days (ruled by Noel
2026-08-19; the design shipped at 90), after which
its removal is logged by name — the one backstop on the never-delete-unresolved
rule, because without a ceiling a machine whose upload prompt keeps failing
grows without bound. Routed to Noel.

**Firmware images age out at 30 days.** A pure cache; the worst case of deleting
one is a download.

**Manual controls live on the Diagnostics tab, beside the explanation.** "Delete
loose log text files" removes the stamp-named plain-text siblings at the settings
folder root REGARDLESS OF AGE, which is the point: the automatic sweep
deliberately keeps the last day, and an operator who has just filled a disk with
a Verbose capture should not have to wait a day to get the space back. The
compressed sessions are never touched, so nothing is lost.

**The failure-moment offer (task #78) is `Radios.OperationFailure` plus
`JJFlexWpf.DiagnosticOffer`.** The reporter has no UI and lives in Radios so any
layer can report; the offer owns every judgement about interrupting the operator
and holds the whole policy in one place. Which failures qualify, which do not,
and the suppression rules are documented in `DiagnosticOffer`'s type comment and
summarised in `docs/planning/for-noel/2026-08-18-diagnostics-three-rulings.md`.

**Session rows read as human phrasing.** `TraceAdmin.HumanSessionPhrase` renders
"Tonight at 8:47 PM" / "Yesterday at 3:12 PM" / "14 Aug at 9:00 AM", and prefixes
"Detailed capture, " when the session was one. It is the FIRST column because a
screen reader reads a row's first cell as the row's identity. Written once and
shared, because the feedback dialog's session picker needs the same phrasing.

### Facts this document had wrong

**"Auto-prune covers `Traces\` only, not the loose `JJFlexRadioTrace-*.txt`
files at the folder root."** Not so. `PrunePlainTextTracesOlderThan` matches
`{DailyTraceFilePrefix}-*.txt`, which IS the loose stamp-named set, and it runs
at every boot. The reason 34 of them (35 MB) were sitting there on 2026-08-18 is
that the retention window is ONE DAY and they were all from that day — a day
with thirty launches produces thirty files. The missing capability was never
automatic pruning; it was a manual control that can act inside the one-day
window. That is what shipped.

**`PruneCrashReports` already existed.** Task #92's framing ("nothing prunes
them") was true when the 2.2 GB was measured and stale by the time the track ran:
a 30-day plus 2 GB newest-first sweep was already in place, and `SaveCrash`
already deletes the loose `.dmp` and `.txt` after zipping them. What was missing
was the part that matters — a size cap alone will happily delete the one report
support is about to ask for.

**The About page had THREE version assemblers, not two.** The audit found
`BuildLibraryVersionsHtml`; `BuildAboutPlainText` carried an identical inline
copy, and the update check read the entry assembly a third time. All three now
read `DiagnosticSnapshot`.

### Retired

`KeepDailyTraceLogs` is no longer read anywhere and `StartDailyTraceIfEnabled`
is gone, along with its call site in the power-on wiring. The FIELD stays on
`PersonalData.personal_v1` so existing operator XML round-trips unchanged —
removing a serialized member buys nothing and risks a migration.
`ArchiveOldDailyTraces` survives as a one-release sunset sweep, now UNGATED
(gating it on the retired field would have meant it never ran on the machines
that need it) and called from `TraceArchiveBootMaintenance`. Remove it after the
release following 4.1.17.

### Still owed

- The help page `docs/help/md/diagnostic-log.md` and the cross-links from
  `connection-troubleshooting.md` / `audio-troubleshooting.md`. Track E owns
  `docs/help/md/` this sprint; the prose is in Track D's report.
- The `keyboard-reference.md` line for `Ctrl+J, Ctrl+D`. Same reason; the exact
  line is in Track D's report.
- The failure-moment hooks in Track A's files — `RadioConfig.SaveForRadio`,
  `SettingsDialog.RadioProfile.cs`, and the connect-failure site in
  `MainWindow.xaml.cs`. Track D is fenced out of all three; the exact call sites
  and lines are in its report.
- A test matrix folding in the unticked Sprint 29 Track H browser checklist,
  now that the browser is reachable.
