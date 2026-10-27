---
keywords:
  - TX sculpting
---

# TX Bandwidth Sculpting

Use transmit (TX) bandwidth sculpting to shape the sound of your transmitted audio. Adjusting the low-frequency and high-frequency edges of the TX filter changes how your signal sounds to other stations.

## Why Sculpt Your TX Audio?

Every voice is different, and band conditions can change during a contact. You can adjust the filter to suit your voice and the conditions:

- Widen the bandwidth for natural-sounding ragchew audio.
- Narrow the bandwidth to improve readability in noisy conditions or during contests.
- Shift the low and high edges to emphasise or cut particular frequency ranges in your voice.

## Adjusting TX Bandwidth from the Keyboard

Use these keys to adjust the TX filter edges without opening a dialog:

- Press `Ctrl+Shift+[` to lower the TX filter's low edge.
- Press `Ctrl+Shift+]` to raise the TX filter's low edge.
- Press `Ctrl+Alt+[` to lower the TX filter's high edge.
- Press `Ctrl+Alt+]` to raise the TX filter's high edge.

Each press moves the selected edge by one step. JJ Flexible Radio Access will speak the new value.

The step size depends on the current filter width. An ordinary transmit filter uses steps of 50 hertz. The steps become finer as the filter narrows and coarser beyond 3.5 kilohertz. Holding a key does not increase the rate of adjustment. Use the announced value to place the edge, because listening to the audio will not tell you when it has reached 2,800 hertz.

The Radio menu's TX Filter submenu and the filter layer use the same step size. The edge moves by the same amount whichever method you use.

You can also press `Ctrl+Shift+W` to open the Audio Workshop and use its TX sculpting controls:

- The **Low edge** sets the lowest frequency passed by the TX filter, typically 100 to 300 hertz.
- The **High edge** sets the highest frequency passed by the TX filter, typically 2,700 to 3,000 hertz.

## Hearing Your Current TX Filter Settings

To hear the current settings, open the Radio menu, then Transmit, then TX Filter, then Read TX Filter. The announcement gives the low edge, the high edge, and the calculated bandwidth between them.

You can also press `Ctrl+J`, then `F` to open the filter layer. Press `T` to select the TX filter, then `S` to hear its settings.

For example, you might hear: "TX filter 100 to 2900, 2.8 kilohertz."

## Typical Settings

- For **standard SSB**, use 100 to 2,800 hertz, giving a bandwidth of 2.7 kilohertz.
- For a **wide ragchew** setting, use 50 to 3,050 hertz, giving a bandwidth of 3.0 kilohertz.
- For a **narrow or contest** setting, use 200 to 2,600 hertz, giving a bandwidth of 2.4 kilohertz.
- For a **long-distance (DX) pileup**, use 300 to 2,700 hertz, giving a bandwidth of 2.4 kilohertz. Cutting the low end reduces rumble in crowded pileups.

**Tip:** Ask a friend on the air for a signal report while you adjust the filter. What sounds good through your headphones may sound quite different on the air. An honest report from an on-air listener is the best way to judge the result.
