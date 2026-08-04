# Know Your Radio: FLEX-6500

This page is a map of your radio's panels: what is on them, what shape each connector is, and how to find the one you want by touch.

Good news first: on a FLEX-6500, the microphone, headphones and CW key all plug into the **front** panel, and there are only a handful of things on it.

## How to read these directions

Front-panel directions assume you are sitting in front of the radio as you would to operate it. Rear-panel directions assume you are behind it, looking at its back — the way you stand when you are actually plugging something in. Left and right are always from the side you are facing, so they swap between the two, and it is worth turning the radio around the first time rather than reaching over the top.

## The front panel

FlexRadio documents the FLEX-6500 and FLEX-6700 as having the same front panel. From the left, working right:

- The microphone jack. A round connector with eight small pins inside and a knurled collar around it. It is the only round multi-pin connector on the radio and the most distinctive thing on the front panel — a good landmark to find first.
- The headphone jack. A quarter-inch jack, the large size used by studio headphones, with a hexagonal nut around it.
- The CW key jack. Another quarter-inch jack, identical in size and feel to the headphone jack, immediately to the right of it.
- The display, a wide window across the middle of the panel. Nothing to plug in here.
- The navigation keypad, a round cluster of keys with a centre OK button and up, down, left and right around it.
- The power button, at the far right, with a status light in a slot just above it.

The headphone and key jacks are the same size and are the only two of their kind, so telling them apart comes down to order: microphone, then headphones, then key, reading left to right.

If you have used a FLEX-6300, note that its power button is at the far **left**. On the 6500 it is at the far **right**, past the keypad.

## Plugging in the hand microphone

The hand microphone plugs into the round eight-pin connector on the front panel, and that single plug carries everything, including push-to-talk. Line up the plug, push it home, and tighten the collar.

This is worth stating plainly because it is not true of every FlexRadio: on the 6400, 6600 and the whole 8000 series, the microphone jack has no PTT pin and the hand microphone needs a second plug in a separate RCA jack. On the FLEX-6500 it does not. One plug and you are done.

**Tip:** The FHM-2 microphone is an electret and needs bias voltage before it will produce any audio. That is a software setting, not a wiring problem — if the microphone is plugged in correctly and still silent, bias is the first thing to check. The FHM-1 does not need it.

## Plugging in a CW key or paddle

A straight key or paddle goes into the key jack, the rightmost of the three front-panel jacks, using a quarter-inch stereo plug.

For a straight key, connect the key to the tip and sleeve and leave the ring unconnected. For paddles, the dot goes to the tip, the dash to the ring, and the common to the sleeve.

## Keying the radio to register it for SmartLink

Registering a radio to a SmartLink account requires proving that somebody is standing at it, by keying it by hand. Either of these works:

- Press the push-to-talk button on the hand microphone plugged into the front panel.
- Close a CW key or paddle plugged into the front panel key jack.

Software transmit does not count, and neither does anything sent over the network — that is the entire point of the check. FlexRadio requires it and it cannot be skipped or done remotely, which is why a radio has to be registered before it is shipped anywhere.

## The rear panel

**Warning:** FlexRadio's manuals describe the FLEX-6500 and FLEX-6700 rear panels together but only ever photograph the 6700, so the positions below are taken from that shared documentation rather than from a picture of a 6500. The list of connectors is right; treat the exact left-to-right positions as a good guide rather than a guarantee, and feel before you push.

The 6500's rear panel is laid out like the 6700's, with one difference: it has a single receiver, so it has the receive antenna A pair and no receive antenna B pair.

Two landmarks make the panel manageable:

- The accessory connector, a VGA-style connector with a D-shaped shell, a row of small holes and a threaded screw post on each side. It sits left of centre and is the only connector of its kind back there.
- The balanced audio input, a large round connector with a locking collar, a little to the left of the accessory connector.

Left of the accessory connector, from the left edge: the DC power input as an Anderson Powerpole pair at the top left; a 10 MHz reference output below it, fitted only with the GPS-disciplined oscillator option; two USB sockets with the ethernet socket below them; the balanced audio input; and the powered-speaker jack.

Right of the accessory connector: a small threaded GPS antenna connector, fitted only with the oscillator option; then a block of eight RCA jacks in two rows of four, the top row being the 10 MHz reference input and TX relays 1, 2 and 3, and the bottom row being remote power on, ALC, TX request and push-to-talk. Beyond that sit the transverter port and the receive antenna A in and out pair, all chunky threaded BNC connectors, then ANT1 and ANT2 as large threaded SO-239 sockets in a recessed panel, and a chassis ground thumbscrew at the far right.

The push-to-talk RCA is there for a foot switch or an external keying line. You do not need it for the hand microphone — on this radio the front microphone connector already carries PTT.
