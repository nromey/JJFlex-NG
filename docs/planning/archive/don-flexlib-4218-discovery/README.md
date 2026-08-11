# Don's FlexLib 4.2.18 silent-discovery investigation

**Status:** ACTIVE — R4 diagnostic build awaiting Don's trace as of 2026-05-02.

Memory entry: `project_flexlib_4218_discovery_investigation.md`

## What this folder is

Don's 6300 isn't discovered by the FlexLib 4.2.18 build. We've shipped four diagnostic-instrumented test builds (R1 → R4) to narrow the failure to one of three layers: socket-level packet arrival, VITA validation, or downstream `myRadioList` plumbing. The R4 build adds the final disambiguator and is parked here pending Don's reply.

This folder lives outside `docs/planning/for-noel/` and `docs/planning/for-claude/` because it's an active engineering artifact set, not a review-queue item. Don's reply trace will land in `docs/planning/for-claude/` when it arrives, get analyzed, and a fix candidate gets drafted.

## Round inventory (newest first)

- **`JJFlex_4218-discovery-diagnostic-R4_x64_debug.zip`** + `round4-r3-test/` — current pending build. Test instructions and Don's autoConnect XML from the R3 round are inside `round4-r3-test/`.
- **`JJFlex_4218-discovery-diagnostic-R3_x64_debug.zip`** + `round3-firewall-test/` — R3 build that explored a firewall hypothesis. The folder holds Don's post-firewall trace.
- **`JJFlex_4218-discovery-diagnostic-R2_x64_debug.zip`** + `round2/` — R2 build that added more instrumentation. The folder holds Don's R2 trace + autoConnect XML.
- **`JJFlex_4218-discovery-diagnostic_x64_debug.zip`** (R1, unsuffixed) + `NOTES-diagnostic.txt` + the two top-level `JJFlexRadioTrace*.txt` files — the original R1 build, its instructions for Don, and Don's R1 trace pair (8:49 AM and 8:54 AM, 2026-04-30).

## When the investigation closes

Once Don's R4 trace arrives and produces a fix:
1. Land the fix on `track/flexlib-42`.
2. Verify with Don on a clean (non-diagnostic) 4.2.18 build.
3. Move this entire folder to `docs/planning/agile/archive/` (or wherever investigation post-mortems land), and remove the `project_flexlib_4218_discovery_investigation.md` memory entry per its "Remove once bug fixed" footer.
