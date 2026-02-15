# JJFlexRadio

Windows desktop application for controlling FlexRadio transceivers (6000/8000 series). Alternative UI to SmartSDR, created by Jim Shaffer. Current version: 4.1.x, using FlexLib v4.0.1.

## Quick Reference

| Item | Value |
|------|-------|
| Solution | `JJFlexRadio.sln` |
| Build x64 (Release) | `dotnet build JJFlexRadio.sln -c Release -p:Platform=x64` |
| Build x86 (Release) | `dotnet build JJFlexRadio.sln -c Release -p:Platform=x86` |
| Build (Debug) | `dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64` |
| Rebuild (Release) | `dotnet clean JJFlexRadio.sln -c Release -p:Platform=x64 && dotnet build JJFlexRadio.sln -c Release -p:Platform=x64` |
| Installer | Runs automatically after Release build |
| Output x64 | `bin\x64\Release\net8.0-windows\win-x64\JJFlexRadio.exe` |
| Output x86 | `bin\x86\Release\net8.0-windows\win-x86\JJFlexRadio.exe` |
| Installer x64 | `Setup JJFlexRadio_[version]_x64.exe` |
| Installer x86 | `Setup JJFlexRadio_[version]_x86.exe` |

**Note**: Use `dotnet build` (preferred) or `msbuild` if in VS Developer shell. Add `--verbosity minimal` to reduce output noise.

**Important**: Close running JJFlexRadio before building (Radios.dll lock).

## Tech Stack

- **Languages**: VB.NET (main app) + C# (libraries)
- **Framework**: `net8.0-windows` (.NET 8)
- **Platforms**: x64 (primary), x86 (legacy support)
- **UI**: WinForms (primary), WPF (UiWpfFramework)
- **Auth**: WebView2 (Edge/Chromium) for SmartLink Auth0
- **Native deps**: Opus 1.5.2, PortAudio 19.7.0 (architecture-specific in `runtimes/`)
- **Installer**: NSIS via `install.bat` + `install template.nsi`

## Project Structure

```
JJFlexRadio.vbproj      Main WinForms app (VB.NET entry point)
Radios/                 Radio abstraction layer (Flex-only)
  FlexBase.cs           Base class for Flex radios
  AllRadios.cs          Rig table and discovery
  Flex6300Filters.cs    Filter/DSP controls UI
FlexLib_API/            Vendor FlexLib v4 + wrappers
  FlexLib/              Core Flex radio API
  Util/                 Utilities (audio, network, etc.)
  Vita/                 VITA-49 protocol implementation
  UiWpfFramework/       WPF MVVM helpers
JJPortaudio/            PortAudio wrapper
P-Opus-master/          Opus codec wrapper
JJLogLib/               Logging library
JJTrace/                Trace/debug utilities
docs/                   Extended documentation
```

## Key Patterns

### TLS Enforcement
Custom TLS 1.2+ wrapper enforces modern TLS without editing vendor FlexLib:
- Wrapper: `FlexLib_API/FlexLib/SslClientTls12.cs`
- App-wide floor: `ApplicationEvents.vb` sets `ServicePointManager.SecurityProtocol`
- **After FlexLib upgrades**: Reapply wrapper per `MIGRATION.md`

### Feature Gating
Use FlexLib APIs to check feature availability before exposing UI:
```csharp
// Check license
if (theRadio.FeatureLicense?.LicenseFeatDivEsc?.FeatureEnabled == true) { ... }

// Check hardware capability
if (theRadio.DiversityIsAllowed) { ... }  // 2-SCU radios only

// Check resources (MultiFlex awareness)
if (theRadio.AvailableSlices >= 2) { ... }
```

### Accessibility Guidelines
- Remove `&` from menu labels (interferes with screen readers)
- Always Set `AccessibleName` and `AccessibleRole` on controls
- Keep unsupported/disabled controls out of tab order
- Use Feature Availability tab to explain why radio features are unavailable due to subscription unavailable or model spec deficiencies

### Changelog Conventions
The changelog (`docs/CHANGELOG.md`) is **user-facing** — it's read by hams, not developers. Write it accordingly:

