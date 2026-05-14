# Firmware Update Design Memo

**Status:** Approved for 4.2.0.x — required prerequisite for merging `track/flexlib-42` (FlexLib v4.2.18). All design decisions locked 2026-04-29. Pulls firmware-update from the post-4.1.17 Sprint 29 updater scope (per `project_sprint29_updater_vision.md`) forward into 4.2.0.x.
**Date:** 2026-04-29 (decisions locked same day)
**Related:**
- `project_jjflex_data_provider.md` — read-only file host architecture
- `project_firmware_distribution_decision.md` — download-don't-bundle policy (decided 2026-04-29)
- `project_no_silent_phone_home.md` — user-initiated outbound only
- `project_friction_tax_principle.md` — minimize friction between user and radio
- `project_flexibility_principle.md` — user-togglable, conservative defaults
- `track/flexlib-42` Phase 5 commit `c355cbec` — the gate this unlocks

## Problem

`track/flexlib-42` ships FlexLib v4.2.18 with a firmware-version gate on four sub commands (`display_marker`, `navtex`, `filt_preset`, `waveform`). Older firmware silently loses those notification streams; modern firmware (4.2.18+) gets the full surface.

Without an in-app way to update firmware, users on older radios have no easy path to unlock the new features. They'd have to install SmartSDR-CAT-DAX, bring up the SmartSDR client, and run its update flow — which violates our friction-tax principle and exposes blind users to a partially-accessible vendor UI. JJFlexible needs to do firmware updates itself.

Per the 2026-04-29 distribution decision (`project_firmware_distribution_decision.md`), the firmware files are NOT bundled in the JJFlexible installer. They live on `data.jjflexible.radio` and the client downloads them on demand for the matching radio model.

## Goal

Detect the connected radio's model and current firmware, compare against a server-hosted manifest of available firmware versions, and let the user — after physically confirming presence at the radio — push the matching firmware via FlexLib's existing `SendUpdateFile` API.

## Architecture overview

Three components:

