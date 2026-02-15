# Sprint 10: FlexBase.cs Decoupling — Cut the Upward Wires

## Context

Sprints 8-9 converted all WinForms UI to WPF, but the old WinForms files can't be deleted yet. FlexBase.cs (the core rig abstraction, 6,500+ lines) reaches *upward* into old WinForms dialog types — Flex6300Filters, FlexMemories, FlexATUMemories, SmartLinkAccountSelector, GetFile. And globals.vb/LogEntry.vb/LOTWMerge.vb reference Form1 directly.

The WPF replacements are all wired with clean delegate-based patterns (FiltersDspControl, MemoriesDialog, etc.). The old WinForms types compile as dead code — never instantiated at runtime — but the compiler still needs them. Sprint 10 cuts these wires so we can finally delete ~8,000 lines of dead WinForms code and the entire RadioBoxes project.

**Approach:** Extract interfaces for the methods FlexBase actually calls, point FlexBase at those interfaces instead of concrete WinForms types, then delete the old types.

---

## Phase 10.1 — Form1 References (8 refs, easiest wins)

**Goal:** Eliminate all `Form1.Xxx` references so Form1.vb can be deleted.

**Only 3 distinct Form1 members are used:**
- `Form1.StatusBox` (RadioBoxes.MainBox) — 5 refs
- `Form1.ScanTmr` (Timer) — 2 refs
- `Form1.SetupOperationsMenu()` — 1 ref

**The fix:** globals.vb already has a `WpfMainWindow` property (line 350) that returns the WPF MainWindow. We just need MainWindow to expose equivalents for these 3 members, then redirect.

### Step 1: Add members to WPF MainWindow

**File:** `JJFlexWpf/MainWindow.xaml.cs`

Add a `WriteStatus(string field, string value)` method that writes to the WPF StatusBar (the StatusBox replacement). MainWindow already has StatusBar items — this just exposes a named-field write API matching the old `StatusBox.Write(field, value)` pattern.

Add a `ScanTimerEnabled` property and `ScanTimer` accessor — the WPF MainWindow already has a DispatcherTimer; scan may reuse it or get its own timer.

Add a `SetupOperationsMenu()` method (or verify it already exists in MenuBuilder).

### Step 2: Redirect globals.vb wrapper properties

**File:** `globals.vb`

Change the 3 wrapper properties (lines 759-775) from `Form1.Xxx` to `WpfMainWindow.Xxx`:

```vb
' StatusBox — change from Form1.StatusBox to WpfMainWindow.WriteStatus()
' ScanInProcess — change from Form1.ScanTmr.Enabled to WpfMainWindow.ScanTimerEnabled
' scanTimer — change from Form1.ScanTmr to WpfMainWindow.ScanTimer
' SetupOperationsMenu — change Form1.SetupOperationsMenu() to WpfMainWindow.SetupOperationsMenu()
```

### Step 3: Fix direct bypasses

**Files:** `LogEntry.vb` (line 248), `LOTWMerge.vb` (line 107)

Both bypass the globals wrapper and call `Form1.StatusBox.Write(...)` directly. Change to call the globals `StatusBox` wrapper (which now routes to WpfMainWindow), or call `WpfMainWindow.WriteStatus(...)` directly.

### Step 4: Delete Form1.vb, Form1.designer.vb, Form1.resx

Remove from disk and comment out/remove Compile entries in JJFlexRadio.vbproj. Also delete LogPanel.vb and RadioPane.vb (only referenced by Form1.vb).

**Build and verify.**

---

## Phase 10.2 — IFilterControl Interface (12 refs in FlexBase.cs)

**Goal:** Replace `Flex6300Filters FilterObj` with an interface so FlexBase doesn't need the concrete type.

### Step 1: Define IFilterControl interface

**File:** New `Radios/IFilterControl.cs`

```csharp
public interface IFilterControl
{
    void RXFreqChange(Slice s);
    void RXFreqChange(List<Slice> slices);
    void PanSetup();
    ulong ZeroBeatFreq();
    void OperatorChangeHandler();
    void Close();
}
```

