# Sprint 32 — copy to approve, and one number to judge by ear

Written 2026-08-19 by the orchestrator, from Track H's completion report.

**Nothing here is blocking.** Every item shipped with existing behaviour intact;
Track H proposed wording rather than inventing it, per the standing rule that
user-facing prose is yours. If you never answer an item, the current text stands.

Answer by number, in any order, a few at a time.

Mark your answers after the `**** ` on each line.

---

## The one that is not copy — it needs your ear, not your judgement

**1. What counts as "a bulk slice change"?**

The CW census now fires once after the slice set settles, rather than once per
slice. "Settled" is currently **1500 ms** of quiet (`SliceSetSettleMs`).

Track H's own words: this is the one number here that wants your ear rather than
reasoning. Too short and a slow connect sends two censuses; too long and the
announcement feels detached from what you just did.

The test is cheap: connect, and release a slice. If you ever hear two censuses
where you expected one, it is too short. If the census feels like it arrives
after you have moved on, it is too long.

**** 

---

## The receipt, when you release a slice

Currently it says, as its own queued sentence following the existing
announcement ("Slice D released, 3 active"):

> This will not survive disconnect unless you save the profile.

**2. Should it name where to go?** You could not find Radio → Profiles, and the
receipt does not name it. Proposed as a Chatty-verbosity-only tail:

> The Radio menu's Profiles item saves it.

Chatty-only so Terse and Normal operators are not read a menu path every time.

**** 

**3. Should it carry the MultiFlex caveat?** Saving the global profile captures
the radio's *whole* state, including another operator's slices if someone else is
connected. That is a real hazard and currently unsaid. It is also a sentence most
operators will never need, since most are the only client.

No wording proposed — the question is whether it belongs at all, and if so
whether it is Chatty-only too.

**** 

---

## New copy Track H introduced, which you have not seen

**4. A guard when selecting a profile the radio does not have.** The old WinForms
dialog refused with *"You must select a global profile currently on the radio."*
The WPF dialog has no guard at all. That old wording also clashes with the
dialog's own voice, which deliberately rewrote "you must select" as *"Pick a
profile in the list first."*

Proposed:

> That profile is not on this radio yet. Save it first.

**** 

**5. A `" (on radio)"` suffix** on merged rig profiles, mirroring the existing
`" (default)"` convention. New copy, but consistent with what is there.

**** 

**6. A `" - not yet implemented"` label suffix**, mirroring the shipped
`" - coming soon"`. This is part of fixing nineteen menu items that announced
their own absence only *after* you had navigated to them and pressed — they now
grey the item and put the state in the label, so the reason arrives with the name
as you pass it.

**** 

---

## A help entry that is now understated

**7. Ctrl+F4.** Repeat-last-message used to hold exactly one message. It now
holds ten, and pressing again within six seconds walks back through earlier ones,
wrapping at the oldest.

Both `KeyCommands` and the keyboard reference still say **"Repeat the last spoken
message"** — which hides the feature, since nothing tells you to press it twice.

Proposed:

> Repeat recent messages, pressing again for earlier ones.

Track G owns that file and will make the edit; this is the wording question.

**** 

---

## For the record, needing no answer

Track H found and fixed three defects nobody had reported, all in the Profiles
dialog: it never showed the radio's own profiles (which also meant the Add
uniqueness check could not see them, so Save would silently overwrite a radio
profile); Delete never persisted, so deleted profiles came back next launch; and
Save vanished entirely on non-global types instead of disabling.

It also found that `MorseNotifier` had no `/` character. Without adding it, your
`3/4` census would have gone out as **"34"** — a silently wrong announcement that
would have sounded plausible.
