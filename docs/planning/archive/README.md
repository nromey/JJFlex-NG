# Planning archive

Finished planning material that is no longer `active/` but is worth keeping as
the record of how a decision got made.

**What belongs here:** closed investigations, completed runbooks, superseded
designs, and dress-rehearsal or run-report documents whose event has happened.
Sprint plans are the exception — they have their own home at
`docs/planning/agile/archive/` per the sprint lifecycle SOP in `CLAUDE.md`.

**What does not belong here — or anywhere in the repo:** build artifacts. Debug
zips, installers, and publish output live on the NAS historical tree
(`\\nas.macaw-jazz.ts.net\jjflex\historical\<version>\x64-debug\`, or
`historical\research\` for one-off investigation builds). `.gitignore` now
blocks `JJFlex_*.zip` and `Setup*.zip` under `docs/`.

**Why that rule is stricter than it looks:** a `git rm` cleans the working tree
but does **not** remove the blobs from history, and a default `git clone`
transfers all reachable history — so every clone keeps paying for a committed
build forever unless someone rewrites history. The only cheap defence is not
committing them in the first place.

## Contents

- `don-flexlib-4218-discovery/` — Don's local-LAN discovery investigation
  against FlexLib 4.2.18, April–May 2026. Closed: the 4.2.18 merge was reverted
  2026-05-15 and the project moved to 4.2.20 on `track/flexlib-4220`. The
  written record, traces, and NOTES are kept here; the eight debug builds that
  accompanied them were removed 2026-08-11 and live on the NAS (see the folder's
  own `BUILDS-MOVED-TO-NAS.md`).
