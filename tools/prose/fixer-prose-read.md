# The words the transmit checks say, read aloud

Everything the tool says, in the order an operator meets it, with the values it fills in shown as realistic examples. This file is for listening to. Editing it does nothing — the file to edit is tools/prose/fixer-prose.md.

# The page around the checks

## Transmit tests — Test TX-4K2P — title 1

Transmit tests — Test TX-4K2P

## Transmit tests — heading 1

Transmit tests

## Your test ID is TX-4K2P. Everything this run records… — paragraph 1

Your test ID is TX-4K2P. Everything this run records carries it, so keep it with any email about this problem.

## Stop everything — button 1

Stop everything

## two passed — text 1

two passed

## two with problems found — text 2

two with problems found

## two skipped — text 3

two skipped

## two could not run — text 4

two could not run

## two not yet run — text 5

two not yet run

## two tests. Two passed, three not yet run — text 6

two tests. Two passed, three not yet run

## Nothing has keyed the radio. — text 7, when transmit count is at most 0

Nothing has keyed the radio.

## The radio has been keyed once this run. — text 8, when transmit count is 1

The radio has been keyed once this run.

## The radio has been keyed two times this run. — text 9, otherwise

The radio has been keyed two times this run.

## Using this page — heading 1

Using this page

## How to move through the tests, and how to leave — disclosure summary 1

How to move through the tests, and how to leave

## Each test is a heading. Your screen reader's heading keys… — bullet 1

Each test is a heading. Your screen reader's heading keys move between them, Tab reaches the controls, and F6 or Shift+F6 jumps between the sections of the page.

## The tests run in order. Each answer you give and each… — bullet 2

The tests run in order. Each answer you give and each test you run carries you to the next thing to do, so you can walk the whole run without going backwards.

## To leave, press Escape or close the window. Once anything… — text 1, when run is saved

To leave, press Escape or close the window. Once anything has been recorded you choose on the way out: keep the run to pick up later from View or resume saved test runs, on the Fix menu, or leave without keeping it.

## To leave, press Escape or close the window. If the run… — text 2, otherwise

To leave, press Escape or close the window. If the run has recorded anything, you are asked before it is lost.

## Stop everything, above, is the emergency control: it… — bullet 3

Stop everything, above, is the emergency control: it stops whatever is happening right now, transmit included. If the radio is transmitting, the carrier drops first and questions come after.

## You said: A dummy load — text 1, when length is over 0

You said: A dummy load

## That is my answer — button 1

That is my answer

## Stage 3: Microphone check — text 1

Stage 3: Microphone check

## passed — text 1

passed

## problems found — text 2

problems found

## skipped — text 3

skipped

## could not run — text 4

could not run

## not yet run — text 5

not yet run

## This test transmits. — paragraph 1

This test transmits.

## Not run yet. — paragraph 2

Not run yet.

## What this test does — disclosure summary 1

What this test does

## Help with this test — link 1

Help with this test

## Run this test again — text 1, when again

Run this test again

## Run this test — text 2, otherwise

Run this test

## The reason you choose changes what the report can say… — paragraph 1

The reason you choose changes what the report can say about the rest of the run, so pick the one that is actually true.

## Why are you skipping this stage? — question above the choices 1

Why are you skipping this stage?

## Skip this stage — button 1

Skip this stage

## Checked at 2026-08-29 14:02 UTC. Re-run; this replaces an… — paragraph 1

Checked at 2026-08-29 14:02 UTC. Re-run; this replaces an earlier result.

## . Re-run; this replaces an earlier result. — text 1, when was re run

. Re-run; this replaces an earlier result.

## What to do: Turn PC audio on — text 2

What to do: Turn PC audio on

## Next: Stage 3: Injected transmit — text 1

Next: Stage 3: Injected transmit

## Go to the report — text 2

Go to the report

## This is the whole run as one document, every stage in… — paragraph 1

This is the whole run as one document, every stage in order, with the test ID at the top. That ID belongs to this report alone, so quote it in any message about the problem.

## Copy puts an email-ready version of the report on the… — paragraph 2

Copy puts an email-ready version of the report on the clipboard, which you can send to your radio's manufacturer. It separates what was measured from what was concluded, so their staff can read the numbers without taking anything on trust.

## If you are able to transmit in your radio manufacturer's… — paragraph 3

If you are able to transmit in your radio manufacturer's own software — for a Flex, that is SmartSDR — run the same test there before you send this, and see whether the problem follows you. If it does, say so in your message. A fault that shows up in the manufacturer's own software as well as in JJ Flexible Radio Access points at the radio or the station rather than at either program, and saying that up front will save you an exchange of emails.

## Copy the report as plain text — button 1

Copy the report as plain text

# The checks themselves

## at 25 watts — text 1

at 25 watts

## into ANT1 — text 2

into ANT1

## I want to skip this stage for now. — the label (operator choice)

I want to skip this stage for now.

## I want to skip this stage for now. — what skipping it means (operator choice)

The report will say this stage was not run, and the overall answer is weaker for it.

## I can't speak directly into my radio — it is somewhere else. — the label (narrows fault domain)

