# Tuning Steps

Tuning-knob step sizes are editable, just like filter widths. You can configure what each click of the tuning knob moves the VFO by, separately for coarse tuning and fine tuning. Tuning-step configuration is per operator, it is saved to disk, and it survives application restarts.

## Coarse Versus Fine

JJ Flexible Radio Access distinguishes two tuning speeds:

- **Coarse** — the default step, used during normal operation. Think of coarse as "move across a band to find a QSO." The default coarse steps range from 1 kHz to 5 kHz depending on the operating mode.
- **Fine** — a smaller step, used for centring on a signal after you have rough-tuned onto it. The default fine steps live in the 5 Hz to 100 Hz range.

There is a hotkey that toggles between coarse and fine — the same tuning knob then moves the VFO by whichever step size is currently active.

## Where to Edit Them

Open **Tools > Settings > Tuning > Edit Tuning Steps**. The dialog shows two lists: one for Coarse steps and one for Fine steps. Each list is a set of step sizes that you cycle through using the "step smaller" and "step bigger" hotkeys during operation.

You can add, remove, reorder, and change each step. The interface treats steps as integer values in hertz — 5 Hz, 10 Hz, 100 Hz, 1000 Hz, and 5000 Hz are all common entries.

## Why You Would Customise

The built-in defaults are general-purpose. Tuning patterns differ by operator and by mode:

- **CW operators** often want very small fine steps — 1 Hz or 5 Hz — to zero-beat a signal precisely.
- **Ragchewing on SSB** rarely needs anything finer than 10 Hz.
- **Contest CW** often wants coarse steps aligned to 500 Hz or 1 kHz, so that band-hopping between stations is predictable.
- **FM operators** frequently want coarse steps matching the channel spacing of their band — 5 kHz, 12.5 kHz, or 25 kHz — so that the tuning knob drops them cleanly onto the next channel with every click.

Configure the step sizes once to match what you actually do on the air, and the tuning knob stops fighting you forever after.

## Per-Operator, Per-Mode

Tuning-step lists live per operator (just like filter presets). If you share a shack computer, each operator gets their own tuning behaviour. You can also have different coarse and fine lists take effect in different modes if you set them up that way — the lists apply to whatever mode is currently active.

## Resetting

**Tools > Settings > Reset to defaults** restores the built-in tuning-step lists. This is useful if you have over-edited your custom lists and you want to start over from the standard configuration.