1. **Server side (data.jjflexible.radio):** Static file host serving firmware blobs and a manifest JSON. Read-only. No upload endpoint, no API. Firmware is extracted from official Flex SmartSDR installers (already on Noel's machine: `SmartSDR_v4.2.18_x64.msi` in `C:\Users\nrome\Downloads`) and uploaded to the server out-of-band by Noel.

2. **Client side (JJFlexible):** Reads radio model + current firmware version on connect, fetches manifest, compares versions, optionally prompts the user. On user consent + physical presence proof, downloads matching firmware, verifies signature + hash, calls `Radio.SendUpdateFile`. The firmware push itself is local — the radio writes flash and reboots, then the existing connection flow handles reconnect (LAN auto-discovery or SmartLink reconnect, whichever path the user originally used). No bespoke post-reboot reconnect logic.

3. **Physical presence proof:** Before any firmware push, require the user to take a physical action at the radio (mic PTT click or CW paddle press) within a short window. Prevents remote-attack scenarios where a malicious client triggers an unsolicited firmware push on someone else's radio.

## Server side

### Firmware extraction (one-time per release, manual by Noel)

The SmartSDR MSI installer contains both Windows-side software and the radio firmware blobs that SmartSDR pushes to the radio. Two separate firmware files: one for 6000-series radios (FLEX-6300/6400/6500/6600/6700) and one for 8000-series radios (FLEX-8400/8600 + Aurora AU-510/AU-520).

Extraction options:
- **`msiexec /a SmartSDR_v4.2.18_x64.msi /qb TARGETDIR=...`** — admin install extracts contents without installing.
- **7-Zip** — opens the MSI directly, can browse and extract. Works without admin rights.

After extraction, locate the firmware payload files (likely under a `firmware/` or `radio/` subdirectory in the installer's payload). Identify the 6000-series file and the 8000-series file by name + size + content inspection. Document the location convention in this memo when known so the next extraction is deterministic.

### Manifest (`data.jjflexible.radio/firmware/manifest.json`)

```json
{
  "schema": 1,
  "lastUpdated": "2026-04-29T00:00:00Z",
  "channels": {
    "stable": {
      "6000": {
        "version": "4.2.18.39794",
        "sha256": "<hex>",
        "sizeBytes": <int>,
        "url": "https://data.jjflexible.radio/firmware/6000/v4.2.18.39794/firmware.bin",
        "releaseNotesUrl": "https://data.jjflexible.radio/firmware/6000/v4.2.18.39794/RELEASE_NOTES.md",
        "datePublished": "2026-04-29",
        "minClientVersion": "4.2.0.0",
        "compatibleModels": ["FLEX-6300", "FLEX-6400", "FLEX-6400M", "FLEX-6500", "FLEX-6600", "FLEX-6600M", "FLEX-6700", "FLEX-6700R"]
      },
      "8000": {
        "version": "4.2.18.39794",
        "sha256": "<hex>",
        "sizeBytes": <int>,
        "url": "https://data.jjflexible.radio/firmware/8000/v4.2.18.39794/firmware.bin",
        "releaseNotesUrl": "https://data.jjflexible.radio/firmware/8000/v4.2.18.39794/RELEASE_NOTES.md",
        "datePublished": "2026-04-29",
        "minClientVersion": "4.2.0.0",
        "compatibleModels": ["FLEX-8400", "FLEX-8400M", "FLEX-8600", "FLEX-8600M", "AU-510", "AU-510M", "AU-520", "AU-520M"]
      }
    }
  }
}
```

Notes:
- `channels` namespace lets us add a `beta` channel later for alpha-tester firmware drops without changing the schema.
- `minClientVersion` blocks pushing newer firmware to older clients that haven't been audited against it.
- `compatibleModels` is the source of truth for which firmware applies to which radio — client looks up `radio.Model` here to pick the right entry.

### URL layout

```
https://data.jjflexible.radio/firmware/manifest.json
https://data.jjflexible.radio/firmware/6000/v4.2.18.39794/firmware.bin
https://data.jjflexible.radio/firmware/6000/v4.2.18.39794/RELEASE_NOTES.md
https://data.jjflexible.radio/firmware/8000/v4.2.18.39794/firmware.bin
https://data.jjflexible.radio/firmware/8000/v4.2.18.39794/RELEASE_NOTES.md
```

Versioned paths so old firmware stays accessible (rollback possible). Client always reads `manifest.json` first to decide which version is "current."

### Hosting

Phase 1 host = Andre's interim server (per `project_jjflex_data_provider.md`). Migrate to dedicated `data.jjflexible.radio` subdomain when DNS + VPS provisioning is done. URL-as-config from day one means migration is a single config change.

## Client side

### When the check happens

On every successful connect, after the radio's status has settled (`Connected = true`, `Version` populated):

1. Read `radio.Model` and `radio.Version`.
2. Map model → series (6000 vs 8000) via a static `ModelInfo`-style table.
3. Fetch `https://data.jjflexible.radio/firmware/manifest.json` (HTTPS, TLS 1.2+ via `ServicePointManager` — already enforced app-wide in `ApplicationEvents.vb`).
4. Look up `channels.stable[<series>]`.
5. Compare manifest version to `radio.Version` using `FlexVersion`.

Conservative-default per the flexibility principle: do NOT auto-download. Surface a notification: "Firmware update available for your radio (current 4.1.5.39794, available 4.2.18.39794). Open Updates settings to apply." User has to opt in. Settings tab gives them the action button.

User-togglable: a setting "Check for radio firmware updates" (default ON) lets users disable the check entirely. When off, no manifest fetch happens — preserves the no-silent-phone-home commitment.

### When the user opts in

UX flow (in the Updates settings tab):

1. **Pre-flight summary:** "FLEX-6300 'inshack' — current firmware 4.1.5.39794, available 4.2.18.39794. Release notes: [link]. Estimated time: ~3 minutes (radio reboots during the update). Continue?"
2. **Presence-proof gate** (see next section).
3. **Download progress:** "Downloading firmware… 12 MB of 47 MB."
4. **Hash verification:** "Verifying firmware integrity…" — SHA256 against manifest. Any mismatch aborts with a clear error.
5. **Upload to radio:** "Uploading firmware to radio… do not power off the radio." Calls `Radio.SendUpdateFile(localPath)`.
6. **Radio reboot:** Radio drops connection. Client shows "Firmware uploaded. Radio is rebooting." No bespoke reconnect logic — the existing connect flow (LAN auto-discovery or SmartLink) handles the reconnect the same way it does after any radio reboot. User experience is whatever they already get post-reboot today.
7. **Verification (after the user is reconnected):** read `radio.Version` again. Compare to expected new version. Announce success or report mismatch.

All steps screen-reader announced. Cancellation possible at any pre-upload stage with one keypress; once `SendUpdateFile` is called, the upload is committed (radio writes flash; can't safely abort mid-write).

### FlexLib API in v4.2.18

`Radio.SendUpdateFile(string updateFilename)` — `async Task` (signature changed from sync in v4.0.1):

1. Sends `file filename <basename>` command.
2. Sends `file upload <length> update` command — radio responds with a TCP port number to upload on.
3. Opens TCP socket to that port, streams the file.
4. Sets `_updating = true`.
5. Waits 5 seconds (vendor's documented quirk — removing the delay crashes both ends).

After this, the radio reboots. The client should expect `Connected` to flip to `false` shortly after.

For our use:
```csharp
// pseudo
await VerifyManifestSignatureAsync(manifest);              // Microsoft artifact-signing service signature
string localPath = await DownloadFirmwareAsync(manifest.url);
await VerifyHashAsync(localPath, manifest.sha256);
await PresenceProofAsync(theRadio);                        // see below
await theRadio.SendUpdateFile(localPath);
// no bespoke reconnect — existing connect flow handles it
```

## Physical presence proof

### Why

A malicious client (or compromised SmartLink relay) could otherwise call `SendUpdateFile` against any radio it has network access to and brick or downgrade it. A physical-presence requirement means the firmware push only succeeds when a human is at the radio's mic, paddle, or front panel — even if the network path is fully compromised.

### How

Detect any of the following events from FlexLib in a 30-second window after the user clicks "Continue":

- **Mic PTT press** — `radio.MicInput == "PC"`/`"MIC"` toggle, or any PTT-active state change. Most universal: every radio has a mic input. Confirmed primary path 2026-04-29 ("just click the microphone PTT switch").
- **CW paddle press** — `Slice.IsKey` event or a `cwx_text` byte on a NetCWStream. Every CW-capable user can produce this. Confirmed second path.
- **Front panel button (8000 series)** — added as a third option for 8000-series owners IF the SmartSDR upgrade docs document this as a usable presence-proof path. Phase E investigation confirms the FlexLib events on front-panel button presses; if they're reliable, expose this option; if not, drop it. 6000-series radios don't have a front panel, so this is supplemental, not primary.

Any one suffices — the user picks whichever is most natural for them. Prompt: "Press your microphone PTT or your CW key to confirm you are physically at the radio. (30 seconds)"

Counting earcons mark the time window — the same 1, 1+1, 1+1+1 tone pattern from `project_stuck_modal_escape_design.md`. Escape cancels.

If no event fires in the window, abort with: "Did not detect a physical confirmation. The firmware update was cancelled. Try again when you are at your radio."

If two consecutive presses come from the same source (mic OR paddle) within ~200ms, count as one — debounce so a CW operator sending a single dit doesn't fail the check by being "too quick."

### What this does NOT defend against

- A user who is at the radio AND has been social-engineered into running malicious code voluntarily presses PTT when prompted. (Inherent to physical-presence checks; mitigated by the v1 signed manifest — even with DNS/HTTPS compromise, the attacker can't substitute a manifest the client will accept.)
- A roommate / housemate at the radio while a remote attacker triggers the prompt. (Edge case; the v1 signed manifest is already the second factor here, since the malicious payload would need to come from a signed manifest the attacker can't produce.)

## Security model

- **Transport:** HTTPS to `data.jjflexible.radio` only. TLS 1.2+ enforced app-wide (already done in `ApplicationEvents.vb`).
- **Integrity:** SHA256 in manifest must match downloaded firmware byte-for-byte. Mismatch = abort.
- **Authenticity:** the manifest is signed using **Microsoft's Trusted Signing service** (Azure-hosted artifact-signing). Client bundles the public key (or accepts the Microsoft trust chain), verifies the signature on every manifest fetch before honoring its contents. Defends against DNS hijack + cert mis-issue: even if an attacker stands up a fake `data.jjflexible.radio`, the manifest signature won't validate. Approved 2026-04-29 — included in v1, not deferred. Same signing infrastructure will sign the JJFlexible installer once the cert is funded (per `project_code_signing_cert_milestone.md`), so the cost is amortized.
- **Local presence:** physical proof at the radio per the section above.
- **Roll-forward only by default:** v1 will not auto-suggest downgrades. Manual downgrade path (advanced settings) ships in a later phase. Prevents accidental downgrade-attacks where a bad manifest version-rolls users to a buggy old firmware.

## UX placement

Per `project_sprint29_diagnostics_settings_tab.md`, the Updates settings tab is the right home. Subsections:

- **App updates** (existing scope from updater memo)
- **Radio firmware updates** (new — this memo)
  - Status row: "Current firmware: 4.1.5.39794 (April 2024). Latest available: 4.2.18.39794 (April 2026)."
  - Action button: "Update Radio Firmware" (only enabled when an update is available + a radio is connected)
  - Toggle: "Check for radio firmware updates on connect" (default ON)
  - Toggle: "Show firmware update notifications" (default ON)
  - Advanced: link to manual rollback / channel selection (later phase)

Notification surface: when an update is detected on connect, announce and show a small banner. Don't pop a modal. Banner has a "Dismiss" and a "Update now" action. User-driven, not nag-driven.

### Interlock with the JJFlexible app updater

When the manifest's `minClientVersion` for the matching firmware is HIGHER than the running JJFlexible version, the firmware update is not directly offered. Instead the flow becomes a chained two-step:

1. App updater detects "newer JJFlexible required for available firmware" and offers: "A new firmware is available for your radio, but it requires JJFlexible {minClientVersion}. Install JJFlexible {x.y.z} now, then update your radio firmware?"
2. On user consent: silent JJFlexible install (per the broader app-updater scope), JJFlexible restart.
3. After restart and reconnect, the firmware-update banner reappears with the firmware now applicable. User clicks "Update now," presence-proof, push.

This is the natural state for alpha/beta firmware drops on Flex's program — firmware and matching client typically release together. Treat the two updaters as one workflow; share the "do you want to install both" prompt between them.

## Decisions (locked 2026-04-29)

1. **Presence proof primary path = mic PTT click.** "code key click" = CW paddle, second path. ("Microsoft" in the original was a typo, not a Windows-Hello reference.) Front-panel button (8000-series) added as a third supplemental option in Phase E if SmartSDR upgrade docs document it as a usable presence-proof path AND FlexLib reliably fires events on it.
2. **Manifest signing IS in v1**, using Microsoft's Trusted Signing service (Azure-hosted artifact signing). Same infrastructure intended for the eventual JJFlexible installer signing per `project_code_signing_cert_milestone.md` — costs amortize. Phase A and Phase D both gain explicit signature-verify steps; Phase F's signing scope is now subsumed by Phase A.
3. **Keep `channels: { stable: ... }` schema in v1.** Beta channel is wired but starts empty until alpha-tester firmware drops (per `project_flex_alpha_tester.md`).
4. **`minClientVersion` enforcement is an interlock with the JJFlexible app updater, not a hide/grey-out.** When a firmware needs a newer client, the app updater chains: offer client update first, restart JJFlexible silently, then offer the firmware update on the next connect. Treat the two updaters as one workflow. Documented in the "Interlock with the JJFlexible app updater" section above.
5. **No bespoke post-reboot reconnect logic.** Firmware push is local; on reboot the existing connect flow (LAN auto-discovery or SmartLink) handles reconnect the same way it does after any other radio reboot. Whatever the user normally experiences post-reboot today is what they get post-firmware-update. Don't add custom reconnection scaffolding that duplicates the normal path.
6. **Front-panel button (8000) is a maybe, contingent on Phase E investigation.** If FlexLib documents and reliably fires button-press events, expose it as a third presence-proof source for 8000-series owners. If the events are unreliable or undocumented, drop it — mic PTT and CW paddle are sufficient coverage.

## Implementation roadmap

Sized for Sprint 29-or-30 inclusion (whichever sprint claims the 4.2.0.x release work). Five phases, each independently shippable behind a feature flag:

### Phase A — server provisioning (Noel + ops)
- Provision `data.jjflexible.radio` (DNS + VPS) OR commit to Andre's interim host.
- Extract firmware from `SmartSDR_v4.2.18_x64.msi`.
- Hash, version, structure under URL layout above.
- Hand-author first `manifest.json`.
- **Sign `manifest.json` via Microsoft Trusted Signing service.** Bundle the public-key trust path into the JJFlexible client.
- Smoke test: `curl https://data.jjflexible.radio/firmware/manifest.json` returns valid JSON; client-side signature verify passes against the bundled trust path.

### Phase B — client manifest fetch + version compare (read-only)
- Add a `FirmwareUpdateService` class (probably in `Radios/`).
- On connect, fetch manifest, **verify Microsoft Trusted Signing signature**, compare versions, log result.
- No UI yet. Tracing only.
- Verifies the network plumbing AND the signing chain without committing to any user-facing surface.

### Phase C — UX surface in Updates settings tab
- Status row, action button, toggles per UX placement section.
- Notification banner on connect when update available.
- "Update now" routes to Phase D dialog.

### Phase D — download + verify + upload
- Download firmware to `%LOCALAPPDATA%\JJFlexRadio\firmware-cache\<series>\<version>\firmware.bin`.
- SHA256 verify against (signed) manifest.
- Call `Radio.SendUpdateFile`.
- After upload, do nothing special — the existing connect flow handles the post-reboot reconnect on its own.
- On the user's next reconnect (whether seconds or minutes later), read `radio.Version` and announce success or report mismatch.
- Cache hit: skip re-download if the file already exists and hash matches.

### Phase E — physical presence proof
- Wire up mic PTT / CW key event listeners.
- 30-second challenge window UI with counting earcons.
- Insert as the gate between "user clicks Update" and "Phase D download starts."
- **Investigate FlexLib events on 8000-series front-panel button presses.** If reliable + documented in SmartSDR upgrade docs, add front-panel button as a third presence-proof source. If not, ship without it.
- Test: mic PTT, CW paddle (and front-panel button if added), escape-cancel, timeout-cancel.

### Phase F (post-v1, optional) — beta-channel UX + rollback UI
- Surface `channels.beta` toggle in advanced settings for alpha testers (the schema already supports it from Phase A; this just exposes the switch).
- Manual version selection ("install older firmware…") in advanced settings — the rollback path. Versioned URLs already exist; this is UI only.
- Manifest signing is NOT in this phase — it shipped in Phase A.

## Bundle target

This work must be in `main` BEFORE merging `track/flexlib-42`. Per `project_flexlib_4218_merge_sequencing.md`, the FlexLib v4.2.18 merge is gated on (a) all foundation-phase 4.1.17-content content landed and (b) the firmware-update path being available so users can self-unlock the gated subs. This memo describes (b).

## Why this matters beyond v4.2.18

The same plumbing (manifest fetch, version compare, download, install) is the foundation for:
- App updates (the broader Sprint 29 updater scope)
- Future radio firmware drops without shipping new JJFlexible builds
- Beta channels for alpha testers per `project_flex_alpha_tester.md`
- The crash-reporter receiver endpoint per `project_sprint29_crash_reporter_vision.md` (different direction but same `data.jjflexible.radio` infrastructure)

A clean firmware-update path is a force multiplier for everything that follows.
