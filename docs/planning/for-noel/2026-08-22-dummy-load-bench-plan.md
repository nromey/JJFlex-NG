# Bench session plan — 2026-08-22, the dummy load arrives

Written 2026-08-21. UPS delivers **Saturday 2026-08-22 between 14:00 and
18:00**. Your wife is out during the day through late Sunday afternoon, so
Saturday evening and all of Sunday daytime are available.

**RUN THIS AS TWO SESSIONS, NOT ONE.**

**Saturday, whenever it lands.** Connect the load, confirm the connection, and
run **Test 0**. That is two keyings and about five minutes — worth doing even if
the truck shows at 17:45, because Test 0 decides whether the meter chain reads
at all, and every other test in this document is void if it does not. Finding
that out Saturday evening beats discovering it mid-session Sunday morning. If
there is daylight left after it, carry on into Tests 1 and 2 — those three
together produce every number stage 12 is currently guessing at.

**Sunday daytime.** Whatever Saturday did not reach, then Tests 3 through 7.
Those are the interesting ones and deserve fresh attention rather than being
rushed at the end of Saturday.

**Why the split matters:** if Test 0 fails, you have a full day to find out why
the meters lie instead of losing the whole session. Monday then stays free for
the build and full test pass, which keeps a real buffer before **Don's radio
returns Tuesday 2026-08-25** and he needs a build.

This plan exists because on 2026-08-21 three attempts to run an automated test
produced zero valid data, and every failure was the same shape: an instrument
that reported something it had not measured. So every test below states **what
it measures**, **what result would falsify it**, and **what its positive control
is** — the thing that proves the instrument was working at all.

Read the safety section before anything else.

---

## Safety envelope

- **Nothing keys until the dummy load is physically connected and confirmed.**
  Not a jumper into open air, not "just a quick one".
- Start at **minimum power**. Every test says explicitly when to raise it.
- **Short keyings.** Seconds, not minutes, except where a test names a duration.
- The 8600 is the radio. No antenna is connected and none should be.
- If the amplifier gets cabled in, it stays **OFF** until Test 7 and not before.
- Anything unexpected: unkey first, ask questions second.

## Before you start

1. Dummy load connected, and you have physically confirmed the connection —
   not assumed it from the last time you looked.
2. Jumpers: two if the amp goes in line, one otherwise. You said you have four
   or five.