I can't speak directly into my radio — it is somewhere else.

## I can't speak directly into my radio — it is somewhere else. — what skipping it means (narrows fault domain)

The radio being remote rules out speaking into it directly, but a microphone on this computer can still be measured, so a comparison is still possible. This narrows where the fault can be.

## I don't have access to a microphone at all. — the label (leaves question open)

I don't have access to a microphone at all.

## I don't have access to a microphone at all. — what skipping it means (leaves question open)

With no microphone there is nothing to compare the injected audio against, so whether your own voice would get through is left open.

## Stage 0 Audio setup and receive — its name

Audio setup and receive

## Stage 0 Audio setup and receive — the question it asks

What is your audio doing right now, and is sound reaching you from the radio?

## Stage 0 Audio setup and receive — what this check does

Two halves, and they answer different questions. The first reads the open audio stream directly: host API, input and output device, sample rate, channel count. Not your saved settings — the stream itself. Those two disagree more often than anyone expects, and when they do, the disagreement is usually the fault. You would never spot it on a settings page. The second walks the receive chain — the slice you are listening to, the radio's outputs and their levels, how the audio reaches you, and how much has actually been arriving over the network. Proving receive first is what makes the transmit tests after it readable, and the receive evidence belongs in the report whether or not you came here about receive. Nothing here keys the radio.

## Stage 0 Audio setup and receive — what pressing Run will do

Running this takes a quick reading of the audio path on this computer, then walks the receive chain and reports how much audio has been arriving from the radio. Nothing transmits.

## Stage 0 Audio setup and receive · Can you hear the radio right now? — the question it asks

Can you hear the radio right now?

## Stage 0 Audio setup and receive · Can you hear the radio right now? — why it matters

You're the best person to help prove this test. If you can hear the radio, it proves that the whole receive path is working. By proving the receive path, we can move to the next tests which will help us zero in on what's going right and what's going wrong with your radio's transmission paths.

## Can you hear the radio right now? · I can hear the radio — the label

I can hear the radio

## Can you hear the radio right now? · I hear nothing from the radio — the label

I hear nothing from the radio

## Can you hear the radio right now? · No radio is connected — the label

No radio is connected

## Stage 0 Audio setup and receive · Open the full audio device picker — the label

Open the full audio device picker

## Stage 1 Microphone test — its name

Microphone test

## Stage 1 Microphone test — the question it asks

Is sound from your microphone arriving in this computer?

## Stage 1 Microphone test — what this check does

This listens to your microphone with the radio out of the picture entirely, and reports the peak in dBFS along with the integrated loudness in LUFS. It settles the first link in the chain before anything downstream is blamed for it. The reading is also kept as a baseline, because stage 4 is judged against it: a quiet result there means something quite different depending on whether your microphone measured well here.

## Stage 1 Microphone test — what pressing Run will do

Running this measures your room's noise in a quiet moment, then counts you in with three tones and listens while you talk. Nothing transmits.

## Stage 2 Transmitter test — its name

Transmitter test

## Stage 2 Transmitter test — the question it asks

Does the radio produce RF when it keys a tune carrier?

## Stage 2 Transmitter test — what this check does

The radio keys a tune carrier — a steady unmodulated signal it generates itself — so no microphone, computer audio or streaming takes any part. Forward power and SWR are read while it is keyed. If RF appears, the transmitter is working and whatever is wrong lies somewhere in the audio path. If no RF appears, you never had an audio problem at all, and no amount of microphone testing would have found it. Nothing is transmitted until you have said what is connected to the antenna port.

## Stage 2 Transmitter test — what pressing Run will do

Running this counts down with three tones, then keys the radio's own tune carrier at 25 watts into ANT1 for about two seconds.

## Stage 2 Transmitter test · Change the tune power — the label

Change the tune power

## Stage 3 Injected transmit — its name

Injected transmit

## Stage 3 Injected transmit — the question it asks

Does audio reach the radio when your microphone is bypassed?

## Stage 3 Injected transmit — what this check does

Tones and a generated voice are sent to the radio with your microphone taken out of the path, and the radio's own SC_MIC meter is watched to see what arrives. This test and stage 4 differ in exactly one thing, which is whether your microphone is involved. If this one works and stage 4 does not, your microphone is the problem. If neither works, your microphone is not the problem, and the fault lies between this computer and the radio.

## Stage 3 Injected transmit — what pressing Run will do

Running this counts down with three tones, then keys the transmitter at 25 watts into ANT1 for several seconds and sends tones and a recorded voice through it. Your microphone stays out of the path.

## Stage 3 Injected transmit · Change the transmit power — the label

Change the transmit power

## Stage 4 Spoken transmit — its name

Spoken transmit

## Stage 4 Spoken transmit — the question it asks

Does your voice reach the radio through your microphone?

## Stage 4 Spoken transmit — what this check does

You speak, and the radio's SC_MIC meter is watched to see what arrives. This is the same measurement stage 3 made, with your microphone put back into the path — that one difference is what makes the pair worth running. The result is read against your stage 1 microphone reading rather than judged on its own, so a quiet result here on a microphone that measured well earlier points somewhere quite specific.

