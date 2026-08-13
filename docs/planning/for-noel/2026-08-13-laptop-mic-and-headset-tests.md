# Laptop microphone and headset tests — 2026-08-13

Build: Debug x64, built 04:46 this morning, at
`bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe`.

Everything below is testable on the laptop. Tests 1 through 7 need **no radio
at all** — they are the whole point of the Microphone Check, which exists so
you never have to key up to find out whether a microphone is working. Tests 8
through 11 need a radio connection; do those over SmartLink to the 8600 (no
antenna, so keying is safe) or skip them and I will fold them into the bench
session.

Answer in the `**** ` slots. Or say "walk me through these" and I will run them
one at a time here in chat instead — same tests, no file.

**What is new since you last ran this build.** The whole "This Computer"
section of the Audio Workshop, the walk-through ordering, the Microphone Check,
device folding in the picker, both audio numbers, noise-floor detection, the
preset toolbar — none of it has met an actual microphone yet. Plus this
morning's sample-rate fix, which is what tests 4 and 5 are really about.

---

## Section A — The Audio Workshop, no radio

### Test 1 — The Workshop opens on something useful

Launch the app. Do not connect to a radio. Press Ctrl+Shift+W.

The Workshop should open on the TX Audio tab. Because no input device is
chosen yet on a fresh profile — or if one is, because this is the first
section now — focus should land somewhere in the "This Computer" group at the
top, not part-way down the dialog.

Tell me where focus landed and what your screen reader said.

**** 

### Test 2 — The sections read as a walk-through

From the top of the Workshop, arrow or tab down through the whole TX Audio tab
and tell me the group headings in the order you meet them.

Expected order: This Computer, Microphone, Processing, TX Filter, TX Monitor,
Test Tone, Audio Check. The intent is that reading top to bottom walks you from
"what is my computer using" through "how does it sound" to "prove it."

Does that order actually feel like a flow, or does something want to move?

**** 

### Test 3 — The Workshop names your device without you asking

Still in the Workshop, in the This Computer group, there is a read-only field
above the buttons.

It should name the input device currently selected, in words, not as a number
or an index. With your USB headset connected and chosen, it should say the
headset.

What does it read?

**** 

---

## Section B — The Microphone Check and the sample rate

This is the part I most want an answer on, because this morning's fix changes
what happens when a device is not running at 48 kHz.

### Test 4 — Check Microphone, straight from the Workshop

In the Workshop's This Computer group there is a "Check Microphone..." button.
Press it.

It should open the Audio Devices dialog with the check **already running** and
focus already in the reading field — you should not have to find and press
Start yourself.

Talk normally for about ten seconds. The reading updates about twice a second
but is deliberately not a live region, so it will not interrupt you; use your
screen reader's read-current-line to sample it when you want.

Tell me: did it start on its own, where did focus land, and what did the
reading say while you were talking?

**** 

### Test 5 — What rate is your headset actually running at?

Same check, still running. Read the **start** message in the reading field —
the first thing it said when the check began.

If your headset is running at 48 kHz, that message is exactly "Microphone
check running. Listening." and nothing more. **That is the good outcome.**

If Windows has it at 44.1 kHz, the message continues: "Note: Windows is
running this microphone at 44.1 kHz..." and tells you to change it in Windows
Sound settings.

Which did you get? This matters more than it looks — the check itself is happy
at 44.1 kHz, but the radio audio link cannot use it, because Opus has no 44.1
kHz mode. Before this morning a 44.1 kHz headset would have transmitted
gap-riddled audio and told you nothing. Now it either works properly or says
so.

**** 

### Test 6 — Silence reads as silence

Stop talking. Mute the headset at the boom if it has a mute, or just hold
still and quiet for about ten seconds.

The reading should say it is hearing nothing, or nothing above the noise
floor. It should not report a level it cannot really be hearing.

**** 

### Test 7 — Windows microphone privacy

Only if you want to prove this one: open Windows Settings, Privacy and
security, Microphone, and turn off "Let desktop apps access your microphone."
Then start the check again.

It should tell you the privacy switch is the reason, in plain words, and put
focus on the button that opens the microphone privacy page — not report an
"unanticipated host error" and leave you guessing.

Turn the switch back on afterwards.

**** 

---

## Section C — With a radio, over SmartLink

Do these against the bench 8600. It has no antenna on any port, so keying is
safe and nothing radiates.

### Test 8 — Connect with the headset chosen

Connect to the 8600 over SmartLink with the USB headset selected as input.
Turn PC Audio on.

Nothing should be said about your microphone at all. Silence here is the pass:
the new "Your microphone could not be opened..." announcement only fires when
the mic stream genuinely fails to open, and a working 48 kHz headset should
never trigger it.

If you *do* hear it, that is a real finding and I want the trace.

**** 

### Test 9 — Speak into the transmit monitor

Still connected, PC Audio on. Turn the TX Monitor on in the Workshop, key up,
and talk.

You should hear yourself. Listen specifically for **regular, periodic gaps** —
a stutter at a steady rhythm rather than random dropouts. That rhythm was the
signature of the bug fixed this morning; if you hear it now, the fix did not
cover the case you are in and I need to know immediately.

**** 

### Test 10 — The two numbers

While transmitting and talking, press Alt+Shift+S.

You should get a verdict and up to two figures: a peak in dBFS, and a loudness
in LUFS. Both, or just the peak if the loudness figure is not available yet.

Read me exactly what it said.

**** 

### Test 11 — The noise floor observation

Transmit and talk normally with something steady running in the room — a fan,
the shack air handler, whatever you have.

If the room is close behind your voice, the reading should add a sentence
about steady background noise, and it should NOT suggest turning the gain up.
If your room is quiet, you should hear nothing extra — that is also correct
and is the more common outcome.

Which did you get, and was the wording right?

**** 

---

## Section D — Presets, no radio needed

### Test 12 — Save a preset and get an honest receipt

In the Workshop, adjust something in the Microphone or Processing group, then
use Save Preset in the preset toolbar and give it a name.

If it says it saved, it saved. That sounds like a low bar; until yesterday the
dialog said "saved" whether or not anything had been written, and there was
nothing behind it at all.

Then close the Workshop, reopen it, and load the preset back. Did the setting
come back?

**** 

### Test 13 — Export and Delete

Export the preset to a file, then delete it from the list, then check the
exported file is still on disk.

Both operations should tell you what actually happened.

**** 

---

## Anything else

Anything that felt wrong, awkward, out of order, or over-talkative that I did
not ask about — put it here. Especially anything the screen reader said that
did not match what was on screen.

**** 
