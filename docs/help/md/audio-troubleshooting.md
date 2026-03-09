# Audio Troubleshooting

If you're not hearing audio or the audio sounds wrong, here are some things to check.

## No Audio at All

1. **Check your computer's audio output.** Make sure speakers or headphones are connected and the system volume is up.
2. **Check JJFlexRadio audio levels.** Press `Alt+Page Up` a few times to increase the audio gain. Press `Alt+Shift+Page Up` to increase headphone volume if you're using headphones.
3. **Check audio routing.** Open the Audio Workshop (`Ctrl+J` then `T`) to verify audio is routed to the correct output device.
4. **Is the radio connected?** Press `Ctrl+Shift+S` to check connection status. No audio flows if you're disconnected.

## Distorted or Clipping Audio

- Turn down the audio gain (`Alt+Page Down`).
- If it's your transmitted audio that's distorted, check the TX sculpting settings and make sure your microphone gain isn't too high.

## Audio Only on One Side (Panning)

Press `Ctrl+P` to check your panning settings. Center panning sends audio equally to both channels.

## Earcons (UI Sounds) Not Playing

Earcons use your default system audio device. If you can hear the radio but not earcons, they might be going to a different audio output. Check your Windows default playback device.

## Remote (SmartLink) Audio Issues

- Audio over SmartLink is compressed with the Opus codec. Some quality loss is normal.
- Audio dropouts or choppy audio usually indicate a network problem. Try a wired connection instead of WiFi.
- If you hear no audio at all over SmartLink, make sure remote audio is enabled in your radio settings.
