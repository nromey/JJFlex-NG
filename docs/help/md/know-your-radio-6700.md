# Know Your Radio: FLEX-6700

This page is a map of your radio's panels: what is on them, what shape each connector is, and how to find the one you want by touch.

Everything here was checked against FlexRadio's own FLEX-6000 Hardware Reference Manual, including its panel photographs. It covers the FLEX-6700 and the receive-only FLEX-6700R, which have different panels — the 6700R sections are marked as such.

Good news first: on a FLEX-6700, the microphone, headphones and CW key all plug into the **front** panel. The back is a crowded place, but it is where the antennas and the station wiring live, and you can mostly set that up once and forget it.

## How to read these directions

Front-panel directions assume you are sitting in front of the radio as you would to operate it. Rear-panel directions assume you are behind it, looking at its back — the way you stand when you are actually plugging something in. Left and right are always from the side you are facing, so they swap between the two, and it is worth turning the radio around the first time rather than reaching over the top.

## The front panel

The FLEX-6700's front panel has three jacks grouped together left of centre, then the display and controls to their right. From the left, working right:

- The microphone jack. A round connector with eight small pins inside and a knurled collar around it. It is the only round multi-pin connector on the radio and the most distinctive thing on the front panel — a good landmark to find first.
- The headphone jack. A quarter-inch jack, the large size used by studio headphones, with a hexagonal nut around it.
- The CW key jack. Another quarter-inch jack, identical in size and feel to the headphone jack, immediately to the right of it.
- The display, a wide window across the middle of the panel. Nothing to plug in here.
- The navigation keypad, a round cluster of keys with a centre OK button and up, down, left and right around it.
- The power button, at the far right, with a status light in a slot just above it.

The headphone and key jacks are the same size and are the only two of their kind, so telling them apart comes down to order: microphone, then headphones, then key, reading left to right.

If you have used a FLEX-6300, note that its power button is at the far **left**. On the 6700 it is at the far **right**, past the keypad.

## The front panel on a FLEX-6700R

The FLEX-6700R is a receiver, and its front panel is much barer. It has the headphone jack, the display, the navigation keypad and the power button in the same relative positions — but there is no microphone jack and no CW key jack at all, because there is nothing to transmit with. The headphone jack sits alone, well left of the display.

## Plugging in the hand microphone

The hand microphone plugs into the round eight-pin connector on the front panel, and that single plug carries everything, including push-to-talk. Line up the plug, push it home, and tighten the collar.

This is worth stating plainly because it is not true of every FlexRadio: on the 6400, 6600 and the whole 8000 series, the microphone jack has no PTT pin and the hand microphone needs a second plug in a separate RCA jack. On the FLEX-6700 it does not. One plug and you are done.

**Tip:** The FHM-2 microphone is an electret and needs bias voltage before it will produce any audio. That is a software setting, not a wiring problem — if the microphone is plugged in correctly and still silent, bias is the first thing to check. The FHM-1 does not need it.

## Plugging in a CW key or paddle

A straight key or paddle goes into the key jack, the rightmost of the three front-panel jacks, using a quarter-inch stereo plug.

For a straight key, connect the key to the tip and sleeve and leave the ring unconnected. For paddles, the dot goes to the tip, the dash to the ring, and the common to the sleeve.

## Keying the radio to register it for SmartLink

Registering a radio to a SmartLink account requires proving that somebody is standing at it, by keying it by hand. Either of these works:

- Press the push-to-talk button on the hand microphone plugged into the front panel.
- Close a CW key or paddle plugged into the front panel key jack.

Software transmit does not count, and neither does anything sent over the network — that is the entire point of the check. FlexRadio requires it and it cannot be skipped or done remotely, which is why a radio has to be registered before it is shipped anywhere.

A FLEX-6700R has neither jack and cannot transmit, so there is no way to key it by hand. If you are setting up SmartLink on a 6700R and are asked to key the radio, that is a question for FlexRadio support rather than something to solve at the panel.

## Finding your place on the rear panel

The rear panel is busy. Two landmarks make it manageable:

- The accessory connector, a VGA-style connector with a D-shaped shell, a row of small holes and a threaded screw post on each side. It sits left of centre and is the only connector of its kind back there.
- The balanced audio input, a large round connector with a locking collar, a little to the left of the accessory connector. It is the biggest connector on the panel apart from the antenna sockets.

## The rear panel, left of the accessory connector

From the left edge:

- The DC power input, an Anderson Powerpole pair, at the top left.
- A 10 MHz reference output, a small threaded connector below it, fitted only with the GPS-disciplined oscillator option.
- Two USB sockets stacked together, with the ethernet socket below them.
- The balanced audio input, the large round locking connector described above.
- The powered-speaker output, a single small jack just left of the accessory connector.

## The rear panel, right of the accessory connector

- A small threaded GPS antenna connector sits just below and right of the accessory connector, fitted only with the GPS-disciplined oscillator option.
- Then a block of eight RCA jacks in two rows of four. The top row, left to right, is the 10 MHz reference input, then TX relay 1, TX relay 2 and TX relay 3. The bottom row, left to right, is remote power on, ALC, TX request, and push-to-talk. The push-to-talk jack is the bottom right RCA of that block.
- The transverter port, a chunky threaded BNC connector, right of the RCA block.
- Two pairs of BNC connectors in their own recessed panels: receive antenna A, in then out, and beyond it receive antenna B, in then out. Receive antenna B exists only on the 6700 and 6700R.
- The two antenna connectors, ANT1 and ANT2, large threaded SO-239 sockets set together in a recessed panel at the upper right.
- A chassis ground thumbscrew at the far right.

The push-to-talk RCA is there for a foot switch or an external keying line. You do not need it for the hand microphone — on this radio the front microphone connector already carries PTT.

## The rear panel on a FLEX-6700R

The 6700R's back is laid out differently, and the biggest difference is at the left: it takes AC mains power through a standard three-pin inlet, not DC through Powerpoles. There is no transmit wiring at all — no TX relays, no PTT, no ALC, no transverter port.

Reading across: the AC inlet at the left, then the USB sockets and ethernet, the powered-speaker jack and the accessory connector, a 10 MHz reference input, then an auxiliary block of RCA jacks carrying three outputs and two inputs that are reserved for future use, then ANT1 and ANT2, and the chassis ground thumbscrew at the far right. The GPS antenna and 10 MHz reference connectors are in the same places as on the 6700.
