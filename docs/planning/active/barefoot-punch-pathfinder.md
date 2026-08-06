# Barefoot Punch Pathfinder

**Created:** 2026-08-06, mid-session, straight from Noel's plan sketch.
**Branch:** `track/flexlib-4220`, serial execution in one session — no worktrees,
no parallel tracks, no merge plan needed. Work happens in phase order.
**Origin:** the 2026-08-06 hole-punch validation run. The punch-race fix proved out
on the wire, the ASUS source-port rewrite got a source-latch fix the same hour, and
then the Settings tier ladder physically prevented setting the fixed punch port the
next test needs. Noel: fix the network settings layer first, then the detached
client, then test the lot.

## Why these belong together

Per-radio profiles, the tier-button regating, and the offline diagnostic all consult
the same spine: what the radio reports about its own reachability (`fwdTcp`,
`fwdUdp`, `punch` from the SmartLink list and `test_connection`). Phase 1 builds
that spine once and hangs three features off it. Phase 2 (detached operations)
then lands on a stabilized connect path and reuses the per-radio store.

## Phase 1 — Network truth bundle

### 1a. Per-radio network profiles (serial-keyed)

- New per-radio fields in the existing `radios\<serial>\config.xml` store:
  connection preference (**Auto | ForwardOnly | HolePunch**, default Auto) and
  optional **fixed hole-punch port** (null = fresh random per connect, which stays
  the recommended default).
- **Auto follows the radio-reported truth** — zero configuration for both known
  stations: Don's 6300 reports forwarded, Noel's 8600 reports punch. Friction-tax
  principle: the app already knows; never make the user restate it.
- `FlexBase.sendRemoteConnect` resolution order: per-radio profile, then legacy
  account fields, then radio-reported flags. Account-level
  `ConnectionMode`/`ConfiguredListenPort` demote to defaults for radios without a
  profile; no migration needed.
- Ends the `ConfiguredListenPort` double-duty (SettingsDialog.xaml.cs:610 writes
  the radio-side forwarded TCP port into the same field the hole-punch box uses
  for the client punch port). Punch port gets its own per-radio field; the
  account field keeps only its Tier 1 forwarded-port meaning.

### 1b. Settings UI rework — the organizing principle

Split the networking tab by the distinction found 2026-08-06:

- **"How to reach this radio"** (client-side strategy): per-radio, editable
  OFFLINE from the known-radios list — no connection required, ever. Radio
  picker populated from saved per-radio configs plus the last SmartLink radio
  list. Mode selection + fixed punch port live here. This kills both bogus
  gates: the connected-radio gate (SettingsDialog.xaml.cs:375) and the
  tier-ladder gate (line 469, `tier1On && portValid`) that made hole punch
  unselectable for a station that can't port-forward — the exact station hole
  punch exists for.
- **"Commands to the radio"** (radio-side actions): `SetSmartLinkPortForwarding`,
  register/unregister, reboot, firmware. These legitimately stay gated on a live
  connection, and the UI says so in words instead of gray mystery buttons
  (no-silent-state rule).
- **Per-radio IP settings** (Noel, 2026-08-06): the radio's own network
  addressing — static IP / DHCP, address, netmask, gateway — joins the per-radio
  settings box AND stays reachable from Radio Setup; same data, two doors.
  Category-wise these are commands to the radio (applying them needs a live
  connection, likely a reboot), so the tab-order rule governs: when NOT
  connected to that radio the IP controls are SKIPPED from tab order entirely —
  a screen reader pass through offline settings never wades through dead
  controls; when connected, everything is present and available. Verify at
  build time exactly what FlexLib exposes for static network params. Live
  driver: Don's radio goes static at 192.168.203.112 once Tony supplies gateway
  and netmask — this UI is how that gets done without a sighted assist.
- Accessibility: every mode change announced; disabled controls explain why via
  the Feature Availability pattern; Escape rules unchanged.

### 1c. Network diagnostic works offline

- `RunNetworkDiagnosticAsync` currently bails on `theRadio == null` — useless in
  the exact failure state it exists to diagnose. `WanServer.SendTestConnection`
  needs only the SmartLink session; loosen the gate so the diagnostic runs from
  the radio list without a connection.
- Surface `test_connection` results on connect failure (queued hardening item,
  layer 1): "the radio reports its forwarded TCP port is not reachable — check
  the router rule." Don's traces read `fwdTcp=False` for hours while we guessed.
- Keep the existing caution: never auto-fire the probe on a hole-punched session
  (correlated with session death; KickPostConnectNetworkTest already skips it).

### Phase 1 exit tests

- Tier/mode selectable with NO radio connected, for a radio that has never had
  port forwarding. The 2026-08-06 gray-button scenario becomes impossible.
- Latch validation via rarbox doorstop, now configured through the real UI
  (pinned port per-radio) instead of the SmartLinkAccounts.json hand-edit.
- Don-station regression: forwarded path still connects; Auto resolves to
  forward for his radio without anyone touching settings.

## Phase 2 — Detached operations engine (serial, after Phase 1)

- Build per `detached-operations-plan.md` (already designed): `API.IsGUI = false`
  engine, firmware upload with the radio's real progress reporting (not
  SmartSDR's fixed 360-second animation), registration operations.
- Then the **firmware dress rehearsal on the 8600**: deliberate downgrade,
  JJFlex updater brings it back. This is the last gate to the 4.2.0 release
  number (per the 2026-08-06 update to `project_flexlib_4218_merge_sequencing.md`
  — Don's radio is out of the test plan entirely; the 8600 simulates everything).

## Phase 3 — Test it all

- Integrated pass: per-radio Auto against both stations, latch validation if a
  friendly NAT has not yet presented itself, firmware cycle result, then the
  4.2.0 gate review.
- Test matrix file when Phase 3 opens, per SOP.

## Parallel research track — auto-update (added mid-session per Noel)

Runs in the background alongside Phase 1 via an in-session research agent — no
extra CLI session, no worktree, read-only plus one output document. Deliverable:
`docs/planning/active/auto-update-research.md` — inventory of the existing
`JJFlexUpdater`/`JJFlexUpdaterHelper` projects and installer pipeline, gap
analysis, proposed channel/manifest/signing architecture, firmware-channel
tie-in to Phase 2, and a batched open-questions section for Noel. Research only;
implementation gets scoped after Noel reads it. Correction discovered during
Phase 1a recon, recorded here so the plan stays honest: the serial-keyed
`radios\<serial>\config.xml` store from the 2026-04-28 memory is a PRINCIPLE,
not shipped code — Phase 1a builds the store fresh as its first deliverable,
with the network profile as first tenant.

## Explicitly not in this plan (queued separately)

- rarbox WireGuard NAT lab (infrastructure hour, Noel-authorized, own item).
- IPv6 direct-path — JJ Flexible Connect protocol design input.
- Lingering-process-after-punched-death investigation (if the laptop Task
  Manager check confirms a survivor — awaiting Noel's report).
