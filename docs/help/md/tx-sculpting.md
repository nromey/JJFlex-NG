# TX Bandwidth Sculpting

TX bandwidth sculpting lets you shape your transmitted audio by adjusting the low and high frequency edges of your TX filter. This gives you control over how your signal sounds to other stations.

## Why Sculpt Your TX Audio?

Every voice is different, and band conditions change. Sculpting lets you:

- **Widen** your bandwidth for natural-sounding ragchew audio
- **Narrow** it for better readability in noisy conditions or contests
- **Shift** the low and high edges to emphasize or cut certain frequencies in your voice

## Adjusting TX Bandwidth

You can adjust the TX filter edges directly from the keyboard without opening any dialog:

- `Ctrl+Shift+[` — Lower the TX filter low edge
- `Ctrl+Shift+]` — Raise the TX filter low edge
- `Ctrl+Alt+[` — Lower the TX filter high edge
- `Ctrl+Alt+]` — Raise the TX filter high edge

Each press adjusts the edge by one step and speaks the new value.

You can also open the Audio Workshop (`Ctrl+Shift+W` or `Ctrl+J` then `W`) for a more visual view of the TX sculpting controls. There you can adjust:

- **Low edge** — The lowest frequency passed by the TX filter (typically 100-300 Hz)
- **High edge** — The highest frequency passed by the TX filter (typically 2700-3000 Hz)

## Hearing Your Current TX Filter

Press `Ctrl+Shift+F` to have the app speak your current TX filter settings. It announces the low edge, high edge, and calculated bandwidth. You can also use the leader key: `Ctrl+J` then `F`.

For example: "TX filter 100 to 2900, 2.8 kilohertz."

## Typical Settings

- **Standard SSB:** 100-2800 Hz (2.7 kHz bandwidth)
- **Wide ragchew:** 50-3050 Hz (3.0 kHz bandwidth)
- **Narrow/contest:** 200-2600 Hz (2.4 kHz bandwidth)
- **DX pileup:** 300-2700 Hz (2.4 kHz, cuts low-end rumble)

**Tip:** Ask a friend on the air to give you a signal report as you adjust. What sounds good to you through headphones may sound different on the air.
