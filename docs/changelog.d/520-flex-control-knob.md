<!-- Fragment for docs/CHANGELOG.md, Unreleased section. Junkbox #520. DRAFT PROSE (#520): awaiting Noel.
     The second bullet must not fold until the bench shows a double-press no longer steps the
     frequency: the measurement never recorded how far the phantom step lands before its press
     code, so whether the two-tick window catches it is unverified (ASK-520-press-window). -->

- **The Flex Control knob no longer acts on turns nobody made** — when the knob gets bumped or twirled while JJ Flexible isn't listening to it, the knob saves those turns up and hands them all over the moment it connects, and the radio used to jump by however many steps had piled up. The same pile-up could land if the app was busy for a second or two. Anything arriving faster than a hand could possibly turn the knob is now thrown away. Your own turning, brisk turning included, still comes through, every step.
- **Pressing the knob no longer nudges the frequency** — pushing the knob in moves the dial a hair inside, and that used to count as a one-step turn, so changing something with a double-press could leave you a step off where you were. A turn that lands right beside a press of the knob or any of its buttons is now ignored.
