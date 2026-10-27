# Tuning Steps

Tuning-knob step sizes are configurable. You can pick the size of one click of the Up/Down arrow on the Frequency field, separately for coarse tuning and fine tuning. The settings are per operator, saved to disk, and they survive application restarts.

## Coarse Versus Fine

JJ Flexible Radio Access has two tuning speeds in Modern tuning mode:

- **Coarse** — used by Up and Down on the Frequency field. Think of coarse as "move across a band to find a QSO." A typical coarse step is 1 kHz to 5 kHz.
- **Fine** — used by Shift+Up and Shift+Down on the Frequency field. Think of fine as "centre on a signal after you've rough-tuned onto it." A typical fine step is 10 Hz to 100 Hz.

The two are always available simultaneously. There's no mode to switch between — Up is coarse, Shift+Up is fine, and that's the whole story.

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

If you remember the old days: there used to be a `C` key that toggled between coarse and fine, plus a Page Up / Page Down pair on the Frequency field that cycled through a list of candidate step sizes for whichever mode you were in. That whole modal dance is gone. One coarse value, one fine value, two separate keys (Up and Shift+Up). No mode to forget.
