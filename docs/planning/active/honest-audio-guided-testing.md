# Guided testing — the honest audio hub and the test tone

**Date:** 2026-08-11. **Build:** Debug x64,
`bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe`, built 13:43 today from
`honest-tx-audio` with Tracks A and C merged.

**What this covers:** everything from today is **compile-verified only —
nothing has ever been launched.** So treat surprises as expected rather than
alarming, and note anything odd rather than fighting it.

**Radio:** the bench 8600. **It has no antenna on any port**, so keep transmit
power low on every keying step. Nothing here needs more than a watt or two.

**Turn tracing on before you start** (Operations → Tracing). Two reasons: it
captures anything that goes wrong, and Part 5 needs the traces this session
produces.

---

## Part 1 — the Audio menu (no transmitting)

The defect being fixed: the old menu's volume items moved the *radio's* jacks,
so a PC-audio operator adjusting "Headphone Level" heard nothing change.

1. Open the **Audio menu**. You should find two groups: **"PC Audio (this
   computer)"** and **"On-Radio Outputs (the radio's own jacks)"**. Both should
   be present at once — that is deliberate, not a bug.
2. In the PC Audio group, find **PC Output Volume up/down**. Adjust it and
   confirm the value **speaks in dB**. Starting value should be **12 dB** —
   that is the loudness you have always had, just now adjustable.
3. **With PC audio playing, does the volume actually change?** This is the
   headline of the whole track. It should apply immediately, no reconnect.
4. Try the range. It goes **0 to 24 dB**. At the top on a strong signal, listen
   for whether it stays clean — a limiter was added, so it should flatten
   rather than turn to hash.
5. In the On-Radio group, confirm the labels all say **"On-radio"** and that
   the three **mutes** are there — headphone, line out, front speaker. Those
   had no menu presence at all before today.
6. Confirm **Audio Workshop** appears on the top-level Audio menu.

## Part 2 — Home's audio group (no transmitting)

7. Arrow through **Home's audio group**. You should find PC Output Volume, Mic
   Level, the on-radio levels, the three mutes, and a **read-only mic-audio
   field**. Before you have transmitted, that field should say something honest
   like "transmit to measure" rather than a fake reading.
8. Check **Settings → Audio**, "Radio Audio Through This Computer" — PC output
   volume should be there too, and should work even with no radio connected.

## Part 3 — the Ctrl+J volume mode (no transmitting yet)

Press **Ctrl+J then V** to enter volume mode. It **stays** in the mode until
Escape — you can switch targets and keep arrowing.

9. Try each target letter and arrow Up/Down on each: **H** on-radio headphone,
   **P** PC output, **M** mic level, **L** on-radio line out, **C** compander
   level, **S** speech processor mode. Every press should speak the new value.
10. Confirm **Escape exits** the mode.
11. Try **`?` inside the mode** — it gives in-mode help (H is a target here, so
    it could not be the help key).
12. Also check the two new toggles outside volume mode: **Ctrl+J then C**
    (compander) and **Ctrl+J then Shift+P** (speech processor).
13. Press **Ctrl+J then H** for the leader's help. It used to be a
    hand-written list that had silently dropped six commands; it is now
    generated from the real registry, so it should read complete and end by
    pointing at F1 and Ctrl+/.

**Four specific judgment calls to form an opinion on** — these are not bugs,
they are choices someone made and you may want reversed:

14. **Arrow announcements use short labels** ("Headphone 55") while selecting
    a target speaks the full name ("On-radio headphone 50"). Is that the right
    trade of speed against clarity, or should both be full?
15. **Mic level now appears twice** in Home — in the Audio group stepping by 5,
    and in the TX group as "Mic Gain" stepping by 1. Two doors, two step sizes.
    Does that grate?
16. **The `[` and `]` filter keys still work inside volume mode** — they are
    consumed before the mode sees them. Harmless leak; worth knowing.
17. **Escape while transmit-locked unkeys instead of exiting volume mode** —
    PTT safety wins. That was judged correct; confirm you agree, since it is a
    safety behaviour.

## Part 4 — transmitting (keep power low, no antenna)

18. **The blocking test.** Key up and, **while still transmitting**, press
    **Alt+Shift+S** (Speak Transmit Status). It should speak the transmit state
    **plus a live mic-audio verdict and peak** — one leading token ("Good.",
    "Hot.", "Quiet.") then coaching and the figure. This is the one item that gates merging to main, and it has
    never run at a radio. If you adjust mic gain while keyed, the verdict should
    follow within about a second and a half.
19. Unkey. Confirm the **Home mic-audio field** now reports your last
    transmission instead of "transmit to measure."

## Part 5 — the test tone (keep power low)

Audio Workshop (**Ctrl+Shift+W**) → TX Audio tab → the new **Test Tone**
section, between Audio Check and Microphone.

20. Arm **"Test tone instead of microphone."** Note it refuses outright, with a
    spoken reason, if PC audio is off or the transmit input is not PC or you are
    in CW mode — that refusal is deliberate, because otherwise a live room mic
    could keep transmitting while you believe you are sending a tone.
21. Key up with the tone armed. **Every transmission should announce that it is
    sending the tone instead of your voice.**
22. **The calibration step, and the most valuable measurement of the day:** with
    the tone at its default **−10 dBFS**, check what the mic-audio verdict and
    peak report. **SC_MIC should read approximately −10 dBFS.** If it does, our
    meter chain is honest end to end and the verdict thresholds are calibrated
    against a known reference. If it reads something else, the offset tells us
    exactly how much the thresholds are wrong by — which is worth knowing either
    way, so this is not a pass/fail step.
23. Try the frequency presets — 440, 700, 1 kHz, and a custom value. Confirm the
    choice **persists** after closing and reopening the workshop.
24. **The passband warning.** Set the tone to something outside your transmit
    filter — try **50 Hz** or **5000 Hz**. You should be warned **out loud when
    you set it, again when you arm, and again at every key-down**. This one
    matters: outside the passband, *nothing goes out*, and a test that silently
    tests nothing is the exact failure this whole arc exists to kill.
25. Try the **"hear the tone while it transmits"** toggle. Known limitation: the
    local monitor rides a 2 Hz timer, so it can lag up to half a second on key
    and unkey. **Local only — the RF side is instant.** Judge whether that lag
    is acceptable.

## Part 6 — the Trace Browser (bonus, if you are still fresh)

This shipped in May and **has never been tested by anyone.** Its checklist in
`agile/sprint29-test-matrix.md` is written and entirely unchecked. Your session
today has generated exactly the traces it needs.

26. **Operations → Tracing → Trace Browser tab.** Does the tab appear and is it
    tab-reachable?
27. Does the **list populate** from the archive with date, radio, outcome and
    duration?
28. Try the **date range filter**, the **outcome filter**, and **text search**.
29. **Sort by a column** — click or activate the date header, confirm the order
    reverses.
30. Select a row and check the **detail panel**, then try **View** (puts the
    trace path on the clipboard) and **Export**.

Leave **Delete** and **Prune** alone unless you want to lose traces — Prune
removes everything older than 30 days.

---

## What to report back

Plain prose is fine. Most useful to me, in order:

- **Anything that crashed, hung, or said nothing when it should have spoken.**
- **The SC_MIC reading from step 22** — the actual number.
- **Your opinion on the four judgment calls** in steps 14 to 17.
- Whether the passband warning in step 24 was genuinely unmissable.
- Whatever you did not get to. Partial is fine; this is a long card and steps
  18 and 22 are the two that matter most.
