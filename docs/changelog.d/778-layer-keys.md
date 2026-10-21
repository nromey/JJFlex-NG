<!-- Fragment for docs/CHANGELOG.md, Unreleased section. Junkbox #778 (both halves) and #778c with its repair round. Plain, per Noel's 2026-10-21 rule: what the operator now gets, no mechanism, no back-story. -->

<!-- DRAFT (#778c) -->
- **A value layer stays open until you leave it.** Inside the audio, filter or noise layer, only Enter, Escape and the JJ key get you out; every other key, Tab included, is refused and you stay where you were.
<!-- DRAFT (#778c) -->
- **Push to talk and the transmit lock work from the app's dialogs.** Ctrl+Space keys and Shift+Space locks from Settings, the Audio Workshop and the rest, instead of pressing whatever button you were on. Escape unkeys first and closes the dialog on the second press.
<!-- DRAFT (#778c) -->
- **A held Shift+Space locks once.** Holding the key no longer flips the lock on and off, on Home or in a dialog, with NVDA or JAWS.
<!-- DRAFT (#778c) -->
- **Text boxes keep their space.** Shift+Space in a text box types a space as before; Ctrl+Space there still keys.
- **Typing a number inside a value layer can't retune the radio.** Digits are refused inside the layer instead of landing on the frequency field.
- **A refused key is never silent.** With the sounds off, the layer's short answer is spoken at every verbosity, Off included.
