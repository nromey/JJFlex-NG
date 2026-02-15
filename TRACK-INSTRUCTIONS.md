# Sprint 9 — Track C: Low-Priority + Library Dialogs

**Branch:** `sprint9/track-c`
**Worktree:** `C:\dev\jjflex-9c`
**Plan:** See `C:\Users\nrome\.claude\plans\frolicking-forging-map.md` for full context.

## Architecture Rules (user-approved)

- **Code-behind pattern** — NOT MVVM
- **C# in JJFlexWpf project** — all new WPF code goes here
- **Delegate-based rig wiring** — WPF controls use Func/Action delegates, NOT direct FlexLib references
- All dialogs go in `JJFlexWpf/Dialogs/` folder
- All dialogs inherit from `JJFlexDialog` base class (created by Track A in Phase 9.0)
- If `JJFlexDialog.cs` doesn't exist yet (Track A hasn't committed it), create a minimal version: `public class JJFlexDialog : Window` with ESC-to-close. Track A's version will be the canonical one at merge time.
- Build clean after each major dialog: `dotnet clean JJFlexRadio.sln -c Debug -p:Platform=x64 && dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal`
- Commit frequently in logical batches
- Read the WinForms source for each dialog BEFORE writing the WPF replacement
- Accessibility: `AutomationProperties.Name` on every interactive control
- **Many of these are trivial (<50 lines)** — batch the simple ones for speed

## Phase 9.3 — Low-Priority Root-Level Dialogs

### Batch 1: Trivial dialogs (< 50 lines each — do these fast)

| # | WinForms Source | WPF Target | Lines | Notes |
|---|---|---|---|---|
| 1 | `FreqInput.vb` | `Dialogs/FreqInputDialog.xaml(.cs)` | 22 | Single numeric frequency input. |
| 2 | `ScanName.vb` | `Dialogs/ScanNameDialog.xaml(.cs)` | 18 | Single text input for scan name. |
| 3 | `ShowStationNames.vb` | `Dialogs/ShowStationNamesDialog.xaml(.cs)` | 19 | Station name list display. |
| 4 | `ReverseBeacon.vb` | `Dialogs/ReverseBeaconDialog.xaml(.cs)` | 22 | Reverse Beacon Network status. |
| 5 | `CMDLine.vb` | `Dialogs/CMDLineDialog.xaml(.cs)` | 22 | Command line help text. |
| 6 | `SelectScan.vb` | `Dialogs/SelectScanDialog.xaml(.cs)` | 34 | List selection for saved scans. |
| 7 | `AboutProgram.vb` | `Dialogs/AboutProgramDialog.xaml(.cs)` | 41 | Product info display. |

### Batch 2: Simple dialogs (50-100 lines)

| # | WinForms Source | WPF Target | Lines | Notes |
|---|---|---|---|---|
| 8 | `About.vb` | `Dialogs/AboutDialog.xaml(.cs)` | 66 | About box — version, credits. |
| 9 | `ShowHelp.vb` | `Dialogs/ShowHelpDialog.xaml(.cs)` | 74 | Help documentation viewer. |
| 10 | `CWMessageUpdate.vb` | `Dialogs/CWMessageUpdateDialog.xaml(.cs)` | 88 | Edit existing CW macro message. |
| 11 | `CWMessageAdd.vb` | `Dialogs/CWMessageAddDialog.xaml(.cs)` | 93 | Add new CW macro — name + message fields. |
| 12 | `ManageGroups.vb` | `Dialogs/ManageGroupsDialog.xaml(.cs)` | 93 | Memory group tree editor. References Form1. |
| 13 | `TraceAdmin.vb` | `Dialogs/TraceAdminDialog.xaml(.cs)` | 95 | Trace config — file picker, trace level selector. |
| 14 | `ManageGroupsEdit.vb` | `Dialogs/ManageGroupsEditDialog.xaml(.cs)` | 71 | Single group name/frequency edit. |

### Batch 3: Medium dialogs (100-260 lines)

