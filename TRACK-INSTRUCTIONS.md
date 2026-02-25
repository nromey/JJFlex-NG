# Sprint 16 — Track A: Connection Tester → RigSelector

**Branch:** `sprint16/track-a`
**Worktree:** `C:\dev\JJFlex-NG` (main repo)
**Build:** `dotnet clean JJFlexRadio.vbproj -c Release -p:Platform=x64 && dotnet restore JJFlexRadio.vbproj -p:Platform=x64 && dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x64 --verbosity minimal`
**Commit prefix:** `Sprint 16 Track A:`

---

## Goal

Move the Connection Tester from a standalone dialog into the RigSelector dialog. The standalone tester creates its own FlexBase and competes for slices when the user is still connected. By embedding it in RigSelector, the user is guaranteed disconnected (they opened the selector to pick a radio), so the tester gets exclusive radio access.

---

## Phase A1: Add "Test" button to RigSelectorDialog.xaml

In `JJFlexWpf/Dialogs/RigSelectorDialog.xaml`, add a "Test" button to the right-side StackPanel between SmartLink and Cancel:

```xml
<Button x:Name="TestButton" Content="Test" MinWidth="100" Height="28" Margin="0,0,0,8"
        IsEnabled="False"
        AutomationProperties.Name="Test connection to selected radio"
        Click="TestButton_Click"/>
```

Place it after the SmartLink button (line 56) and before Cancel (line 57).

Also enlarge the dialog to accommodate the test UI — change `Height="320"` to `Height="420"`.

Add a test config/status area. Below the GlobalAutoConnectCheckbox (row 2), add a new row (row 3) with a panel for test configuration and status:

```xml
<!-- Test configuration and status (Row 3) -->
<StackPanel x:Name="TestPanel" Grid.Row="3" Grid.Column="0" Grid.ColumnSpan="2"
            Margin="0,8,0,0" Visibility="Collapsed">
    <StackPanel Orientation="Horizontal" Margin="0,0,0,4">
        <TextBlock Text="Tests:" VerticalAlignment="Center" Margin="0,0,8,0"/>
        <TextBox x:Name="TestCountBox" Width="60" Text="25" VerticalAlignment="Center"
                 AutomationProperties.Name="Number of tests, minimum 25"/>
        <TextBlock Text="Delay (sec):" VerticalAlignment="Center" Margin="16,0,8,0"/>
        <TextBox x:Name="DelayBox" Width="60" Text="5" VerticalAlignment="Center"
                 AutomationProperties.Name="Delay between tests in seconds"/>
    </StackPanel>
    <TextBlock x:Name="TestStatusText" Text="" TextWrapping="Wrap" Margin="0,4,0,0"
               AutomationProperties.Name="Test status"/>
</StackPanel>
```

Add a 4th RowDefinition (Height="Auto") to the Grid for this panel.

---

## Phase A2: Add test callbacks to RigSelectorCallbacks

In `RigSelectorDialog.xaml.cs`, add to `RigSelectorCallbacks` class (after line 87):

```csharp
/// <summary>OpenParms for creating test FlexBase instances.</summary>
public FlexBase.OpenParms? OpenParms { get; init; }

/// <summary>SmartLink account selector for test connections.</summary>
public Func<SmartLinkAccountManager, (bool newLogin, SmartLinkAccount selected, bool ok)?>? AccountSelector { get; init; }
```

These are the same params currently in `ConnectionTesterCallbacks`. They let the tester create its own FlexBase instances for each test cycle.

---

## Phase A3: Wire TestButton_Click in RigSelectorDialog.xaml.cs

Add these to `RigSelectorDialog.xaml.cs`:

1. Private fields:
```csharp
private ConnectionTester? _tester;
private bool _testRunning;
```

2. Enable/disable TestButton when ListBox selection changes. Add to `RadiosBox_GotFocus` area or add a new `RadiosBox_SelectionChanged` handler:
```csharp
private void RadiosBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
{
    TestButton.IsEnabled = RadiosBox.SelectedItem != null && !_testRunning;
}
```
Wire this in XAML: `SelectionChanged="RadiosBox_SelectionChanged"` on RadiosBox.

