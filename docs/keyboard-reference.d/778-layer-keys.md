<!-- Fragment for docs/help/md/keyboard-reference.md. Junkbox #778.

Replaces the "Any other key..." bullets in the audio layer (today's lines 165 and 166) and
the filter layer (today's line 197). The noise layer's section has no such bullet; the same
rule applies there and may deserve one. Same wording for all three; the layer's name differs
in the spoken sentence. Line 166 today quotes "B is not a key in the Audio layer. Ctrl+B: ..."
as the near-miss sentence; the shipped sentence has no such prefix and the bullet below says
what is actually spoken. -->

- Any other key keeps your changes, announces the layer closed, and then does its normal job — a hotkey like `Ctrl+F`, push to talk, the transmit lock, the bracket keys, Tab. The layer only holds on to two kinds of key, below.
- A letter the layer doesn't use stays in the layer. You get the refusal tone, and at chatty verbosity the layer's own answer: the list of letters above if you haven't picked anything yet, or "Not a key in the Audio layer. H lists its keys." once you have. If the letter sits one modifier away from a key that *does* mean something in here — plain `B` when you meant `Ctrl+B` — it names the key you nearly pressed instead: "Ctrl+B: Binaural receive on or off." Until now a letter the layer didn't use closed the layer and went on to whatever had focus, so the refusal you heard came from Home, not from the layer, and the layer was already gone.
- The digit keys `1` to `9`, and the keypad digit keys, are refused inside a layer rather than let out: "A typed number does nothing in the Audio layer. Use the arrows to move the value." On the frequency field a loose digit starts typing a frequency, so a value you meant for the layer could become a retune. `0` is still the centre jump, and with NumLock off the keypad's arrows, Home and End still do their layer jobs.
