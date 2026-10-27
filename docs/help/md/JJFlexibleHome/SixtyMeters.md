# 60 Meter Operation

The 60 meter band (5 MHz) is a special case on the amateur bands. In the US, it is not a continuous band the way 40 meters or 20 meters are — it is a set of specific channelised frequencies, plus a digital segment, each with its own power and mode restrictions. JJ Flexible Radio Access handles all of this for you.

## The Channels

The FCC allocates five Upper Side Band voice channels on 60 meters, each with a maximum of 100 watts PEP:

- **Channel 1** — 5.3320 MHz, Upper Side Band
- **Channel 2** — 5.3480 MHz, Upper Side Band
- **Channel 3** — 5.3585 MHz, Upper Side Band
- **Channel 4** — 5.3730 MHz, Upper Side Band
- **Channel 5** — 5.4050 MHz, Upper Side Band

There is also a digital segment from 5.3515 MHz to 5.3665 MHz for CW and digital modes.

## Navigating the Channels

Press `Alt+Shift+Up` or `Alt+Shift+Down` to step through the 60 meter channels. JJ Flexible Radio Access speaks each channel as you land on it — for example, "Channel 1, 5.332 megahertz, Upper Side Band."

The digital segment is included in the rotation, and it is announced as: "60 meter digital segment, 5.3515 megahertz."

The channels wrap around — pressing Up from Channel 5 takes you to the digital segment, and then back to Channel 1. You can also use the band menu to find "60m Channel Up" and "60m Channel Down" menu items if you prefer.

## Transmit Rule Enforcement

Open **Tools > Settings > License** to configure country-specific rules:

- **Country selection** — defaults to US. Your country determines which 60 meter allocations apply.
- **Enforce transmit rules** — when this is enabled, the application forces USB mode on the channelised frequencies and caps your power at 100 watts. The enforcement setting helps you stay legal without having to remember every rule yourself.

## Automatic Mode Switching

When you step to a 60 meter channel using the channel navigation keys, JJ Flexible Radio Access switches the radio's mode for you:

- Landing on one of the five voice channels forces Upper Side Band mode — this matches the FCC allocation, which allows USB only on the voice channels.
- Landing on the digital segment switches to CW mode as a sensible default. If you actually want to run RTTY, FT8, or another digital mode on that segment, switch modes manually after you land — JJ Flexible Radio Access only sets CW as the "not USB" default.

The mode switch is announced alongside the frequency, so you hear "Channel 1, 5.332 megahertz, Upper Side Band" even if you were in Lower Side Band before stepping onto the channel. No accidental LSB-on-60-meters misadventures.

## Tips

- The 60 meter channels are popular for near-vertical-incidence-skywave (NVIS) propagation, which is excellent for regional contacts during the day.
- If you operate outside the US, the 60 meter allocation is probably different from the US channelisation. Check your country's regulations and update the country setting in **Tools > Settings > License** accordingly.
