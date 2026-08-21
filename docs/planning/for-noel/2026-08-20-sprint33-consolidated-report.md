# Sprint 33 consolidated report — barefoot harness pileup

**20 August 2026.** Eleven parallel tracks built the three-tier test suite, and it
immediately found that the transmit analyzer had been lying in both directions.

---

## The one-sentence version

**Every confident static diagnosis made today was wrong. Every measurement was
right.** Each truth surfaced the same way: two independent channels disagreeing
until one of them turned out to be reading the wrong thing.

That is the sprint's actual product. Not the test suite — the ability to
GENERATE DISAGREEMENTS instead of relying on one reading being careful enough.

---

## What we found today

### The analyzer would have told Don two opposite lies

Seven of the fifty-seven facts the transmit analyzer collects were publishing
values the radio had never reported. Two are mirror images of each other.

**Forward power had no gate at all.** `_PowerDBM` initialises to minus 150 dBm —
*below the meter's own declared floor of 0.0*, a value the radio physically
cannot send. That converts to about 1e-18 watts and formats as "0". The
`no-power-out` rule fires below 0.1 W. So a meter that had simply never spoken
produced *"your radio is transmitting but almost no power is leaving it"* — in
the same evidence block where the meter fact correctly reported that the meter
had never spoken.

**SWR had no gate and no initialiser, publishing 0.** The SWR meter's declared
range starts at 1.0, so 0 was provably never a reading. `high-swr` tests "above
3." A silent meter read as a perfect antenna.

A false alarm about your transmitter, and a false all-clear about your antenna,
from the same defect at opposite ends.

All seven now gate on `HasReading`, with tests locking both halves — a silent
meter must not fire the rule, AND a meter genuinely at its floor still must.

The other five: `codec-mic` published 0 dBFS, which is full scale — the loudest
value on the scale. Three meter facts gated on the meter *existing* while reading
fields that only move if FlexLib's case-sensitive lookup found them, while the
gate asked case-insensitively — two conditions, one checked. And `meter-revpwr`
asked for `REVPWR` when the radio publishes `REFPWR`, so it was permanently
absent, telling operators in a high-SWR evidence block that their radio has no
reflected-power meter.

### The honest-tx-audio bug, finally measured rather than argued

During a five-second keying, the codec MIC meter read minus 120 to minus 70.3
dBFS while `SC_MIC` read minus 10.8 dBFS AT THE SAME INSTANTS. Sixty to a hundred
and ten decibels of gap.

The meter everyone stared at for two days was that far from the one actually
hearing the audio. The audio was fine the whole time.

### The ALC safety alert cannot fire. Ever.

The TX Peak Watcher guards `HWALC` — source `TX-` index 5, the amplifier jack.
Across a full transmission it returned **7,345 readings and every single one was
0.0**. Its thresholds are 0.5 and 0.8.

Meanwhile a plain `ALC` meter sits directly beside it at source index 0, and
swung 2.5 dB in the same window. The watcher is on the wrong meter, not the only
one. The checkbox says "ALC safety alerts"; operators believe they are protected
against overdriving and are not.

Also worth knowing: those thresholds are 0.5 and 0.8 — zero-to-one fractions
compared against decibels.

Not fixed, deliberately: you ruled on 2026-08-11 that HWALC stays surfaced as
amplifier ALC. The answer is a SECOND guard with its own wording, which is your
copy to approve.

### Four identical copies of every transmit meter, and both lookups take the first

With a station client connected the radio publishes **102 meters**, not the 35
predicted. Every transmit-chain meter appears **four times, once per slice** —
four `SC_MIC` at indices 24, 48, 72 and 91, byte-identical in their descriptors,
nothing to tell them apart.

`MeterInventory.Find` is first-name-wins. FlexLib's `FindMeterByName` is a
`FirstOrDefault`. The app subscribes to index 24 and never learns the others
exist.

**It works only because the first copy happens to be the one that streams.**
Nothing guarantees that. On a radio where it is not, `ScMicDb` sits at its
sentinel forever while three identical meters report normally — and every
diagnosis downstream is built on a meter that was never going to move. The same
silent-wrong-instrument shape as the codec MIC bug, one level down, masked by an
ordering coincidence.

Written up durably in the remarks on `MeterInventory.Find`, where the next person
reading meter code will land. Not fixed — choosing among identical descriptors
needs a rule nobody has established, and that is a design decision.

### Removing a radio could have deleted your entire settings tree