- **Warm, personal, first-person tone**: "I fixed...", "You can now...", "This one's been on my list forever." The voice is a funny nerdy ham radio developer talking to friends. Read the existing entries for the vibe.
- **No internal jargon**: No track labels (Track A/B/C), sprint numbers, bug IDs (BUG-017), WPF, WinForms, ElementHost, async patterns, AutomationPeer, interop, or framework names. Users don't care about the plumbing.
- **Explain the *what*, not the *how***: "DSP toggles now tell you on or off" — not "Fixed async property pattern using local variable to capture toggled state before FlexLib round-trip."
- **Screen reader details are OK**: Our users *are* screen reader users. "Your screen reader now announces the callsign" is fine. Just don't say "added AutomationProperties.Name to the DataGrid row template."
- **Technical details live in planning docs**: Sprint plans, test matrices, `JJFlex-TODO.md`, and `Agent.md` are the developer record. The changelog is the user record.

## Build Notes

### Build Commands
```batch
# Build x64 Release (recommended)
dotnet build JJFlexRadio.sln -c Release -p:Platform=x64

# Build x86 Release (for older 32-bit systems)
dotnet build JJFlexRadio.sln -c Release -p:Platform=x86

# Minimal output (recommended for CI/automation)
dotnet build JJFlexRadio.sln -c Release -p:Platform=x64 --verbosity minimal

# Clean + rebuild (guaranteed fresh output)
dotnet clean JJFlexRadio.sln -c Release -p:Platform=x64 && dotnet build JJFlexRadio.sln -c Release -p:Platform=x64 --verbosity minimal
```

### WARNING: `--no-incremental` Does NOT Guarantee Fresh Builds

**Do NOT rely on `--no-incremental` to produce fresh binaries.** It only disables incremental *compilation* but the build system can still skip projects entirely if it believes outputs are up-to-date. This means:

- Output files may retain old timestamps
- The NSIS installer post-build step won't run (it only triggers when the project actually compiles)
- You can end up distributing stale binaries

**Always use `dotnet clean` before `dotnet build` when you need fresh output.** Or use `build-installers.bat` which deletes the output folder before building.

### CRITICAL: Verify Build Output After Every Build

**After every build, verify the exe timestamp matches the current time.** Stale binaries have wasted entire testing sessions. Run:

```batch
powershell -Command "(Get-Item 'bin\x64\Release\net8.0-windows\win-x64\JJFlexRadio.exe').LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')"
```

If the timestamp doesn't match the current time, the build did NOT produce a fresh binary. Also note: building the **solution** (`JJFlexRadio.sln`) may skip the main project — always build the **project** directly (`JJFlexRadio.vbproj`) to be safe.

### Platforms
- Primary: x64 (64-bit, recommended)
- Legacy: x86 (32-bit, for older systems)
- Framework: `net8.0-windows` only

### Installer Generation
Installer runs automatically as post-build step for Release builds. Creates:
- `Setup JJFlexRadio_[version]_x64.exe` (64-bit installer)
- `Setup JJFlexRadio_[version]_x86.exe` (32-bit installer)

The installer:
- Auto-detects architecture from build output
- Installs to correct Program Files folder (64-bit vs 32-bit)
- Includes architecture suffix in filename

### Native DLLs
Architecture-specific native libraries are in:
- `runtimes/win-x64/native/` - 64-bit portaudio.dll, libopus.dll
- `runtimes/win-x86/native/` - 32-bit portaudio.dll, libopus.dll

`NativeLoader.vb` resolves the correct DLLs at runtime.

### Known Warnings (safe to ignore)
- CA1416 platform compatibility warnings (Windows-only APIs)
- System.Drawing.Common version conflicts
- WPF assembly resolution warnings

### Security Notes
- TLS 1.2/1.3 enforced via `SslClientTls12.cs` wrapper
- WebView2 replaces legacy IE WebBrowser for Auth0
- DotNetZip being replaced with `System.IO.Compression` (Zip Slip CVE)

## Radio Support

| Model | SCUs | Diversity | Max Slices | Notes |
|-------|------|-----------|------------|-------|
| FLEX-6300 | 1 | No | 2 | Entry-level, optional ATU |
| FLEX-6400(M) | 1 | No | 2 | 3rd-order preselectors |
| FLEX-6500 | 1 | No | 4 | 30dB bandpass filters |
| FLEX-6600(M) | 2 | Yes | 4 | 7th-order filters, full duplex |
| FLEX-6700(R) | 2 | Yes | 8 | Flagship 6000 series |
| FLEX-8400(M) | 1 | No | 2 | 8000 series entry |
| FLEX-8600(M) | 2 | Yes | 4 | 8000 series mid-range |
| AU-510(M) | 1 | No | 2 | Aurora 500W, based on 8400 |
| AU-520(M) | 2 | Yes | 4 | Aurora 500W, based on 8600 |

