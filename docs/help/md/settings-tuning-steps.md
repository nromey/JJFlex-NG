# Tuning Steps

Tuning-knob steps are editable too — not just the filter widths. You can configure what each step of the knob moves the VFO by, separately for coarse tuning and fine tuning. Per operator, saved, survives restart.

## Coarse vs fine

JJ Flex distinguishes two tuning speeds:

- Coarse — the default step, used during normal operation. Think "move across a band to find a QSO." Defaults range from 1 kHz to 5 kHz depending on mode.
- Fine — a smaller step for centering on a signal after you've got it rough-tuned. Defaults are down in the 5 Hz to 100 Hz range.

There's a hotkey that toggles between coarse and fine — the same knob then moves in whichever step size is currently active.

## Where to edit them

Settings > Tuning > Edit Tuning Steps opens the dialog with two lists: Coarse and Fine. Each list is a set of step sizes you can cycle through with the step-smaller / step-bigger hotkeys during operation.

You can add, remove, reorder, and change each step. The UI treats them as integer Hz values — 5 Hz, 10 Hz, 100 Hz, 1000 Hz, 5000 Hz are common entries.

## Why customize

The defaults are general-purpose. Tuning patterns differ by mode and by operator:

- CW operators often want very small fine steps (1 Hz or 5 Hz) to zero-beat.
- Ragchewing on SSB, you probably never need finer than 10 Hz.
- Contest CW might want coarse steps aligned to 500 Hz or 1 kHz so band-hopping between stations is predictable.
- FM operators frequently want coarse steps matching channel spacing — 5 kHz, 12.5 kHz, or 25 kHz — so the tuning knob drops you cleanly onto the next channel every click.

Configure it once to match what you actually do; it stops fighting you forever after.

## Per-operator, per-mode

Tuning step lists live per operator (like filter presets). If you share a shack PC, each operator gets their own tuning behaviors. You can also have different coarse/fine lists take effect on different modes if you set them up that way — the lists apply to whatever mode is active at the moment.

## Resetting

Settings > Reset to defaults restores the built-in lists. Useful if you've over-edited.
