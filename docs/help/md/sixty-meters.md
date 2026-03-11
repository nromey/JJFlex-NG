# 60 Meter Operation

The 60 meter band (5 MHz) is a special case. In the US, it's not a continuous band like 40 or 20 meters — it's a set of specific channelized frequencies plus a digital segment, each with power and mode restrictions. JJ Flexible Radio Access handles all of this for you.

## The Channels

The FCC allocates five USB voice channels on 60 meters, all at 100 watts PEP maximum:

- Channel 1: 5.3320 MHz, USB
- Channel 2: 5.3480 MHz, USB
- Channel 3: 5.3585 MHz, USB
- Channel 4: 5.3730 MHz, USB
- Channel 5: 5.4050 MHz, USB

There's also a digital segment from 5.3515 to 5.3665 MHz for CW and digital modes.

## Navigating the Channels

Press `Alt+Shift+Up` and `Alt+Shift+Down` to step through the 60 meter channels. The app speaks each channel as you land on it: "Channel 1, 5.332 megahertz, USB."

The digital segment is included in the rotation and announced as: "60 meter digital segment, 5.3515 megahertz."

Channels wrap around — going up from Channel 5 takes you to the digital segment, then back to Channel 1. You can also use the band menu: look for "60m Channel Up" and "60m Channel Down."

## Transmit Rule Enforcement

Go to Settings > License tab to find:

- **Country selection** — defaults to US. This determines which 60 meter allocations apply.
- **Enforce transmit rules** — when enabled, the app forces USB mode on channelized frequencies and caps power at 100 watts. This helps you stay legal without having to remember the rules yourself.

## Tips

- The 60 meter channels are popular for NVIS (near vertical incidence skywave) propagation — great for regional contacts during the day.
- If you're outside the US, the 60 meter allocation may be different. Check your country's regulations and update the country setting accordingly.
