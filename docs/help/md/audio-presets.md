# Audio Presets

An audio preset is a snapshot of your transmit audio chain — everything the Audio Workshop can shape about how you sound going out. Save the way you sound for ragchews under one name and the way you punch through a pileup under another, and switching between them becomes two keystrokes instead of resetting eight controls by hand.

One honest boundary up front: presets are about your **transmit** audio — the signal leaving your station. They do not touch the receive side. No listening volumes, no receive filters, no noise reduction, no audio routing, none of that. What *you* hear is yours to set elsewhere in JJ Flexible Radio Access; what the *other station* hears is what a preset carries.

## What a preset holds

When you save a preset, JJ Flexible captures these settings from the radio, and loading it later puts every one of them back at once:

- Mic gain, mic boost, and mic bias
- The compander, on or off, and its level
- The speech processor, on or off, and its mode (Normal, DX, or DX+)
- The transmit filter's low and high edges
- The TX monitor — on or off, its level, and its pan

That is the complete list. If a control is not on it, a preset neither saves nor changes it.

## Three presets come in the box

On a fresh start you already have three, built from sensible starting points rather than anyone's idea of perfection:

- **Ragchew** — a wide, natural voice for casual contacts. Transmit filter from 100 to 3100 hertz, no processing.
- **Contest SSB** — punchy and narrow for pileups. More mic gain, compander and speech processor on, filter tightened to 200 through 2900 hertz.
- **DX Pileup** — maximum punch for DX work. The processor in its most aggressive mode and the filter squeezed to 300 through 2700 hertz.

Treat them as launch pads: load one, run an Audio Check, adjust to taste, and save the result under your own name.

## Where presets live

Everything happens in the Audio Workshop — press `Ctrl+Shift+W` from anywhere in the application. The toolbar across the top of the workshop has five buttons: Load Preset, Save Preset, Export, Import, and Reset.

### Saving

Press **Save Preset** (or `Ctrl+S` anywhere in the workshop), give it a name, and the transmit chain as it stands on the radio right now is captured. Get your audio where you like it first — the preset saves what the radio is actually doing, not what you meant to set.

### Loading

Press **Load Preset** (or `Ctrl+O`) and pick from the list. The moment you press OK, every setting in the preset goes to the radio — this one *does* retune your transmit chain, immediately, because that is its entire job.

### Deleting

Deleting lives inside the Load Preset picker, right where the list is: arrow to the preset you are done with and press the `Delete` key, or Tab to the Delete button. JJ Flexible reads the preset back to you and asks before removing anything, because there is no undo. Deleting never touches the radio — even if the preset you delete is the one you loaded five minutes ago, your current settings stay exactly where they are.

### Sharing: Export and Import

**Export** (`Alt+E`) writes your current transmit chain to a small XML file you choose — one preset, one file, easy to email or drop in a club chat. **Import** (`Alt+I`) reads such a file and adds it to your saved presets.

Two things worth knowing about Import:

- **Importing does not touch the radio.** The preset lands in your list and waits; nothing changes on the air until you deliberately load it. A file arriving from a friend is not permission to retune your transmitter.
- **A bad file gets called a bad file.** If the file cannot be read as a preset — wrong format, corrupted, not a preset at all — JJ Flexible says so and imports nothing, rather than quietly handing you a blank preset. And if an import's name matches one you already have, the newcomer gets a number after its name so the two stay tellable-apart in the list.

A word to the wise before loading a preset from someone else: their mic, their voice, and their station are not yours. Load it, run an Audio Check, and listen before you call anyone.

### Reset

**Reset** (`Alt+R`) is not a preset at all — it puts the whole transmit chain back to stock defaults (mic gain 50, no processing, filter 100 through 2900 hertz). A known-good starting point for when an experiment has wandered somewhere strange.

## Your presets are yours

Presets are stored per operator. If more than one person uses JJ Flexible on this computer, each operator keeps their own list — your contest settings will not ambush anyone else's ragchew.

## When to reach for presets

Any time your transmit audio wants to be different depending on what you are doing:

- **Ragchewing** — a wide filter and a natural, unprocessed voice for friends who will be listening to you for an hour.
- **Contesting** — compression and a narrow filter so your call cuts through when everyone is shouting at once.
- **DX chasing** — everything the processor has, concentrated into the frequencies that carry.
- **Different microphones** — a hand mic and a boom mic rarely want the same gain and boost settings; a preset per mic ends the re-tweaking.

Set each one up once, prove it with an Audio Check, save it, and from then on changing hats is a `Ctrl+O` away.
