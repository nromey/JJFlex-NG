# Filter Presets

Filter widths on every mode are preset to reasonable defaults out of the box — but "reasonable" depends on what you do on the air. A DXer chasing weak CW signals wants a narrower set of CW presets than a ragchewer who lives in 600 Hz of passband. You can edit the filter presets per operator, and your changes survive across application restarts.

## Where to Edit Them

Open **Tools > Settings > Tuning > Edit Filter Presets**. A dialog appears, listing each mode (CW, USB, LSB, AM, FM, DIG, and others) along with its current preset widths. Each mode has its own list of preset widths, which you can cycle through using the filter hotkeys during operation.

From the Edit Filter Presets dialog, you can:

- Change the width of an existing preset.
- Add a new preset to a mode's list.
- Remove a preset you do not use.
- Reorder the presets, so the one you reach for most often is first.

## What "Per Operator" Means

Filter presets live with the rest of your per-operator configuration (callsign, audio routing, typing sounds, and so on). If two hams share the same computer under different operator names, each one gets their own preset lists. Switching operators switches the presets along with everything else.

## Defaults If You Reset

**Tools > Settings > Reset to defaults** restores the baseline preset lists for the current operator. This is useful if you have edited yourself into a corner and you want to start over from the standard configuration. The reset is scoped per operator — your defaults come back, but another operator's custom lists on the same computer are not disturbed.

## How the Filter Hotkeys Use the Presets

During operation, the "filter narrower" and "filter wider" hotkeys step through the current mode's preset list. Narrowing from the widest preset goes one step tighter with each press; widening does the opposite. When you reach the narrowest or widest preset in the list, the hotkey announces the current setting and then stays there instead of wrapping around — so you always know when you have hit the edge of what you have configured.

If you find yourself wishing for a width that is not in the list, just add it in Settings. That is the whole point of customisable presets.
