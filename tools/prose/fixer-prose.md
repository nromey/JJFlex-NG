# The words the transmit checks say

Everything the transmit checks put in front of an operator, pulled out of the code so it can be edited as writing. Change the words; the code keeps working.

## How to use this file

Every heading below is one thing the tool says. Under the heading is what it says, as one paragraph. Change the words, save the file, and run `tools\prose\prose apply` to put them back into the program. Nothing else you do here has any effect, so you can read straight through it without worrying about breaking anything.

Your screen reader's heading keys are the way around. Level one walks the sections; level two walks the sentences, one heading per sentence.

Lines that begin with a quote mark are for the tool, not for you. They carry the name it files each sentence under, so it can find its way back to the code. Leave them alone and ignore them; nothing in them is worth hearing.

Some sentences have a value the program fills in when it speaks — a power level, a test ID, an antenna port. Those show in braces, like `{RunId}`. Keep every one of them, and keep them in the order they appear; you can change every word around them. Those entries also carry a `Reads as` line showing the sentence with real values in it, which is what an operator actually hears.

If you change something the tool cannot write back, it refuses the whole run and tells you which sentence and what is wrong with it. It never writes a half-applied file.

To throw away everything you have changed here and start again from the program's current words, run `tools\prose\prose extract --force`.

# The page around the checks

The heading, the state-of-play sentence, the how-to bullets, the buttons and the words for each state a check can be in. This is the frame every check sits inside.

## Transmit tests — Test TX-4K2P — title 1

{Name} tests — Test {RunId}

> Reads as: Transmit tests — Test TX-4K2P
> Keep {Name} and {RunId}, in that order.
> fixer.page.render.title-1

## Transmit tests — heading 1

{Name} tests

> Reads as: Transmit tests
> Keep {Name}.
> fixer.page.header.heading-1

## Your test ID is TX-4K2P. Everything this run records… — paragraph 1

Your test ID is <strong>{RunId}</strong>. Everything this run records carries it, so keep it with any email about this problem.

> Reads as: Your test ID is <strong>TX-4K2P</strong>. Everything this run records carries it, so keep it with any email about this problem.
> Keep {RunId}.
> Keep the markup exactly as it is — it builds the page.
> fixer.page.header.paragraph-1

## Stop everything — button 1

Stop everything

> fixer.page.header.button-1

## two passed — text 1

{Count} passed

> Reads as: two passed
> Keep {Count}.
> fixer.page.summary.text-1

## two with problems found — text 2

{Count} with problems found

> Reads as: two with problems found
> Keep {Count}.
> fixer.page.summary.text-2

## two skipped — text 3

{Count} skipped

> Reads as: two skipped
> Keep {Count}.
> fixer.page.summary.text-3

## two could not run — text 4

{Count} could not run

> Reads as: two could not run
> Keep {Count}.
> fixer.page.summary.text-4

## two not yet run — text 5

{Count} not yet run

> Reads as: two not yet run
> Keep {Count}.
> fixer.page.summary.text-5

## two tests. Two passed, three not yet run — text 6

{Count} tests{Join}

> Reads as: two tests. Two passed, three not yet run
> Keep {Count} and {Join}, in that order.
> fixer.page.summary.text-6

## Nothing has keyed the radio. — text 7, when transmit count is at most 0

Nothing has keyed the radio.

> This runs on from the words before it, so it is spoken as part of a longer sentence.
> fixer.page.summary.text-7

## The radio has been keyed once this run. — text 8, when transmit count is 1

The radio has been keyed once this run.

> This runs on from the words before it, so it is spoken as part of a longer sentence.
> fixer.page.summary.text-8

## The radio has been keyed two times this run. — text 9, otherwise

The radio has been keyed {Count} times this run.

> Reads as: The radio has been keyed two times this run.
> Keep {Count}.
> This runs on from the words before it, so it is spoken as part of a longer sentence.
> fixer.page.summary.text-9

## Using this page — heading 1

Using this page

> fixer.page.how-to-section.heading-1

## How to move through the tests, and how to leave — disclosure summary 1

How to move through the tests, and how to leave

> fixer.page.how-to-section.disclosure-summary-1

## Each test is a heading. Your screen reader's heading keys… — bullet 1

Each test is a heading. Your screen reader's heading keys move between them, Tab reaches the controls, and F6 or Shift+F6 jumps between the sections of the page.

> fixer.page.how-to-section.bullet-1

## The tests run in order. Each answer you give and each… — bullet 2

The tests run in order. Each answer you give and each test you run carries you to the next thing to do, so you can walk the whole run without going backwards.

> fixer.page.how-to-section.bullet-2

## To leave, press Escape or close the window. Once anything… — text 1, when run is saved

To leave, press Escape or close the window. Once anything has been recorded you choose on the way out: keep the run to pick up later from View or resume saved test runs, on the Fix menu, or leave without keeping it.

> fixer.page.how-to-section.text-1

## To leave, press Escape or close the window. If the run… — text 2, otherwise

To leave, press Escape or close the window. If the run has recorded anything, you are asked before it is lost.

> fixer.page.how-to-section.text-2

## Stop everything, above, is the emergency control: it… — bullet 3

Stop everything, above, is the emergency control: it stops whatever is happening right now, transmit included. If the radio is transmitting, the carrier drops first and questions come after.

> fixer.page.how-to-section.bullet-3

## You said: A dummy load — text 1, when length is over 0

You said: {answered}

> Reads as: You said: A dummy load
> Keep {answered}.
> fixer.page.declaration.text-1

## That is my answer — button 1

That is my answer

> Reads as: That is my answer
> Keep {Id} and {MessageKind}, in that order.
> fixer.page.declaration.button-1

## Stage 3: Microphone check — text 1

Stage {Number}: {Title}

> Reads as: Stage 3: Microphone check
> Keep {Number} and {Title}, in that order.
> fixer.page.stage-label.text-1

## passed — text 1

passed

> fixer.page.status-phrase.text-1

## problems found — text 2

problems found

> fixer.page.status-phrase.text-2

## skipped — text 3

skipped

> fixer.page.status-phrase.text-3

## could not run — text 4

could not run

> fixer.page.status-phrase.text-4

## not yet run — text 5

not yet run

> fixer.page.status-phrase.text-5

## This test transmits. — paragraph 1

This test transmits.

> Reads as: This test transmits.
> Keep {Id}.
> fixer.page.stage-card.paragraph-1

## Not run yet. — paragraph 2

Not run yet.

> fixer.page.stage-card.paragraph-2

## What this test does — disclosure summary 1

What this test does

> fixer.page.stage-card.disclosure-summary-1

## Help with this test — link 1

Help with this test

> Reads as: Help with this test
> Keep {HelpTopic}, in that order.
> fixer.page.stage-card.link-1

## Run this test again — text 1, when again

Run this test again

> fixer.page.run-button.text-1

## Run this test — text 2, otherwise

Run this test

> fixer.page.run-button.text-2

## The reason you choose changes what the report can say… — paragraph 1

The reason you choose changes what the report can say about the rest of the run, so pick the one that is actually true.

> fixer.page.skip-controls.paragraph-1

## Why are you skipping this stage? — question above the choices 1

Why are you skipping this stage?

> fixer.page.skip-controls.question-above-the-choices-1

## Skip this stage — button 1

Skip this stage

> Reads as: Skip this stage
> Keep {Id}.
> fixer.page.skip-controls.button-1

## Checked at 2026-08-29 14:02 UTC. Re-run; this replaces an… — paragraph 1

Checked at {AtUtc}{result}

> Reads as: Checked at 2026-08-29 14:02 UTC. Re-run; this replaces an earlier result.
> Keep {AtUtc} and {result}, in that order.
> fixer.page.result-block.paragraph-1

## . Re-run; this replaces an earlier result. — text 1, when was re run

. Re-run; this replaces an earlier result.

> fixer.page.result-block.text-1

## What to do: Turn PC audio on — text 2

What to do: {WhatToDo}

> Reads as: What to do: Turn PC audio on
> Keep {WhatToDo}.
> fixer.page.result-block.text-2

## This is the whole run as one document, every stage in… — paragraph 1

This is the whole run as one document, every stage in order, with the test ID at the top. That ID belongs to this report alone, so quote it in any message about the problem.

> fixer.page.report-section.paragraph-1

## Copy puts an email-ready version of the report on the… — paragraph 2

Copy puts an email-ready version of the report on the clipboard, which you can send to your radio's manufacturer. It separates what was measured from what was concluded, so their staff can read the numbers without taking anything on trust.

> fixer.page.report-section.paragraph-2

## If you are able to transmit in your radio manufacturer's… — paragraph 3

If you are able to transmit in your radio manufacturer's own software — for a Flex, that is SmartSDR — run the same test there before you send this, and see whether the problem follows you. If it does, say so in your message. A fault that shows up in the manufacturer's own software as well as in JJ Flexible Radio Access points at the radio or the station rather than at either program, and saying that up front will save you an exchange of emails.

> fixer.page.report-section.paragraph-3

## Copy the report as plain text — button 1

Copy the report as plain text

> fixer.page.report-section.button-1

# The checks themselves

Each check's name, the question it asks, what it does, what pressing Run will do, and the two questions asked before anything transmits.

## at 25 watts — text 1

at {watts}{watts2}

> Reads as: at 25 watts
> Keep {watts} and {watts2}, in that order.
> This runs on from the words before it, so it is spoken as part of a longer sentence.
> fixer.stage.build.text-1

## into ANT1 — text 2

into {port}

> Reads as: into ANT1
> Keep {port}.
> This runs on from the words before it, so it is spoken as part of a longer sentence.
> fixer.stage.build.text-2

## I want to skip this stage for now. — the label (operator choice)

I want to skip this stage for now.

> fixer.skip.operator-skip.operator-choice.label

## I want to skip this stage for now. — what skipping it means (operator choice)

The report will say this stage was not run, and the overall answer is weaker for it.

> fixer.skip.operator-skip.operator-choice.effect-text

## I can't speak directly into my radio — it is somewhere else. — the label (narrows fault domain)

I can't speak directly into my radio — it is somewhere else.

> fixer.skip.remote-no-direct-speech.narrows-fault-domain.label

## I can't speak directly into my radio — it is somewhere else. — what skipping it means (narrows fault domain)

The radio being remote rules out speaking into it directly, but a microphone on this computer can still be measured, so a comparison is still possible. This narrows where the fault can be.

> fixer.skip.remote-no-direct-speech.narrows-fault-domain.effect-text

## I don't have access to a microphone at all. — the label (leaves question open)

I don't have access to a microphone at all.

> fixer.skip.no-microphone.leaves-question-open.label

## I don't have access to a microphone at all. — what skipping it means (leaves question open)

With no microphone there is nothing to compare the injected audio against, so whether your own voice would get through is left open.

> fixer.skip.no-microphone.leaves-question-open.effect-text

## Stage 0 Audio setup and receive — its name

Audio setup and receive

> fixer.stage.audio-setup.title

## Stage 0 Audio setup and receive — the question it asks

What is your audio doing right now, and is sound reaching you from the radio?

> fixer.stage.audio-setup.question

## Stage 0 Audio setup and receive — what this check does

Two halves, and they answer different questions. The first reads the open audio stream directly: host API, input and output device, sample rate, channel count. Not your saved settings — the stream itself. Those two disagree more often than anyone expects, and when they do, the disagreement is usually the fault. You would never spot it on a settings page. The second walks the receive chain — the slice you are listening to, the radio's outputs and their levels, how the audio reaches you, and how much has actually been arriving over the network. Proving receive first is what makes the transmit tests after it readable, and the receive evidence belongs in the report whether or not you came here about receive. Nothing here keys the radio.

> fixer.stage.audio-setup.explanation

## Stage 0 Audio setup and receive — what pressing Run will do

Running this takes a quick reading of the audio path on this computer, then walks the receive chain and reports how much audio has been arriving from the radio. Nothing transmits.

> fixer.stage.audio-setup.describe-run-action

## Stage 0 Audio setup and receive · Can you hear the radio right now? — the question it asks

Can you hear the radio right now?

> fixer.declaration.radio-hearing.question

## Stage 0 Audio setup and receive · Can you hear the radio right now? — why it matters

You're the best person to help prove this test. If you can hear the radio, it proves that the whole receive path is working. By proving the receive path, we can move to the next tests which will help us zero in on what's going right and what's going wrong with your radio's transmission paths.

> fixer.declaration.radio-hearing.why-it-matters

## Can you hear the radio right now? · I can hear the radio — the label

I can hear the radio

> fixer.answer.hears.label

## Can you hear the radio right now? · I hear nothing from the radio — the label

I hear nothing from the radio

> fixer.answer.hears-nothing.label

## Can you hear the radio right now? · No radio is connected — the label

No radio is connected

> fixer.answer.no-radio.label

## Stage 0 Audio setup and receive · Open the full audio device picker — the label

Open the full audio device picker

> fixer.action.open-device-picker.label

## Stage 1 Microphone test — its name

Microphone test

> fixer.stage.microphone-check.title

## Stage 1 Microphone test — the question it asks

Is sound from your microphone arriving in this computer?

> fixer.stage.microphone-check.question

## Stage 1 Microphone test — what this check does

This listens to your microphone with the radio out of the picture entirely, and reports the peak in dBFS along with the integrated loudness in LUFS. It settles the first link in the chain before anything downstream is blamed for it. The reading is also kept as a baseline, because stage 4 is judged against it: a quiet result there means something quite different depending on whether your microphone measured well here.

> fixer.stage.microphone-check.explanation

## Stage 1 Microphone test — what pressing Run will do

Running this measures your room's noise in a quiet moment, then counts you in with three tones and listens while you talk. Nothing transmits.

> fixer.stage.microphone-check.describe-run-action

## Stage 2 Transmitter test — its name

Transmitter test

> fixer.stage.transmitter-check.title

## Stage 2 Transmitter test — the question it asks

Does the radio produce RF when it keys a tune carrier?

> fixer.stage.transmitter-check.question

## Stage 2 Transmitter test — what this check does

The radio keys a tune carrier — a steady unmodulated signal it generates itself — so no microphone, computer audio or streaming takes any part. Forward power and SWR are read while it is keyed. If RF appears, the transmitter is working and whatever is wrong lies somewhere in the audio path. If no RF appears, you never had an audio problem at all, and no amount of microphone testing would have found it. Nothing is transmitted until you have said what is connected to the antenna port.

