# Sprint 31 — Track R — Rescue and the Honest Menu

**Worktree:** `C:\dev\jjflex-31r`  **Branch:** `sprint31/track-r`  **Base:** `honest-tx-audio` @ `01c2d346`
**Model:** opus  **Merges:** THIRD

You own Home and the menu bar. Two tasks: finish the rescue page Track A started, and close the last
two silent absences.

---

## House rules

**The user is blind and uses NVDA.** He will operate every line you write.

- **No tables, no ASCII art, no diagrams** in anything you write. Prose or bullets.
- **Every control gets `AutomationProperties.Name`.** Keep disabled controls OUT of the tab order.
- **Long explanations go in `JJFlexHelp.Text`, NEVER `AutomationProperties.HelpText`.** NVDA reads
  HelpText aloud as the description on EVERY focus. `JJFlexHelp` (in `JJFlexWpf`) is the on-demand
  Ctrl+F1 channel. HelpText is legitimate ONLY for a short hint meant to be heard every time.
- **A screen reader flushes its speech queue on any window change.** Load-bearing for your first
  task — see below.
- **Escape closes every dialog.**

**Build:** `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`, verify the
exe timestamp. **Tests:** `dotnet test Radios.Tests/Radios.Tests.csproj -c Debug` — 122 at base.

**Commits:** per item, `Sprint 31 Track R: <description>`, push after EVERY commit to `origin`,
NEVER `upstream`. Never `git add -A`.

**Do not edit:** `CLAUDE.md`, `docs/CHANGELOG.md`, `docs/help/md/*` — report that content instead.

**Unattended, owner away.** Never block. Most defensible option, write it down, keep going. End with
**"Needs Noel"**.

**Verify before you fix.** Four times on 2026-08-19 an answer already existed in the tree and was
re-derived instead of found.

---

## Your work

### 1. Task #101 — rescue page follow-ups

Track A shipped a rescue Home for the no-radio startup case: five buttons (Connect, Settings, Audio
Workshop, Help, Exit), everything radio-dependent collapsed, focus routed through one `FocusHome()`
funnel because WPF's `Focus()` on a collapsed element silently fails.

**Two additions Noel asked for.**

**(a) Radio Setup on the page.** His reasoning is the case the page exists for: *"if you can't
connect, perhaps one needs to setup a radio / enroll it."* A rescue page offering no way to
configure the radio you are failing to reach is missing what a stuck operator most likely needs.

Settings already reaches per-radio setup — Track A made the per-radio panel editable while
disconnected, and REM ON lives there. So decide: a sixth button, or a deep link into Settings that
lands directly on Radio Setup. Either is defensible; say which you chose and why. A deep link is
probably better than a sixth tab stop, but it must land somewhere real and announce where it landed.

**(b) A mid-session route in.** Today a radio dropping mid-session uses the old `RestoreNoRadioShell`
path, which leaves the frequency display visible and focuses it — so the two paths describe "no
radio" differently, which is the real reason to fix this.

Noel's design: bring the rescue page up if the connection stays down for **three minutes**, and add
a menu item so the operator can reach it sooner by choice. His suggested name: **"Radio Rescue"**.

The delay is the right shape and worth preserving in the implementation: a momentary drop that
recovers on its own must not tear the operator's context away. Three minutes is long enough that the
session is genuinely over; the menu item means nobody has to wait it out.

**THE HARD PART, and it is why Track A deferred this:** it is a window transition during LIVE
operation. Everything that must be heard has to be carried BY the arriving surface, folded into its
Title — never spoken before it, because the transition destroys anything in flight. See
`PendingDisconnectLead` in `globals.vb` for the working pattern. Also settle the reverse: what
happens if the radio returns while the rescue page is showing. It should go back to full Home, and
that transition has the same constraint in the other direction.

**Useful members Track A left you, callable without editing its files:** `FocusHome()` — use this
instead of `FreqOut.FocusDisplay()` after any dialog, it is the only call that is correct with no
radio — plus `InRescueMode`, `ExitRescueMode()`, `EnterRescueModeIfNoRadio()`.

**A live defect to fix while you are here:** Noel reported 2026-08-19 that **disconnecting does not
announce anything**. The `PendingDisconnectLead` mechanism only speaks if the picker actually opens
to carry it. If a disconnect leaves no arriving window, the lead is set and silently discarded —
worse than the original bug, because the message exists and never plays. Track A also narrowed the
condition to "only when a radio was genuinely connected", which may be one degree too strict.
Establish which, from the code, and make a disconnect always announce itself somehow.

### 2. Task #96 — the last two silent absences

Track A closed the diversity and advanced-NR cases: a feature you cannot have now stays in the menu
and explains which gate is shut, rather than silently not being there. Two remain.

- **ESC** has NO menu item anywhere, on ANY radio, regardless of licence or hardware. It appears
  only in the Feature Availability text. **Determine first whether the feature is reachable in the
  app at all** — if it is not, this is a missing feature rather than a missing menu item, and you
  should report that rather than building a menu entry for nothing.
- **ATU** items vanish entirely on a radio without one. That is a HARDWARE gate rather than a
  licence gate, which is why Track A left it — but from the keyboard, missing and "not for this
  radio" feel identical, and only one of them is true.

**The pattern to follow, already in the code:** Track A's `AdvancedNrGateMessage()` in
`NativeMenuBar.cs`, including its deliberate asymmetry — hardware is a local fact so "your model
lacks it" is safe to state; a licence **never reported** leaves the controls in place and claims
nothing, because we do not know; only a licence positively reported as disabled produces a
subscription message. Never claim a feature is unavailable when the truth is that we do not know.

---

## Files you own

`JJFlexWpf\MainWindow.xaml` + `.xaml.cs`, `JJFlexWpf\NativeMenuBar.cs`,
`JJFlexWpf\Controls\ScreenFieldsPanel.xaml.cs` (rescue visibility only — Track P is extracting a
helper from this file and merges first, so rebase carefully), `globals.vb` **rescue and connect
regions only**, `ConnectingForm.vb` if needed.

## Collisions

- **`globals.vb` — you and Track Q.** Q owns the trace and diagnostics regions; you own rescue and
  connect. Q merges before you.
- **`ScreenFieldsPanel.xaml.cs` — Track P extracts the expander helper from it and merges first.**
- **`SettingsDialog.xaml` — Track P merges first;** if your Radio Setup deep link needs a hook there,
  keep it to one line and say so in your report.

## Merge position

**THIRD**, after P and Q.

## Your report

What landed per item, which shape you chose for Radio Setup and why, how the mid-session transition
carries its state, what you found about the disconnect announcement, whether ESC is reachable at
all, every user-facing string, changelog lines in the house voice, doc content for the help pages,
and **Needs Noel**.
