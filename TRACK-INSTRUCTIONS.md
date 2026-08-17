# Track C — Settings that stick

**Worktree:** `C:\dev\jjflex-c` · **Branch:** `bsr/track-c` · **Model:** Sonnet

**Read first:** `docs/planning/active/barefoot-splatter-ragchew.md`, the whole of
"Track C — Settings that stick".

## Theme

Settings you set and the app quietly did not keep. Every item treats a stated
intent as a command it may decline.

## 1. The convention — do this first, it is the spine

**OK applies and closes. Apply applies and stays. Cancel discards. EVERY settings
screen carries that same pair — no per-feature variants** like "apply connection"
or "apply setting".

This is the standard Windows convention, and the reason to adopt it is the reason
it became standard: the buttons mean the same thing in every dialog, so muscle
memory transfers. Bespoke per-screen buttons force relearning each dialog —
friction tax, and worse through a screen reader.

**This reframes the radio-name bug.** Either there was an Apply that was missed
and OK discarded the edit, or there was no Apply and OK silently failed. **Both
are the same defect** — an OK that does not apply is broken either way. **The
convention IS the fix.**

Two things already decided, do not re-litigate:

- **Cancel after Apply does not roll back.** Windows convention; the alternative
  needs an undo buffer per setting.
- **Apply stays present and enabled even when nothing has changed.** Convention
  says grey it out, but the house rule keeps disabled controls out of the tab
  order — which would make Apply *vanish* mid-dialog. Worse than useless when
  counting tab stops. Keep it present; let it be a no-op.

**Queued intents need their own voice.** When a setting cannot apply now — radio
disconnected — **OK must say so plainly** rather than implying it took effect.
"Saved; applies when you connect" is honest. Silence is what got us here. See
`memory/project_settings_are_intents_not_commands.md`.

## 2. The items

- **REM ON reachable while disconnected.** It already exists —
  `TXControlsDialog` wires `RemoteOnCheck` to `Radio.RemoteOnEnabled`. **This is
  not building a feature**; it is reaching it from per-radio settings with no
  live connection, queued to apply on connect. Don's radio being off is exactly
  the case it prevents, and it is unreachable precisely when needed.
- **The radio name that did not save.** Track A owns the *display* half
  (`PaintRoster` skipping the roster for discovered radios); **you own the save
  half. Verify with A or each looks fixed while the other still breaks it.**
- **Network settings discard unapplied port-forward edits on OK.**
- **Show the router mapping, and fix the comment that lies about it.** External
  TCP goes to radio port 4994, external UDP to 4993. The doc comment on
  `FlexBase.SetSmartLinkPortForwarding` claims the radio listens on the ports you
  type — wrong, and it misled a live debugging session.

## 3. The "no physical access" flag

A per-radio setting meaning **"this radio is operated remotely; I cannot reach
its front panel."**

**Not redundant with Track A's path chain.** The chain answers *how do I
connect*; this answers *can a human reach the radio*. A dual-homed radio at your
own house might prefer SmartLink because you are often out — and you can still
walk over and press the button. Geography, not networking.

**Explicit, not derived**, even though A will know enough to guess. The failure
modes are asymmetric: wrongly inferring "local" *suppresses* a warning that would
have saved you; wrongly inferring "remote" shows a prompt you did not need.
Pre-populate from the path chain, show what it picked, allow override.

**The cascade.** Checking it presents a warning, then a yes/no over the settings
it implies (REM ON active, update-without-hardware-check). Unchecking asks again
and reverses.

- **Enumerate the bundle — do not just ask yes/no.** "Yes" to an unnamed set is
  not informed consent and teaches nothing. Name each setting and its new value.
- **Do not clobber on reverse.** If someone hand-tuned one of those settings
  afterwards, un-checking must not silently undo it. Listing what changes handles
  this — they can see it and decline that one.
- **REM ON has a hardware prerequisite.** Enabling it does nothing unless the RCA
  jack is wired to a relay. Say so, or it hands someone false confidence about a
  radio they cannot reach — the exact failure this flag exists to prevent.

**"Don't show this again", and why it is safe.** The dialog is two things with
different rules: the enumerated prompt is *teaching and consent*; the summary
afterwards is a *receipt*. **Suppressing the teaching is fine. Suppressing the
receipt would be a silent change, which we do not do.**

- The receipt is an **OK-only dialog, not a Tolk `Speak`.** A `Speak` is
  ephemeral, never reaches braille, and can be cut off mid-sentence — which is
  exactly a complaint Noel raised on 2026-08-14. A dialog is a real object:
  re-readable, braille-reachable, acknowledged.
- **Scope the suppression globally, not per radio.** The explanation is identical
  everywhere; learning it once should count once. The receipt still fires per
  radio, every time.
- **Version the bundle so it returns when its contents change.** Otherwise
  "don't show again" quietly becomes "never tell me about new things you do to my
  radio."

## Papercuts you own

Wording papercuts in the settings dialogs you are already in.

## Rules

- **Reuse the symbols you find. If you conclude one should move or change
  signature, REPORT it rather than doing it.**
- `FlexBase.cs` is shared with A and B in disjoint regions.
- Build: `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`
- Commit style: `Track C: <description>`.
- **Do not merge, do not push to main, do not touch other worktrees.**

## Done means

Builds clean. Every settings screen has OK and Apply behaving identically. A
setting made while disconnected survives and says when it will apply. Port-forward
edits survive OK. The router mapping is shown and the drifted comment corrected.
The no-physical-access flag works with an enumerated cascade, a receipt dialog,
and suppression that returns when the bundle changes.