3. TestButton_Click handler:
```csharp
private void TestButton_Click(object sender, RoutedEventArgs e)
{
    var radio = GetSelectedRadio();
    if (radio == null)
    {
        MessageBox.Show(MustSelect, "Select Radio", MessageBoxButton.OK, MessageBoxImage.Warning);
        RadiosBox.Focus();
        return;
    }

    if (_callbacks.OpenParms == null)
    {
        _callbacks.ScreenReaderSpeak?.Invoke("Connection testing not available", true);
        return;
    }

    // Validate test config
    if (!int.TryParse(TestCountBox.Text, out int testCount) || testCount < 25)
    {
        ScreenReaderOutput.Speak("Test count must be at least 25");
        TestCountBox.Focus();
        return;
    }
    if (!int.TryParse(DelayBox.Text, out int delay) || delay < 1)
    {
        ScreenReaderOutput.Speak("Delay must be at least 1 second");
        DelayBox.Focus();
        return;
    }

    // Show test config panel
    TestPanel.Visibility = Visibility.Visible;

    // Disable all buttons except Cancel
    _testRunning = true;
    ConnectButton.IsEnabled = false;
    TestButton.IsEnabled = false;
    RadiosBox.IsEnabled = false;
    TestCountBox.IsEnabled = false;
    DelayBox.IsEnabled = false;
    GlobalAutoConnectCheckbox.IsEnabled = false;

    _tester = new ConnectionTester
    {
        TestCount = testCount,
        DelayBetweenTestsMs = delay * 1000,
        RadioSerial = radio.Serial,
        RadioName = radio.Name,
        LowBandwidth = radio.LowBW,
        IsRemote = radio.IsRemote,
        OpenParms = _callbacks.OpenParms,
        AccountSelector = _callbacks.AccountSelector
    };

    _tester.PhaseChanged += (testNum, phase) =>
        Dispatcher.BeginInvoke(() =>
        {
            TestStatusText.Text = $"Test {testNum} of {testCount}: {phase}";
        });

    _tester.TestCompleted += (testNum, success, reason, durationMs) =>
        Dispatcher.BeginInvoke(() =>
        {
            string result = success ? "PASS" : $"FAIL ({reason})";
            TestStatusText.Text = $"Test {testNum}: {result} ({durationMs / 1000.0:F1}s)";
        });

    _tester.AllTestsCompleted += (summary) =>
        Dispatcher.BeginInvoke(() =>
        {
            _testRunning = false;
            TestStatusText.Text = $"Complete: {summary.Passed}/{summary.TestCount} passed.";
            TestButton.Content = "Done";

            // Re-enable UI
            ConnectButton.IsEnabled = true;
            RadiosBox.IsEnabled = true;
            GlobalAutoConnectCheckbox.IsEnabled = true;

            _callbacks.ScreenReaderSpeak?.Invoke(
                $"All tests complete. {summary.Passed} of {summary.TestCount} passed. " +
                $"{summary.Failed} failed. Report saved.", true);
        });

    // Run on background STA thread
    var testThread = new System.Threading.Thread(() => _tester.Run())
    {
        IsBackground = true,
        Name = "ConnectionTester"
    };
    testThread.SetApartmentState(System.Threading.ApartmentState.STA);
    testThread.Start();
}
```

4. Cancel the test on dialog closing:
```csharp
// In RigSelectorDialog_Closing, add:
_tester?.Cancel();
```

---

## Phase A4: Wire OpenParms and AccountSelector in globals.vb

In `globals.vb`, method `wpfSelectorProc()` (around line 1485), add to the RigSelectorCallbacks construction:

```vb
.OpenParms = OpenParms,
.AccountSelector = Function(mgr)
                       Dim accounts = mgr.Accounts
                       If accounts.Count = 0 Then
                           Return (True, Nothing, True)
                       End If
                       Dim best = accounts.OrderByDescending(Function(a) a.LastUsed).First()
                       Return (False, best, True)
                   End Function
```

This reuses the same OpenParms and account selection logic that `ShowConnectionTester()` uses.

---

## Phase A5: Improve tester failure reason reporting

In `Radios/ConnectionTester.cs`, line 158, the hardcoded `"Start failed (station name timeout)"` should capture the actual failure. In `FlexBase.cs`, the `Start()` method already traces the failure reason but doesn't expose it.