Detection: Use `theRadio.Model`, `theRadio.DiversityIsAllowed`, `theRadio.MaxSlices` rather than hardcoding.

## Migration Status (.NET 8 + x64) - COMPLETED

**All phases complete:**
- Phase 0: Legacy cleanup (removed Icom, Kenwood, Generic radio support)
- Phase 0.5: Added FLEX-8400 and Aurora AU-510 to RigTable
- Phase 1-2: All C# projects converted to SDK-style
- Phase 3: All projects updated to `net8.0-windows` only
- Phase 4: Native DLL loading with architecture detection (`NativeLoader.vb`)
- Phase 5: WebView2 migration for Auth0 (`AuthFormWebView2.cs`)
- Phase 6: Dual x86/x64 build support with architecture-specific installers
- Phase 7: Cleanup (removed conditional compilation, updated documentation)

## Related Documentation

| File | Description |
|------|-------------|
| `MIGRATION.md` | FlexLib upgrade guide, TLS wrapper notes |
| `docs/CHANGELOG.md` | Version history |
| `docs/planning/` | Product vision, design proposals, sprint plans |
| `Agent.md` | Recent work summary (session context) |

## Releases

### Version Bump Checklist (IMPORTANT!)

**You MUST update BOTH files when bumping the version:**

1. **`JJFlexRadio.vbproj`** - Update these three lines:
   ```xml
   <Version>4.1.X</Version>
   <AssemblyVersion>4.1.X.0</AssemblyVersion>
   <FileVersion>4.1.X.0</FileVersion>
   ```

2. **`My Project\AssemblyInfo.vb`** - Update these three lines:
   ```vb
   <Assembly: AssemblyVersion("4.1.X.0")>
   <Assembly: AssemblyFileVersion("4.1.X.0")>
   <Assembly: AssemblyInformationalVersion("4.1.X")>
   ```

**Why both?** The AssemblyInfo.vb attributes override the project file settings. If you only update the .vbproj, the compiled exe will have the OLD version number. This has confused multiple AI assistants (Claude, Codex, Gemini) - don't let it confuse you too!

### Building Clean Installers

**Problem:** Incremental builds may use cached binaries with old version numbers. The `--no-incremental` flag does NOT reliably fix this (see warning above).

**Solution:** Always do a clean build when creating release installers:

```batch
# Option 1: Use the build script (recommended)
build-installers.bat

# Option 2: Manual clean build
dotnet clean JJFlexRadio.vbproj -c Release -p:Platform=x64 && dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x64
dotnet clean JJFlexRadio.vbproj -c Release -p:Platform=x86 && dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x86
```

**Verify the version before distributing:**
```batch
powershell -Command "(Get-Item 'bin\x64\Release\net8.0-windows\win-x64\JJFlexRadio.exe').VersionInfo.ProductVersion"
```

### Creating a GitHub Release

1. **Bump version** in both files (see checklist above)

2. **Commit the version bump**:
   ```batch
   git add JJFlexRadio.vbproj "My Project\AssemblyInfo.vb"
   git commit -m "Bump version to 4.1.X"
   ```

3. **Create and push a tag**:
   ```batch
   git tag -a v4.1.X -m "Release 4.1.X - Brief description"
   git push origin main --tags
   ```

4. **Build installers locally**:
   ```batch
   build-installers.bat
   ```

5. **Create GitHub Release and upload**:
   - Go to GitHub → Releases → "Create a new release"
   - Select your tag, add release notes
   - Upload the x64 and x86 installer .exe files
   - Or use gh CLI:
   ```batch
   gh release create v4.1.X --title "JJFlexRadio 4.1.X" --notes "Release notes here"
   gh release upload v4.1.X "Setup JJFlexRadio_4.1.X_x64.exe" "Setup JJFlexRadio_4.1.X_x86.exe"
   ```

### Local Build Scripts

| Script | Purpose |
|--------|---------|
| `build-installers.bat` | Clean build + create both x64/x86 installers |
| `build-installers.bat x64` | Build x64 installer only |
| `build-installers.bat x86` | Build x86 installer only |
| `install.bat` | Low-level installer script (called by build-installers.bat) |

## Common Tasks

### Add new DSP feature
1. Check if FlexLib exposes the property (e.g., `Slice.RNNOn`)
2. Add wrapper property in `FlexBase.cs`
3. Check license with `Radio.FeatureLicense`
4. Add UI control in appropriate Filters form
5. Update Feature Availability tab if gated

