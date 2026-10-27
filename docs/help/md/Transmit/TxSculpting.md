---
keywords:
  - TX sculpting
---

# TX Bandwidth Sculpting

You can shape the sound of your transmitted audio by adjusting the low and high edges of the transmit (TX) filter. These edges control the bandwidth of your signal and how it sounds to other stations.

## Why Sculpt Your TX Audio?

Every voice is different, and band conditions can change during a contact. You can widen the bandwidth for a natural-sounding ragchew audio profile, or narrow it for better readability in noisy conditions or during contests. You can also shift the low and high edges to emphasise or cut certain frequency ranges in your voice.

## Adjusting TX Bandwidth from the Keyboard

You can adjust either edge of the TX filter from the keyboard without opening a dialog:

- Press `Ctrl+Shift+[` to lower the low edge of the TX filter.
- Press `Ctrl+Shift+]` to raise the low edge of the TX filter.
- Press `Ctrl+Alt+[` to lower the high edge of the TX filter.
- Press `Ctrl+Alt+]` to raise the high edge of the TX filter.

Each press moves the corresponding edge by one step, and JJ Flexible Radio Access speaks the new value. The step depends on the current width of the filter: it is 50 hertz for an ordinary transmit filter, finer as the filter narrows, and coarser beyond 3.5 kilohertz. The step does not speed up when you hold the key. An edge is placed by its numerical value, and your ear will not tell you when you have reached 2,800 hertz. The Radio menu's TX Filter submenu and the filter layer use the same step, so an edge moves the same distance whichever way you adjust it.

For a more visual view of the TX sculpting controls, open the Audio Workshop with `Ctrl+Shift+W`. From the workshop, you can adjust both edges:

- The low edge is the lowest frequency passed by the TX filter, typically 100 to 300 hertz.
- The high edge is the highest frequency passed by the TX filter, typically 2700 to 3000 hertz.

## Hearing Your Current TX Filter Settings

To hear the current TX filter settings, open the Radio menu, then Transmit, then TX Filter, and select Read TX Filter. The announcement includes the low edge, the high edge, and the calculated bandwidth between them. For example, you might hear: "TX filter 100 to 2900, 2.8 kilohertz."

You can also press `Ctrl+J`, then `F` to open the filter layer. Press `T` to select the TX filter, then `S` to hear its settings.

## Typical Settings

- For standard SSB, the range is 100 to 2800 hertz, with a bandwidth of 2.7 kilohertz.
- For a wide ragchew, the range is 50 to 3050 hertz, with a bandwidth of 3.0 kilohertz.
- For narrow or contest operation, the range is 200 to 2600 hertz, with a bandwidth of 2.4 kilohertz.
- For a DX pileup, the range is 300 to 2700 hertz, with a bandwidth of 2.4 kilohertz. The low end is cut to reduce rumble in crowded pileups.

As you adjust the filter, ask a friend on the air for a signal report. What sounds good to you through headphones may sound quite different on the air, and an honest on-air listener is the best tool you have for getting your sculpt right.
