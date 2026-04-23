# Filters and DSP

Your Flex radio has a lot of DSP (Digital Signal Processing) horsepower built in, and JJ Flexible Radio Access gives you keyboard control over all of it.

## Quick DSP Toggles via the Leader Key

The fastest way to toggle DSP features is through the leader key. Press `Ctrl+J` to enter leader-key mode, then press one of the following:

| Key | Feature |
|-----|---------|
| N | Legacy Noise Reduction (NR) |
| Shift+N | NR Filter (model-specific) |
| B | Noise Blanker (NB) |
| W | Wideband Noise Blanker (WNB) |
| R | Neural Noise Reduction (RNN) |
| Shift+R | PC-side Neural Noise Reduction (runs on your computer) |
| S | Spectral Noise Reduction |
| A | Auto Notch Filter (ANF) |
| P | Audio Peak Filter (APF, CW only) |
| F | Speak the current TX filter width |
| Shift+F | Speak the current RX filter width |

Each toggle plays an earcon — a rising tone when the feature turns on, a falling tone when it turns off — followed by a speech announcement confirming the new state.

## Noise Reduction (NR)

The Legacy Noise Reduction feature reduces background noise on received signals. It is one of the most commonly used DSP features. Toggle it with `Ctrl+J`, then `N`.

## Noise Blanker (NB)

The Noise Blanker is designed to suppress impulse noise — things like power line interference, electric fences, and other sharp clicks and pops. Toggle it with `Ctrl+J`, then `B`.

## Wideband Noise Blanker (WNB)

The Wideband Noise Blanker works on broader noise patterns than the standard Noise Blanker, and it complements NB rather than replacing it. Toggle it with `Ctrl+J`, then `W`.

## Neural Noise Reduction (RNN)

Neural NR uses machine learning to separate speech from noise. It requires a radio model that supports Neural NR in its firmware, along with an appropriate license tier. Toggle it with `Ctrl+J`, then `R`.

You can also run a PC-side Neural NR engine on any Flex radio, regardless of license. See the PC-side Noise Reduction help page for details.

## Spectral Noise Reduction

Spectral NR works in the frequency domain to reduce steady-state noise. It is often the cleanest choice for removing things like a noisy power supply hum. Toggle it with `Ctrl+J`, then `S`.

## Auto Notch Filter (ANF)

The Auto Notch automatically finds and removes carriers and tones — great for eliminating that annoying heterodyne on a busy band. Toggle it with `Ctrl+J`, then `A`.

## Audio Peak Filter (APF)

The Audio Peak Filter narrows the audio passband around a CW signal, making it easier to pick out a single station in a pileup. APF is mostly useful in CW mode, and JJ Flexible Radio Access will tell you "Audio Peak Filter is CW only" if you try to enable it in another mode. Toggle it with `Ctrl+J`, then `P`.

## Filter Width

You can adjust the receive filter width through the ScreenFields panel. The filter settings available depend on your current operating mode — voice modes use wider filters, and CW uses narrower ones.

To hear the current TX filter width, press `Ctrl+J`, then `F`. To hear the current RX filter width, press `Ctrl+J`, then `Shift+F`.

## Feature Availability

Not all DSP features are available on every radio model. Some require specific hardware, specific firmware, or a particular software license tier. The Feature Availability tab in JJ Flexible Radio Access shows you which features your radio supports, and explains why any greyed-out features are not available.