3. JJ Flexible running on a **fresh build**, connected to the radio.
4. **TURN ON "Record the meter stream (for bench sessions)"** in Settings,
   Diagnostics. This is new as of 2026-08-21 and **every test below depends on
   it**. Per-meter lines are now OFF by default and a detailed capture alone
   will NOT produce them — that was deliberate, because the old behaviour wrote
   roughly 1 MB per minute and buried everything else (#170).

   With the switch on you get one line per meter per second carrying **min, max,
   last and sample count**, e.g. `micData: min=-120 max=-3.5 last=-18 n=42`.
   Coalesced, not raw — but the **max** is preserved, which is what transmit
   diagnosis actually cares about. Costs about 26 KB per minute.

   If the meter lines are not appearing, check this switch before suspecting
   anything else.

---

## The positive control that governs everything

Before any measurement is believed, we need to know the meter chain is
**reading** and not just **reporting a stored number**.

Here is the specific reason to worry, observed live on 2026-08-21 while the
radio was merely **receiving**:

    forwardPower:0
    SWRData:1
    compPeakData:-150

`SWRData:1` while receiving is the problem. A dummy load should read an SWR of
about 1.0 — so if we key into the load and see 1.0, **that number is
indistinguishable from the idle value it already had.** It would look like a
perfect result and prove nothing. Sprint 33 found seven transmit-chain facts
initialised below their own declared floors and published as measurements;
this is exactly that trap.

**So Test 0 comes first and everything else depends on it.**

---

## Test 0 — prove the meters actually move

**Measures:** whether forward power and SWR change in response to reality, or
merely hold plausible constants.

**Method:** with the dummy load connected, key at minimum power for two or three
seconds. Watch `forwardPower` and `SWRData`. Then key again at a clearly higher
power setting — say a quarter scale — for the same duration.

**Positive control:** forward power must be **different between the two
keyings**. Not "plausible" — *different*, and in the right direction.

**Falsified if:** forward power reads the same at both power settings, or stays
at 0, or SWR never leaves 1. Any of those means the meter chain is not reading
and **every later test in this document is void.** Stop and say so.

**Record:** the two forward power figures and the two power settings.

---

### Test 0 RESULT — 2026-08-22, ~12:25. PASSES.

Load: Palstar DL-2000, connected and confirmed, set for 500 W, fan verified
working but never needed. Noel afterwards: "the load basically sneezed at those
power levels, it didn't get hot it didn't turn on the fan." Tests 1 through 7
have large thermal margin.

**READ THIS BEFORE READING ANY METER LINE.** The meter stream logs
`forwardPower` as the RAW meter value, which is **dBm, not watts**. The app
itself is correct — `FlexBase.ForwardPowerWatts` is `DBmToWatts(_PowerDBM)`, so
everything spoken or displayed to the operator is real watts. But anyone reading
the trace directly must convert, and the field name invites exactly the mistake.

That mistake was made on the first pass at this result, and it manufactured a
finding that did not exist: reading the dBm figures as watts produced an
apparent 45 W ceiling and a suspected #164 acked-but-not-applied fault. Both
evaporated on conversion. Watts = 10 ^ ((dBm − 30) / 10).

Four keyings, requested power against measured, converted:

- **0 W requested** → 23.5 dBm ≈ **0.22 W**
- **25 W requested** → 42.7 dBm ≈ **18.6 W**
- **50 W requested** → 45.5 dBm ≈ **35.5 W**
- **100 W requested** → 47.4 dBm ≈ **55.0 W**

Each setting was confirmed applied — the radio echoed `RFPower` back at every
step, so there is no evidence of a discarded write.

**The positive control is satisfied.** Forward power rises monotonically with
the request across a 4× range, and a zero request produced very nearly zero.
The meters are reading, not holding a constant.

**SWR settles it harder.** It moved between the −25 idle sentinel, 1.000 and
**1.008**. That third figure is the important one: this document was written
around the worry that a dummy load reads 1.0 and that number is
indistinguishable from the idle value it already had. A stored constant cannot
produce 1.008.

**Every test below is live.**

### The real question this leaves for Tests 1 and 2

Measured output runs consistently at roughly **55 to 70 percent of requested**:
25 → 18.6, 50 → 35, 100 → 55. Consistent enough to be a relationship rather than
noise, and worth understanding before any absolute power figure in this document
is trusted.

Candidates, in no order: dummy load calibration, the radio's own forward-power
meter calibration, band-dependent output, or the 8600 genuinely not reaching
rated output into this load at 14.1 MHz.

**Procedure fix for tomorrow, learned the hard way:** write each requested value
down BEFORE keying it. The pairing could not be reconstructed afterwards today,
and only the trace's own `RFPower` lines rescued it.

---

## Test 1 — the dead-key floor (#163)

**Measures:** what forward power a genuine dead key reports on this radio. This
is the number the entire transmit-chain analyzer is currently guessing at.

**Why it matters:** `Radios/ChainChecks/tx-chain-rules.txt` fires a
"no power is leaving your radio" rule below 0.1 W, and its own comment admits
*"the floor a Flex reports on a genuine dead-key has not been measured."* Every
threshold in stage 12 is a guess until this number exists.

**Method:** dummy load connected. Set power to **minimum**. Key with no
modulation for three seconds. Record forward power. Then repeat with the power
setting at 0 if the radio allows it.

**Record:** forward power at minimum setting, and at setting 0.

**Known figure to compare against:** 0.036 W was measured at minimum power
setting previously — which is 15.56 dBm, and essentially the top of the legal
transverter drive range. If tomorrow's figure differs materially from 0.036,
that itself is a finding.

**Falsified if:** Test 0 did not pass. Otherwise this test cannot fail, it can
only produce a number — which is the point.

---

## Test 2 — the power curve

**Measures:** the relationship between the `rfpower` setting and actual watts
out, on this radio.

**Why it matters:** #163 established that stage 12 cannot judge power with a
single absolute threshold, because expected output depends on what is in the
path. It needs to compare measured output against **commanded** output, and
that comparison needs this curve.

**Method:** dummy load connected. Key three seconds at each of roughly six
settings spread across the range — minimum, 10, 25, 50, 75, 100. Record forward
power at each.

**Record:** six pairs of setting and measured watts.

**Falsified if:** the curve is not monotonic. If raising the setting ever
lowers measured power, stop and record exactly where.

---

## Test 3 — does the radio apply power writes? (#164)

**Measures:** whether `transmit set rfpower=N` is honoured when a properly
registered station client is connected.

**Why it matters:** on 2026-08-21 a raw TCP probe sent `transmit set rfpower=12`,
received `R|0|` — success — and the value **never left 0**, sampled every 250 ms
for three seconds. But that probe connected as a bare socket and never
registered as a GUI client (`tx_client_handle=0x00000000`, `tx_allowed=0`), so
the result is confounded. The radio may simply refuse transmit-side writes from
an unregistered connection while reporting success.

**Method:** with JJ Flexible connected normally, change the transmit power
through the app. Confirm the radio's reported `rfpower` actually changes. Then
change it again to a different value.

**Positive control:** the value must change **twice**, to two different numbers.
One change could be coincidence with something else.

**Falsified if:** the setting reports success and does not change. That would
confirm a genuine silent-success at the radio and is a significant finding
worth reporting upstream.

**Also worth knowing:** FlexLib's `RFPower` is an `int` in both directions, and
its parser uses `int.TryParse` — so if the radio ever reported a fractional
power, we would silently keep the stale value with the only trace going to
`Debug.WriteLine`. Not testable tomorrow, recorded so it is not forgotten.

---

## Test 4 — the Peak Watcher (#139)

**Measures:** whether the TX Peak Watcher is reading the real transmit drive or
the amplifier jack's ALC.

**Why it matters:** Sprint 33 Track D **settled** this as "the Peak Watcher
guards a jack, not the transmitter" — but settling it means the bug was
CONFIRMED, not fixed. And the supporting evidence was stark: `HWALC` returned
**7,345 readings of exactly 0.0** across a full transmission, against thresholds
of 0.5 and 0.8. A meter that never once reported a measurement scored as
perfect health.

**Method:** dummy load, no amplifier in line. Key at a power level that Test 2
showed produces real output. Watch `hwALCData` throughout.

**Positive control:** Test 2's forward power reading for the same setting. If
forward power moves and HWALC does not, HWALC is not watching the transmitter.

**Falsified if:** HWALC tracks power. That would mean Track D's conclusion is
wrong and should be said plainly.

**Record:** HWALC across the keying, and the matching forward power.

---

## Test 5 — the transmit audio chain, at real power

**Measures:** whether PC audio actually reaches the transmitter — the
honest-tx-audio saga, open longest, and the thing Don is blocked on.

**Method:** dummy load. PC audio on, microphone selected and confirmed. Key and
speak for five to ten seconds at a power level Test 2 proved produces output.
Watch `micData` and `micPeakData`.

**Positive control, and this one is essential:** speak, then **stop speaking
while still keyed**, then speak again. `micData` must move with your voice. A
steady reading during speech proves nothing; a reading that follows your voice
proves the chain.

**Falsified if:** `micData` sits at its floor (−120 was observed at idle) while
you are speaking into a confirmed-working microphone.

**Note the trap already ruled out:** #140 claimed the TX stream was created
without a compression parameter. That was **falsified** — an 8600 answers
`compression=OPUS` to the bare command and shipping SmartSDR sends the identical
command. Do not re-chase it.

---

## Test 6 — a reference recording (#150)

**Measures:** nothing. It **creates** something: a deterministic voice sample so
future transmit results are comparable to each other rather than to memory.

**Method:** with the chain proven by Test 5, record a short fixed phrase
including your callsign. Same phrase, same mic, same levels, every future test.

**Why:** every transmit-audio judgement so far has been "does this sound right
today". A fixed input turns that into a comparison.

---

## Test 7 — the amplifier, only if cabled (#125)

**Do not start this until Tests 0 through 5 have passed.** If the meter chain is
suspect, adding an amplifier makes everything harder to interpret and adds real
risk.

**Measures:** whether amplifier meters attach and report.

**Known hazard before you start:** #137 — FlexLib formats one amplifier handle
**unpadded**, so meters silently fail to attach when the handle has a leading
zero. If amp meters do not appear, check the handle before assuming a cabling
fault.

**Method:** amp in line, dummy load after it, amp **OFF** first. Confirm the
radio still keys correctly through the amp bypass. Only then power the amp, at
minimum drive.

**Record:** whether amp meters appear at all, and the handle if they do not.

---

## What to bring back

For each test: the numbers, and whether the positive control passed. A test
whose control failed produces **no** result — not a bad result, no result. Say
so plainly rather than recording the number anyway.

If something surprises you, write down what you actually observed before
theorising about it. Most of 2026-08-21's wasted effort came from acting on a
plausible mechanism instead of a measured one.

## What is NOT in this plan

- Anything requiring an antenna. There is none and there should not be.
- On-air testing (#155). Separate task, needs a clear legal frequency and the
  identification question answered first.
- The transverter band model (#27). That session is explicitly zero-keying and
  does not need the dummy load at all, so it is not competing for this window.
