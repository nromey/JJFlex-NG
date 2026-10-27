# Know Your Radio: FLEX-8400 and FLEX-8600

There are a lot of ports on the back of an 8000-series radio, and nobody hands you a map. This page is the map: what is back there, what shape it is, and how to find the one you want by touch.

Everything here was checked against FlexRadio's own FLEX-8000 Hardware Reference Manual, including its panel photographs. It covers the FLEX-8400, FLEX-8400M, FLEX-8600 and FLEX-8600M.

## How to read these directions

All rear-panel directions assume you are behind the radio, looking at its back — the way you stand when you are actually plugging something in. Left and right are your left and right from there. If you are reaching around from the front instead, they swap, so it is worth turning the radio or walking around it the first time.

## The front panel

On a FLEX-8400 or FLEX-8600, the front panel has no jacks at all. There is one control: the power button, at the top left corner, a small rounded rectangle set into the upper edge of the panel. Everything else on the front is trim.

On the M models — the FLEX-8400M and FLEX-8600M — the front is a different animal. It carries a large touchscreen on the left, and to the right of it a field of knobs and buttons: four multi-function knobs across the top, then MOX, TUNE and ATU, then the function buttons, then two large tuning knobs low down. The power button is still at the top left corner.

The important part is the same for both: even on the M models, there is still no microphone jack, no key jack and no headphone jack on the front. Every connection you make goes on the back.

## Finding your place on the rear panel

Find the accessory connector first. It is a VGA-style connector — a D-shaped shell with a row of small holes and a threaded screw post on each side — sitting right of centre. It is the only connector of its kind back there, and the two screw posts make it unmistakable under your fingers. Once you have it, everything else is described relative to it.

## The four small jacks left of the accessory connector

Just left of the accessory connector is a block of four identical small jacks, arranged as two columns of two. This is the cluster you want for audio and keying.

Working out from the accessory connector:

- The column nearest the accessory connector holds the CW key jack on top and the microphone jack directly below it.
- The column further left holds the powered-speaker output on top and the headphone jack directly below it.

All four are the same size and feel, so counting position is the only reliable way to tell them apart. Key over microphone, speakers over headphones.

Above and slightly left of that block is the balanced audio input, a noticeably larger round connector with a locking collar — the only one of its kind on the panel. It takes a quarter-inch plug for a balanced microphone.

## The RCA jacks right of the accessory connector

Immediately right of the accessory connector is a block of eight RCA jacks in two rows of four. Reading each row left to right:

- The top row is PTT, TX request, ALC, and remote power on.
- The bottom row is TX relay 1, TX relay 2, TX relay 3, and a one-pulse-per-second output.

The top-left RCA of that block — the one closest to the accessory connector — is the push-to-talk input. That one matters more than the rest, for the reason in the next section.

## Plugging in the hand microphone

The hand microphone that came with the radio has two plugs, and both of them matter:

- The small plug goes into the microphone jack: the bottom jack of the column nearest the accessory connector.
- The RCA plug goes into the push-to-talk jack: the top-left RCA in the block of eight, just right of the accessory connector.

**Warning:** The microphone jack does not carry push-to-talk. If you plug in only the small plug, audio will work and the microphone's PTT button will do nothing at all. This catches people out constantly, and it is not a fault — the jack simply has no PTT pin. Both plugs, every time.

## Plugging in a CW key or paddle

A straight key or paddle goes into the key jack: the top jack of the column nearest the accessory connector, directly above the microphone jack. It needs only its one plug — unlike the microphone, there is nothing else to connect.

## Keying the radio to register it for SmartLink

Registering a radio to a SmartLink account requires proving that somebody is standing at it, by keying it by hand. Either of these works:

- Press the push-to-talk button on the hand microphone, with both of its plugs connected as described above.
- Close a CW key or paddle plugged into the key jack.

Software transmit does not count, and neither does anything sent over the network — that is the entire point of the check. FlexRadio requires it and it cannot be skipped or done remotely, which is why a radio has to be registered before it is shipped anywhere.

## Everything else on the rear panel

For completeness, the rest of what is back there, roughly left to right:

- The DC power input, an Anderson Powerpole pair, at the lower left.
- Two antenna connectors, ANT1 and ANT2, large threaded SO-239 sockets left of centre.
- A chassis ground thumbscrew, low and left of centre.
- The receive antenna input and the transverter port, chunky threaded BNC connectors below the small-jack cluster. A FLEX-8600 or 8600M has two of each, side by side, because it has two receivers; a FLEX-8400 or 8400M has one of each.
- A serial data slot above the accessory connector.
- An external display connector below the RCA block.
- Two more BNC connectors below that, a 10 MHz reference input and a 10 MHz reference output.
- An ethernet socket at the far right, with two USB sockets stacked below it.
- A small threaded GPS antenna connector at the top right, present only if the radio was ordered with the GPS-disciplined oscillator option. If your radio does not have that option, that spot is blank.
