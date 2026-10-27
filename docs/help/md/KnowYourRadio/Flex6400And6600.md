# Know Your Radio: FLEX-6400 and FLEX-6600

There are a lot of ports on the back of a 6400 or 6600, and nobody hands you a map. This page is the map: what is back there, what shape it is, and how to find the one you want by touch.

Everything here was checked against FlexRadio's own FLEX-6400/FLEX-6600 Hardware Reference Manual, including its panel drawings, and against the FLEX-6400M/FLEX-6600M User Guide. It covers the FLEX-6400, FLEX-6400M, FLEX-6600 and FLEX-6600M.

## How to read these directions

All rear-panel directions assume you are behind the radio, looking at its back — the way you stand when you are actually plugging something in. Left and right are your left and right from there. If you are reaching around from the front instead, they swap, so it is worth turning the radio or walking around it the first time.

## The front panel

On a FLEX-6400 or FLEX-6600, the front panel holds one thing: the power button. There are no jacks on it at all.

On the M models — the FLEX-6400M and FLEX-6600M — the front carries an eight-inch touchscreen on the left, and to the right of it a field of knobs and buttons: the multi-function controls for each receiver, MOX, TUNE and ATU, the programmable function buttons, and two large tuning knobs. The power button sits at the top left.

Either way, there is no microphone jack, no key jack and no headphone jack on the front. Every connection you make goes on the back.

## Finding your place on the rear panel

Find the accessory connector first. It is a VGA-style connector — a D-shaped shell with a row of small holes and a threaded screw post on each side — sitting in the middle of the panel. It is the only connector of its kind back there, and the two screw posts make it unmistakable under your fingers. Once you have it, everything else is described relative to it.

## The four small jacks left of the accessory connector

Just left of the accessory connector is a block of four identical small jacks, arranged as two columns of two. This is the cluster you want for audio and keying.

Working out from the accessory connector:

- The column nearest the accessory connector holds the CW key jack on top and the microphone jack directly below it.
- The column further left holds the powered-speaker output on top and the headphone jack directly below it.

All four are the same size and feel, so counting position is the only reliable way to tell them apart. Key over microphone, speakers over headphones.

On a FLEX-6600 or 6600M there is also a balanced audio input above and left of that block: a noticeably larger round connector with a locking collar, the only one of its kind on the panel. It takes a quarter-inch plug for a balanced microphone. The FLEX-6400 and 6400M do not have it.

## The RCA jacks right of the accessory connector

Right of the accessory connector is a block of eight RCA jacks in two rows of four. Reading each row left to right:

- The top row is PTT, TX request, ALC, and remote power on.
- The bottom row is TX relay 1, TX relay 2, TX relay 3, and the 10 MHz reference input.

The top-left RCA of that block is the push-to-talk input. That one matters more than the rest, for the reason in the next section.

## Plugging in the hand microphone

The hand microphone that came with the radio has two plugs, and both of them matter:

- The small plug goes into the microphone jack: the bottom jack of the column nearest the accessory connector.
- The RCA plug goes into the push-to-talk jack: the top-left RCA in the block of eight, just right of the accessory connector.

**Warning:** The microphone jack does not carry push-to-talk. FlexRadio's manual says so in as many words. If you plug in only the small plug, the microphone's PTT button will do nothing at all. Both plugs, every time.

**Tip:** The FHM-2 microphone supplied with the M models is an electret and needs bias voltage before it will produce any audio. That is a software setting, not a wiring problem — if the microphone is plugged in correctly and still silent, bias is the first thing to check.

## Plugging in a CW key or paddle

A straight key or paddle goes into the key jack: the top jack of the column nearest the accessory connector, directly above the microphone jack. It needs only its one plug — unlike the microphone, there is nothing else to connect.

For a straight key, use a stereo plug and connect the key to the tip and sleeve. For paddles, the dot goes to the tip, the dash to the ring, and the common to the sleeve.

## Keying the radio to register it for SmartLink

Registering a radio to a SmartLink account requires proving that somebody is standing at it, by keying it by hand. Either of these works:

- Press the push-to-talk button on the hand microphone, with both of its plugs connected as described above.
- Close a CW key or paddle plugged into the key jack.

**Warning:** On the M models, the MOX button on the front panel does not count. FlexRadio's own guide is explicit that the registration check cannot be satisfied from the front panel MOX button or from any remote keying input. It has to be the microphone's PTT button, a CW key, or a switch wired into the PTT RCA.

## Everything else on the rear panel

For completeness, the rest of what is back there, roughly left to right:

- The DC power input, an Anderson Powerpole pair, at the lower left.
- Two antenna connectors, ANT1 and ANT2, large threaded SO-239 sockets left of centre.
- A chassis ground thumbscrew, low and left of centre.
- The receive antenna input and the transverter port, chunky threaded BNC connectors below the small-jack cluster. A FLEX-6600 or 6600M has two of each, side by side, because it has two receivers; a FLEX-6400 or 6400M has one of each.
- An external display connector below the RCA block. It is fitted but not supported on these models.
- A small threaded GPS antenna connector and a 10 MHz reference output at the top right, present only if the radio was ordered with the GPS-disciplined oscillator option.
- An ethernet socket at the far right, with two USB sockets stacked below it.
