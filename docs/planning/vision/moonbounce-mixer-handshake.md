# Transverter profiles — moonbounce-mixer-handshake

**Status:** idea captured 2026-08-08 from Noel, unratified. Own feature, not part of the Audio Workshop track. Arose alongside the transverter-loopback work but stands independently.

**The ask, in Noel's words:** add a 2 m transverter and a 1.2 GHz transverter to a setting owned by the radio's operator, then either switch to one deliberately or simply enter 144.1, get a confirmation that the configured transverter is actually connected to the port you named when you added it, and have the radio switch port and frequency automatically.

## The finding that shapes the design

**The radio's own transverter model has no port field.** Verified on `track/flexlib-4220`, `FlexLib_API/FlexLib/Xvtr.cs` — the complete status field set is `name`, `rf_freq`, `if_freq`, `lo_error`, `rx_gain`, `rx_only`, `max_power`, `order`, `is_valid`. The TX/RX antenna (XVT A, XVT B) is a separate per-slice setting the radio never binds to a band.

Consequence: with two transverters on two ports, **the radio cannot tell them apart.** It knows two frequency translations exist; it does not know which jack either one is behind. The operator holds that knowledge and today has nowhere to put it.

So this feature adds information the radio does not model, and drives band selection and antenna selection together as one act. That is exactly the kind of gap JJFlex should close.

## Shape

**A transverter profile, per radio, keyed by serial** (per `project_per_radio_config_serial_keyed.md` — transverters are physical station facts, and an operator with two radios does not have the same boxes on both):

- Friendly name the operator chooses ("2 meter", "23 centimeter") — note the radio-side `name` field truncates to 4 characters, so our name is ours and is not the radio's.
- The band translation: `rf_freq` / `if_freq` (and `lo_error` when the operator has measured their box's drift).
- **Which port it is plugged into** — the field the radio does not have.
- **Its safe drive level in dBm/mW** — each transverter has a different appetite, and mixer overdrive is the classic way to destroy one. This is the slider ratified 2026-08-08 in `audio-workshop-plan.md` §4a, owned per-profile rather than globally.
- `rx_gain` so S-meter readings mean something through the converter.

**Tuning:** entering 144.100 recognizes the band, speaks what is about to happen, sets the slice's TX and RX antenna to that profile's port, and tunes. The operator never selects a port by hand or does LO arithmetic.

## Where the confirmation belongs — and why

Noel's confirmation instinct is right, and the reason is sharper than "be careful": **the software can verify the band definition but it physically cannot verify that a box is plugged into XVT A.** Only the operator knows that. The confirmation is the one moment where we ask the human to vouch for something the machine genuinely cannot check — which is precisely the carve-out the friction-tax principle allows.

That argues the confirmation must **name the port, not just the band**: "2 meter transverter, transmit antenna XVT A — connected?" A confirmation that only says "switch to 2 meters?" asks the operator to approve the part we already know and skips the part we do not.

**Proposed refinement: gate the confirmation on first transmit, not on tuning.** Receiving through a transverter port with nothing connected is harmless — you hear nothing and learn something. Transmitting is the act with consequences (wrong band, unterminated port, or silently radiating nothing while believing otherwise). So let tuning be friction-free and put the handshake at first key-up in that band per session. Less friction, and the check lands where the risk actually is.

Open question for Noel: re-ask per session, per reconnect, or once per profile with a "this is connected" state the operator maintains?

## Why this unifies with the loopback work

**The Audio Check loopback is itself a synthetic transverter** — a band whose only job is to unlock dBm drive control, pointed at a port, at a very low level. If transverter profiles exist, the loopback stops needing private drive logic and becomes a built-in profile ("Loopback", low drive, whatever port), reusing the same machinery the operator can inspect and override.

That also resolves the automation's teardown obligation cleanly: **band definitions persist in the radio profile**, so ad-hoc creation mutates operator state that outlives the session. A profile system owns its bands deliberately instead of creating and destroying them behind the operator's back, and must never renumber or reuse bands the operator defined by hand.

## Needs verification at the radio

- **Does the radio auto-select a band when you tune into its range, or must the band be selected explicitly?** `order` implies a precedence list, which suggests the radio does the mapping itself — but `rf_freq` and `if_freq` are single values with no width field anywhere, so how the band's extent is determined is unknown. If the radio already maps frequency to band, our job is the port binding and the speech; if not, we own the mapping too.
- Whether a defined band translates the *ears-slice* as well as the TX slice (matters for the loopback, see `audio-workshop-plan.md` §4c).
- What `is_valid` rejects — it is the radio's verdict on a coherent definition and should drive our validation messages rather than us reimplementing the rules.

## Why it is worth doing

VHF/UHF weak-signal work is real ham activity, transverter setup is fiddly arithmetic, and the operator currently has to hold the port mapping in their head every single time. It is also a good fit for the accessibility thesis: the failure mode of getting it wrong is silent (you transmit into nothing, or onto the wrong band, and nothing tells you), and silent failures are exactly what our users cannot afford.
