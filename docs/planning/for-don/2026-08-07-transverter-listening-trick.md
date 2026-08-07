# Questions for Don — the transverter listening trick

Date: 2026-08-07, updated the same night after we got the trick working on Noel's 8600. From the JJ Flex audio workshop planning session.

Don, good news first: the transverter listening trick works, and we have it recipe'd on the 8600. Noel transmitted at 1 watt into the transverter port with a second slice listening on that same port, and heard his own signal — processing and all — inside one radio, no antennas connected. We're building this into JJ Flex as a one-button "check my audio" feature.

But our recipe needed one thing your radio might not have: we had to turn on the radio's full duplex setting — a hidden switch that lets the receivers keep running while transmitting. On two-SCU radios like the 8600 that switch exists; the regular software only shows it on those models. Your 6300 has one SCU, and Noel remembers you've done this trick before — which means either your radio doesn't need that switch, or your way of doing it is different from ours in a way we really want to understand.

So the questions got sharper. Six of them; short answers, voice memo, or rough notes all welcome.

1. Which radio have you done the transverter listening trick on? Specifically — have you done it on your 6300?

2. When you do it, do you hear yourself live while you are talking, or do you listen back to a recording afterward? (This is the one we care most about — live listening needs the receivers running during transmit; playing back a recording doesn't.)

3. Here is our 8600 recipe. Does it match what you do, and if not, what's different? Transmit antenna set to the transverter port. Second slice, same frequency, same mode, receive antenna also on the transverter port. Power at 1 watt (zero was too low — the loop went silent). Transmit monitor OFF, because with it on you hear yourself twice — once instantly, once delayed through the loop — and it's an echo mess.

4. Did you ever have to change any radio setting to make it work — anything like a full duplex switch, or something in the transverter setup screens?

5. Noel remembers that Jim's original JJ software had something related to this, and that it was buggy or stopped working. Do you remember what Jim's version did, and what was wrong with it?

6. You've been checking your transmit audio against an online SDR lately. Comparing that with the transverter trick and the radio's TX monitor — what does each one tell you that the others don't? And if JJ Flex could do one thing to make checking your audio easier, what would it be?

Thanks, Don. Your answers decide whether this feature works on every Flex or only the two-SCU radios — your 6300 is the datapoint we can't get anywhere else.
