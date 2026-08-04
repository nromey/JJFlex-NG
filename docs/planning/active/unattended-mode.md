# Unattended mode — design capture (2026-08-04, from Noel)

Noel's ask, verbatim intent: a per-radio switch for a station operated
remotely with nobody at the radio. When on, JJ Flex stops gating actions on
someone being physically present: "if unattended is on, it'd set firmware to
not confirm, rem on. not sure what else."

Status: DESIGN. Not started; slots after the firmware/network dress rehearsal
on the `track/flexlib-4220` line (it is remote-readiness work and touches the
same flows being tested). Per-radio, serial-keyed config
(`radios\<serial>\config.xml`), conservative default OFF — flexibility
principle applies.

## What the switch can honestly control (v1 candidates)

- **Firmware flow confirmations.** Today's policy (2026-08-03): firmware
  never auto-installs; the user initiates, then confirms through dialogs.
  Unattended ON keeps "user initiates" but collapses the confirmation
  ceremony to a single consent — no repeated "are you sure" steps, and no
  prompt language that assumes someone can walk over to the radio.
  Auto-install stays off in every mode; that policy is not renegotiated here.
- **Reconnect behavior.** After a firmware update or `radio reboot`, JJ Flex
  should automatically wait out the reboot and reconnect, rather than asking
  the user to reconnect by hand. (Arguably good behavior in ALL modes —
  decide whether this is unattended-only or just better default behavior.)
- **At-radio-assuming advisories.** Suppress or reword anything that says
  "walk to the radio" / assumes LAN presence, e.g. the firmware advisory's
  "wait until you are home with it" phrasing becomes the informational
  content, not a call to action.

## What it cannot control — say so in the UI, never imply otherwise

- **Registration's mic/CW keydown.** FlexRadio requirement, radio-side,
  cannot be bypassed by any client. One-time per radio, must happen at the
  radio. Unattended mode's setup text should state this plainly so nobody
  ships a radio to a remote site unregistered (the existing warning already
  teaches this).
- **REM ON.** Remote power-on is a rear-panel hardware jack (short to ground
  to power on/off) — there is nothing for software to toggle (verified: no
  WOL, no power-on API; `radio reboot` is the only power command, and only
  while connected). What JJ Flex CAN do: the unattended-mode help/setup
  checklist documents the REM ON wiring option and its implications, next to
  static-IP and port-forwarding steps. Pairs with the Know Your Radio port
  reference for locating the jack (top-right RCA row, "REM ON", on 8000
  series).

## Open questions for Noel (batch — do not drip)

1. Which prompts count as "that crap"? Proposal: firmware confirmation
   ceremony only, v1. Reboot confirmations stay (a reboot kills the session
   you are using).
2. Should unattended ON change the stuck-modal escape timing or anything in
   the update auto-check cadence?
3. Does unattended mode belong on the Radio Setup tab (it is a property of
   the station) or the Updates/Diagnostics tab? Proposal: Radio Setup, as a
   late step — "Step 8: Running unattended" — since the tab is already the
   remote-readiness checklist.
4. Interplay with the radio-access-scheduling vision (time-bounded grants):
   unattended mode is plausibly the substrate those grants toggle. Flag, not
   v1.
