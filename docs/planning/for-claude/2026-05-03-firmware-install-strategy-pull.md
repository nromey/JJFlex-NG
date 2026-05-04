---
type: design pull doc — synthesize firmware-required install strategy options
review needed: read, annotate with `**** ` per for-noel protocol; final selection deferred until FlexLib 4.2.18 source diff resolves
priority: high — gates 4.2.0 release shape if FlexLib 4.2.18 turns out to be firmware-required
draft author: Claude
date: 2026-05-03
---

# Firmware-required install strategy — pull doc

## Why this doc exists

Per `project_flexlib_4218_discovery_investigation.md`, FlexLib 4.2.18 silently fails to discover Don's 6300 on his local LAN. R4 trace confirmed Outcome B (packets aren't reaching the bound socket); R4 source-diff investigation falsified the Discovery.cs hypothesis on 2026-05-02. **The bug location is unknown** as of this draft. If the source diff (next steps: API.cs / HAAPI.cs) finds a wrapper-fixable bug, this whole concern dissolves. **If the bug turns out to be firmware-required**, JJFlex 4.2.0's installer needs to handle the case where a user's radio firmware is older than FlexLib 4.2.18 expects — without silently breaking the user's working JJFlex 4.1.16 install.

You raised this concern on 2026-05-02 and expanded it in the concert-night annotation. This pull doc inventories the install-strategy options, including the new ones from your concert-night annotation, so we can make a clean selection once the source-diff outcome lands.

## Inline-context: existing memory

`project_firmware_install_dependency_strategy.md` already captures four options (A through D). Quoting the relevant content inline so you don't have to context-switch:

> **Option A — Block install if older firmware detected.** Pre-install probe: try to discover any Flex radios on local LAN. If found AND firmware version reports older than the 4.2.18-compatible threshold, block install with explanatory message.
>
> **Option B — Allow install with rollback safety net.** Install JJFlex 4.2 over 4.1.16, but keep the 4.1.16 installer cached. If the user runs JJFlex 4.2 and it fails to discover their radio for N consecutive sessions, prompt to roll back.
>
> **Option C — Force firmware update as part of install.** Install flow includes "we'll also update your radio firmware to a compatible version." User consents to both at once. Pairs with `project_chained_updater_pattern.md`.
>
> **Option D — Hybrid: A + B.** Probe for radios on install. If found AND firmware-too-old, suggest firmware update FIRST (Option C-style) but allow user to proceed anyway. Cache 4.1.16 for rollback per Option B regardless.
**** I have no idea about this because if we can't discover radios that are running older firmware but we're using flexlib 4.2.18 which later installers will be, then we got a problem. We really need to find a way, see other docs taht I wrote extensively about this.

## Your concert-night annotation expanded the option set

Quoting your 2026-05-02 annotation on the autonomous-results doc:

> *"I would have to put the new firmware on my 8600 as I got it previously to the 4.2 firmware. I'm pretty close to, if we don't show anything on this pass, that when running the installer, or when first running the software, we upload the firmware. Can we set up the installer to upload firmware previous to installation? If that's not possible, can we, if we see the older firmware, back up previous install, either that or backup the previous install, then, on first run, if old firmware is detected, try to upload. I have no idea if it would be possible to create a firmware uploader that could be run pre install, but we need to make it clear that if you're running a flex, you must upload to 4.2 to use the software, either that or we ship 4.1 until we find a way to safely install prior to upload, then if the user does not install the firmware, the install is rolled back. If firmware is installed and verified working, we delete the old jjflexible version. We know that you can run this software separately. We also could install 4.2 separately, so that if the user wants to install 4.2 and realizes that a firmware is required, they can run jjflexible 4.1 or install 4.2. We make sure that 4.1 can be successfully be removed if 4.2 works."*

This expands the option set:

