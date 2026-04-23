# Slice Management

FlexRadio slices are like independent receivers — each one has its own frequency, mode, filters, and audio. If you have used a radio with dual VFOs, you can think of slices as VFOs on steroids. Depending on your radio model, you can have anywhere from 2 to 8 slices active at once.

## Creating and Releasing Slices

- **Create Slice** — adds a new slice, as long as your radio supports more slices than you currently have active. You will hear: "Slice created, 2 slices active."
- **Release Slice** — removes the most recently added slice. You will hear a confirmation such as "Slice released, 2 slices active" depending on how many slices remain.
- You cannot release the only slice. If you try, you will hear: "Cannot release the only slice."

These commands are available in the Classic and Modern menus, and also through the ScreenFields panel under the Audio and Slice category (`Ctrl+Shift+2`).

## How Many Slices Can You Have?

The maximum number of slices depends on your radio model:

- FLEX-6300, FLEX-6400, FLEX-8400, AU-510 — up to 2 slices
- FLEX-6500, FLEX-6600, FLEX-8600, AU-520 — up to 4 slices
- FLEX-6700 — up to 8 slices

Keep in mind that in MultiFlex mode (when multiple operators share a single radio), the available slices are split among the connected clients.

## Enhanced Slice Status

Press `Ctrl+Shift+S` for a detailed slice status report. When you have two or more slices active, you will hear something like this:

"2 slices. Slice A selected, transmit, 14.250 megahertz, Upper Side Band, pan center. Slice B, 7.150 megahertz, Lower Side Band, muted, pan right."

When only one slice is active, `Ctrl+Shift+S` gives you the normal single-slice status instead.

## Tips

- Each slice is independent, which means you can listen to 20 meters on Slice A and 40 meters on Slice B at the same time.
- Only one slice at a time can be the transmit slice. The status report always tells you which slice is currently the transmit slice.
