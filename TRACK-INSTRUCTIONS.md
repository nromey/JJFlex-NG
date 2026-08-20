# Sprint 33 Track A — Tier 1, the in-process invariant sweep

**Worktree:** `C:\dev\jjflex-33a` · **Branch:** `sprint33/track-a`
**Plan:** `docs/planning/active/barefoot-harness-pileup.md`
**You are the MERGE TARGET.** Other tracks merge into you.

Build your own worktree only:
`dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal`

---

## What you are building

`JJFlexWpf.Tests` — an STA xunit project that references JJFlexWpf, constructs
every dialog IN PROCESS, walks its UI Automation tree, and asserts invariants.

No desktop dependency if you can manage it. No radio. Should run in CI.

## Why this exists — read this, it determines what you write

On 2026-08-19 the Audio Workshop shipped effectively unusable to a screen reader.
Tabbing through the first category announced nothing on most stops. **The code
was read statically three separate times and diagnosed wrong every time.** The
actual cause was found in minutes by walking the live automation tree.

The cause: `CategoryTabControl` in `JJFlexWpf/Styles/DialogStyles.xaml` had a
template with a bare `ContentPresenter` and no `TabPanel`.
`TabControlAutomationPeer` is items-based, so it produced a peer whose subtree
was EMPTY — while the controls inside remained focusable and correctly named.
Focusable and named, but invisible to the tree that screen readers consume.

**That is the class of defect this track exists to catch, and it is invisible to
every form of static reading.** The fix shipped as `JJFlexWpf/CategoryTabHost.cs`
(commit `85fc3f9e`) — a TabControl subclass whose `OnCreateAutomationPeer`
returns a plain `FrameworkElementAutomationPeer` exposing a Pane. Read both files
before you start; they are your worked example.

## The invariants, in priority order

Assert INVARIANTS, never specifics. "Every focusable control has a non-empty
name" survives a redesign. "The third tab stop is Load Preset" breaks the next
time a panel is reordered, and a suite that cries wolf gets ignored — which is
worse than no suite at all.

**1. Every focusable control exposes a non-empty automation Name.**
The highest-value assertion in the sprint. Silent tab stops are the failure the
project's users cannot work around.

**2. No category or tab renders an empty automation subtree while its controls
remain focusable.** The silent-Workshop signature exactly. Compare the set of
focusable descendants against the set reachable by walking automation peers; a
non-empty difference is the bug.

**3. Every control declaring HelpText has non-empty text.**
Description drift is this project's dominant defect class.

**4. Focus cycles are conserved.** N moves produce N focus events.

**5. No duplicate automation ids within one window.** SmartSDR reusing
`chkboxToggleTX` for both the slice TX flag and the main MOX button cost an
entire evening of debugging. Do not ship the same trap.

## SOLVE THIS FIRST — the focus-stealing hazard

WPF automation peers generally need the element measured and arranged, which
normally means the window has been shown. **Showing a window steals Noel's
focus.** He is blind, works in NVDA, and is at the keyboard while you run. A
suite that hijacks focus is a suite he cannot run.

Try in this order and report which works:

1. Measure and arrange the window's content WITHOUT showing it. Cheapest if peers
   populate — try it first.
2. `ShowActivated = false` with the window positioned far off-screen (e.g. `Left
   = -32000`).
3. A dedicated hidden desktop.

**Report the answer explicitly in your completion report.** Whether Tier 1 can
run while Noel works is most of its value, so this is a headline result, not an
implementation detail.

## STA and xunit

WPF needs STA plus a Dispatcher. xunit does not give you that by default. Either
run each test body on an explicitly created STA thread with its own Dispatcher,
or install an STA test framework. Pick one, apply it consistently, and document
it at the top of the project so the next person does not re-derive it.

## Coverage

Every dialog under `JJFlexWpf/Dialogs/`. Where a dialog cannot be constructed
without a live radio, construct it in the disconnected state and assert what
holds there — do NOT skip it silently. A skipped dialog must be reported as
skipped with the reason.

`AudioWorkshopDialog` is the priority: it is the biggest surface, it is split
across several partial classes, and it is the one that already failed.

## Report findings, do NOT fix them

Where a dialog fails an invariant, record the finding with the control's
identity, the dialog, and which invariant failed. **Your job is the harness plus
an inventory of what is broken.** Repairs get triaged afterwards, one at a time,
by Noel's rule. A track that quietly fixes twenty controls produces a diff nobody
can review.

## House rules

- **No tables** in any output, report or doc. Prose or bullets. Screen reader first.
- Do not steal focus. Do not open windows on the visible desktop during a normal run.
- Do not touch files outside your worktree.
- You add a project to `JJFlexRadio.sln`. Other tracks do too. That conflict is
  expected, additive, and visible — it is the easy kind.

## Commits

`Sprint 33 Track A: <description>`. Commit per meaningful chunk. Do not squash.

## Completion report

State: which focus-avoidance strategy works; how many dialogs are covered and how
many were skipped with reasons; the findings inventory grouped by invariant; and
anything you concluded should change outside your worktree (report it, do not do
it).
