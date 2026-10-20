<!-- Fragment for docs/help/md/keyboard-reference.md. Junkbox #778, both halves.

Replaces the "Any other key that means something elsewhere..." bullet and the near-miss bullet
in the audio layer (today's lines 165 and 166) and the same bullet in the filter layer (today's
line 197). The noise layer's section has no such bullet; the same rule applies there and may
deserve one. Same wording for all three; the layer's name differs in the spoken sentences.

Line 166 today quotes "B is not a key in the Audio layer. Ctrl+B: ..." as the near-miss
sentence; the shipped sentence has no such prefix (#695) and the bullet below says what is
actually spoken.

Tab is deliberately NOT named below. Inside a dialog a live layer refuses Tab like any other
key, but on the main window Tab never reaches the layer (MainWindow_PreviewKeyDown returns
before asking it), so Tab moves focus and the layer stays open. That is the subject of
ASK-778b-main-window-tab.md; name Tab here once it is ruled.

"On Home" below means the main window. Push to talk there also needs focus in the Home fields
or field groups and the radio powered on, as it always has; that gate is the main window's and
did not change. The low refusal tone is the ordinary refusal earcon; it follows the sound
switches, not the speech verbosity, so it plays at Off, and if the sounds are switched off the
words are spoken at every level instead (the usual refusal rule). -->

- **Enter, Escape and the JJ key are the only ways out.** Any other key the layer doesn't use is refused and the layer stays where it was: a letter it doesn't use, the space bar, and real hotkeys alike, like `Ctrl+F` or a band key. You get the refusal tone, and at chatty verbosity the layer's own answer: the list of letters above if you haven't picked anything yet, or "Not a key in the Audio layer. Press H to list available keys for this layer." once you have. Until now any key that meant something elsewhere closed the layer and then did its job, and the "layer closed" was usually talked over by that job, so you couldn't tell.
- **A few keys do their job and leave the layer open.** On Home, with the radio on and you in the Home fields: push to talk (`Ctrl+Space`), the transmit lock (`Shift+Space`) and the bracket filter keys. Wherever those three have no job to do (another window such as the Audio Workshop, the full log form, or Home with focus elsewhere or the radio off) a layer refuses them: the low refusal tone, at every verbosity, and at chatty only a very short sentence, "No push to talk here.", "No transmit lock here." or "That filter key does nothing here." `Ctrl+Shift` with a bracket (the transmit filter's low edge) and the verbosity cycle (`Ctrl+Shift+V`) work inside a layer on Home and in windows like the Audio Workshop; the full log form has neither, so there they are refused too. The menus on `Alt` and `F1` help work exactly as they always did. A layer that uses one of these keys for something of its own gets it first.
- **A slip is named.** A key that means nothing anywhere, and sits one modifier away from a key that *does* mean something in here, names the key you nearly pressed: "Ctrl+B: Binaural receive on or off."
- **A typed number does nothing yet.** The digit keys `1` to `9`, and the keypad digit keys, are refused: "A typed number does nothing in the Audio layer. Use arrow keys to change the selected number." Where holding Shift with the arrows gives a finer step, it adds "Shift with the arrows moves it in fine steps." On the frequency field a loose digit starts typing a frequency, so a value you meant for the layer could have become a retune. `0` is still the centre jump, and with NumLock off the keypad's arrows, Home and End still do their layer jobs.
