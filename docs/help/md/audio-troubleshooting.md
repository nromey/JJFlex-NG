# Audio Troubleshooting

If you are not hearing audio, or the audio sounds wrong, here are some things to check before digging any deeper.

## No Audio at All

1. **Check your computer's audio output.** Make sure your speakers or headphones are connected, and that the system volume is up.
2. **Check the JJ Flexible Radio Access audio levels.** Press `Alt+Page Up` a few times to increase the audio gain. If you are using headphones, press `Alt+Shift+Page Up` to increase the headphone volume specifically.
3. **Check your audio routing.** Open the Audio Workshop by pressing `Ctrl+Shift+W` to verify that audio is being routed to the correct output device.
4. **Is the radio actually connected?** Press `Ctrl+Shift+S` to hear the connection status. If you are disconnected, no audio will flow — you will need to reconnect first.

## Distorted or Clipping Audio

- Turn the audio gain down with `Alt+Page Down`.
- If it is your transmitted audio that sounds distorted, open the TX sculpting settings and make sure your microphone gain is not too high.

## Audio Only on One Side (Panning)

Press `Ctrl+P` to hear your current panning settings. Centre panning sends audio equally to both channels; if one channel is silent, the audio may simply be panned fully to the other side.

## Earcons (User Interface Sounds) Are Not Playing

Earcons use your default Windows audio device, which may be different from where your radio audio is going. If you can hear the radio clearly but not the earcons, check your Windows default playback device in the system tray or in Windows Sound Settings.

## Remote (SmartLink) Audio Issues

- Audio over SmartLink is compressed with the Opus codec. A small amount of quality loss is normal and expected.
- Audio dropouts or choppy audio usually indicate a network problem. Try a wired Ethernet connection instead of Wi-Fi, as Wi-Fi jitter is often the cause.
- If you hear no audio at all over SmartLink, open your radio's settings (in SmartSDR or via the radio's web interface) and make sure remote audio is enabled for the radio itself.
