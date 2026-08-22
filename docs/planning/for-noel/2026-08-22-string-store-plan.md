# The string store — a one-day fleet job

Written 2026-08-21 21:00, revised the same evening after Noel's call: **track it
and it lands Saturday. Sunday is the bench day with the load.**

That resequencing is right, and it also fixes the risk the first draft was worried
about. A string store half-landed on Monday, three days before Don's build, is
dangerous. A string store fully landed Saturday, with Sunday and Monday to test it,
is ordinary.

## The size, measured

**732 `Speak`/`Output` call sites. 850 `AutomationProperties.Name` in XAML. 449
XAML `Content=`. 60 help files. About 2,031 sites.** Too many for one session, which
is exactly what parallel tracks are for — six domains, six tracks, no two touching
the same files.

## What makes this safe now, and did not before

The historic reason #65 kept being deferred: externalise two thousand strings and
you cannot tell whether you changed the app's voice. Verifying it meant listening
to the whole app twice and remembering.

**The output transcript removes that.** Record a session before the extraction,
record the same session after, diff the speech events. Every string that came from
the store is in the transcript with its text and origin. Matching transcripts prove
the voice is unchanged; a differing line is either an intended consolidation or a
bug, and it is named rather than hunted.

**So the acceptance test for every track is a transcript diff, not a listen.** Any
track that cannot produce one for its domain has not finished.

## Serial first — these cannot be parallelised

### Step 1: the five decisions (no code)

**Key shape.** Hierarchical, behaviour-describing, never screen-position
describing. `connect.smartlink.offer_local_only` survives a redesign;
`settings.tab3.checkbox2` does not. Keys reach translation files and bug reports,
where they are read by people who cannot see the screen.

**Missing-key fallback.** Show the key. Never empty, never silent — silence is
invisible to the operator who most needs the text, and a key on screen is a bug
report that writes itself.

**Verbosity ladders are DATA.** Several utterances already pick between Chatty,
Terse and Critical in a `switch`. Those three belong in the store as a set under
one key. This is the single strongest argument for the whole job: the ladder
becomes reviewable as a ladder.

**Interpolation: named placeholders.** `{radio}` and `{freq}`, never `{0}` and
`{1}` — a translator reordering a sentence breaks positional ones silently.

**Six partitions, split for REVIEW not speed.** An in-memory dictionary is the same
speed whichever file it loaded from. Say this out loud or someone splits a hot set
across files chasing an imaginary gain. Load strategy is a separate axis: the five
non-help domains load at startup and stay resident (frequency and meter
announcements fire many times a second and can never wait on a file); help text
loads lazily on first `Ctrl+F1`.

### Step 2: the store, with zero strings in it

Loader, lookup, fallback, tests. Lands with nothing calling it, so it is reviewable
in isolation. Tests that matter: a missing key returns the key; a malformed file
fails LOUDLY at startup rather than returning empties (the #49 corrupt-preset
lesson); and every key present in one verbosity tier is present in all three, or
the ladder has a hole nobody hears until an operator switches tiers.

**Both steps must be finished and merged before any track starts.** They are the
shared contract; six tracks inventing it independently is six different stores.

## Then six tracks, in parallel

- **Track A — connect and session lifecycle.** The noisiest and best-understood
  vocabulary; #85, #87, #80 and #107 already settled its wording, so drift found
  here is real drift, not work in flight. **Also the highest-stakes**, because it is
  the first thing Don hears Tuesday.
- **Track B — audio and DSP.**
- **Track C — settings and per-radio config.**
- **Track D — logging and cluster.**
- **Track E — earcon and CW vocabulary.** Pairs with #113's registry: if earcons
  gain display names, they belong in this store, not a second one.
- **Track F — help text.** By far the largest set, and the only lazily-loaded one.

### Rules every track brief must carry

**Verify your base commit.** Name the specific SHA. Four of five agents on
2026-08-21 were handed worktrees cut from an old commit, and one built an entire
feature against a library deleted four days earlier. `git log --oneline -1`, then
`git merge --ff-only <sha>` if stale, and say so in the report.

**REPORT inconsistencies, never silently normalise them.** Two places saying the
same thing differently is a finding. Choosing which wording survives is the owner's
call. #71 was one instance of that drift and it was found by ear, which does not
scale — the entire point of the store is that it stops needing ears.

**Do not improve wording while extracting.** Extraction and editing are separate
passes. A track that does both makes the transcript diff useless, because every
intentional change hides a possible accident.

**Produce the transcript diff.** Before and after, same session, for your domain.
That is the deliverable, not the file.

## The schedule this now implies

- **Saturday** — steps 1 and 2 in the morning, six tracks after. Load arrives
  between 14:00 and 18:00; when it lands, run **Test 0 only** (two keyings, about
  five minutes) so the meter chain is proven before Sunday depends on it.
- **Sunday** — the bench day. Tests 1 through 7 with the load.
- **Monday** — build, full test pass, upgrade-over-existing check (#179).
- **Tuesday** — Don's radio returns.

## The one thing to protect

If Saturday runs long, **Track A is the one to hold back**, not the one to rush.
Everything else can land half-done and be finished Monday without touching what Don
hears on connect. Track A cannot.
