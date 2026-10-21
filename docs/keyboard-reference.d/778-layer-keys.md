<!-- Fragment for docs/help/md/keyboard-reference.md. Junkbox #778, both halves, and #778c.

Replaces the "Any other key that means something elsewhere..." bullet and the near-miss bullet
in the audio layer (today's lines 165 and 166) and the same bullet in the filter layer (today's
line 197). The noise layer's section has no such bullet; the same rule applies there and may
deserve one. Same wording for all three; the layer's name differs in the spoken sentences.

Line 166 today quotes "B is not a key in the Audio layer. Ctrl+B: ..." as the near-miss
sentence; the shipped sentence has no such prefix (#695) and the bullet below says what is
actually spoken.

Tab is named below as of #778c. Noel ruled it on 2026-10-21 at 06:32 ("Once in a layer, tab gets
refused"): on Home, as in a dialog, a live layer refuses Tab and Shift+Tab, and Ctrl+Tab and
Ctrl+Shift+Tab with them (the track's reading of the ruling, flagged in its report). Before that
round MainWindow_PreviewKeyDown returned on Tab before asking the layer, so Home's focus moved
behind an open layer; ASK-778b-main-window-tab.md is answered.

Push to talk and the transmit lock from a dialog, ruled 06:30 ("ctrl+space and shift+space
should never be used to activate a button in a dialog"): in every WPF window Ctrl+Space keys and
Shift+Space locks, a layer open or not, through the same push-to-talk pipeline Home uses
(DialogTransmitKeys, the third door beside Home and the system-wide chord). Shift+Space in an
editable text box still types a space (ruled 06:42). The WinForms full log form is not a WPF
window and is unchanged. The brackets inside a layer were ruled the other way at 06:37 (they
belong to the layer; only the filter layer moves filters), but the filter layer has no bracket
keys of its own yet, so that change is not built in this round and the bracket bullet says so;
see ASK-778c-filter-layer-brackets.md.

"On Home" below means the main window. Push to talk there also needs focus in the Home fields
or field groups and the radio powered on, as it always has; that gate is the main window's and
did not change. The low refusal tone is the ordinary refusal earcon, in the Commands and
confirmations category; it follows the sound switches, not the speech verbosity, so it plays at
Off. When it cannot play (all sounds off, or that category off) the layer's sentence is spoken at
every verbosity instead, Off included, for every refusal in a layer, not only these three
(ValueSubLayer.Refuse, #778b round four; Noel, 2026-10-19 21:18: "yes, we'd need to let the user
know somehow"). Until round four that fallback was dropped at Off, so this comment was false;
ValueLayerRefusalDeliveryTests now holds it. -->

<!-- DRAFT (#778c) -->
- **Enter, Escape and the JJ key are the only ways out.** Any other key the layer doesn't use is refused and the layer stays where it was: a letter it doesn't use, the space bar, `Tab` and `Shift+Tab` (and `Ctrl+Tab`, so your place on Home doesn't move while a layer is open), and real hotkeys alike, like `Ctrl+F` or a band key. You get the refusal tone, and at chatty verbosity the layer's own answer: the list of letters above if you haven't picked anything yet, or "Not a key in the Audio layer. Press H to list available keys for this layer." once you have. If you've switched the sounds off, or just the Commands and confirmations sounds, there is no tone to hear, so the answer is spoken at every verbosity, Off included: a refused key is never silent. Until now any key that meant something elsewhere closed the layer and then did its job, and the "layer closed" was usually talked over by that job, so you couldn't tell.
<!-- DRAFT (#778c) -->
- **Push to talk and the transmit lock work in every window, and leave the layer open.** `Ctrl+Space` keys the radio while you hold it and `Shift+Space` locks and unlocks the transmitter, on Home (with the radio on and you in the Home fields, as always) and in every dialog, the Audio Workshop and Settings included, a layer open or not. Until now, in a dialog those two chords pressed whichever button or checkbox had focus. One exception, on purpose: in a text box `Shift+Space` still types a space, so a capital letter followed quickly by a space can never lock the transmitter; `Ctrl+Space` in a text box still keys, because it never typed anything. The release is always honoured: if the dialog closes or loses focus while you are holding `Ctrl+Space`, the radio unkeys. With the radio off, or no radio connected, a dialog answers the way the system-wide key does, "No radio connected" (at chatty, "JJ Flexible Home, no radio connected"). The full log form is different: it has never had push to talk, and a layer open there refuses both with the low refusal tone, and at chatty only a very short sentence (at every verbosity if your sounds are off), "No push to talk here." or "No transmit lock here." `Ctrl+Shift` with a bracket (the transmit filter's low edge) and the verbosity cycle (`Ctrl+Shift+V`) work inside a layer on Home and in windows like the Audio Workshop; the full log form has neither, so there they are refused too. The menus on `Alt` and `F1` help work exactly as they always did.
<!-- DRAFT (#778c) -->
- **The bracket filter keys inside a layer, for now.** On Home, with the radio on, the brackets still move the filter while a layer is open, and in any other window a layer refuses them with the low tone and, at chatty, "That filter key does nothing here." This is changing: inside a layer the brackets are going to belong to the layer, with only the filter layer moving filters with them, so a layer that has nothing to do with filters will refuse them everywhere. That lands once the filter layer has bracket keys of its own.
- **A slip is named.** A key that means nothing anywhere, and sits one modifier away from a key that *does* mean something in here, names the key you nearly pressed: "Ctrl+B: Binaural receive on or off."
- **A typed number does nothing yet.** The digit keys `1` to `9`, and the keypad digit keys, are refused: "A typed number does nothing in the Audio layer. Use arrow keys to change the selected number." Where holding Shift with the arrows gives a finer step, it adds "Shift with the arrows moves it in fine steps." On the frequency field a loose digit starts typing a frequency, so a value you meant for the layer could have become a retune. `0` is still the centre jump, and with NumLock off the keypad's arrows, Home and End still do their layer jobs.
