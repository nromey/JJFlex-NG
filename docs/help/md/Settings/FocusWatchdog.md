# When Another Program Takes Your Keyboard

Windows lets any program take the keyboard away from the one you are in, once you have been idle for a few minutes. It does not ask, and it does not tell you. If you can see the screen you notice the new window and click back; if you cannot, the window you were in simply stops answering, your screen reader has nothing to say, and there is no way to tell that apart from a crash.

JJ Flexible Radio Access watches for this while one of its dialogs is waiting on you. If the keyboard goes somewhere else at a moment when you were not touching anything, it takes the keyboard back within a few seconds and tells you what happened — "Another program had taken the keyboard. You are back in Select Radio" — naming the program when it can.

## The Rules It Follows

Taking the keyboard back is a serious thing for a program to do, so it only happens when all of the following are true.

- **You were idle.** If you pressed a key, moved the mouse, or Alt-Tabbed after the last moment this program had the keyboard, then you went somewhere on purpose and you are left exactly where you went. Every time. There is no time limit involved and no "long enough" threshold — it is a straight comparison between when you last did something and when this program last had the keyboard.
- **One of its own dialogs is holding the rest of the application anyway.** While a dialog like Select Radio or Settings is up, the rest of JJ Flexible Radio Access is not available to you regardless, so there is nothing of ours you could have gone to instead.
- **The program that took the keyboard is not a password, security or permission prompt.** Those are never taken from. Making a sign-in box or a Windows permission prompt disappear from under someone who cannot see the screen is the one case where stepping in would be worse than the outage.
- **It gives up rather than fights.** If something keeps grabbing the keyboard back and you still have not touched anything, it stops after two attempts and waits for you. The count resets the moment you press a key.

## Turning It Off

Open **Tools > Settings**, go to the **Accessibility** category, and find the checkbox **Take the keyboard back if another program takes it while you are idle**. It is on when the program is installed. Clear it and press OK to switch it off.

Turn it off if a JJ Flexible Radio Access window ever comes to the front while you are doing something else and you did not ask it to. That is the symptom, and it is the only one — a window of ours arriving uninvited, while you were busy somewhere else. If that happens, please note what you were doing and what was on screen at the time, and tell me: the rules above are meant to make it impossible, so an occurrence is something I want to see rather than something you should have to live with.

Switching it off does **not** turn off the separate repair for a keyboard that has gone nowhere at all. If no window anywhere on your screen is taking input — keys reaching nothing, your screen reader with nothing to follow — a dialog of this program's will still bring itself back. That case cannot be you having gone somewhere else, because there is nowhere you could have gone.

## What Happens When It Is Off

Nothing is taken and nothing is spoken. The keyboard stays where it went, and you get back to the dialog the way you would with any other program: Alt+Tab, or your screen reader's window list.

The watchdog keeps watching, though, and writes into the diagnostic log every time it would have stepped in. If you switch it off and then have a session where the keyboard disappeared on you, the log can say afterwards that this would have brought you back and that it was switched off at the time. That is deliberate: an off switch that also erases the evidence would make the next problem harder to find rather than easier.

`Ctrl+J` then `Alt+W` still tells you who took it, too. The last row of that window list names the program that took the keyboard, which dialog it was taken from, and when — and when the watchdog is switched off, that row says plainly that it was not taken back.

## Related Topics

- The Diagnostic Log
- Screen Reader Setup
- Keyboard Reference