Add to `FlexBase.cs`:
```csharp
/// <summary>Reason the last Start() call failed. Set before returning false.</summary>
public string? LastStartFailureReason { get; private set; }
```

In `Start()`, set `LastStartFailureReason` before each `return false`:
- "No slices available" (when initialFreeSlices <= 0)
- "No RX antenna detected" (when antenna list empty)
- "Station name timeout" (when station name wait expires)
- "Client removed during connection" (when GUIClient removed too long)

Then in `ConnectionTester.RunSingleTest()`, change line 158:
```csharp
result.Reason = started ? "OK" : (rig.LastStartFailureReason ?? "Start failed (unknown)");
```

---

## Phase A6: Remove standalone Connection Tester

1. **Delete files:**
   - `JJFlexWpf/Dialogs/ConnectionTesterDialog.xaml`
   - `JJFlexWpf/Dialogs/ConnectionTesterDialog.xaml.cs`

2. **NativeMenuBar.cs** — Remove "Connection Tester" menu items:
   - Line ~736 (Classic): Remove `AddWired(operations, "Connection Tester", ...)`
   - Line ~932 (Modern): Remove `AddWired(tools, "Connection Tester", ...)`
   - Keep "View Test Results" items in both menus

3. **MainWindow.xaml.cs** — Remove the callback property:
   - Remove `public Action? ShowConnectionTesterCallback { get; set; }` (line ~736)

4. **ApplicationEvents.vb** — Remove the callback wiring:
   - Remove line 65: `WpfMainWindow.ShowConnectionTesterCallback = AddressOf ShowConnectionTester`

5. **globals.vb** — Remove the `ShowConnectionTester()` method:
   - Delete lines 1810-1879

6. **ConnectionTesterCallbacks class** is defined inside `ConnectionTesterDialog.xaml.cs` — it gets deleted with the file.

---

## Phase A7: Build & Verify

```batch
dotnet clean JJFlexRadio.vbproj -c Release -p:Platform=x64 && dotnet restore JJFlexRadio.vbproj -p:Platform=x64 && dotnet build JJFlexRadio.vbproj -c Release -p:Platform=x64 --verbosity minimal
```

Verify:
- No compile errors
- `ConnectionTesterDialog` class no longer exists
- `RigSelectorDialog` compiles with Test button and tester logic
- `ConnectionTester.cs` still exists (it's the engine, not the UI)

Commit: `Sprint 16 Track A: Move Connection Tester into RigSelector`

---

## Files Modified

| File | Action |
|------|--------|
| `JJFlexWpf/Dialogs/RigSelectorDialog.xaml` | Add Test button, test config panel, enlarge |
| `JJFlexWpf/Dialogs/RigSelectorDialog.xaml.cs` | Add callbacks, test handler, cancel logic |
| `Radios/ConnectionTester.cs` | Use `LastStartFailureReason` instead of hardcoded string |
| `Radios/FlexBase.cs` | Add `LastStartFailureReason` property, set in `Start()` |
| `globals.vb` | Add OpenParms/AccountSelector to RigSelectorCallbacks, remove `ShowConnectionTester()` |
| `ApplicationEvents.vb` | Remove `ShowConnectionTesterCallback` wiring |
| `JJFlexWpf/MainWindow.xaml.cs` | Remove `ShowConnectionTesterCallback` property |
| `JJFlexWpf/NativeMenuBar.cs` | Remove "Connection Tester" menu items |
| `JJFlexWpf/Dialogs/ConnectionTesterDialog.xaml` | DELETE |
| `JJFlexWpf/Dialogs/ConnectionTesterDialog.xaml.cs` | DELETE |

## Architecture Notes

- **RigSelectorCallbacks** already has `StartLocalDiscovery`, `StartRemoteDiscovery`, `RegisterRadioFound` — the tester reuses these for radio discovery
- **ConnectionTester** creates its OWN FlexBase instances per test (it needs to connect/disconnect/reconnect) — it just needs OpenParms and AccountSelector
- The tester runs on a background STA thread inside the RigSelector dialog, with UI updates via Dispatcher
- **No FlexBase dependency from the dialog itself** — the tester engine handles all radio interaction