These are the ONLY 6 methods FlexBase calls on FilterObj (confirmed by research agent — 12 call sites total, all use one of these 6 methods).

### Step 2: Change FlexBase.cs FilterObj type

**File:** `Radios/FlexBase.cs`

```csharp
// Old: protected Flex6300Filters FilterObj;
protected IFilterControl FilterObj;  // Now an interface
```

Remove the cast on line 1201:
```csharp
// Old: ((Flex6300Filters)RigFields.RigControl).Close();
FilterObj?.Close();
```

The 10 other call sites (`FilterObj.RXFreqChange(s)`, `FilterObj.PanSetup()`, etc.) don't change — they already call the exact methods on the interface.

### Step 3: Create a WPF adapter that implements IFilterControl

**File:** New `Radios/WpfFilterAdapter.cs`

This adapter bridges from IFilterControl to the WPF FiltersDspControl delegates. It implements `IFilterControl` and forwards calls to the WPF MainWindow's FiltersDspControl.

```csharp
public class WpfFilterAdapter : IFilterControl
{
    private readonly FlexBase rig;

    public void RXFreqChange(Slice s) { /* update WPF frequency display */ }
    public void PanSetup() { /* initialize WPF pan display */ }
    public ulong ZeroBeatFreq() { /* query waterfall data */ }
    public void OperatorChangeHandler() { /* reload config */ }
    public void Close() { /* cleanup */ }
}
```

### Step 4: Wire adapter in place of Flex6300Filters

Currently Flex6300Filters is created in `FlexBase.Start()` (line 415):
```csharp
FilterObj = new Flex6300Filters(this);
```

Change to:
```csharp
FilterObj = new WpfFilterAdapter(this);
```

### Step 5: Move RigFields_t setup out of Flex6300Filters

Currently `Flex6300Filters` constructor sets `rig.RigFields` (line 1064). The WpfFilterAdapter or the app initialization code needs to do this instead. The key piece is the `updateDel` — which the WPF MainWindow's poll timer already calls `UpdateAllControls()` on FiltersDspControl. We just need to wire `RigFields.RigUpdate` to that.

**Build and verify. Flex6300Filters.cs is no longer referenced by FlexBase.**

---

## Phase 10.3 — IMemoryManager Interface (9 refs in FlexBase.cs)

**Goal:** Replace `FlexMemories memoryHandling` with an interface.

### Step 1: Define IMemoryManager interface

**File:** New `Radios/IMemoryManager.cs`

```csharp
public interface IMemoryManager
{
    int CurrentMemoryChannel { get; set; }
    int NumberOfMemories { get; }
    bool SelectMemory();
    bool SelectMemoryByName(string name);
    List<string> MemoryNames();
    IEnumerable<IMemoryElement> SortedMemories { get; }
}

public interface IMemoryElement
{
    Memory Value { get; }
}
```

### Step 2: Change FlexBase.cs memoryHandling

```csharp
// Old: private FlexMemories memoryHandling { get { return (FlexMemories)RigFields.Memories; } }
private IMemoryManager memoryManager;  // Direct field, no cast needed
```

Update all 9 usage sites to use `memoryManager` instead of `memoryHandling`. The method signatures match exactly.

For `FlexMemories.MemoryElement` → use `IMemoryElement` (just needs `.Value` property returning `Memory`).

### Step 3: Create WPF memory adapter

**File:** New `Radios/WpfMemoryAdapter.cs`

Implements `IMemoryManager` by wrapping the FlexLib `Radio.MemoryList` directly (the same data source FlexMemories.cs was wrapping). This is pure data access — no UI.

### Step 4: Wire adapter

Set `memoryManager = new WpfMemoryAdapter(theRadio)` during radio connection (where FlexMemories was previously created inside Flex6300Filters constructor, line 1063).

**Build and verify. FlexMemories.cs is no longer referenced by FlexBase.**

---

## Phase 10.4 — Simple Dialog Replacements (3 easy wins)

### FlexATUMemories (3 lines)

**File:** `Radios/FlexBase.cs`, method `AntennaTunerMemories()` (lines 3122-3127)

