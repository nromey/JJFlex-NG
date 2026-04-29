# Per-Radio Config Strategy

**Status:** Multi-radio Phase 5 deliverable
**Date:** 2026-04-29
**Author:** Claude Opus 4.7 (track/multi-radio)
**Audience:** Noel — input to the architecture synthesis (Phase 9)
**Builds on:** Phase 4 (`IRadioBackend` abstraction); `project_per_radio_config_serial_keyed.md`

---

## What this document is

`project_per_radio_config_serial_keyed.md` already commits to "radio-state-
dependent config is keyed by radio serial, not by user." For Flex radios,
serial is the natural unique key. For Hamlib radios, the key story is
fuzzier — many CAT radios don't expose a serial number via the protocol, and
multiple radios of the same model on different ports are a real use case.

This document proposes a keying scheme that works cleanly for both worlds, plus
the directory layout, schema, and migration plan for moving today's user-scoped
config that contains radio-state-mixed-in toward serial-keyed config.

---

## 1. The keying problem in one paragraph

Per-radio config needs a stable identifier for the radio it describes. For
FLEX-6300 serial `1315-4176-6300-7236`, the radio's serial is unique,
embedded in every CAT exchange, and visible in `RigData`. For TS-2000 over
COM3 at 9600 baud, the radio reports a model code via `ID;` but no serial.
Two TS-2000s on different COM ports are indistinguishable from each other via
CAT. The user's "name your radio" affordance (used today only as a UI label
for Flex) becomes load-bearing.

The strategy: **a stable radio-id string per saved radio, with the construction
rule depending on the backend.**

---

## 2. Proposed keying rules

### 2.1 The radio-id string

Single string identifier, used as the directory name in
`%AppData%\JJFlexRadio\radios\<radio-id>\`. Construction rules:

**For Flex radios (FlexLibBackend):**
```
flex-<serial>
```
e.g., `flex-1315-4176-6300-7236`. Serial is the source of truth; identical
across reinstalls, across SmartLink remote vs local, across firmware updates.

**For Hamlib radios where Hamlib-reported serial is available:**
```
hamlib-<rig_model_id>-<serial>
```
e.g., `hamlib-1011-AB123456` for a TS-2000 with serial `AB123456` exposed
via `AE` command. Most Kenwoods and Yaesus do; many Icoms do not. When
present, this is preferred.

**For Hamlib radios where serial is not available:**
```
hamlib-<rig_model_id>-<user-nickname>
```
e.g., `hamlib-1011-shack-rig` for a TS-2000 the user nicknamed "shack-rig."
The user nickname is JJF-side data — the user types it when adding the radio.
JJF normalizes (lowercase, dashes for spaces, ASCII only).

**For users who own two of the same radio model with neither serial nor a
unique connection profile:**
```
hamlib-<rig_model_id>-<user-nickname>
```
The user nickname is the disambiguator. JJF enforces uniqueness across the
catalog at config-creation time.

### 2.2 Why include `rig_model_id` for Hamlib

It guards against confusion when serial isn't present. Two TS-2000s would
both be `hamlib-1011-shack-rig` and `hamlib-1011-mobile-rig`. A TS-2000
swapped for a TS-590 (same nickname migration) would change `rig_model_id`
and become a different identifier — preventing accidental config mismatches
where TS-590 inherits TS-2000 antenna definitions.

### 2.3 What the user sees

In the JJF UI, the user picks "Add a Radio," picks a model from the catalog,
configures port + baud (if relevant), enters a nickname. JJF persists the
config under the right radio-id. The user sees the nickname; the radio-id
string is internal.

When the user later "Edits Radio" or "Removes Radio," they're picking by
nickname. The radio-id is the file-system layer; the nickname is the
user-facing layer.

---

## 3. Directory layout

```
%AppData%\JJFlexRadio\
├── k5ner_Noel_Romey_autoConnectV2.xml      (existing user-scoped configs — unchanged)
├── ... other user-scope files ...
└── radios\
    ├── flex-1315-4176-6300-7236\
    │   ├── connection.json         user's saved connection params (port, baud, etc.)
    │   ├── antennas.json           per-port antenna labels
    │   ├── atu-memories.json       ATU memory annotations
    │   ├── tx-controls.json        TX control profile (microphone profile name, EQ presets)
    │   ├── customize-home.json     per-radio Customize Home layout (Sprint 30+)
    │   ├── memory-channel-meta.json per-channel user-friendly labels
    │   └── notes.json              radio-state notes the user added
    │
    ├── hamlib-1011-AB123456\        (TS-2000 with reported serial)
    │   ├── connection.json
    │   ├── antennas.json
    │   ├── atu-memories.json       (only present if radio has ATU)
    │   ├── customize-home.json
    │   ├── memory-channel-meta.json
    │   ├── satellite-presets.json  (TS-2000-class only)
    │   └── notes.json
    │
    └── hamlib-2042-mobile-rig\      (TM-V71A with user-nickname keying)
        ├── connection.json
        ├── customize-home.json
        ├── memory-channel-meta.json
        ├── group-names.json        (TM-V71A has 8 channel groups)
        └── notes.json
