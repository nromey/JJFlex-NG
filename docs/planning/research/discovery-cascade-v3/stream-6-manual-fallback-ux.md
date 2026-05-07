---
title: Stream 6 — Manual fallback UX
stream: 6
status: research output (round 1)
date: 2026-05-06
author: Claude (research agent)
related-design: docs/planning/design/discovery-fallback-chain.md (round 2, ACK'd 2026-05-04)
parent: docs/planning/research/discovery-cascade-v3/README.md
related memories:
  - project_flexibility_principle.md
  - project_friction_tax_principle.md
  - project_no_silent_keystrokes_rule.md
  - project_dialog_escape_rule.md
  - project_anti_patterns_from_blindcat.md
  - project_jjflexible_home_terminology.md
  - project_no_silent_phone_home.md
  - project_smartlink_login_silent_validation_bug.md
research-question: |
  Design the UI for the discovery cascade's last-resort manual fallback. This is what
  the user sees when every automated rung (cached IPs, ARP, subnet probe, mDNS, SSDP,
  NetBIOS, UDP broadcast) failed. Must be screen-reader-first (NVDA + JAWS), assume
  the user may not know what an IP address is, may have multiple radios, may be unable
  to read the front panel, and must offer escape paths per the no-stuck-modal rule.
---

# Stream 6 — Manual fallback UX

## Executive summary

Five top design decisions, in priority order:

1. **Layout A wins: single dialog with progressive-disclosure regions (no wizard).** A wizard forces the user through steps they may not need; a single tabbed/regioned dialog lets the common case ("I already know my radio's IP") finish in three keystrokes while still surfacing help to the user who needs it. Wizards also fight screen reader users — every step transition is an orientation cost. Layout B (wizard) is documented for comparison and rejected.

2. **The dialog never opens cold.** It is preceded by a "We couldn't find your radio. Would you like help finding it?" plain-language prompt with three buttons: *Yes, help me* (opens the manual fallback dialog), *Let me try again* (re-runs the entire cascade), *Cancel* (closes everything, returns to the empty radio list). The fallback dialog is itself the *help-me* destination — never an unsolicited modal. This satisfies friction-tax (don't surface complexity until the user opts in) and the no-stuck-modal rule (every entry path has an obvious exit).

3. **"Show last-known IPs" is the load-bearing affordance, not the IP entry field.** The most likely path to success in the manual fallback is *"the cascade scraped a candidate but couldn't confirm it"* — that's not a failure of finding; that's a failure of confirming. Surface the candidates as a focusable list FIRST, with the manual entry field below. Most users will pick a row and never type an octet. Help text frames this honestly: "Your radio has probably been here before. Pick from the list, or type its address if you know it."

4. **Help text is structured by user-capability ladder, not by data source.** Don't list "the radio's front panel, the router admin page, the Maestro" as parallel options — they aren't parallel for a blind user. Group as: *(a) what JJ Flex already knows, (b) what you can ask another device or person, (c) what you'd ask a sighted helper to read for you, (d) what you'd ask FlexRadio support.* This frames the situation by what the user can actually do, not by where the bytes live.

5. **No silent rung failures, no silent button presses.** Every cascade-failure error (Error catalog section) is spoken at Critical verbosity when first surfaced AND visible in the dialog's diagnostic region. Every button press in the dialog produces speech feedback per `project_no_silent_keystrokes_rule.md`. The "Scan my LAN now" button produces continuous progress speech ("Scanning, 50 of 254 addresses checked…") so the user is never wondering whether the app froze.

Bonus: this dialog is JJ Flex's first concrete user-visible expression of the **Don't ask the user for an IP address until you absolutely must** principle. The cascade is the substance; this dialog is the apology.

---

## Layout proposal A — Single dialog with progressive-disclosure regions (RECOMMENDED)

### High-level shape

A single non-resizable WPF `JJFlexDialog` (subclass of `Window`), 560×620, titled **"Find My Radio."** It contains five vertically-stacked regions:

1. Top — **explanation banner** (read-only TextBlock).
2. Middle-upper — **"Last-known addresses" list** (focusable ListBox with column header).
3. Middle — **manual IP entry field** + **Test connection** button.
4. Middle-lower — **action buttons row** (Scan my LAN now / Show me how to find it / Try cascade again).
5. Bottom — **diagnostic region** (collapsed by default; "Show details" expander).

Plus the standard footer: **Save and Connect / Cancel**.

The dialog is **not a wizard**. There are no Next/Back buttons. Every region is reachable in tab order from the moment the dialog opens. The user can:
- pick a row from the last-known list and press Enter (3 keystrokes from open: Tab to list, arrow to row, Enter)
- type an IP and press Enter (Type, Tab to Save and Connect, Enter)
- press one of the three action buttons (open help, scan LAN, retry cascade)
- press Escape to close everything

### Region-by-region spec

#### Region 1 — Explanation banner

```
TextBlock (read-only, multi-line, no border, no Tab stop)
Text:
  "We couldn't find your radio automatically. We tried six different ways to
   reach it on your network. Below are some addresses your radio has used
   before. Pick one if you recognize it, or type the address if you know it.
   If you don't know what an address is, the 'Show me how to find it' button
   will explain in plain language."
AccessibleName: same as Text (set explicitly via AutomationProperties.Name)
AccessibleRole: StaticText
TabStop: false
Foreground: SystemColors.ControlText (so it inherits dark-mode + high-contrast)
```

**Reasoning:** Screen readers do read TextBlocks as the user tabs into the dialog only if they're in the Window's logical tree at load time. Setting an explicit `AutomationProperties.Name` ensures NVDA/JAWS announces the full banner via the dialog's `Loaded` event (the `JJFlexDialog` base class already speaks the Title; we extend that pattern by speaking the banner immediately after Title for context).

**On dialog Loaded:** speak `"Find My Radio. We couldn't find your radio automatically. There are 3 saved addresses. Press Tab to pick one or type the address."` — this is the orientation message. The number of saved addresses is dynamic. If zero saved addresses: `"Find My Radio. We couldn't find your radio automatically. No saved addresses. Press Tab to type the address, or to find help."`

#### Region 2 — Last-known addresses list

A ListBox showing every IP candidate the cascade scraped from any source, deduplicated, ordered by recency-of-last-success.

```
Label (TextBlock):
  Content: "Saved addresses for your radios"
  AccessibleName: "Saved addresses list. {N} items."
  Where {N} updates dynamically.

ListBox (RadiosBox-style, IsTabStop=true):
  AccessibleName: "Saved radio addresses"
  AccessibleRole: List
  KeyboardNavigation.TabNavigation: Once
  Each item is a RadioCandidate with DisplayText:
     "{Nickname} — {Model} — {IP} — last seen {RelativeTime} — from {Source}"
  Example:
     "K5NER's Shack — FLEX-6600M — 192.168.1.42 — last seen yesterday — from JJ Flex"
     "(unknown nickname) — FLEX-6300 — 192.168.1.105 — last seen 3 days ago — from SmartSDR"
     "K5NER's Shack — FLEX-6600M — 10.0.1.42 — last seen 2 weeks ago — from Hamlib config"
  Empty state: "No saved addresses yet. Use the field below to type one."

Below the ListBox, two adjacent buttons (visible only when the list has items):
  Button "Connect to selected" (Alt+N, IsDefault=true while ListBox has focus)
  Button "Forget this address" (Alt+F)
```

**Source labels** are deliberately friendly:
- `from JJ Flex` (autoConnectV2.xml or equivalent)
- `from SmartSDR` (FlexRadio Systems config in %AppData%)
- `from Hamlib config` (rigctld.conf etc.)
- `from N1MM Logger` (N1MM config files)
- `from WSJT-X config` (wsjtx.ini)
- `from your network's recently-seen devices` (ARP table)
- `from previous manual entry` (user typed it last time)

**Why surface source:** a blind user's debugging path may include reasoning like "I uninstalled SmartSDR but JJ Flex is suggesting an old SmartSDR address — I should pick one of the JJ Flex entries." Surfacing source converts opaque "5 candidates, dunno which" into "I can choose the right one because I know where it came from."

**Why include "Forget this address":** stale entries pile up over time. Users who change ISPs, move to a new home, or replace their radio need a way to remove dead candidates. Honors flexibility principle (`project_flexibility_principle.md`) — user controls their own data.

**Selection behavior:** Single-select. On selection change, speak `"{Nickname} — {Model} — {IP} — last seen {RelativeTime}"` — full descriptor each time, since blind users have no peripheral vision to scan list state. Per the existing `RigSelectorDialog` pattern (`RigSelectorDialog.xaml.cs:475-490`), the selection is announced when ListBox has focus.

#### Region 3 — Manual IP entry field

```
Label (TextBlock):
  Content: "Or type your radio's address here:"
  AccessibleName: "Manual IP address entry"

TextBox:
  AccessibleName: "Radio IP address"
  AccessibleRole: Text
  Hint text in label below: "Example: 192.168.1.42"
  Validation: live as user types; show validation message in adjacent
    TextBlock when invalid; do NOT block typing (input masks fight screen
    readers — see "Why no input mask" below).

TextBlock (validation feedback, AutomationProperties.LiveSetting=Polite):
  AccessibleName: dynamic — empty when valid; speaks updates politely
  Examples:
    "" (valid, nothing announced)
    "Address is incomplete. An address has four parts separated by dots."
    "192.168.1.500 — that fourth number is too big. Each part must be 0 to 255."
    "Looks good. Press Tab to test or Enter to connect."

Button "Test connection" (Alt+T, MinWidth=120)
  AccessibleName: "Test connection to typed address"
  IsEnabled: only when validation passes
```

**Why no input mask:** Input masks (`192.___.___.___` style) are accessibility hostile. Screen readers either narrate the mask characters as "underscore underscore underscore" (annoying) or skip them silently (confusing — the user can't tell what the field expects). Use plain text input + live polite validation feedback instead. Reference: Carbon Design System's text-input accessibility guidance discourages input masks for non-sighted users; web pattern surveys reach the same conclusion.

**Why "Test connection" before "Save and Connect":** blind users have no visual cue that an address is reachable. A separate Test button (which performs a quick TCP/4992 handshake against the typed IP, with progress speech and a clear pass/fail announcement) lets the user verify before committing. If Test passes, the dialog speaks `"192.168.1.42 answered as a FLEX-6600M, serial 1234-5678. Press Save and Connect to use this radio."` If it fails, it speaks the failure reason at Critical verbosity (see Error catalog).

**Live validation rules** (in priority order, only the first failing rule is announced):
1. Empty: silent (no validation message until user starts typing).
2. Contains characters other than digits and dots: `"Address can only contain numbers and dots."`
3. Fewer than three dots: `"Address is incomplete. An address has four parts separated by dots."`
4. More than three dots: `"Too many dots. An address has exactly four parts."`
5. Any octet > 255: `"That fourth number is too big. Each part must be 0 to 255."` (or whichever octet)
6. All octets are 0: `"All zeros isn't a valid address."`
7. First octet is 127: `"127.something is your own computer, not a radio."`
8. First octet ≥ 224: `"Addresses starting with 224 or higher aren't used for normal devices."`
9. Address looks like a public/internet IP (not in 10.x, 172.16-31.x, 192.168.x, 169.254.x, 100.64-127.x): `"Looks like an internet address. Most radios use a local address starting with 192, 10, or 172. Is this what you meant?"` (warning, not error — let them proceed, since SmartLink users *do* have public-IP radios).
10. Valid private IP: `"Looks good. Press Tab to test or Enter to connect."`

#### Region 4 — Action buttons row

Three side-by-side buttons, full-width row:

```
Button "Scan my LAN now" (Alt+S, MinWidth=140)
  AccessibleName: "Scan my LAN now to find radios"
  When clicked: trigger Rung 2 (subnet probe) manually. Display progress in
  Region 5 diagnostic area. Speak progress every 25 addresses ("Scanning,
  25 of 254 checked.") and at completion ("Found 1 radio at 192.168.1.42.").

Button "Show me how to find it" (Alt+H, MinWidth=160)
  AccessibleName: "Show me how to find my radio's address"
  When clicked: opens the FindIpHelpDialog (sub-dialog, see Help text catalog).
  This is the heart of the help-text catalog below — explains plain-language
  how to find an IP via every available source.

Button "Try cascade again" (Alt+R, MinWidth=140)
  AccessibleName: "Try the automatic search again"
  When clicked: closes this dialog, re-runs the entire cascade from Rung 1
  (e.g., user just fixed their network and wants to retry).
```

**Why these three together:** they're the user's three escape valves from the manual-entry scenario. *Scan* is "I'll wait while you try harder." *Show me how* is "I need help understanding what to type." *Try cascade again* is "I think I fixed something — try the easy way again."

#### Region 5 — Diagnostic region (collapsible)

```
Expander:
  Header: "Show details about why we couldn't find your radio"
  AccessibleName: "Show diagnostic details"
  IsExpanded: false
  When expanded:
    Multi-line read-only TextBlock with the cascade-failure error catalog
    output (see Error catalog section).
    AcceptsReturn: true, IsReadOnly: true, VerticalScrollBarVisibility: Auto
    AccessibleName: "Cascade diagnostic. {N} lines."

Below expander, a single-line "Copy details to clipboard" button:
  Button "Copy details" (Alt+P)
    AccessibleName: "Copy diagnostic details to clipboard"
```

**Why collapsed by default:** most users never need it. But for the user who's about to ask a friend or FlexRadio support for help, having the full diagnostic on the clipboard is exactly what they need. Honors `project_no_silent_phone_home.md` — the diagnostic stays local; the user manually shares it.

#### Footer — Save and Connect / Cancel

```
StackPanel (orientation Horizontal, HorizontalAlignment Right, bottom of dialog):
  Button "Save and Connect" (Alt+N, IsDefault=true, MinWidth=140)
    AccessibleName: "Save this address and connect"
    IsEnabled: only when validation passes for typed IP OR a list row is selected
  Button "Cancel" (Alt+C, IsCancel=true, MinWidth=80)
    AccessibleName: "Cancel and close this dialog"
```

**Save and Connect behavior:**
- If a list row is selected, use that row's IP + serial.
- Else use the typed IP. (Test the connection first to learn the radio's serial / model. If Test failed, prompt: `"This address didn't answer. Save it anyway and connect later, or cancel?"` — gives user a way to save a known-correct-but-currently-offline IP.)
- Persist the IP into `autoConnectV2.xml` (or equivalent v3 cache) tagged `from previous manual entry`.
- Speak `"Saving 192.168.1.42 and connecting to your FLEX-6600M."`
- Close dialog, dismiss the cascade-failure flow, hand off to the standard "connecting" form.

**Cancel behavior:**
- Speak `"Cancelled. No radio connected."`
- Close dialog. Return to the empty `RigSelectorDialog` state. User remains in JJ Flex with no active radio.

### Tab order

The order of focus traversal when the dialog opens, from first Tab onward:

1. (focus starts on) **Region 2 ListBox** — if at least one candidate exists. If empty, skip to step 4.
2. *(within ListBox: arrow keys navigate items; Enter selects)*
3. **"Connect to selected"** button (visible only when ListBox has items).
4. **"Forget this address"** button (visible only when ListBox has items).
5. **Region 3 TextBox** (manual IP entry).
6. **"Test connection"** button.
7. **"Scan my LAN now"** button.
8. **"Show me how to find it"** button.
9. **"Try cascade again"** button.
10. **Diagnostic Expander** ("Show details").
11. **"Copy details"** button (only reachable inside expander once expanded).
12. **"Save and Connect"** button.
13. **"Cancel"** button.

The Region 1 explanation banner is **NOT** in tab order (TabStop=false). Its content is spoken at dialog Loaded; it's not interactive. Same for Region 5 when the expander is collapsed.

If no candidates exist (fresh install), tab order starts at Region 3 TextBox. The dialog's Loaded speech adapts: `"Find My Radio. No saved addresses. Type your radio's address, or press Show me how to find it for help."`

### Keyboard shortcuts

| Key | Action |
|---|---|
| `Tab` / `Shift+Tab` | Walk focus order |
| `Escape` | Cancel dialog (`JJFlexDialog` base class handles this) |
| `Alt+F4` | Cancel dialog (Windows standard) |
| `Enter` | If focus is on ListBox row → Connect to selected. If focus on TextBox → Save and Connect (when valid). Else → activate default button (Save and Connect). |
| `Alt+L` | Focus the **L**ast-known list (skip-to shortcut) |
| `Alt+I` | Focus the manual **I**P entry field (skip-to) |
| `Alt+S` | **S**can my LAN now |
| `Alt+H` | Show me **h**ow to find it |
| `Alt+R` | Try cascade again (**r**etry) |
| `Alt+T` | **T**est connection |
| `Alt+N` | Save and Co**n**nect (this matches the existing `RigSelectorDialog` "Connect" mnemonic at line 53; users develop muscle memory for `Alt+N` = "Connect") |
| `Alt+C` | **C**ancel |
| `Alt+P` | Co**p**y diagnostic details |
| `Alt+F` | **F**orget the selected address |
| `F1` | Open context-sensitive help — opens the same `FindIpHelpDialog` as Alt+H |
| `Ctrl+/` | Command Finder (existing JJF pattern, opens overlay) |

**Mnemonic conflict check:** Alt+N for both "Save and Connect" and (in current `RigSelectorDialog`) "Connect" is intentional — same semantic, same key. Alt+S in `RigSelectorDialog` is "Switch Account"; this dialog never has a Switch Account button so no collision. Alt+T in `RigSelectorDialog` is "Test"; same semantic here; reuse is correct.

### Visual design

- High contrast: inherit `SystemColors.ControlText / ControlBrush` so dark mode + high-contrast themes work without per-theme work.
- Focus indicators: keep WPF default focus rectangle (do not set `FocusVisualStyle="{x:Null}"`).
- Font sizing: inherit OS default. Do not hard-code `FontSize`. (The codebase's `RigSelectorDialog` and `AutoConnectFailedDialog` follow this pattern.)
- Layout uses `Grid` (not `StackPanel`) for the outer shell so tab order matches visual order even at large font sizes.
- Minimum dialog size 560×620; users with display scaling ≥ 200% may need to resize — set `ResizeMode="CanResizeWithGrip"` for this dialog only (not the codebase default `NoResize`), since long help text expanding into Region 5 may exceed the height in scaled environments.

### Behavior on dialog open

1. `JJFlexDialog.Loaded` runs (existing base class behavior).
2. Override `FocusFirstControl` to focus the ListBox if non-empty, else the TextBox.
3. After base speaks `Title` ("Find My Radio"), speak the orientation message (varies by candidate count).
4. Begin polling validation on the TextBox (only after the first character is typed — no premature feedback).
5. If clipboard contains text that looks like an IP address (validation pass), offer to paste it: speak `"You have an address on your clipboard. Press Ctrl+V in the address field to paste it."` — friction-tax win for the common case where the user just looked up the IP somewhere else.

### Behavior on Save and Connect

1. Speak `"Saving and connecting…"` at Critical verbosity.
2. Persist IP/serial/nickname to v3 cache.
3. Close dialog.
4. Hand off to existing `ConnectingForm` (the cancel-able connecting modal documented at `ConnectingForm.vb:29-414`).

### Behavior on Cancel / Escape

1. `JJFlexDialog` base sets `DialogResult = false` and closes.
2. The caller (`wpfSelectorProc` or equivalent) returns to the `RigSelectorDialog` empty state.
3. Speak `"Cancelled. No radio connected. Press Remote for SmartLink, or Try Again for local search."` so the user knows where they landed.

---

## Layout proposal B — Wizard (REJECTED, documented for comparison)

### Shape

A multi-step wizard, 480×400, titled **"Find My Radio."** Steps:

- **Step 1 — Welcome.** "We couldn't find your radio. Let's find it together." Two options: *Skip wizard, I know my IP* (jumps to Step 4), *Walk me through it*.
- **Step 2 — Pick from saved.** ListBox of candidates. *Next* / *None of these* / *Back*.
- **Step 3 — Try one more search.** "Want me to scan your network one more time?" *Yes, scan now* (runs Rung 2, 3-5 seconds) / *No, skip*.
- **Step 4 — Type address.** TextBox + Test button. *Next* (when valid) / *Back*.
- **Step 5 — Confirm and connect.** Summary of what will happen. *Save and Connect* / *Back* / *Cancel*.

### Why rejected

1. **Wizards add orientation cost on every step transition.** A blind user has to relearn the page each time. Five steps = five orientation cycles. The single dialog has one.
2. **The expert-skip path doesn't actually skip.** "Skip wizard, I know my IP" still requires the user to land on a step, parse it, and act. A non-modal dialog with a focused entry field is a one-action skip.
3. **Wizards encourage developers to gate later steps on earlier ones.** The single-dialog pattern keeps the user in control of which affordance they use first.
4. **Wizard back-buttons fight screen-reader history.** When the user presses Back, NVDA/JAWS announce the new step's content but the user has to mentally diff against the previous step. The single dialog never has this problem.
5. **Empirical: BlindCat590's wizard is one of the BlindCat anti-patterns** referenced in `project_anti_patterns_from_blindcat.md`. We deliberately don't repeat it.
6. **Sighted-user focus testing has shown wizards cause higher abandonment** for power-user tasks (entering known data). This is a power-user task — the user already failed cascade and is looking for the fastest path to a working connection.

The wizard pattern is appropriate for *first-launch onboarding* (where it's already used by `WelcomeDialog.xaml`), but not for an error-recovery dialog. See "First-launch integration" below for how the manual-fallback dialog plugs into the existing welcome flow.

### When a wizard would be the right call

If usability testing on Layout A reveals novice users overwhelmed by the dense single-dialog layout, an **opt-in wizard mode** could be added later — a single button on Layout A labeled *"Walk me through this step by step"* that opens Layout B as a wrapper. This preserves Layout A as the default (zero added cost) while offering wizard-style guidance to users who request it. Defer until evidence demands it.

---

## Help text catalog

Every user-facing string in the dialog and its sub-dialog. Ready to paste into resource files when the localization-prep architecture (`project_localization_strings_file.md`) lands.

### Top-level prompt (precedes the manual fallback dialog)

**Triggered when:** the cascade has exhausted all automated rungs and is about to surface the manual fallback.

```
Title: We couldn't find your radio
Body:
  We tried six different ways to reach your radio on your local network and
  none of them worked. This usually means one of three things:

  • Your radio is powered off or still booting up.
  • Your radio is on a different network than this computer.
  • Your radio is on the network but our discovery messages didn't reach it.

  Would you like to:

  [ Help me find it ]   [ Try again ]   [ Cancel ]
```

**Speak on open:** `"We couldn't find your radio. Three buttons: Help me find it, Try again, Cancel."` at Critical verbosity.

**Buttons:**
- *Help me find it* (Alt+H, IsDefault=true) — opens the manual fallback dialog (Layout A).
- *Try again* (Alt+T) — closes this prompt, re-runs the entire cascade.
- *Cancel* (Alt+C, IsCancel=true) — closes this prompt and the cascade entirely; returns to the empty `RigSelectorDialog`.

### Find My Radio dialog — Region 1 explanation banner

```
We couldn't find your radio automatically. We tried six different ways to
reach it on your network. Below are some addresses your radio has used
before. Pick one if you recognize it, or type the address if you know it.
If you don't know what an address is, the "Show me how to find it" button
will explain in plain language.
```

### Find My Radio dialog — orientation speech (varies by candidate count)

- N=0: `"Find My Radio. No saved addresses. Type your radio's address, or press Show me how to find it for help."`
- N=1: `"Find My Radio. One saved address. Press Tab to pick it, or type a different address."`
- N≥2: `"Find My Radio. {N} saved addresses. Press Tab to pick one, or type the address."`

### Find My Radio dialog — TextBox label and validation strings

(See Region 3 spec for the live-validation strings — duplicated verbatim there for layout context.)

### "Show me how to find it" sub-dialog (FindIpHelpDialog)

A second `JJFlexDialog`, 580×640, titled **"How to find your radio's address."** Single read-only TextBox styled with TextWrapping=Wrap, AcceptsReturn=true, IsReadOnly=true, VerticalScrollBarVisibility=Auto. Plus an "OK" button at bottom.

Body text:

```
HOW TO FIND YOUR RADIO'S ADDRESS

Every radio on a network has a number called an "IP address" — like a phone
number for the radio. JJ Flex normally finds this number on its own, but
sometimes it can't. Here's how to find it yourself.

────────────────────────────────────────────────────────

WHAT WE ALREADY KNOW

Before you go looking, check the list in the previous window first. JJ Flex
remembers every address your radio has used before, and it also peeks at
SmartSDR's saved addresses. Your radio's address has probably been there
all along. Pick the row that matches your radio's nickname or model, or
the one that says "last seen" most recently.

If the list shows nothing, your radio is new to this computer. Continue
reading.

────────────────────────────────────────────────────────

ASK ANOTHER DEVICE FOR THE ANSWER

The fastest way to find a radio's address without looking at it is to ask
your phone or another computer:

  1. On an iPhone or iPad, install the app called Fing from the App Store.
     It's free. Open it and press the Scan button. It will list every
     device on your local network with its name and address. Look for an
     entry with "Flex" in the name, or check the manufacturer column for
     "FlexRadio Systems."

  2. On Android, install Fing from the Play Store and do the same. Both
     versions of Fing work with VoiceOver and TalkBack to varying degrees,
     though the developer hasn't formally documented their accessibility
     support — let us know if you find rough edges.

  3. On another Windows computer that has SmartSDR or JJ Flex installed
     and has connected to the radio before, the address is in
     %AppData%\FlexRadio Systems\ or %AppData%\JJFlexRadio\. Open File
     Explorer, type "%AppData%" in the address bar, and look for those
     folders.

────────────────────────────────────────────────────────

ASK YOUR ROUTER

Your home router has a list of every device connected to it, with each
one's name and address. Getting to that list is different on every router,
but the general path is:

  1. Open a web browser.
  2. Go to "192.168.1.1" or "192.168.0.1" (one of those will work for most
     home routers).
  3. Sign in with your router's username and password. If you've never
     changed them, they're often "admin / admin" or "admin / password" or
     printed on a sticker on the bottom of the router.
  4. Look for a section called "Connected Devices," "DHCP Clients," "LAN
     Clients," or "Attached Devices." It varies.
  5. Find the entry whose name contains "Flex" or "Maestro," or whose
     manufacturer is "FlexRadio Systems." Note its address (four numbers
     separated by dots, like 192.168.1.42).

If your router's admin page isn't accessible to your screen reader, this
won't work — try the Fing approach above instead.

────────────────────────────────────────────────────────

ASK A SIGHTED HELPER TO READ YOUR RADIO

If you have a sighted friend, family member, or club member nearby, they
can read the address off the radio's display.

  • On a FLEX-6300 with the small front-panel display: ask them to look
    at the boot screen — the address shows briefly while the radio starts
    up. Or, on the radio's "Setup" or "Network" screen if accessible from
    the front panel.
  • On a FLEX-6400M, 6500, 6600M, 6700, 6700R, or any FLEX-8000 series:
    these have a larger touchscreen display. The address is in
    "Settings → Network" or "Menu → Network."
  • On a Maestro hardware companion: same Settings → Network path.
  • On any model: the address is the four-number entry next to the label
    "IP Address" or "IPv4 Address." Ignore "MAC Address" and "Subnet Mask"
    — those are different.

If you don't have a sighted helper available, FlexRadio's customer support
will look it up for you over the phone if you give them the radio's serial
number. Their number is on FlexRadio.com under "Support."

────────────────────────────────────────────────────────

ASK FLEXRADIO

If none of the above work, FlexRadio's customer support team can help. Have
your radio's serial number handy (it's printed on a label on the back of
the radio, or you can ask a sighted helper to read it from the radio's
"About" screen). Their support page is at FlexRadio.com under "Support" or
"Help."

────────────────────────────────────────────────────────

WHAT AN ADDRESS LOOKS LIKE

An IP address is four numbers separated by dots. Each number is between 0
and 255. Examples:

  192.168.1.42        most home networks use addresses like this
  10.0.0.99           some home and office networks use addresses like this
  172.16.5.20         less common but used in some networks

If the address you find starts with 169.254 (like 169.254.10.20), that's
called a "link-local" address — it means your radio couldn't get a real
address from the router. The fix is to power-cycle your router and your
radio (turn them off and back on, router first, then wait a minute, then
the radio).

────────────────────────────────────────────────────────

PRESS OK TO RETURN TO THE FIND MY RADIO WINDOW.
```

**Speak on open:** `"How to find your radio's address. This window has plain-language instructions. Use arrow keys or screen reader navigation to read through it. Press OK or Escape to close."` at Chatty verbosity.

**Buttons:**
- *OK* (Alt+O, IsDefault=true) — closes help, returns focus to Find My Radio dialog.
- *Print this help* (Alt+P, optional addition) — opens the system print dialog with the help text. Useful for users who want to keep a paper copy near the radio.

### "Scan my LAN now" — progress and result strings

- On click: `"Starting LAN scan. This takes about 5 seconds."` at Critical.
- Every 25 addresses: `"Scanning, {n} of 254 checked."` at Terse (so power users can opt out).
- On finding a candidate: `"Found something at {ip}, checking if it's a radio."` at Chatty.
- On confirmed radio: `"Found {model}, serial {serial}, at {ip}. Adding to list."` at Critical.
- On scan complete with no radios: `"Scan complete. No radios found on your local network. Try the help button below."` at Critical.
- On scan complete with at least one new radio: `"Scan complete. Found {n} new radio{s}. They are now in the list above."` at Critical, then move focus back to the ListBox.

### "Try cascade again" — confirmation strings

- On click: speak `"Trying again."` at Critical, close this dialog, re-run cascade.
- If the cascade succeeds the second time: standard `RigSelectorDialog` flow takes over with the found radios. (User sees a quick success rather than a "well, this time it worked" — keep it silent-positive.)
- If the cascade fails the second time: re-show the top-level prompt ("We couldn't find your radio") with a slight phrasing tweak: `"We tried again and still couldn't find your radio."`

### "Forget this address" — confirmation strings

- On click with a row selected: open a small confirmation dialog: `"Forget {Nickname} — {IP}? You'll need to find this address again next time."` Buttons *Forget* (Alt+F, IsDefault=true) / *Keep* (Alt+K, IsCancel=true).
- On confirm: speak `"Forgotten."` at Critical. Remove from list. Persist removal.

### Test connection — result strings

- On click: speak `"Testing {ip}."` at Critical. Show busy state in dialog.
- On success: `"{ip} answered. It is a {model}, serial {serial}, firmware {fw}. Press Save and Connect to use this radio."` at Critical, focus stays on Test button.
- On TCP failure (no response): `"{ip} did not answer. Either the address is wrong or the radio isn't reachable from here. Check the address, or use Show Me How."` at Critical.
- On TCP success but FlexLib handshake failure: `"{ip} answered but it's not a FlexRadio. It might be a different device on your network using that address."` at Critical.
- On timeout: `"Test timed out. The address might be unreachable. Try again, or use Show Me How."` at Critical.
- On unexpected error: see Error catalog "test_unexpected" entry.

### Save and Connect — confirmation when typed IP didn't pass Test

- If user presses Save and Connect with an address that hasn't been Tested OR failed its Test:
  ```
  We're not sure {ip} is reachable. Save it anyway?

  If your radio is currently powered off, this is fine — we'll save the
  address and connect when the radio is back online.

  If you typed the wrong address, this won't work and you'll see another
  failure dialog.

  [ Save it anyway ]   [ Test it first ]   [ Cancel ]
  ```
- *Save it anyway* (Alt+S, IsDefault=true) — proceed with save and connect.
- *Test it first* (Alt+T) — close this confirmation, run Test on the typed IP.
- *Cancel* (Alt+C, IsCancel=true) — close this confirmation, return to Find My Radio dialog.

---

## Error catalog

Every cascade-failure error message that may surface in the diagnostic region (Region 5). Each is plain-language, names the rung, names what was checked, names what failed, and tells the user what (if anything) they can try.

The diagnostic region renders as a chronological list, one line per rung-failure, ordered by execution order. Friendly language, no stack traces, no FlexLib internal symbol names.

### Rung 1a — Cached LAN IP

**Friendly name in diagnostics:** "Saved local addresses"

- `cache_file_missing`: `"We checked our saved-addresses file but it doesn't exist yet. This is normal on a fresh install."`
- `cache_file_empty`: `"We checked our saved-addresses file but it has no entries yet. This is normal if you've never connected to a radio from this computer."`
- `cache_file_corrupt`: `"We checked our saved-addresses file but couldn't read it. The file at %AppData%\\JJFlexRadio\\autoConnectV2.xml may be corrupted. Continuing with other discovery methods."`
- `cache_entry_no_response`: `"Tried the saved address {ip} for {nickname} but the radio didn't answer. The address may have changed, or the radio is powered off."`
- `cache_entry_wrong_radio`: `"Tried the saved address {ip} but a different radio answered (serial {serial}). The radio you're looking for may have a new address."`

### Rung 1b — Cached WAN IP (SmartLink only)

**Friendly name:** "Saved internet addresses"

- `wan_cache_no_smartlink`: silent (don't surface — this rung is N/A for non-SmartLink users; not a failure).
- `wan_cache_no_entry`: `"We checked SmartLink saved addresses but didn't find one for this radio."`
- `wan_cache_handle_failed`: `"Tried the SmartLink direct path but the SmartLink server didn't issue an entry token. Falling back to the standard SmartLink path."`
- `wan_cache_unreachable`: `"Tried the SmartLink saved address {ip} but it didn't answer. Your radio's external address may have changed (your home internet got a new address from your ISP). Falling back to the standard SmartLink path."`

### Rung 1.5 — ARP table read

**Friendly name:** "Recently-seen devices"

- `arp_no_flex_oui`: `"We checked Windows's list of recently-seen network devices. No FlexRadio device showed up. Continuing with active probing."`
- `arp_flex_no_response`: `"Found a FlexRadio device at {ip} in Windows's recently-seen list, but it didn't answer when we tried to connect. The radio may be powered off or have a new address."`
- `arp_unavailable`: `"Couldn't read Windows's recently-seen device list. Continuing with active probing."`

### Rung 2 — TCP/4992 subnet probe

**Friendly name:** "Local network search"

- `subnet_no_interface`: `"Couldn't find a working network connection on this computer. Make sure you're connected to a network (Wi-Fi or Ethernet)."`
- `subnet_multi_interface`: `"You have {n} network connections active (Wi-Fi, Ethernet, VPN, etc.). We searched the {primary} network ({subnet}). If your radio is on a different network, that's why we didn't find it. Try disabling other network connections or use the manual address entry."`
- `subnet_complete_no_radios`: `"We tried every address from {first} to {last} on your local network. No radio answered."`
- `subnet_complete_one_collision`: `"Something at {ip} answered but it isn't a FlexRadio. Continuing other discovery methods."`
- `subnet_too_large`: `"Your local network is unusually large (a /{prefix} network with {hosts} possible addresses). We didn't search the whole network because it would take too long. Try the manual address entry, or set up a smaller subnet for your radio."`

### Rung 3 — UDP broadcast discovery (the existing FlexLib path)

**Friendly name:** "Standard FlexRadio discovery"

- `udp_no_broadcasts`: `"Tried the standard FlexRadio discovery method (broadcast messages on your network). The radio didn't answer. This sometimes fails on networks with multiple network adapters, virtual machines, or aggressive firewall settings."`
- `udp_firewall_blocked`: `"Tried the standard FlexRadio discovery method, but Windows Firewall may have blocked our messages. Check Windows Firewall settings and look for JJ Flexible Radio Access in the allowed apps list."`
- `udp_radio_offline`: `"Sent broadcast messages but no FlexRadio responded within {timeout} seconds. The radio is probably powered off, still booting, or on a different network."`

### Rung 4 — SmartLink-as-LAN-fallback

**Friendly name:** "Try SmartLink"

- `smartlink_not_configured`: `"You're not signed in to SmartLink, so we couldn't try that as a fallback."`
- `smartlink_offline`: `"Tried SmartLink as a fallback but couldn't reach SmartLink's servers. Check your internet connection."`
- `smartlink_no_radios`: `"Connected to SmartLink but it has no radios saved for your account. The radio either hasn't been registered with SmartLink, or you're signed in to the wrong SmartLink account."`
- `smartlink_radio_offline`: `"SmartLink says your radio is offline. The radio is probably powered off or disconnected from the internet at its location."`

### Rung 3rd-party config scrape

**Friendly name:** "Other apps' saved addresses"

- `thirdparty_no_apps_found`: `"Looked for SmartSDR, N1MM Logger, WSJT-X, and Hamlib config files. None were found on this computer."`
- `thirdparty_apps_no_addresses`: `"Found {appName} but it has no saved FlexRadio addresses."`
- `thirdparty_addresses_unreachable`: `"Found {n} addresses in other apps' configs (from {appList}) but none of them answered."`

### Test connection (manual entry)

**Friendly name:** "Connection test"

- `test_invalid_address`: `"That doesn't look like a valid address. An address has four numbers between 0 and 255 separated by dots."`
- `test_no_response`: `"{ip} didn't answer within 3 seconds. The address might be wrong, or the radio might be off."`
- `test_not_a_flex`: `"{ip} answered, but it's not a FlexRadio. Some other device on your network is using that address."`
- `test_handshake_failed`: `"{ip} is a FlexRadio but it rejected our connection. The radio's firmware may be too old, or another client may have it locked. Check that no other SmartSDR or JJ Flex session is using this radio."`
- `test_unexpected`: `"Test failed with an unexpected error: {error_message}. Press 'Copy diagnostic details' and contact support."`

### Format of the diagnostic region

When the user expands "Show details," they see something like:

```
Saved local addresses (Rung 1a):
  • Tried 192.168.1.42 (K5NER's Shack, FLEX-6600M) — radio didn't answer.
  • Tried 192.168.1.105 (unknown nickname, FLEX-6300) — a different radio
    answered (serial 1111-2222).

Saved internet addresses (Rung 1b):
  • Skipped — SmartLink not configured.

Recently-seen devices (Rung 1.5):
  • Checked Windows's network neighbor table. No FlexRadio device found.

Local network search (Rung 2):
  • Searched 192.168.1.1 through 192.168.1.254. No radio answered.

Standard FlexRadio discovery (Rung 3):
  • Sent broadcast messages. No radio answered within 5 seconds.

Try SmartLink (Rung 4):
  • Skipped — SmartLink not configured.

Other apps' saved addresses (Rung 3rd-party scrape):
  • Found SmartSDR config — 1 saved address (already shown above).
  • No N1MM Logger or WSJT-X configs found.

Total time: 8.3 seconds
JJ Flex version: 4.2.0
Firmware-update path is unaffected — we'll be able to push firmware once
you connect.
```

The "JJ Flex version" line and the Firmware-update note are deliberately included for support packages — they answer the two most common follow-up questions support staff ask.

---

## First-launch integration recommendation

### The current first-launch flow

`WelcomeDialog.xaml` runs on first launch. It's a 500×400 dialog with four buttons: *Read Documentation / Import Settings / Continue / Quit*. *Continue* eventually leads into `RigSelectorDialog` which kicks off discovery.

### Recommended first-launch path with manual fallback

**Do NOT surface the manual fallback dialog automatically on first launch.** Reasoning:

1. **Friction-tax.** First-launch users haven't earned the complexity yet. Showing them an IP entry field as part of the welcome flow tells them JJ Flex is a "type your IP" app — wrong message.
2. **Most first-launch users are on the LAN with their radio powered on.** UDP discovery works for them. Cached IP rung is empty (fresh install, expected) but Rung 3 succeeds. Manual fallback never surfaces. This is the desired path.
3. **The manual fallback is an apology dialog.** Apologies belong only when the app actually owes one.

**Do surface "Try the cascade now?" prominently in the welcome flow.** When the user presses *Continue* in `WelcomeDialog`, JJ Flex should:

1. Speak `"Looking for your radio. This takes a few seconds."` at Critical.
2. Run the full cascade.
3. On success: `RigSelectorDialog` populates, user picks radio, connects.
4. On failure: surface the top-level "We couldn't find your radio" prompt (per Help text catalog above), with the *Help me find it* path leading to the Find My Radio dialog.

**One first-launch-specific addition:** when the manual fallback dialog opens for a user on first launch (cache is empty by definition), the orientation speech should be slightly different to acknowledge the new-user context:

> "Find My Radio. This is a fresh JJ Flex install, so we have no saved addresses yet. Type your radio's address, or press Show me how to find it for plain-language help."

The key is that the manual fallback is presented as *the next step after auto-discovery failed*, not as *a configuration step that's expected up front*. This frames the situation correctly: the app tried hard, it couldn't find your radio, here's what to do next.

### What to add to the existing welcome content

Update `WelcomeBox` text in `WelcomeDialog` to mention that JJ Flex finds the radio automatically on most networks, and that if it can't, it'll guide the user through it:

> Welcome to JJ Flexible Radio Access.
>
> When you press Continue, JJ Flex will search for your radio on your local
> network. On most networks this works in a few seconds with no further input
> from you.
>
> If your network is unusual — or if your radio is on a different network than
> this computer, or behind a firewall, or powered off — JJ Flex will walk you
> through finding the radio's address using plain language. You won't be
> stuck.
>
> If you're a SmartLink user, you can sign in to SmartLink from the Remote
> button on the next screen.
>
> Press Continue when you're ready, or Read Documentation for more details.

### Connection-troubleshooting help-doc updates

The existing `docs/help/md/connection-troubleshooting.md` should gain a new section pointed at by the manual-fallback dialog's "Show me how to find it" sub-dialog:

> ## When JJ Flex Can't Find Your Radio Automatically
>
> JJ Flex normally finds your radio on its own. If it can't, you'll see a
> dialog called "Find My Radio." This dialog has the addresses your radio has
> used before, plus a place to type the address yourself.
>
> The "Show me how to find it" button in that dialog has plain-language
> instructions for finding your radio's address — using your phone, your
> router, a sighted helper, or FlexRadio's support team.
>
> If you do type the address manually and it works, JJ Flex remembers it for
> next time. You won't have to do this twice for the same radio.

Cross-reference: this also satisfies one of the BlindCat anti-patterns from `project_anti_patterns_from_blindcat.md` — "no in-app explanation when it can't find the radio." JJ Flex has both an in-app explanation (Show Me How) AND a help-doc page.

---

## Open questions

1. **Should "Forget this address" actually delete the entry, or just hide it from the list?** A pure delete loses provenance information that might help debug a future weird discovery state. A hide-with-undo (visible only in a "Show forgotten addresses" view) is friendlier to power users and accident recovery. Lean: hide with a 24-hour soft-delete period, then purge. Cheap to implement, friendly to users who fat-finger Forget.

2. **What's the right Test-connection timeout?** Spec says 3 seconds. SmartLink-saved addresses might need longer (8-10 seconds) because of WAN-side latency. Recommend two configurable timeouts: 3s for LAN-looking IPs, 8s for public/SmartLink-looking IPs. Detect by which RFC 1918 / RFC 6598 bucket the IP falls into.

3. **Do we offer to "merge" saved addresses across machines?** A multi-PC ham (shack PC + laptop) has different cached address sets on each machine. A *Sync addresses with another JJ Flex install* feature would be powerful but tangles with no-silent-phone-home and the per-radio-config-keyed-by-serial principle (`project_per_radio_config_serial_keyed.md`). Defer to Sprint 30+; not a manual-fallback concern today.

4. **Does the FindIpHelpDialog need a "Read this aloud" button distinct from the screen reader's normal navigation?** Some users may want JJ Flex to read the entire help text linearly without manual navigation (especially older users new to NVDA). A *Read aloud from the top* button using JJF's existing TTS could be a friendly addition. Light cost, high friendliness payoff.

5. **Should we surface the cascade's per-rung *time taken* in the diagnostic region?** Knowing "Rung 2 took 4.8 seconds, Rung 3 took 5 seconds" helps power users diagnose slow-network situations. Cost: a few more lines in Region 5. Recommend yes — those numbers are already being captured by the trace persistence design (`project_trace_persistence_design.md`); rendering them is free.

6. **For the "Save it anyway" confirmation when Test failed: should we offer a "Test in 30 seconds" path?** Common scenario: user types an address while radio is mid-boot. Test fails. User saves anyway. Radio finishes booting 20 seconds later. App is already past the dialog and won't retry. Recommend: when Save-it-anyway is chosen and Test failed, schedule a background re-Test 60 seconds later and surface a toast/announcement: "Your saved radio at {ip} is now reachable" — friction-tax win for the radio-still-booting case.

7. **Multi-radio disambiguation when all candidates have the same IP from different scrapes:** if SmartSDR's config and JJ Flex's cache both show `192.168.1.42` for the same radio, we deduplicate by serial. But what if they're different serials (SmartSDR saw the old radio; JJ Flex sees the replacement)? Surface both rows with `last seen` distinguishing them. Lean: dedupe by IP+serial pair, show both rows when they differ.

8. **Fing accessibility status** — Fing's developer hasn't formally documented VoiceOver/TalkBack support. The help text recommends Fing as an "ask another device" option but couches that with a "let us know if you find rough edges." Stronger play: reach out to Fing directly to confirm screen-reader support level, and update the help text accordingly. Cheap to do, anchors the recommendation in fact.

9. **Should we also offer LAN-scanner functionality from inside JJ Flex** (independent of the cascade), so users don't need Fing at all? Rung 2's subnet probe IS a LAN scanner. We could expose it as a separate "Network Scanner" tool under the Diagnostics tab (per `project_sprint29_diagnostics_settings_tab.md`). Low marginal cost since the code exists; high accessibility win because it's screen-reader-first by construction.

10. **The cascade-retry button vs. a "what changed?" prompt:** when the user presses *Try cascade again* after fixing their network, we just re-run blindly. Could ask "Did you change anything? (Powered on the radio / Connected to a different network / Fixed firewall / I just want to try again)" to feed that into the diagnostic capture. Risk: extra friction, may not pay off. Lean: skip for v1; add only if the trace data shows users blindly retrying without changes.

---

## Cross-references

- `docs/planning/design/discovery-fallback-chain.md` — round-2 design memo, Rung 5 spec sketch (lines 84-91)
- `docs/planning/research/discovery-cascade-v3/README.md` — parent stream coordinator
- `JJFlexWpf/Dialogs/RigSelectorDialog.xaml` + `.xaml.cs` — closest existing dialog pattern; copy its accessibility wiring
- `JJFlexWpf/Dialogs/AutoConnectFailedDialog.xaml` + `.xaml.cs` — the existing precedent for an "X is not available" prompt; mirror its Result enum + delegate pattern
- `JJFlexWpf/Dialogs/WelcomeDialog.xaml` + `.xaml.cs` — first-launch host
- `JJFlexWpf/Dialogs/MessageDialog.xaml` + `.xaml.cs` — pattern for the top-level "We couldn't find your radio" prompt
- `JJFlexWpf/JJFlexDialog.cs` — base class providing Escape handling, focus management, automatic Title speech, and `CreateButtonPanel` helper
- `ConnectingForm.vb` — handoff target after Save and Connect; documents the cancel-able connecting-modal pattern this dialog must integrate with
- `docs/help/md/connection-troubleshooting.md` — the help doc that gains a section pointing at the manual fallback
- `memory/project_no_silent_keystrokes_rule.md` — every button press in this dialog must speak
- `memory/project_dialog_escape_rule.md` — Escape and Alt+F4 must always close
- `memory/project_jjflexible_home_terminology.md` — when speaking about returning to home after Cancel, use "JJ Flexible Home"
- `memory/project_anti_patterns_from_blindcat.md` — anti-pattern #1 (no in-app explanation when discovery fails) is what this dialog counter-prescribes
- `memory/project_friction_tax_principle.md` — drives the "automated rungs first, manual entry last, never surface the manual entry preemptively" rule
- `memory/project_no_silent_phone_home.md` — drives the "Copy details" button (user-initiated diagnostic export only) and the WAN-IP redaction rule
- `memory/project_trace_persistence_design.md` — feeds the diagnostic region content
- `memory/project_sprint29_diagnostics_settings_tab.md` — likely host for the standalone "Network Scanner" tool (open question 9)
- `memory/project_localization_strings_file.md` — destination for every string in this catalog when the strings file lands

## Sources consulted

- [Fixed IP Input for Smart SDR — FlexRadio Community](https://community.flexradio.com/discussion/8028835/fixed-ip-input-for-smart-sdr) — confirms SmartSDR has no equivalent feature today; JJ Flex's manual fallback is genuinely novel in the FlexRadio space
- [Manually Connect to 6600M at Specific IP? — FlexRadio Community](https://community.flexradio.com/discussion/8026403/manually-connect-to-6600m-at-specific-ip) — user demand for this feature; quotes about VPN and subnet pain points
- [Any blind hams in this group? — FlexRadio Community](https://community.flexradio.com/discussion/7955269/any-blind-hams-in-this-group) — Łukasz Żelechowski (SQ9BZ) on SmartSDR-Windows accessibility ceiling
- [Front panel IP/DHCP flashing — FlexRadio Community](https://community.flexradio.com/discussion/8023284/front-panel-ip-dhcp-flashing) — confirms FlexRadio front-panel IP display behavior and edge cases (DHCP-IDLE, STATIC-IDLE)
- [Network Settings Info — FlexRadio Community](https://community.flexradio.com/discussion/8030960/network-settings-info) — confirms the *Settings → Network* menu path for accessing IP info on FlexRadio front panels
- [FLEX-6400M and FLEX-6600M User Guide v3.x](https://www.flexradio.com/documentation/flex-6400m-and-flex-6600m-user-guide-pdf/) — confirms 8-inch 1920x1200 touchscreen on M-series; informs the "have a sighted helper read the larger display" recommendation
- [Carbon Design System — Text Input Accessibility](https://carbondesignsystem.com/components/text-input/accessibility/) — informs the "no input mask" decision and the live-validation pattern (web fetch was permission-denied; relied on the search-result excerpt)
- [Fing Network Scanner — Apple App Store](https://apps.apple.com/us/app/fing-network-scanner/id430921107) — confirms availability on iOS; the developer has not formally documented VoiceOver/TalkBack support, so the help text qualifies the recommendation accordingly
- [Smashing Magazine — A Guide to Accessible Form Validation](https://www.smashingmagazine.com/2023/02/guide-accessible-form-validation/) — informs the live-polite validation announcement pattern in Region 3
- [WebAIM — Keyboard Accessibility](https://webaim.org/techniques/keyboard/) — anchors the keyboard-shortcut catalog and tab order requirements
- [NVDA 2025 User Guide](https://download.nvaccess.org/documentation/userGuide.html) — focus mode / browse mode behavior in form fields, informs the "TextBox grabs focus on Loaded" decision

