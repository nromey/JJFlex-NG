# RigSurface — the radio-in-the-loop harness

Tier 3 of the test harness. Tiers 1 and 2 prove the interface is reachable and
that keys route. Neither proves the radio did anything. This closes that gap:
command it, read the radio's own state back, assert it actually changed.

Bench radio is a FLEX-8600. **No antenna is connected.**

Build and run from the repository root:

```
dotnet build tools/RigSurface/RigSurface.csproj -c Debug
dotnet run   --project tools/RigSurface/RigSurface.csproj -- selftest
```

`selftest` needs no radio and checks the transmit classifier, the status parser
and the ownership table. Run it before trusting anything else here.

---

## The architecture, and the trap it exists to avoid

**This tool is an observer, never a second operator.**

Connecting to a FlexRadio makes you a MultiFlex client with your own handle.
That is unavoidable and it is fine. What is not fine is forgetting what it
means, and the failure mode is genuinely nasty because it looks exactly like
success.

If this tool opened its own connection, created its own slice, set that slice's
mode and asserted the mode changed — the test would pass, every time, and would
say nothing whatsoever about whether JJ Flexible can change a mode. It would be
inspecting its own reflection. Nothing downstream can catch that.

So every piece of state has to be classified before it is asserted on, and the
classification is in `StateOwnership.cs`. Print it with:

```
dotnet run --project tools/RigSurface/RigSurface.csproj -- ownership
```

### Station-global

One value for the whole radio. Transmit power, tune power, mic gain, mic source,
mic boost and bias, the compander, the speech processor, VOX, the monitor, the
CW keyer settings, the interlock wiring, the ATU's memories setting, the radio's
name and callsign, tracking notch filters, binaural receive.

Every connected client sees the same number. Any client that writes it changes it
for everyone. Reading it from our own connection is completely honest — there is
only one of them and it belongs to the radio, not to whoever asked.

**So exercising station-global state from our own connection proves the real
thing.** That is most of what `surface exercise` does.

### Client-owned

Lives on an object carrying a `client_handle`: slices, panadapters, audio
streams.

Here is the part worth stating carefully, because the obvious framing is
slightly wrong and the correction is what makes the composed test possible.

Client-owned state is **globally observable and privately owned**. Any client
can read slice 3's mode; the radio broadcasts it to everybody. What is private
is not visibility, it is *authority*: only slice 3's owner should write it.

That means an observer with a connection of its own **can honestly verify the
application's slices**, provided it attributes every object by handle. It does
not need the application's cooperation to read. What it must never do is create
its own slice and assert on that.

The receiver surface is all here: mode, filter edges, AGC, noise blanker,
wideband noise blanker, noise reduction in five separate flavours, the automatic
notch filters, APF, RIT, XIT, receive and transmit antenna, tuning step, audio
level, mute and pan, squelch, frequency lock, and the slice's own transmit flag.

### Telemetry

The radio reporting on itself. Interlock state, ATU tune result, meter
descriptors, capability counts, whether the passband may be changed right now.
Never written, never restored, only observed.

### Unknown

Anything not in the table. Treated as unknown and never written. The default is
the safe answer on purpose.

---

## The composed mode — the highest-value test in the sprint

Noel's framing: *exercise a hotkey or action, see if the radio did what it was
supposed to.*

That needs two halves. The UI driver presses a key in the real running JJ
Flexible. This asks the radio whether it happened. Neither half is sufficient
alone, and this half never writes anything.

The seam is **file-based and asynchronous**, deliberately. The two processes do
not take turns and neither blocks on the other:

```
RigSurface surface mark --out before.json
   ... driver presses the key and waits for the interface to settle ...
RigSurface surface diff --since before.json --owner "JJFlex"
```

`--owner` accepts either a client handle or a fragment of the program name the
client registered with, so the diff can be narrowed to the application's own
objects and nobody else's.

For a whole key sweep, `surface watch` is better than a mark and diff per key.
It records every change the radio reports with a millisecond timestamp, for the
duration of the sweep, on one connection:

```
RigSurface surface watch --seconds 300 --out radio-trace.txt
```

The driver writes its own timestamped log of what it pressed and when, and the
two are correlated afterwards. One connection, no round trips, tolerant of
latency.

If a driver would rather block than correlate, `surface await` does that:

```
RigSurface surface await --field slice.0.mode --equals CW --timeout 3000
```

**A note on interpreting a diff that shows nothing.** Three different things
produce that, and they are not equally interesting: the key never reached a
handler, the handler never reached the radio, or the change is one the radio
does not report. The third is real — several DSP levels are writable with no
status key at all, listed below.

---

## Reading from the radio, not from our own cache

Everything in `RadioState` arrived on the wire from the radio. Nothing in it is
ever populated from a value we sent. There is exactly one mutation path and it
is called from exactly one place, the socket reader thread.

This is not fastidiousness. The vendored FlexLib keeps a local cache of slice
state and dedups a set against it, so a command the radio rejects can leave the
library reporting the value we asked for. A test that reads back through FlexLib
would let a broken command path pass. That is why this tool speaks raw wire.

