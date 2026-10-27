# Know Your Radio: FLEX-6300

This page is a map of your radio's panels: what is on them, what shape each connector is, and how to find the one you want by touch.

Everything here was checked against FlexRadio's own FLEX-6000 Hardware Reference Manual, including its panel photographs.

Good news first: on a FLEX-6300, the microphone, headphones and CW key all plug into the **front** panel, and there are only four things on it. The back is where the antennas and the station wiring live, and you can mostly set that up once and forget it.

## How to read these directions

Front-panel directions assume you are sitting in front of the radio as you would to operate it. Rear-panel directions assume you are behind it, looking at its back — the way you stand when you are actually plugging something in. Left and right are always from the side you are facing, so they swap between the two, and it is worth turning the radio around the first time rather than reaching over the top.

## The front panel

The FLEX-6300's front panel is a single row of four items, all of them in the left half of the panel. From the left edge, working right:

- The power button. It is a small rectangular button at the far left, with a status light in a slot just above it.
- The microphone jack. A round connector with eight small pins inside and a knurled collar around it. It is the only round multi-pin connector on the radio, and by far the most distinctive thing on the front panel — a good landmark to find first.
- The headphone jack. A quarter-inch jack, the large size used by studio headphones, with a hexagonal nut around it.
- The CW key jack. Another quarter-inch jack, identical in size and feel to the headphone jack, at the right-hand end of the group.

The headphone and key jacks are the same size and are the only two of their kind, so telling them apart comes down to order: headphones first, key on the right. The microphone connector to their left is unmistakable.

The right half of the front panel is blank. If you are used to a FLEX-6500 or FLEX-6700, note that the 6300 has no display and no navigation keypad — and its power button is at the far **left**, where those radios put theirs at the far right.

## Plugging in the hand microphone

The hand microphone plugs into the round eight-pin connector on the front panel, and that single plug carries everything, including push-to-talk. Line up the plug, push it home, and tighten the collar.

This is worth stating plainly because it is not true of every FlexRadio: on the 6400, 6600 and the whole 8000 series, the microphone jack has no PTT pin and the hand microphone needs a second plug in a separate RCA jack. On the FLEX-6300 it does not. One plug and you are done.

**Tip:** The FHM-2 microphone is an electret and needs bias voltage before it will produce any audio. That is a software setting, not a wiring problem — if the microphone is plugged in correctly and still silent, bias is the first thing to check. The FHM-1 does not need it.

## Plugging in a CW key or paddle

A straight key or paddle goes into the key jack at the right-hand end of the front panel group, using a quarter-inch stereo plug.

For a straight key, connect the key to the tip and sleeve and leave the ring unconnected. For paddles, the dot goes to the tip, the dash to the ring, and the common to the sleeve.

## Keying the radio to register it for SmartLink

Registering a radio to a SmartLink account requires proving that somebody is standing at it, by keying it by hand. Either of these works:

- Press the push-to-talk button on the hand microphone plugged into the front panel.
- Close a CW key or paddle plugged into the front panel key jack.

Software transmit does not count, and neither does anything sent over the network — that is the entire point of the check. FlexRadio requires it and it cannot be skipped or done remotely, which is why a radio has to be registered before it is shipped anywhere.

## Finding your place on the rear panel

The rear panel is laid out in two rough rows. The easiest landmark is the accessory connector: a VGA-style connector with a D-shaped shell, a row of small holes and a threaded screw post on each side, sitting in the middle of the lower row. It is the only connector of its kind back there.

## The rear panel, upper row

From the left:

- Two antenna connectors, ANT1 then ANT2, large threaded SO-239 sockets set together in a recessed panel at the far left.
- A chassis ground thumbscrew, on its own in the middle of the panel.
- The transverter port, a chunky threaded BNC connector, right of centre.
- The ethernet socket at the far right.

## The rear panel, lower row

From the left:

- The DC power input, an Anderson Powerpole pair, left of centre.
- A pair of RCA jacks side by side: remote power on at the left, TX relay at the right.
- The accessory connector, the VGA-style connector described above, in the middle.
- The powered-speaker output, a single small jack just right of the accessory connector.
- Another pair of RCA jacks side by side: ALC at the left, push-to-talk at the right.
- Two USB sockets at the far right, stacked below the ethernet socket.

The push-to-talk RCA is there for a foot switch or an external keying line. You do not need it for the hand microphone — on this radio the microphone connector already carries PTT.

**Tip:** If you are wiring your own accessory cable, note that the FLEX-6300's accessory connector is mounted the other way up compared with the FLEX-6500 and FLEX-6700, so pin numbering runs opposite to the drawings in the manual for those models. Also, pin 5 is a chassis ground on those radios but is reserved and not grounded on the 6300.
