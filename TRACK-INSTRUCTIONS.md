# Sprint 9 — Track A: High-Priority Dialogs + Dialog Base

**Branch:** `sprint9/track-a`
**Worktree:** `C:\dev\JJFlex-NG`
**Plan:** See `C:\Users\nrome\.claude\plans\frolicking-forging-map.md` for full context.

## Architecture Rules (user-approved)

- **Code-behind pattern** — NOT MVVM
- **C# in JJFlexWpf project** — all new WPF code goes here
- **Delegate-based rig wiring** — WPF controls use Func/Action delegates, NOT direct FlexLib references
- All dialogs go in `JJFlexWpf/Dialogs/` folder
- All dialogs inherit from `JJFlexDialog` base class (you create this in Step 1)
- Build clean after each major dialog: `dotnet clean JJFlexRadio.sln -c Debug -p:Platform=x64 && dotnet build JJFlexRadio.sln -c Debug -p:Platform=x64 --verbosity minimal`
- Commit after each phase or logical group
- Read the WinForms source for each dialog BEFORE writing the WPF replacement
- Accessibility: `AutomationProperties.Name` on every interactive control

## Step 1: Phase 9.0 — Dialog Base Infrastructure (DO THIS FIRST)

Create shared dialog base class and styles that ALL Sprint 9 dialogs will use.

### Create `JJFlexWpf/JJFlexDialog.cs`:
- Inherits from `System.Windows.Window`
- ESC key closes dialog (PreviewKeyDown handler, sets DialogResult = false)
- Enter key on OK button triggers accept (optional, per-dialog)
- Owner defaults to MainWindow (centers dialog on parent)
- Icon inherited from application
- On Loaded: focus first interactive control (find first focusable element)
- `AutomationProperties.Name` auto-set from `Window.Title`
- Helper method: `AddStandardButtons(Action okAction, Action cancelAction)` — creates OK/Cancel StackPanel

### Create `JJFlexWpf/Styles/DialogStyles.xaml`:
- Resource dictionary with standard button styles (OK, Cancel, Apply)
- Consistent margins, padding, font sizes
- Merge into App.xaml or reference from JJFlexDialog

### Build clean, commit: "Sprint 9 Phase 9.0: Dialog base infrastructure"

## Step 2: Phase 9.1 — Convert These Dialogs

Convert each WinForms form to a WPF Window inheriting from JJFlexDialog. Read the original source first.

| # | WinForms Source | WPF Target | Lines | Key Notes |
|---|---|---|---|---|
| 1 | `RigSelector.vb` | `Dialogs/RigSelectorDialog.xaml(.cs)` | 437 | Radio discovery, auto-connect. Shown at startup. References Form1 — use delegates instead. |
| 2 | `Welcome.vb` | `Dialogs/WelcomeDialog.xaml(.cs)` | 39 | First-run text display. Very simple. |
| 3 | `PersonalInfo.vb` | `Dialogs/PersonalInfoDialog.xaml(.cs)` | 352 | Operator setup — text fields, combos for license class/grid square. References Form1. |
| 4 | `Profile.vb` | `Dialogs/ProfileDialog.xaml(.cs)` | 209 | Profile list with new/edit/delete operations. References Form1. |
| 5 | `AuthFormWebView2.cs` | `Dialogs/AuthDialog.xaml(.cs)` | 593 | SmartLink WebView2 login. PKCE OAuth, JWT parsing, JavaScript injection. Keep WebView2 control — just re-host in WPF Window. |
| 6 | `FlexInfo.cs` (in Radios/) | `Dialogs/RadioInfoDialog.xaml(.cs)` | 529 | TabControl: General info + Feature Availability matrix. License checking, DSP control display. |
| 7 | `Radios/AutoConnectSettingsDialog.cs` | `Dialogs/AutoConnectSettingsDialog.xaml(.cs)` | 161 | Checkboxes for auto-connect options. Simple. |
| 8 | `Radios/AutoConnectFailedDialog.cs` | `Dialogs/AutoConnectFailedDialog.xaml(.cs)` | 187 | 4-button dialog: Try Again / Disable Auto-Connect / Choose Another / Cancel. Static factory method pattern. |
| 9 | `Radios/SmartLinkAccountSelector.cs` | `Dialogs/SmartLinkAccountDialog.xaml(.cs)` | 366 | ListBox of saved accounts + Add/Edit/Delete/Connect buttons. |
| 10 | `LoginName.vb` | `Dialogs/LoginNameDialog.xaml(.cs)` | 51 | Single text input for operator callsign. Very simple. |
| 11 | `ProfileWorker.vb` | `Dialogs/ProfileWorkerDialog.xaml(.cs)` | 92 | Background profile loader with progress display. References Form1. |

## Step 3: Migrate Form1 References in KeyCommands.vb

There are 24 references to `Form1` in `KeyCommands.vb`. Replace them all with `WpfMainWindow` (the accessor property already exists in `globals.vb`). This is what allows Form1.vb to be deleted in Phase 9.5.

## Step 4: Final Build + Commit

- Clean build both x64 and x86 Debug
- Verify no errors
- Commit: "Sprint 9 Track A: High-priority dialogs + Form1 ref migration"
- Update Agent.md with Track A completion status
