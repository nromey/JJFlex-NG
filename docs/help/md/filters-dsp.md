# Filters and DSP

Your Flex radio has powerful DSP (Digital Signal Processing) built in. JJFlexRadio gives you keyboard control over all of it.

## Quick DSP Toggles with Leader Key

The fastest way to toggle DSP features is the leader key. Press `Ctrl+J`, then press one of these keys:

| Key | Feature |
|-----|---------|
| N | Noise Reduction (NR) |
| B | Noise Blanker (NB) |
| W | Wideband Noise Blanker (WNB) |
| R | Neural Noise Reduction (RNN) |
| S | Spectral Noise Reduction |
| A | Auto Notch Filter (ANF) |
| P | Audio Peak Filter (APF) |

Each toggle plays an earcon — a rising tone when the feature turns on, a falling tone when it turns off — followed by a speech announcement confirming the new state.

## Noise Reduction (NR)

Noise Reduction reduces background noise on received signals. It's one of the most commonly used DSP features. Toggle it with `Ctrl+J` then `N`.

## Noise Blanker (NB)

The Noise Blanker is designed to suppress impulse noise — things like power line interference, electric fences, and other sharp clicks and pops. Toggle it with `Ctrl+J` then `B`.

## Wideband Noise Blanker (WNB)

The Wideband Noise Blanker works on broader noise patterns. It complements the standard NB. Toggle it with `Ctrl+J` then `W`.

## Neural Noise Reduction (RNN)

Neural NR uses machine learning to separate speech from noise. It requires a radio model that supports it and an appropriate license. Toggle it with `Ctrl+J` then `R`.

## Spectral Noise Reduction

Spectral NR works in the frequency domain to reduce steady-state noise. Toggle it with `Ctrl+J` then `S`.

## Auto Notch Filter (ANF)

The Auto Notch automatically finds and removes carriers and tones — great for eliminating that annoying heterodyne on a busy band. Toggle it with `Ctrl+J` then `A`.

## Audio Peak Filter (APF)

The Audio Peak Filter narrows the audio passband around a CW signal, making it easier to pick out a single station in a pileup. Mostly useful in CW mode. Toggle it with `Ctrl+J` then `P`.

## Filter Width

You can adjust the receive filter width through the ScreenFields panel. The filter settings available depend on your current mode (voice modes have wider filters, CW has narrower ones).

To hear the current TX filter width, press `Ctrl+J` then `F`.

## Feature Availability

Not all DSP features are available on all radio models. Some require specific hardware or software licenses. JJFlexRadio's Feature Availability tab shows you which features your radio supports and why certain ones might be grayed out.
