# Status dialog (Ctrl+Alt+S)

Ctrl+Alt+S opens a live status dialog — every piece of radio state you'd want to know at a glance, organized into navigable categories, refreshing in real time while it's open. It's a different tool from Ctrl+Shift+S (Speak Status), and it's useful in different moments.

## Speak Status vs the Status dialog

- Ctrl+Shift+S — Speak Status. Fires a short spoken summary: "Listening on 14.225, USB, 20 meter band, slice A." No dialog, no focus change. Use it constantly during operation to confirm where you are.
- Ctrl+Alt+S — the Status dialog. Opens a resizable window with categories you can arrow through. Use it when you want to inspect in depth: what's every slice doing, what's the ATU status, what are all the meters showing right now, what's the current CPU temperature. The dialog stays open until you close it (Escape or Alt+F4).

## Navigating the dialog

The dialog is organized into expandable categories. Arrow up/down moves between category headers. Enter expands or collapses a category. Inside an expanded category, Tab moves through the individual fields.

Typical categories you'll see:

- VFO and slice state — frequencies, modes, filter widths per slice.
- Meters — S-meter, SWR, ALC, forward power, PA temperature, voltage.
- DSP state — NR, NB, APF, preselector, active on/off and current settings.
- Transmit state — PTT status, tune carrier status, microphone gain, compression.
- Network — connection type, SmartLink status, latency if available.
- Radio identity — model, serial, firmware version.

## Refresh behavior

Fields update live while the dialog is open. The refresh rate is tuned for responsiveness without being chatty — S-meter reads a few times a second; slower-changing fields update when they change, not on a poll cycle.

## When to use it vs Speak Status

- Speak Status for "where am I right now?" during normal operating.
- Status dialog for "why is the radio doing that?" when something's off. You can leave it open on a second monitor (or just alt-tab to it) and watch everything in one place while you troubleshoot.