> fixer.stage.transmitter-check.explanation

## Stage 2 Transmitter test — what pressing Run will do

Running this counts down with three tones, then keys the radio's own tune carrier{AtInto} for about {SecondsPhrase}.

> Reads as: Running this counts down with three tones, then keys the radio's own tune carrier at 25 watts into ANT1 for about two seconds.
> Keep {AtInto} and {SecondsPhrase}, in that order.
> fixer.stage.transmitter-check.describe-run-action

## Stage 2 Transmitter test · Change the tune power — the label

Change the tune power

> fixer.action.transmitter-check.open-power-dialog.label

## Stage 3 Injected transmit — its name

Injected transmit

> fixer.stage.injected-transmit.title

## Stage 3 Injected transmit — the question it asks

Does audio reach the radio when your microphone is bypassed?

> fixer.stage.injected-transmit.question

## Stage 3 Injected transmit — what this check does

Tones and a generated voice are sent to the radio with your microphone taken out of the path, and the radio's own SC_MIC meter is watched to see what arrives. This test and stage 4 differ in exactly one thing, which is whether your microphone is involved. If this one works and stage 4 does not, your microphone is the problem. If neither works, your microphone is not the problem, and the fault lies between this computer and the radio.

> fixer.stage.injected-transmit.explanation

## Stage 3 Injected transmit — what pressing Run will do

Running this counts down with three tones, then keys the transmitter{AtInto} for several seconds and sends tones and a recorded voice through it. Your microphone stays out of the path.

> Reads as: Running this counts down with three tones, then keys the transmitter at 25 watts into ANT1 for several seconds and sends tones and a recorded voice through it. Your microphone stays out of the path.
> Keep {AtInto}.
> fixer.stage.injected-transmit.describe-run-action

## Stage 3 Injected transmit · Change the transmit power — the label

Change the transmit power

> fixer.action.injected-transmit.open-power-dialog.label

## Stage 4 Spoken transmit — its name

Spoken transmit

> fixer.stage.spoken-transmit.title

## Stage 4 Spoken transmit — the question it asks

Does your voice reach the radio through your microphone?

> fixer.stage.spoken-transmit.question

## Stage 4 Spoken transmit — what this check does

You speak, and the radio's SC_MIC meter is watched to see what arrives. This is the same measurement stage 3 made, with your microphone put back into the path — that one difference is what makes the pair worth running. The result is read against your stage 1 microphone reading rather than judged on its own, so a quiet result here on a microphone that measured well earlier points somewhere quite specific.

> fixer.stage.spoken-transmit.explanation

## Stage 4 Spoken transmit — what pressing Run will do

Running this counts you in with three tones, then keys the transmitter{AtInto} for about {SecondsPhrase} while you speak into your microphone.

> Reads as: Running this counts you in with three tones, then keys the transmitter at 25 watts into ANT1 for about two seconds while you speak into your microphone.
> Keep {AtInto} and {SecondsPhrase}, in that order.
> fixer.stage.spoken-transmit.describe-run-action

## Stage 4 Spoken transmit · Change the transmit power — the label

Change the transmit power

> fixer.action.spoken-transmit.open-power-dialog.label

## Transmit — the introduction

Work forward from stage 0. What each stage finds feeds the ones after it — stage 1 measures your microphone, and stage 4 is judged against that measurement rather than on its own. Stage 0 also walks your receive chain, so the report carries receive evidence whether or not receive is what brought you here. Jump around if you want; the report records what was skipped. Stages 0 and 1 do not key the radio.

> fixer.set.transmit.intro

## Transmit · What is the antenna socket connected to right now? — the question it asks

What is the antenna socket connected to right now?

> fixer.declaration.antenna-load.question

## Transmit · What is the antenna socket connected to right now? — why it matters

Nothing transmits until you answer this question. Into a real antenna, or through an amplifier, the tests that transmit keep the power at {LowPowerCeilingWatts} watts or less. Answering that nothing is connected, or that you are not sure, keeps them parked — everything else still runs.

> Reads as: Nothing transmits until you answer this question. Into a real antenna, or through an amplifier, the tests that transmit keep the power at 25 watts or less. Answering that nothing is connected, or that you are not sure, keeps them parked — everything else still runs.
> Keep {LowPowerCeilingWatts}.
> fixer.declaration.antenna-load.why-it-matters

## What is the antenna socket connected to… · A dummy load — the label

A dummy load

> fixer.answer.antenna-load.choices.dummy-load.label

## Antenna — the label

An antenna, and transmitting a short low-power test into it is fine

> fixer.answer.antenna-load.choices.antenna.label

## Amplifier — the label

An amplifier — the radio feeds it before anything reaches an antenna or a load

> fixer.answer.antenna-load.choices.amplifier.label

## What is the antenna socket connected to… · Nothing, or I am not sure — the label

Nothing, or I am not sure

> fixer.answer.nothing-unsure.label

## Transmit · What is the antenna socket connected to right now? — the question, when the radio is somewhere else, when length is over 0

You are connected remotely. The radio will transmit on {port} — what is connected to {port} at that station right now?

> Reads as: You are connected remotely. The radio will transmit on ANT1 — what is connected to ANT1 at that station right now?
> Keep {port}, in that order.
> fixer.declaration.transmit.run-declarations.when-length-is-over-0.antenna-load.question-now

## Transmit · What is the antenna socket connected to right now? — the question, when the radio is somewhere else, otherwise

You are connected remotely. What is connected to the antenna port at that station right now?

> fixer.declaration.otherwise.antenna-load.question-now

## Transmit · What is the antenna socket connected to right now? — the question, when the radio is somewhere else, when length is over 0

The radio will transmit on {port}. What is connected to {port} right now?

> Reads as: The radio will transmit on ANT1. What is connected to ANT1 right now?
> Keep {port}, in that order.
> fixer.declaration.transmit.run-declarations.when-length-is-over-0.antenna-load.question-now.2

## Dummy load — the label

A dummy load — someone at the station has confirmed it is connected

> fixer.answer.antenna-load.choices-now.dummy-load.label

## Antenna — the label

An antenna — someone at the station has confirmed it, and a short low-power test into it is fine

> fixer.answer.antenna-load.choices-now.antenna.label

## Amplifier — the label

An amplifier the radio feeds — someone at the station has confirmed it

> fixer.answer.antenna-load.choices-now.amplifier.label

## What is the antenna socket connected to… · I have not confirmed what… — the label

I have not confirmed what is connected

> fixer.answer.remote-not-confirmed.label

## Transmit · What is the antenna socket connected to right now? — why it matters, when the radio is somewhere else

Nothing transmits until you answer this question. You are not at that station, so every answer here states what someone there has confirmed with you — the report will say your answer came over a remote session, on someone else's word. Whatever is connected, the tests that transmit keep the power at {LowPowerCeilingWatts} watts or less, because a confirmation relayed from a distance is not the same as seeing the socket. Answering that you have not confirmed keeps them parked — everything else still runs.

> Reads as: Nothing transmits until you answer this question. You are not at that station, so every answer here states what someone there has confirmed with you — the report will say your answer came over a remote session, on someone else's word. Whatever is connected, the tests that transmit keep the power at 25 watts or less, because a confirmation relayed from a distance is not the same as seeing the socket. Answering that you have not confirmed keeps them parked — everything else still runs.
> Keep {LowPowerCeilingWatts}.
> fixer.declaration.antenna-load.why-it-matters-now

# What the audio setup check finds

Stage 0's answer, and everything it can find wrong, with what to do about each one.

## Mme in use — what is wrong (us)

Currently, you have selected the MME audio subsystem. It records perfectly well, but it will not tell you the truth about your hardware: Windows resamples behind it and reports its own converted format back, so the 44.1 kHz shown above may be 48 kHz at the device itself. Every level measured in this run would belong to that converter rather than to your microphone.

> fixer.finding.mme-in-use.us.what-is-wrong

## Mme in use — what to do about it (us)

Switch to WASAPI

> fixer.finding.mme-in-use.us.what-to-do

## Mme in use — what is wrong (nobody here)

Currently, you have selected the MME audio subsystem, and this computer offers no WASAPI to move to. Recording works normally; the format MME reports simply does not have to match what the hardware is really doing.

> fixer.finding.mme-in-use.nobody-here.what-is-wrong

## Mme in use — what to do about it (nobody here)

Nothing here can change that. Read every level in this run as approximate — they describe Windows' resampling as much as your microphone.

> fixer.finding.mme-in-use.nobody-here.what-to-do

## No input selected — what is wrong (us)

You have not selected an input device, so nothing you say can reach the radio.

> fixer.finding.no-input-selected.us.what-is-wrong

## No input selected — what to do about it (us)

Use {SuggestedInputDevice}

> Reads as: Use Microphone (USB Audio CODEC)
> Keep {SuggestedInputDevice}.
> fixer.finding.no-input-selected.us.what-to-do

## No input anywhere — what is wrong (operator)

You have not selected an input device, and Windows is not offering one to choose.

> fixer.finding.no-input-anywhere.operator.what-is-wrong

## No input anywhere — what to do about it (operator)

Plug a microphone in, then run this stage again.

> fixer.finding.no-input-anywhere.operator.what-to-do

## Pc audio off — what is wrong (us)

PC audio is currently switched off, so nothing at all leaves this computer for the radio — not your microphone, and not the test tone either.

> fixer.finding.pc-audio-off.us.what-is-wrong

## Pc audio off — what to do about it (us)

Turn PC audio on

> fixer.finding.pc-audio-off.us.what-to-do

## Mic profile empty — what is wrong (us)

No mic profile is loaded on the radio. It will key up and transmit silence. Receive is unaffected, and nothing you did caused this — a Flex arrives from the factory this way.

> fixer.finding.mic-profile-empty.us.what-is-wrong

## Mic profile empty — what to do about it (us)

Load a working profile

> fixer.finding.mic-profile-empty.us.what-to-do

## Windows muted — what is wrong (operator)

Windows itself has your microphone muted. This is not the radio and not this application: the mute is in Windows, and it has to be cleared there.

> fixer.finding.windows-muted.operator.what-is-wrong

## Windows muted — what to do about it (operator)

Unmute it in Sound settings, then run this stage again.

> fixer.finding.windows-muted.operator.what-to-do

## Privacy blocked — what is wrong (operator)

Windows privacy is blocking desktop apps from the microphone. The device is fine; Windows will not hand it over.

> fixer.finding.privacy-blocked.operator.what-is-wrong

## Privacy blocked — what to do about it (operator)

Settings, Privacy, Microphone — allow desktop apps, then run this stage again.

> fixer.finding.privacy-blocked.operator.what-to-do

## Unplugged — what is wrong (operator)

The microphone you have selected is reporting itself as unplugged.

> fixer.finding.unplugged.operator.what-is-wrong

## Unplugged — what to do about it (operator)

Check the cable and the connector, then run this stage again.

> fixer.finding.unplugged.operator.what-to-do

## Hears nothing — what is wrong (operator), when remote radio

You hear nothing from the radio, even though PC audio is on — so the receive path is not delivering sound to your ears.

> fixer.finding.when-remote-radio.hears-nothing.operator.what-is-wrong

## Hears nothing — what to do about it (operator), when remote radio

Check this computer's output device and its volume first. If the radio's audio genuinely is not arriving, the transmit tests will likely fail for the same reason, so settle this before reading them.

> fixer.finding.when-remote-radio.hears-nothing.operator.what-to-do

## Hears nothing — what is wrong (operator), otherwise

You hear nothing from the radio.

> fixer.finding.otherwise.hears-nothing.operator.what-is-wrong

## Hears nothing — what to do about it (operator), otherwise

Check the volume on the radio and on this computer, and where your receive audio normally comes out. A silent receiver is worth settling before the transmit results are read.

> fixer.finding.otherwise.hears-nothing.operator.what-to-do

## Config open mismatch — what to do about it (us)

Reopen on the configured device

> fixer.finding.config-open-mismatch.us.what-to-do

## you chose Microphone (USB Audio CODEC), but Microphone… — text 1

you chose {ConfiguredInputDevice}, but {OpenInputDevice} is what is open

> Reads as: you chose Microphone (USB Audio CODEC), but Microphone (USB Audio CODEC) is what is open
> Keep {ConfiguredInputDevice} and {OpenInputDevice}, in that order.
> fixer.finding.describe-mismatch.text-1

## you chose  through WASAPI, but the stream is actually… — text 2

you chose {HostApiPhrase}, but the stream is actually running on {OpenHostApi}

> Reads as: you chose  through WASAPI, but the stream is actually running on WASAPI
> Keep {HostApiPhrase} and {OpenHostApi}, in that order.
> fixer.finding.describe-mismatch.text-2

## Your settings and the open stream disagree — the open… — text 3

Your settings and the open stream disagree — {Join}. Something overrode your choice, most often a device that disappeared and came back on a different index.

> Reads as: Your settings and the open stream disagree — the open input device is not the one you chose. Something overrode your choice, most often a device that disappeared and came back on a different index.
> Keep {Join}.
> fixer.finding.describe-mismatch.text-3

## ; and — text 4

; and

> The words after this run straight on from it, so it is spoken as part of a longer sentence.
> fixer.finding.describe-mismatch.text-4

## an unnamed audio subsystem — text 1

an unnamed audio subsystem

> fixer.finding.host-api-phrase.text-1

## the WASAPI audio subsystem — text 2

the {hostApi} audio subsystem

> Reads as: the WASAPI audio subsystem
> Keep {hostApi}.
> fixer.finding.host-api-phrase.text-2

## No stream is open. Nothing below was measured — it is all… — text 1

No stream is open. Nothing below was measured — it is all read back from your settings, which is exactly the thing this stage exists to distrust.

> fixer.finding.answer.text-1

## You are recording from — text 2

You are recording from

> The words after this run straight on from it, so it is spoken as part of a longer sentence.
> fixer.finding.answer.text-2

## an unnamed device — text 3, otherwise

an unnamed device

> fixer.finding.answer.text-3

## using  through WASAPI — text 4

using {HostApiPhrase}

> Reads as: using  through WASAPI
> Keep {HostApiPhrase}.
> This runs on from the words before it, so it is spoken as part of a longer sentence.
> fixer.finding.answer.text-4