Two related traps, both in the vendor library and both worth knowing:

- Setting a slice's mode to what the library already believes it to be sends
  **nothing at all**.
- A **locked** slice swallows every tune command client-side. The library returns
  without sending, and the radio never hears it.

---

## What the wire actually calls things

Status keys and set keys are different vocabularies as a rule, not as an
exception. This cost real time to establish and every case is recorded in the
ownership table's notes. The ones that bite:

- Mic gain reads `mic_level` and writes `miclevel`. No underscore on the way out.
- Slice frequency reads `RF_frequency` and writes with `slice tune`, not
  `slice set`.
- Slice filter edges read `filter_lo` and `filter_hi` and write with `filt`,
  both edges in one command. Transmit filter edges read `lo` and `hi` and write
  `filter_low` and `filter_high`.
- The transmit monitor reads `sb_monitor` and writes `mon`.
- AM carrier reads `am_carrier_level` and writes `am_carrier`.
- The radio's name reads `nickname` and writes `radio name`.
- Keyer speed reads `speed` and writes `cw wpm`. All the CW settings report under
  `transmit` and are written with the top-level `cw` verb.
- Mic source, boost, bias and accessory all report under `transmit` and are
  written with the top-level `mic` verb.
- The newer noise reduction family reports under short names and writes under
  long ones: `nrl` writes `lms_nr`, `anfl` writes `lms_anf`, `nrs` writes
  `speex_nr`, `rnn` writes `rnnoise`.
- Slice lock is not a value at all. It is `slice lock N` and `slice unlock N`.

### Things that are not where you would look for them

- **RF gain and band are panadapter properties**, not slice properties. They are
  per-SCU. The slice-level `rfgain` setter in the vendor library is marked
  obsolete *and* is malformed — it emits `slice set0 rfgain=...` with no space,
  so it could never have worked.
- **There is no attenuator concept anywhere in this API.** Preamp and attenuator
  are one signed dB figure, `rfgain`, on the panadapter. The `pre` key that sits
  beside it is an opaque string the vendor library never parses and never writes.
- **There is no MOX status key.** The radio never reports one. Transmit state is
  synthesised from `interlock state`, which is what the vendor library does and
  what `Guards.ReadTransmitState` does. An observer waiting for a `mox` key waits
  forever and concludes, silently and permanently, that the radio never
  transmits.
- **Split and VFO A/B do not exist on the wire.** They are application constructs
  layered over two slices. The only radio-side notion of which slice transmits is
  the slice's own `tx` flag plus `interlock tx_client_handle`. There is nothing
  to assert, which is itself the finding.
- **Some DSP levels are write-only.** `lms_nr_level`, `lms_anf_level`,
  `speex_nr_level` and `nrf_level` can be set and have no status key. They can be
  commanded and never read back. The harness reports these as not observable
  rather than assuming the write took.

### Wire format details that are easy to get half right

- Meter status is **hash-delimited**, not space-delimited. Splitting it on spaces
  finds one meter and silently loses the rest.
- A released slice is announced as **`in_use=0`**. There is no "removed" token
  for slices.
- Values carry embedded spaces as **U+007F**. Station and profile names arrive
  that way in both directions.
- The handle banner arrives as **bare hex** while `client_handle` is
  **0x-prefixed**. Comparing them without normalising is a silent way to conclude
  that none of your own slices are yours.
- Only meter **descriptors** arrive over the command channel. The readings travel
  over UDP as VITA-49, so this tool knows what every meter means and never what
  any of them currently says.

---

## Meters, and why the inventory prints the source

```
dotnet run --project tools/RigSurface/RigSurface.csproj -- meters
```

Each meter is printed with its **source** as well as its name. That is the point
of the command rather than a decoration.

A fact wired to the wrong instrument reads perfectly plausibly. It has a
sensible range, it moves when you expect it to move, and it produces a
confidently wrong diagnosis that nothing downstream can catch. This project has
already lost days to exactly that — a microphone meter that was structurally
incapable of seeing PC audio, read as though it were the microphone level that
mattered.

Printing source alongside name makes "this number comes from the analog
microphone's converter" visible instead of inferred.

---

## The guards

**Not transmitting, checked before every assertion.** Not once at the start. A
run walks dozens of fields over a minute or more and the operator can pick up a
microphone at any point during it. The check is a dictionary lookup against a
model the radio is already pushing at us, so there is no excuse for doing it
less often.

The check is three-state. `Unknown` — no interlock status, or a value we do not
recognise — is treated exactly like `Transmitting`. An unreadable condition is
never counted as a safe one.

If the radio has gone quiet for more than a few seconds, the guard proves the
link with a round trip before trusting anything cached. A dead socket looks
exactly like a calm radio.

**Refuse under MultiFlex with another operator connected.** This applies to the
*exercising* mode, which changes station state. It deliberately does **not**
apply to observe mode, where the application being connected is the entire
point.

