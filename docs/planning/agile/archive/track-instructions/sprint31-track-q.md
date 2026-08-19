# Sprint 31 — Track Q — Quiet Diagnostics

**Worktree:** `C:\dev\jjflex-31q`  **Branch:** `sprint31/track-q`  **Base:** `honest-tx-audio` @ `01c2d346`
**Model:** opus  **Merges:** SECOND

You own the diagnostics surface Track D built last sprint. Two tasks: replace its interaction model
with one the owner actually wants, and delete the dialog it superseded.

---

## House rules

**The user is blind and uses NVDA.** He will operate every line you write, at the exact moment
something has just gone wrong for him.

- **No tables, no ASCII art, no diagrams** in anything you write. Prose or bullets.
- **Every control gets `AutomationProperties.Name`.** Keep disabled controls OUT of the tab order.
- **Long explanations go in `JJFlexHelp.Text`, NEVER `AutomationProperties.HelpText`.** NVDA reads
  HelpText aloud as the description on EVERY focus. `JJFlexHelp` (in `JJFlexWpf`) is the on-demand
  channel read only by Ctrl+F1. HelpText is legitimate ONLY for a short hint that SHOULD be heard
  every time ("Arrows to change").
- **A screen reader flushes its speech queue on any window change.** This is the crux of your first
  task. Anything crossing a window boundary must be carried BY the arriving window, in its Title.
- **Escape closes every dialog.**

**Build:** `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`, verify the
exe timestamp. **Tests:** `dotnet test Radios.Tests/Radios.Tests.csproj -c Debug` — 122 at base.

**Commits:** per item, `Sprint 31 Track Q: <description>`, push after EVERY commit to `origin`,
NEVER `upstream`. Never `git add -A`.

**Do not edit:** `CLAUDE.md`, `docs/CHANGELOG.md`, `docs/help/md/*` — report that content instead.

**Unattended, owner away.** Never block. Most defensible option, write it down, keep going. End with
**"Needs Noel"**.

**Verify before you fix.** Four times on 2026-08-19 an answer already existed in the tree and was
re-derived instead of found. Grep before you write.

---

## Your work

### 1. Task #100 — the failure offer must announce and persist, not open a window

Track D built a `DiagnosticOfferDialog` that appears when something fails, offering to save the
diagnostic log. Noel rejected the **interaction model**, not the failure list. His words:

> "You could put up a window for the user or announce something that an issue has been detected,
> pressing a key would bring up that window. I worry that a window popping up might confuse the
> user. I'm not sure what we do if a user doesn't hear the notification, I have problems with that
> in Windows with notifications that I forget to note."

**The second reason he did not raise, and it settles it:** a window that appears on failure FLUSHES
the screen reader's speech queue. So a failure window destroys whatever was mid-sentence — which on
a failure is very often the message explaining the failure. The interrupting design fights itself.

**Why Windows notifications fail him, diagnosed:** not that they are quiet, that they are
EPHEMERAL. Miss one and it is gone with no way to ask what was missed. So the fix is persistence
and retrievability, not volume.

**Build this:**

- On failure: an **earcon plus one short spoken line**, e.g. "A setting could not be saved.
  Ctrl+J, Ctrl+R for details." No window, no focus change, nothing flushed.
- The failure goes into a **Problems list that persists**, so missing the announcement costs nothing.
- **Ctrl+J, Ctrl+R** opens it — beside D's existing `Ctrl+J, Ctrl+D` capture chord in the same
  leader layer. Everything flows from `KeyInventory.LeaderCommands`; updating that one place
  satisfies Command Finder, the `Ctrl+J, H` spoken help, and the key manifest together.
- The **Diagnostics tab shows a count** ("2 problems recorded this session") so it is discoverable
  without knowing the key.

**What makes this safe rather than a compromise, and worth putting in a comment:** the diagnostic
log is written either way. The offer was never a safety net — it is a convenience that saves hunting
later. Miss it, ignore it, or quit entirely, and the evidence is still on disk and exportable from
Settings → Diagnostics. Once that is clear the case for interrupting collapses.

**Consequence — relax D's frequency limits.** "Max two offers per session" and "one Not now ends
offers" existed to stop a modal window becoming a nuisance. With nothing stealing focus, **record
every failure** and cap only how many get ANNOUNCED. Discard no information.

**KEEP D's classification work — it is the valuable half.** Four kinds surface (setting not saved,
named-radio connect failed, audio unavailable, reporting failed); six deliberately do not (crashes,
empty discovery, login/token rejections, retry-absorbed, corrupt presets, firmware download). The
login exclusion is on PRIVACY grounds: the log carries the SmartLink email and JWT fragments.
Keep `OperationFailure` and `DiagnosticOffer`'s judgement; replace the dialog.

**Open, decide and state it:** whether the problems list persists across sessions or only within
one. Leaning within-session, since the log is the durable record and cross-session would need its
own pruning story.

**Keyboard audit applies** — new binding. `docs/help/md/keyboard-reference.md` is not yours, so put
the exact line in your report. And **PRESS THE KEY** on a real build. A binding shipped completely
dead on 2026-08-13 because it compiled, reviewed clean, and was never pressed.

### 2. Task #103 — delete the retired trace dialog

Noel: *"kill it, it was not designed well."*

Track D removed Help → Tracing from the menu and replaced the surface with Settings → Diagnostics
plus Saved Diagnostic Logs, but KEPT `TraceAdminDialog` as a rollback fallback. That argument was
always time-limited and Noel has now ruled.

Remove `JJFlexWpf\Dialogs\TraceAdminDialog.xaml` + `.xaml.cs`, and the legacy VB form behind it if
it is also dead.

**CHECK FIRST, and this is the whole risk in the task:** Track D reworked `TraceAdmin.Browser.vb` as
part of giving the Saved Diagnostic Logs browser a door. **Some of that file is now LIVE.** Do not
delete what D just wired up. Grep for every type name before removing anything — the Saved
Diagnostic Logs browser sat unreachable for an entire sprint precisely because nobody checked what
instantiated what.

Track D's fix to the fictional `_isTracing` state dies with this dialog, which is fine, but it means
the trace-state bug is only truly gone once the new surface is the only surface.

---

## Files you own

`JJFlexWpf\DiagnosticOffer.cs`, `JJFlexWpf\Dialogs\DiagnosticOfferDialog.xaml` + `.xaml.cs`,
`Radios\OperationFailure.cs`, `JJFlexWpf\Dialogs\SettingsDialog.Diagnostics.cs`,
`JJFlexWpf\KeyCommands.cs`, `JJFlexWpf\KeyInventory.cs`, `JJFlexWpf\Dialogs\TraceAdminDialog.*`,
`TraceAdmin*.vb` (with the check above), `JJFlexWpf\EarconPlayer.cs` (the failure earcon —
categories exist now, pick the right family), `globals.vb` **trace and diagnostics regions only**.

## Collisions

- **`globals.vb` — you and Track R.** R touches the rescue-Home and connect regions; you touch trace
  and diagnostics. Different regions. You merge before R.
- **`SettingsDialog.xaml` — Track P owns the Network/Radios regions and merges first.** Your
  Diagnostics tab additions are a separate region.
- **`KeyCommands.cs` — yours alone this sprint.**

## Merge position

**SECOND**, after P.

## Your report

What landed per item, the exact `keyboard-reference.md` line for Ctrl+J Ctrl+R, **the observed
result of pressing it on a real build**, which failure kinds now announce, every user-facing string,
changelog lines in the house voice, and **Needs Noel**.