## , at VALUE kHz — text 5

, at {value} kHz

> Reads as: , at VALUE kHz
> Keep {value}.
> fixer.finding.answer.text-5

## in mono — text 6

in mono

> This runs on from the words before it, so it is spoken as part of a longer sentence.
> fixer.finding.answer.text-6

## in stereo — text 7

in stereo

> This runs on from the words before it, so it is spoken as part of a longer sentence.
> fixer.finding.answer.text-7

## across one channels — text 8

across {OpenChannels} channels

> Reads as: across one channels
> Keep {OpenChannels}.
> This runs on from the words before it, so it is spoken as part of a longer sentence.
> fixer.finding.answer.text-8

## Playback is going to Speakers (Realtek High Definition… — text 9

Playback is going to {OpenOutputDevice}.

> Reads as: Playback is going to Speakers (Realtek High Definition Audio).
> Keep {OpenOutputDevice}.
> This runs on from the words before it, so it is spoken as part of a longer sentence.
> fixer.finding.answer.text-9

## You can hear the radio, and over a remote connection that… — text 1

You can hear the radio, and over a remote connection that one fact proves the whole receive path at a stroke — the link is up, audio is flowing, and your output device is playing it. A silent transmit now points at the microphone side rather than at the connection.

> This runs on from the words before it, so it is spoken as part of a longer sentence.
> fixer.finding.hearing.text-1

## You can hear the radio. With the radio in the room that… — text 2

You can hear the radio. With the radio in the room that may be its own speaker rather than this computer, so it says less about the computer's audio path than it would over a remote connection.

> This runs on from the words before it, so it is spoken as part of a longer sentence.
> fixer.finding.hearing.text-2

## You said no radio is connected, so the tests that need… — text 3

You said no radio is connected, so the tests that need one will wait until it is.

> This runs on from the words before it, so it is spoken as part of a longer sentence.
> fixer.finding.hearing.text-3

## Audio setup, read from what is actually open — text 1

Audio setup, read from what is actually open

> fixer.finding.evidence.text-1

## Open host API: not read — text 2

Open host API: {ValueOrNot}

> Reads as: Open host API: not read
> Keep {ValueOrNot}.
> fixer.finding.evidence.text-2

## Open input device: not read — text 3

Open input device: {ValueOrNot}

> Reads as: Open input device: not read
> Keep {ValueOrNot}.
> fixer.finding.evidence.text-3

## Open output device: not read — text 4

Open output device: {ValueOrNot}

> Reads as: Open output device: not read
> Keep {ValueOrNot}.
> fixer.finding.evidence.text-4

## Open sample rate: 48,000 — text 5

Open sample rate: {OpenSampleRateHz}

> Reads as: Open sample rate: 48,000
> Keep {OpenSampleRateHz}.
> fixer.finding.evidence.text-5

## 48,000 Hz — text 6, when open sample rate hz is over 0

{OpenSampleRateHz} Hz

> Reads as: 48,000 Hz
> Keep {OpenSampleRateHz}.
> fixer.finding.evidence.text-6

## not reported — text 7, otherwise

not reported

> fixer.finding.evidence.text-7

## Open channels: one — text 8

Open channels: {OpenChannels}

> Reads as: Open channels: one
> Keep {OpenChannels}.
> fixer.finding.evidence.text-8

## not reported — text 9, otherwise

not reported

> fixer.finding.evidence.text-9

## Configured host API: not read — text 10

Configured host API: {ValueOrNot}

> Reads as: Configured host API: not read
> Keep {ValueOrNot}.
> fixer.finding.evidence.text-10

## Configured input device: not read — text 11

Configured input device: {ValueOrNot}

> Reads as: Configured input device: not read
> Keep {ValueOrNot}.
> fixer.finding.evidence.text-11

## WASAPI available: yes — text 12

WASAPI available: {yesOrNo}

> Reads as: WASAPI available: yes
> Keep {yesOrNo}.
> fixer.finding.evidence.text-12

## PC audio: on (remote radio) — text 13

PC audio: {onOrOff}{radio}

> Reads as: PC audio: on (remote radio)
> Keep {onOrOff} and {radio}, in that order.
> fixer.finding.evidence.text-13

## (remote radio) — text 14, when remote radio

(remote radio)

> This runs on from the words before it, so it is spoken as part of a longer sentence.
> fixer.finding.evidence.text-14

## (local radio) — text 15, otherwise

(local radio)

> This runs on from the words before it, so it is spoken as part of a longer sentence.
> fixer.finding.evidence.text-15

## Microphone profile: has settings — text 16

Microphone profile: {emptyOrSettings}

> Reads as: Microphone profile: has settings
> Keep {emptyOrSettings}.
> fixer.finding.evidence.text-16

## has settings — text 17, otherwise

has settings

> fixer.finding.evidence.text-17

## Muted in Windows: no — text 18

Muted in Windows: {Tristate}

> Reads as: Muted in Windows: no
> Keep {Tristate}.
> fixer.finding.evidence.text-18

## Blocked by Windows privacy: no — text 19

Blocked by Windows privacy: {Tristate}

> Reads as: Blocked by Windows privacy: no
> Keep {Tristate}.
> fixer.finding.evidence.text-19

## Device reports unplugged: no — text 20

Device reports unplugged: {Tristate}

> Reads as: Device reports unplugged: no
> Keep {Tristate}.
> fixer.finding.evidence.text-20

## Operator hears the radio:  You said you can hear the… — text 21

Operator hears the radio: {HearingEvidence}

> Reads as: Operator hears the radio:  You said you can hear the radio.
> Keep {HearingEvidence}.
> fixer.finding.evidence.text-21

## could not be read — text 1, when v is null

could not be read

> fixer.finding.tristate.text-1

## yes, by their own account — text 1

yes, by their own account

> fixer.finding.hearing-evidence.text-1

## no — they hear nothing — text 2

no — they hear nothing

> fixer.finding.hearing-evidence.text-2

## no radio connected, by their own account — text 3

no radio connected, by their own account

> fixer.finding.hearing-evidence.text-3

## not asked, or not answered — text 4

not asked, or not answered

> fixer.finding.hearing-evidence.text-4

# What the microphone and transmit checks find

Stages 1 to 4: the answer each one gives, and everything they can find wrong.

## The microphone was not measured — the measurement did not… — text 1

The microphone was not measured — the measurement did not produce a result, so whether sound is arriving cannot be said either way.

> fixer.finding.microphone.text-1

## Yes — sound is arriving in this computer from Microphone… — text 2

Yes — sound is arriving in this computer from {NameOr}{HostApiPhrase}. No radio was involved in this test, so this stands on its own whatever happens later.

> Reads as: Yes — sound is arriving in this computer from Microphone (USB Audio CODEC) through WASAPI. No radio was involved in this test, so this stands on its own whatever happens later.
> Keep {NameOr} and {HostApiPhrase}, in that order.
> fixer.finding.microphone.text-2

## your microphone — text 3

your microphone

> fixer.finding.microphone.text-3

## , through  through WASAPI — text 4, when length is over 0

, through {HostApiPhrase}

> Reads as: , through  through WASAPI
> Keep {HostApiPhrase}.
> fixer.finding.microphone.text-4

## No — the measurement ran and heard nothing above the… — text 5

No — the measurement ran and heard nothing above the noise floor from {NameOr}.

> Reads as: No — the measurement ran and heard nothing above the noise floor from Microphone (USB Audio CODEC).
> Keep {NameOr}.
> fixer.finding.microphone.text-5

## your microphone — text 6

your microphone

> fixer.finding.microphone.text-6

## Mic silent — what is wrong (operator)

Your microphone was measured and nothing arrived.

> fixer.finding.mic-silent.operator.what-is-wrong

## Stage 0 has already named the cause: Windows has this… — text 1

Stage 0 has already named the cause: Windows has this microphone muted, and Windows privacy is blocking it as well. Its findings in the stage 0 card carry the steps — clear both, then run this stage again.

> fixer.finding.silent-mic-advice.text-1

## Stage 0 has already named the cause: Windows itself has… — text 2

Stage 0 has already named the cause: Windows itself has this microphone muted. Its finding in the stage 0 card carries the steps — clear the mute, then run this stage again.

> fixer.finding.silent-mic-advice.text-2

## Stage 0 has already named the cause: Windows privacy is… — text 3

Stage 0 has already named the cause: Windows privacy is blocking desktop apps from the microphone. Its finding in the stage 0 card carries the steps — allow it, then run this stage again.

> fixer.finding.silent-mic-advice.text-3

## Windows is not muting this microphone and is not blocking… — text 4

Windows is not muting this microphone and is not blocking it — stage 0 checked both — so what is left is the cable, the connector, or the device itself. Check those, then run this stage again.

> fixer.finding.silent-mic-advice.text-4

## Check the cable, the Windows mute, and the Windows… — text 5

Check the cable, the Windows mute, and the Windows microphone privacy setting, then run this stage again.

> fixer.finding.silent-mic-advice.text-5

## Microphone, no radio involved — text 1

Microphone, no radio involved

> fixer.finding.mic-evidence.text-1

## Measured: yes — text 2

Measured: {yesOrNo}

> Reads as: Measured: yes
> Keep {yesOrNo}.
> fixer.finding.mic-evidence.text-2

## Device: Microphone (USB Audio CODEC) — text 3

Device: {NameOr}

> Reads as: Device: Microphone (USB Audio CODEC)
> Keep {NameOr}.
> fixer.finding.mic-evidence.text-3

## not reported — text 4

not reported

> fixer.finding.mic-evidence.text-4

## Host API: Microphone (USB Audio CODEC) — text 5

Host API: {NameOr}

> Reads as: Host API: Microphone (USB Audio CODEC)
> Keep {NameOr}.
> fixer.finding.mic-evidence.text-5

## not reported — text 6

not reported

> fixer.finding.mic-evidence.text-6

## Peak: minus 12 — text 7

Peak: {Db}

> Reads as: Peak: minus 12
> Keep {Db}.
> fixer.finding.mic-evidence.text-7

## Noise floor: minus 12 — text 8

Noise floor: {Db}

> Reads as: Noise floor: minus 12
> Keep {Db}.
> fixer.finding.mic-evidence.text-8

## Tx no power — what is wrong (operator)

This is not an audio problem, and no amount of microphone testing will find it. A tune carrier is the radio's own signal, with nothing of yours in the path, and no RF came out — so the fault sits upstream of anything the remaining stages can measure.

> fixer.finding.tx-no-power.operator.what-is-wrong

## Tx no power — what to do about it (operator)

Check the antenna connection, the band, whether the slice is set to transmit, and whether anything is inhibiting transmit. Then run this stage again.

> fixer.finding.tx-no-power.operator.what-to-do

## Tx load suspect — what is wrong (operator)

The transmitter works, but a large share of its power came straight back instead of going out.

> fixer.finding.tx-load-suspect.operator.what-is-wrong

## Tx load suspect — what to do about it (operator)

Check what is connected to the antenna port before transmitting again.

> fixer.finding.tx-load-suspect.operator.what-to-do

## Injected transmit, microphone bypassed — text 1

Injected transmit, microphone bypassed

> fixer.finding.injected.text-1

## Conditioning chain: VALUE — text 2

Conditioning chain: {value}

> Reads as: Conditioning chain: VALUE
> Keep {value}.
> fixer.finding.injected.text-2

## could not be read — text 3, when conditioning active is null

could not be read

> fixer.finding.injected.text-3

## The spoken test did not produce a measurement, so whether… — text 1

The spoken test did not produce a measurement, so whether your voice reaches the radio cannot be said either way.

> fixer.finding.spoken.text-1

## Yes — your voice, through Microphone (USB Audio CODEC),… — text 2

Yes — your voice, through {NameOr}, reached the radio.

> Reads as: Yes — your voice, through Microphone (USB Audio CODEC), reached the radio.
> Keep {NameOr}.
> fixer.finding.spoken.text-2

## your microphone — text 3

your microphone

> fixer.finding.spoken.text-3

## No — your voice did not reach the radio. But when the… — text 4

No — your voice did not reach the radio. But when the microphone check ran, sound from {NameOr} WAS arriving in this computer, so the microphone itself is the least likely culprit. The difference lies between this computer and the radio — and the injected test just walked that same path, so read the two side by side.

> Reads as: No — your voice did not reach the radio. But when the microphone check ran, sound from Microphone (USB Audio CODEC) WAS arriving in this computer, so the microphone itself is the least likely culprit. The difference lies between this computer and the radio — and the injected test just walked that same path, so read the two side by side.
> Keep {NameOr}.
> fixer.finding.spoken.text-4

## that microphone — text 5

that microphone

> fixer.finding.spoken.text-5

## No — your voice did not reach the radio, and the… — text 6

No — your voice did not reach the radio, and the microphone test heard nothing either. Start at the microphone: until sound arrives in this computer, nothing further along can carry it.

> fixer.finding.spoken.text-6

## No — your voice did not reach the radio, and because the… — text 7

No — your voice did not reach the radio, and because the microphone test was not run, whether the microphone or the path beyond it is at fault cannot be separated. Run the microphone test; it splits this question in two.

> fixer.finding.spoken.text-7

## Spoken transmit, microphone in the path — text 8

Spoken transmit, microphone in the path

> fixer.finding.spoken.text-8

## Attempted: yes — text 9

Attempted: {yesOrNo}

> Reads as: Attempted: yes
> Keep {yesOrNo}.
> fixer.finding.spoken.text-9

## Reached the radio: yes — text 10

Reached the radio: {yesOrNo}

> Reads as: Reached the radio: yes
> Keep {yesOrNo}.
> fixer.finding.spoken.text-10

## not measured — text 11, otherwise

not measured

> fixer.finding.spoken.text-11

## Device: Microphone (USB Audio CODEC) — text 12

Device: {NameOr}

> Reads as: Device: Microphone (USB Audio CODEC)
> Keep {NameOr}.
> fixer.finding.spoken.text-12

## not reported — text 13

not reported

> fixer.finding.spoken.text-13

## Host API: Microphone (USB Audio CODEC) — text 14

Host API: {NameOr}

> Reads as: Host API: Microphone (USB Audio CODEC)
> Keep {NameOr}.
> fixer.finding.spoken.text-14

## not reported — text 15

not reported

