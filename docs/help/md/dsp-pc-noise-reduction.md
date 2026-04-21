# PC-Side Noise Reduction

Every Flex radio has noise reduction built into the radio's firmware. JJ Flex adds a second layer that runs on your computer, separately from the radio. Both layers are useful; they're not the same thing; and knowing which one to reach for makes a difference.

## Two noise reduction layers, two purposes

- Radio-side NR (the Flex's own NR, WNR, and similar) runs inside the radio's DSP hardware. It's fast, doesn't touch your CPU, and works whether JJ Flex is running or not. Use it as your first stop — it's always available, and for most conditions it's enough.
- PC-side NR runs inside JJ Flex on your computer. It processes the audio stream after it leaves the radio. It's free, available on every radio model regardless of license tier, and gives you two different engines to pick from.

## The two PC-side engines

JJ Flex currently offers two:

- RNNoise — a neural-network-trained engine designed to reduce wideband noise while preserving voice. Good first-choice engine for voice modes. Light on CPU, no tuning knobs.
- Spectral subtraction — a more classic DSP approach that estimates the noise floor and subtracts it. Often cleaner on steady-state noise like a noisy power supply or a nearby SMPS hum. Can sound a little artifact-y on music-like signals but works well for voice.

Both are independent of the radio's own NR. You can run radio NR alongside PC NR if you want — they compound.

## When PC-side NR earns its keep

- Your radio doesn't have a great NR license tier but your computer has CPU to spare.
- You want a different NR character than what your radio's firmware offers. RNNoise in particular sounds different from Flex's WNR.
- You're recording or digitally processing the audio downstream and want cleaner input to work with.

## How to turn it on

Settings > Audio > PC Noise Reduction. Pick an engine from the dropdown, or set it to Off to bypass. The Audio Workshop dialog also exposes quick A/B comparison controls if you want to hear the difference between engines on live audio.

## What it won't do

PC NR runs on the audio stream — it can't make a weak signal louder. For weak-signal work, reach for the radio's AGC + preselector + filter settings first. PC NR is a polish stage, not a signal-lifter.
