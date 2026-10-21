<!-- Fragment for docs/help/md/keyboard-reference.md. Junkbox #778, both halves, and #778c with its repair rounds.

Replaces the "Any other key that means something elsewhere..." bullet and the near-miss bullet
in the audio layer (today's lines 165 and 166) and the same bullet in the filter layer (today's
line 197). The noise layer's section has no such bullet; the same rule applies there and may
deserve one. Same wording for all three; the layer's name differs in the spoken sentences.

Line 166 today quotes "B is not a key in the Audio layer. Ctrl+B: ..." as the near-miss
sentence; the shipped sentence has no such prefix (#695) and the bullet below says what is
actually spoken.

Rulings this fragment rests on, all Noel, 2026-10-21: 06:29 Alt and F1 keep working in a layer;
06:30 Ctrl+Space and Shift+Space in a dialog go to transmit, never to a button; 06:32 Tab is
refused in a layer on Home too, and 07:41 Ctrl+Tab and Ctrl+Shift+Tab with it; 06:34 "if the
layer's open, the layer is open" (a layer over a text box refuses Shift+Space); 06:42 in a text
box Shift+Space types a space and Ctrl+Space still keys; 07:37 Escape in a dialog unkeys first,
then a second Escape closes; 07:38 while an Audio Check is running the dialog keys are refused
with the tone; 10:55 the dialog lock waits for the bench, so Shift+Space in a dialog is refused
for now and Home's lock is what it was before #778c. Brackets inside a layer were ruled at 06:37
and 07:40 to be the next wave's own track; this fragment says only what brackets do today.

Routes, so the prose can be checked: the dialog door is DialogTransmitKeys, in the Window class
preview handler, ahead of the layer and of every control, feeding the same MainWindow edges as
Home's step 3 and the system-wide chord for push to talk. It cannot see a native message box
(MessageBox.Show is not a WPF window), the WinForms full log form, or WinForms-hosted surfaces.
The system-wide chord's defaults are Ctrl+Alt+Space and Alt+Shift+Space, so plain Ctrl+Space in
a dialog is the door's even with the hook on. Shift+Space on a non-text control in a dialog is
consumed by the door and refused through the same path as its Audio Check refusal (the low tone,
"No transmit lock here." at Chatty); it toggles nothing, and its repeats are consumed quietly.
The lock itself is Home's step 3 (one toggle per key-down event, exactly as before #778c, so a
held key repeats as it always did) and the system-wide chord (one toggle per press). The dialog
lock returns as its own track once a quick JAWS tap has been measured; SpaceKeyTimingTrace
writes one line per Space key event under a detailed capture for that bench session.

"On Home" below means the main window. Push to talk there also needs focus in the Home fields
or field groups and the radio powered on, as it always has. The low refusal tone is the ordinary
refusal earcon, in the Commands and confirmations category; it follows the sound switches, not
the speech verbosity, so it plays at Off. When it cannot play, the sentence is spoken at every
verbosity, Off included (ValueSubLayer.Refuse, #778b round four; the dialog door uses the same
rule, RefusalVoice.ToneStandsAlone). -->

<!-- DRAFT (#778c) -->
- **Enter, Escape and the JJ key are the only ways out.** Any other key the layer doesn't use is refused and the layer stays where it was: a letter it doesn't use, the space bar, `Tab`, `Shift+Tab`, `Ctrl+Tab` and `Ctrl+Shift+Tab` (so your place on Home doesn't move while a layer is open), and real hotkeys alike, like `Ctrl+F` or a band key. You get the refusal tone, and at chatty verbosity the layer's own answer: the list of letters above if you haven't picked anything yet, or "Not a key in the Audio layer. Press H to list available keys for this layer." once you have. If you've switched the sounds off, or just the Commands and confirmations sounds, there is no tone to hear, so the answer is spoken at every verbosity, Off included: a refused key is never silent. The menus on `Alt` and `F1` help work exactly as they always did.
<!-- DRAFT (#778c) -->
- **Push to talk works in the app's own dialogs, and leaves a layer open.** `Ctrl+Space` keys the radio while you hold it, on Home (with the radio on and you in the Home fields, as always) and in every dialog the app draws itself, Settings and the Audio Workshop included, a layer open or not. It never presses the button or checkbox you are on. The release is always honoured: let go of `Ctrl+Space` and the radio unkeys (under JAWS it may take up to about a second, while the app makes sure you really let go); close the dialog or switch to another window while still holding it, and the radio unkeys at once. `Shift+Space`, the transmit lock, is not taken from a dialog yet: for now it is refused there, with the low tone and, at chatty, "No transmit lock here.", and it never presses the button you are on either. Lock and unlock from Home with `Shift+Space`, which works exactly as it did before, or from anywhere with the system-wide key. `Escape` in a dialog while the radio is keyed or locked from one of these keys unkeys first and leaves the dialog open; a second `Escape` closes it, the same as on Home, and the next `Ctrl+Space` keys again as normal. Four places are different, and the help is honest about each:
  - In a text box, `Shift+Space` types a space, so a capital letter followed quickly by a space can never lock the transmitter. `Ctrl+Space` in a text box still keys. With a layer open over that text box, the layer wins and refuses `Shift+Space` like any other key it doesn't use.
  - While an Audio Check is running in the Audio Workshop, it owns the transmitter: `Ctrl+Space` is refused with the low tone and, at chatty, "No push to talk here." `Escape` in the Audio Workshop stops the check first, as it always has. `Escape` in any other dialog while the check is running closes that dialog and leaves the check transmitting; stop the check from the Audio Workshop.
  - The small Yes, No and OK message boxes are Windows' own, not the app's, so these keys do not reach the radio from them.
  - The full log form has never had push to talk, and a layer open there refuses both chords with the low tone and, at chatty, the same two sentences.
  With the radio off or no radio connected, `Ctrl+Space` in a dialog answers "No radio connected" (at chatty, "JJ Flexible Home, no radio connected") and nothing keys.
<!-- DRAFT (#778c) -->
- **The bracket filter keys inside a layer, today.** On Home with the radio on, `[` and `]`, with or without `Shift` or `Ctrl`, still move the filter while a layer is open. `Ctrl+Shift` with a bracket (the transmit filter's low edge) and the verbosity cycle (`Ctrl+Shift+V`) work inside a layer on Home and in dialogs like the Audio Workshop, but not in the full log form, which has never had them. `Alt` with a bracket cycles filter presets on Home only, as it always has, and a layer lets it through there like every other `Alt` chord; in a dialog it does nothing, as before. `Ctrl+Alt` with a bracket (the transmit filter's high edge) works wherever `Ctrl+Shift` with a bracket does. In a dialog or the full log form, a plain, `Shift` or `Ctrl` bracket is refused inside a layer with the low tone and, at chatty, "That filter key does nothing here."
- **A slip is named.** A key that means nothing anywhere, and sits one modifier away from a key that *does* mean something in here, names the key you nearly pressed: "Ctrl+B: Binaural receive on or off."
- **A typed number does nothing yet.** The digit keys `1` to `9`, and the keypad digit keys, are refused: "A typed number does nothing in the Audio layer. Use arrow keys to change the selected number." Where holding Shift with the arrows gives a finer step, it adds "Shift with the arrows moves it in fine steps." On the frequency field a loose digit starts typing a frequency, so a value you meant for the layer could have become a retune. `0` is still the centre jump, and with NumLock off the keypad's arrows, Home and End still do their layer jobs.
