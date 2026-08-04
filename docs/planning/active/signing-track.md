# Track D: Code signing — JJFlex onto the Trusted Signing chain

**Start condition:** after `track/rename-jjflexible` (Track B) merges into
`track/flexlib-4220`. Two reasons: SmartScreen reputation attaches to the
signed publisher+file identity, so the first signed artifacts should carry the
final `jjflexible.exe` name; and both tracks edit `build-installers.bat`,
so serial avoids a pointless conflict. Create the worktree off
`track/flexlib-4220` at that point; branch name `track/signing`.

**Model:** Opus. Involves Azure RBAC and build-script surgery; Noel must be
reachable for `az login` and any role grant (he is the account owner).

## What already exists (do not re-provision)

Read `memory/project_microsoft_trusted_signing.md` first. Summary: Trusted
Signing account `nromey`, certificate profile `romeycert`, eastus endpoint
`https://eus.codesigning.azure.net/`, provisioned and working. The proven
reference pipeline is `C:\dev\Civ-vi-access\.github\workflows\release.yml` —
OIDC federation via App Reg `civviaccess-signing`, zero stored client secrets,
`azure/trusted-signing-action@v0`, RFC3161 timestamping, verify-or-fail step.

## Work items ("move the secrets into JJFlexible" — Noel, 2026-08-04)

1. **Local signing hook** in `build-installers.bat` (the agreed item 0):
   sign the built exe and both NSIS installers via `Invoke-TrustedSigning`
   (official PS module) against `romeycert`, with RFC3161 timestamp and a
   verify step that fails the build if the signature isn't Valid. Auth is
   interactive `az login` — no secrets on disk. Check whether Noel's user
   needs the Certificate Profile Signer role granted (owner ≠ signer).
   Sign the exe BEFORE NSIS packs it, then sign the installers.

2. **JJFlex's own identity on the shared account:** add a federated
   credential for `nromey/JJFlex-NG` — decide sibling App Reg
   (`jjflex-signing`) vs. a second federated credential on the existing one.
   Either way the JJFlex pipeline gets its own least-privilege path to the
   same `romeycert` profile. No client secrets anywhere; if any Azure IDs
   need recording, they go in 1Password (Noel has service accounts/vaults),
   not in the repo.

3. **CI signing (may defer to updater-track kickoff):** adapt the Civ VI
   release workflow for JJFlex tag pushes. The memory entry flags an open
   decision — OIDC-on-GitHub vs. roarbox-only release builds. Present the
   trade-off to Noel via for-noel rather than deciding unilaterally.

4. **Uninstaller signing check:** NSIS generates the uninstaller at install
   time on the user's machine, which breaks naive signing. Verify how our
   NSIS config handles this (pre-generated uninstaller signing is the usual
   fix) — an unsigned uninstaller undoes part of the trust story.

5. **Verify:** fresh-VM install of a signed installer — the prompt must show
   the named publisher, not "unknown publisher". Record the "reputation
   warmup" expectation (first releases may still prompt some users; clean by
   release 3-4) in the release notes plan so nobody reads early prompts as
   failure.
