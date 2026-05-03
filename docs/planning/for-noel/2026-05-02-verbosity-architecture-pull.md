---
type: full design memo — fresh review pass
review needed: confirm 05-02 decisions still hold, weigh in on remaining open questions, push back on anything that feels wrong
priority: medium — Sprint 30+ scope, not blocking 4.2.0 but you wanted a re-pass
source: pulled from memory/project_verbosity_architecture_proposal.md per your "Pull X" request
---

# Verbosity architecture — full design pass (2026-05-02)

This is your fresh-pass review on the verbosity-channel design. You captured this as a "treatise" message during 4.1.17 matrix testing on 04-28; you answered some questions on 04-29 (now locked-in and called out below); now you want to look at the whole memo and decide whether anything needs revising.

**Three things to decide on this pass:**
1. Confirm or revise the locked-in decisions (you can change your mind — 05-02 isn't 04-29)
2. Answer or refine the open Sprint 30+ questions
3. Push back on anything in the framing that feels wrong now that you're seeing the full picture

Use `**** ` for inline answers; `**** ACK` for sections you approve as-is. Move to `for-claude/` when done.

---

## Today's model (the problem we're solving)

`ScreenReaderOutput.VerbosityLevel`:
- `Critical` (= 0) — labelled "Off (critical only)" in Settings UI. Always-spoken messages still fire; everything else is silent.
- `Terse` (= 1) — toggle confirmations, value changes.
- `Chatty` (= 2, default) — all messages including hints.

CW notifications are a separate `CwNotificationsEnabled` boolean flag, hidden in a settings page.

**The bug:** a user at `Critical` who *also* turns off CW notifications has no audible output channel at all. The "Off" verbosity setting silently assumes the user has another channel (CW, future braille). Today the user can disable both and end up in a fully silent app — accessibility breakage with no warning.

---

## Proposed model (independent channels + speech-scoped verbosity)

### Locked-in decisions from 04-29 — push back if any of these feel wrong now

**1. The existing verbosity ladder is NOT replaced.** Today's three-level Off/Terse/Chatty stays as-is, gets renamed to **"Speech verbosity"**, and is explicitly scoped to the speech channel only.

**** (push back here if you want to re-open this — e.g., if you've thought about it and decided you DO want a different ladder shape)

**2. Per-channel on/off is added on top.** CW and braille get their own toggles but do NOT each get their own verbosity ladder.

**** (push back here if you've reconsidered — e.g., if you want CW to have its own chattiness level too)

**3. Defaults: speech on, CW off** (today's behavior, no change for existing users).

**** (push back here if you want different defaults — e.g., CW on by default for new users so they discover it)

### Three independent output channels

1. **Speech** — on/off toggle, defaults on
2. **CW navigation** — on/off toggle, defaults off
3. **Braille** — on/off toggle, defaults off (future, when braille support matures)

### Speech verbosity ladder (the today's-Verbosity model, renamed)

1. **Chatty** (today's default) — full announcements with context, hints, supplementary info
2. **Terse** — value changes + state transitions, not hints
3. **Off (critical only)** — Critical-level events only (errors, "Connection failed", "Disconnected"); routine output is suppressed

**Naming flag for your consideration:** with channels-and-verbosity separated, the lowest verbosity tier called **"Off (critical only)"** is now confusing — "Off" implies the speech CHANNEL is off, but the channel is on and just at minimum verbosity. Worth considering a rename to something like **"Critical only"** or **"Minimal"** to avoid the word conflict. Not strictly required (the parenthetical clarifies), but the friction is real for a screen-reader user processing the words sequentially.

**** (your call: keep "Off (critical only)", rename to "Critical only", rename to "Minimal", or other?)

### Discoverable cycling key for speech verbosity

`Ctrl+Shift+V` (already exists; needs better discoverability). Matches the speech/CW/braille channel-toggle pattern of separate keys for separate things.

### Smart-warn safeguard

- If a screen reader is detected (via Tolk's `DetectScreenReader`) AND user attempts to disable speech AND CW is also off (and braille, when shipped, is also off), JJ Flex warns: *"You need at least one accessibility output channel. Disabling all means you won't hear navigation feedback. Continue?"*
- If NO screen reader is detected (Tolk reports SAPI-only fallback OR no provider), allow all off — user is in "sighted mode" and explicit consent isn't needed.
- Triggers when the user's action would result in zero enabled channels; otherwise silent.

---

## Why this matters (rationale section)

**Accessibility-channel-as-feature, not verbosity-modifier.** Today's "Off" really means "speech off, hopefully you have CW notifications enabled, otherwise good luck." That's an implicit assumption that breaks silently. The proposed model makes the channel selection explicit: the user actively chooses speech / CW / braille (or any combo), and the verbosity ladder tunes the speech channel's chattiness.

**Discoverability via toggle keys.** Buried-in-settings options have low discoverability. A toggle key for speech on/off (analogous to mute on a radio) is something users learn fast and use often. Same for CW toggle. Users will internalize "Ctrl+Shift+S speech mute, Ctrl+Shift+W CW mute, Ctrl+Shift+V verbosity cycle" the way ham operators internalize Ctrl+Z RIT clear.

**Separation enables future sprint scope.** When braille output matures (Sprint 30+ braille work), it slots in as a third channel without retrofitting the verbosity model. Today's collapsed model would need restructuring at that point; the proposed model accommodates expansion natively.

**Critical-level message routing becomes unambiguous** — *modulo Q5 below*. A Critical-level message ("no radio connected", connection lost, error) routes to whichever channels are enabled. Today's "Critical always fires through speech regardless of verbosity setting" is a hack that papers over the collapsed model.

**** ACK on this section? Push back if any of the rationale feels off in retrospect.

---

## Channel-toggle key proposal (subject to keyboard audit)

Reuse the `Ctrl+Shift+<letter>` namespace established by Sprint 23+:

- `Ctrl+Shift+V` — verbosity cycle (already implemented)
- `Ctrl+Shift+S` — speech on/off (NEW; needs keyboard audit for conflicts)
- `Ctrl+Shift+W` — CW navigation on/off (NEW)
- `Ctrl+Shift+D` — braille (future, if Dot Pad / Focus 40 audio sink ships)

These bindings need to clear the keyboard audit (`docs/help/md/keyboard-reference.md`) before implementation. Some letter slots may already be claimed.

**** Pick (or push back) on the letter assignments if any feel wrong before we go to audit.

---

## What MUST work in the new model (constraints, not decisions)

1. **Default state must mirror today's Chatty experience for new users.** Speech on, CW off, verbosity Chatty. No surprise quietness on first run.
2. **Speech verbosity setting persists across app sessions** (already does via `audioConfig.xml`).
3. **Channel on/off persists across sessions** (new — needs config schema addition).
4. **Speak the new state on toggle** ("Speech on", "Speech off", "CW navigation on", "CW navigation off", "Verbosity terse"). Always Critical-level so the toggle is heard regardless of speech-verbosity setting.
5. **Smart-warn logic must not hang the app** if both channels are off and warn produces speech to a disabled channel — the warning has to use whichever channel WAS active before the toggle.
6. **Sighted-mode users still hear a one-time confirmation** on first time they disable both — "All audio output disabled. JJ Flexible will continue with no audible feedback." So even sighted users know what they did. (Optional — design call.)

**** ACK or push back on each constraint.

---

## Sequencing / scope

**This is Sprint 30+ scope, NOT 4.2.0.** Reasons:
- Touches verbosity model, settings persistence, dialog warning logic, multiple channel implementations, and (eventually) braille routing — substantial cross-cutting work.
- Depends on `Tolk.DetectScreenReader` reliability for the smart-warn (low risk but worth verifying first).
- It's a UX design proposal that benefits from a design discussion before implementation, not a bug fix that can ship hot.

**4.2.0 commitment:** **don't paint into a corner with the current collapsed model**. Specifically: when adding new Critical-level messages, route them through the existing `Speak` filter — but keep an eye on whether the message NEEDS to also trigger a CW prosign or braille update for users at speech-off. Where ambiguous, log a TODO so the Sprint 30+ work can resolve cleanly.

**** Per `project_build_now_ship_later.md` (saved this morning), the build decision and ship decision are independent. **Do you want to authorize starting the implementation on a `track/verbosity-channels` branch now, even though release scope is Sprint 30+?** Lets the dev work happen without committing to a release window. (Default if you don't answer: hold until you formally pull this into a sprint.)

---

## Decisions and open questions

### Locked from 04-29 (revisit if your thinking has changed)

**1. Per-channel verbosity? — NO.** Speech keeps its three-level ladder (renamed to "Speech verbosity"). CW and braille get on/off only, no per-channel verbosity ladder. Keeps configuration surface small.

**** Still good?

**2. Default channel state.** Speech on, CW off (today's behavior). Migration must NOT change anything the user had configured.

**** Still good?

### Open for Sprint 30+ design pass

**3. Warning UX for the "disable last channel" case.** Modal dialog (blocks until OK)? Toast (non-blocking but easy to miss for screen reader users)? Speak-only-once with a settings toggle to suppress? Needs a design discussion.

**** 

**4. Mode detection for sighted vs blind.** Tolk's screen reader detection isn't 100% reliable in edge cases (NVDA running but not yet attached, JAWS in transition, custom screen readers). The smart-warn needs to handle "uncertain" gracefully — probably by defaulting to the safer "warn anyway" behavior.

**** 

**5. Critical-level routing precedence when multiple channels are on.** If speech AND CW are both on, does a Critical-level message route to both? Or pick one based on a preference setting? Or speak first then CW as redundant confirmation?

This is the most architecturally consequential open question — affects how the routing layer is designed. Three plausible answers:

- (a) **Both fire** — every Critical message goes to every enabled channel. Simple. Risk: redundant speech + CW for the same message becomes annoying.
- (b) **Speech preferred when both on** — CW is the fallback when speech is off. Today's mental model.
- (c) **User picks a "primary channel" preference** — explicit user choice, more config surface, more flexibility.

**** Lean: 

**6. NEW question: lowest-tier verbosity name.** Should the lowest tier in "Speech verbosity" be called "Off (critical only)" (today), "Critical only", "Minimal", or something else? Word-conflict concern with the channel-on/off toggle (raised above).

**** 

**7. NEW question: build-now-ship-later for this design.** Do you want a `track/verbosity-channels` branch spun up for parallel development, or hold until Sprint 30+ formally starts?

**** 

---

## How to apply this memory (operational guidance)

- When designing new accessibility-output features, **frame them as additions to channels (speech / CW / braille)** with verbosity tuning, not as additions to verbosity levels.
- When writing test matrices that include verbosity variants, **mark Off-verbosity tests as DEFERRED-by-dependency** when alternate channels (CW nav, braille) aren't mature enough to carry the message.
- When auditing keyboard bindings, **reserve slots for the channel-toggle keys** (Ctrl+Shift+S, Ctrl+Shift+W, Ctrl+Shift+D) so they don't get squatted by lower-priority features.
- When discussing the verbosity model with new contributors, **lead with the channels-and-ladder framing**, not today's collapsed enum.

**** ACK on operational guidance? Push back if anything would change your mind on pattern usage.

---

## Confirm or revise

`**** ACK` — design is locked, this is the canonical Sprint 30+ design memo, no revision needed.

`**** ` (specific changes per inline question) — apply changes; I'll update the memory entry with your refinements and re-pull if the changes are substantial enough to warrant another review pass.
