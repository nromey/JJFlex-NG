# JJ Flexible Radio Access — Design Principles

JJ Flexible is built around four foundational design principles. They emerged from real-user pain points reported by the blind ham radio community and from the operating-room experience of designing software primarily experienced through audio rather than vision. Together they form the product's design ethos. A feature passes when it satisfies all four.

---

## 1. Flexibility — User Choice Over Developer Assumption

**The principle:** "Flexible" in JJ Flexible's name is a literal design commitment, not marketing. Any behavior that affects radio state, user workflow, or the operator's environment must be user-togglable by default, not decided for them. The operator decides; the developer does not.

**Why this matters:** Accessibility-first design is sometimes interpreted as "make decisions for the user so they don't have to." That is wrong. Disabled users — especially blind operators — need *more* user agency, not less. They have usually been burned by software that assumed what they "wanted." The respect they are owed is the choice itself.

**Anti-pattern (real product, real harm):** A competitor CAT-control program for the Kenwood TS-590 is hardcoded so that closing the application powers off the radio. No setting, no prompt. The developer decided "close the app means turn off the radio." Users close apps for many reasons: switching programs, restart, accidental click. A 30-second cold-boot on the radio is an expensive cost for a wrong assumption.

**How to apply:**

- For new features that touch radio state, operator environment, or workflow: design the conservative default first ("do the minimum invasive thing"), then add an opt-in for the more opinionated behavior.
- "What's the right default?" comes before "What should the app do automatically?"
- If a feature can't be designed with a user-togglable opt-in, it probably doesn't belong in the product.

---

## 2. Friction-Tax — Minimize Friction Between User and Goal

**The principle:** Disabled users already pay a "friction tax" for everything — every form, every installer, every version-tracking task, every error dialog. Each step the app adds is multiplied against the cost of every step the rest of the world already adds. JJ Flexible's job is to add as little as possible.

**Why this matters:** The friction tax compounds. The users who most need the app are the users most impacted by friction in accessing it. Update friction specifically is recursive — missed updates mean missed accessibility fixes, so update friction is itself an accessibility regression.

**Anti-pattern:** Sending the user through a four-step wizard to do something the app could have done itself. Asking the user to "verify" something that has already been verified. Designing flows where the burden of proof falls on removing a step rather than on adding it.

**How to apply:**

- For every UX step, ask: "What does the user *have* to do here, versus what can the app do for them?" Default is app-does-it-for-them unless safety, ownership, or privacy specifically requires user action.
- The principle is *minimize* friction, not *eliminate* it. Confirmation dialogs that protect the user are fine; "are you sure" theater that does not protect anything is not.
- The burden of proof is on the step that exists, not on removing it.

---

## 3. No Silent Phone-Home — User-Initiated Outbound Only

**The principle:** JJ Flexible does not send data to servers without explicit per-event user action. No usage analytics. No background crash reporting. No telemetry beacons. No A/B testing. No "still here" pings. User-consented update checks are a narrow and disclosed exception.

**Why this matters:** This is a deliberate exception to the Friction-Tax principle, where transparency outranks convenience. "I know what my software sends" is a meaningful user experience that almost no software offers. Trust-through-awareness is specifically valuable to screen-reader users, who navigate data-flow questions more attentively than casual sighted users. Ham radio operators are also disproportionately sovereign-systems types — they appreciate software that respects operator control of data.

**The rule, concretely:** Every outbound communication must be:

1. **User-initiated** by a specific user action, not a timer or background trigger.
2. **Consented with disclosure** — user sees the package contents before sending, with per-category opt-out.
3. **Minimal and purposeful** — only what is needed for the user-requested purpose, no opportunistic field-stuffing.

**Explicitly forbidden:** Usage analytics, silent crash reporting (Sentry-style), feature-flag downloads, A/B testing, retention beacons, install-ID telemetry, heartbeat pings.

**Narrow exceptions (all user-controlled and disclosed):** the update check, user-triggered "Ask for help" support packages, user-triggered crash report prompts where the user sees the package and chooses send-or-skip per crash.

**How to apply:**

- For any new feature involving outbound communication, the first design question is "under what user action does this send happen?" If the answer is "automatically on a timer" or "in response to an internal event the user did not trigger," redesign.
- For any new feature involving data collection, the second question is "what does the user see before this leaves their machine?" If the answer is "nothing — it's background — they wouldn't notice," either surface the consent UI or reject the feature.
- Direct user-developer support email is the analytics. At JJ Flexible's scale, that channel provides better information than aggregate metrics would.

---

## 4. No Silent Keystrokes — Every Input Gets Feedback

**The principle:** Every keystroke bound to a command must produce audible (or otherwise alt-channel) feedback in every reachable application state, even when that feedback is "not available right now." Silence is not a valid response to a user input.

**Why this matters:** For sighted users, "the menu item is greyed out" is sufficient feedback — they *see* the dimming. For screen reader users, there is no equivalent visual cue. A silent keystroke reads identically to a broken-and-silent one. The user has no way to distinguish "this key needs a radio first" from "the app crashed" except by trying every other key to see if anything responds.

**Anti-pattern:** A handler that does `if (TryGetValue(...)) { ... }` with no else branch — silently early-returns when the precondition is not met. The user pressed something; the app heard them; nothing happened; nothing was said. From the user's perspective, the app is broken.

**How to apply:**

- When a command can't perform its real work in the current state, speak (or otherwise output) a short reason. The user hears that the keystroke registered AND learns the gating condition.
- Greying out a control is *invisible* to screen readers. Speech is the parallel channel that carries the same information sighted users get from dimming.
- Do not assume "the user wouldn't press this in this state." They will. Discoverability happens by experimentation. Every key press is a question, and the app must answer it.
- An "Off" verbosity user is not a user who wants silence — they are a user with a different primary channel (CW notification, braille display). Critical-level messages still need to land; they just route to a different channel.

---

## How the four principles interact

- **Flexibility** is the "you decide" axis — choice flows from user to developer.
- **Friction-Tax** is the "we'll do it for you when we can" axis — work flows from user to developer.
- **No Silent Phone-Home** is the deliberate Friction-Tax exception where transparency outranks convenience.
- **No Silent Keystrokes** is the consequence-of-design rule for an interface designed to be experienced primarily through audio.

These principles are cumulative, not alternatives. They produce a coherent design language where every feature respects the user's agency, every interaction respects the user's time, every byte respects the user's data, and every keystroke respects the user's attention.

---

## Bigger picture — why these matter beyond ham radio

Mainstream software increasingly violates all four. Default-on telemetry. Mandatory-consent-then-still-collected analytics. Update flows that demand attention from the user instead of doing the work themselves. Greyed-out controls that screen readers cannot perceive. Workflows redesigned around what the developer wants to measure rather than what the user wants to do.

The blind ham radio community has been a quiet canary on these patterns for years. They feel the friction tax most acutely. They notice the silent telemetry first. They are the population for whom "the silent menu item" is a literal accessibility regression rather than a minor UI quirk. JJ Flexible's design principles emerged from listening to that community — but they generalize. Software that respects these four principles is better software for everyone.
