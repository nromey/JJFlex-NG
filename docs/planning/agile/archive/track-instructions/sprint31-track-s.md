# Sprint 31 — Track S — Whose Radio Is It

**Worktree:** `C:\dev\jjflex-31s`  **Branch:** `sprint31/track-s`  **Base:** `honest-tx-audio` @ `01c2d346`
**Model:** opus  **Merges:** FOURTH (last)

You own radio ownership and the silent-transmit failure it gates. Two tasks, and the second one
closes a defect that is live on real radios today.

---

## House rules

**The user is blind and uses NVDA.** He will operate every line you write.

- **No tables, no ASCII art, no diagrams** in anything you write. Prose or bullets.
- **Every control gets `AutomationProperties.Name`.** Keep disabled controls OUT of the tab order.
- **Long explanations go in `JJFlexHelp.Text`, NEVER `AutomationProperties.HelpText`.** NVDA reads
  HelpText aloud as the description on EVERY focus. `JJFlexHelp` (in `JJFlexWpf`) is the on-demand
  Ctrl+F1 channel.
- **A screen reader flushes its speech queue on any window change** — anything crossing a boundary
  is carried by the arriving window, in its Title.
- **Escape closes every dialog.**

**Build:** `dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal`, verify the
exe timestamp. **Tests:** `dotnet test Radios.Tests/Radios.Tests.csproj -c Debug` — 122 at base.

**Commits:** per item, `Sprint 31 Track S: <description>`, push after EVERY commit to `origin`,
NEVER `upstream`. Never `git add -A`.

**Do not edit:** `CLAUDE.md`, `docs/CHANGELOG.md`, `docs/help/md/*` — report that content instead.

**Unattended, owner away.** Never block. Most defensible option, write it down, keep going. End with
**"Needs Noel"**.

---

## Your work

### 1. Task #99 FIRST — the announce-only silent-TX warning

**Do this first. It closes a live failure and it is small.**

**The defect:** a radio whose mic-profile selection is EMPTY leaves the transmit DSP chain
unconfigured, so PC transmit audio modulates nothing and SC_MIC pins at -120. The operator is told
nothing at all. A factory-fresh radio lands here, as does one whose global profile was loaded
without a mic profile. SmartSDR users never see it because SmartSDR keeps "Default" selected.

**Why it stayed invisible:** Noel's own radio HAS a profile selected, so his working setup cannot
detect it. A correct setup is blind to this class of bug — which is exactly why it needs to announce.

**What ships now: the announcement ONLY. No write of any kind.** When a connected radio reports an
empty mic-profile selection, say so — the shape being "this radio has no mic profile selected, so
computer transmit audio will not modulate". Offer no automatic remedy yet.

This is safe on a guest connection precisely because it does nothing to the radio.

**What must NOT ship yet:** branch `diag/don-audio-708` (origin, commit `7b2c427e`) carries nineteen
lines that auto-select a profile when the selection is empty. It is pcap-diffed against SmartSDR and
the mechanism is real — but applying it **writes to shared radio state**, which on a guest connection
silently changes someone else's transmit chain. That is exactly what the ownership flag gates. Read
that commit for the mechanism; do not apply it.

**Wording needs Noel's read before it ships** — it must explain a failure most operators have never
heard of, without alarming anyone whose radio is working fine. Put every candidate string in your
report.

### 2. Task #94 — the ownership model, ratified

Noel ratified the whole design 2026-08-19. Full design in
`docs/planning/design/Mic-Profile-Ownership.md`. Nothing is implemented.

**Ratified points, build to these:**

- **The model.** Ownership is a per-radio flag the operator SETS, stored serial-keyed. Registration
  or discovery may seed a first guess; neither decides it. Unset means guest behaviour — the safe
  default.
- **Two destinations, two verbs.** A Workshop preset stays PC-side and safe on anyone's radio.
  Writing to the radio is a separate, explicitly named action, surfaced only on radios marked yours.
- **How it feels: BOTH** — a field on the per-radio Settings panel, AND an ask at the first moment an
  action needs it.
- **Bindings keep working regardless of the flag.** Applying a profile applies the PC half always,
  and the radio half only where a binding for THIS radio already exists — the binding is the
  consent. The flag gates creating NEW radio-side state.

**Why ownership cannot be derived, which you will be tempted to shortcut:** Noel connected to
Margaret's radio USING MARGARET'S ACCOUNT. To SmartLink he WAS the owner. Registration answers who
has ACCESS, not whose radio it is, and those diverge the moment anyone helps anyone else. A LAN-only
radio has no registration at all. Don's 6300 lives at Tony's house — local to Tony, remote to Don,
unambiguously Don's — so physical location does not settle it either.

**Noel's question, and the answer to encode in the design rather than in code:** *"how do we keep
users from noting that they own the radio?"* We do not, and should not try. Ownership is a
**declaration of intent, not a security control**. The app is not defending against a malicious
operator; it protects an honest one from an accident. Anyone who deliberately marks another
person's radio as theirs has taken that responsibility knowingly. Do not build enforcement.

**Where the flag lives:** `Radios\RadioConfig.cs`, serial-keyed, beside `SmartLinkIntents` and
`RemOnOnConnect` which Track A already shipped there. Follow those as the pattern.

**Once the flag exists**, wire the silent-TX remedy per the ratified design: applies silently on
radios marked yours, becomes an OFFER on any other radio, never fires unasked on someone else's rig.

**Out of scope, do not attempt:** whether one radio can be registered to two SmartLink accounts.
That is bench-gated (task #95) and needs Margaret's radio. If registration turns out to be
exclusive, registering a friend's radio would silently evict them — a hazard the app must refuse
without warning, but that is a later task.

---

## Files you own

`Radios\RadioConfig.cs` (the ownership flag — see collisions), `Radios\MicrophoneProfile.cs`,
`Radios\AudioChainPreset.cs`, `Radios\FlexBase.cs` (mic-profile selection and the empty-selection
detection), `JJFlexWpf\Dialogs\AudioWorkshopDialog.xaml` + `.xaml.cs`,
`JJFlexWpf\Dialogs\SettingsDialog.RadioProfile.cs` (the ownership field).

## Collisions

- **`Radios\RadioConfig.cs` — you and Track P.** P touches path-learning and radio-removal fields;
  you add the ownership flag. Different regions, but **P merges FIRST**, so you resolve. You are
  last in the train deliberately — you are the strongest reasoner available for that resolution.
- **`SettingsDialog.*` — P merges first, R may add one line.** Keep your ownership field self-contained.
- Do NOT touch `globals.vb`, `MainWindow.xaml.cs`, `KeyCommands.cs`, or the diagnostics surface.

## Merge position

**FOURTH and last.**

## Your report

What landed per item, **every candidate string for the silent-TX warning** (this one genuinely needs
Noel's read before shipping), how the ownership ask is surfaced, what you deliberately did not
build, changelog lines in the house voice, doc content for the help pages, and **Needs Noel**.
