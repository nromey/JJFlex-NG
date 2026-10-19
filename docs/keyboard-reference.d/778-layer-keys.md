<!-- Fragment for docs/help/md/keyboard-reference.md. Junkbox #778.

Replaces the "Any other key..." bullets in the audio layer (today's lines 165 and 166) and
the filter layer (today's line 197). The noise layer's section has no such bullet; the same
rule applies there and may deserve one. Same wording for all three; the layer's name differs
in the spoken sentence. -->

- Any other key that is a command somewhere else — `Ctrl+F` to type a frequency, a bound function key — keeps your changes, announces the layer closed, and then does its normal job. That is the only way a key other than `Enter`, `Escape` or `Ctrl+J` leaves the layer.
- A key that means nothing anywhere stays in the layer. You get the refusal tone, and at chatty verbosity the layer's own answer: the list of letters above if you haven't picked anything yet, or "Not a key in the Audio layer. H lists its keys." once you have. If the key sits one modifier away from a key that *does* mean something in here — plain `B` when you meant `Ctrl+B` — it names the key you nearly pressed instead. Until now such a key closed the layer and went on to whatever had focus, so the refusal you heard came from Home, not from the layer, and the layer was already gone.
- The digits `1` to `9`, and the whole numeric keypad, are refused inside a layer rather than let out: "A typed number does nothing in the Audio layer. Use the arrows to move the value." On the frequency field a loose digit starts typing a frequency, so a value you meant for the layer could become a retune. `0` is still the centre jump.
- Tab, Space, Page Up and Page Down stay in the layer too while it is open; close it with `Enter` or `Escape` first.
