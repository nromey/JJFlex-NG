# Panadapter Visibility

The panadapter is the spectrum and waterfall display — the visual view of what is on the band. It is primarily useful for sighted operators (or for sighted helpers looking over your shoulder). If you do not need the visual view, you can hide it.

## The Toggle

Open **Tools > Settings > Notifications** and find the **Show panadapter** option. Turning it on displays the panadapter; turning it off hides it. The default is on.

When the panadapter is hidden, JJ Flexible Radio Access is still receiving all of the IQ data from the radio — everything under the hood keeps working — and the application simply does not render the view to the screen. Turn the setting back on at any time, and the display reappears.

## Why You Would Turn It Off

- You operate entirely by ear and by screen reader, with no sighted helpers involved. The display is wasted pixels and a small amount of wasted CPU cycles for you.
- You share the shack computer with a sighted operator occasionally, but not usually. Turn the panadapter on for their session, and off for yours.
- You are running JJ Flexible Radio Access over a remote desktop connection with limited bandwidth, and you want to skip the cost of streaming the panadapter bitmap over the remote connection.

## What Keeps Working with the Panadapter Hidden

- All slice operations — tuning, filters, modes, and noise reduction.
- All audio output, meters, and status dialogs.
- Speak Status, periodic status announcements, and CW notifications.
- Audio and QSO recording.
- Transmit.

Essentially everything except the visual spectrum view keeps working normally. If you had a sighted operator in the chair, they would have less to look at — but for blind and low-vision operating, nothing is lost by turning the panadapter off.

## Future Accessible Waterfall Work

A future release will introduce an accessible waterfall output — rendering the spectrum as braille patterns on compatible braille displays, and as structured audio representations for listening. That accessible waterfall will be separate from the visual panadapter; it will have its own controls when it lands. Today's **Show panadapter** toggle controls only the sighted pixel view.