## Stage 4 Spoken transmit — what pressing Run will do

Running this counts you in with three tones, then keys the transmitter at 25 watts into ANT1 for about two seconds while you speak into your microphone.

## Stage 4 Spoken transmit · Change the transmit power — the label

Change the transmit power

## Transmit — the introduction

Work forward from stage 0. What each stage finds feeds the ones after it — stage 1 measures your microphone, and stage 4 is judged against that measurement rather than on its own. Stage 0 also walks your receive chain, so the report carries receive evidence whether or not receive is what brought you here. Jump around if you want; the report records what was skipped. Stages 0 and 1 do not key the radio.

## Transmit · What is the antenna socket connected to right now? — the question it asks

What is the antenna socket connected to right now?

## Transmit · What is the antenna socket connected to right now? — why it matters

Nothing transmits until you answer this question. Into a real antenna, or through an amplifier, the tests that transmit keep the power at 25 watts or less. Answering that nothing is connected, or that you are not sure, keeps them parked — everything else still runs.

## What is the antenna socket connected to… · A dummy load — the label

A dummy load

## Antenna — the label

An antenna, and transmitting a short low-power test into it is fine

## Amplifier — the label

An amplifier — the radio feeds it before anything reaches an antenna or a load

## What is the antenna socket connected to… · Nothing, or I am not sure — the label

Nothing, or I am not sure

## Transmit · What is the antenna socket connected to right now? — the question, when the radio is somewhere else, when length is over 0

You are connected remotely. The radio will transmit on ANT1 — what is connected to ANT1 at that station right now?

## Transmit · What is the antenna socket connected to right now? — the question, when the radio is somewhere else, otherwise

You are connected remotely. What is connected to the antenna port at that station right now?

## Transmit · What is the antenna socket connected to right now? — the question, when the radio is somewhere else, when length is over 0

The radio will transmit on ANT1. What is connected to ANT1 right now?

## Dummy load — the label

A dummy load — someone at the station has confirmed it is connected

## Antenna — the label

An antenna — someone at the station has confirmed it, and a short low-power test into it is fine

## Amplifier — the label

An amplifier the radio feeds — someone at the station has confirmed it

## What is the antenna socket connected to… · I have not confirmed what… — the label

I have not confirmed what is connected

## Transmit · What is the antenna socket connected to right now? — why it matters, when the radio is somewhere else

Nothing transmits until you answer this question. You are not at that station, so every answer here states what someone there has confirmed with you — the report will say your answer came over a remote session, on someone else's word. Whatever is connected, the tests that transmit keep the power at 25 watts or less, because a confirmation relayed from a distance is not the same as seeing the socket. Answering that you have not confirmed keeps them parked — everything else still runs.

# What the audio setup check finds

## Mme in use — what is wrong (us)

Currently, you have selected the MME audio subsystem. It records perfectly well, but it will not tell you the truth about your hardware: Windows resamples behind it and reports its own converted format back, so the 44.1 kHz shown above may be 48 kHz at the device itself. Every level measured in this run would belong to that converter rather than to your microphone.

## Mme in use — what to do about it (us)

Switch to WASAPI

## Mme in use — what is wrong (nobody here)

Currently, you have selected the MME audio subsystem, and this computer offers no WASAPI to move to. Recording works normally; the format MME reports simply does not have to match what the hardware is really doing.

## Mme in use — what to do about it (nobody here)

Nothing here can change that. Read every level in this run as approximate — they describe Windows' resampling as much as your microphone.

## No input selected — what is wrong (us)

You have not selected an input device, so nothing you say can reach the radio.

## No input selected — what to do about it (us)

Use Microphone (USB Audio CODEC)

## No input anywhere — what is wrong (operator)

You have not selected an input device, and Windows is not offering one to choose.

## No input anywhere — what to do about it (operator)

Plug a microphone in, then run this stage again.

## Pc audio off — what is wrong (us)

PC audio is currently switched off, so nothing at all leaves this computer for the radio — not your microphone, and not the test tone either.

## Pc audio off — what to do about it (us)

Turn PC audio on

## Mic profile empty — what is wrong (us)

No mic profile is loaded on the radio. It will key up and transmit silence. Receive is unaffected, and nothing you did caused this — a Flex arrives from the factory this way.

## Mic profile empty — what to do about it (us)

Load a working profile

## Windows muted — what is wrong (operator)

Windows itself has your microphone muted. This is not the radio and not this application: the mute is in Windows, and it has to be cleared there.

## Windows muted — what to do about it (operator)

Unmute it in Sound settings, then run this stage again.

## Privacy blocked — what is wrong (operator)

Windows privacy is blocking desktop apps from the microphone. The device is fine; Windows will not hand it over.

## Privacy blocked — what to do about it (operator)

Settings, Privacy, Microphone — allow desktop apps, then run this stage again.

## Unplugged — what is wrong (operator)

The microphone you have selected is reporting itself as unplugged.

## Unplugged — what to do about it (operator)

Check the cable and the connector, then run this stage again.

## Hears nothing — what is wrong (operator), when remote radio

You hear nothing from the radio, even though PC audio is on — so the receive path is not delivering sound to your ears.

