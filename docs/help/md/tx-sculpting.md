# TX Bandwidth Sculpting

TX bandwidth sculpting lets you shape the sound of your transmitted audio by adjusting the low-frequency and high-frequency edges of the TX filter. This gives you control over how your signal sounds to other stations.

## Why Sculpt Your TX Audio?

Every voice is different, and band conditions change over the course of a contact. Sculpting lets you:

- **Widen** the bandwidth for a natural-sounding ragchew audio profile.
- **Narrow** the bandwidth for better readability in noisy conditions or during contests.
- **Shift** the low and high edges to emphasise or cut certain frequency ranges in your voice.

## Adjusting TX Bandwidth from the Keyboard

You can adjust the TX filter edges directly from the keyboard, without opening any dialog:

- `Ctrl+Shift+[` — lower the TX filter low edge.
- `Ctrl+Shift+]` — raise the TX filter low edge.
- `Ctrl+Alt+[` — lower the TX filter high edge.
- `Ctrl+Alt+]` — raise the TX filter high edge.

Each press adjusts the corresponding edge by one step, and JJ Flexible Radio Access speaks the new value aloud.

You can also open the Audio Workshop (`Ctrl+Shift+W`) for a more visual view of the TX sculpting controls. From the Audio Workshop you can adjust:

- **Low edge** — the lowest frequency passed by the TX filter (typically 100 to 300 Hz).
- **High edge** — the highest frequency passed by the TX filter (typically 2700 to 3000 Hz).

## Hearing Your Current TX Filter Settings

Press `Ctrl+Shift+F` to have JJ Flexible Radio Access speak your current TX filter settings. The announcement includes the low edge, the high edge, and the calculated bandwidth between them. You can also use the leader-key form — press `Ctrl+J`, then `F`.

For example, you might hear: "TX filter 100 to 2900, 2.8 kilohertz."

## Typical Settings

- **Standard SSB** — 100 to 2800 Hz (2.7 kHz bandwidth).
- **Wide ragchew** — 50 to 3050 Hz (3.0 kHz bandwidth).
- **Narrow / contest** — 200 to 2600 Hz (2.4 kHz bandwidth).
- **DX pileup** — 300 to 2700 Hz (2.4 kHz, with the low end cut to reduce rumble in crowded pileups).

**Tip:** Ask a friend on the air to give you a signal report as you adjust. What sounds good to you through headphones may sound quite different on the air, and an honest on-air listener is the best tool you have for getting your sculpt right.