### Update FlexLib
1. Copy new FlexLib to `FlexLib_API/`
2. Reapply `SslClientTls12.cs` wrapper
3. Update `TlsCommandCommunication.cs` to use wrapper
4. Verify TLS negotiation in remote connect
5. Update version references

### Trace File Location
- Boot trace: `%AppData%\JJFlexRadio\JJFlexRadioTrace.txt` (enabled when `BootTrace = True` in `globals.vb`)
- Multi-instance: `%AppData%\JJFlexRadio\JJFlexRadio2Trace.txt` (instance 2+)
- User-initiated trace: set via Operations → Tracing in the UI (`TraceAdmin.vb`)
- Tracing code: `JJTrace\Tracing.cs`

### Debug Remote/SmartLink
1. Check `AuthFormWebView2.cs` for Auth0 flow (uses WebView2/Edge)
2. Legacy `AuthForm.cs` (IE-based) kept as fallback, marked `[Obsolete]`
3. Verify TLS 1.2+ in network traces
4. See `docs/remote-migration.md` for current state

## Workflow

### Sprint Lifecycle (Standard Operating Procedure)

Every sprint follows this lifecycle. Claude (in the Desktop/orchestrator session) drives planning, setup, and merging. The user spawns CLI sessions to execute tracks.

#### Phase 1: Planning (Claude Desktop + User)
1. **Scope discussion** — User and Claude discuss what the sprint should accomplish
2. **Plan file creation** — Claude writes a detailed sprint plan to `docs/planning/` (named with ham-radio words, e.g. `barefoot-qrm-trap.md`)
3. **Track decomposition** — Claude analyzes the work and splits it into parallel tracks:
   - Identify independent work units (dialogs, features, files that don't overlap)
   - Group into tracks (max 6 concurrent tracks)
   - Identify dependencies between tracks (e.g., "Track A must complete Phase X before B/C can start")
   - Identify merge order (which tracks merge first, any conflict-prone areas)
4. **User approval** — User reviews track split, adjusts if needed

#### Phase 2: Setup (Claude Desktop)
Claude performs ALL setup before the user spawns any CLI sessions:

1. **Create branches** from the current base (usually `main`):
   ```batch
   git checkout -b sprintN/track-a
   ```

2. **Create worktrees** for each parallel track:
   ```batch
   # Track A stays in main repo: C:\dev\JJFlex-NG
   git worktree add ../jjflex-Nb sprintN/track-a -b sprintN/track-b
   git worktree add ../jjflex-Nc sprintN/track-a -b sprintN/track-c
   # ... up to 6 tracks
   ```
   **Naming convention:** `../jjflex-Nx` where N = sprint number, x = track letter.

3. **Write TRACK-INSTRUCTIONS.md** in each worktree root:
   - Complete file list with WinForms source → WPF target mapping (or equivalent)
   - Architecture rules and patterns to follow
   - Build commands specific to that worktree
   - Commit strategy (how often, what message format)
   - Any track-specific notes (dependencies, gotchas, special handling)

4. **Update the plan file** with track assignments, worktree paths, and branch names

5. **Report to user — MUST include execution order AND merge plan:**
   - Number of tracks to run
   - **Execution order** — Claude MUST explicitly tell the user which tracks to start first and which to start later:
     - If tracks have dependencies: specify the order (e.g., "Start Track A first. When Track A reports Phase 9.0 committed, start Tracks B and C.")
     - If all tracks are independent: say "Start all N tracks simultaneously."
     - If mixed: group them (e.g., "Start A and B now. Start C after A completes Step 1.")
   - The directory to `cd` into for each track
   - The exact prompt to type in each CLI session (always just: `Start Sprint N Track X from TRACK-INSTRUCTIONS.md`)
   - **Merge plan** — Claude MUST tell the user the planned merge order if it's non-trivial:
     - Which track is the merge target (usually Track A)
     - Whether merges can happen as tracks complete or must wait for all tracks
     - Any merge order constraints (e.g., "Track B must merge before Track C because C depends on B's DataGrid patterns")
     - If merge order doesn't matter: say "Tracks merge in any order as they complete."
     - This lets the user know what to expect and whether completing Track C before Track B changes anything

#### Phase 3: Execution (User spawns CLI sessions)
User opens Claude CLI sessions (one per track) following the execution order Claude specified.

Each CLI session:
- User `cd`s to the track's worktree directory
- User types: `Start Sprint N Track X from TRACK-INSTRUCTIONS.md`
- CLI reads its own `TRACK-INSTRUCTIONS.md` for full context
- Works independently in its own worktree directory
- Builds and commits within its own branch
- Reports completion when done

**The user reports track completion to Claude Desktop:** "Track A is done" / "Track B is done" etc.

#### Phase 4: Merging (Claude Desktop)
As tracks complete, Claude Desktop handles merges and keeps the user informed:

1. **Merge order** — Claude follows the merge plan communicated in Phase 2. If circumstances change (unexpected conflicts, track completing out of order), Claude informs the user before proceeding with an adjusted merge strategy.
2. **Standard merge process:**
   ```batch
   git checkout sprintN/track-a
   git merge sprintN/track-b --no-ff -m "Merge Track B into Track A"
   # Resolve conflicts if any (Claude Desktop handles this)
   git merge sprintN/track-c --no-ff -m "Merge Track C into Track A"
   ```
3. **Post-merge build verification** — clean build after each merge to catch integration issues. If build fails, Claude fixes conflicts/issues before merging the next track.
4. **Status updates** — Claude tells the user after each merge completes (e.g., "Track B merged into A, build clean. Waiting for Track C.")
5. **Final merge to main:**
   ```batch
   git checkout main
   git merge sprintN/track-a --no-ff -m "Sprint N: [description]"
   ```

#### Phase 5: Cleanup (Claude Desktop)
1. **Remove worktrees:**
   ```batch
   git worktree remove ../jjflex-Nb
   git worktree remove ../jjflex-Nc
   ```
2. **Delete track branches** (optional, after merge is confirmed good)
3. **Delete TRACK-INSTRUCTIONS.md** files (they're in git history if needed)
4. **Final cleanup phase** (sprint-specific: delete dead code, update docs, etc.)
5. **Clean build** both x64 and x86, verify installers
6. **Update Agent.md** with sprint completion status
7. **Archive sprint plan** to `docs/planning/agile/archive/`
8. **Create test matrix** at `docs/planning/agile/sprintN-test-matrix.md`

---

### Parallel Track Rules

**CRITICAL: Always use `git worktree` for parallel CLI sessions.** Do NOT just check out different branches in the same working directory — CLI sessions will fight over files, lose changes, and produce checkout races.

**Lesson learned (Sprint 6):** Using branches without worktrees caused CLI sessions to collide — multiple sessions sharing one working directory led to file corruption and build issues. Worktrees are mandatory, not optional.

| Rule | Details |
|------|---------|
| Max concurrent tracks | 6 (practical limit for Claude CLI sessions) |
| Worktree naming | `../jjflex-Nx` (N=sprint, x=track letter a-f) |
| Branch naming | `sprintN/track-x` |
| Track instructions | `TRACK-INSTRUCTIONS.md` in each worktree root |
| CLI prompt format | `Start Sprint N Track X from TRACK-INSTRUCTIONS.md` |
| Build isolation | Each worktree builds independently |
| Commit style | Track-specific prefix: `Sprint N Track X: description` |

### Track Dependency Handling

When tracks have dependencies (e.g., Track A creates a base class that B and C need):
- **Option 1:** Track A completes the shared work first, user reports done, Claude merges into B/C branches, then user starts B/C
- **Option 2:** Dependent tracks create a minimal stub version and note in their instructions that Track A's version is canonical at merge time
- **Option 3:** Serial-then-parallel — Track A runs solo first, then B/C/D/E/F run in parallel after A merges

### Commits
- Commit and push after completing each phase or significant chunk of work
- No PR required for work on feature branches
- Use descriptive commit messages following existing style

### Plan File Names
Plan files are named with three random ham-radio-flavored words, e.g. `barefoot-qrm-trap`. Use ham radio terms (QRM, QSO, ragchew, barefoot, rig, shack, pileup, splatter, etc.) mixed with random fun words. Keep it lighthearted — this is a ham radio project!

### Test Matrices
Create a separate test matrix file for each sprint: `docs/planning/agile/sprintN-test-matrix.md`. This keeps the test checklist accessible during testing without having to dig through the full sprint plan. Include per-track functional tests, integration tests, and a screen reader matrix (JAWS + NVDA). Archive alongside the sprint plan when done.

### Resuming Work
If a session ends mid-task, tell Claude: "Resume [phase/task name] from `docs/barefoot-qrm-trap.md`"

Claude will:
1. Read the plan and `git status` to see what's done
2. Check `Agent.md` for recent work context
3. Continue from where work left off

To help resumption, Claude should:
- Update `Agent.md` with current progress before ending sessions
- Commit partial work with clear "WIP:" prefix if mid-phase
- Note the current step in commit messages when practical