> fixer.finding.spoken.text-15

## Microphone baseline: VALUE — text 16

Microphone baseline: {value}

> Reads as: Microphone baseline: VALUE
> Keep {value}.
> fixer.finding.spoken.text-16

## none — the microphone test was not run — text 17, when mic baseline is null

none — the microphone test was not run

> fixer.finding.spoken.text-17

## sound was arriving from Microphone (USB Audio CODEC) — text 18, when audio arrived

sound was arriving from {NameOr}

> Reads as: sound was arriving from Microphone (USB Audio CODEC)
> Keep {NameOr}.
> fixer.finding.spoken.text-18

## the microphone — text 19, when audio arrived

the microphone

> fixer.finding.spoken.text-19

## measured, and nothing arrived — text 20, otherwise

measured, and nothing arrived

> fixer.finding.spoken.text-20

## attempted, but nothing was measured — text 21, otherwise

attempted, but nothing was measured

> fixer.finding.spoken.text-21

## Antenna socket, as stated by the operator: a dummy… — text 1, otherwise

Antenna socket, as stated by the operator: {loadDeclaration}{NewLine}

> Reads as: Antenna socket, as stated by the operator: a dummy loadNEWLINE
> Keep {loadDeclaration} and {NewLine}, in that order.
> fixer.finding.load-line.text-1

## not measured — text 1, when is na n

not measured

> fixer.finding.db.text-1

## yes dBFS — text 2, otherwise

{v} dBFS

> Reads as: yes dBFS
> Keep {v}.
> fixer.finding.db.text-2

# When the tool refuses to transmit

Every reason nothing was sent. These are heard at the moment an operator expected the radio to key, so they have to say what happened and what to do next.

## declared A dummy load — text 1, when has value

declared {Value}

> Reads as: declared A dummy load
> Keep {Value}.
> fixer.refusal.load-declaration-for-report.text-1

## , over a remote session, by an operator not at the station — text 2, when load declared remotely

, over a remote session, by an operator not at the station

> fixer.refusal.load-declaration-for-report.text-2

## That step is not meant to transmit, so nothing was sent. — text 1

That step is not meant to transmit, so nothing was sent.

> fixer.refusal.request.text-1

## The test was stopped, so nothing was transmitted. Start… — text 2

The test was stopped, so nothing was transmitted. Start it again if you want to carry on.

> fixer.refusal.request.text-2

## There is no test running, so nothing was transmitted. — text 3

There is no test running, so nothing was transmitted.

> fixer.refusal.request.text-3

## That request belongs to an earlier test, so nothing was… — text 4

That request belongs to an earlier test, so nothing was transmitted. Close this and start again.

> fixer.refusal.request.text-4

## The radio is not reachable, so nothing was transmitted. — text 5

The radio is not reachable, so nothing was transmitted.

> fixer.refusal.request.text-5

## The radio is already transmitting, so nothing more was… — text 6

The radio is already transmitting, so nothing more was sent. Let it finish, or press Stop everything.

> fixer.refusal.request.text-6

## A transmit is already running, so nothing more was sent. — text 7

A transmit is already running, so nothing more was sent.

> fixer.refusal.request.text-7

## Nothing was transmitted, because you have not said yet… — text 8

Nothing was transmitted, because you have not said yet what the antenna socket is connected to. Say what is connected, and this step will run.

> fixer.refusal.request.text-8

## Nothing was transmitted. You said you have not confirmed… — text 9, when load declared remotely

Nothing was transmitted. You said you have not confirmed what the antenna socket at that station is connected to — and this tool never transmits into an unknown load, least of all at a station you are not at. Ask someone at the station what is connected, answer the antenna question again, and this step will run.

> fixer.refusal.request.text-9

## Nothing was transmitted. You said nothing is connected,… — text 10, otherwise

Nothing was transmitted. You said nothing is connected, or that you are not sure — and this tool never transmits into an unknown load. Connect a dummy load or an antenna, answer the antenna question again, and this step will run.

> fixer.refusal.request.text-10

## a real antenna — text 11, when load kind is antenna

a real antenna

> fixer.refusal.request.text-11

## an amplifier — text 12, otherwise

an amplifier

> fixer.refusal.request.text-12

## Nothing was transmitted. You declared a dummy load, and… — text 13

Nothing was transmitted. You declared {into}, and the radio's power for this step could not be read — into {into} these tests only transmit when the power is known to be {LowPowerCeilingWatts} watts or less.

> Reads as: Nothing was transmitted. You declared a dummy load, and the radio's power for this step could not be read — into a dummy load these tests only transmit when the power is known to be 25 watts or less.
> Keep {into} and {LowPowerCeilingWatts}, in that order.
> fixer.refusal.request.text-13

## Nothing was transmitted. The radio's power for this step… — text 14

Nothing was transmitted. The radio's power for this step is {transmitPowerWatts} watts, and you declared {into}. Into {into} these tests transmit at {LowPowerCeilingWatts} watts or less — turn the power down, or declare a dummy load, and this step will run.

> Reads as: Nothing was transmitted. The radio's power for this step is 25 watts, and you declared a dummy load. Into a dummy load these tests transmit at 25 watts or less — turn the power down, or declare a dummy load, and this step will run.
> Keep {transmitPowerWatts}, {into} and {LowPowerCeilingWatts}, in that order.
> fixer.refusal.request.text-14

## Nothing was transmitted. The dummy load was declared over… — text 15

Nothing was transmitted. The dummy load was declared over a remote session, and the radio's power for this step could not be read — on a declaration made from a distance these tests only transmit when the power is known to be {LowPowerCeilingWatts} watts or less.

> Reads as: Nothing was transmitted. The dummy load was declared over a remote session, and the radio's power for this step could not be read — on a declaration made from a distance these tests only transmit when the power is known to be 25 watts or less.
> Keep {LowPowerCeilingWatts}.
> fixer.refusal.request.text-15

## Nothing was transmitted. The radio's power for this step… — text 16

Nothing was transmitted. The radio's power for this step is {transmitPowerWatts} watts, and the dummy load was declared over a remote session — on the word of someone at the station, not your own eyes. On a remote declaration these tests transmit at {LowPowerCeilingWatts} watts or less; turn the power down and this step will run.

> Reads as: Nothing was transmitted. The radio's power for this step is 25 watts, and the dummy load was declared over a remote session — on the word of someone at the station, not your own eyes. On a remote declaration these tests transmit at 25 watts or less; turn the power down and this step will run.
> Keep {transmitPowerWatts} and {LowPowerCeilingWatts}, in that order.
> fixer.refusal.request.text-16

## That step has already transmitted once. Choose Run again… — text 17

That step has already transmitted once. Choose Run again if you meant to repeat it.

> fixer.refusal.request.text-17

## Transmit requests are arriving faster than they should… — text 18

Transmit requests are arriving faster than they should be, so this one was refused. That usually means something is repeating itself rather than anything you did.

> fixer.refusal.request.text-18

## This test has transmitted for about 2 seconds altogether,… — text 19

This test has transmitted for about {Round} seconds altogether, which is as much as one run allows. Start a new test to carry on.

> Reads as: This test has transmitted for about 2 seconds altogether, which is as much as one run allows. Start a new test to carry on.
> Keep {Round}.
> fixer.refusal.request.text-19

## This test has transmitted as many times as one run… — text 20

This test has transmitted as many times as one run allows. Start a new test to carry on.

> fixer.refusal.request.text-20

# The report

The document the run becomes — the one that goes to a radio manufacturer.

## JJ Flexible Transmit test report — text 1

JJ Flexible {Name} test report

> Reads as: JJ Flexible Transmit test report
> Keep {Name}.
> fixer.report.build.text-1

## Test ID: TX-4K2P — text 2

Test ID: {RunId}

> Reads as: Test ID: TX-4K2P
> Keep {RunId}.
> fixer.report.build.text-2

## Run started 2026-08-29 14:02 UTC. — text 3

Run started {Stamp}.

> Reads as: Run started 2026-08-29 14:02 UTC.
> Keep {Stamp}.
> fixer.report.build.text-3

## This copy of the report was written 2026-08-29 14:02 UTC. — text 4

This copy of the report was written {Stamp}.

> Reads as: This copy of the report was written 2026-08-29 14:02 UTC.
> Keep {Stamp}.
> fixer.report.build.text-4

## What was found, and what to do — its name

What was found, and what to do

> fixer.report.found-section.title

## No stages have been run yet, so there is nothing to… — text 1

No stages have been run yet, so there is nothing to report. Start at the first stage.

> fixer.report.found-section.text-1

## Nothing that ran found a problem it could name. The… — text 2

Nothing that ran found a problem it could name. The stage-by-stage detail below says what was actually measured.

> fixer.report.found-section.text-2

## Stage 3, Microphone check — text 3, otherwise

Stage {Number}, {Title}

> Reads as: Stage 3, Microphone check
> Keep {Number} and {Title}, in that order.
> fixer.report.found-section.text-3

## FIXED during this run at 2026-08-29 14:02 UTC — it… — text 4

FIXED during this run at {Stamp} — it became: {WhatItBecame}

> Reads as: FIXED during this run at 2026-08-29 14:02 UTC — it became: the input is now WASAPI
> Keep {Stamp} and {WhatItBecame}, in that order.
> fixer.report.found-section.text-4

## JJ Flexible offers a one-press fix for this (Turn PC… — text 5

JJ Flexible offers a one-press fix for this ({WhatToDo}).

> Reads as: JJ Flexible offers a one-press fix for this (Turn PC audio on).
> Keep {WhatToDo}.
> fixer.report.found-section.text-5

## What to do: Turn PC audio on — text 6

What to do: {WhatToDo}

> Reads as: What to do: Turn PC audio on
> Keep {WhatToDo}.
> fixer.report.found-section.text-6

## How much of the test was done — its name

How much of the test was done

> fixer.report.coverage-section.title

## The stages were done in this order: the open input device… — text 1

The stages were done in this order: {Join}.

> Reads as: The stages were done in this order: the open input device is not the one you chose.
> Keep {Join}.
> fixer.report.coverage-section.text-1

## , then — text 2

, then

> The words after this run straight on from it, so it is spoken as part of a longer sentence.
> fixer.report.coverage-section.text-2

## Not attempted at all: the open input device is not the… — text 3

Not attempted at all: {Join}. That weakens the overall answer: each stage rules something in or out, and the stages after it depend on knowing that.

> Reads as: Not attempted at all: the open input device is not the one you chose. That weakens the overall answer: each stage rules something in or out, and the stages after it depend on knowing that.
> Keep {Join}.
> fixer.report.coverage-section.text-3

## stage 3 (Microphone check) — text 4

stage {Number} ({Title})

> Reads as: stage 3 (Microphone check)
> Keep {Number} and {Title}, in that order.
> fixer.report.coverage-section.text-4

## Stage 3Microphone check was not run. The reason given: "A… — text 5

Stage {Number}{Title} was not run. The reason given: "{Label}" {EffectText}

> Reads as: Stage 3Microphone check was not run. The reason given: "A dummy load" EFFECTTEXT
> Keep {Number}, {Title}, {Label} and {EffectText}, in that order.
> fixer.report.coverage-section.text-5

## These results span 2 minutes: the oldest is DESCRIBE at… — text 6

These results span {Round} minutes: the oldest is {Describe} at {Stamp} and the newest is {Describe2} at {Stamp2}. Things may have changed in between — a microphone swapped, a setting altered — so do not read them as one snapshot.

> Reads as: These results span 2 minutes: the oldest is DESCRIBE at 2026-08-29 14:02 UTC and the newest is DESCRIBE2 at 2026-08-29 14:07 UTC. Things may have changed in between — a microphone swapped, a setting altered — so do not read them as one snapshot.
> Keep {Round}, {Describe}, {Stamp}, {Describe2} and {Stamp2}, in that order.
> fixer.report.coverage-section.text-6

## Nothing has been done yet. — text 7

Nothing has been done yet.

> fixer.report.coverage-section.text-7

## Changes made during this run — its name

Changes made during this run

> fixer.report.fixes-section.title

## These settings were changed while the test was running,… — text 1

These settings were changed while the test was running, each one offered on the page and applied on a press — never silently. Results recorded after a change describe the changed setup, not the one the run started with.

> fixer.report.fixes-section.text-1

## stage 3 (Microphone check) — text 2, when st is not null

stage {Number} ({Title})

> Reads as: stage 3 (Microphone check)
> Keep {Number} and {Title}, in that order.
> fixer.report.fixes-section.text-2

## It became: the input is now WASAPI — text 3, when succeeded

It became: {WhatItBecame}

> Reads as: It became: the input is now WASAPI
> Keep {WhatItBecame}.
> fixer.report.fixes-section.text-3

## The fix was attempted and DID NOT succeed: the input is… — text 4, otherwise

The fix was attempted and DID NOT succeed: {WhatItBecame}

> Reads as: The fix was attempted and DID NOT succeed: the input is now WASAPI
> Keep {WhatItBecame}.
> fixer.report.fixes-section.text-4

## Stages recorded after this change: the open input device… — text 5

Stages recorded after this change: {Join}.

> Reads as: Stages recorded after this change: the open input device is not the one you chose.
> Keep {Join}.
> This runs on from the words before it, so it is spoken as part of a longer sentence.
> fixer.report.fixes-section.text-5

## Stage 3: Microphone check — its name

Stage {Number}: {Title}

> Reads as: Stage 3: Microphone check
> Keep {Number} and {Title}, in that order.
> fixer.report.stage-section.title

## This stage has not been run. — text 1

This stage has not been run.

> fixer.report.stage-section.text-1

## Attempted at 2026-08-29 14:02 UTC and could not run. — text 2

Attempted at {Stamp} and could not run.

> Reads as: Attempted at 2026-08-29 14:02 UTC and could not run.
> Keep {Stamp}.
> fixer.report.stage-section.text-2

## Run at 2026-08-29 14:02 UTC. This stage was re-run; this… — text 3

Run at {Stamp}{one}

> Reads as: Run at 2026-08-29 14:02 UTC. This stage was re-run; this result replaces an earlier one.
> Keep {Stamp} and {one}, in that order.
> fixer.report.stage-section.text-3

## . This stage was re-run; this result replaces an earlier… — text 4, when was re run

. This stage was re-run; this result replaces an earlier one.