```

**Files are JSON.** Per `project_anti_patterns_from_blindcat.md` anti-pattern
#4 (CSV avoided), JSON is the format for structured data. XML is preserved
for legacy formats already in `%AppData%\JJFlexRadio` but new per-radio config
is JSON-first.

**Files only exist when the radio has the relevant capability.** A TM-V71A
folder doesn't have `atu-memories.json` because the radio has no ATU. JJF
checks for file presence as a soft signal but the source of truth is the
backend's capability flags.

**Per-radio config is per-radio user data. It is NOT** synced across users
sharing a radio (Don's 6300 used by a guest operator: each user has their own
JJF install with their own per-radio config, both keyed by the same Flex
serial; the `radios\flex-...\` folder lives in each user's AppData).

---

## 4. What lives where

### 4.1 In `IRadioBackend` (queryable at runtime — the radio's view)

Things that change with the radio's hardware/firmware/license. The backend
reports them; user can't override.

- `Facts.Class`, `Facts.MaxSlices`, `Facts.SupportedBands`, `Facts.SupportedModes`
- `Facts.MaxMemoryChannels`, `Facts.MemoryNameLength`
- `Capabilities` — set of `RadioCapability` flags, hardware-and-license-derived
- Current state: frequency, mode, S-meter, etc.

### 4.2 In `IPerRadioConfig` (user-scope-per-radio — the user's view)

Things the user wants for THIS radio that JJF stores.

- Connection params (port, baud, civ-address, RTS/CTS toggle, transceive on/off,
  any other per-radio settable knob)
- Antenna names per port: `{ port: 1, name: "Hex Beam" }`, `{ port: 2, name: "Wire Dipole" }`
- ATU memory annotations: per-frequency-range ATU memory user labels
- Per-radio Customize Home layout (Sprint 30+ feature)
- Memory channel labels (when JJF stores its own labels in addition to the
  radio's, or when the radio's label format is too restrictive)
- TX control profiles (mic profile, EQ, compression preset)
- Slice layout preferences ("when I connect to this radio, default slices
  to 14.225 USB, 7.090 LSB")
- Per-radio default verbosity (some users want chatty on their HF rig and
  terse on their VHF mobile)
- Boolean flags overriding backend defaults (e.g., `UseHamlibTransceive: false`
  for a buggy transceive implementation)
- Connection auto-reconnect policy per radio
- Per-radio voice memos / TX message text (for voice memory radios)

### 4.3 In user-scope global config (`k5ner_Noel_Romey_*.xml`)

Things the user wants regardless of which radio is connected.

- Verbosity ladder default (overridden per-radio if specified)
- Keyboard customization
- NVDA / JAWS preferences
- Log file paths
- Updater channel preference
- Crash reporter consent
- Diagnostic preferences
- Earcon mute, CW notification toggles
- Default audio output device (which speakers / headset)

### 4.4 The boundary

Some categories straddle. Resolution rules:

- **Last band tuned** — user-scope (operating habit, not radio-specific)
- **Memory channel default selection** — per-radio (radio-specific) — *unless*
  the user explicitly picks a band the radio doesn't support, in which case it
  resolves to the last-band-the-radio-was-tuned-to
- **Notification volume** — user-scope (apps-wide)
- **TX power preset names per band** — per-radio (the radio has bands; the
  preset list lives with the radio)

The principle: if a setting is meaningful only because the radio has specific
capabilities (TX power, antenna ports, ATU memories), it's per-radio. If a
setting is about the user's preferences regardless of radio, it's user-scope.

---

## 5. The migration plan — moving from today

Today's `%AppData%\JJFlexRadio\k5ner_Noel_Romey_autoConnectV2.xml` is
user-scope. Some of it is genuinely user (last-band, verbosity); some has
radio-state mixed in (per-port antenna names because Don's 6300 has different
antennas than Justin's 8400).

### 5.1 Migration is incremental, not bulk

Per the principle in `project_per_radio_config_serial_keyed.md`:

> Don't bulk-refactor for its own sake — migrate when touching a feature anyway.

When a feature is touched that involves radio-specific config, that's the
migration moment for that feature's data. Examples:

- Antenna definitions feature touched (Sprint 27 or later) → antenna names
  migrate to `radios\<id>\antennas.json`
- ATU memories work touched → `atu-memories.json` migrates
- Customize Home (Sprint 30+) → ships natively per-radio, no migration needed
- Memory channel labels → migrate when memory feature touched

User-scope config retains backward compatibility during migration. Code reads
per-radio first, falls back to user-scope, deprecation-warns when fallback is
used. Eventually the user-scope mixed-radio-state fields are deleted from the
schema.

### 5.2 First-run migration prompt

When JJF starts after the first sprint that introduces per-radio config, it
detects user-scope config that has radio-state mixed in. UI prompts:

> "JJ Flex now stores antenna names and ATU memories per radio. Should we
> move your existing settings to be specific to this radio? You'll be able to
> set different ones for other radios you connect to in the future."

Yes → migrate; No → leave user-scope (warn at next connection of a different
radio). This honors flexibility principle: user gets to decide.

### 5.3 What about radios I had before but no longer connect to?

If a user adds and removes radios over time, `radios\<id>\` folders accumulate.
JJF doesn't auto-prune — disk cost is trivial (KB-scale per radio), and the
user might re-add a radio months later expecting their settings to persist.

A "Remove old radios" UI affordance lets the user prune explicitly. JJF
shows last-connected date per radio, user picks which to remove, JJF deletes
the folder (with confirmation per flexibility principle).

---

## 6. Schema sketches — illustrative JSON

### 6.1 `connection.json`

```json
{
  "displayName": "TS-2000 Shack",
  "backendKind": "Hamlib",
  "rigModelId": 1011,
  "manufacturer": "Kenwood",
  "modelName": "Kenwood TS-2000",
  "port": "COM3",
  "baudRate": 9600,
  "parity": "None",
  "dataBits": 8,
  "stopBits": 1,
  "handshake": "None",
  "civAddress": null,
  "useHamlibTransceive": null,
  "extraConfig": {
    "rig_pathname": "COM3",
    "serial_speed": "9600"
  },
  "lastConnectedUtc": "2026-04-28T23:45:12Z",
  "lastFirmwareSeen": "2.16",
  "schemaVersion": 1
}
```

### 6.2 `antennas.json`

```json
{
  "ports": [
    { "portIndex": 1, "name": "Hex Beam", "notes": "rooftop" },
    { "portIndex": 2, "name": "Wire Dipole", "notes": "20m / 40m" },
    { "portIndex": 3, "name": "Dummy Load", "notes": "" }
  ],
  "defaultPerBand": {
    "20m": 1,
    "40m": 2,
    "80m": 2
  },
  "schemaVersion": 1
}
```

### 6.3 `atu-memories.json`

```json
{
  "memories": [
    { "frequencyMHz": 14.225, "antennaPort": 1, "label": "20m phone" },
    { "frequencyMHz":  7.090, "antennaPort": 2, "label": "40m phone" },
    { "frequencyMHz":  3.640, "antennaPort": 2, "label": "80m phone" }
  ],
  "schemaVersion": 1
}
```

### 6.4 `memory-channel-meta.json` (TM-V71A example)

```json
{
  "channels": [
    {
      "channelNumber": 0,
      "userLabel": "K0BAK Repeater",
      "groupName": "PA repeaters",
      "userNotes": "Tone 88.5 / +600",
      "preferred": true
    },
    {
      "channelNumber": 1,
      "userLabel": "146.520 simplex",
      "groupName": "calling",
      "preferred": true
    }
  ],
  "groupNames": [
    { "groupId": 0, "name": "PA repeaters" },
    { "groupId": 1, "name": "calling" }
  ],
  "schemaVersion": 1
}
```

The `userLabel` here can exceed the radio's hardware limit (TM-V71A radio
stores 6 chars; JJF's user label can be 32). When showing memory channels in
JJF UI, the radio label and user label are both available; user label takes
priority for screen reader announcement (it's longer, more meaningful), radio
label is shown alongside in the visible UI for sighted users matching what's
on the radio's display.

### 6.5 `customize-home.json` (forward-looking, Sprint 30+)

```json
{
  "homeLayout": "compact-mobile",
  "sectionsVisible": ["frequency", "mode", "memoryChannel", "ptt", "squelch"],
  "sectionOrder": ["frequency", "memoryChannel", "ptt", "squelch", "mode"],
  "fastKeys": [
    { "key": "F5", "action": "go-to-memory-channel-0" },
    { "key": "F6", "action": "toggle-cross-band-repeat" }
  ],
  "schemaVersion": 1
}
```

### 6.6 `notes.json` (the catch-all)

```json
{
  "notes": [
    {
      "createdUtc": "2026-04-22T18:00:00Z",
      "tag": "antenna-tuning",
      "body": "20m hex beam SWR best at 14.150 — band edge"
    },
    {
      "createdUtc": "2026-04-25T14:30:00Z",
      "tag": "issue",
      "body": "AS retry happens often when CW is active. Maybe related to TX timing."
    }
  ],
  "schemaVersion": 1
}
```

A user-facing notes feature isn't in scope for the first multi-radio sprint
but the file exists in the schema as forward-thinking storage for any per-
radio user data we accumulate. JJF UI surfaces this as a "Radio Notes"
section in Settings.

### 6.7 Schema versioning

Every file has `schemaVersion`. When schema evolves, code reads version and
upgrades in place if straightforward, or prompts the user if not. Don't
silently transform user data.

---

## 7. Privacy and portability

### 7.1 Per-radio config is private

It's local-only. Per `project_no_silent_phone_home.md`, JJF doesn't ship per-
radio config anywhere without explicit user consent.

### 7.2 Export / import

User-driven export of one radio's full config bundle ("save Don's 6300
config to a zip") is useful for backup, migration to a new computer, or
sharing the noise-profile annotations from `project_noise_profile_sharing.md`.

Export shape:
```
ts2000-shack-export-2026-04-28.zip
└── radios/hamlib-1011-AB123456/
    ├── connection.json    (with sensitive fields redacted/blanked at user option)
    ├── antennas.json
    ├── atu-memories.json
    ├── customize-home.json
    ├── memory-channel-meta.json
    └── notes.json
