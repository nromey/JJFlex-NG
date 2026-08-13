# Why your audio has two numbers

**Draft — Claude's wording, awaiting Noel's human pass.**

If you ask JJ Flexible Radio Access how your audio is doing, it can answer in plain English ("just right", "coming in hot", "turn it up"), or it can hand you the actual figures. Ask for the figures and you get two of them: **dBFS** and **LUFS**. They are not two ways of saying the same thing, and neither one is the "real" number. They answer different questions, and they go wrong in different directions.

Here is the short version, in terms you already own.

## dBFS is peak. LUFS is closer to average.

You already live with this distinction every time you look at a wattmeter. **PEP** tells you what your loudest instant is doing. **Average power** tells you how much signal is really getting out there over time. A voice peak can slam the needle while the average sits far lower — that is just what speech looks like.

**dBFS is the PEP end of that.** It measures how close your loudest instant came to the ceiling — digital full scale, the point where there is simply no room left and the waveform gets its top sliced off. Zero is the ceiling, and everything below it is negative, so **−10 dBFS is louder than −30 dBFS.** What dBFS tells you is *headroom*: how much space you have left before things break.

**LUFS is the average end**, but a smarter average. It is weighted to match how human hearing actually works — the frequencies your ear is most sensitive to count for more — and it politely ignores the silence between your words instead of letting your pauses drag the number down. What LUFS tells you is *how loud you actually sound to the person copying you*.

## Why we show both

Because you can be wrong in two different ways, and only one number catches each.

**You can have healthy peaks and still be too quiet.** If your speech is peaky — big transients, lots of gaps — dBFS looks fine while your average is way down and you sound small on the air.

**You can sound comfortably loud and still be clipping.** Your LUFS reads nicely in the pocket while the odd consonant slams into the ceiling and gets chopped. That is the kind of thing that makes people say you sound "harsh" without being able to say why.

This is why the coaching uses LUFS for the friendly verdict but keeps the radio's own ALC as a hard guardrail. **LUFS says "you sound right." ALC says "you are not overdriving the transmitter."** Two different ways to be wrong, so two different instruments.

## Why the numbers sometimes disagree

Two reasons, both normal.

**They are measured in different places.** LUFS is measured here on your computer, on the raw microphone audio before anything is compressed and sent. The dBFS reading from the radio's microphone meter is measured at the other end, after the trip. They will not agree to the decimal, and they are not supposed to. When we injected a test tone at exactly −10 dBFS, the radio reported −11. One decibel across the whole chain is a very honest result, not an error.

**LUFS is frequency-weighted, so pitch matters.** The weighting is referenced around 1 kHz. Feed in the 440 Hz test tone — a low A — and LUFS reads a little under the dBFS figure, because your ear genuinely finds that pitch slightly less loud. Nothing is wrong. Change the tone frequency and the gap changes with it.

## The one place this catches people out

LUFS ignores the gaps between your words. That is normally exactly what you want — your pauses should not count against how loud you sound.

**But a noisy room does not produce gaps.** A fan, an air conditioner, traffic through a window: that noise is running continuously, so there is nothing for the measurement to skip. It becomes part of your level. In a genuinely noisy shack the number can tell you that you are sitting pretty while your voice is actually buried behind your own room.

The number is not lying to you — it is honestly reporting a signal that happens to include your room. But it is worth knowing, because the fix is not turning yourself up. Turning up a noisy signal just gives everyone more noise.

So JJ Flexible watches the quiet stretches too, not only the loud ones. When your room is loud enough to matter and your voice is not standing far enough clear of it, your mic reading picks up one extra line:

"Steady background noise, about 14 dB under your voice. Turning up would raise the room too."

Two things about that line. It is an observation, not a verdict — it sits **alongside** your level, never in place of it. Your level can be perfectly good and your room can still be loud; those are two separate facts and you get told both. And it says nothing about turning up on purpose, because that is the one move that does not help.

The rest of the time you will never hear it. A quiet shack does not get told about its noise floor, and a transmission too short to judge gets no guess — the only thing worse than a meter that misses a problem is one that invents one every time you key up.
