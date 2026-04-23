# Status Dialog

`Ctrl+Alt+S` opens a live Status dialog — every piece of radio state you would want to know at a glance, organised into navigable categories, and refreshing in real time while the dialog is open. The Status dialog is a different tool from `Ctrl+Shift+S` (Speak Status), and each is useful in different moments.

## Speak Status vs the Status Dialog

- **`Ctrl+Shift+S` (Speak Status)** — fires a short spoken summary: "Listening on 14.225, Upper Side Band, 20 meter band, slice A." No dialog is shown, and focus does not move. Use Speak Status constantly during operation to confirm where you are.
- **`Ctrl+Alt+S` (Status dialog)** — opens a resizable window with categories you can arrow through. Use the Status dialog when you want to inspect in depth: what is every slice doing, what is the ATU status, what are all the meters showing right now, what is the current power amplifier temperature. The dialog stays open until you close it (with `Escape` or `Alt+F4`).

## Navigating the Status Dialog

The Status dialog is organised into expandable categories. Arrow Up and Arrow Down move between category headers. Enter expands or collapses the focused category. Inside an expanded category, Tab moves through the individual fields.

Typical categories you will see include:

- **VFO and slice state** — frequency, mode, and filter width for each active slice.
- **Meters** — S-meter, SWR, ALC, forward power, power-amplifier temperature, voltage.
- **DSP state** — which of NR, NB, APF, and the preselector are active, and their current settings.
- **Transmit state** — PTT status, tune carrier status, microphone gain, compression.
- **Network** — connection type, SmartLink status, and latency where available.
- **Radio identity** — radio model, serial number, firmware version.

## Refresh Behaviour

Fields inside the Status dialog update live while the dialog is open. The refresh rate is tuned for responsiveness without being chatty — the S-meter reads several times a second, while slower-changing fields update only when they change rather than on a poll cycle.

## When to Use Each

- Use **Speak Status** for "where am I right now?" during normal operating.
- Use the **Status dialog** for "why is the radio doing that?" when something is off. You can leave the dialog open on a second monitor (or just Alt-Tab to it) and watch everything in one place while you troubleshoot.