```

Connection params include port path (COM3) which differs across machines.
Export should optionally redact those, or transform to "import-time prompt
for port."

### 7.3 The shared-radio case

Don and Noel both connect to the same Flex 6300 (Don's local radio, Noel via
SmartLink). Each has their own per-radio config keyed by the same `flex-
1315-4176-6300-7236` ID, in their respective `%AppData%\JJFlexRadio\radios\`
folders. They DON'T share the config; each user shapes their own Customize
Home, their own ATU memory annotations.

This is correct — Don's preferences and Noel's preferences for the same
radio differ even though they're operating the same hardware. Per-radio
config is "this user's preferences for this radio," not "this radio's
canonical settings."

---

## 8. The MultiFlex / multi-active-radio dimension

JJF eventually supports up to four concurrent connections (per
`project_strategic_identity.md`). With multiple radios connected:

- Each `IRadioBackend` instance reads its own per-radio config
- The orchestration layer (which one is the foreground / receives keystrokes
  / emits to audio) is JJF UI scope, not per-radio config scope
- Per-radio config can hold "default audio output for this radio" — when
  multiple radios are connected and audio routing decides which speaker gets
  which radio's audio, the per-radio default is the input

This is forward-looking for now; the first multi-radio sprint focuses on
single-radio-at-a-time and the multi-active surface is a later expansion.

---

## 9. Honest issues

### 9.1 Hamlib serial absence

For radios where Hamlib doesn't report a serial AND the user adds two of the
same model, the user nickname is load-bearing. If the user names them
identically by mistake, JJF surfaces the conflict at config-creation time.
The radio-id is internal but reflects nickname; collision is detectable.

### 9.2 Radio replaced by same-model radio

If a user replaces their TS-590S with another TS-590S (warranty exchange,
upgrade), the new radio has a different serial. JJF's discovery and the
connection profile both work, but the per-radio config doesn't follow
because the radio-id changed. UI affordance: "I'm replacing an existing
radio of the same model — keep my settings." Migrates the per-radio config
folder to the new ID.

### 9.3 Same Hamlib model, different firmware

A TS-590S vs TS-590SG have different `rig_model_id` (Hamlib defines them
separately). Good — they get different folders, no conflict. But TS-2000 and
TS-2000X (1.2 GHz variant) share a model ID; capability flags surface the
difference. Per-radio config can hold "this is the X variant" annotation if
it matters for UI rendering.

### 9.4 Per-port antenna semantics differ across radios

Flex radios have ANT1, ANT2, ANT3, RX-A, RX-B etc. — each port is a clear
identifier. Hamlib radios have integer `ant_t` values whose meanings come
from the radio backend driver. The per-radio config maps integer to user
label, but the integer's "what does this port do" semantics need
documentation per radio. Capture that as part of the per-radio config schema
or as a note alongside the antenna labels.

### 9.5 Backwards compatibility for years

Existing JJFlexRadio installs have years of user-scope config. The migration
plan must respect that — old fields keep working until explicit migration.

---

## 10. Open design questions for Noel

1. **Confirm the radio-id format.** Three options floated:
   (a) `flex-<serial>` / `hamlib-<modelid>-<serial-or-nickname>` (proposed)
   (b) UUID per radio assigned at config-creation time, decoupled from serial
   (c) Pure user-nickname keying with collision detection
   Recommendation: (a). Tracks identity sources transparently. UUIDs are
   opaque; nickname-only is fragile when nicknames change.

2. **What about radios connected via multiple paths?** A FLEX-6300 connected
   over LAN today, over SmartLink remote tomorrow — same radio, same per-
   radio config? Recommendation: yes, same config, keyed by serial. The
   connection.json carries connection-attempt strategy (LAN / SmartLink /
   manual IP) but the radio-state config is shared.

3. **Auto-prune cadence.** `last-connected-date` on each per-radio folder
   lets JJF identify radios not connected to in a year. Auto-prune them after
   N years? Recommendation: never auto-prune. Only user-driven removal.
   Storage cost is negligible; surprise-deletion is high-cost.

4. **Where does "radios I've added but never connected to" go?** The picker
   shows them. Recommendation: yes, they live in `radios\<id>\` immediately
   on add, with `lastConnectedUtc: null`. Picker shows them. User can connect
   later; per-radio config persists.

5. **Multi-user shared install on same machine.** Two operators on the same
   Windows account doesn't really happen but worth thinking about — separate
   Windows accounts are the right answer. Per-radio config is per-Windows-
   account inherently because it's in `%AppData%`.

6. **Noise profile sharing** (`project_noise_profile_sharing.md`) — exportable
   profiles include radio metadata. The export bundle from section 7.2
   includes notes; noise profiles add a `noise-profile.json` file. Not in
   first multi-radio sprint scope; designs into the same per-radio folder.

---

## 11. Forward references

This document feeds:

- **Phase 6 (Audio routing)** — per-radio audio routing config goes in
  `radios\<id>\audio.json`
- **Phase 8 (Tester onboarding)** — the radio-add UX produces the
  `connection.json` for each tester's radio
- **Phase 9 (Architecture synthesis)** — per-radio config is the user-side
  layer of the architecture proposal

---

## Document Status

**Phase 5 of 10 complete.** Radio-id keying scheme that handles both Flex
serial-keyed and Hamlib nickname-or-serial-keyed cases. Directory layout,
per-file schemas, migration plan, multi-user / multi-radio implications.

**Estimated read time:** 18-22 minutes for full review; 8-10 minutes for
sections 2, 4, and 5 alone.