## Hears nothing — what to do about it (operator), when remote radio

Check this computer's output device and its volume first. If the radio's audio genuinely is not arriving, the transmit tests will likely fail for the same reason, so settle this before reading them.

## Hears nothing — what is wrong (operator), otherwise

You hear nothing from the radio.

## Hears nothing — what to do about it (operator), otherwise

Check the volume on the radio and on this computer, and where your receive audio normally comes out. A silent receiver is worth settling before the transmit results are read.

## Config open mismatch — what to do about it (us)

Reopen on the configured device

## you chose Microphone (USB Audio CODEC), but Microphone… — text 1

you chose Microphone (USB Audio CODEC), but Microphone (USB Audio CODEC) is what is open

## you chose  through WASAPI, but the stream is actually… — text 2

you chose  through WASAPI, but the stream is actually running on WASAPI

## Your settings and the open stream disagree — the open… — text 3

Your settings and the open stream disagree — the open input device is not the one you chose. Something overrode your choice, most often a device that disappeared and came back on a different index.

## ; and — text 4

; and

## an unnamed audio subsystem — text 1

an unnamed audio subsystem

## the WASAPI audio subsystem — text 2

the WASAPI audio subsystem

## No stream is open. Nothing below was measured — it is all… — text 1

No stream is open. Nothing below was measured — it is all read back from your settings, which is exactly the thing this stage exists to distrust.

## You are recording from — text 2

You are recording from

## an unnamed device — text 3, otherwise

an unnamed device

## using  through WASAPI — text 4

using  through WASAPI

## , at VALUE kHz — text 5

, at VALUE kHz

## in mono — text 6

in mono

## in stereo — text 7

in stereo

## across one channels — text 8

across one channels

## Playback is going to Speakers (Realtek High Definition… — text 9

Playback is going to Speakers (Realtek High Definition Audio).

## You can hear the radio, and over a remote connection that… — text 1

You can hear the radio, and over a remote connection that one fact proves the whole receive path at a stroke — the link is up, audio is flowing, and your output device is playing it. A silent transmit now points at the microphone side rather than at the connection.

## You can hear the radio. With the radio in the room that… — text 2

You can hear the radio. With the radio in the room that may be its own speaker rather than this computer, so it says less about the computer's audio path than it would over a remote connection.

## You said no radio is connected, so the tests that need… — text 3

You said no radio is connected, so the tests that need one will wait until it is.

## Audio setup, read from what is actually open — text 1

Audio setup, read from what is actually open

## Open host API: not read — text 2

Open host API: not read

## Open input device: not read — text 3

Open input device: not read

## Open output device: not read — text 4

Open output device: not read

## Open sample rate: 48,000 — text 5

Open sample rate: 48,000

## 48,000 Hz — text 6, when open sample rate hz is over 0

48,000 Hz

## not reported — text 7, otherwise

not reported

## Open channels: one — text 8

Open channels: one

## not reported — text 9, otherwise

not reported

## Configured host API: not read — text 10

Configured host API: not read

## Configured input device: not read — text 11

Configured input device: not read

## WASAPI available: yes — text 12

WASAPI available: yes

## PC audio: on (remote radio) — text 13

PC audio: on (remote radio)

## (remote radio) — text 14, when remote radio

(remote radio)

## (local radio) — text 15, otherwise

(local radio)

## Microphone profile: has settings — text 16

Microphone profile: has settings

## has settings — text 17, otherwise

has settings

## Muted in Windows: no — text 18

Muted in Windows: no

## Blocked by Windows privacy: no — text 19

Blocked by Windows privacy: no

## Device reports unplugged: no — text 20

Device reports unplugged: no

## Operator hears the radio:  You said you can hear the… — text 21

Operator hears the radio:  You said you can hear the radio.

## could not be read — text 1, when v is null

could not be read

## yes, by their own account — text 1

yes, by their own account

## no — they hear nothing — text 2

no — they hear nothing

## no radio connected, by their own account — text 3

no radio connected, by their own account

## not asked, or not answered — text 4

not asked, or not answered

# What the microphone and transmit checks find

## The microphone was not measured — the measurement did not… — text 1

The microphone was not measured — the measurement did not produce a result, so whether sound is arriving cannot be said either way.

## Yes — sound is arriving in this computer from Microphone… — text 2

Yes — sound is arriving in this computer from Microphone (USB Audio CODEC) through WASAPI. No radio was involved in this test, so this stands on its own whatever happens later.

## your microphone — text 3

your microphone

## , through  through WASAPI — text 4, when length is over 0

, through  through WASAPI

## No — the measurement ran and heard nothing above the… — text 5

No — the measurement ran and heard nothing above the noise floor from Microphone (USB Audio CODEC).

## your microphone — text 6

your microphone

## Mic silent — what is wrong (operator)

Your microphone was measured and nothing arrived.

## Stage 0 has already named the cause: Windows has this… — text 1

Stage 0 has already named the cause: Windows has this microphone muted, and Windows privacy is blocking it as well. Its findings in the stage 0 card carry the steps — clear both, then run this stage again.

