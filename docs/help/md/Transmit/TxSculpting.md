---
keywords:
  - TX sculpting
---

# TX Bandwidth Sculpting

Adjust the low and high edges of the transmit (TX) filter to shape how your audio sounds to other stations. You can make these changes from the keyboard or in the Audio Workshop.

## Why Sculpt Your TX Audio?

Your voice and the conditions of a contact can call for different filter settings. Adjusting the TX filter lets you:

- **Widen the bandwidth** for a natural-sounding ragchew audio profile.
- **Narrow the bandwidth** to improve readability in noisy conditions or during contests.
- **Shift the low and high edges** to emphasise or cut parts of your voice's frequency range.

## Adjusting TX Bandwidth from the Keyboard

To adjust the TX filter without opening a dialog, use these keys:

- `Ctrl+Shift+[` — lower the TX filter low edge.
- `Ctrl+Shift+]` — raise the TX filter low edge.
- `Ctrl+Alt+[` — lower the TX filter high edge.
- `Ctrl+Alt+]` — raise the TX filter high edge.

Each press moves the chosen edge one step, and JJ Flexible Radio Access announces its new value. The step depends on the filter's current width: 50 hertz for an ordinary transmit filter, smaller as the filter narrows, and larger beyond 3.5 kilohertz. Holding the key does not make the steps grow, so you can place an edge by its announced number. The Radio menu's TX Filter submenu and the JJ key filter layer use the same step. An edge therefore moves the same distance whichever control you use.

You can also open the Audio Workshop with `Ctrl+Shift+W` to adjust these TX filter controls:

- **Low edge:** This is the lowest frequency passed by the TX filter, typically 100 to 300 hertz.
- **High edge:** This is the highest frequency passed by the TX filter, typically 2700 to 3000 hertz.

## Hearing Your Current TX Filter Settings

To hear the current TX filter settings, open the Radio menu, choose Transmit, then TX Filter, then Read TX Filter. You will hear the low edge, the high edge, and the calculated bandwidth. You can also press `Ctrl+J`, then `F` to open the filter layer. Press `T` to select the TX filter, then `S` to hear its settings.

For example, you might hear: "TX filter 100 to 2900, 2.8 kilohertz."

## Typical Settings

- **Standard SSB:** Set the edges from 100 to 2800 hertz for a bandwidth of 2.7 kilohertz.
- **Wide ragchew:** Set the edges from 50 to 3050 hertz for a bandwidth of 3.0 kilohertz.
- **Narrow or contest:** Set the edges from 200 to 2600 hertz for a bandwidth of 2.4 kilohertz.
- **DX pileup:** Set the edges from 300 to 2700 hertz for a bandwidth of 2.4 kilohertz. Cutting the low end can reduce rumble in a crowded pileup.

**Tip:** Ask a friend on the air for a signal report as you adjust the filter. What sounds good through your headphones may sound different on the air, and an honest listener can help you judge the result.
