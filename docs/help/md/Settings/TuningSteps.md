---
keywords: []
---

# Tuning Steps

The coarse and fine tuning steps control how far the frequency moves with each press of a tuning key. `Ctrl+Up` and `Ctrl+Down` use the coarse step, and `Shift+Up` and `Shift+Down` use the fine step, from every field on the Home slice row in both tuning modes. The settings are per operator, saved to disk, and they survive application restarts.

## Coarse Versus Fine

JJ Flexible Radio Access provides coarse and fine steps in both tuning modes:

- **Coarse** — `Ctrl+Up` and `Ctrl+Down` move across the band by the coarse step. A typical coarse step is 1 to 5 kilohertz.
- **Fine** — `Shift+Up` and `Shift+Down` adjust the frequency by the fine step to center on a signal. A typical fine step is 10 to 100 hertz.

Plain `Up` and `Down` on the Frequency field use the coarse step in Modern tuning mode and the digit under the cursor in Classic tuning mode. The Slice and Slice Operations fields use the coarse step for plain `Up` and `Down` in both tuning modes.

## Where to Edit Them

Open **Tools > Settings > Tuning**. There's a Coarse step combo box and a Fine step combo box. Pick one value for each.

The defaults are 5 kHz coarse and 100 Hz fine. The combo boxes offer the most-common amateur values; if you need something different, let us know — we can extend the lists.

## Why You Would Customise

The built-in defaults are general-purpose. Tuning patterns differ by operator and by mode:

- **CW operators** often want a very small fine step — 5 Hz — to zero-beat a signal precisely.
- **Ragchewing on SSB** rarely needs finer than 100 Hz.
- **Contest CW** often wants a 1 kHz coarse step so that band-hopping between stations is predictable.
- **FM operators** want coarse aligned to the channel spacing of their band — 5 kHz being the common one — so the tuning knob drops them cleanly onto the next channel.

## Resetting

**Tools > Settings > Reset to defaults** restores 5 kHz coarse and 100 Hz fine.

## What Changed in 4.1.17

If you remember the old days: there used to be a `C` key that toggled between coarse and fine, plus a Page Up / Page Down pair on the Frequency field that cycled through a list of candidate step sizes for whichever mode you were in. That whole modal dance is gone. The Home slice row provides both steps directly: `Ctrl+Up` and `Ctrl+Down` use the coarse value, and `Shift+Up` and `Shift+Down` use the fine value.