> fixer.report.stage-section.text-4

## JJ Flexible offers a one-press fix for this (Turn PC… — text 5

JJ Flexible offers a one-press fix for this ({WhatToDo}).

> Reads as: JJ Flexible offers a one-press fix for this (Turn PC audio on).
> Keep {WhatToDo}.
> fixer.report.stage-section.text-5

## What to do: Turn PC audio on — text 6

What to do: {WhatToDo}

> Reads as: What to do: Turn PC audio on
> Keep {WhatToDo}.
> fixer.report.stage-section.text-6

## stage 3 (Microphone check) — text 1, otherwise

stage {Number} ({Title})

> Reads as: stage 3 (Microphone check)
> Keep {Number} and {Title}, in that order.
> fixer.report.describe.text-1

## Microphone (USB Audio CODEC), skipped — text 2

{name}, skipped

> Reads as: Microphone (USB Audio CODEC), skipped
> Keep {name}.
> fixer.report.describe.text-2

## Microphone (USB Audio CODEC), could not run — text 3

{name}, could not run

> Reads as: Microphone (USB Audio CODEC), could not run
> Keep {name}.
> fixer.report.describe.text-3

# What the window says while you work

Announcements, notices and the words for things that happen outside the page itself.

## Transmit tests — JJ Flexible — text 1

{Name} tests — JJ Flexible

> Reads as: Transmit tests — JJ Flexible
> Keep {Name}.
> fixer.window.fixer-dialog.text-1

## Transmit tests — text 2

{Name} tests

> Reads as: Transmit tests
> Keep {Name}.
> fixer.window.fixer-dialog.text-2

## The transmit noise gate is currently off, so no threshold… — text 1

The transmit noise gate is currently off, so no threshold applies.

> fixer.window.describe-gate-derivation.text-1

## The transmit noise gate is holding its deliberately low… — text 2

The transmit noise gate is holding its deliberately low default threshold of {threshold} dB, because no transmitted speech has taught it your room's noise floor yet.

> Reads as: The transmit noise gate is holding its deliberately low default threshold of minus 45 dBFS dB, because no transmitted speech has taught it your room's noise floor yet.
> Keep {threshold}.
> fixer.window.describe-gate-derivation.text-2

## Your transmit noise gate's threshold is currently minus… — text 3

Your transmit noise gate's threshold is currently {threshold} dB, derived from the noise floor measured in your own transmitted audio ({NoiseFloorLufs} LUFS, plus a {ThresholdMarginDb} dB margin). Stated here so you can see where it came from; whether it is right for your room is not judged by this test.

> Reads as: Your transmit noise gate's threshold is currently minus 45 dBFS dB, derived from the noise floor measured in your own transmitted audio (NOISEFLOORLUFS LUFS, plus a 6 dB margin). Stated here so you can see where it came from; whether it is right for your room is not judged by this test.
> Keep {threshold}, {NoiseFloorLufs} and {ThresholdMarginDb}, in that order.
> fixer.window.describe-gate-derivation.text-3

## no reference recording is installed on this computer — text 1

no reference recording is installed on this computer

> fixer.window.prepare-reference-voice.text-1

## the reference recording could not be prepared: the device… — text 2

the reference recording could not be prepared: {Message}

> Reads as: the reference recording could not be prepared: the device was not available
> Keep {Message}.
> fixer.window.prepare-reference-voice.text-2

## Transmit tests — JJ Flexible — text 1

Transmit tests — JJ Flexible

> fixer.window.show.text-1

## The transmit tests need the Microsoft Edge WebView2… — text 2

The transmit tests need the Microsoft Edge WebView2 runtime, which is not installed on this computer. Everything they would have checked can still be reached from the Audio Workshop and from Diagnostics.

> fixer.window.show.text-2

## Transmit tests — JJ Flexible — text 3

Transmit tests — JJ Flexible

> fixer.window.show.text-3

## There is no saved run to continue. — text 1

There is no saved run to continue.

> fixer.window.why-it-cannot-be-resumed.text-1

## Run TX-4K2P is a set of Transmit tests, and this is the… — text 2

Run {RunId} is a set of {StageSetName} tests, and this is the {Name} tests. It can still be read and exported from the saved test runs list.

> Reads as: Run TX-4K2P is a set of Transmit tests, and this is the Transmit tests. It can still be read and exported from the saved test runs list.
> Keep {RunId}, {StageSetName} and {Name}, in that order.
> fixer.window.why-it-cannot-be-resumed.text-2

## Run TX-4K2P was recorded with a different set of tests… — text 3

Run {RunId} was recorded with a different set of tests from the ones this version of JJ Flexible offers, so continuing it would mix measurements from two different runs. It can still be read and exported from the saved test runs list.

> Reads as: Run TX-4K2P was recorded with a different set of tests from the ones this version of JJ Flexible offers, so continuing it would mix measurements from two different runs. It can still be read and exported from the saved test runs list.
> Keep {RunId}.
> fixer.window.why-it-cannot-be-resumed.text-3

## Whether run TX-4K2P can be continued could not be worked… — text 4

Whether run {RunId} can be continued could not be worked out, so it has not been opened. It can still be read and exported from the saved test runs list.

> Reads as: Whether run TX-4K2P can be continued could not be worked out, so it has not been opened. It can still be read and exported from the saved test runs list.
> Keep {RunId}.
> fixer.window.why-it-cannot-be-resumed.text-4

## Something went wrong handling that. Nothing was… — text 1

Something went wrong handling that. Nothing was transmitted.

> fixer.window.on-web-message.text-1

## You said: A dummy load. — text 1

You said: {Value}.

> Reads as: You said: A dummy load.
> Keep {Value}.
> fixer.window.handle.text-1

## You said: A dummy load. — text 2

You said: {Value}.

> Reads as: You said: A dummy load.
> Keep {Value}.
> fixer.window.handle.text-2

## That test has already run, and its measurement is kept.… — text 3

That test has already run, and its measurement is kept. To measure again, choose Run this test again.

> fixer.window.handle.text-3

## That fix did not succeed: the input is now WASAPI — text 4, otherwise

That fix did not succeed: {WhatItBecame}

> Reads as: That fix did not succeed: the input is now WASAPI
> Keep {WhatItBecame}.
> fixer.window.handle.text-4

## Something is already running. Wait for it to finish, or… — text 1

Something is already running. Wait for it to finish, or press Stop everything.

> fixer.window.run-stage.text-1

## Something went wrong running that test. Nothing was… — text 2

Something went wrong running that test. Nothing was transmitted.

> fixer.window.run-stage.text-2

## Do you want to stop the test? — text 1, otherwise

Do you want to stop the test?

> fixer.window.ask-exit.text-1

## Deletes this run's saved record. Everything recorded so… — text 2, when kept

Deletes this run's saved record. Everything recorded so far is gone for good{transmission}

> Reads as: Deletes this run's saved record. Everything recorded so far is gone for good, including measurements that keyed the radio — taking those again costs real transmission.
> Keep {transmission}.
> fixer.window.ask-exit.text-2

## , including measurements that keyed the radio — taking… — text 3, when transmit count is over 0

, including measurements that keyed the radio — taking those again costs real transmission.

> fixer.window.ask-exit.text-3

## Ends the test and closes the window. This run was not… — text 4, otherwise

Ends the test and closes the window. This run was not being saved, so nothing is kept.

> fixer.window.ask-exit.text-4

## Closes the window and keeps the run. Continue it later… — text 5, when offers resume later

Closes the window and keeps the run. Continue it later from View or resume saved test runs, on the Fix menu — everything already recorded stays{recorded}, and the report will say the tests were done in more than one sitting.

> Reads as: Closes the window and keeps the run. Continue it later from View or resume saved test runs, on the Fix menu — everything already recorded stays, though the test running right now stops and is not recorded, and the report will say the tests were done in more than one sitting.
> Keep {recorded}.
> fixer.window.ask-exit.text-5

## , though the test running right now stops and is not… — text 6, when run in progress

, though the test running right now stops and is not recorded

> fixer.window.ask-exit.text-6

## Transmit tests — JJ Flexible — text 7

{Name} tests — JJ Flexible

> Reads as: Transmit tests — JJ Flexible
> Keep {Name}.
> fixer.window.ask-exit.text-7

## stopped to resume later — text 1

stopped to resume later

> fixer.window.close-keeping-run.text-1

## stopped to resume later — text 1

stopped to resume later

> fixer.window.on-closing.text-1

## window.jjflex && window.jjflex.receive(JSONENCODE) — text 1

window.jjflex && window.jjflex.receive({JsonEncode})

> Reads as: window.jjflex && window.jjflex.receive(JSONENCODE)
> Keep {JsonEncode}.
> fixer.window.to-page.text-1

## The report is on the clipboard, as plain text, ready to… — text 1

The report is on the clipboard, as plain text, ready to paste into an email.

> fixer.window.copy-report.text-1

## That help page could not be opened. — text 1

That help page could not be opened.

> fixer.window.open-help.text-1

## No radio is connected, so there is no power to change. — text 1

No radio is connected, so there is no power to change.

> fixer.window.open-power-dialog.text-1

## The power window could not be opened. — text 2

The power window could not be opened.

> fixer.window.open-power-dialog.text-2

## The audio device list could not be opened. — text 1

The audio device list could not be opened.

> fixer.window.open-device-picker.text-1

# Leaving the checks

What you are asked on the way out, and what each answer costs.

## Exit without saving — text 1

Exit without saving

> fixer.exit.fixer-exit-prompt.text-1

## Continue the test — text 2

Continue the test

> fixer.exit.fixer-exit-prompt.text-2

## Nothing changes. You go back to the tests where you left… — text 3

Nothing changes. You go back to the tests where you left off.

> fixer.exit.fixer-exit-prompt.text-3

## Stop tests and resume later — text 4

Stop tests and resume later

> fixer.exit.fixer-exit-prompt.text-4

# Saved runs

The window that lists runs already made, and lets one be read again or carried on.

## Saved test runs — JJ Flexible — text 1

Saved test runs — JJ Flexible

> fixer.saved.fixer-past-runs-dialog.text-1

## Every test run saves itself as it happens, named by its… — the text

Every test run saves itself as it happens, named by its test ID. Open one to read its report, rename it so you can find it again, continue one you stopped part-way, export it to send to someone, or delete it. JJ Flexible keeps the newest {MaxRunsKept} runs; export anything you want to keep forever.

> Reads as: Every test run saves itself as it happens, named by its test ID. Open one to read its report, rename it so you can find it again, continue one you stopped part-way, export it to send to someone, or delete it. JJ Flexible keeps the newest 200 runs; export anything you want to keep forever.
> Keep {MaxRunsKept}.
> fixer.saved.fixer-past-runs-dialog.text

## _View report — text 2

_View report

> fixer.saved.fixer-past-runs-dialog.text-2

## View report — text 3

View report

> fixer.saved.fixer-past-runs-dialog.text-3

## Continue this run — text 4

Continue this run

> fixer.saved.fixer-past-runs-dialog.text-4

## Saved test runs — text 5

Saved test runs

> fixer.saved.fixer-past-runs-dialog.text-5

## One line per saved run: its name or test ID, when it… — text 6

One line per saved run: its name or test ID, when it started, how many stages have results, and whether it finished. Newest first. Enter opens the report.

> fixer.saved.fixer-past-runs-dialog.text-6

## That could not be done: the device was not available — text 1

That could not be done: {Message}

> Reads as: That could not be done: the device was not available
> Keep {Message}.
> fixer.saved.make-button.text-1

## The settings folder could not be resolved, so no saved… — text 1

The settings folder could not be resolved, so no saved runs can be shown.

> fixer.saved.refresh.text-1

## No test runs have been saved yet. Runs save themselves as… — text 2

No test runs have been saved yet. Runs save themselves as they happen — run a test and it will appear here.{UnreadableNote}

> Reads as: No test runs have been saved yet. Runs save themselves as they happen — run a test and it will appear here. One could not be read.
> Keep {UnreadableNote}.
> fixer.saved.refresh.text-2

## One saved run. One could not be read. — text 3, when count is 1

One saved run.{UnreadableNote}

> Reads as: One saved run. One could not be read.
> Keep {UnreadableNote}.
> fixer.saved.refresh.text-3

## two saved runs, newest first. One could not be read. — text 4, otherwise

{Count} saved runs, newest first.{UnreadableNote}

> Reads as: two saved runs, newest first. One could not be read.
> Keep {Count} and {UnreadableNote}, in that order.
> fixer.saved.refresh.text-4

## One saved run could not be read and is not listed. — text 1, when unreadable is 1

One saved run could not be read and is not listed.

> This runs on from the words before it, so it is spoken as part of a longer sentence.
> fixer.saved.unreadable-note.text-1

## one saved runs could not be read and are not listed. — text 2, otherwise

{unreadable} saved runs could not be read and are not listed.

> Reads as: one saved runs could not be read and are not listed.
> Keep {unreadable}.
> This runs on from the words before it, so it is spoken as part of a longer sentence.
> fixer.saved.unreadable-note.text-2

## No run is selected. — text 1

No run is selected.

> fixer.saved.selected.text-1

## Transmit test report — TX-4K2P — text 1

{StageSetName} test report — {RunId}

> Reads as: Transmit test report — TX-4K2P
> Keep {StageSetName} and {RunId}, in that order.
> fixer.saved.view-selected.text-1

## Copy report — text 2

Copy report

> fixer.saved.view-selected.text-2

## Since this run stopped: Five checks. Two passed, three… — text 1

Since this run stopped: {summary}{NewLine}{NewLine}

> Reads as: Since this run stopped: Five checks. Two passed, three not yet run.NEWLINENEWLINE
> Keep {summary} and {NewLine}, in that order.
> fixer.saved.staleness-lead.text-1

## Since this run stoppedFive checks. Two passed, three not… — text 2

<h2>Since this run stopped</h2><p>{summary}</p>

> Reads as: <h2>Since this run stopped</h2><p>Five checks. Two passed, three not yet run.</p>
> Keep {summary}.
> Keep the markup exactly as it is — it builds the page.
> fixer.saved.staleness-lead.text-2

## Name cleared. The run goes back to its test ID, TX-4K2P. — text 1, when length is 0

Name cleared. The run goes back to its test ID, {RunId}.

