# Sprint 30 — every open question, in one place

Written 2026-08-19. This replaces the two 2026-08-18 files as the place to
answer; both are kept because they hold the full reasoning, and each question
below points at its detail.

Nothing here is blocking. Every track proceeded on a stated assumption and
shipped, so a question you never answer just leaves the current behaviour
standing. Answer by number, in any order, a few at a time — no need to work
through it in one sitting.

Where I have a recommendation it says so. Where a track argued against what I
recommend, that is said too.

---

## Already answered

**Unanswered crash reports: one week.** Ruled 2026-08-19, over Track D's
recommendation against shortening. Implemented and pushed. Worth knowing what it
actually means: the newest three reports are never touched by age, verdict or
folder cap, so the week governs only the fourth unresolved report and older. If
it ever feels tight, raise `KeepCrashReports` rather than the window — it is
operator-configurable, and it protects more reports outright instead of delaying
the same pile-up.

---

## Connect, Home, and the rescue page (Track A)

**1. Is the rescue page's button set right?**
Shipped as Connect, Settings, Audio Workshop, Help, Exit. Deliberately not
included: Radio Setup, and Track D's new diagnostics surface. Logging is absent
as a button but reachable with Ctrl+Shift+L.
*If you want more or fewer, say which.*

**2. Should the rescue page also appear when a radio drops mid-session?**
Not built — scope was startup only. The old `RestoreNoRadioShell` path still
handles a mid-session loss, and it leaves the frequency display visible and
focuses it. So the two paths now describe "no radio" differently, which is the
real reason to decide this.
*My recommendation: yes, eventually, but not as a quick follow-up — it is a
window transition during live operation, so every speech-flush lesson applies.*

**3. Does `FeatureLicense` populate at all on a purely local connect with no
SmartLink account?**
Cannot be settled from code. Track A traces the first verdict per radio now, so
one local session with tracing on answers it. The fallback is already safe: a
licence never reported produces no "unavailable" claim, because we do not know.
*This is a "run one session" item rather than a decision.*

**4. Is three the right threshold for learning a connection path?**
Three consecutive successful connects on the same path makes it the prefill.
Bounded above by the store: the history ring holds ten attempts and a
chain-walking connect writes two, so much past four is unreachable for a radio
that falls back.

**5. Is "local only" an app-side setting?**
Assumed yes and built that way — nothing is written to the radio, nothing asked
of SmartLink. It is stored per radio, keyed by serial, on this computer only.

**6. Should Logging mode be reachable from the rescue page?**
It is, via Ctrl+Shift+L, and Logging mode overrides the page. Track A's
reasoning: the log is a logbook, works offline, and suppressing it would make
the page wrong about its own claim to offer only what works.
*Say if you would rather Logging waited for a radio.*

**7. Test Tone is now treated as radio-side, and disabled offline.**
The old code comment listed it as offline-safe; the code disagrees — arming it
writes `rig.TxToneFrequency` to the radio's transmit chain. Flagged because it
changes what the Workshop offers on the rescue page.

**8. Live Meters are now gated offline.**
Beyond the literal #90 remainder. Every box read "S-Meter: no reading yet" with
no radio, which is a promise rather than a fact.
*Say if you would rather they stayed reachable for inspection.*

**9. The auto-started remote pass no longer shows its connecting window.**
The biggest behaviour change in #85, and the one most wanting your ear. That
window's stated job was holding focus during SmartLink auth. Track A's
judgement: background work must not take the foreground, and interactive sign-in
brings its own window. Needs testing on a real account with auto-start enabled.

**10. There is no way to reject a learned prefill and return to plain
automatic.**
To overrule the trend you pick an explicit order, which is a stored choice and
always wins. Clean, but it means the automatic option can never be returned to
its un-learned meaning.
*Say if you want a fourth option in the combo.*

**11. Track A softened one shipped user-facing line.**
"Registering your radio... tells Flex the radio is yours" became "Registering a
radio lets that account reach it over the internet", following the finding that
registration answers access, not ownership. This is your prose, so it wants your
read.

---

## Diagnostics (Track D)

Detail: `docs/planning/for-noel/2026-08-18-diagnostics-three-rulings.md`.

**12. Is the failure-offer list right?**
Four kinds interrupt you: a setting that would not save, a connection to a named
radio that failed, audio that would not open, and the reporting pipeline itself
failing. Six deliberately do not: crashes (the crash reporter already prompts,
with a fuller bundle), empty discovery, login and token rejections, anything a
retry absorbed, corrupt preset files, and firmware download failures.

The login exclusion is the one worth a second look, because it is a privacy call
rather than a UX one: the diagnostic log carries your SmartLink email and JWT
fragments, so exporting on an auth failure costs something and diagnoses nothing.