**Never write another client's objects.** Enforced in the restore path and in the
exercise path, by handle.

---

## Snapshot and restore

`RigStateScope` is an `IDisposable` scope, not a call at the end of a happy path.
The only way to skip it is to skip the `using`. This is Noel's own station, and a
harness that abandons a half-configured radio is worse than no harness, because
the next person to key it does not know what changed.

Restore is **verified, not assumed**. Every write is followed by waiting for the
radio to report the old value back. A write the radio silently ignores is
reported as DID NOT STICK rather than counted as a success. The report is bullets
and prose, and it goes to standard error automatically when it is not clean, so a
failed restore is loud even if nobody reads the return value.

What it will not do, all deliberate:

- It will not write a field with no documented write path.
- It will not touch an object belonging to another client.
- It will not recreate a slice that vanished. Slice changes are known not to
  persist; a released slice returns on reconnect from the radio's own global
  profile. The harness must not "fix" that by writing a profile. **Writing
  station profiles is not this tool's business.**

Prove the restore path against the safest field there is, the radio's nickname —
cosmetic and instantly reversible:

```
dotnet run --project tools/RigSurface/RigSurface.csproj -- snapshot --prove
```

If restore cannot put *that* back, nothing here should be trusted with anything
that matters.

---

## The transmit harness

**Built now. Runs almost nothing yet.** The Palstar DL-2000 dummy load is on
order and is not here. Building it calmly in advance is much better than writing
it in a hurry next to a hot load.

```
dotnet run --project tools/RigSurface/RigSurface.csproj -- transmit plan
```

### Consent

Never automatic and never a side effect of constructing anything.
`TransmitConsent` has no public constructor. The only way to get one is
`Grant`, which reads the plan aloud — what it will transmit, at what power, for
how long in total — and requires the operator to type `TRANSMIT`. There is no
consenting by pressing return and no consenting by not answering.

### Power, approached from below and read back from the radio

The ceiling is enforced in code. Power is set to zero first, then raised to the
ceiling, then **read back from the radio's own status** and compared. If the
radio does not confirm a value at or below the ceiling, the harness refuses to
key.

That read-back is not belt and braces. The application's own first-run setup
writes full power unconditionally when it finds no saved profile, so "we set it
to one watt" and "the radio is at one watt" are genuinely different claims. This
harness only ever makes the second one.

### Duty cycle

A total key-down budget for the run, a ceiling on any single keying, and an
enforced cooling gap proportional to the transmission that preceded it. The load
will handle 400 watts continuously and 2 kilowatts for a minute, but an iterative
harness keys many times, so the budget is a ledger checked on every keying rather
than a number the author is trusted to respect.

### Keying is always paired

There is no way to key without also unkeying. The raw commands are not exposed;
callers get a bounded `KeyDown(duration)` whose unkey runs in a `finally` with a
watchdog behind it. The watchdog also fires on Ctrl+C and on process exit,
because if the process dies between key and unkey the radio stays keyed with
nobody watching.

The unkey path is classified as harmless by the transmit guard **on purpose**, so
that the way out of transmit can never be blocked by a spent budget, a revoked
consent, or anything else. A guard that can trap you in transmit is worse than no
guard.

### The ATU is rationed by relay wear, not by RF

It will tune with nothing connected at all. The cost is mechanical: physical
relays with a finite number of operations, spent whether or not any power went
anywhere. So the budget is a hard count per run, enforced in
`TransmitConsent.Authorise`, not a comment asking nicely.

**A dummy load cannot meaningfully test the ATU.** State this plainly to anyone
who expects otherwise. Into a matched fifty ohms the tuner finds a match
immediately, so all that gets exercised is the command path — did we ask, did it
answer, did the status move through `TUNE_IN_PROGRESS` to a result. Real tuning
behaviour needs a real mismatch, which means a real antenna. There is no
substitute and no amount of harness design creates one.

`transmit atu` therefore refuses to run and prints why. When the load arrives it
becomes runnable by removing exactly that refusal.

### What is sanctioned right now

One watt, single short keyings, used sparingly, with no antenna connected.
Nothing keys repeatedly at any power until the load is here.

---

## Subscriptions, and a hazard worth knowing

The subscribe list here is a deliberate **subset** of what the application
subscribes to, and contains only long-established topics.

Four of the newer subscriptions are firmware-gated in this repository precisely
because older firmware halts the Opus audio stream a couple of packets after
receiving a subscription it does not recognise. An observer that broke the
application's audio while measuring it would be worse than useless.

Note also that the subscribe token is not the status topic. `sub tx all` produces
`transmit ...` status, and `sub pan all` produces `display pan ...`. Panadapters
matter here because RF gain, preamp and band all live on them.

---

## Confidence

Every row of the ownership table carries where its classification came from:
inferred, read out of the vendor parser, or exercised against the bench radio and
confirmed. Anything not marked as confirmed on hardware has not been proven.

That is the same three-state honesty the chain analyzer is built on, applied to
the harness's own metadata. A classification that could not be checked is never
presented as one that was.
