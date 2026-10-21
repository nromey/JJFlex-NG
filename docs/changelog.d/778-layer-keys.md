<!-- Fragment for docs/CHANGELOG.md, Unreleased section. Junkbox #778 (both halves) and #778c with its repair round. Plain, per Noel's 2026-10-21 rule: what the operator now gets, no mechanism, no back-story. -->

<!-- DRAFT (#778c) -->
- **A value layer stays open until you leave it.** Inside the audio, filter or noise layer, only Enter, Escape and the JJ key get you out; every other key, Tab included, is refused and you stay where you were.
<!-- DRAFT (#778c) -->
- **Push to talk and the transmit lock work from the app's own dialogs.** Ctrl+Space keys and Shift+Space locks from Settings and the other dialogs instead of pressing the button you were on. While the radio is transmitting from one of these keys, Escape unkeys it before anything else; a second Escape closes the dialog.
<!-- DRAFT (#778c) -->
- **A held Shift+Space locks once.** Holding the key no longer flips the lock on and off, on Home or in a dialog, with NVDA or JAWS.
<!-- DRAFT (#778c) -->
- **Shift+Space in a text box never locks the transmitter.** Ctrl+Space there still keys.
- **Typing a number inside a value layer can't retune the radio.** Digits are refused inside the layer instead of landing on the frequency field.
- **A refused key is never silent.** With the sounds off, the layer's short answer is spoken at every verbosity, Off included.
