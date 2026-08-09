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

**RATIFIED (Noel, 2026-08-08): once per session by default, with a checkbox to remember the state.** The operator makes the rule; we pick the conservative default. Straight application of the flexibility principle — togglable, conservative default, per-radio. A station where the 2 m transverter is permanently wired and always powered should not be asked daily; a station where boxes get swapped should be asked every time. Only the operator knows which station they have.

## Why this unifies with the loopback work

**The Audio Check loopback is itself a synthetic transverter** — a band whose only job is to unlock dBm drive control, pointed at a port, at a very low level. If transverter profiles exist, the loopback stops needing private drive logic and becomes a built-in profile ("Loopback", low drive, whatever port), reusing the same machinery the operator can inspect and override.

That also resolves the automation's teardown obligation cleanly: **band definitions persist in the radio profile**, so ad-hoc creation mutates operator state that outlives the session. A profile system owns its bands deliberately instead of creating and destroying them behind the operator's back, and must never renumber or reuse bands the operator defined by hand.

## Needs verification at the radio

- **Does the radio auto-select a band when you tune into its range, or must the band be selected explicitly?** `order` implies a precedence list, which suggests the radio does the mapping itself — but `rf_freq` and `if_freq` are single values with no width field anywhere, so how the band's extent is determined is unknown. If the radio already maps frequency to band, our job is the port binding and the speech; if not, we own the mapping too.
- Whether a defined band translates the *ears-slice* as well as the TX slice (matters for the loopback, see `audio-workshop-plan.md` §4c).
- What `is_valid` rejects — it is the radio's verdict on a coherent definition and should drive our validation messages rather than us reimplementing the rules.

## Transverters as a grantable resource in JJ Flexible Connect (Noel, 2026-08-08)

**Belongs in the Connect design (`cookie-sked-keydown.md`) once merged — kept here for now because that file had uncommitted edits in flight on 2026-08-08.**

**The rule:** the owner can disallow or enable transverter access per guest, and **enabling availability for a port is an active, deliberate act** — never a side effect of granting a session. Default off. Exception: the operator's own "don't ask" checkbox above, which is their standing statement about their own station.

**The driving use case, and it is a good one.** Noel: a friend asks an operator whether he can play with a European operator's QO-100 rig. If the transverter is on and the grant is correct, the friend gets his time slot. Noel wants to do this himself.

This is the strongest Connect story yet, because **QO-100 is not reachable from Memphis at all.** Es'hail-2 is geostationary over Africa and the Middle East — below the horizon from North America, permanently. No amount of equipment or patience gets a US operator onto that transponder. The only path is somebody else's station. That reframes Connect from "operate your radio from elsewhere" (convenience) to **"operate a radio you could never own, pointed at a sky you cannot see"** (access to the physically impossible). For an operator whose travel is constrained, that difference is the whole point.

**Why default-off is not paranoia here.** Transverters are the most damage-prone thing on the port list: drive is milliwatt-class and mixer overdrive is the classic way to destroy one, the boxes are expensive, and band privileges differ by country — a guest transmitting outside their licence, through the owner's station, lands in the *owner's* jurisdiction. QO-100 additionally has an enforced operating norm (do not exceed the beacon level). An owner sharing HF should never discover they also shared a 2.4 GHz uplink.

**Design consequences that fall out:**

- **The drive ceiling travels with the grant.** Since each profile owns a dBm/mW drive setting, the owner should cap what a guest may reach — the guest's slider tops out at the *granted* ceiling, not the hardware ceiling. This is what protects a stranger's transverter from a guest who has never met that box, and it maps directly onto the QO-100 beacon-level norm.
- **A guest cannot perform the connection handshake, and must not be asked to.** The whole justification for that confirmation is that it asks a human to vouch for physical reality the machine cannot check — but a remote guest cannot check it either. So the physical assertion moves to the **owner, at grant time** ("XVT A has the 2.4 GHz transverter, it is powered"). What the guest sees is a statement of the grant's terms — band, port, drive ceiling, slot length — and an acknowledgment, not a verification. Same principle, correctly re-aimed at the only person who can actually answer.
- **Silent failure is the risk to design against.** If the transverter is off, or was never on, the guest keys up and simply radiates nothing — with no local symptom. The radio cannot see the box, so we cannot detect this directly. Candidate signals: reflected power or SWR anomalies, or the absence of an expected downlink. Open question, not solved here.
- **QO-100 operation needs full duplex, so it needs 2 SCUs.** The transponder is worked full-duplex — operators find themselves on the downlink while transmitting, which is the standard way to confirm you are on frequency and at the right level. That requires the receiver alive during transmit, i.e. `Radio.FullDuplexEnabled`, i.e. a 2-SCU radio. The same capability gate the audio-check loopback work established. Worth noting the convergence: **QO-100 operating IS the hear-yourself loop, at satellite scale** — the feature we are building for audio checks is the same mechanism the satellite requires.
- Slot mechanics belong to the existing scheduling problem (`project_radio_access_scheduling.md`, `project_multiflex_tx_is_a_mutex.md`); a transverter grant is an attribute of a slot, not a separate booking system.

## Why it is worth doing

VHF/UHF weak-signal work is real ham activity, transverter setup is fiddly arithmetic, and the operator currently has to hold the port mapping in their head every single time. It is also a good fit for the accessibility thesis: the failure mode of getting it wrong is silent (you transmit into nothing, or onto the wrong band, and nothing tells you), and silent failures are exactly what our users cannot afford.