`SanitizeRadioId` treats `.` as legal, so a serial of `..` survives intact.
`Path.Combine(base, "radios", "..")` then resolves to the BASE DIRECTORY — the
parent of every radio's folder — and the destructive scope calls
`Directory.Delete(dir, recursive: true)` on exactly that.

Sprint 32 made that path keyboard-reachable. Nobody checked what was at the end
of it.

Two independent guards now: the sanitiser refuses dots-only ids, and removal
proves the target is strictly inside `radios\` before deleting. Verified in both
directions — removing either guard makes the new test fail.

### The S-meter reads "not reported by this radio" on a radio reporting it

A slice source index of minus 1 means "follow whichever slice I'm listening to."
Every default carries it; every migrated config carries it. But choices built
from a live reading copied the slice's REAL number, and `Matches` requires
equality. Minus 1 never equals 0.

So with a radio connected, no slice meter could match its own definition, and the
panel fell through to its unknown-meter fallback.

**The tone was right the whole time** — the tone engine resolves minus 1
correctly. Sound and screen disagreed, and only the screen talks to the operator.

The picker now offers an "Active slice" entry per slice meter, which makes the
setting every default ships with SELECTABLE for the first time rather than
reachable by accident.

### Your radio's floor is 36 milliwatts. The rule trips at 100.

At `rfpower = 0` — the minimum setting, not "off" — the 8600 measured 15.6 to
22.6 dBm across a real transmission. That is 0.036 to 0.182 watts, and a
standalone trace caught the range going to 30.6 dBm.

The `no-power-out` threshold of a tenth of a watt sits INSIDE that range rather
than below it. The rule cannot distinguish "barely transmitting" from "the
operator chose minimum power." A guard (`needs rf-power-setting above 0`) hides
it today — and that guard bites exactly the transverter and QRP case.

**This one needs your ruling.** What counts as "no power out" is a judgement with
verdict wording attached. It is now a measured number rather than a guess.

### One dialog crashed the entire test host

`ExportDialog_Loaded` sets `DialogResult`, which can only be set on a window
shown via `ShowDialog()`. Shown any other way it throws from inside WPF's render
path — unhandled, on the dispatcher.

Result: **zero tests discovered**, whole assembly aborted, reported as
`total=0 passed=0 failed=0`. Not one failure among many.

It also explains three separate mysteries from the same day: the "export log"
dialog that kept appearing and could not be accounted for; why `jjflexible.exe`
was never running when checked (it was `testhost.exe`); and Explorer windows
accumulating at the build output folder in step with test runs.

---

## The morning, before any of that

**Sprint 32 merged and shipped.** The connected-close farewell verified with a
radio attached and a continuous earcon running — the last check gating a build.

Then a design conversation produced tasks #144 through #152, including the
three-tier test suite that became this sprint. Four memory entries written, task
store reconciled, full backups across ten projects.

**Three tasks turned out to be already done** — closed by reading rather than
working. The repeat-message history shipped in Sprint 32; `Beep()` no longer
exists; the third additive synthesiser was already deleted. A backlog that
overstates itself makes every planning decision slightly wrong, so a post-sprint
reconciliation pass is worth making routine.

---

## The pattern worth keeping

Six confident static diagnoses were made today and every one was wrong:

- **#140 was not the root cause.** The TX stream genuinely lacks a compression
  parameter — but the 8600 answers an undeclared stream with `compression=OPUS`,
  shipping SmartSDR sends the identical bare command, and the whole question had
  been answered ten days earlier. Track G falsified its own headline using
  evidence already in the repository.
- **The Earcon Explorer was never broken.** I concluded that from a grep that
  found no `ConnectPhase` in the Workshop — but a registry-driven explorer never
  contains those names. Absence of the string was evidence FOR the registry.
- **The unaccounted dialog was not a credential picker.** I identified it from an
  incomplete window enumeration that cannot see modals.
- **Track B's "this build produced no log"** — it was logging the whole time,
  into a file the tool did not know about.
- **Track C's harness reported a clean restore** while leaving the radio altered,
  within a minute of first contact.
- **Track D's session selector skipped the file containing the reproduction**
  because a Detailed capture carries no boot header.

Each was caught the same way: SOMETHING DISAGREED WITH SOMETHING ELSE. Two
channels, two connections, two independent runs, a control press against an
automated one. Never by reading more carefully.

---

## What each track delivered

### Track A — Tier 1, dialogs walked in process

Built `JJFlexWpf.Tests`, an STA suite that constructs dialogs and walks their
automation trees. Established by measurement that **Tier 1 needs no permission
grant and cannot take the keyboard**: Win32 focus is per message queue, so
off-foreground `SetFocus` touches only the calling thread's record. Foreground
verified unchanged across runs.

Also measured two traps that would each have cost a day. `EnsureHandle` raises
`Loaded` but leaves a Window `Collapsed`, so a suite built on it reports every
dialog in the app as empty. And a private desktop is unreachable from an STA WPF
thread, because the CLR's `OleInitialize` got there first.

### Track B — Tier 2, the probe rescued and proven

The UIA probe that solved the silent Workshop was living in a temp scratchpad. It
now ships as `jjprobe` in the repository. 140 inventory rows expand to **243
concrete chords with zero residue**.

Injection proven end to end: `DoCommand:00000071` then `DoCommand:ShowFreq`.

Its first measurement was a FALSE POSITIVE — it credited its own window
activation as evidence the keystroke worked — caught only because the routing
channel disagreed with the UIA channel. It also displaced a security prompt its
own action had raised, which is now guarded against.

Two findings from it worth having: the key dispatcher already logs
`DoCommand:key not found:<key>` at Info level, which separates *the chord never
arrived* from *the chord arrived and nothing was listening*. And Slice F has no
leader jump — `Ctrl+J, Shift+F` collides with RX filter width, so an operator
with six or more slices cannot jump to the sixth at all.

### Track C — Tier 3, the radio surface

**The radio sends no status delta to the client that made the change.** It
broadcasts to every OTHER client. Proven both directions. So a single-connection
harness cannot verify its own writes — it reads back its own stale model while
believing it reads the radio.

That makes the composed observer arrangement not merely preferable but the only
sound one.

A 234-field before-and-after diff came back identical except the nickname, which
it could not clear and correctly refused to invent a value for.

It also found a safety bug in its own guard by re-reading rather than running:
the transmit classifier treated `xmit` as keying unconditionally, so `xmit 0` —
the UNKEY — was refused. The failure mode would have been keying the radio,
having the unkey refused, printing "the unkey command failed," and leaving it
transmitting.

### Track D — the analyzer's senses

Thirty existing tests proved the rules engine handles unreadable facts correctly.
None touched fact collection.

Final tally across all 57 facts: **33 verified true, 10 verified wrong and fixed,
24 unverified each with a stated reason.** Nothing previously reported as
verified was invalidated.

The sentinel bug reproduced on two independent keyings:
`SC_MIC=-150.0 SWALC=-150.0 fwd=22.1 dBm` — forward power already flowing while
both audio meters sat on the initialiser. It is the opening second of EVERY
transmission, which is exactly when someone debugging silent audio runs the
check.

It also answered your nickname questions. **Maximum is 15 characters** — 16 is
refused outright, not truncated, but nothing inspected the reply so a longer name
told the operator nothing. Now capped. **A space is silently removed**, not
truncated at: "ALPHA BRAVO" becomes "ALPHABRAVO".

### Track E — the triage that gives you your evenings back

44 numbered tests, but many bundle assertions from different tiers. Split
properly: **98 assertions — 45 Tier 1, 33 Tier 2, six Tier 3, and 14 human**, one
of which is not a test at all.

**A 44-test sitting becomes 13 things worth your ears.** Only six assertions
genuinely need the radio.

Two tests were already wrong WITHIN HOURS of being written — both because they
counted controls, which is the assert-invariants-not-specifics rule breaking in
practice. One has no possible proxy: About reading aloud renders in a WebView2
island an in-process tree walk cannot see into.

### Track F — how the application sounds

CW pitch follows the radio's sidetone or a configured tone. Six CW waveform
choices with equal-power normalisation, so changing shape changes character and
NOT level. A CW repeat key on `Ctrl+J, E`. And the sine-versus-modern voice set —
**two definitions of seven named voices and one switch**, so all 45 earcons follow
without a single call site changing.

It also modelled the farewell timeout properly: the flat 5000 ms is exceeded at
10, 12, 15, 25 AND 30 WPM. Only 20 and 40-plus fit. You sit in the one gap in the
middle, which is why yours works.

### Track H — the meters panel

Most of its brief was already fixed by Sprint 32 — which it discovered by
auditing rather than trusting, and that audit is what found the S-meter bug
above.

Real residuals fixed: an engine subscription made once but unsubscribed on EVERY
unload, so the first reload left the panel permanently deaf; and a test-tone
timer created per click, so a second press cut the first short.

Four wrong facts in help corrected, including `tuning-frequency.md` telling
operators that `Ctrl+M` opens memory channels.

### Track I — a known input, and a recorder worth keeping

A 127-second reference recording with a level anchor, phonetics, three levels of
the same sentence, plosives and a silence gap for the noise floor — **deliberately
carrying no callsign and no CQ**, so that if it ever escapes it announces itself
as a test recording twice.

Verified negative on the Jim question: **he built no PC-side audio recorder.**
Searched the current tree, the deleted `JJRadio/` tree, and all history. What you
were remembering is `CWMessages.vb` and its editor dialogs — a per-operator,
key-bound, labelled message library with substitution. That is Jim's design, it
is untouched, and it is exactly the shape #151 needs.

### Track J — three dead surfaces and a build script that never worked

The zip failure's error message was IMPOSSIBLE — the script already exits with
code 6 before the zip if the app is running. The real cause: `Compress-Archive`
lives in a script module that will not load under the default execution policy.
The old code checked an exit code and nothing else, so a truncated archive would
have shipped to Don.

TX Controls turned out to be wired-able rather than abandoned — `theRadio` was
`internal`, so the app side could not reach 13 of its 14 properties. Third
instance of one pattern.

And the biggest unwired find: `RadioPaneControl` has all ten delegates
unassigned, so **the Logging-mode radio pane permanently reports "no radio" even
with one connected.**

### Track K — slices that come back

Zero verbs are stubbed; the transport was never the gap. The real gap is that the
procedure is UNREACHABLE — which is what "I don't know what I need to do in JJ
Flexible to get it to stick" was telling us.

Found an undocumented silent auto-save: `saveNewGlobalProfile()` runs from
`Dispose()` and is the only path that writes a global profile with nobody
pressing anything. It captures the station at teardown, and under MultiFlex that
bakes another operator's slices into the profile loaded on every subsequent
connect.

It also declined to overturn Sprint 32's deliberate rejection of a disconnect
prompt — it built the offer as a setting that ships OFF.

---

## Decided today

- **Modern sounds stay the default**, classic sine becomes an option. Your listen
  produced the requirement: not "I like new," but "if I can have a preference, so
  can someone else."
- **The connect series is fine as it is.** Track F's four candidates get deleted
  at merge.
- **CW repeat walks its own history**, text messages only, prosigns excluded.
- **Waterfall may move ahead of Connect** — and its interaction model is now two
  queries rather than a display: *where is it quiet* for transmitting, *take me
  to the next signal* for listening.
- **Tier 3 reads meters from the trace**, not its own UDP stream — the more
  correct measurement, and no firewall change.
- **Authorisation is brokered** while a fleet is running: tracks report ready to
  the orchestrator, you get one ping per batch.

---

## Still open

- The `no-power-out` threshold, now that the floor is measured at 36 mW.
- **User-facing prose from four tracks**: the tone-set and CW labels (F), the
  reference script and its announcements (I), the destructive-remove wording (J),
  and the save-to-radio offer (K). The last one matters most — it is a prompt
  about shared station state.
- `Ctrl+J, E` has never been pressed on a real build.
- Whether the JJ key browser opens on `Ctrl+F1` or `Ctrl+J` then `F1`.
- Track B's settle rule watches raw trace growth, and a connected radio writes
  continuously — so it can never settle, and would mark all 199 sweep rows
  unreliable.
- **The command-level sweep.** The suite walks KEYS, so a command reachable three
  ways is tested once — and the untested routes are where the observed bugs live.

---

## State of the tree

Ten tracks merged into Track A. **67 commits, build clean, zero errors.**
`Radios.Tests` reports **223 passed, 0 failed**, up from 211 this morning.
`JJFlexWpf.Tests` cannot report until `ExportDialog` is fixed or the suite is
hardened against it.

The only real merge conflict was the predicted pair — Track F against Track K on
two files — and both sides turned out to be making the same argument about the
same list, in comments neither track's author had seen.

Cleared along the way: 16 stale worktrees, and 14 stale firewall rules identified
(four of them BLOCK rules, meaning those builds had no inbound UDP and therefore
no meters, silently). `C:\temp\jjclean.ps1` removes them when you want.

Task backlog now runs to #159.