## Stage 0 has already named the cause: Windows itself has… — text 2

Stage 0 has already named the cause: Windows itself has this microphone muted. Its finding in the stage 0 card carries the steps — clear the mute, then run this stage again.

## Stage 0 has already named the cause: Windows privacy is… — text 3

Stage 0 has already named the cause: Windows privacy is blocking desktop apps from the microphone. Its finding in the stage 0 card carries the steps — allow it, then run this stage again.

## Windows is not muting this microphone and is not blocking… — text 4

Windows is not muting this microphone and is not blocking it — stage 0 checked both — so what is left is the cable, the connector, or the device itself. Check those, then run this stage again.

## Check the cable, the Windows mute, and the Windows… — text 5

Check the cable, the Windows mute, and the Windows microphone privacy setting, then run this stage again.

## Microphone, no radio involved — text 1

Microphone, no radio involved

## Measured: yes — text 2

Measured: yes

## Device: Microphone (USB Audio CODEC) — text 3

Device: Microphone (USB Audio CODEC)

## not reported — text 4

not reported

## Host API: Microphone (USB Audio CODEC) — text 5

Host API: Microphone (USB Audio CODEC)

## not reported — text 6

not reported

## Peak: minus 12 — text 7

Peak: minus 12

## Noise floor: minus 12 — text 8

Noise floor: minus 12

## Tx no power — what is wrong (operator)

This is not an audio problem, and no amount of microphone testing will find it. A tune carrier is the radio's own signal, with nothing of yours in the path, and no RF came out — so the fault sits upstream of anything the remaining stages can measure.

## Tx no power — what to do about it (operator)

Check the antenna connection, the band, whether the slice is set to transmit, and whether anything is inhibiting transmit. Then run this stage again.

## Tx load suspect — what is wrong (operator)

The transmitter works, but a large share of its power came straight back instead of going out.

## Tx load suspect — what to do about it (operator)

Check what is connected to the antenna port before transmitting again.

## Injected transmit, microphone bypassed — text 1

Injected transmit, microphone bypassed

## Conditioning chain: VALUE — text 2

Conditioning chain: VALUE

## could not be read — text 3, when conditioning active is null

could not be read

## The spoken test did not produce a measurement, so whether… — text 1

The spoken test did not produce a measurement, so whether your voice reaches the radio cannot be said either way.

## Yes — your voice, through Microphone (USB Audio CODEC),… — text 2

Yes — your voice, through Microphone (USB Audio CODEC), reached the radio.

## your microphone — text 3

your microphone

## No — your voice did not reach the radio. But when the… — text 4

No — your voice did not reach the radio. But when the microphone check ran, sound from Microphone (USB Audio CODEC) WAS arriving in this computer, so the microphone itself is the least likely culprit. The difference lies between this computer and the radio — and the injected test just walked that same path, so read the two side by side.

## that microphone — text 5

that microphone

## No — your voice did not reach the radio, and the… — text 6

No — your voice did not reach the radio, and the microphone test heard nothing either. Start at the microphone: until sound arrives in this computer, nothing further along can carry it.

## No — your voice did not reach the radio, and because the… — text 7

No — your voice did not reach the radio, and because the microphone test was not run, whether the microphone or the path beyond it is at fault cannot be separated. Run the microphone test; it splits this question in two.

## Spoken transmit, microphone in the path — text 8

Spoken transmit, microphone in the path

## Attempted: yes — text 9

Attempted: yes

## Reached the radio: yes — text 10

Reached the radio: yes

## not measured — text 11, otherwise

not measured

## Device: Microphone (USB Audio CODEC) — text 12

Device: Microphone (USB Audio CODEC)

## not reported — text 13

not reported

## Host API: Microphone (USB Audio CODEC) — text 14

Host API: Microphone (USB Audio CODEC)

## not reported — text 15

not reported

## Microphone baseline: VALUE — text 16

Microphone baseline: VALUE

## none — the microphone test was not run — text 17, when mic baseline is null

none — the microphone test was not run

## sound was arriving from Microphone (USB Audio CODEC) — text 18, when audio arrived

sound was arriving from Microphone (USB Audio CODEC)

## the microphone — text 19, when audio arrived

the microphone

## measured, and nothing arrived — text 20, otherwise

measured, and nothing arrived

## attempted, but nothing was measured — text 21, otherwise

attempted, but nothing was measured

## Antenna socket, as stated by the operator: a dummy… — text 1, otherwise

Antenna socket, as stated by the operator: a dummy loadNEWLINE

## not measured — text 1, when is na n

not measured

## yes dBFS — text 2, otherwise

yes dBFS

# When the tool refuses to transmit

## declared A dummy load — text 1, when has value

declared A dummy load

## , over a remote session, by an operator not at the station — text 2, when load declared remotely

, over a remote session, by an operator not at the station

## That step is not meant to transmit, so nothing was sent. — text 1

That step is not meant to transmit, so nothing was sent.

## The test was stopped, so nothing was transmitted. Start… — text 2

The test was stopped, so nothing was transmitted. Start it again if you want to carry on.

## There is no test running, so nothing was transmitted. — text 3

There is no test running, so nothing was transmitted.

