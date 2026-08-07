# QB Track C — Per-radio network settings, serial-keyed

**Recommended model: Fable.** This track carries real design judgment: a
config model that must describe radios with opposite needs, precedence rules,
and offline-editing UX. Document every judgment call you make in a "Design
decisions" section you append to this file.

## Context

One of six parallel tracks in the 2026-08-07 queue-burn (plan:
`docs/planning/active/nightowl-pileup-ragchew.md`). JJ Flex is a
screen-reader-first FlexRadio client. Real-world driver: Don's radio needs
port-forward mode, Noel's 8600 needs hole punch + fixed port — per-ACCOUNT
settings structurally cannot describe both stations. Noel also hit both
gates live: Settings → Networking grays Tier 2/3 unless a radio is CONNECTED
(chicken-and-egg) and unless a valid port-forward config exists (backwards —
punch exists precisely for stations that can't forward).

Read first:
- `docs/planning/active/research-queue.md` — Track C section
- `memory/project_per_radio_config_serial_keyed.md` (via MEMORY.md if
  needed) — the store already exists
- `CLAUDE.md` — accessibility + build rules

## What exists today (verified in code)

- Serial-keyed per-radio store: `radios\<serial>\config.xml` under the config
  directory — a profile stub is written on EVERY connect attempt; holds
  nickname + connection metadata.
- Gate (a): `SettingsDialog.xaml.cs:375` — Networking Tier 2/3 requires a
  connected radio.
- Gate (b): `SettingsDialog.xaml.cs:469` — punch settings require a valid
  Tier 1 port-forward config.
- Double-duty field: `ConfiguredListenPort` — port-forward Apply writes the
  radio-side forwarded TCP port into it (line 610) while the hole-punch port
  box writes the client punch port into the same field; one field, two
  meanings, disambiguated only by mode.
- The radio reports its own reachability flags (`fwdTcp`/`fwdUdp`/`punch`)
  during connect; `sendRemoteConnect` is the consumer.
- Interim unblocker Noel uses today: hand-edit
  `%AppData%\JJFlexRadio\SmartLinkAccounts.json` (app closed):
  `"connectionMode": 2` + `"configuredListenPort": 40420`.

## Work items

1. **Per-radio profile schema.** Extend the per-radio store with:
   `connectionMode` = Auto | ForwardOnly | HolePunch (Auto = follow the
   radio-reported flags — zero config for both known stations,
   friction-tax principle) and optional `fixedPunchPort`. Append fields to
   the existing config class; Track E is adding display-metadata fields
   (favorite, last-seen) to the same class — keep your additions append-only
   and orthogonally named so the merge is trivial.
2. **Offline editing.** The Settings → Radios tab picker already enumerates
   known radios without a connection — add the per-radio network settings
   there, editable OFFLINE. This kills gate (a).
3. **Punch without forward config.** HolePunch mode selectable with no
   port-forward config at all. This kills gate (b).
4. **Untangle `ConfiguredListenPort`.** Two fields (or per-radio storage
   makes the account-level field legacy) — your call, but the two meanings
   must stop sharing one slot. Document the migration.
5. **Precedence.** `sendRemoteConnect` consults per-radio profile → account
   legacy fields → radio-reported flags. Account-level fields demote to
   legacy defaults; per-radio wins when present.
6. **ForwardOnly = fail-fast.** ForwardOnly skips doomed punch attempts and
   fails fast into guidance ("this radio needs port forwarding — here's the
   recipe") instead of 30s of silent grinding. The guidance TEXT will improve
   further in Track D — emit a clear placeholder message; don't duplicate
   D's router-rule generation.
7. **Speech.** Every mode change and every gate you remove gets honest
   speech. Offline edits announce that they apply on next connect.

## Ownership boundaries (do not cross)

- SettingsDialog **Network + Radios tabs** are yours. Do NOT touch the audio
  section (Track B), `RigSelectorDialog` (Track E), or the RadioSetup
  partial (Track F owns the rename GroupBox there).
- `sendRemoteConnect` consult order is yours; the connect FAILURE paths
  (classification, messaging) belong to Track D. If you need a seam, define
  it and note it for the orchestrator.
- No key bindings without flagging the orchestrator.

## Build & verify

```
dotnet build JJFlexRadio.vbproj -c Debug -p:Platform=x64 --verbosity minimal
powershell -Command "(Get-Item 'bin\x64\Debug\net10.0-windows\win-x64\jjflexible.exe').LastWriteTime"
```
Timestamp must be fresh. Verify per surface: Escape closes; AccessibleName
everywhere; no silent keystrokes; disabled controls stay out of tab order
with the reason surfaced (Feature Availability pattern).

## Commit style

Commit after each work item: `QB Track C: <what changed>`. Push to `origin`
(never `upstream`). Append your Design decisions section as you go; report
completion to Noel when done.

## Design decisions (QB Track C execution, 2026-08-07)

**Finding first: items 1, 2, 3, and 5 were already substantially built.**
The branch base carries prior barefoot-punch-pathfinder work (Phase 1a
`231c90b9`/`f04dfc09`, Phase 1b `82bb9277`/`2b5d1614`, waivers `7f4f7e3f`):
`RadioConfig` already has `ConnectionPreference` (Auto/ForwardOnly/HolePunch)
and `FixedHolePunchPort`; the Radios tab already edits profiles offline with
no tier ladder; HolePunch is already selectable there with zero port-forward
config; `sendRemoteConnect` already consults per-radio → account-legacy →
radio-reported. This section's "What exists today" list predates that work.
Rather than re-implement, each item was audited against the live code and
only the real gaps were built: item 4 (untangle), item 6 (fail-fast), and
speech/signpost polish for 2/3/7. No schema fields were added — so the
Track E append-only merge concern is moot; `RadioConfig` is untouched.

**Item 4 — the account field goes legacy (option B).** One writer, one
meaning: `SmartLinkAccount.ConfiguredListenPort` now carries ONLY the
radio-side forwarded-port preference (written by port-forward Apply). The
Network tab's account-level punch-port editor (box + random/clear/save) was
REMOVED, not rewired — one editable home per setting; the Radios tab's
per-radio `FixedHolePunchPort` covers both connected and offline cases and
is the semantically correct home (the punch port belongs to reaching a
radio's site, not to the operator's account). Replacement prose on the
Network tab explains where the setting went and that old values still work.

**Item 4 migration.** No data migration runs. Legacy on-disk punch values —
including the documented hand-edit interim unblocker (`connectionMode: 2` +
`configuredListenPort` in SmartLinkAccounts.json) — keep working through
`sendRemoteConnect`'s account fallback, consulted only when the radio
requires punch, has no per-radio fixed port, and the account mode is
AutomaticHolePunch. Per-radio wins whenever set. Deliberately NO
auto-migration of the account pin into per-radio profiles: the pin is
per-account-to-any-radio, and copying it to every punch radio the account
touches would spread a port meant for one specific radio. Known accepted
wart (documented at the fallback): a value written by port-forward Apply can
be read as a punch port if the router rule later breaks and the account is
in Tier 3 — semantically wrong, functionally harmless (any port number
punches equally well).

**Item 6 — fail-fast scope.** ForwardOnly fails fast ONLY when the radio
reports RequiresHolePunch AND advertises no public TLS port — then there is
literally no address:port to try and the old behavior was tens of seconds of
grinding into a bare "Connection failed". ForwardOnly with public ports
advertised still attempts the forward path: that is the escape hatch the
mode's own description promises ("use when you know the radio's report is
wrong"). The Track D seam is `FlexBase.LastConnectFailureAdvice` — a bare
user-speakable string set only on pre-attempt refusal, cleared at Connect()
and sendRemoteConnect() entry (each retry re-loads the profile, so a
Settings edit between attempts is honored). Placeholder consumption at the
wpfSelectorProc failure site speaks it instead of the generic line. The
message deliberately contains NO router-rule recipe — generating that from
radio-reported values is Track D's job.

**Item 5 — account ConnectionMode deliberately does not veto punch.** A
Tier 1 account connecting to a punch-requiring radio in Auto mode still
punches. Enforcing the ladder's "Tier 1 = never punch" promise at connect
time would break zero-config users whose NAT punches fine (friction-tax);
per-radio ForwardOnly is the real "never punch this station" mechanism. The
gap this leaves — a client-wide security-policy "never punch from this
machine" switch — is flagged under Needs Noel.

**Tier 2/3 stay focusable-while-disabled.** Sprint 27's deliberate
explore-the-choices pattern is kept (screen-reader users can read all three
tiers); the change is the gate now explains itself in place (Feature
Availability pattern) and signposts the per-radio path as the answer for a
radio with no port forwarding at all. Account-level gate (b) itself is kept:
in the cumulative tier model Tier 3 genuinely builds on Tier 1; the
backwards-gate complaint is answered by the per-radio path, not by making
the account ladder incoherent.

**Speech honesty (item 7).** Mode-change announcements append "Choose save
profile to keep it" (matching the waiver toggles — nothing may sound applied
while quietly needing a save button), and the save announcement now SPEAKS
"Applies from the next connection to this radio" instead of leaving it
status-text-only.

**Keyboard audit: skipped** — no key bindings added, removed, or remapped.

**Needs Noel.**
1. Client-wide "never hole punch from this machine" policy (STIG-style):
   per-radio ForwardOnly covers "never punch this station," but nothing
   enforces the account ladder's Tier 1 promise machine-wide. Wave-2
   candidate if wanted.
2. Lifetime of the legacy account punch fallback in sendRemoteConnect:
   currently kept indefinitely so the hand-edit unblocker keeps working.
   Say the word when it should be retired (e.g., once the 8600 has a saved
   per-radio profile everywhere it is operated from).
