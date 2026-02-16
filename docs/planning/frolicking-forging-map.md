# Sprint 11: WPF Adapters + WinForms Removal — Kill the Old Forms

## Context

Sprint 10 decoupled FlexBase.cs from WinForms types using interfaces (IFilterControl, IMemoryManager) and delegates. But `FlexBase.Start()` still creates `new Flex6300Filters(this)` — the old WinForms UserControl is still the live implementation. Form1.vb is still the hidden bridge form that wires up radio connections. RadioBoxes/ still compiles. ~13,000 lines of dead WinForms code remain.

**This sprint creates WPF adapters to fully replace the WinForms implementations, moves radio wiring from Form1 to MainWindow, and deletes everything old.** By the end, there will be zero WinForms forms in the app.

## Track Structure (3 parallel tracks)

The work decomposes into 3 tracks. **Track A runs first** (it creates the adapters everything else depends on). **Tracks B and C run in parallel after Track A merges.**

```
Track A: Radios/ adapters (sequential, runs first)
   |
   +---> Track B: MainWindow wiring + Form1 kill (parallel)
   |
   +---> Track C: Dead code deletion + cleanup (parallel)
```

---

## Track A — Adapters (runs first, solo)

**Branch:** `sprint11/track-a`
**Directory:** `C:\dev\JJFlex-NG`

### Phase 11.1: Extract PanAdapterManager from Flex6300Filters

The pan adapter / braille waterfall code (~600 lines) is pure radio logic with zero UI. Extract it into a standalone class.

**New file: `Radios/PanAdapterManager.cs`**

Extract from `Flex6300Filters.cs`:
- `PanData` inner class → top-level in PanAdapterManager
- `PanRanges` usage (panRanges field, segment management)
- `iRXFreqChange()` + `panParameterSetup()` + `copyPan()`
- `zeroBeatProc()` + zero-beat voting algorithm
- `panDataHandler` + `waterfallDataHandler` event handlers
- Fields: `brailleWidth`, `segment`, `segmentLock`, `flexPan`, `generation`, `currentPanData`, `PanReady`, `panadapter`/`waterfall` accessors
- `getConfig()` / `OpsConfigInfo` (operator config loading)

Constructor: `PanAdapterManager(FlexBase rig, string operatorsDirectory, int brailleCells)`

The braille output callback replaces `TextOut.PerformGenericFunction(PanBox, ...)` — the adapter provides an `Action<string>` for pan display text.

**Decouple FlexWaterfall (`Radios/FlexWaterfall.cs`):**
- `private Flex6300Filters parent` → `private PanAdapterManager parent`
- `private FlexBase rig { get { return Flex6300Filters.rig; } }` → accept FlexBase via constructor
- Constructor: `FlexWaterfall(PanAdapterManager p, FlexBase rig, ulong l, ulong h, int c)`
- `Flex6300Filters.PanData` → `PanAdapterManager.PanData`
- `parent.iRXFreqChange(...)` → `parent.FreqChangeCallback(...)` or equivalent

**Move inner types out of Flex6300Filters:**
- `trueFalseElement`, `offOnElement` → move to `Radios/FilterTypes.cs` (used by FlexTNF, TXControls)
- `PanData` → move to PanAdapterManager
- `modeChangeClass` → stays with Flex6300Filters (dies with it), WPF has its own mode change logic

**Build checkpoint:** Flex6300Filters still exists but pan adapter code routes through PanAdapterManager. Both old and new code compile.

### Phase 11.2: Create WpfFilterAdapter

**New file: `Radios/WpfFilterAdapter.cs`** — implements `IFilterControl`

```csharp
public class WpfFilterAdapter : IFilterControl
{
    private FlexBase rig;
    private PanAdapterManager panManager;

    public WpfFilterAdapter(FlexBase rig) { ... }

    public void RXFreqChange(object o) => panManager?.RXFreqChange(o);
    public void PanSetup() { /* create PanAdapterManager, create WpfMemoryManager, wire RigFields */ }
    public ulong ZeroBeatFreq() => panManager?.ZeroBeatFreq() ?? 0;
    public void OperatorChangeHandler() => panManager?.OperatorChangeHandler();
    public void Close() => panManager?.Close();
}
```

Key responsibilities:
- Constructor: set `rig.Callouts.GetSWRText` delegate
- `PanSetup()`: create PanAdapterManager, create WpfMemoryManager, set up RigFields_t, call memory manager Setup()

