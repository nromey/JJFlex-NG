# Band Navigation

JJ Flexible Radio Access makes it easy to jump between amateur bands without having to spin the tuning dial through megahertz of spectrum every time.

## Quick Band Jumps

Press a function key to jump directly to a band:

| Key | Band |
|-----|------|
| F3 | 160 meters |
| F4 | 80 meters |
| F5 | 40 meters |
| F6 | 20 meters |
| F7 | 15 meters |
| F8 | 10 meters |
| F9 | 6 meters |

For the WARC bands and the less common bands, hold Shift:

| Key | Band |
|-----|------|
| Shift+F3 | 60 meters |
| Shift+F4 | 30 meters |
| Shift+F5 | 17 meters |
| Shift+F6 | 12 meters |

## Sequential Band Changes

If you prefer to step through bands one at a time:

- `Alt+Up` moves to the next higher band.
- `Alt+Down` moves to the next lower band.

## Where Do You Land?

When you jump to a band, JJ Flexible Radio Access tunes to a sensible default frequency for that band based on your current mode. For example, jumping to 20 meters in USB might land you near 14.200 MHz, while jumping in CW mode would land you in the CW portion of the band.

Press `F2` after jumping to hear exactly where you ended up.

## 60-Meter Channels

60 meters is a special case on the amateur bands. In the US, 60 meters is channelised — you can only transmit on 5 specific Upper Side Band frequencies, plus a digital segment. JJ Flexible Radio Access knows about those channels and makes it easy to navigate between them.

When you are on the 60-meter band (jump there with `Shift+F3`), use these keys:

- `Alt+Shift+Up` — move to the next 60-meter channel.
- `Alt+Shift+Down` — move to the previous 60-meter channel.

As you navigate, JJ Flexible Radio Access speaks each channel — for example, "Channel 1, 5.332 megahertz, Upper Side Band" or "60 meter digital segment, 5.3515 megahertz." The channels wrap around, so pressing Up from the last channel takes you back to Channel 1.

### Channel Enforcement

By default, JJ Flexible Radio Access uses US channelisation rules for 60 meters. You can change your country (and you can control whether transmit rules are enforced) under **Tools > Settings > License**. The **Enforce transmit rules** checkbox controls whether the application restricts you to legal channels on 60 meters, or lets you tune freely.
