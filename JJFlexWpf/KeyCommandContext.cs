using System;
using System.Windows.Forms;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// Context interface that provides access to VB globals for the C# KeyCommands class.
/// Each delegate wraps a VB global or method so KeyCommands has no direct VB dependency.
/// Wired up in ApplicationEvents.vb at startup.
/// Sprint 24 Phase 2.
/// </summary>
public class KeyCommandContext
{
    // ── Radio state ──
    public Func<FlexBase?> GetRigControl { get; init; } = () => null;
    public Func<bool> GetPower { get; init; } = () => false;

    // ── UI mode ──
    /// <summary>
    /// Returns the current UI mode as an integer matching VB UIMode enum:
    /// 0 = Classic, 1 = Modern, 2 = Logging.
    /// </summary>
    public Func<int> GetActiveUIMode { get; init; } = () => 0;

    // ── WPF MainWindow ──
    public Func<MainWindow?> GetMainWindow { get; init; } = () => null;

    // ── Speech and tracing ──
    public Action<string> Trace { get; init; } = _ => { };

    // ── Scan state ──
    public Func<bool> GetScanRunning { get; init; } = () => false;
    public Action StopScan { get; init; } = () => { };
    public Action BeginScan { get; init; } = () => { };
    public Action ResumeScan { get; init; } = () => { };
    public Action<string> UseSavedScan { get; init; } = _ => { };
    public Action MemoryScan { get; init; } = () => { };

    // ── Logging ──
    public Action<string?> BringUpLogForm { get; init; } = _ => { };
    public Action FinalizeLog { get; init; } = () => { };
    public Action SetLogDateTime { get; init; } = () => { };
    public Action GetLogFileName { get; init; } = () => { };
    public Action SearchLog { get; init; } = () => { };
    public Action LogStats { get; init; } = () => { };

    // ── CW text ──
    public Func<CWMessageItem[]> GetCWText { get; init; } = () => Array.Empty<CWMessageItem>();
    public Action<string> SendCW { get; init; } = _ => { };
    public Action<int, string, int, bool> WriteTextX { get; init; } = (_, _, _, _) => { };

    // ── Navigation / routing ──
    public Action DisplayFreq { get; init; } = () => { };
    public Action WriteFreq { get; init; } = () => { };
    public Action GotoReceive { get; init; } = () => { };
    public Action GotoSend { get; init; } = () => { };
    public Action GotoSendDirect { get; init; } = () => { };
    public Action StartPanning { get; init; } = () => { };
    public Action CycleContinuous { get; init; } = () => { };

    // ── Dialogs ──
    public Action DisplayMemory { get; init; } = () => { };
    public Action ShowMenus { get; init; } = () => { };
    public Action ShowReverseBeacon { get; init; } = () => { };
    public Action ShowDXCluster { get; init; } = () => { };
    public Action StationLookup { get; init; } = () => { };
    public Action GatherDebug { get; init; } = () => { };
    public Action ShowATUMemories { get; init; } = () => { };
    public Action RebootRadio { get; init; } = () => { };
    public Action ShowTXControls { get; init; } = () => { };
    public Action AudioSetup { get; init; } = () => { };
    public Action ShowLogCharacteristics { get; init; } = () => { };
    public Action LogOpenFullForm { get; init; } = () => { };

    // ── Audio ──
    public Action PCAudioToggle { get; init; } = () => { };
    public Func<string> AudioMenuString { get; init; } = () => "";
    public Func<string> SMeterMenuString { get; init; } = () => "";

    // ── Logging pane ──
    public Action LogPaneSwitch { get; init; } = () => { };

    // ── Config directory (for persistence) ──
    public Func<string?> GetConfigDirectory { get; init; } = () => null;

    // ── Key string formatter (from globals.vb KeyString) ──
    public Func<Keys, string> FormatKey { get; init; } = k => k.ToString();
}
