# Earcon Explorer

Earcons are short audio tones that JJFlexRadio plays to confirm actions and communicate state changes. They're designed to be quick and informative without interrupting your workflow.

## What Are Earcons?

Think of earcons as audio icons. Instead of a visual checkmark or color change, you hear a brief tone. A rising tone generally means "on" or "success," a falling tone means "off" or "cancel," and a buzz means "error" or "unrecognized."

## Common Earcons

- **Rising tone (bink):** Feature turned on, action confirmed
- **Falling tone (bonk):** Feature turned off, cancelled
- **Rising two-step (bonk-bink):** DSP feature toggled on via leader key
- **Falling two-step (bink-bonk):** DSP feature toggled off via leader key
- **Soft descending tone:** Leader key timeout or escape
- **Dull buzz:** Invalid key pressed, unrecognized command
- **Double chime:** Help requested (leader key + ?)

## Earcons and Screen Readers

Earcons play alongside your screen reader, not instead of it. After a toggle earcon, JJFlexRadio also speaks the state change (for example, "Noise Reduction on"). The earcon gives you instant feedback while the speech provides confirmation.

## Customizing Earcons

Earcon settings can be adjusted in the Earcon Explorer, accessible from the Audio menu. You can preview each earcon to learn what it sounds like.
