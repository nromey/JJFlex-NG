# The debug builds that used to live here

Removed from the repo 2026-08-11. They were 55.36 MB of the 58.6 MB this
planning tree occupied — the written record beside them is only 0.17 MB.

## Where they went

**The five 4.2.18 discovery diagnostic builds** (2026-04-30, built from the
`track/flexlib-42` worktree, app version 4.1.16.0, FlexLib 4.2.18) had no
version folder on the NAS because of their ad-hoc version stamp, so they were
copied to a dedicated one:

`\\nas.macaw-jazz.ts.net\jjflex\historical\research\4218-discovery-diagnostics\`

- `JJFlex_4218-discovery-diagnostic_x64_debug.zip`
- `JJFlex_4218-discovery-diagnostic-R2_x64_debug.zip`
- `JJFlex_4218-discovery-diagnostic-R3_x64_debug.zip`
- `JJFlex_4218-discovery-diagnostic-R4_x64_debug.zip`
- `JJFlex_4218-discovery-diagnostic-R6_x64_debug.zip`
- `NOTES-diagnostic.txt` alongside them

**The two nightly builds** were already on the NAS under their normal version
folders and the repo copies were duplicates:

- `4.1.16.241` → `historical\4.1.16.241\` (with a timestamped NOTES; NAS also
  holds the `.exe` and `.pdb`)
- `4.1.16.242` → `historical\4.1.16.242\` (NAS holds **two** revisions,
  `20260506-1919` and `20260509-0025` — one more than the repo ever had)

`trace_april30.zip` was kept — it is 2 KB of Don's trace data, not a build.

## What this does and does not fix

Removing them cleans the working tree and stops new checkouts from carrying
them. It does **not** shrink the repository: the blobs remain reachable from the
2026-05 commits, and a default `git clone` transfers all history, so the ~55 MB
is still paid on every fresh clone. Reclaiming it for real needs a
`git filter-repo` history rewrite, which rewrites every downstream commit SHA —
deliberately not done, because the repo has an `upstream` remote and live
worktrees and the cost is already sunk.

`.gitignore` now blocks `JJFlex_*.zip` and `Setup*.zip` under `docs/` so this
cannot recur.
