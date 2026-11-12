---
keywords:
  - tuning steps
  - coarse step
  - fine step
  - custom step lists
---

# Tuning Steps

The coarse and fine tuning steps control how far the frequency moves with each press of a tuning key. `Ctrl+Up` and `Ctrl+Down` use the coarse step; `Shift+Up` and `Shift+Down` use the fine step. These pairs work throughout the main window in both tuning modes, including every field on the Home slice row. Controls that use those arrows for their own values or selections keep their keys, as do dialogs and the Logging radio pane. See [Tuning and Frequency](TuningAndFrequency.md) for the details.

The last steps chosen and the lists of available steps are saved for each operator, shared across radios, and kept across application restarts.

## Coarse Versus Fine

JJ Flexible Radio Access provides coarse and fine steps in both tuning modes:

- **Coarse** — `Ctrl+Up` and `Ctrl+Down` move across the band by the coarse step. A typical coarse step is 1 to 5 kilohertz.
- **Fine** — `Shift+Up` and `Shift+Down` adjust the frequency by the fine step to center on a signal. A typical fine step is 10 to 100 hertz.

Plain `Up` and `Down` on the Frequency field use the coarse step in Modern tuning mode and the digit under the cursor in Classic tuning mode. The Slice field's arrows switch slices by default; the Slice Operations field's arrows tune by the coarse step. Settings, **Accessibility**, **Slice list** has a separate arrow-tuning checkbox for each field. See [Slice Management](SliceManagement.md).

## Choosing the Steps

Press `S` on the Frequency field in Modern tuning mode to open **Tuning Steps**. Choose a value in the coarse list and the fine list, then press the **OK** button to keep the choices. The **Coarse step default** and **Fine step default** combo boxes in **Tools > Settings > Tuning** offer the same lists; Settings selects steps but does not edit the lists. A new operator starts with a coarse step of 5 kilohertz and a fine step of 100 hertz.

The Modern Frequency field also changes step sizes without opening a window. `Alt+Left` and `Alt+Right` choose a smaller or larger coarse step; `Shift+Left` and `Shift+Right` choose a smaller or larger fine step. The choices follow the operator's lists and stop at either end.

## Editing the Step Lists

The **Tuning Steps** window opened by `S` has a new-step box and add, remove and restore buttons for each list.

1. Enter a value in the **New coarse step, in Hz or kHz** box or the **New fine step, in Hz or kHz** box. Type the unit as `Hz` or `kHz`, for example `250 Hz` or `2.5 kHz`. The value must equal a whole number of hertz.
2. Press `Enter` in that box, or choose the matching **Add coarse step** or **Add fine step** button. The window stays open, and adding a step does not select that step for tuning.
3. Choose the desired tuning steps in the lists and press **OK** to keep both the list edits and the choices. **Cancel** or `Escape` discards changes made in this window.

Fine steps range from 1 hertz to 1 kilohertz; coarse steps range from 10 hertz to 1 megahertz. Each list stays sorted and refuses duplicates within that list. The same value may appear in both lists.

Select a step and choose **Remove coarse step** or **Remove fine step** to remove that entry. The last step in a list cannot be removed. Removing the current tuning step leaves that step in use until another is selected.

## Why You Would Customise

The built-in defaults are general-purpose. Tuning patterns differ by operator and by mode:

- **CW operators** often want a very small fine step — 5 hertz — to zero-beat a signal precisely.
- **Ragchewing on SSB** rarely needs finer than 100 hertz.
- **Contest CW** often wants a 1 kilohertz coarse step so that band-hopping between stations is predictable.
- **FM operators** want coarse aligned to the channel spacing of their band — 5 kilohertz being the common one — so the tuning knob drops them cleanly onto the next channel.

## Restoring the Starting Lists

Choose **Restore starting coarse steps** or **Restore starting fine steps** in the **Tuning Steps** window. A confirmation asks before replacing the chosen list; **OK** in Tuning Steps keeps the replacement.

- The starting coarse list contains 500 hertz, then 1, 2, 5 and 10 kilohertz.
- The starting fine list contains 1, 5, 10, 50 and 100 hertz.

Restoring a list leaves the current tuning step unchanged. Choose a step in the list before pressing **OK** to change the current value.

## What Changed in 4.1.17

If you remember the old days: there used to be a `C` key that toggled between coarse and fine, plus a Page Up / Page Down pair on the Frequency field that cycled through a list of candidate step sizes for whichever mode you were in. That whole modal dance is gone. The Home slice row provides both steps directly: `Ctrl+Up` and `Ctrl+Down` use the coarse value, and `Shift+Up` and `Shift+Down` use the fine value.

## Related Topics

- [Tuning and Frequency](TuningAndFrequency.md)
- [Slice Management](SliceManagement.md)
- [Settings and Profiles](Settings.md)