- **Option E — Firmware update during installer.** Two sub-variants:
  - **E1 (pre-install):** installer uploads firmware to radio BEFORE laying down JJFlex 4.2 binaries. If firmware update fails or user declines, installer aborts entirely; JJFlex 4.1.16 remains untouched. Cleanest user state but highest engineering complexity (installer talking to radio before install completes).
  - **E2 (first-run):** JJFlex 4.2 installs cleanly; on first launch, JJF 4.2 detects firmware version. If too old, prompts for firmware update. If user declines or update fails, JJF 4.2 auto-uninstalls itself (or self-rolls-back) and restores JJF 4.1.16 access. Lower engineering complexity, slightly worse worst-case user state (briefly broken).
- **Option F — Coexist: 4.1.16 and 4.2.0 installed side-by-side.** User keeps 4.1.16 working as a fallback while trying 4.2.0. After 4.2.0 is verified working (or user has updated firmware), they uninstall 4.1.16 manually OR JJF prompts to remove the older version after N successful 4.2 sessions.
**** We really need to figure out more about discovery now, it's super different now that I just realized how important / necessary it is now to figure out how to discover older firmware like Don's radio even though we're using 4.2.18. As is now, we can't even see his radio and this ... is a blocking agent, like a hard block.

## Tradeoff matrix (in prose, not table — screen reader friendly)