## That request belongs to an earlier test, so nothing was… — text 4

That request belongs to an earlier test, so nothing was transmitted. Close this and start again.

## The radio is not reachable, so nothing was transmitted. — text 5

The radio is not reachable, so nothing was transmitted.

## The radio is already transmitting, so nothing more was… — text 6

The radio is already transmitting, so nothing more was sent. Let it finish, or press Stop everything.

## A transmit is already running, so nothing more was sent. — text 7

A transmit is already running, so nothing more was sent.

## Nothing was transmitted, because you have not said yet… — text 8

Nothing was transmitted, because you have not said yet what the antenna socket is connected to. Say what is connected, and this step will run.

## Nothing was transmitted. You said you have not confirmed… — text 9, when load declared remotely

Nothing was transmitted. You said you have not confirmed what the antenna socket at that station is connected to — and this tool never transmits into an unknown load, least of all at a station you are not at. Ask someone at the station what is connected, answer the antenna question again, and this step will run.

## Nothing was transmitted. You said nothing is connected,… — text 10, otherwise

Nothing was transmitted. You said nothing is connected, or that you are not sure — and this tool never transmits into an unknown load. Connect a dummy load or an antenna, answer the antenna question again, and this step will run.

## a real antenna — text 11, when load kind is antenna

a real antenna

## an amplifier — text 12, otherwise

an amplifier

## Nothing was transmitted. You declared a dummy load, and… — text 13

Nothing was transmitted. You declared a dummy load, and the radio's power for this step could not be read — into a dummy load these tests only transmit when the power is known to be 25 watts or less.

## Nothing was transmitted. The radio's power for this step… — text 14

Nothing was transmitted. The radio's power for this step is 25 watts, and you declared a dummy load. Into a dummy load these tests transmit at 25 watts or less — turn the power down, or declare a dummy load, and this step will run.

## Nothing was transmitted. The dummy load was declared over… — text 15

Nothing was transmitted. The dummy load was declared over a remote session, and the radio's power for this step could not be read — on a declaration made from a distance these tests only transmit when the power is known to be 25 watts or less.

## Nothing was transmitted. The radio's power for this step… — text 16

Nothing was transmitted. The radio's power for this step is 25 watts, and the dummy load was declared over a remote session — on the word of someone at the station, not your own eyes. On a remote declaration these tests transmit at 25 watts or less; turn the power down and this step will run.

## That step has already transmitted once. Choose Run again… — text 17

That step has already transmitted once. Choose Run again if you meant to repeat it.

## Transmit requests are arriving faster than they should… — text 18

Transmit requests are arriving faster than they should be, so this one was refused. That usually means something is repeating itself rather than anything you did.

## This test has transmitted for about 2 seconds altogether,… — text 19

This test has transmitted for about 2 seconds altogether, which is as much as one run allows. Start a new test to carry on.

## This test has transmitted as many times as one run… — text 20

This test has transmitted as many times as one run allows. Start a new test to carry on.

# The report

## JJ Flexible Transmit test report — text 1

JJ Flexible Transmit test report

## Test ID: TX-4K2P — text 2

Test ID: TX-4K2P

## Run started 2026-08-29 14:02 UTC. — text 3

Run started 2026-08-29 14:02 UTC.

## This copy of the report was written 2026-08-29 14:02 UTC. — text 4

This copy of the report was written 2026-08-29 14:02 UTC.

## What was found, and what to do — its name

What was found, and what to do

## No stages have been run yet, so there is nothing to… — text 1

No stages have been run yet, so there is nothing to report. Start at the first stage.

## Nothing that ran found a problem it could name. The… — text 2

Nothing that ran found a problem it could name. The stage-by-stage detail below says what was actually measured.

## Stage 3, Microphone check — text 3, otherwise

Stage 3, Microphone check

## FIXED during this run at 2026-08-29 14:02 UTC — it… — text 4

FIXED during this run at 2026-08-29 14:02 UTC — it became: the input is now WASAPI

## JJ Flexible offers a one-press fix for this (Turn PC… — text 5

JJ Flexible offers a one-press fix for this (Turn PC audio on).

## What to do: Turn PC audio on — text 6

What to do: Turn PC audio on

## How much of the test was done — its name

How much of the test was done

## The stages were done in this order: the open input device… — text 1

The stages were done in this order: the open input device is not the one you chose.

## , then — text 2

, then

## Not attempted at all: the open input device is not the… — text 3

Not attempted at all: the open input device is not the one you chose. That weakens the overall answer: each stage rules something in or out, and the stages after it depend on knowing that.

## stage 3 (Microphone check) — text 4

stage 3 (Microphone check)

## Stage 3Microphone check was not run. The reason given: "A… — text 5

Stage 3Microphone check was not run. The reason given: "A dummy load" EFFECTTEXT

## These results span 2 minutes: the oldest is DESCRIBE at… — text 6

These results span 2 minutes: the oldest is DESCRIBE at 2026-08-29 14:02 UTC and the newest is DESCRIBE2 at 2026-08-29 14:07 UTC. Things may have changed in between — a microphone swapped, a setting altered — so do not read them as one snapshot.

