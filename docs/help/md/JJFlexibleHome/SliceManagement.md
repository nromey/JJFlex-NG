---
keywords:
  - slices
---

# Slice Management

FlexRadio slices are like independent receivers — each one has its own frequency, mode, filters, and audio. If you have used a radio with dual VFOs, you can think of slices as VFOs on steroids. Depending on your radio model, you can have anywhere from 2 to 8 slices active at once.

## Creating and Releasing Slices

- **New Slice** — adds a new slice, as long as your radio supports more slices than you currently have active. You will hear: "Slice created, 2 active."
- **Release Active Slice** — removes the slice you are currently on, not the one you added last. You will hear which one went: "Slice D released, 3 active."
- You cannot release your last remaining slice. If you try, you will hear: "Cannot release last slice."

These commands live on the **Slice** menu, in the **Selection** submenu, and are also on the ScreenFields panel under the Audio category (`Ctrl+Shift+U`). The menu structure is the same whether you are in Classic tuning mode or Modern tuning mode.

If other operators are sharing the radio with you, the Selection submenu also tells you how many slices they are using. You can only release your own.

## Making Slice Changes Stick

Slices you add or release last only as long as your connection. The radio keeps
its own stored setup, and it puts that back the next time you connect — so if
you release Slice D today and find it waiting for you tomorrow, nothing is
broken. Nothing had told the radio to keep the change.

After you add or release a slice you will hear a reminder: "This will not
survive disconnect unless you save the profile."

Choose **Save Station Setup to Radio** on the **Radio** menu to keep the change. The explanation names the global profile about to be written and asks for confirmation. Selecting **Don't show this explanation again** and confirming the save hides that explanation for later saves. Restore the explanation through **Messages You Have Silenced** on Settings' **Notifications** tab; see [Messages You Have Silenced](SilencedMessages.md).

Two things are worth knowing before you save:

- **It saves the whole station, not just the slice you were thinking about.**
  What the radio stores is a global profile, which covers every slice, its
  frequency and its mode.
- **A global profile belongs to the radio, not to you.** Everyone who connects
  to that radio gets what you save. If you are borrowing someone else's rig,
  this is the one to leave alone.

For that second reason, JJ Flexible Radio will not save while another operator is
connected to the same radio — saving then would store their setup along with
yours. It says so rather than failing quietly.

If you would rather be asked automatically, turn on **Offer to save my setup to
the radio when I disconnect** on the Notifications tab in Settings. It only ever
asks. It never saves on its own, it stays quiet when you have changed nothing,
and it leaves other people's radios alone.

## How Many Slices Can You Have?

The maximum number of slices depends on your radio model:

- FLEX-6300, FLEX-6400, FLEX-8400, AU-510 — up to 2 slices
- FLEX-6500, FLEX-6600, FLEX-8600, AU-520 — up to 4 slices
- FLEX-6700 — up to 8 slices

Keep in mind that in MultiFlex mode (when multiple operators share a single radio), the available slices are split among the connected clients.

## Selecting a Slice and Tuning

The Slice field uses letter keys `A` through `H` and digit keys to select a slice directly. `Space` cycles to the next slice and wraps at the end; `V` also cycles forward on this field. The Slice Operations field also accepts `A` through `H` for direct selection.

The Slice field's `Up` and `Down` switch slices by default, following **Slice arrow direction** in Settings, **Accessibility**, **Slice list**. Check **Tune with Up and Down on the Slice field** to make those arrows tune by the coarse step instead.

The Slice Operations field's `Up` and `Down` tune by the coarse step by default. Clear **Tune with Up and Down in Slice operations** in the same Settings group to make those arrows adjust the slice's volume. **Slice arrow direction** applies only when the Slice field's arrows switch slices.

Every field on the Home slice row, including Frequency, accepts `Ctrl+Up` and `Ctrl+Down` for coarse tuning and `Shift+Up` and `Shift+Down` for fine tuning in both tuning modes. These pairs also tune elsewhere in the main window, except where a control keeps the arrows for its own value or selection. See [Tuning and Frequency](TuningAndFrequency.md) for those exceptions. Plain `Up` and `Down` on the Classic Frequency field continue to tune by the digit under the cursor.

Pan keys work regardless of either arrow-tuning checkbox. The Slice field's `Page Up` pans fully right, `Home` centers the pan, and `Page Down` pans fully left. The Slice Operations field's `Page Up` and `Page Down` move pan right or left in steps. Those page keys on other slice-row fields announce the audio-layer shortcut instead.

The Volume field also adjusts slice volume. The audio layer offers both controls: press the JJ key, then `A`, then `V` for slice volume or `P` for pan.

## Enhanced Slice Status

Press `Ctrl+Shift+S` for a detailed slice status report. When you have two or more slices active, you will hear something like this:

"2 slices. Slice A selected, transmit, 14.250 megahertz, Upper Side Band, pan center. Slice B, 7.150 megahertz, Lower Side Band, muted, pan right."

When only one slice is active, `Ctrl+Shift+S` gives you the normal single-slice status instead.

## Tips

- Each slice is independent, which means you can listen to 20 meters on Slice A and 40 meters on Slice B at the same time.
- Only one slice at a time can be the transmit slice. The status report always tells you which slice is currently the transmit slice.

## Related Topics

- [Tuning and Frequency](TuningAndFrequency.md)
- [Tuning Steps](TuningSteps.md)
- [Messages You Have Silenced](SilencedMessages.md)