**Option A (block install on older firmware):**
- Pros: prevents broken-state install. User stays on a known-working version. Simplest from a "don't break what works" angle.
- Cons: requires firmware-version probe BEFORE install (chicken-and-egg if installer can't reach radio). Users with radios offline at install time get a worse message. Pessimistic: assumes older firmware = bad even if R4 turns out to be a wrapper-fixable bug.

**Option B (rollback safety net):**
- Pros: doesn't gate install. Users can try 4.2.0 and fall back if needed. Low friction for the common case (firmware OK, install just works).
- Cons: requires installer to keep prior-version cached (~50-100MB). Requires "is the failure firmware-related?" detection logic. User experiences broken state for at least one session before rollback prompt.

**Option C (force firmware update during install):**
- Pros: single deliberate user choice; both pieces in compatible state after install.
- Cons: firmware updates are scary for non-technical users. Can fail mid-update and brick a radio. Some users prefer staying on a known-working firmware. Forced-update framing is hostile to your flexibility principle (`project_flexibility_principle.md`).

**Option D (Hybrid A + B):**
- Pros: combines the safety of A with the safety net of B. Most defensive.
- Cons: most engineering work; combines the cons of both.

**Option E1 (firmware update pre-install):**
- Pros: cleanest end state. JJFlex 4.2 only lays down if radio is ready. JJFlex 4.1.16 untouched on abort.
- Cons: installer must talk to radio (firmware upload protocol from inside installer process). Significant engineering. Radio-offline-at-install case = installer can't proceed.

**Option E2 (firmware update on first run):**
- Pros: installer stays simple. First-run firmware check is a normal feature, not installer logic. Auto-rollback path is well-defined.
- Cons: brief broken-state window between "JJFlex 4.2 installed" and "first run completed firmware update." User who bypasses first-run prompt is in broken state.

**Option F (4.1.16 and 4.2.0 coexist):**
- Pros: user always has a working fallback. No scary "your install just broke" moments. Can take their time deciding when to update firmware.
- Cons: disk usage doubles (~200-300MB combined). Two Start menu entries, two AppData folders, two trace files — confusion risk. Eventually the older version needs cleanup; manual or JJF-prompted both have edge cases.
**** Need youyr advice given my writings in "for claude" from 5-03 evening submission

## Recommendation (provisional, conditional on source diff)

**If source diff finds a wrapper-fixable bug:** none of this matters. JJFlex 4.2.0 ships with a fixed wrapper, installs cleanly over 4.1.16, no firmware concern.

**If source diff confirms firmware-required behavior:** my lean is **Option E2 + Option F as a transition mechanism**.

- JJF 4.2.0 installs as a SEPARATE product (different install path / Start menu entry / version suffix), not over 4.1.16. So "installing 4.2" doesn't touch a working 4.1.16 install. Aligns with your "we know you can run this software separately" observation.
- On first launch of JJF 4.2.0, detect firmware version. If radio is reachable AND firmware too old, prompt for firmware update inline. Update succeeds → user is good, can manually uninstall 4.1.16 from Add/Remove Programs whenever they want. Update fails or user declines → JJF 4.2 surfaces an explanatory message and exits; 4.1.16 keeps working as their daily driver.
- After N successful sessions on 4.2.0 (configurable, maybe 5-10), JJF 4.2 prompts: *"Your 4.2.0 install has been working reliably. Remove the older 4.1.16 version?"* with a one-click action.
- If firmware update succeeds during install but JJF 4.2 still fails to discover the radio (unexpected), the user can launch JJF 4.1.16 from Start menu and report the issue. We see traces from both versions. Diagnostic posture improves.

This combines the no-silently-break property of Option F with the user-prompted firmware update of Option E2, without the engineering complexity of Option E1's pre-install firmware upload.

**Why not Option A:** depends on a pre-install probe that can fail in normal "radio is asleep / off-network" conditions. False negatives block users from installing for benign reasons.

**Why not Option C:** forced firmware update violates `project_flexibility_principle.md`. Some users have legitimate reasons to defer firmware updates (alpha/beta testing, known-good config, hardware compatibility concerns).

## Open research questions

These should resolve in parallel with the source-diff investigation:

1. **How does SmartSDR handle this case?** SmartSDR has a far longer history of FlexLib version bumps that required firmware updates. Their installer behavior is precedent. Two ways to find out:
   - Ask Don or Justin to describe their last SmartSDR upgrade experience. Do they remember being prompted for firmware? Was it pre-install, post-install, or did SmartSDR just refuse to launch?
   - Test on a clean machine: install SmartSDR Plus, observe install flow. Higher effort but authoritative.
**** ASlso could reverse compile. Justin or Don have no damn idea.
2. **What's the firmware compatibility matrix for FlexLib 4.2.18?** Vendor docs should specify which radio firmware versions are compatible. Threshold for the "is firmware too old?" check depends on this.
3. **Can we detect firmware version pre-install?** Discovery broadcast packets include firmware metadata. Pre-install we could run a one-shot discovery probe. If radios are found, we know firmware. If not, we can't tell (radio offline, OR 4.2.18 is broken on this firmware). This affects Option A and Option E1 feasibility.
4. **What's the actual cost of "two JJFlex installs side-by-side"?** Disk space is one thing; user-AppData conflicts (config files, traces) are the real concern. Need to verify the existing JJFlex install doesn't share state with prior versions in a way that two coexisting installs would corrupt.

## What I need from you on this read

1. **Sanity check the option inventory.** Have I captured your concert-night sketch correctly as Options E and F? Anything missing?
2. **React to the recommendation.** E2 + F sounds reasonable to me but you have more user-experience context with your tester pool — does this hold up against Don / Justin / Mark's likely reactions?
3. **Authorize the SmartSDR research path.** OK for me to ask Don or Justin about their last SmartSDR upgrade experience to establish precedent? Or do you want to handle that conversation yourself?
4. **Sequencing.** Do you want me to spin up `track/firmware-install-strategy` now (build-now-ship-later per `project_build_now_ship_later.md`) so the install-strategy code is ready when the source-diff outcome lands? Or wait?
**** It's all a mess due to firmware and disaovery

## Cross-references

- `project_firmware_install_dependency_strategy.md` — the existing Options A-D inventory
- `project_flexlib_4218_discovery_investigation.md` — the active investigation that determines whether this concern materializes
- `project_flexlib_4218_merge_sequencing.md` — 4.2.0 release this gates
- `project_chained_updater_pattern.md` — relevant if Option C/E selected
- `project_firmware_distribution_decision.md` — firmware hosting at jjflexible.radio
- `project_flexibility_principle.md` — flexibility constraint on Option C
- `project_no_silent_keystrokes_rule.md` — broader principle applied here as "don't silently break things"