> Reads as: Name cleared. The run goes back to its test ID, TX-4K2P.
> Keep {RunId}.
> fixer.saved.rename-selected.text-1

## Renamed to A dummy load. It keeps its test ID, TX-4K2P. — text 2, otherwise

Renamed to {Label}. It keeps its test ID, {RunId}.

> Reads as: Renamed to A dummy load. It keeps its test ID, TX-4K2P.
> Keep {Label} and {RunId}, in that order.
> fixer.saved.rename-selected.text-2

## The new name could not be saved. — text 3

The new name could not be saved.

> fixer.saved.rename-selected.text-3

## Run TX-4K2P has a result for every test, so there is… — text 1

Run {RunId} has a result for every test, so there is nothing left to continue. Open it to read the report.

> Reads as: Run TX-4K2P has a result for every test, so there is nothing left to continue. Open it to read the report.
> Keep {RunId}.
> fixer.saved.resume-selected.text-1

## Continue run Shack bench, Thursday? Its five recorded… — text 2

Continue run {DisplayName}? Its {ResolvedStageCount} recorded results are kept and the remaining {remaining} tests are yours to run. This is recorded as a second sitting, so the report will say the tests were not all done at one go.

> Reads as: Continue run Shack bench, Thursday? Its five recorded results are kept and the remaining REMAINING tests are yours to run. This is recorded as a second sitting, so the report will say the tests were not all done at one go.
> Keep {DisplayName}, {ResolvedStageCount} and {remaining}, in that order.
> fixer.saved.resume-selected.text-2

## Saved test runs — JJ Flexible — text 3

Saved test runs — JJ Flexible

> fixer.saved.resume-selected.text-3

## Export test run Shack bench, Thursday — its name

Export test run {DisplayName}

> Reads as: Export test run Shack bench, Thursday
> Keep {DisplayName}.
> fixer.saved.export-selected.title

## Exported to TX-4K2P.html. — text 1, when written

Exported to {GetFileName}.

> Reads as: Exported to TX-4K2P.html.
> Keep {GetFileName}.
> fixer.saved.export-selected.text-1

## The export failed. Nothing was written. — text 2, otherwise

The export failed. Nothing was written.

> fixer.saved.export-selected.text-2

## Delete run Shack bench, Thursday (test ID TX-4K2P)? Its… — text 1

Delete run {DisplayName} (test ID {RunId})? Its report and measurements will be gone for good — there is no undo.

> Reads as: Delete run Shack bench, Thursday (test ID TX-4K2P)? Its report and measurements will be gone for good — there is no undo.
> Keep {DisplayName} and {RunId}, in that order.
> fixer.saved.delete-selected.text-1

## Saved test runs — JJ Flexible — text 2

Saved test runs — JJ Flexible

> fixer.saved.delete-selected.text-2

## Run TX-4K2P deleted. — text 3

Run {RunId} deleted.

> Reads as: Run TX-4K2P deleted.
> Keep {RunId}.
> fixer.saved.delete-selected.text-3

## Run TX-4K2P could not be deleted. — text 4

Run {RunId} could not be deleted.

> Reads as: Run TX-4K2P could not be deleted.
> Keep {RunId}.
> fixer.saved.delete-selected.text-4

# Where these words live

Nothing to read here. This is how the tool finds each sentence again.

