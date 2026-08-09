# Questions for Don — the transverter listening trick

Date: 2026-08-07, updated the same night after we got the trick working on Noel's 8600. From the JJ Flex audio workshop planning session.

**ANSWERED by Don, returned 2026-08-07 evening.** His replies are inline below, quoted verbatim under each question (his raw copy, with his own numbering, is in `<DropboxRoot>\JJFlexRadio\don\2026-08-07-transverter-listening-trick.md`). Absorbed into `docs/planning/active/audio-workshop-plan.md` §4 and §6 on 2026-08-08.

Don, good news first: the transverter listening trick works, and we have it recipe'd on the 8600. Noel transmitted at 1 watt into the transverter port with a second slice listening on that same port, and heard his own signal — processing and all — inside one radio, no antennas connected. We're building this into JJ Flex as a one-button "check my audio" feature.

But our recipe needed one thing your radio might not have: we had to turn on the radio's full duplex setting — a hidden switch that lets the receivers keep running while transmitting. On two-SCU radios like the 8600 that switch exists; the regular software only shows it on those models. Your 6300 has one SCU, and Noel remembers you've done this trick before — which means either your radio doesn't need that switch, or your way of doing it is different from ours in a way we really want to understand.

So the questions got sharper. Six of them; short answers, voice memo, or rough notes all welcome.

1. Which radio have you done the transverter listening trick on? Specifically — have you done it on your 6300?

> **Don:** "No I haven't done it, just saw it demoed by another ham. I am not sure which flex he was using. It could have been a 6400 or 6600. It certainly predated the 8000 series."

2. When you do it, do you hear yourself live while you are talking, or do you listen back to a recording afterward? (This is the one we care most about — live listening needs the receivers running during transmit; playing back a recording doesn't.)

> **Don:** "No you heard it live during the demo not a recording."

3. Here is our 8600 recipe. Does it match what you do, and if not, what's different? Transmit antenna set to the transverter port. Second slice, same frequency, same mode, receive antenna also on the transverter port. Power at 1 watt (zero was too low — the loop went silent). Transmit monitor OFF, because with it on you hear yourself twice — once instantly, once delayed through the loop — and it's an echo mess.

> **Don:** "I know in the demo I saw he put the radio in transverter mode and that allowed him to set the transmitter to 100 milliwatts."
>
> "He also turned on the receiver maybe by using the separate receive antenna port (rca) jack not sure."

4. Did you ever have to change any radio setting to make it work — anything like a full duplex switch, or something in the transverter setup screens?

> **Don:** (answered as part of Q3 — transverter mode is the setting he remembers being changed; no mention of a full duplex switch.)

5. Noel remembers that Jim's original JJ software had something related to this, and that it was buggy or stopped working. Do you remember what Jim's version did, and what was wrong with it?

> **Don:** "It never worked. It did allow for the transverter and I believe the power could be set properly but duplex was not achieved."
>
> "I don't know if Jim achieved it either on his radio, not sure."

6. You've been checking your transmit audio against an online SDR lately. Comparing that with the transverter trick and the radio's TX monitor — what does each one tell you that the others don't? And if JJ Flex could do one thing to make checking your audio easier, what would it be?

> **Don:** "Firstly Other sdr receivers vary a fair bit quality wise so sometimes their receiver characteristics are not great for checking high quality audio."
>
> "Some sdr interfaces don't always allow for control of the receivers band with due to accessibility issues."
>
> "I trust the flexes receiver and audio chain to render my transmit audio most accurately."
>
> "This also eliminates the worry about band conditions which in some cases can prevent you from using another sdr for such checks."
>
> "There is also the issue of qsb and qrm at times so in short using your own equipment is the way to go if in can render the transmit audio accurately."
>
> "The way you described your plan to implement this feature is excellent especially if full duplex can be achieved."
>
> "IF it can't due to radio limitations then a recording should do the trick if a few conditions are met."
>
> "1. IF possible set the receivers band width to match that of the transmitter before recording or request the user to do so before they start. I think this could work very well."

Thanks, Don. Your answers decide whether this feature works on every Flex or only the two-SCU radios — your 6300 is the datapoint we can't get anywhere else.

---

## What changed as a result (2026-08-08)

- **Provenance corrected a second time.** Not a YouTube video (the 2026-08-07 afternoon correction) and not Don's own operating — an **in-person live demo by another ham** on an unidentified 6000-series radio. Predates the 8000 series, so 6300/6400/6500/6600/6700; Don guesses 6400 or 6600. The "ask Don for the YouTube link" queue item is moot — there is no link.
- **Live RX-during-TX is eyewitnessed on some 6000-series radio.** Not conclusive on SCU count (6400 = 1 SCU, 6600 = 2 SCU — the guess straddles the exact line the question was trying to settle), but it is a second independent sighting of the mechanism working.
- **NEW: transverter mode → 100 mW.** The demo set drive via a defined transverter band, not the integer-watt main power control. This is the `Xvtr.MaxPower` dBm path (`Xvtr.cs:169-202`, −10.0 to +10.0 dBm in hundredths) that the plan had marked "not needed" after the 8600 session succeeded with plain antenna selection.
- **NEW: possibly a separate RX port**, not the same XVT port on both sides. Don is explicitly unsure ("maybe... not sure"), so treat as a hypothesis, not a fact.
- **Jim's version is closed: it never achieved duplex.** Transverter selection and power setting worked; the listening half never did. Consistent with the code archaeology (Jim's only transverter artifact was a hardcoded `"XVTR"` TX-antenna string, `Flex6300Filters.cs:701`) — the TX half existed, the RX half was never built.
- **NEW hard requirement for the record tier: RX bandwidth must match TX bandwidth**, set automatically or prompted before recording. Don's condition for accepting the record fallback.
- **Don ranks the in-radio path ABOVE external SDR** for audio fidelity judgment, on three grounds: variable SDR receiver quality, inaccessible SDR bandwidth controls, and band conditions (QSB/QRM) that can block the check outright. **Resolved by Noel 2026-08-08: either tier is fine.** The bandwidth-control objection is about other people's web interfaces, not about SDR — when JJ Flex is the SDR client (KiwiSDR is already on the roadmap) we control the bandwidth and that objection evaporates. Neither tier gets ranked above the other; help text explains what each can and cannot prove. Don's bandwidth-matching requirement carries over to the SDR tier too. Plan §4c.