```csharp
// Old:
public void AntennaTunerMemories()
{
    Form theForm = new FlexATUMemories(this);
    theForm.ShowDialog();
    theForm.Dispose();
}

// New: use delegate to show WPF dialog
public Action ShowATUMemoriesDialog { get; set; }

public void AntennaTunerMemories()
{
    ShowATUMemoriesDialog?.Invoke();
}
```

Wire in app init: `rig.ShowATUMemoriesDialog = () => new ATUMemoriesDialog(...).ShowDialog();`

### SmartLinkAccountSelector (2 call sites)

**File:** `Radios/FlexBase.cs` line 669, `Radios/SmartLinkAccountManager.cs` line 97

Both create `new SmartLinkAccountSelector(...)` and read `NewLoginRequested` + `SelectedAccount`.

Replace with delegates on FlexBase:
```csharp
public Func<SmartLinkAccountManager, (bool newLogin, SmartLinkAccount selected, bool ok)> ShowAccountSelector { get; set; }
```

Wire to WPF SmartLinkAccountDialog in app init.

### GetFile (2 call sites in FlexDB.cs)

**File:** `Radios/FlexDB.cs` lines 63, 187

Replace `new GetFile(title, suffix, saveMode)` with delegates:
```csharp
public Func<string, string, bool, (string fileName, bool ok)> ShowFileDialog { get; set; }
```

Or simpler: just use `Microsoft.Win32.OpenFileDialog` / `SaveFileDialog` directly in FlexDB — no custom form needed. The WPF GetFileDialog already wraps these.

### WebBrowserHelper (1 call, likely dead)

**File:** `Radios/FlexBase.cs` line 550

Delete the call to `WebBrowserHelper.ClearCache()` — this was for the old IE WebBrowser control. WebView2 has its own cache management. Delete `WebBrowserHelper.cs` too.

**Build and verify.**

---

## Phase 10.5 — Delete Dead Code + RadioBoxes

With all interfaces wired and adapters in place:

1. **Delete from Radios/:**
   - `Flex6300Filters.cs` + `.Designer.cs` (2,822+ lines)
   - `FlexFilters.cs` (trivial subclass)
   - `FlexWaterfall.cs` (used only by Flex6300Filters)
   - `FlexMemories.cs` + `.Designer.cs`
   - `FlexATUMemories.cs` + `.Designer.cs`
   - `SmartLinkAccountSelector.cs` + `.Designer.cs`
   - `GetFile.cs` + `.Designer.cs`
   - `WebBrowserHelper.cs`
   - Any other old dialog .cs/.Designer.cs files in Radios/ that have WPF replacements

2. **Delete RadioBoxes/ project entirely** — no longer referenced by anything

3. **Delete from root:**
   - `Form1.vb`, `Form1.designer.vb`, `Form1.resx`
   - `LogPanel.vb`
   - `RadioPane.vb`
   - `StationLookup.Designer.vb` (orphan from Sprint 9)

4. **Update project files:**
   - Remove RadioBoxes from `JJFlexRadio.sln`
   - Remove RadioBoxes ProjectReference from `JJFlexRadio.vbproj`
   - Remove RadioBoxes ProjectReference from `Radios/Radios.csproj`
   - Comment out or remove Compile entries for deleted files

5. **Clean build x64 + x86 Release — must be 0 errors**

---

## Phase 10.6 — Cleanup & Verification

1. Update `Agent.md` with Sprint 10 completion
2. Update `docs/CHANGELOG.md` (user-facing — "cleaned house, removed thousands of lines of old code")
3. Run full test matrix from sprint9-test-checklist.md
4. Build both installers, verify timestamps
5. Archive sprint plan

---

## Files to Create

| File | Purpose |
|------|---------|
| `Radios/IFilterControl.cs` | Interface for filter/DSP operations |
| `Radios/IMemoryManager.cs` | Interface for memory channel operations |
| `Radios/WpfFilterAdapter.cs` | Bridges IFilterControl → WPF FiltersDspControl |
| `Radios/WpfMemoryAdapter.cs` | Bridges IMemoryManager → FlexLib Radio.MemoryList |