> Made 2026-08-29 09:46 · 343 sentences · 9 files
> fixer.page.render.title-1 · Radios/Fixer/FixerPage.cs 135 · 1c21bf
> fixer.page.header.heading-1 · Radios/Fixer/FixerPage.cs 234 · f01686
> fixer.page.header.paragraph-1 · Radios/Fixer/FixerPage.cs 241 · e816a0
> fixer.page.header.button-1 · Radios/Fixer/FixerPage.cs 257 · 3685fc
> fixer.page.summary.text-1 · Radios/Fixer/FixerPage.cs 292 · e5e572
> fixer.page.summary.text-2 · Radios/Fixer/FixerPage.cs 293 · 8d2dad
> fixer.page.summary.text-3 · Radios/Fixer/FixerPage.cs 294 · 2fd58b
> fixer.page.summary.text-4 · Radios/Fixer/FixerPage.cs 295 · f690ce
> fixer.page.summary.text-5 · Radios/Fixer/FixerPage.cs 296 · 188704
> fixer.page.summary.text-6 · Radios/Fixer/FixerPage.cs 298 · 4bd0ba
> fixer.page.summary.text-7 · Radios/Fixer/FixerPage.cs 302 · 4c820d
> fixer.page.summary.text-8 · Radios/Fixer/FixerPage.cs 304 · f92c89
> fixer.page.summary.text-9 · Radios/Fixer/FixerPage.cs 305 · 9c09f7
> fixer.page.how-to-section.heading-1 · Radios/Fixer/FixerPage.cs 372 · d68ae9
> fixer.page.how-to-section.disclosure-summary-1 · Radios/Fixer/FixerPage.cs 375 · dd0214
> fixer.page.how-to-section.bullet-1 · Radios/Fixer/FixerPage.cs 378 · b2e3d7
> fixer.page.how-to-section.bullet-2 · Radios/Fixer/FixerPage.cs 381 · 606ac1
> fixer.page.how-to-section.text-1 · Radios/Fixer/FixerPage.cs 388 · 4788d6
> fixer.page.how-to-section.text-2 · Radios/Fixer/FixerPage.cs 392 · 77bd47
> fixer.page.how-to-section.bullet-3 · Radios/Fixer/FixerPage.cs 394 · 0dad41
> fixer.page.declaration.text-1 · Radios/Fixer/FixerPage.cs 440 · cb5f46
> fixer.page.declaration.button-1 · Radios/Fixer/FixerPage.cs 458 · c89348
> fixer.page.stage-label.text-1 · Radios/Fixer/FixerPage.cs 515 · ac0988
> fixer.page.status-phrase.text-1 · Radios/Fixer/FixerPage.cs 537 · 284d1e
> fixer.page.status-phrase.text-2 · Radios/Fixer/FixerPage.cs 538 · b37361
> fixer.page.status-phrase.text-3 · Radios/Fixer/FixerPage.cs 539 · 389595
> fixer.page.status-phrase.text-4 · Radios/Fixer/FixerPage.cs 540 · 6d9973
> fixer.page.status-phrase.text-5 · Radios/Fixer/FixerPage.cs 541 · 2a3c14
> fixer.page.stage-card.paragraph-1 · Radios/Fixer/FixerPage.cs 595 · d25b2d
> fixer.page.stage-card.paragraph-2 · Radios/Fixer/FixerPage.cs 639 · 345ccd
> fixer.page.stage-card.disclosure-summary-1 · Radios/Fixer/FixerPage.cs 675 · f49af9
> fixer.page.stage-card.link-1 · Radios/Fixer/FixerPage.cs 681 · 08e8d8
> fixer.page.run-button.text-1 · Radios/Fixer/FixerPage.cs 708 · abaca0
> fixer.page.run-button.text-2 · Radios/Fixer/FixerPage.cs 708 · 82eba3
> fixer.page.skip-controls.paragraph-1 · Radios/Fixer/FixerPage.cs 736 · 9d80a7
> fixer.page.skip-controls.question-above-the-choices-1 · Radios/Fixer/FixerPage.cs 739 · 329191
> fixer.page.skip-controls.button-1 · Radios/Fixer/FixerPage.cs 750 · 163d38
> fixer.page.result-block.paragraph-1 · Radios/Fixer/FixerPage.cs 781 · d22df4
> fixer.page.result-block.text-1 · Radios/Fixer/FixerPage.cs 784 · 8eb931
> fixer.page.result-block.text-2 · Radios/Fixer/FixerPage.cs 821 · 8c0fe2
> fixer.page.report-section.paragraph-1 · Radios/Fixer/FixerPage.cs 897 · 82044a
> fixer.page.report-section.paragraph-2 · Radios/Fixer/FixerPage.cs 912 · 5e7a71
> fixer.page.report-section.paragraph-3 · Radios/Fixer/FixerPage.cs 938 · 26ce1b
> fixer.page.report-section.button-1 · Radios/Fixer/FixerPage.cs 947 · 0d1635
> fixer.stage.build.text-1 · Radios/Fixer/TransmitStageSet.cs 221 · 754bcd
> fixer.stage.build.text-2 · Radios/Fixer/TransmitStageSet.cs 223 · 3fd905
> fixer.skip.operator-skip.operator-choice.label · Radios/Fixer/TransmitStageSet.cs 229 · 081eab
> fixer.skip.operator-skip.operator-choice.effect-text · Radios/Fixer/TransmitStageSet.cs 231 · 3e9f5a
> fixer.skip.remote-no-direct-speech.narrows-fault-domain.label · Radios/Fixer/TransmitStageSet.cs 240 · d6052a
> fixer.skip.remote-no-direct-speech.narrows-fault-domain.effect-text · Radios/Fixer/TransmitStageSet.cs 242 · 97e376
> fixer.skip.no-microphone.leaves-question-open.label · Radios/Fixer/TransmitStageSet.cs 248 · a4fdfd
> fixer.skip.no-microphone.leaves-question-open.effect-text · Radios/Fixer/TransmitStageSet.cs 250 · 714cad
> fixer.stage.audio-setup.title · Radios/Fixer/TransmitStageSet.cs 265 · 17ffec
> fixer.stage.audio-setup.question · Radios/Fixer/TransmitStageSet.cs 266 · 42ed37
> fixer.stage.audio-setup.explanation · Radios/Fixer/TransmitStageSet.cs 269 · 0f0dc6
> fixer.stage.audio-setup.describe-run-action · Radios/Fixer/TransmitStageSet.cs 285 · a0f4c7
> fixer.declaration.radio-hearing.question · Radios/Fixer/TransmitStageSet.cs 306 · cf8f0b
> fixer.declaration.radio-hearing.why-it-matters · Radios/Fixer/TransmitStageSet.cs 307 · 022a0e
> fixer.answer.hears.label · Radios/Fixer/TransmitStageSet.cs 316 · b4e10b
> fixer.answer.hears-nothing.label · Radios/Fixer/TransmitStageSet.cs 318 · 6e1b79
> fixer.answer.no-radio.label · Radios/Fixer/TransmitStageSet.cs 320 · 65b9f8
> fixer.action.open-device-picker.label · Radios/Fixer/TransmitStageSet.cs 332 · c7c382
> fixer.stage.microphone-check.title · Radios/Fixer/TransmitStageSet.cs 342 · e1d1a8
> fixer.stage.microphone-check.question · Radios/Fixer/TransmitStageSet.cs 343 · 0c653e
> fixer.stage.microphone-check.explanation · Radios/Fixer/TransmitStageSet.cs 345 · bbaef8
> fixer.stage.microphone-check.describe-run-action · Radios/Fixer/TransmitStageSet.cs 359 · 247e4c
> fixer.stage.transmitter-check.title · Radios/Fixer/TransmitStageSet.cs 376 · c92679
> fixer.stage.transmitter-check.question · Radios/Fixer/TransmitStageSet.cs 377 · 4aab13
> fixer.stage.transmitter-check.explanation · Radios/Fixer/TransmitStageSet.cs 379 · e01542
> fixer.stage.transmitter-check.describe-run-action · Radios/Fixer/TransmitStageSet.cs 400 · 6f4938
> fixer.action.transmitter-check.open-power-dialog.label · Radios/Fixer/TransmitStageSet.cs 415 · a5f7ce
> fixer.stage.injected-transmit.title · Radios/Fixer/TransmitStageSet.cs 427 · f7e268
> fixer.stage.injected-transmit.question · Radios/Fixer/TransmitStageSet.cs 428 · ba59a8
> fixer.stage.injected-transmit.explanation · Radios/Fixer/TransmitStageSet.cs 430 · d4176e
> fixer.stage.injected-transmit.describe-run-action · Radios/Fixer/TransmitStageSet.cs 443 · da1160
> fixer.action.injected-transmit.open-power-dialog.label · Radios/Fixer/TransmitStageSet.cs 455 · 77c969
> fixer.stage.spoken-transmit.title · Radios/Fixer/TransmitStageSet.cs 470 · a3977d
> fixer.stage.spoken-transmit.question · Radios/Fixer/TransmitStageSet.cs 471 · cb6b4c
> fixer.stage.spoken-transmit.explanation · Radios/Fixer/TransmitStageSet.cs 473 · ef950c
> fixer.stage.spoken-transmit.describe-run-action · Radios/Fixer/TransmitStageSet.cs 488 · 8b64c7
> fixer.action.spoken-transmit.open-power-dialog.label · Radios/Fixer/TransmitStageSet.cs 498 · 77c969
> fixer.set.transmit.intro · Radios/Fixer/TransmitStageSet.cs 521 · fda9db
> fixer.declaration.antenna-load.question · Radios/Fixer/TransmitStageSet.cs 534 · 052b3c
> fixer.declaration.antenna-load.why-it-matters · Radios/Fixer/TransmitStageSet.cs 535 · 7e61cd
> fixer.answer.antenna-load.choices.dummy-load.label · Radios/Fixer/TransmitStageSet.cs 545 · a661b8
> fixer.answer.antenna-load.choices.antenna.label · Radios/Fixer/TransmitStageSet.cs 547 · abe454
> fixer.answer.antenna-load.choices.amplifier.label · Radios/Fixer/TransmitStageSet.cs 554 · 325399
> fixer.answer.nothing-unsure.label · Radios/Fixer/TransmitStageSet.cs 557 · f4c7ec
> fixer.declaration.transmit.run-declarations.when-length-is-over-0.antenna-load.question-now · Radios/Fixer/TransmitStageSet.cs 575 · d88a18
> fixer.declaration.otherwise.antenna-load.question-now · Radios/Fixer/TransmitStageSet.cs 578 · d29299
> fixer.declaration.transmit.run-declarations.when-length-is-over-0.antenna-load.question-now.2 · Radios/Fixer/TransmitStageSet.cs 582 · 8f5b22
> fixer.answer.antenna-load.choices-now.dummy-load.label · Radios/Fixer/TransmitStageSet.cs 613 · 2a35fd
> fixer.answer.antenna-load.choices-now.antenna.label · Radios/Fixer/TransmitStageSet.cs 616 · ae7918
> fixer.answer.antenna-load.choices-now.amplifier.label · Radios/Fixer/TransmitStageSet.cs 619 · 6a1be4
> fixer.answer.remote-not-confirmed.label · Radios/Fixer/TransmitStageSet.cs 622 · 4dda59
> fixer.declaration.antenna-load.why-it-matters-now · Radios/Fixer/TransmitStageSet.cs 636 · 1352da
> fixer.finding.mme-in-use.us.what-is-wrong · Radios/Fixer/AudioSetupCheck.cs 191 · 834c2c
> fixer.finding.mme-in-use.us.what-to-do · Radios/Fixer/AudioSetupCheck.cs 197 · 36b5fd
> fixer.finding.mme-in-use.nobody-here.what-is-wrong · Radios/Fixer/AudioSetupCheck.cs 203 · d20aaf
> fixer.finding.mme-in-use.nobody-here.what-to-do · Radios/Fixer/AudioSetupCheck.cs 207 · 883e51
> fixer.finding.no-input-selected.us.what-is-wrong · Radios/Fixer/AudioSetupCheck.cs 219 · b3acb2
> fixer.finding.no-input-selected.us.what-to-do · Radios/Fixer/AudioSetupCheck.cs 221 · 6fb43a
> fixer.finding.no-input-anywhere.operator.what-is-wrong · Radios/Fixer/AudioSetupCheck.cs 227 · f38234
> fixer.finding.no-input-anywhere.operator.what-to-do · Radios/Fixer/AudioSetupCheck.cs 229 · c72de5
> fixer.finding.pc-audio-off.us.what-is-wrong · Radios/Fixer/AudioSetupCheck.cs 239 · a26923
> fixer.finding.pc-audio-off.us.what-to-do · Radios/Fixer/AudioSetupCheck.cs 242 · bcb9df
> fixer.finding.mic-profile-empty.us.what-is-wrong · Radios/Fixer/AudioSetupCheck.cs 249 · 5d663f
> fixer.finding.mic-profile-empty.us.what-to-do · Radios/Fixer/AudioSetupCheck.cs 252 · b3478c
> fixer.finding.windows-muted.operator.what-is-wrong · Radios/Fixer/AudioSetupCheck.cs 261 · ce739c
> fixer.finding.windows-muted.operator.what-to-do · Radios/Fixer/AudioSetupCheck.cs 264 · 245039
> fixer.finding.privacy-blocked.operator.what-is-wrong · Radios/Fixer/AudioSetupCheck.cs 268 · d0539f
> fixer.finding.privacy-blocked.operator.what-to-do · Radios/Fixer/AudioSetupCheck.cs 270 · 4fcb89
> fixer.finding.unplugged.operator.what-is-wrong · Radios/Fixer/AudioSetupCheck.cs 275 · e6dbb2
> fixer.finding.unplugged.operator.what-to-do · Radios/Fixer/AudioSetupCheck.cs 276 · 4b6c41
> fixer.finding.when-remote-radio.hears-nothing.operator.what-is-wrong · Radios/Fixer/AudioSetupCheck.cs 292 · 32866c
> fixer.finding.when-remote-radio.hears-nothing.operator.what-to-do · Radios/Fixer/AudioSetupCheck.cs 294 · 47662b
> fixer.finding.otherwise.hears-nothing.operator.what-is-wrong · Radios/Fixer/AudioSetupCheck.cs 299 · 7eabc4
> fixer.finding.otherwise.hears-nothing.operator.what-to-do · Radios/Fixer/AudioSetupCheck.cs 300 · cbbae9
> fixer.finding.config-open-mismatch.us.what-to-do · Radios/Fixer/AudioSetupCheck.cs 312 · 0dddd1
> fixer.finding.describe-mismatch.text-1 · Radios/Fixer/AudioSetupCheck.cs 434 · fa5a90
> fixer.finding.describe-mismatch.text-2 · Radios/Fixer/AudioSetupCheck.cs 439 · 67a042
> fixer.finding.describe-mismatch.text-3 · Radios/Fixer/AudioSetupCheck.cs 442 · 9e310d
> fixer.finding.describe-mismatch.text-4 · Radios/Fixer/AudioSetupCheck.cs 443 · fa8fc1
> fixer.finding.host-api-phrase.text-1 · Radios/Fixer/AudioSetupCheck.cs 488 · 44ea9d
> fixer.finding.host-api-phrase.text-2 · Radios/Fixer/AudioSetupCheck.cs 489 · c4c08b
> fixer.finding.answer.text-1 · Radios/Fixer/AudioSetupCheck.cs 495 · 962516
> fixer.finding.answer.text-2 · Radios/Fixer/AudioSetupCheck.cs 518 · ff1aeb
> fixer.finding.answer.text-3 · Radios/Fixer/AudioSetupCheck.cs 521 · 063571
> fixer.finding.answer.text-4 · Radios/Fixer/AudioSetupCheck.cs 522 · ef2176
> fixer.finding.answer.text-5 · Radios/Fixer/AudioSetupCheck.cs 524 · e11e14
> fixer.finding.answer.text-6 · Radios/Fixer/AudioSetupCheck.cs 527 · 4d34a9
> fixer.finding.answer.text-7 · Radios/Fixer/AudioSetupCheck.cs 528 · 3a3c51
> fixer.finding.answer.text-8 · Radios/Fixer/AudioSetupCheck.cs 530 · a0dfa8
> fixer.finding.answer.text-9 · Radios/Fixer/AudioSetupCheck.cs 533 · 10d4d7
> fixer.finding.hearing.text-1 · Radios/Fixer/AudioSetupCheck.cs 550 · a62ec8
> fixer.finding.hearing.text-2 · Radios/Fixer/AudioSetupCheck.cs 556 · 78372b
> fixer.finding.hearing.text-3 · Radios/Fixer/AudioSetupCheck.cs 561 · 916121
> fixer.finding.evidence.text-1 · Radios/Fixer/AudioSetupCheck.cs 571 · e789fc
> fixer.finding.evidence.text-2 · Radios/Fixer/AudioSetupCheck.cs 573 · 5fe07e
> fixer.finding.evidence.text-3 · Radios/Fixer/AudioSetupCheck.cs 574 · 5f0d15
> fixer.finding.evidence.text-4 · Radios/Fixer/AudioSetupCheck.cs 575 · 8dcb10
> fixer.finding.evidence.text-5 · Radios/Fixer/AudioSetupCheck.cs 576 · ea964e
> fixer.finding.evidence.text-6 · Radios/Fixer/AudioSetupCheck.cs 577 · 4df271
> fixer.finding.evidence.text-7 · Radios/Fixer/AudioSetupCheck.cs 578 · 2087cb
> fixer.finding.evidence.text-8 · Radios/Fixer/AudioSetupCheck.cs 579 · a23402
> fixer.finding.evidence.text-9 · Radios/Fixer/AudioSetupCheck.cs 580 · 2087cb
> fixer.finding.evidence.text-10 · Radios/Fixer/AudioSetupCheck.cs 581 · 0c46b5
> fixer.finding.evidence.text-11 · Radios/Fixer/AudioSetupCheck.cs 582 · b847c0
> fixer.finding.evidence.text-12 · Radios/Fixer/AudioSetupCheck.cs 583 · 0a07a7
> fixer.finding.evidence.text-13 · Radios/Fixer/AudioSetupCheck.cs 584 · d5e2a8
> fixer.finding.evidence.text-14 · Radios/Fixer/AudioSetupCheck.cs 585 · 856b44
> fixer.finding.evidence.text-15 · Radios/Fixer/AudioSetupCheck.cs 585 · 59cbb9
> fixer.finding.evidence.text-16 · Radios/Fixer/AudioSetupCheck.cs 586 · 004593
> fixer.finding.evidence.text-17 · Radios/Fixer/AudioSetupCheck.cs 586 · 2e0e68
> fixer.finding.evidence.text-18 · Radios/Fixer/AudioSetupCheck.cs 587 · 9872a2
> fixer.finding.evidence.text-19 · Radios/Fixer/AudioSetupCheck.cs 588 · e19b29
> fixer.finding.evidence.text-20 · Radios/Fixer/AudioSetupCheck.cs 589 · 61a81c
> fixer.finding.evidence.text-21 · Radios/Fixer/AudioSetupCheck.cs 590 · 6ff542
> fixer.finding.tristate.text-1 · Radios/Fixer/AudioSetupCheck.cs 598 · 37b79a
> fixer.finding.hearing-evidence.text-1 · Radios/Fixer/AudioSetupCheck.cs 604 · cfbb17
> fixer.finding.hearing-evidence.text-2 · Radios/Fixer/AudioSetupCheck.cs 605 · 2ce652
> fixer.finding.hearing-evidence.text-3 · Radios/Fixer/AudioSetupCheck.cs 606 · 881b31
> fixer.finding.hearing-evidence.text-4 · Radios/Fixer/AudioSetupCheck.cs 607 · ce1533
> fixer.finding.microphone.text-1 · Radios/Fixer/TransmitStages.cs 94 · d9dd98
> fixer.finding.microphone.text-2 · Radios/Fixer/TransmitStages.cs 114 · f847ad
> fixer.finding.microphone.text-3 · Radios/Fixer/TransmitStages.cs 115 · 1b619c
> fixer.finding.microphone.text-4 · Radios/Fixer/TransmitStages.cs 117 · 85c714
> fixer.finding.microphone.text-5 · Radios/Fixer/TransmitStages.cs 124 · 50a22c
> fixer.finding.microphone.text-6 · Radios/Fixer/TransmitStages.cs 125 · 1b619c
> fixer.finding.mic-silent.operator.what-is-wrong · Radios/Fixer/TransmitStages.cs 127 · 89add3
> fixer.finding.silent-mic-advice.text-1 · Radios/Fixer/TransmitStages.cs 154 · 373d26
> fixer.finding.silent-mic-advice.text-2 · Radios/Fixer/TransmitStages.cs 159 · a25856
> fixer.finding.silent-mic-advice.text-3 · Radios/Fixer/TransmitStages.cs 163 · 77c830
> fixer.finding.silent-mic-advice.text-4 · Radios/Fixer/TransmitStages.cs 171 · 9766eb
> fixer.finding.silent-mic-advice.text-5 · Radios/Fixer/TransmitStages.cs 178 · f3c150
> fixer.finding.mic-evidence.text-1 · Radios/Fixer/TransmitStages.cs 185 · 1b491e
> fixer.finding.mic-evidence.text-2 · Radios/Fixer/TransmitStages.cs 187 · 704e4c
> fixer.finding.mic-evidence.text-3 · Radios/Fixer/TransmitStages.cs 188 · 258861
> fixer.finding.mic-evidence.text-4 · Radios/Fixer/TransmitStages.cs 188 · 2087cb
> fixer.finding.mic-evidence.text-5 · Radios/Fixer/TransmitStages.cs 189 · ac4632
> fixer.finding.mic-evidence.text-6 · Radios/Fixer/TransmitStages.cs 189 · 2087cb
> fixer.finding.mic-evidence.text-7 · Radios/Fixer/TransmitStages.cs 190 · 6303e0
> fixer.finding.mic-evidence.text-8 · Radios/Fixer/TransmitStages.cs 191 · de6528
> fixer.finding.tx-no-power.operator.what-is-wrong · Radios/Fixer/TransmitStages.cs 230 · 0496ec
> fixer.finding.tx-no-power.operator.what-to-do · Radios/Fixer/TransmitStages.cs 234 · a751ae
> fixer.finding.tx-load-suspect.operator.what-is-wrong · Radios/Fixer/TransmitStages.cs 244 · 92859d
> fixer.finding.tx-load-suspect.operator.what-to-do · Radios/Fixer/TransmitStages.cs 246 · 7d9739
> fixer.finding.injected.text-1 · Radios/Fixer/TransmitStages.cs 277 · 439a88
> fixer.finding.injected.text-2 · Radios/Fixer/TransmitStages.cs 285 · 5aba0f
> fixer.finding.injected.text-3 · Radios/Fixer/TransmitStages.cs 286 · 37b79a
> fixer.finding.spoken.text-1 · Radios/Fixer/TransmitStages.cs 321 · 7591cb
> fixer.finding.spoken.text-2 · Radios/Fixer/TransmitStages.cs 326 · 2f148b
> fixer.finding.spoken.text-3 · Radios/Fixer/TransmitStages.cs 326 · 1b619c
> fixer.finding.spoken.text-4 · Radios/Fixer/TransmitStages.cs 331 · dbfe31
> fixer.finding.spoken.text-5 · Radios/Fixer/TransmitStages.cs 332 · b3b67e
> fixer.finding.spoken.text-6 · Radios/Fixer/TransmitStages.cs 340 · e3fb3d
> fixer.finding.spoken.text-7 · Radios/Fixer/TransmitStages.cs 346 · 1dc89d
> fixer.finding.spoken.text-8 · Radios/Fixer/TransmitStages.cs 353 · e0191e
> fixer.finding.spoken.text-9 · Radios/Fixer/TransmitStages.cs 355 · 529240
> fixer.finding.spoken.text-10 · Radios/Fixer/TransmitStages.cs 356 · 2b5da6
> fixer.finding.spoken.text-11 · Radios/Fixer/TransmitStages.cs 357 · 059dcc
> fixer.finding.spoken.text-12 · Radios/Fixer/TransmitStages.cs 358 · 258861
> fixer.finding.spoken.text-13 · Radios/Fixer/TransmitStages.cs 358 · 2087cb
> fixer.finding.spoken.text-14 · Radios/Fixer/TransmitStages.cs 359 · ac4632
> fixer.finding.spoken.text-15 · Radios/Fixer/TransmitStages.cs 359 · 2087cb
> fixer.finding.spoken.text-16 · Radios/Fixer/TransmitStages.cs 360 · 147bce
> fixer.finding.spoken.text-17 · Radios/Fixer/TransmitStages.cs 361 · 8acaee
> fixer.finding.spoken.text-18 · Radios/Fixer/TransmitStages.cs 364 · 8bd75a
> fixer.finding.spoken.text-19 · Radios/Fixer/TransmitStages.cs 364 · 6bf7ed
> fixer.finding.spoken.text-20 · Radios/Fixer/TransmitStages.cs 365 · a7652f
> fixer.finding.spoken.text-21 · Radios/Fixer/TransmitStages.cs 366 · 664de2
> fixer.finding.load-line.text-1 · Radios/Fixer/TransmitStages.cs 391 · f17b64
> fixer.finding.db.text-1 · Radios/Fixer/TransmitStages.cs 398 · 059dcc
> fixer.finding.db.text-2 · Radios/Fixer/TransmitStages.cs 399 · 339ae3
> fixer.refusal.load-declaration-for-report.text-1 · Radios/Fixer/FixerTransmitGate.cs 233 · 5441d7
> fixer.refusal.load-declaration-for-report.text-2 · Radios/Fixer/FixerTransmitGate.cs 238 · 8814e7
> fixer.refusal.request.text-1 · Radios/Fixer/FixerTransmitGate.cs 367 · f1e327
> fixer.refusal.request.text-2 · Radios/Fixer/FixerTransmitGate.cs 371 · 8fe8e2
> fixer.refusal.request.text-3 · Radios/Fixer/FixerTransmitGate.cs 376 · 0bb8e1
> fixer.refusal.request.text-4 · Radios/Fixer/FixerTransmitGate.cs 380 · 13b84d
> fixer.refusal.request.text-5 · Radios/Fixer/FixerTransmitGate.cs 385 · 1fe537
> fixer.refusal.request.text-6 · Radios/Fixer/FixerTransmitGate.cs 389 · 3b2613
> fixer.refusal.request.text-7 · Radios/Fixer/FixerTransmitGate.cs 394 · 86ce98
> fixer.refusal.request.text-8 · Radios/Fixer/FixerTransmitGate.cs 398 · 3a0708
> fixer.refusal.request.text-9 · Radios/Fixer/FixerTransmitGate.cs 410 · 91a3ab
> fixer.refusal.request.text-10 · Radios/Fixer/FixerTransmitGate.cs 416 · abca6c
> fixer.refusal.request.text-11 · Radios/Fixer/FixerTransmitGate.cs 429 · 29add2
> fixer.refusal.request.text-12 · Radios/Fixer/FixerTransmitGate.cs 429 · 10ab58
> fixer.refusal.request.text-13 · Radios/Fixer/FixerTransmitGate.cs 433 · e63030
> fixer.refusal.request.text-14 · Radios/Fixer/FixerTransmitGate.cs 441 · c0e728
> fixer.refusal.request.text-15 · Radios/Fixer/FixerTransmitGate.cs 461 · 297798
> fixer.refusal.request.text-16 · Radios/Fixer/FixerTransmitGate.cs 470 · 330403
> fixer.refusal.request.text-17 · Radios/Fixer/FixerTransmitGate.cs 481 · e8c637
> fixer.refusal.request.text-18 · Radios/Fixer/FixerTransmitGate.cs 488 · d9523b
> fixer.refusal.request.text-19 · Radios/Fixer/FixerTransmitGate.cs 494 · ee0b1f
> fixer.refusal.request.text-20 · Radios/Fixer/FixerTransmitGate.cs 501 · bfadd6
> fixer.report.build.text-1 · Radios/Fixer/FixerReport.cs 73 · 1b732a
> fixer.report.build.text-2 · Radios/Fixer/FixerReport.cs 74 · 04b444
> fixer.report.build.text-3 · Radios/Fixer/FixerReport.cs 79 · 43cf6b
> fixer.report.build.text-4 · Radios/Fixer/FixerReport.cs 80 · 7ac8e2
> fixer.report.found-section.title · Radios/Fixer/FixerReport.cs 103 · b30522
> fixer.report.found-section.text-1 · Radios/Fixer/FixerReport.cs 110 · 07aeea
> fixer.report.found-section.text-2 · Radios/Fixer/FixerReport.cs 117 · f2f17c
> fixer.report.found-section.text-3 · Radios/Fixer/FixerReport.cs 126 · 4e50ef
> fixer.report.found-section.text-4 · Radios/Fixer/FixerReport.cs 136 · 8efb18
> fixer.report.found-section.text-5 · Radios/Fixer/FixerReport.cs 141 · 725b37
> fixer.report.found-section.text-6 · Radios/Fixer/FixerReport.cs 143 · 8c0fe2
> fixer.report.coverage-section.title · Radios/Fixer/FixerReport.cs 153 · 918978
> fixer.report.coverage-section.text-1 · Radios/Fixer/FixerReport.cs 158 · ae9ab0
> fixer.report.coverage-section.text-2 · Radios/Fixer/FixerReport.cs 159 · aae687
> fixer.report.coverage-section.text-3 · Radios/Fixer/FixerReport.cs 165 · 0f464a
> fixer.report.coverage-section.text-4 · Radios/Fixer/FixerReport.cs 167 · a4b7d4
> fixer.report.coverage-section.text-5 · Radios/Fixer/FixerReport.cs 176 · bb7184
> fixer.report.coverage-section.text-6 · Radios/Fixer/FixerReport.cs 191 · 206978
> fixer.report.coverage-section.text-7 · Radios/Fixer/FixerReport.cs 202 · e0aa9d
> fixer.report.fixes-section.title · Radios/Fixer/FixerReport.cs 208 · b9e2a3
> fixer.report.fixes-section.text-1 · Radios/Fixer/FixerReport.cs 209 · 5ab2b7
> fixer.report.fixes-section.text-2 · Radios/Fixer/FixerReport.cs 218 · a4b7d4
> fixer.report.fixes-section.text-3 · Radios/Fixer/FixerReport.cs 222 · ad79ad
> fixer.report.fixes-section.text-4 · Radios/Fixer/FixerReport.cs 223 · 05b4ab
> fixer.report.fixes-section.text-5 · Radios/Fixer/FixerReport.cs 227 · 1c4baf
> fixer.report.stage-section.title · Radios/Fixer/FixerReport.cs 238 · ac0988
> fixer.report.stage-section.text-1 · Radios/Fixer/FixerReport.cs 246 · 56f94f
> fixer.report.stage-section.text-2 · Radios/Fixer/FixerReport.cs 259 · 2ce2c7
> fixer.report.stage-section.text-3 · Radios/Fixer/FixerReport.cs 264 · c95a52
> fixer.report.stage-section.text-4 · Radios/Fixer/FixerReport.cs 266 · 70f2fb
> fixer.report.stage-section.text-5 · Radios/Fixer/FixerReport.cs 274 · 725b37
> fixer.report.stage-section.text-6 · Radios/Fixer/FixerReport.cs 276 · 8c0fe2
> fixer.report.describe.text-1 · Radios/Fixer/FixerReport.cs 293 · a4b7d4
> fixer.report.describe.text-2 · Radios/Fixer/FixerReport.cs 297 · 0ad98a
> fixer.report.describe.text-3 · Radios/Fixer/FixerReport.cs 298 · 139ed5
> fixer.window.fixer-dialog.text-1 · JJFlexWpf/Dialogs/FixerDialog.cs 358 · 50c62a
> fixer.window.fixer-dialog.text-2 · JJFlexWpf/Dialogs/FixerDialog.cs 363 · f01686
> fixer.window.describe-gate-derivation.text-1 · JJFlexWpf/Dialogs/FixerDialog.cs 506 · bf1578
> fixer.window.describe-gate-derivation.text-2 · JJFlexWpf/Dialogs/FixerDialog.cs 514 · 4e7933
> fixer.window.describe-gate-derivation.text-3 · JJFlexWpf/Dialogs/FixerDialog.cs 520 · 839045
> fixer.window.prepare-reference-voice.text-1 · JJFlexWpf/Dialogs/FixerDialog.cs 573 · 59cb22
> fixer.window.prepare-reference-voice.text-2 · JJFlexWpf/Dialogs/FixerDialog.cs 581 · f18756
> fixer.window.show.text-1 · JJFlexWpf/Dialogs/FixerDialog.cs 631 · 1105cb
> fixer.window.show.text-2 · JJFlexWpf/Dialogs/FixerDialog.cs 632 · f41851
> fixer.window.show.text-3 · JJFlexWpf/Dialogs/FixerDialog.cs 644 · 1105cb
> fixer.window.why-it-cannot-be-resumed.text-1 · JJFlexWpf/Dialogs/FixerDialog.cs 663 · 81bf22
> fixer.window.why-it-cannot-be-resumed.text-2 · JJFlexWpf/Dialogs/FixerDialog.cs 670 · 944011
> fixer.window.why-it-cannot-be-resumed.text-3 · JJFlexWpf/Dialogs/FixerDialog.cs 677 · 46b4b0
> fixer.window.why-it-cannot-be-resumed.text-4 · JJFlexWpf/Dialogs/FixerDialog.cs 688 · c1bc28
> fixer.window.on-web-message.text-1 · JJFlexWpf/Dialogs/FixerDialog.cs 936 · f1f4dd
> fixer.window.handle.text-1 · JJFlexWpf/Dialogs/FixerDialog.cs 972 · 35e79f
> fixer.window.handle.text-2 · JJFlexWpf/Dialogs/FixerDialog.cs 994 · 35e79f
> fixer.window.handle.text-3 · JJFlexWpf/Dialogs/FixerDialog.cs 1018 · d8d035
> fixer.window.handle.text-4 · JJFlexWpf/Dialogs/FixerDialog.cs 1076 · 9afbad
> fixer.window.run-stage.text-1 · JJFlexWpf/Dialogs/FixerDialog.cs 1122 · 586e26
> fixer.window.run-stage.text-2 · JJFlexWpf/Dialogs/FixerDialog.cs 1155 · c6ce0d
> fixer.window.ask-exit.text-1 · JJFlexWpf/Dialogs/FixerDialog.cs 1402 · d70346
> fixer.window.ask-exit.text-2 · JJFlexWpf/Dialogs/FixerDialog.cs 1406 · 0c909a
> fixer.window.ask-exit.text-3 · JJFlexWpf/Dialogs/FixerDialog.cs 1409 · a65575
> fixer.window.ask-exit.text-4 · JJFlexWpf/Dialogs/FixerDialog.cs 1412 · d76c05
> fixer.window.ask-exit.text-5 · JJFlexWpf/Dialogs/FixerDialog.cs 1416 · b19830
> fixer.window.ask-exit.text-6 · JJFlexWpf/Dialogs/FixerDialog.cs 1420 · 67c5df
> fixer.window.ask-exit.text-7 · JJFlexWpf/Dialogs/FixerDialog.cs 1426 · 50c62a
> fixer.window.close-keeping-run.text-1 · JJFlexWpf/Dialogs/FixerDialog.cs 1436 · 4e06ae
> fixer.window.on-closing.text-1 · JJFlexWpf/Dialogs/FixerDialog.cs 1507 · 4e06ae
> fixer.window.to-page.text-1 · JJFlexWpf/Dialogs/FixerDialog.cs 1649 · dcdfdf
> fixer.window.copy-report.text-1 · JJFlexWpf/Dialogs/FixerDialog.cs 1681 · 0a1462
> fixer.window.open-help.text-1 · JJFlexWpf/Dialogs/FixerDialog.cs 1708 · ba4416
> fixer.window.open-power-dialog.text-1 · JJFlexWpf/Dialogs/FixerDialog.cs 1728 · 8dcc3f
> fixer.window.open-power-dialog.text-2 · JJFlexWpf/Dialogs/FixerDialog.cs 1741 · 088ed2
> fixer.window.open-device-picker.text-1 · JJFlexWpf/Dialogs/FixerDialog.cs 1772 · 23e690
> fixer.exit.fixer-exit-prompt.text-1 · JJFlexWpf/Dialogs/FixerExitPrompt.cs 85 · e08eaa
> fixer.exit.fixer-exit-prompt.text-2 · JJFlexWpf/Dialogs/FixerExitPrompt.cs 88 · 6e2daf
> fixer.exit.fixer-exit-prompt.text-3 · JJFlexWpf/Dialogs/FixerExitPrompt.cs 89 · 8f9554
> fixer.exit.fixer-exit-prompt.text-4 · JJFlexWpf/Dialogs/FixerExitPrompt.cs 100 · febcc7
> fixer.saved.fixer-past-runs-dialog.text-1 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 47 · 7bc86b
> fixer.saved.fixer-past-runs-dialog.text · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 59 · e7b3cb
> fixer.saved.fixer-past-runs-dialog.text-2 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 82 · 67daba
> fixer.saved.fixer-past-runs-dialog.text-3 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 82 · 45e1eb
> fixer.saved.fixer-past-runs-dialog.text-4 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 84 · 952612
> fixer.saved.fixer-past-runs-dialog.text-5 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 93 · 229203
> fixer.saved.fixer-past-runs-dialog.text-6 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 95 · 484027
> fixer.saved.make-button.text-1 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 136 · ac8c6a
> fixer.saved.refresh.text-1 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 152 · 3ac7a2
> fixer.saved.refresh.text-2 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 164 · 65b04b
> fixer.saved.refresh.text-3 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 171 · 01f8df
> fixer.saved.refresh.text-4 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 172 · 9ca456
> fixer.saved.unreadable-note.text-1 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 184 · a97efa
> fixer.saved.unreadable-note.text-2 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 185 · 7c09af
> fixer.saved.selected.text-1 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 192 · 4665f6
> fixer.saved.view-selected.text-1 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 207 · d22567
> fixer.saved.view-selected.text-2 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 211 · ffedb1
> fixer.saved.staleness-lead.text-1 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 234 · 3f0da5
> fixer.saved.staleness-lead.text-2 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 236 · 1cd814
> fixer.saved.rename-selected.text-1 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 287 · b14948
> fixer.saved.rename-selected.text-2 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 288 · 4e6d11
> fixer.saved.rename-selected.text-3 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 298 · 0d9182
> fixer.saved.resume-selected.text-1 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 334 · 05d85e
> fixer.saved.resume-selected.text-2 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 344 · 39d0c2
> fixer.saved.resume-selected.text-3 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 348 · 7bc86b
> fixer.saved.export-selected.title · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 368 · bc49a5
> fixer.saved.export-selected.text-1 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 384 · 33b03c
> fixer.saved.export-selected.text-2 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 385 · 910960
> fixer.saved.delete-selected.text-1 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 396 · f665a1
> fixer.saved.delete-selected.text-2 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 398 · 7bc86b
> fixer.saved.delete-selected.text-3 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 404 · a5ce1b
> fixer.saved.delete-selected.text-4 · JJFlexWpf/Dialogs/FixerPastRunsDialog.cs 409 · 2a81c7

