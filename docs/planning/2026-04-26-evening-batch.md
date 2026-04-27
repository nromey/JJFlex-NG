# 2026-04-26 Evening Coding Batch — Tonight's Work for Fresh Context

**Purpose:** single self-contained plan for the fresh Claude Code context to execute tonight's loose-end coding without hunting across multiple sources.

**Mode:** auto. Noel wants to set it up + walk away + see results in the morning. Make reasonable assumptions, commit + push as you go, run the seal procedure at the end.

**Branch:** `sprint28/home-key-qsk` (continue here, do not rotate branches)

**Scope:** three blocks of work, in priority order:
1. **Block A — Bug-fix tranche** (small, batched rebuild)
2. **Block B — Rigmeter v1.2 implementation** (biggest rock)
3. **Block C — End-of-day seal** (per CLAUDE.md procedure, full seal — radio code WAS touched today)

---

## Block A — Bug-fix tranche

**Why first:** smallest in time, batches cleanly into one rebuild, gets fixes onto a build Don can play with. Each fix is a separate commit (clean git log).

### A.1 — Step-entry mode: `+` and `-` are identically-behaved

**File:** `JJFlexWpf/FreqOutHandlers.cs` ~line 295 (in AdjustFreq's default block)

**Current code:**
```csharp
if (ch == '+' || ch == '-')
{
    _inStepEntry = true;
    _stepBuffer = "";
    _stepMultiplier = 1;
    Radios.ScreenReaderOutput.Speak("Step entry");
    e.Handled = true;
}
```

**Fix (Option A — recommended):** remove `-` from the entry trigger. `-` becomes either a no-op or an error/notice ("Use `+` to enter step entry"). `+` remains the only step-entry trigger.

```csharp
if (ch == '+')
{
    _inStepEntry = true;
    _stepBuffer = "";
    _stepMultiplier = 1;
    Radios.ScreenReaderOutput.Speak("Step entry");
    e.Handled = true;
}
else if (ch == '-')
{
    Radios.ScreenReaderOutput.Speak("Step entry uses plus only", VerbosityLevel.Terse);
    e.Handled = true;
}
```

**Verification:** press `+` from Frequency field → "Step entry" speech. Press `-` from Frequency field → "Step entry uses plus only" speech. No multiplier reset on `-`.

**Commit message subject:** `Fix step-entry: '-' no longer enters step-entry mode (was identical to '+')`

---

### A.2 — Connection-event speech announces "no active slice" prematurely

**Symptom:** When connecting to a Flex, screen reader announces "Connected to Flex 6300, no active slice" — the "no active slice" portion is true at the connect-event moment but slices populate a beat later. Same shape as the Command Finder pre-deferral bug fixed in commit `3893082b`.

**Investigation needed first:** find the connection-event speech handler. Search:
```bash
grep -rn "no active slice\|Connected to Flex\|ConnectedEvent" --include="*.cs" --include="*.vb"
```

Likely candidates: `MainWindow.ConnectedEventHandler` (around line 1918 — read this morning's grep output), or `FlexBase.cs` similar event handlers. The speech may be assembled from multiple sources.

**Fix pattern (mirroring Command Finder fix):** defer the slice-portion of the announcement until first slice update arrives. Two options:
- **Option 1 (state-check):** before speaking the slice portion, check `theRadio.AvailableSlices > 0` (or `MyNumSlices > 0`). If zero, omit slice portion from the announcement entirely. The slice arrival event owns the slice announcement.
- **Option 2 (Dispatcher.BeginInvoke at ApplicationIdle):** wrap the slice-portion speech in `Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, ...)` to fire after the connect event's queued work (including slice creation) completes. Same pattern as Command Finder commit `3893082b`.

**Recommend Option 1** — simpler, more direct, no race window at all. Option 2 has a small race window if slice arrival takes longer than ApplicationIdle.

**Verification:** connect to a Flex (Don's 6300 if available, otherwise own radio). Confirm the connect announcement does NOT include "no active slice" when slices ARE present.

**Commit message subject:** `Connection-event speech: omit "no active slice" when slices already exist`

---

### A.3 — SetupFreqoutModern stale doc comment

**File:** `JJFlexWpf/MainWindow.xaml.cs:1383-1387`

**Current comment (LIES — claims Modern is "Freq + Slice + SMeter only"):**
```csharp
/// <summary>
/// Modern mode: simplified field set — Freq + Slice + SMeter only.
/// Tuning via modifier keys (Up/Down = coarse, Shift+Up/Down = fine).
/// Other controls (Mute, Volume, Split, VOX, RIT, XIT) accessible via Slice menu.
/// </summary>
```

**Reality** (verified from reading lines 1395-1450): Modern field list is Slice, SliceOps, Freq, SMeter, Squelch, SquelchLevel, Split, VOX, Offset, RIT, XIT — eleven fields. Mute and Volume are intentionally omitted (universal `M` covers mute; Volume goes through Slice menu / Audio expander).

**Fix:** rewrite the comment to match current reality.

```csharp
/// <summary>
/// Modern mode: full field set MINUS Mute and Volume. Sprint 26 Phase 8
/// added the checkbox-field mirroring (Split, VOX, Offset, RIT, XIT) so
/// operators can arrow-right to toggle them without leaving Modern tuning.
/// Mute is universal-key territory (`M` from any field). Volume is via
/// Slice menu / Audio expander.
///
/// Field order: Slice → SliceOps → Freq → SMeter → Squelch → SquelchLevel
///   → Split → VOX → Offset → RIT → XIT
///
/// Tuning: simplified Freq handler with coarse/fine via Up/Down + Shift+Up/Down.
/// </summary>
```

**Verification:** comment-only change — reads correctly, no behavioral test needed. Build clean is sufficient.

**Commit message subject:** `Doc comment: SetupFreqoutModern reflects post-Sprint-26-Phase-8 reality`

---

### A.4 — Universal `V` cycle key does not wrap

**Files:**
- `JJFlexWpf/FreqOutHandlers.cs` AdjustFreq's V handler (around line 339 — search for `CycleVFO(1)` in the AdjustFreq context)
- `JJFlexWpf/FreqOutHandlers.cs` `TryHandleUniversalHomeKey` V handler (added 2026-04-26 in commit `4cf73823` — search for `CycleVFO(1)` in that helper)

**Current code (both sites):**
```csharp
CycleVFO(1);
```

**Fix (both sites):**
```csharp
CycleVFO(1, wrap: true);
```

**Verification:** with 2+ slices, press V repeatedly from any Home field. Should cycle A→B→A or A→B→C→A (wrapping at the end). Pre-fix: stopped at last slice silently.

**Commit message subject:** `Universal V slice cycle wraps from last slice back to first`

---

### A.5 — Letter slice-jump inconsistency between Classic and Modern

**Symptom:** Pressing `a` (or `b`/`c`/etc.) from the **Slice Operations field** in Classic mode does nothing; same key in Modern mode jumps to slice A. Modern's behavior is the right one.

**Investigation needed first:** find the Slice Operations key handler:
```bash
grep -nE "AdjustSliceOps" JJFlexWpf/FreqOutHandlers.cs
```

Compare the letter-handling code path between Classic and Modern modes. Look for whether AdjustSliceOps has a tuning-mode branch for letter handling.

**Fix:** make Classic mirror Modern's letter-jump behavior in AdjustSliceOps.

**Verification:** in Classic, focus Slice Ops field, press `a` → confirm jumps to slice A. Same in Modern. Repeat for `b`, `c` (per available slices on test radio).

**Commit message subject:** `AdjustSliceOps: letter-jump consistency between Classic and Modern`

### A.6 — RIT/XIT silent `+`/`-` announcement (pick Option A: "made positive/negative")

**Symptom:** From RIT/XIT field, `+` calls `Math.Abs(value)` (force positive), `-` calls `-Math.Abs(value)` (force negative). Both modify the value correctly but emit ZERO speech announcement — discoverability and feedback gap.

**File:** `JJFlexWpf/FreqOutHandlers.cs:1467` (the `+` and `-` handlers in AdjustRITXIT default block).

**Fix (Option A — "made positive/negative" terminology, recommended):**

After the `Math.Abs(value)` (or negative variant) and the `Rig.RIT = ...` (or XIT) assignment, add speech:

For `+` handler (force positive):
```csharp
string label = isRIT ? "RIT" : "XIT";
bool changed = data.Value != pos.Value;
string sign = pos.Value >= 0 ? "+" : "";
string terse = $"{label} made positive, {sign}{pos.Value} hertz";
string chatty = changed
    ? $"{label} offset made positive: {sign}{pos.Value} hertz, was {data.Value:+#;-#;0}"
    : $"{label} offset made positive: {sign}{pos.Value} hertz";
Radios.ScreenReaderOutput.Speak(chatty, VerbosityLevel.Chatty);
Radios.ScreenReaderOutput.Speak(terse, VerbosityLevel.Terse);
```

(Or whatever the existing chatty/terse pattern is in this codebase — verify by checking how AdjustRITXIT's other paths announce.)

For `-` handler (force negative): symmetric — `"made negative"` instead of `"made positive"`, `"-Math.Abs(value)"` for the new value. The "was X" delta only included when the value actually changed.

**Tonal note:** strictly, `+` is "make positive" not "invert" — pressing `+` from already-positive is a no-op. The terminology "made positive" reflects the actual action accurately for screen-reader users building a mental model.

**Verification:** focus RIT field, set RIT to -30 via Down arrow, press `+` → hear "RIT made positive, +30 hertz, was -30" (chatty) or "RIT made positive, +30 hertz" (terse). Press `+` again from +30 → hear "RIT made positive, +30 hertz" (no "was" delta, value didn't change). Repeat for `-` and for XIT field.

**Commit message subject:** `RIT/XIT: announce sign-change actions for plus/minus key presses`

### A.7 — RIT/XIT cursor-orphaning after toggle-off from sub-position (BIGGER FIX)

**Symptom:** With RIT (or XIT) on, user arrows into one of the field's expanded sub-positions (e.g., 10 Hz adjust), then toggles RIT off via either Space or R. The field collapses (no value to display when off). Arrow-left/right then do not navigate (no speech, possibly no visual cursor movement either).

**This is the biggest fix in tonight's tranche.** Touches `JJFlexWpf/Controls/FrequencyDisplay.xaml.cs` (the variable-width field rendering and cursor model). Estimated 1-2 hours of focused work. Per `feedback_dont_under_design_or_defer_aggressively.md`, this is in scope tonight unless investigation reveals it's secretly Radios-class-rebuild-scale (it shouldn't be).

**Investigation first:**
- Read `JJFlexWpf/Controls/FrequencyDisplay.xaml.cs` — focus on `DisplayField`, `Populate`, `NavigateToField`, `PositionToField`, `GetFocusedField`, and the arrow-key handlers
- Identify how `SelectionStart` is managed when a field's effective length changes (RIT/XIT collapses from 5 chars to 1 char on toggle-off)
- Determine whether the arrow-key handlers clamp position to the new field's length, or silently fail when out-of-range

**Fix options (pick best after investigation):**
- **(A) Clamp `SelectionStart`** to the new max position whenever any field's content shrinks. Safest, most general fix. Applies to any future variable-length field too.
- **(B) On RIT/XIT toggle-off, explicitly reset `SelectionStart` to position 0** of the field (lands cursor on the on/off indicator). Smaller change, predictable behavior, but specific to RIT/XIT.
- **(C) Always render RIT/XIT field with at least the on/off indicator + a placeholder value position** (e.g., "OFF" + "+0000"), so the structure never shrinks. UX impact — changes read-aloud verbosity.

**Recommend Option A** if the FrequencyDisplay control's selection model is shared across multiple variable-length fields (likely). Option B as fallback if Option A is more invasive than it sounds.

**Verification:** with RIT on, arrow to 10 Hz position (announces "10 hertz"), press R or Space (toggles RIT off, announces "RIT off"). Press left arrow → confirm cursor moves to a sane position with announcement (NOT silent no-op). Repeat with XIT. Confirm no regressions in normal RIT/XIT navigation when value is non-zero.

**Commit message subject:** `FrequencyDisplay: clamp cursor to new field length when RIT/XIT collapses on toggle-off`

**If investigation reveals this is genuinely Radios-class-rebuild-scale:** STOP and document the investigation findings in the TODO entry. Don't crash the budget on a single fix — defer to tomorrow with clear "investigated tonight, here's what I found" notes.

---

## Block B — Rigmeter v1.2 implementation

**Why second:** biggest in time (~2-3 hours for the must-haves; longer if stretch goals all land). Build it after the bug-fix tranche is committed so the radio fixes are durable on git first.

**Source of truth:** `C:\Users\nrome\.claude\projects\c--dev-JJFlex-NG\memory\project_rigmeter_stats_tool.md` — has the FULL design under "v1.2+ deferred" section. Below is a consolidated implementation checklist drawing from that source plus this session's discussions (auto-fetch design, `--verbose` design, `--interactive` design, output-format design).

**Scope is COMPREHENSIVE (per `feedback_dont_under_design_or_defer_aggressively.md`).** Token budget is generous, overnight time is available, build the full v1.2 properly. Only `serve` stays deferred (genuinely VPS-gated). Implementation order below is suggested for sane dependency-resolution; the fresh context can reorder if it sees something better.

**Implementation order (recommended):**
1. **B.1** — Tokei integration + auto-fetch (CORE — comment-vs-code split depends on this)
2. **B.2** — `--verbose` flag (instrument the codebase first; helps debug subsequent work)
3. **B.3** — `--interactive` mode (BOTH Option A menu wizard AND Option B TUI with rich/textual)
4. **B.4** — Output formats (`--format=json|markdown|csv`)
5. **B.5** — Author attribution via git blame
6. **B.6** — Sprint auto-detection
7. **B.7** — TODO/FIXME/HACK counter with snapshot trend
8. **B.8** — Docs-to-code ratio over snapshots
9. **B.9** — ASCII sparkline trend in `all`
10. **B.10** — `forgotten` subcommand
11. **B.11** — Update README + memory
12. **B.12** — Confirm only `serve` is genuinely deferred

### B.1 — Tokei integration + auto-fetch (MUST-HAVE — implement first)

**Goal:** detect tokei on PATH or in `%LOCALAPPDATA%\rigmeter\tools\`. If absent, prompt user once, download from GitHub releases, cache, use. Subsequent runs use cache silently.

**Implementation:**

1. **Detection helper** in `tools/rigmeter/rigmeter.py`:
   - Check `RIGMETER_TOKEI_PATH` env var first (power-user override)
   - Check `%LOCALAPPDATA%\rigmeter\tools\tokei.exe` (cache)
   - Check tokei on PATH (`shutil.which("tokei")`)
   - Returns the path to a usable tokei executable, or None

2. **One-time download prompt:**
   - Function `prompt_and_fetch_tokei()` — uses `input()` to ask: `"rigmeter needs tokei (~5MB) to count code-vs-comments accurately. Download from github.com/XAMPPRocky/tokei/releases? [Y/n]"`
   - On Y: HTTPS GET the latest tokei Windows .exe from GitHub releases API. Store in `%LOCALAPPDATA%\rigmeter\tools\tokei.exe`. Verify SHA-256 if available. Return the path.
   - On N: return None. Caller falls back to no-comment-detection mode (existing v1.1 behavior).

3. **`--no-fetch` flag** on `all`, `growth`, `snapshot` subcommands: skips the prompt entirely; if tokei missing, skip comment detection silently with `(approximate)` suffix in output.

4. **Tokei JSON parser:** when tokei is available, run `tokei --output json <repo-path>` and parse the result. Tokei JSON shape: `{"<language>": {"code": N, "comments": N, "blanks": N, "files": N, "reports": [...]}, ...}`

5. **Layer existing semantics on tokei output:**
   - tokei reports per-language across the whole repo
   - We need per-project AND authored-vs-vendor splits
   - Strategy: run tokei twice — once with `--exclude FlexLib_API/ --exclude P-Opus-master/ --exclude PortAudioSharp-src-0.19.3/` for "authored", once without for "all"; subtract for vendor
   - Or: use tokei's per-file mode (`--files`) and apply our existing classify_path() filtering in Python

6. **Add `pure_code`, `comments`, `blank` dimensions** to the data model in `Snapshot.add_record()` and downstream aggregation. Default to `lines` for v1.1 backward-compat when tokei unavailable.

7. **Update outputs:**
   - `all` adds a "Code structure (tokei)" section: per-language breakdown of code/comments/blanks lines + percentage
   - `growth` adds dedicated headline lines for **pure-code growth %** AND **comment growth %** (separate from total-lines growth)
   - `fun` stays based on authored line totals (no tokei needed)

8. **Tonal framing rule:** the comment-density output must be NEUTRAL-CURIOUS, not judgmental. Output reads "17% of your code is comments" — not "only 17% comments — add more." Noel LIKES commenting; this isn't a code-quality stick to beat anyone with.

### B.2 — `--verbose` flag (MUST-HAVE — using stdlib logging)

**Goal:** opt-in deep visibility into rigmeter's decisions. Useful for debugging "why is this number so big?" questions without code changes.

**Implementation:**
- Use Python stdlib `logging` module
- Set up logger in `main()` based on `args.verbose` flag
- Scatter `logger.debug()` calls throughout: file classification decisions ("classified `cty.dat` as binary"), git invocations ("git ls-files returned 951 paths"), tokei invocations, snapshot writes, etc.
- INFO level is the default; DEBUG only when `--verbose` is passed
- Free at INFO level; only emits at DEBUG when explicitly asked

**Per-command:** add `--verbose` to `all`, `growth`, `snapshot`, `backfill`, `installers`. Single flag, consistent meaning.

### B.3 — `--interactive` mode (BOTH Option A menu wizard AND Option B TUI explorer)

**Goal:** users get TWO interactive surfaces — a stdlib-only menu wizard for accessibility-first basic exploration, and a `rich`/`textual` TUI for power users who want a proper data-explorer.

**Implementation — Option A first (menu wizard, ~80 lines, stdlib-only):**

When `rigmeter` is invoked with no args, OR with `--interactive` (no further qualifier), default to the menu wizard:

```
Rigmeter — interactive mode (basic)
1. all              Full repo statistics
2. today            Git activity since midnight
3. week             Git activity last 7 days
4. month            Git activity last 30 days
5. start            Cumulative since first commit
6. releases         List release tags
7. release <tag>    Stats for one release
8. growth <a> <b>   Growth between two refs
9. snapshot         Write JSON snapshot to NAS
10. backfill        Walk release tags, write snapshots
11. installers      Installer size trend
12. fun             Playful comparisons
13. (TUI explorer — needs rich/textual)
0. exit

Choice:
```

Each choice that needs args prompts: `Tag name: ` etc. Output identical to direct CLI invocation. Loop continues until 0/exit/Ctrl+D. Choice 13 launches Option B if `rich`/`textual` is available; otherwise prompts to install (with auto-install offer using pip).

**Implementation — Option B (TUI stats explorer, ~250-300 lines, requires `rich` or `textual`):**

When `rigmeter --interactive=tui` is invoked (or selected from the menu wizard), launch a full TUI:

- Screen panels: snapshot list (left), drill-down detail (center), trend chart (right or bottom)
- Arrow keys navigate snapshots by date or by tag
- Enter on a snapshot drills into per-language / per-project / file-accounting views
- `g` enters growth-comparison mode (pick two snapshots, compute deltas)
- `t` toggles trend chart (ASCII/Unicode sparkline of code-base growth)
- `q` quits, `?` shows context help, `f` filters by category
- Mouse support optional (textual handles it free)
- Color-coded growth (green for additions, red for deletions, neutral for unchanged)

**Dependency handling for Option B:**
- Detect `rich` and `textual` on first launch of `--interactive=tui`
- If absent: prompt `"TUI mode needs rich and textual (~10MB total via pip). Install? [Y/n]"`
- On Y: subprocess to `pip install --user rich textual`. On N: fall back to Option A menu wizard with a note.
- Cache the install state (`%LOCALAPPDATA%\rigmeter\tui-deps-installed`) so subsequent launches skip the check.
- This adds the FIRST third-party dependency to rigmeter; document in README that stdlib-only is preserved for non-TUI workflows.

**Accessibility consideration for Option B:** TUI must be screen-reader compatible. `textual` has accessibility hooks; verify NVDA can read panel content + react to focus changes. If accessibility regresses below Option A (menu wizard), document the limitation and recommend menu mode for screen-reader users.

### B.4 — Additional output formats (`--format=json|markdown|csv`)

**Goal:** machine-consumable + paste-friendly outputs beyond the v1.1.1 default screen-reader bullets.

**Implementation:**
- Add `--format` flag to `all`, `growth`, `snapshot`, `installers`, `releases`, `today`/`week`/`month`/`year`/`start` (anywhere current output is structured)
- `--format=text` (DEFAULT) — existing screen-reader-bullet output from v1.1.1, unchanged
- `--format=json` — emit the underlying data structure as JSON (similar shape to `snapshot` JSON, but to stdout for arbitrary commands)
- `--format=markdown` — emit as markdown tables (the standard kind, with `|---|---|` separators) — pasteable into GitHub issues, docs, etc.
- `--format=csv` — emit per-row CSV for spreadsheet import / pandas analysis

**Why all four:** `--format=json | jq '.totals.authored.lines'` for shell pipelines. `--format=markdown > today.md` for doc sharing. `--format=csv` for spreadsheet users. Default text stays accessible.

### B.5 — Author attribution via git blame

**Goal:** % of current code authored by Noel, Jim, or others — answers "what did Jim build vs what has Noel built since taking over?"

**Implementation:**
- New subcommand: `rigmeter authors` — shows authorship breakdown per file/per project/repo-wide
- Uses `git blame --line-porcelain` per file (parse author email/name)
- Author-name normalization: small alias table for known authors. Jim's email was [Noel — fill in if you know it]; Noel's is `you@your.email` per recent commits OR his real Gmail. Aliases collapse multiple emails for the same person.
- Output integration: add author-attribution column to `all` per-project breakdown; `authors` subcommand for the deep view
- Snapshot JSON: add `by_author` dimension per project + repo-wide

**Tonal note:** for the Jim/Noel split specifically, this number means something emotionally — Jim never saw what Noel built. Output should be respectful, not just statistical. `"Jim authored 60% of current lines, Noel authored 40%, others authored <1%"` is fine; avoid "trivializing language" framings.

### B.6 — Sprint auto-detection

**Goal:** `rigmeter sprint <N>` reports stats for sprint N's work — auto-detects sprint branches by name pattern.

**Implementation:**
- Pattern: `sprint<N>/<track>` per CLAUDE.md sprint lifecycle convention
- Subcommand `rigmeter sprint <N>` queries `git for-each-ref --format='%(refname:short)' refs/heads/sprint<N>/* refs/remotes/origin/sprint<N>/*`, finds all sprint-N branches (track A, track B, etc.)
- Computes growth: from the merge base of all sprint-N branches with main, to HEAD on the latest sprint-N branch
- Per-track breakdown: separate growth stats per track branch
- Matrix integration: if a `<sprintN>-test-matrix.md` (or `<version>-test-matrix.md`) exists in `docs/planning/agile/`, summarize tested vs untested entries

**Edge cases:** sprint that's been merged to main (branches deleted). Use git tags or commit-message conventions (`Sprint N: ...`) as fallback.

### B.7 — TODO/FIXME/HACK counter with snapshot trend

**Goal:** track technical-debt markers in the codebase over time.

**Implementation:**
- Scan tracked files for `TODO`, `FIXME`, `HACK`, `XXX` markers (case-sensitive in commit-touch-spirit context; usually all-caps)
- Categorize per file/project, count per language
- Add `tech_debt_markers` to snapshot JSON: per-marker-type counts, examples
- Trend output in `growth` subcommand: "TODO count: X → Y (Δ +N)", "FIXME count: ..."
- Subcommand: `rigmeter debt` for the focused debt-marker view

**Tonal note:** rising TODO count isn't necessarily bad (means you're surfacing more); falling isn't necessarily good (might mean deletion without fixing). Output should report both the absolute counts and the deltas without value-judgment language.

### B.8 — Docs-to-code ratio over snapshots

**Goal:** "are we maintaining doc parity?" as a number, not a vibe. Tracks the ratio of docs-text to code-text over time.

**Implementation:**
- Already have `docs` and `code` categories in v1.1
- v1.2 adds: ratio computation in `all` ("docs:code = 0.18:1") and in growth output ("docs ratio was 0.16, now 0.18 — doc base growing faster than code")
- Snapshot JSON: add `docs_to_code_ratio` field for trend queries
- Optional: a `rigmeter doc-parity` subcommand for the focused view (might be redundant with `all` augmentation; decide based on signal-to-noise)

### B.9 — ASCII sparkline trend in `all`

**Goal:** visual at-a-glance sense of code-base trajectory across snapshots.

**Implementation:**
- Read all snapshots from NAS `historical/stats/*.json` (sorted by commit-date prefix)
- Render a tiny Unicode sparkline (▁▂▃▄▅▆▇█) of authored-lines over snapshots
- Plus per-category sparklines (code, docs, text_data, build) so trend differs per category
- Add to `all` output as a new "Trend (last N snapshots)" section
- Screen-reader fallback: also print the underlying numbers as a bullet list, since sparklines are hard to interpret aurally

**Now-meaningful condition:** we have 6 snapshots from tonight's backfill. ≥5 snapshots = enough to show a trend.

### B.10 — `forgotten` subcommand

**Goal:** find files not modified in N days — surfaces dead code candidates.

**Implementation:**
- Subcommand: `rigmeter forgotten [--days N]` (default 90 days)
- Uses `git log -1 --format=%at -- <file>` per tracked file to get last-touched timestamp
- Filters to files older than threshold; sorts oldest-first
- Output: per-file with last-touch date, and aggregate per project/category
- Optional `--exclude` flag to skip certain paths (e.g., `--exclude vendor/` to focus on authored code)

**Use case:** Noel can run `rigmeter forgotten --days 180` and find code that hasn't been touched in 6 months — candidates for dead-code review.

### B.11 — Update README + memory

After all of B.1-B.10 lands:
- `tools/rigmeter/README.md` — comprehensive update covering ALL new subcommands and flags. New subcommand list: `authors`, `sprint`, `debt`, `forgotten`, `serve` (mention deferred). New flags: `--verbose`, `--no-fetch`, `--interactive` (with `=tui` qualifier), `--format`. Document the `pure_code`/`comments`/`blank`/`tech_debt_markers`/`by_author`/`docs_to_code_ratio` data dimensions. New tonal-framing note (don't editorialize the numbers).
- `memory/project_rigmeter_stats_tool.md` — add comprehensive `## v1.2 — shipped 2026-04-26` section. Update the "v1.2+ deferred" list to ONLY contain `serve` (VPS-gated). Add Sprint 30+ candidates if any new ones surface during implementation.

### B.12 — Genuinely deferred (only `serve`)

After tonight's comprehensive v1.2 lands, the only thing remaining for v1.3+ is:

- **`serve` subcommand for HTML rendering at jjflexible.radio/stats** — gated on the JJ Flexible Data Provider VPS being live (see `memory/project_jjflex_data_provider.md`). Effort isn't the blocker; infrastructure is.

If implementation reveals additional architectural questions (e.g., author-name normalization across Jim's many email addresses requires a design pass), document the question in the memory file and ship the rest. Don't block the comprehensive v1.2 on a single sub-question.

---

## Block C — End-of-day seal (FULL seal, not skip-publish)

**Why full seal:** radio code WAS touched today. Multiple commits affect the SmartLink auth flow (WebView2), keyboard handling (universal-keys + KeyToChar), and crash handling (WPF dispatcher). Don and other testers benefit from a fresh build.

**Procedure** (per CLAUDE.md "End-of-day 'done developing' workflow"):

1. **Build a fresh Debug build with proper 4-part version stamp:**
   ```
   build-debug.bat
   ```
   This computes the version from `<Version>` in `JJFlexRadio.vbproj` + `git rev-list --count HEAD` + `BUILDNUM_OFFSET`, builds, archives the zip + NOTES + exe + pdb to NAS `historical/<version>/x64-debug/`.

2. **Promote the debug zip to Dropbox top level as the "daily":**
   ```
   publish-daily-to-dropbox.ps1
   ```
   Replaces any existing `JJFlex_*_debug*.zip` and `NOTES-*-debug*.txt` at Dropbox top level. This is the easy-to-find "what's tonight's build?" artifact for testers.

3. **Memory backup:**
   ```
   backup-memory-to-nas.ps1
   ```

4. **Private docs backup:**
   ```
   backup-private-to-nas.ps1
   ```

5. **Agent.md end-of-day seal entry** — replace the current "RESUME HERE" section with a SEAL entry. Title: `## 2026-04-26 end-of-day seal: Sprint 28 wrap commits + 4.1.17 matrix Section C runtime testing + rigmeter v1.2 + bug-fix tranche`. Include:
   - Commits landed today (full chronological list)
   - What was tested at runtime today
   - What was implemented tonight (rigmeter v1.2 + bug fixes)
   - Memory updates today
   - CLAUDE.md drift (none unless you found some)
   - **Plan for tomorrow:** Section C testing resumes at C.2 (verbosity Terse F2 announcement); 4.1.17 matrix continues per cadence; address any new bugs found tonight; consider whether to land RIT/XIT cursor-orphaning fix tomorrow.

6. **Rigmeter snapshot in seal entry** (CLAUDE.md step 4a):
   - Run `python tools/rigmeter/rigmeter.py all` and `python tools/rigmeter/rigmeter.py today`
   - Paste a condensed version of both outputs into a "Rigmeter snapshot — end of 2026-04-26" subsection at the bottom of the seal entry
   - Run `python tools/rigmeter/rigmeter.py snapshot` to write the structured JSON to NAS at `\\nas.macaw-jazz.ts.net\jjflex\historical\stats\<commit-date>-<short-sha>.json`

7. **Commit the day's seal:**
   - Stage Agent.md, any other modified files (NOT the CHM unless intentional)
   - Commit with subject: `End-of-day seal 2026-04-26: <one-line summary>`
   - Push to `origin/sprint28/home-key-qsk` — NEVER `upstream`

8. **CLAUDE.md drift check:** if today's work exposed any stale guidance in CLAUDE.md (referenced a retired script, missed a new workflow), flag it for update. Likely none today, but worth the 30-second check.

---

## Auto-mode authorization

You have permission to:
- Edit any file in the repo + the user's memory directory
- Run dotnet build, build-debug.bat, build-installers.bat
- Run rigmeter with any subcommand
- Run the backup PowerShell scripts (memory + private)
- Run publish-daily-to-dropbox.ps1
- Commit to `sprint28/home-key-qsk`
- Push to `origin sprint28/home-key-qsk` (NEVER upstream — that's KevinSShaffer's fork)
- Run tokei on first-fetch (download from GitHub) — this is implicit user consent via running rigmeter

You do NOT have permission to:
- Force push (any branch)
- Push to main or upstream
- Skip git hooks (--no-verify)
- Bypass signing
- Run destructive git commands (reset --hard, branch -D, checkout --) without explicit user authorization
- Modify CLAUDE.md without surfacing the change as a deliberate update (CLAUDE.md drift check is a prompt, not autonomous edits)

When you encounter ambiguity:
- Default to the conservative choice
- Log the ambiguity in Agent.md so morning-Noel can review
- Don't block on it — proceed with reasonable assumptions

---

## What's NOT in tonight's scope (deferred deliberately)

Per Noel's comprehensive-scope direction (`feedback_dont_under_design_or_defer_aggressively.md`), only items that are GENUINELY blocked or out of scope by category stay deferred. The list shrank significantly:

- **Universal slice-jump implementation** (Ctrl+J Shift+letter design) — **Sprint 29 candidate**, NOT tonight. Reason: this is a major new architectural feature with multi-channel feedback, layer-help affordances, auto-create-on-jump-to-uncreated-slice, radio-aware filtering, inactivity announcement, etc. Deserves Sprint 29 discipline (proper plan, matrix tests, dedicated commit history). Full design captured in `docs/planning/vision/JJFlex-TODO.md`.
- **Universal `=` transceive toggle with memory** — **Sprint 29 candidate**, NOT tonight. Same reasoning as above — substantial new feature deserves sprint discipline. Design captured in TODO.
- **`+`/`-` cross-field semantic rationalization** — DESIGN QUESTION pending Noel's decision. No code change to make until he decides whether to rationalize or accept the field-specific behaviors.
- **Mode-change coach text polish + PlayCwSK "e e" close** — Noel said he'd take these himself. Don't preempt.
- **Radios class rebuild** — explicitly out of scope per Noel ("not something I wanted to do tonight"). Out of scope across the entire foundation phase, not just tonight.
- **Section C remaining tests** — resume tomorrow at C.2 (verbosity Terse F2 announcement). The matrix-as-we-go cadence holds for tomorrow's session.
- **Rigmeter `serve` subcommand** — VPS-gated (waiting on JJ Flexible Data Provider infrastructure). Effort isn't the blocker; infrastructure is.

Compare to what's IN tonight's scope: 7 bug fixes (A.1-A.7), comprehensive rigmeter v1.2 (B.1-B.11 plus everything previously listed as v1.2-deferred), full end-of-day seal (C.1-C.8). That's the complete tonight scope; nothing else gets deferred without good reason.

---

## File pointers (where things are)

- **This plan:** `docs/planning/2026-04-26-evening-batch.md`
- **Rigmeter source code:** `tools/rigmeter/rigmeter.py`
- **Rigmeter design memory:** `C:\Users\nrome\.claude\projects\c--dev-JJFlex-NG\memory\project_rigmeter_stats_tool.md` (v1.2+ section has full design)
- **JJFlex bug backlog:** `docs/planning/vision/JJFlex-TODO.md` (entries logged 2026-04-26 are at the top under newer entries)
- **4.1.17 verification matrix:** `docs/planning/agile/4.1.17-test-matrix.md`
- **Agent.md:** `Agent.md` (RESUME HERE will be replaced by your end-of-day seal entry)
- **CLAUDE.md:** `CLAUDE.md` (project guidance; seal procedure is in the "End-of-day 'done developing' workflow" section)