## Nothing has been done yet. — text 7

Nothing has been done yet.

## Changes made during this run — its name

Changes made during this run

## These settings were changed while the test was running,… — text 1

These settings were changed while the test was running, each one offered on the page and applied on a press — never silently. Results recorded after a change describe the changed setup, not the one the run started with.

## stage 3 (Microphone check) — text 2, when st is not null

stage 3 (Microphone check)

## It became: the input is now WASAPI — text 3, when succeeded

It became: the input is now WASAPI

## The fix was attempted and DID NOT succeed: the input is… — text 4, otherwise

The fix was attempted and DID NOT succeed: the input is now WASAPI

## Stages recorded after this change: the open input device… — text 5

Stages recorded after this change: the open input device is not the one you chose.

## Stage 3: Microphone check — its name

Stage 3: Microphone check

## This stage has not been run. — text 1

This stage has not been run.

## Attempted at 2026-08-29 14:02 UTC and could not run. — text 2

Attempted at 2026-08-29 14:02 UTC and could not run.

## Run at 2026-08-29 14:02 UTC. This stage was re-run; this… — text 3

Run at 2026-08-29 14:02 UTC. This stage was re-run; this result replaces an earlier one.

## . This stage was re-run; this result replaces an earlier… — text 4, when was re run

. This stage was re-run; this result replaces an earlier one.

## JJ Flexible offers a one-press fix for this (Turn PC… — text 5

JJ Flexible offers a one-press fix for this (Turn PC audio on).

## What to do: Turn PC audio on — text 6

What to do: Turn PC audio on

## stage 3 (Microphone check) — text 1, otherwise

stage 3 (Microphone check)

## Microphone (USB Audio CODEC), skipped — text 2

Microphone (USB Audio CODEC), skipped

## Microphone (USB Audio CODEC), could not run — text 3

Microphone (USB Audio CODEC), could not run

# What the window says while you work

## Transmit tests — JJ Flexible — text 1

Transmit tests — JJ Flexible

## Transmit tests — text 2

Transmit tests

## The transmit noise gate is currently off, so no threshold… — text 1

The transmit noise gate is currently off, so no threshold applies.

## The transmit noise gate is holding its deliberately low… — text 2

The transmit noise gate is holding its deliberately low default threshold of minus 45 dBFS dB, because no transmitted speech has taught it your room's noise floor yet.

## Your transmit noise gate's threshold is currently minus… — text 3

Your transmit noise gate's threshold is currently minus 45 dBFS dB, derived from the noise floor measured in your own transmitted audio (NOISEFLOORLUFS LUFS, plus a 6 dB margin). Stated here so you can see where it came from; whether it is right for your room is not judged by this test.

## no reference recording is installed on this computer — text 1

no reference recording is installed on this computer

## the reference recording could not be prepared: the device… — text 2

the reference recording could not be prepared: the device was not available

## Transmit tests — JJ Flexible — text 1

Transmit tests — JJ Flexible

## The transmit tests need the Microsoft Edge WebView2… — text 2

The transmit tests need the Microsoft Edge WebView2 runtime, which is not installed on this computer. Everything they would have checked can still be reached from the Audio Workshop and from Diagnostics.

## Transmit tests — JJ Flexible — text 3

Transmit tests — JJ Flexible

## There is no saved run to continue. — text 1

There is no saved run to continue.

## Run TX-4K2P is a set of Transmit tests, and this is the… — text 2

Run TX-4K2P is a set of Transmit tests, and this is the Transmit tests. It can still be read and exported from the saved test runs list.

## Run TX-4K2P was recorded with a different set of tests… — text 3

Run TX-4K2P was recorded with a different set of tests from the ones this version of JJ Flexible offers, so continuing it would mix measurements from two different runs. It can still be read and exported from the saved test runs list.

## Whether run TX-4K2P can be continued could not be worked… — text 4

Whether run TX-4K2P can be continued could not be worked out, so it has not been opened. It can still be read and exported from the saved test runs list.

## Something went wrong handling that. Nothing was… — text 1

Something went wrong handling that. Nothing was transmitted.

## You said: A dummy load. — text 1

You said: A dummy load.

## You said: A dummy load. — text 2

You said: A dummy load.

## That test has already run, and its measurement is kept.… — text 3

That test has already run, and its measurement is kept. To measure again, choose Run this test again.

## That fix did not succeed: the input is now WASAPI — text 4, otherwise

That fix did not succeed: the input is now WASAPI

## Something is already running. Wait for it to finish, or… — text 1

Something is already running. Wait for it to finish, or press Stop everything.

## Something went wrong running that test. Nothing was… — text 2

Something went wrong running that test. Nothing was transmitted.

## Do you want to stop the test? — text 1, otherwise

Do you want to stop the test?

## Deletes this run's saved record. Everything recorded so… — text 2, when kept

Deletes this run's saved record. Everything recorded so far is gone for good, including measurements that keyed the radio — taking those again costs real transmission.

## , including measurements that keyed the radio — taking… — text 3, when transmit count is over 0

, including measurements that keyed the radio — taking those again costs real transmission.