Also part of this: at most two offers per session, one offer per kind, silent
while transmitting, and one "Not now" ends offers for the session.

**13. Delete the retired trace dialog now, or after 4.1.17?**
Help → Tracing is gone from the menu, but the dialog itself was kept — corrected
rather than deleted — because the Settings dialog is this sprint's most likely
rollback candidate and the old dialog is the fallback. Say the word and it goes.

---

## Mic profiles and radio ownership (Track B)

Detail: `docs/planning/for-noel/2026-08-18-mic-profile-ownership-questions.md`
and `docs/planning/design/Mic-Profile-Ownership.md`. Nothing here is
implemented; the flag lives in a file Track A owned this sprint.

**14. Ratify the ownership model?**
Ownership is a per-radio flag you set, stored serial-keyed. Registration or
discovery may suggest a first answer; neither decides it. Unset means guest
behaviour, which is the safe default. The Margaret test is what killed deriving
it: you connected to her radio using her account, so to SmartLink you *were* the
owner.

**15. Two destinations, two verbs?**
Saving a Workshop profile stays PC-side and safe on anyone's radio. Writing to
the radio becomes a separate, explicitly named action that appears only on
radios you have marked as yours. Costs one extra step on your own rig; buys
never having to think about whose radio you are on.

**16. How should the ownership question feel?**
Ask once at the first moment an action needs it, a field on the per-radio
Settings panel, or both. Track B's preference is both.

**17. The silent-TX auto-select — apply, offer, or park?**
This is the one with teeth. An empty mic-profile selection means PC transmit
audio modulates nothing, and the operator is told nothing. **Your own radio
cannot detect this, because your radio has a profile selected** — which is
exactly why it stayed invisible. The fix exists on `diag/don-audio-708` but
writing it touches shared radio state.

Proposed: silent on radios you own, an offer on any other radio, parked until
the flag exists.
*The sub-question worth answering even if you defer the rest: should an
announce-only version ship now — no write, just "this radio has no mic profile
selected, so computer transmit audio will not modulate"? That closes a live
silent failure without touching anyone's radio.*

**18. Bless bindings-keep-working-regardless-of-flag?**
Applying a profile applies your PC half always, and the radio half only where a
binding for *this* radio already exists — the binding you created is the
consent. Track B recommends keeping that, but it is a write to radio state that
a bare reading of the ownership flag would forbid, so it wants explicit
blessing.

**19. Can one radio be registered to two SmartLink accounts?**
Unknown, and it matters beyond ownership: if registration is exclusive, then
registering a friend's radio would silently evict them, which the app should
refuse to do without warning. Margaret's radio is the ready-made test. Folded
into task #95, bench-gated.

---

## Still to come (Track E, running now; Track F, not yet started)

**20. Should an earcon quick-mute silently outlive the session?**
It does today, and the help page says it does not — wrong twice over, since the
mute persists both by an immediate save on toggle and by shutdown capture. Track
E has to make the code and the page agree, and needs to know which way.

**21. Is `audio-earcon-control.md` a spec to build to, or does the promised
category list need revising first?**
I have already told Track E to treat the page as SUSPECT and report rather than
faithfully build a wrong list, because reality has drifted further from it than
the task said. Confirm or override.

**22. How deep should the repeat-last-message history be, and on what keys?**
Track F, not yet started. Proposal: ten.

**23. May Track F spend live-session minutes on the one protocol-bound Shift+Tab
attempt (#89)?**
Four failed attempts is a stop signal. If you say yes, the protocol is: establish
a known-good commit or prove none exists, instrument focus events, one attempt
verified by NVDA on the spot, then stop either way. You may also veto it
outright. There is one cheap piece of evidence available first — start the
IdentityExpander expanded and press the key, which discriminates the leading
suspect in one build without touching navigation code.

**24. When is the bench day (#56)?**
It gates receiver simulation (#10), transverter session one (#27), the four
mystery slices (#59), and now the registration-eviction test (#95). None of them
block anything else.

---

## For information — no answer needed

- **Two silent absences are still standing**, now task #96: ESC has no menu item
  anywhere on any radio, and ATU items vanish entirely on a radio without one.
  Both are the same shape as the diversity and advanced-NR cases Track A fixed;
  ATU is a hardware gate rather than a licence gate, which is why it was left.
- **The Saved Diagnostic Logs browser existed and had no door.** Built in Sprint
  29, and nothing ever instantiated the form hosting it — which is why its
  checklist sat unticked and nobody had ever seen it. It is reachable now.
- **Gathering a problem report used to switch your logging off** for the rest of
  the session. Fixed.
- **Rigmeter's authorship numbers changed.** With `-w -C` and a Unicode decode
  fix, roughly 800 lines moved from you to Jim, about 500 of them in `Radios`.
  The repo-wide split is now 73.6 percent you, 26.4 percent Jim.