### Phase 11.3: Create WpfMemoryManager

**New file: `Radios/WpfMemoryManager.cs`** — implements `IMemoryManager`

Headless class (NOT a Form). Extracts the pure data logic from FlexMemories.cs:
- Sorted memory list (by group, then name)
- `MemoryElement` inner class implementing `IMemoryElement`
- `Setup()` method (called from PanSetup)
- `SelectMemory()` enqueues via `rig.q`
- `SelectMemoryByName()` finds and selects

### Phase 11.4: Refactor RigFields_t

**File: `Radios/FlexBase.cs`**

Change WinForms types to interfaces:
- `Control RigControl` → remove (WPF adapter is not a Control)
- `Form Memories` → `IMemoryManager Memories`
- `Form Menus` → remove (dead, already `#if 0`)
- `Control[] ScreenFields` → remove (WPF manages own layout)
- `updateDel RigUpdate` → keep (pure delegate)

Simplify constructors to: `RigFields_t(updateDel rtn, IMemoryManager mem)`

Update `memoryHandling`: `get { return RigFields?.Memories; }` — no cast needed.

### Phase 11.5: Wire FlexBase.Start() to WpfFilterAdapter

**File: `Radios/FlexBase.cs`**

Change line 415: `FilterObj = new Flex6300Filters(this)` → `FilterObj = new WpfFilterAdapter(this)`

**Build checkpoint:** Full solution build. Flex6300Filters is no longer instantiated but still compiles.

---

## Track B — MainWindow Wiring + Form1 Kill (after Track A)

**Branch:** `sprint11/track-b`
**Directory:** worktree `../jjflex-11b`

### Phase 11.6: Move radio wiring from Form1 to MainWindow

**File: `JJFlexWpf/MainWindow.xaml.cs`** — fill in the "Phase 8.4+" stubs

Move from Form1.vb:
1. **OpenParms setup** (Form1 lines 308-321) — ProgramName, ConfigDirectory, BrailleCells, FormatFreq delegates, etc.
2. **openTheRadio()** (Form1 lines 305-397) — auto-connect, RigSelector, `RigControl.Start()`
3. **Poll timer body** (Form1 UpdateStatus) — wire `RigFields.RigUpdate()`, DSP control refresh
4. **Power handlers** — powerNowOn/Off, power status event
5. **DSP control wiring** — connect FiltersDspControl delegates to FlexBase properties
6. **Button wiring** — Transmit, Tune, ATU toggle (fill Phase 8.4+ stubs)

### Phase 11.7: Fix KeyCommands.vb

**File: `KeyCommands.vb`**

Line 1161: `CType(RigControl.RigFields.Memories, FlexMemories).ShowDialog()` → open WPF `MemoriesDialog` with delegates wired from RigControl.

### Phase 11.8: Gut Form1

**File: `Form1.vb` / `Form1.designer.vb`**

Replace Form1 with an empty shell `BridgeForm.vb`:
```vb
Public Class BridgeForm
    Inherits Form
    ' Empty - My.Application requires a MainForm for the message loop.
    ' All actual UI is in WPF MainWindow.
End Class
```

Update `My Project/Application.Designer.vb` to use `BridgeForm`.
Delete `Form1.vb`, `Form1.designer.vb`, `Form1.resx`.

---

## Track C — Deletion + Cleanup (after Track A, parallel with B)

**Branch:** `sprint11/track-c`
**Directory:** worktree `../jjflex-11c`

### Phase 11.9: Delete dead WinForms files

Delete from Radios/:
- `Flex6300Filters.cs` + `.Designer.cs` + `.resx` (~2,800 lines)
- `FlexMemories.cs` + `.Designer.cs` + `.resx` (~550 lines)
- `FlexATUMemories.cs` + `.Designer.cs` + `.resx`
- `FlexEq.cs` + `.Designer.cs` + `.resx`
- `FlexTNF.cs` + `.Designer.cs` + `.resx` (only if not needed by PanAdapterManager)
- `TXControls.cs` + `.Designer.cs` + `.resx`
- `FlexInfo.cs` + `.Designer.cs` + `.resx`

Delete from root:
- `LogPanel.vb`
- `RadioPane.vb`

Delete entire project:
- `RadioBoxes/` directory
- Remove from `JJFlexRadio.sln`
- Remove project reference from `Radios.csproj`

