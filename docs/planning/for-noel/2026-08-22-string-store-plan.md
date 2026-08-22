# The string store — a plan that fits before the load arrives

Written 2026-08-21 at 21:00, for Saturday morning. The dummy load lands between
14:00 and 18:00, so the window is the morning and early afternoon.

## What is honestly achievable, and what is not

Measured tonight: **732 `Speak`/`Output` call sites, 850 `AutomationProperties.Name`
in XAML, 449 XAML `Content=`, 60 help files. Roughly 2,031 sites.** Not extractable
in a morning, and the task's own note says every extraction is a chance to notice
two places saying the same thing differently — so it cannot be done mindlessly
either.

But volume was never the blocker. **Five unmade decisions are.** Nothing can start
until they are settled, and settling them touches no strings at all. Decisions,
plus a working store, plus one domain proven end to end, is a realistic morning
and it converts the remaining 2,000 into ordinary parallel work.

## Phase 1 — the five decisions (no code)

**Key shape.** Hierarchical and behaviour-describing, never screen-position
describing. `connect.smartlink.offer_local_only` survives a redesign;
`settings.tab3.checkbox2` does not. Keys appear in translation files and bug
reports, so they are read by humans who cannot see the screen.

**Missing-key fallback.** Show the key itself. Never empty, never silent. Silence
is invisible to the operator who most needs the text, and a key on screen is a bug
report that writes itself.

**Verbosity ladders are data.** Several utterances already choose between Chatty,
Terse and Critical in a `switch`. Those three variants belong in the store as a set
under one key, not as branches at the call site. This is the single biggest reason
the store is worth building: the ladder becomes reviewable as a ladder.

**Interpolation.** Named placeholders, not positional. `{radio}` and `{freq}`
survive reordering by a translator; `{0}` and `{1}` do not, and a translator
working in a language with different word order will reorder.

**Partition boundaries.** Six domains: connect and session lifecycle; audio and
DSP; settings and per-radio config; logging and cluster; earcon and CW vocabulary;
help text. Split for REVIEW, not for speed — an in-memory dictionary is the same
speed whichever file the entry came from. Stated explicitly because otherwise
someone partitions for the wrong reason and splits a hot set across files.

Load strategy is a separate axis from partitioning: the first five load at startup
and stay resident (a few thousand short strings cost nothing, and frequency and
meter announcements fire many times a second and can never wait on a file). Help
text loads lazily on first `Ctrl+F1`.

## Phase 2 — the store itself

A loader, a lookup, the fallback behaviour, and tests. Small and self-contained.
It ships with **zero strings extracted** and nothing calling it, which is the point:
it can land, be reviewed and be tested without touching a single user-facing line.

Tests that matter: a missing key returns the key; a malformed file fails loudly at
startup rather than silently returning empties (the corrupt-preset lesson from
#49); every key present in one verbosity tier is present in all three, or the
ladder has a hole nobody would hear until an operator switched tiers.

## Phase 3 — one domain, proven end to end

**Connect and session lifecycle**, and specifically because it is the noisiest and
best understood — #85, #87, #80 and #107 all worked on exactly this vocabulary, so
its wording is settled and any drift found is real drift rather than work in
flight.

This is the phase that proves the design. If the key shape is wrong, or
interpolation is awkward, or the verbosity ladder does not fit, one domain is a
cheap place to find out and a cheap place to change it.

The output transcript makes this verifiable in a way it would not have been last
week: run a connect with `--record`, and every string that came out of the store is
in the transcript with its origin. Extraction can be proven not to have changed the
voice.

## Phase 4 — the remaining five domains

Ordinary parallel work once Phase 3 has proven the shape. This is the fleet job,
and it is the part that does NOT fit before the load arrives.

**Agents must REPORT inconsistencies, never silently normalise them.** Two places
saying the same thing differently is a finding, and choosing which wording survives
is the owner's call, not an agent's. #71 was one instance of that drift and it was
found by ear, which does not scale.

## Why now is a good moment, and the one risk

The task deferred itself for a specific reason: it collides with every wording
change in flight, and doing it while tracks are editing user-facing text guarantees
merge pain and captures a voice that is still moving.

**That blocker is gone.** Sprint 33 is fully merged, no tracks are live, and #91
settled where help text lives.

The remaining risk is the reverse: a string store landing right before a build for
Don. Phases 1 and 2 are safe — they change no behaviour. **Phase 3 touches the
connect vocabulary, which is the first thing Don will hear.** So either Phase 3
lands and gets tested properly, or it waits until after Tuesday. It should not be
half-landed on Monday.

## Suggested shape for Saturday

Phase 1 and 2 in the morning, both reviewable and both safe. Phase 3 only if the
morning goes fast and there is time to run a real connect against it before the
load arrives. If the truck is early, stop — the bench session is time-boxed by
daylight and the string store is not.