## Files to Modify

| File | Changes |
|------|---------|
| `Radios/FlexBase.cs` | FilterObj type → IFilterControl, memoryHandling → IMemoryManager, ATU/SmartLink/GetFile → delegates |
| `Radios/FlexDB.cs` | GetFile → standard file dialog or delegate |
| `Radios/SmartLinkAccountManager.cs` | SmartLinkAccountSelector → delegate |
| `globals.vb` | Form1.StatusBox/ScanTmr/SetupOperationsMenu → WpfMainWindow equivalents |
| `LogEntry.vb` | Form1.StatusBox → globals wrapper or WpfMainWindow |
| `LOTWMerge.vb` | Form1.StatusBox → globals wrapper or WpfMainWindow |
| `JJFlexWpf/MainWindow.xaml.cs` | Add WriteStatus(), ScanTimer accessors, SetupOperationsMenu() |
| `JJFlexRadio.vbproj` | Remove RadioBoxes ref, Form1/LogPanel/RadioPane entries |
| `Radios/Radios.csproj` | Remove RadioBoxes ref, add new interface files |
| `JJFlexRadio.sln` | Remove RadioBoxes project entry |

## Files to Delete (~8,000+ lines of dead code)

| File | Lines | Replaced By |
|------|-------|-------------|
| `Form1.vb` | 3,757 | JJFlexWpf/MainWindow.xaml |
| `Form1.designer.vb` | 573 | (same) |
| `Form1.resx` | 129 | (same) |
| `LogPanel.vb` | 1,217 | JJFlexWpf/LogEntryControl.xaml |
| `RadioPane.vb` | 264 | JJFlexWpf/Controls/RadioPaneControl.xaml |
| `Radios/Flex6300Filters.cs` | ~2,822 | JJFlexWpf/Controls/FiltersDspControl.xaml |
| `Radios/Flex6300Filters.Designer.cs` | ~500 | (same) |
| `Radios/FlexFilters.cs` | ~10 | (dead subclass) |
| `Radios/FlexWaterfall.cs` | ~260 | (internal to Flex6300Filters) |
| `Radios/FlexMemories.cs` | ~673 | JJFlexWpf/Dialogs/MemoriesDialog.xaml |
| `Radios/FlexATUMemories.cs` | ~67 | JJFlexWpf/Dialogs/ATUMemoriesDialog.xaml |
| `Radios/SmartLinkAccountSelector.cs` | ~366 | JJFlexWpf/Dialogs/SmartLinkAccountDialog.xaml |
| `Radios/GetFile.cs` | ~64 | JJFlexWpf/Dialogs/GetFileDialog.xaml |
| `Radios/WebBrowserHelper.cs` | ~50 | (dead — WebView2 replaced IE) |
| `RadioBoxes/` (entire project) | ~2,000 | JJFlexWpf/Controls/* |
| `StationLookup.Designer.vb` | ~240 | (orphan) |
| **Total** | **~13,000** | |

## Verification

1. **Clean build** x64 + x86 Release: 0 errors
2. **No RadioBoxes references** anywhere in solution
3. **No Form1 references** anywhere in solution
4. **No Flex6300Filters/FlexMemories/FlexATUMemories** references outside of the new adapters
5. **Runtime test:** Connect to radio, verify filters/DSP work, memories work, scan works, status bar updates
6. **Screen reader test:** JAWS + NVDA — all existing functionality preserved

## Execution Strategy

This is a single-track sprint — all changes are in FlexBase.cs and related files, so parallel tracks would just create merge conflicts. Estimated 2-3 sessions.

**Phase order matters:**
1. Phase 10.1 (Form1) — independent, quick win
2. Phase 10.2 (IFilterControl) — the big one, most surgical
3. Phase 10.3 (IMemoryManager) — follows same pattern as 10.2
4. Phase 10.4 (simple dialogs) — quick delegate swaps
5. Phase 10.5 (delete dead code) — the payoff
6. Phase 10.6 (cleanup) — docs and verification