### Phase 11.10: Cleanup

- Remove `Imports RadioBoxes` / `using RadioBoxes` everywhere
- Remove `Imports System.Windows.Forms` where no longer needed
- Update `JJFlexRadio.vbproj` Compile items (remove deleted files)
- Update `install.nsi` template + `deleteList.txt`
- Clean build x64 + x86 Release
- Verify installers generated
- Update `docs/CHANGELOG.md`
- Update `Agent.md`
- Archive sprint plan

---

## Critical Files

| File | Role |
|------|------|
| `Radios/FlexBase.cs` | Start(), RigFields_t, memoryHandling — all adapter work converges here |
| `Radios/Flex6300Filters.cs` | Source of pan adapter code to extract (~2,800 lines) |
| `Radios/FlexWaterfall.cs` | Hard-coupled to Flex6300Filters, must decouple |
| `Radios/FlexMemories.cs` | Source of memory logic to extract (~550 lines) |
| `JJFlexWpf/MainWindow.xaml.cs` | Destination for radio wiring (has Phase 8.4+ stubs) |
| `Form1.vb` | Source of radio wiring to move (3,757 lines) |
| `KeyCommands.vb` | DisplayMemory() casts to FlexMemories |
| `globals.vb` | Shared state (RigControl, WpfMainWindow) |

## Risk Areas

**High risk:** Pan adapter extraction (PanAdapterManager). ~600 lines of thread-safe frequency tracking, waterfall data processing, braille rendering. This is the user's screen reader accessibility feature — must work.

**Medium risk:** Radio wiring move (Form1 → MainWindow). ~200 lines of critical startup code. The Phase 8.4+ stubs in MainWindow mark where it goes.

**Low risk:** WpfMemoryManager, RigFields_t refactor, dead code deletion.

## Backlog: Braille Waterfall Enhancements (future sprint, depends on PanAdapterManager)

Once PanAdapterManager is extracted in Sprint 11, these features become straightforward:

1. **Auto-refreshing braille waterfall** — 2-second timer pushes waterfall line to braille display via `Tolk.Braille()` (braille-only, no speech). Silent update so screen reader doesn't announce every refresh.
2. **Waterfall hotkey (Modern mode)** — Key combo brings waterfall into braille focus. Classic mode already has it as a tabbable field.
3. **Peak detection + prev/next station** — PanData already has per-cell signal levels. Find top N peaks, sort by strength, prev/next hotkeys tune radio to that frequency.
4. **Previous/next panadapter** — Cycle between slice pan adapters via hotkey.
5. **Sonified audio sweep** — Pipe pan data through tone generator, sweep left to right. (Further backlog.)

**Key UX decisions:**
- Use `AutomationProperties.LiveSetting="Off"` on a WPF TextBox — braille display shows the line but screen reader doesn't announce refreshes. Jim's original approach used an editable field.
- Cursor router buttons on braille display map position X → frequency bin X → tune radio to that frequency. This is the braille equivalent of clicking on the waterfall. Standard braille display interaction, no JAWS/NVDA addons needed.
- Tolk.Braille() as fallback/alternative if LiveSetting doesn't behave across all screen readers.
- **Zoom via cursor router + hotkey**: Click cursor router at position X to mark a frequency, then press a hotkey to create a new narrower panadapter centered on that frequency. Braille zoom — go from band overview to individual signals. Multiple zoom levels (zoom stack with zoom-out to go back up).
- **Band overview hotkey**: Opens waterfall showing entire band (e.g., all of 20m for current mode). Then zoom in from there — band → segment → cluster → individual signal.
- **Waterfall scope**: Dedicated key binding scope when waterfall is focused. Keys for: zoom in, zoom out, prev/next peak, tune to cursor position, band overview.
- Pattern generalizes to any SDR with waterfall data — not Flex-specific.

## Verification

1. Clean build x64 + x86 Release (0 errors)
2. `grep -r "Flex6300Filters" --include="*.cs" --include="*.vb"` returns 0 hits
3. `grep -r "RadioBoxes" --include="*.cs" --include="*.vb"` returns 0 hits
4. `grep -r "FlexMemories" --include="*.cs" --include="*.vb"` returns 0 hits (except comments)
5. Installer generates both x64 and x86
6. RadioBoxes.dll absent from output
7. App launches and connects to radio (functional test by user)
