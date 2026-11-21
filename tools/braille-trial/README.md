# Braille trial

Register task #347. A small standalone program, a trial-only NVDA app module and a trial-only JAWS script that prove, on a real braille display and before anything goes into JJ Flexible, the risky parts of the braille Home design: that the program can put its Home line on the display, that a routing key over a cell lands on the right field and character in the program, that live updates keep the reader's place, and that a line longer than the display pans and still routes right.

It is not JJ Flexible and needs no radio. It references no JJ Flexible project and is not in `JJFlexRadio.sln`. Everything here is throwaway: the shipped add-on and script will be built from what the trial proves.

## What is here

- `app/`: the trial program, `jjbrailletrial.exe` (C#, WPF, .NET 10, x64). `app/Model/` holds the window-free view model.
- `tests/`: xUnit tests of the view model. They construct no window.
- `nvda/appModules/jjbrailletrial/`: the NVDA app module. `layout.py` in it is the pure logic.
- `nvda/verify/`: three offline checks of the app module, each with its own positive control.
- `jaws/`: the JAWS script, key map and braille structure file. A draft that has never been compiled.
- `install-nvda-trial.ps1`: puts the app module in NVDA's developer scratchpad, or takes it out.
- `compile-jaws-trial.ps1`: copies the JAWS files into the JAWS user settings folder and compiles them there, or takes them out.

## Build

From the repository root, with build servers off:

    dotnet build tools/braille-trial/app/BrailleTrial.csproj -c Debug -p:Platform=x64 --disable-build-servers -nodeReuse:false

The program lands at `tools/braille-trial/app/bin/x64/Debug/net10.0-windows/jjbrailletrial.exe`.

Tests, by name only:

    dotnet test tools/braille-trial/tests/BrailleTrial.Tests.csproj -c Debug -p:Platform=x64 --disable-build-servers -nodeReuse:false

Offline checks of the NVDA side, no screen reader needed:

    python -I tools/braille-trial/nvda/verify/verify_trial_layout.py
    python -I tools/braille-trial/nvda/verify/verify_trial_symbols.py
    python -I -B tools/braille-trial/nvda/verify/verify_trial_appmodule.py

Static check of the JAWS script (names, argument counts, block balance; it does not compile):

    python -I tools/readercapture/verify/verify_jaws_script.py tools/braille-trial/jaws/jjbrailletrial.jss

That check reports two calls to `CreateUIARawViewTreeWalker` as unknown. They are not: the function is declared in JAWS's `UIA.jsd`, which the script imports and the checker does not read.

## The program

One window, "JJ Flexible braille trial". Focus starts on the trial Home, a read-only line built the way JJ Flexible's Home is today: its screen reader peer is deliberately silent, so readers get nothing from it directly. The line reads

    14.225.000 USB S7 VOX off Tuning modern

Five fields: the frequency, the mode, the S meter, a VOX toggle and a tuning-mode toggle. Each field has a fixed slot on screen, so a value changing never moves another field.

Keys on the trial Home:

- Left and Right move the caret one character. The program says where it is: entering the frequency it says the digit and its place ("frequency, digit 6, 100 hertz"); entering another field it says the field and its value.
- Up and Down tune. In modern tuning they step 1 kHz wherever the caret is. In classic tuning they step by the place value of the frequency digit under the caret (on the 100 Hz digit, Up adds 100 Hz), and refuse, saying so, when the caret is not on a digit. The program says the new frequency.
- Space flips VOX or the tuning mode when the caret is on one.
- F5 starts and stops a walk: the frequency steps 100 Hz five times a second, up for twenty steps then down for twenty, and the meter changes each step. F6 takes one step.
- F7 switches between the Home view (every field) and the field view (only the caret's field).
- Tab reaches the trace list. Alt+F4 closes the program.

What the program says is trial wording, sent as a UI Automation notification, which both readers voice.

Beside the trial Home is the carrier: an invisible element with automation id `JJFlexBrailleView` whose name is the view string, out of both the control and the content view so no reader lands on it. It changes whenever the line or the caret changes and raises the name-changed event when it does.

### The view string

Plain text, lines separated by a line feed, parts separated by a tab (design section 4.3). For example:

    JJFV1  1  1  home  1  0  0
    1  V  14.225.000
    2  C  USB
    3  R  S7
    4  T  VOX off
    5  T  Tuning modern

(Tabs are shown as spaces here.) The header is the format tag, the revision (up whenever anything in the string changes), the layout number (changes only if the field list changes; always 1 in the trial), the view (home, field or off), the ordinal of the field the caret is on, and whether clickable cells are to be marked (off). Each field line is its ordinal, its kind (T toggle, V value, C choice, R readout, A action) and its text.

**The trial adds a seventh header part the design does not have: the caret's character within its field, counted from 0.** The brief asks the reader to keep its cursor at the program's caret, and with six parts the reader can only put it at the start of the caret's field, never on a digit. A reader that knows six parts ignores the seventh. Whether the real build should carry it is a question for the design, not settled here.

### The bridge

Three window messages, registered by name with `RegisterWindowMessage` and posted to the program's window, never sent (design section 4.4). Two numbers each, packed with multiplication so the JAWS script language can build them too.

- `JJFlexBraille_Hello`: wParam is reader times 65536 plus protocol version (reader 1 is NVDA, 2 is JAWS; version 1). lParam is display rows times 65536 plus cells per row.
- `JJFlexBraille_Route`: wParam is the layout number the reader used. lParam is the field ordinal times 65536 plus a character code: 0 means "not known", otherwise the character within the field plus one.
- `JJFlexBraille_Command`: wParam is the command (1 switches view). lParam is the reader.

A route on the frequency puts the caret on that digit (a dot lands on the digit after it; "not known" lands on the first digit) and says which digit. On the mode or the meter it moves the caret there and says it. On VOX or the tuning toggle it flips the toggle and says the new state; the caret stays where it was. A route is dropped, and the trace says why, when the trial Home does not have the keyboard focus, when the program is not the foreground window, when the layout number is not the current one, or when the field number names no field. A character past the end of its field is treated as "not known", and the trace says that too.

### The trace

Every line goes to `jjbrailletrial-trace.txt` beside the executable (appended, so earlier runs stay) and to the list in the window. Each starts with the date and time to the millisecond, then a kind:

- `session`: the program started or closed, and where the trace file is.
- `bridge`: the window handle the messages must be posted to, the three registered message numbers, and whether the message filter allowed them.
- `hello`: which reader, its protocol version, the display's rows and cells, and the raw numbers.
- `route`: the raw numbers and what they decoded to (layout, field, character); then either "dropped:" with the reason, or the field and character it landed on, what was done, and what was said.
- `command`: a view switch, from which reader or from F7, and the view now.
- `carrier`: a new view string was published: its revision, why (start, route, tune, caret, walk and so on), its length, whether the name-changed event was raised (it is raised only when some UI Automation client is listening for property changes, and the line says when none was), and the string itself, with tabs as `\t` and line feeds as `\n`.
- `caret`: the caret moved, and whether the keyboard or the program moved it, with the field, the character and the screen position.
- `tune`: an Up, Down, walk or F6 step: the mode, the step size, and the frequency before and after, or why it was refused.
- `walk`: the walk started or stopped.
- `key`: Space, and what it did.

Nothing else is recorded: no speech, no keys other than these.

## NVDA

The app module recognises the trial Home by its automation id and gives NVDA one braille region for it. It reads the carrier on a background thread with its own UI Automation client (so NVDA's main thread never waits on the program), lays every field out on one line with two blank cells between fields, keeps each field at the widest it has been so nothing after a toggle moves, and keeps NVDA's cursor on the program's caret. NVDA pans the line itself. A routing press is turned into a field and a character and posted to the program. Hello goes out each time the trial Home gains focus. If anything goes wrong, or the view string is not one it understands, NVDA shows its own line instead. Its lines in NVDA's log start with `jjbrailletrial:`.

It also listens for the carrier's name-changed event. The design expects that event never to reach NVDA (NVDA listens for property changes on the focus and its ancestors only, and the carrier is neither), so the background read is what keeps the line current. The log counts carrier updates found by the background read and those that followed an event, so the first session shows which.

### Install

    powershell -File tools\braille-trial\install-nvda-trial.ps1

It copies `nvda\appModules\jjbrailletrial` to `%AppData%\nvda\scratchpad\appModules\jjbrailletrial`, reports whether the developer scratchpad is switched on, and tells you to press NVDA+Control+F3 to reload plugins. It never edits NVDA's settings, never restarts NVDA and never reloads anything itself. If the scratchpad is off, it says how to turn it on: NVDA menu, Preferences, Settings, Advanced; tick the box that says you understand; tick "Enable loading custom code from Developer Scratchpad Directory"; OK; restart NVDA.

The app module loads only while `jjbrailletrial.exe` runs.

A command to switch view from the display is in Input Gestures under "JJ Flexible braille trial", unbound; bind it to a display key if you want one. F7 in the program does the same thing.

### Remove

    powershell -File tools\braille-trial\install-nvda-trial.ps1 -Remove

Then NVDA+Control+F3, or restart NVDA.

## JAWS

A draft, built after NVDA and never compiled. It claims the trial Home as a custom control (the `.jbs` gives it one component, its value) and paints each field as its own segment with a routing callback, held at its widest. If JAWS never asks the script what kind of control the trial Home is (it asks only for objects it cannot identify, and whether this one counts is unmeasured), the same line is painted in the name component instead. The status key says which happened.

Known gaps, written into the script: JAWS gets no cursor position on this line; it is not known whether the routing data offset counts from the start of the whole line or of the segment, or whether it allows for panning (the script assumes the whole line and logs every number it used); and it is not known whether JAWS's rebuild of the line keeps the place you panned to.

### Compile

    powershell -File tools\braille-trial\compile-jaws-trial.ps1 -Version 2026

Copies `jjbrailletrial.jss`, `.jkm` and `.jbs` into `%AppData%\Freedom Scientific\JAWS\<version>\Settings\enu\` and runs `scompile.exe` there. The user folder only: nothing touches `default.jss` or `default.jsb`. Without `-Version` it uses the newest JAWS user folder; this machine has 2026 and 2027, so name the one you run. JAWS loads the script the next time the trial program comes to the front.

Keys, inside the trial program only: Insert+Alt+Shift+B speaks the status (press it first: if it says nothing, the script is not loaded and nothing else means anything); Insert+Alt+Shift+V switches view.

The script logs to `%LOCALAPPDATA%\jjbrailletrial\jaws-trial-<date>-<time>.log`.

### Remove

    powershell -File tools\braille-trial\compile-jaws-trial.ps1 -Version 2026 -Remove

Deletes the four `jjbrailletrial.*` files from that settings folder.

## The four steps

NVDA first, on a 40-cell display; JAWS only after NVDA passes all four.

1. **The line shows.** Start the program; focus is on the trial Home. The display should show the window's name, then `14.225.000  USB  S7  VOX off  Tuning modern`, with the cursor on the first digit. Is it what the program sent? (The program's view string is in the trace's first `carrier` line.)
2. **Routing lands where you pressed.** A routing key over a digit of the frequency moves the program's caret to that digit and the program says which digit and its place. Over VOX or the tuning toggle it flips it and says the new state. Over the mode it says "mode, USB". The trace's `route` lines show which cell arrived as which field and character.
   - The sequence to prove: route on the tuning toggle (it says "Tuning classic"); route on the 100 Hz digit, the sixth digit, `0` after the second dot (it says "frequency, digit 6, 100 hertz"); then Up and Down tune 100 Hz at a time, and the line and the carrier change with each step.
3. **Live updates keep your place.** Press F5. The frequency walks and the meter changes five times a second. Does the line flicker, jump, or lose the place you panned to? F5 again stops it; F6 takes single steps.
4. **Panning on a short display.** On a 20-cell display the line is longer than the display. Pan forward and back. Routing on the second panel should still land on the right field and character. Where a panel breaks the line is NVDA's own panning, not the design's never-split-a-field page rule (section 3.3), which the trial deliberately does not build; whether NVDA's panning is good enough, or the page rule is needed, is part of what this step shows.

## What has not been verified

Nothing here has run inside a screen reader, and the program has not been started. The offline checks prove the parsing, the layout, the cell-to-field mapping, the message numbers and the app module's logic against stand-ins for NVDA; the tests prove the program's view model. Unproved until a session: that NVDA loads the app module and calls it as its source says; that the background thread can read the carrier's name; that a posted message crosses from the reader's process to the program; that NVDA's line keeps your pan when the program changes it; and everything about the JAWS script, starting with whether it compiles.
