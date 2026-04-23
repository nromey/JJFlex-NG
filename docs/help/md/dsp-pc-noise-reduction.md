# PC-Side Noise Reduction

Every Flex radio has noise reduction built into the radio's own firmware. JJ Flexible Radio Access adds a second layer of noise reduction that runs on your computer, separately from the radio. Both layers are useful, they are not the same thing, and knowing which one to reach for makes a real difference.

## Two Noise Reduction Layers, Two Different Purposes

- **Radio-side NR** (the Flex's built-in NR, WNR, and similar features) runs inside the radio's own DSP hardware. It is fast, it does not touch your computer's CPU, and it works whether JJ Flexible Radio Access is running or not. Use it as your first stop — it is always available, and for most conditions it is enough.
- **PC-side NR** runs inside JJ Flexible Radio Access on your computer. It processes the audio stream after the audio leaves the radio. It is free, it is available on every radio model regardless of license tier, and it gives you two different engines to choose from.

## The Two PC-Side Engines

JJ Flexible Radio Access currently offers two PC-side noise reduction engines:

- **RNNoise** — a neural-network-trained engine designed to reduce wideband noise while preserving voice. This is a good first-choice engine for voice modes. It is light on CPU, and it has no tuning knobs to get wrong.
- **Spectral subtraction** — a more classic DSP approach that estimates the noise floor and subtracts it from the audio. It often sounds cleaner than RNNoise on steady-state noise, like a noisy power supply or a nearby switching-mode power supply hum. It can sound a little artifact-heavy on music-like signals, but it works well for voice.

Both engines are independent of the radio's built-in NR. You can run the radio's NR alongside PC-side NR if you want — the effects compound, each layer cleaning up what the other one leaves behind.

## When PC-Side NR Earns Its Keep

- Your radio does not have a premium NR license tier, but your computer has CPU cycles to spare.
- You want a different sonic character than what the radio's firmware NR offers. RNNoise in particular sounds noticeably different from Flex's WNR.
- You are recording or digitally processing the audio downstream and you want cleaner input going into that process.

## How to Turn It On

Under **Settings > Audio > PC Noise Reduction**, pick an engine from the dropdown, or set it to Off to bypass PC-side NR entirely. The Audio Workshop dialog also exposes quick A/B comparison controls so you can hear the difference between engines on live audio without committing to a setting.

## What PC-Side NR Will Not Do

PC-side NR runs on the audio stream after it reaches your computer — it cannot make a weak signal louder. For weak-signal work, reach for the radio's AGC, preselector, and filter settings first. PC-side NR is a polish stage, not a signal-lifter.
