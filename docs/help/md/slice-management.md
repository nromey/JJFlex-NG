# Slice Management

FlexRadio slices are like independent receivers — each one has its own frequency, mode, filters, and audio. If you've used a radio with dual VFOs, think of slices as VFOs on steroids. Depending on your radio model, you can have anywhere from 2 to 8 slices active at once.

## Creating and Releasing Slices

- **Create Slice** — adds a new slice if your radio supports more. You'll hear: "Slice created, 2 slices active."
- **Release Slice** — removes the last slice. You'll hear: "Slice released, 1 slices active."
- You can't release the only slice — if you try, you'll hear: "Cannot release the only slice."

These commands are available in the Classic and Modern menus, and also in ScreenFields under the Audio and Slice category (`Ctrl+Shift+2`).

## How Many Slices?

The maximum number of slices depends on your radio model:

- FLEX-6300, 6400, 8400, AU-510: up to 2 slices
- FLEX-6500, 6600, 8600, AU-520: up to 4 slices
- FLEX-6700: up to 8 slices

Keep in mind that in MultiFlex mode (multiple operators sharing one radio), available slices are split among connected clients.

## Enhanced Slice Status

Press `Ctrl+Shift+S` for a detailed slice status report. When you have two or more slices, you'll hear something like:

"2 slices. Slice A selected, transmit, 14.250 megahertz, USB, pan center. Slice B, 7.150 megahertz, LSB, muted, pan right."

With just one slice active, `Ctrl+Shift+S` gives you the normal single-slice status instead.

## Tips

- Each slice is independent — you can listen to 20 meters on Slice A and 40 meters on Slice B at the same time.
- Only one slice can be the transmit slice at a time. The status report tells you which one.
