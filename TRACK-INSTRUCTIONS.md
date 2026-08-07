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
