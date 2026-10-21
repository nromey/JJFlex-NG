<!-- Fragment for docs/CHANGELOG.md, Unreleased section. Junkbox #778 (both halves) and #778c with its repair rounds. Plain, per Noel's 2026-10-21 rule: what the operator now gets, no mechanism, no back-story, nothing that needs an exception to be true. -->

<!-- DRAFT (#778c) -->
- **A value layer stays open until you leave it.** Inside the audio, filter or noise layer, only Enter, Escape and the JJ key get you out; every other key, Tab included, is refused and you stay where you were.
<!-- DRAFT (#778c) -->
- **Push to talk works from the app's own dialogs.** Ctrl+Space keys the radio from Settings and the other dialogs instead of pressing the button you were on. While an Audio Check is running, the check keeps the transmitter and Ctrl+Space is refused until it has finished.
<!-- DRAFT (#778c) -->
- **Shift+Space in a dialog never presses the button you are on.** It doesn't lock the transmitter from a dialog yet either; for now you hear the refusal tone. Lock and unlock from Home or with the system-wide key, as before.
<!-- DRAFT (#778c) -->
- **Escape unkeys before it closes.** While the radio is keyed or locked from the keyboard, Escape in a dialog unkeys it first; a second Escape closes the dialog.
- **Typing a number inside a value layer can't retune the radio.** Digits are refused inside the layer instead of landing on the frequency field.
- **A refused key is never silent.** With the sounds off, the layer's short answer is spoken at every verbosity, Off included.