| # | WinForms Source | WPF Target | Lines | Notes |
|---|---|---|---|---|
| 15 | `ShowBands.vb` | `Dialogs/ShowBandsDialog.xaml(.cs)` | 119 | Band/license/mode filter display with multi-select lists. |
| 16 | `MemoryScan.vb` | `Dialogs/MemoryScanDialog.xaml(.cs)` | 121 | Scan parameter editor — combos for freq/name. |
| 17 | `CommandFinder.vb` | `Dialogs/CommandFinderDialog.xaml(.cs)` | 126 | Search keyboard commands — text search + results list. |
| 18 | `Lister.vb` | `Dialogs/ListerDialog.xaml(.cs)` | 166 | Debug radio object browser — TreeView of Flex radio objects. References Form1. |
| 19 | `MemoryGroup.vb` | `Dialogs/MemoryGroupDialog.xaml(.cs)` | 245 | Memory group editor — tree structure, name validation. References Form1. |
| 20 | `LogCharacteristics.vb` | `Dialogs/LogCharacteristicsDialog.xaml(.cs)` | 252 | Log statistics — grid display of log stats, calculations. |
| 21 | `LOTWMerge.vb` | `Dialogs/LOTWMergeDialog.xaml(.cs)` | 256 | LOTW sync — file processing, database merge, dup detection. References Form1. |

## Phase 9.4 — Library Dialogs

### MsgLib (used heavily throughout the app)

| # | WinForms Source | WPF Target | Lines | Notes |
|---|---|---|---|---|
| 22 | `MsgLib/MessageForm.cs` | `Dialogs/MessageDialog.xaml(.cs)` | 81 | Generic message/confirm dialog with optional "Don't Show Again" checkbox. Used by many callers. Dynamic content via custom UserControl. |

### JJFlexControl (macro key configuration)

| # | WinForms Source | WPF Target | Lines | Notes |
|---|---|---|---|---|
| 23 | `JJFlexControl/SetupKeysAndActions.cs` | `Dialogs/SetupKeysDialog.xaml(.cs)` | 171 | Macro key configuration — key binding grid, action selector. |
| 24 | `JJFlexControl/ShowKeysAndActions.cs` | `Dialogs/ShowKeysDialog.xaml(.cs)` | 78 | Read-only display of active key bindings. |

### JJArclusterLib (DX Cluster)

| # | WinForms Source | WPF Target | Lines | Notes |
|---|---|---|---|---|
| 25 | `JJArclusterLib/ClusterForm.cs` | `Dialogs/ClusterDialog.xaml(.cs)` | 850+ | **COMPLEX**: DX Cluster telnet connection. Real-time stream display, multi-threaded text updates, beep/track toggle, message parsing. This is the biggest dialog on Track C. |

### Radios library (small utility dialogs)

| # | WinForms Source | WPF Target | Lines | Notes |
|---|---|---|---|---|
| 26 | `Radios/FlexATUMemories.cs` | `Dialogs/ATUMemoriesDialog.xaml(.cs)` | 67 | ATU memory list display. Simple. |
| 27 | `Radios/PanListForm.cs` | `Dialogs/PanListDialog.xaml(.cs)` | 51 | Frequency pan range picker — ListBox selection. |
| 28 | `Radios/GetFile.cs` | `Dialogs/GetFileDialog.xaml(.cs)` | 64 | File picker wrapper — thin wrapper around OpenFileDialog. |

### JJLogLib (contest log templates)

| # | WinForms Source | WPF Target | Lines | Notes |
|---|---|---|---|---|
| 29 | `JJLogLib/DefaultLog.cs` | `Dialogs/LogTemplateDialog.xaml(.cs)` | varies | Default log template. Consider: one shared WPF dialog with different field configs for each contest type. |
| 30 | `JJLogLib/FieldDay.cs` | (share LogTemplateDialog) | varies | ARRL Field Day template. |
| 31 | `JJLogLib/NASprint.cs` | (share LogTemplateDialog) | varies | NA Sprint template. |
| 32 | `JJLogLib/SKCCWESLog.cs` | (share LogTemplateDialog) | varies | SKCC Weekend Sprint template. |

### JJW2WattMeter (if still used)

| # | WinForms Source | WPF Target | Lines | Notes |
|---|---|---|---|---|
| 33 | `JJW2WattMeter/ConfigForm.cs` | `Dialogs/WattMeterConfigDialog.xaml(.cs)` | 95 | Watt meter configuration — COM port, type, disposition. |

## Build + Commit Strategy

- **Batch the trivials**: Do all < 50 line dialogs in one commit
- **Group by complexity**: Simple batch, medium batch, library batch
- Suggested commits:
  - "Sprint 9 Track C: Trivial dialogs (FreqInput, ScanName, etc.)" (batch 1)
  - "Sprint 9 Track C: Simple dialogs (About, CW, Help, Trace)" (batch 2)
  - "Sprint 9 Track C: Medium dialogs (Bands, Memory, Log stats, LOTW)" (batch 3)
  - "Sprint 9 Track C: Library dialogs (MessageForm, Keys, Cluster)" (phase 9.4)
  - "Sprint 9 Track C: Log templates + utility dialogs" (remaining 9.4)
- Final clean build both x64 and x86 Debug
- Update Agent.md with Track C completion status
