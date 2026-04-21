# Panadapter visibility

The panadapter is the spectrum/waterfall display — the visual view of what's on the band. It's primarily useful for sighted operators (or sighted helpers looking over your shoulder). If you don't need it visually, you can hide it.

## The toggle

Settings > Notifications > "Show panadapter" turns the panadapter view on or off. Default: on.

When it's off, JJ Flex still receives the IQ data from the radio — everything under the hood keeps working — the view just isn't rendered to the screen. Turn it back on any time and the display reappears.

## Why you'd turn it off

- You operate entirely by ear and screen reader, with no sighted helpers. The display is wasted pixels and a small amount of wasted CPU.
- You share the shack PC with a sighted operator sometimes but not usually. Turn it on for their session, off for yours.
- You're running JJ Flex over a remote desktop connection with limited bandwidth and want to skip the bandwidth cost of streaming the panadapter bitmap.

## What stays working with the panadapter hidden

- All slice operations (tuning, filters, modes, NR).
- All audio, meters, status dialogs.
- Speak Status, status announcements, CW notifications.
- Recording.
- Transmit.

Essentially everything except the visual spectrum view. If you had a sighted operator in the chair, they'd have less to look at — but for blind and low-vision operating, nothing is lost by turning it off.

## Future waterfall work

Sprint 28 and later will introduce accessible waterfall output — rendering the spectrum as braille patterns on compatible displays, and as structured audio representations. Those are separate from the visual panadapter; they'll have their own controls when they land. Today's Show panadapter toggle controls only the sighted pixel view.
