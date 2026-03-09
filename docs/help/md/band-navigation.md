# Band Navigation

JJFlexRadio makes it easy to jump between bands. No more spinning the dial through megahertz of spectrum.

## Quick Band Jumps

Press a function key to jump directly to a band:

| Key | Band |
|-----|------|
| F3 | 160 meters |
| F4 | 80 meters |
| F5 | 40 meters |
| F6 | 20 meters |
| F7 | 15 meters |
| F8 | 10 meters |
| F9 | 6 meters |

For the WARC and less common bands, hold Shift:

| Key | Band |
|-----|------|
| Shift+F3 | 60 meters |
| Shift+F4 | 30 meters |
| Shift+F5 | 17 meters |
| Shift+F6 | 12 meters |

## Sequential Band Changes

If you prefer to step through bands one at a time:

- `Alt+Up` moves to the next higher band.
- `Alt+Down` moves to the next lower band.

## Where Do I Land?

When you jump to a band, JJFlexRadio tunes to a sensible default frequency for that band based on the current mode. For example, jumping to 20 meters in USB might land you near 14.200 MHz, while CW mode would land you in the CW portion of the band.

Press `F2` after jumping to hear exactly where you ended up.
