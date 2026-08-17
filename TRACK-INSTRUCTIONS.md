# Track D1 — Make the Live Meters readout navigable

**Worktree:** `C:\dev\jjflex-d1` · **Branch:** `bsr/track-d1` · **Model:** Fable

**Read first:** `docs/planning/active/barefoot-splatter-ragchew.md`, "Track D"
sections "What actually exists today" and "D1 — Make the readout navigable".

## The problem, precisely

**The Live Meters tab contains ZERO tab stops, and never has.**
`MakeMeterLabel` in `JJFlexWpf/Dialogs/AudioWorkshopDialog.xaml.cs` builds each
reading as a plain `TextBlock` with an accessible name and
`AutomationLiveSetting.Polite`. **A `TextBlock` is not focusable and nothing sets
`Focusable`.** Tab does nothing there because there is nothing to land on.

The live-region setting is why the tab has *seemed* to work: values are announced
as they change. But **an operator can never go and ASK a meter what it says.**
The readings arrive when they arrive.

Eight labels are affected: S-Meter, Forward Power, SWR, Mic audio, TX drive
(ALC), Amp ALC, PA Temperature, Supply Voltage.

## The work

**1. Make them focusable and readable on demand.** Use the same idiom the Audio
Devices page and the Workshop's own device and mic readings already use:
**read-only `TextBox` controls with proper labels.** Focusable, arrowable at the
operator's own pace, and readable by the screen reader's own review commands.

**2. Then reconsider the live region.** A polite live region on a value that
changes twice a second is a great deal of announcement for something the operator
can now simply go and read. See
`memory/feedback_speak_only_when_ui_does_not_convey.md` — the rule is to fix the
tree rather than narrate around it. **Recommend, with reasoning; do not just
delete it.**

**3. Keep the tab order sane.** Eight new stops is a real cost. Check they read
in a sensible order and that the section still works with F6 navigation, which
the Workshop already has.

## Deliberately NOT yours

- **Do not touch `AudioOutputConfig.cs`** — Tracks D2 and F own the models there.
- **Do not build meter creation, management, tones, or the `Ctrl+J` layer.** D2
  and D3 own those. You are making eight existing readings reachable, nothing
  more.
- **Do not change which meters are shown.** The eight are a hardcoded subset and
  the radio actually reports 102 — but replacing that list is D3's job, and doing
  it here would collide.

## Why this is small but not trivial

It is one file and one idiom. **But it is accessibility correctness**, which is
the thing this project is for — a control that is focusable but unlabelled, or
labelled but announces the wrong thing, is worse than the TextBlock it replaced.
**Verify by using it with NVDA rather than by inspection.**

## Papercuts you own

Any wording papercut in the Live Meters region while you are in there.

## Rules

- **Reuse the symbols you find. If you conclude one should move or change
  signature, REPORT it rather than doing it.**
- Build: `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`
- Commit style: `Track D1: <description>`.
- **Do not merge, do not push to main, do not touch other worktrees.**

## Done means

Builds clean. Tab lands on each of the eight readings, each announces a sensible
label and its value, arrow review works, F6 still moves between sections, and you
have made a reasoned recommendation about the live region rather than silently
removing or keeping it.