## Ends the test and closes the window. This run was not… — text 4, otherwise

Ends the test and closes the window. This run was not being saved, so nothing is kept.

## Closes the window and keeps the run. Continue it later… — text 5, when offers resume later

Closes the window and keeps the run. Continue it later from View or resume saved test runs, on the Fix menu — everything already recorded stays, though the test running right now stops and is not recorded, and the report will say the tests were done in more than one sitting.

## , though the test running right now stops and is not… — text 6, when run in progress

, though the test running right now stops and is not recorded

## Transmit tests — JJ Flexible — text 7

Transmit tests — JJ Flexible

## stopped to resume later — text 1

stopped to resume later

## stopped to resume later — text 1

stopped to resume later

## window.jjflex && window.jjflex.receive(JSONENCODE) — text 1

window.jjflex && window.jjflex.receive(JSONENCODE)

## The report is on the clipboard, as plain text, ready to… — text 1

The report is on the clipboard, as plain text, ready to paste into an email.

## That help page could not be opened. — text 1

That help page could not be opened.

## No radio is connected, so there is no power to change. — text 1

No radio is connected, so there is no power to change.

## The power window could not be opened. — text 2

The power window could not be opened.

## The audio device list could not be opened. — text 1

The audio device list could not be opened.

# Leaving the checks

## Exit without saving — text 1

Exit without saving

## Continue the test — text 2

Continue the test

## Nothing changes. You go back to the tests where you left… — text 3

Nothing changes. You go back to the tests where you left off.

## Stop tests and resume later — text 4

Stop tests and resume later

# Saved runs

## Saved test runs — JJ Flexible — text 1

Saved test runs — JJ Flexible

## Every test run saves itself as it happens, named by its… — the text

Every test run saves itself as it happens, named by its test ID. Open one to read its report, rename it so you can find it again, continue one you stopped part-way, export it to send to someone, or delete it. JJ Flexible keeps the newest 200 runs; export anything you want to keep forever.

## _View report — text 2

_View report

## View report — text 3

View report

## Continue this run — text 4

Continue this run

## Saved test runs — text 5

Saved test runs

## One line per saved run: its name or test ID, when it… — text 6

One line per saved run: its name or test ID, when it started, how many stages have results, and whether it finished. Newest first. Enter opens the report.

## That could not be done: the device was not available — text 1

That could not be done: the device was not available

## The settings folder could not be resolved, so no saved… — text 1

The settings folder could not be resolved, so no saved runs can be shown.

## No test runs have been saved yet. Runs save themselves as… — text 2

No test runs have been saved yet. Runs save themselves as they happen — run a test and it will appear here. One could not be read.

## One saved run. One could not be read. — text 3, when count is 1

One saved run. One could not be read.

## two saved runs, newest first. One could not be read. — text 4, otherwise

two saved runs, newest first. One could not be read.

## One saved run could not be read and is not listed. — text 1, when unreadable is 1

One saved run could not be read and is not listed.

## one saved runs could not be read and are not listed. — text 2, otherwise

one saved runs could not be read and are not listed.

## No run is selected. — text 1

No run is selected.

## Transmit test report — TX-4K2P — text 1

Transmit test report — TX-4K2P

## Copy report — text 2

Copy report

## Since this run stopped: Five checks. Two passed, three… — text 1

Since this run stopped: Five checks. Two passed, three not yet run.NEWLINENEWLINE

## Since this run stoppedFive checks. Two passed, three not… — text 2

Since this run stoppedFive checks. Two passed, three not yet run.

## Name cleared. The run goes back to its test ID, TX-4K2P. — text 1, when length is 0

Name cleared. The run goes back to its test ID, TX-4K2P.

## Renamed to A dummy load. It keeps its test ID, TX-4K2P. — text 2, otherwise

Renamed to A dummy load. It keeps its test ID, TX-4K2P.

## The new name could not be saved. — text 3

The new name could not be saved.

## Run TX-4K2P has a result for every test, so there is… — text 1

Run TX-4K2P has a result for every test, so there is nothing left to continue. Open it to read the report.

## Continue run Shack bench, Thursday? Its five recorded… — text 2

Continue run Shack bench, Thursday? Its five recorded results are kept and the remaining REMAINING tests are yours to run. This is recorded as a second sitting, so the report will say the tests were not all done at one go.

## Saved test runs — JJ Flexible — text 3

Saved test runs — JJ Flexible

## Export test run Shack bench, Thursday — its name

Export test run Shack bench, Thursday

## Exported to TX-4K2P.html. — text 1, when written

Exported to TX-4K2P.html.

## The export failed. Nothing was written. — text 2, otherwise

The export failed. Nothing was written.

## Delete run Shack bench, Thursday (test ID TX-4K2P)? Its… — text 1

Delete run Shack bench, Thursday (test ID TX-4K2P)? Its report and measurements will be gone for good — there is no undo.

## Saved test runs — JJ Flexible — text 2

Saved test runs — JJ Flexible

## Run TX-4K2P deleted. — text 3

Run TX-4K2P deleted.

## Run TX-4K2P could not be deleted. — text 4

Run TX-4K2P could not be deleted.

