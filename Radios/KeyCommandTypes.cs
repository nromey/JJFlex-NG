using System;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace Radios;

/// <summary>
/// Shared types for the key command system. Used by both the C# KeyCommands
/// implementation (JJFlexWpf) and VB.NET consumer code.
/// Migrated from KeyCommands.vb — Sprint 24 Phase 1.
/// </summary>

// ────────────────────────────────────────────────────────────────
//  CommandValues — one entry per bindable action. New commands go at the end.
// ────────────────────────────────────────────────────────────────
public enum CommandValues
{
    NotACommand = -1,
    ShowHelp = 0,
    ShowFreq,
    SetFreq,
    ShowMemory,
    CycleContinuous,
    LogForm,
    LogDateTime,
    LogFinalize,
    LogFileName,
    LogCall,
    LogHisRST,
    LogMyRST,
    LogQTH,
    LogState,
    LogHandle,
    LogRig,
    LogAnt,
    LogComments,
    NewLogEntry,
    StartScan,
    MemoryScan,
    SavedScan,
    StopScan,
    LogMode,
    SearchLog,
    ShowMenus,
    ShowReceived,
    ShowSend,
    StopCW,
    SendLoggedCall,
    SendLoggedName,
    DoPanning,
    AudioGainUp,
    AudioGainDown,
    HeadphonesUp,
    HeadphonesDown,
    LineoutUp,
    LineoutDown,
    ResumeTheScan,
    CWZeroBeat,
    ClearRIT,
    ReverseBeacon,
    ArCluster,
    LogGrid,
    Toggle1,
    LogStats,
    RemoteAudio,    // PCAudio
    AudioSetup,
    StationLookup,
    GatherDebug,
    ATUMemories,
    Reboot,
    TXControls,
    ShowSendDirect,
    SmeterDBM,
    // Logging-only actions (added for scope-aware hotkeys)
    LogPaneSwitchF6,
    LogCharacteristicsDialog,
    LogOpenFullForm,
    ContextHelp,
    SpeakStatus,
    ShowStatusDialog,
    SpeakTxStatus,
    BandJump160,
    BandJump80,
    BandJump60,
    BandJump40,
    BandJump30,
    BandJump20,
    BandJump17,
    BandJump15,
    BandJump12,
    BandJump10,
    BandJump6,
    BandJump2,
    BandUp,
    BandDown,
    ModeNext,
    ModePrev,
    ModeUSB,
    ModeLSB,
    ModeCW,
    ReadSMeter,
    ToggleMeterTones,
    CycleMeterPreset,
    SpeakMeters,
    TXFilterLowDown,
    TXFilterLowUp,
    TXFilterHighDown,
    TXFilterHighUp,
    SpeakTXFilter,
    OpenAudioWorkshop,
    ShowContextHelp,
    TuneToggle,
    ATUTune,
    ToggleMeters,
    SixtyMeterChannelUp,
    SixtyMeterChannelDown,
    ToggleDspExpander,
    ToggleAudioExpander,
    ToggleReceiverExpander,
    ToggleTransmissionExpander,
    ToggleAntennaExpander,
    SpeakFrequency,
    RepeatLastMessage,
}

/// <summary>
/// Sentinel value for CW text message commands (IDs above this are CW messages).
/// </summary>
public static class KeyCommandConstants
{
    public const int FirstMessageCommandValue = 1_000_000;
}

/// <summary>
/// Shared CW message item type. Used by KeyCommands (C#) and wired from
/// CWMessages.MessageItem (VB) via the KeyCommandContext delegate.
/// </summary>
public class CWMessageItem
{
    public Keys Key;
    public string Message = string.Empty;
    public string Label = string.Empty;

    public CWMessageItem() { }
    public CWMessageItem(Keys k, string message, string label)
    {
        Key = k;
        Message = message;
        Label = label;
    }
}

// ────────────────────────────────────────────────────────────────
//  KeyScope — determines when a hotkey binding is active
// ────────────────────────────────────────────────────────────────
/// <summary>
/// 5-scope system (Sprint 8 Phase 8.6):
///   Global  = all modes
///   Radio   = Classic + Modern (both)
///   Classic = Classic mode only
///   Modern  = Modern mode only
///   Logging = Logging Mode only
/// </summary>
public enum KeyScope
{
    Global = 0,     // Active in all modes
    Radio = 1,      // Classic + Modern (both — shared)
    Classic = 2,    // Classic mode only
    Modern = 3,     // Modern mode only
    Logging = 4,    // Logging Mode only
}

// ────────────────────────────────────────────────────────────────
//  KeyTypes — flags for command vs CW text vs log
// ────────────────────────────────────────────────────────────────
[Flags]
public enum KeyTypes
{
    Command = 1,
    CWText = 2,
    Log = 4,
    AllKeys = Command | CWText | Log,
}

// ────────────────────────────────────────────────────────────────
//  FunctionGroups — categories for key table entries
// ────────────────────────────────────────────────────────────────
public enum FunctionGroups
{
    Audio,
    CwMessage,
    Dialog,
    General,
    Help,
    Logging,
    Routing,
    RoutingScan,
    Scan,
    Tuning,
}

// ────────────────────────────────────────────────────────────────
//  KeyDefType — key binding definition with XML serialization
// ────────────────────────────────────────────────────────────────
/// <summary>
/// Command key definition. Serialized to/from KeyDefs.xml.
/// BUG-014 fix: Keys stored as integer to avoid XmlSerializer corruption.
/// XmlSerializer treats Keys as a [Flags] enum and decomposes combined
/// values into space-separated flag names that can't be parsed back.
/// </summary>
public class KeyDefType
{
    [XmlIgnore]
    public Keys Key;

    /// <summary>
    /// XML proxy — stores the Keys value as an integer for reliable round-trip.
    /// Old files stored Keys as enum names (e.g. "F1", "LButton ShiftKey ...").
    /// On read we try integer first; if that fails we fall back to Enum.Parse
    /// so legacy files still load.
    /// </summary>
    [XmlElement("key")]
    public string KeyAsString
    {
        get => ((int)Key).ToString();
        set
        {
            if (int.TryParse(value, out int n))
            {
                Key = (Keys)n;
            }
            else
            {
                // Legacy format: enum name(s). Simple names like "F1" work;
                // corrupted multi-flag names will fall back to Keys.None.
                try
                {
                    Key = (Keys)Enum.Parse(typeof(Keys), value);
                }
                catch
                {
                    Key = Keys.None;
                }
            }
        }
    }

    public int i;
    public KeyScope Scope = KeyScope.Global;

    /// <summary>
    /// Command ID. Stored as integer in XML because Vista's XmlSerializer
    /// doesn't handle enums reliably.
    /// </summary>
    [XmlIgnore]
    public CommandValues Id
    {
        get => (CommandValues)i;
        set => i = (int)value;
    }

    /// <summary>
    /// Stores the default key at the time this config was saved.
    /// On load, if the current default differs from this saved default,
    /// we know the default changed. If the user's key matches this old default,
    /// they never customized — apply new default. If their key differs,
    /// they customized — keep their binding.
    /// </summary>
    [XmlIgnore]
    public Keys SavedDefaultKey = Keys.None;

    /// <summary>XML proxy for SavedDefaultKey — same integer pattern as Key.</summary>
    [XmlElement("defaultKey")]
    public string SavedDefaultKeyAsString
    {
        get => ((int)SavedDefaultKey).ToString();
        set
        {
            if (int.TryParse(value, out int n))
                SavedDefaultKey = (Keys)n;
            else
                SavedDefaultKey = Keys.None;
        }
    }

    public KeyDefType() { }

    public KeyDefType(Keys k, CommandValues id)
    {
        Key = k;
        Id = id;
    }

    public KeyDefType(Keys k, CommandValues id, KeyScope scope)
    {
        Key = k;
        Id = id;
        Scope = scope;
    }
}

// ────────────────────────────────────────────────────────────────
//  KeyTableEntry — runtime key table entry (renamed from VB keyTbl)
// ────────────────────────────────────────────────────────────────
/// <summary>
/// Runtime entry linking a key binding to its handler, help text, and metadata.
/// In C# the handler is an Action instead of VB's AddressOf delegate.
/// </summary>
public class KeyTableEntry
{
    public KeyDefType KeyDef;
    public KeyTypes KeyType;
    public Action? Handler;
    public string HelpText;
    public string? ADIFTag;
    public bool UseWhenLogging;
    public FunctionGroups Group;
    public KeyScope Scope = KeyScope.Global;
    public string? Description;
    public string[]? Keywords;

    // Menu text can be static or dynamic (via delegate).
    private Func<string>? _menuTextFunc;
    private string? _menuTextStatic;

    public string? MenuText
    {
        get
        {
            if (_menuTextFunc != null)
                _menuTextStatic = _menuTextFunc();
            return _menuTextStatic;
        }
    }

    // Copy constructor
    public KeyTableEntry(KeyTableEntry source)
    {
        KeyDef = source.KeyDef;
        KeyType = source.KeyType;
        Handler = source.Handler;
        HelpText = source.HelpText;
        _menuTextStatic = source._menuTextStatic;
        _menuTextFunc = source._menuTextFunc;
        ADIFTag = source.ADIFTag;
        UseWhenLogging = source.UseWhenLogging;
        Group = source.Group;
        Scope = source.Scope;
        Description = source.Description;
        Keywords = source.Keywords;
    }

    // For a command
    public KeyTableEntry(CommandValues id, Action handler,
        string helpText, string? menuText, FunctionGroups group,
        KeyScope scope = KeyScope.Global)
    {
        KeyDef = new KeyDefType(Keys.None, id);
        KeyType = KeyTypes.Command;
        Handler = handler;
        HelpText = helpText;
        _menuTextStatic = menuText;
        ADIFTag = string.Empty;
        UseWhenLogging = false;
        Group = group;
        Scope = scope;
    }

    // For a log field
    public KeyTableEntry(CommandValues id, Action handler,
        string helpText, string? menuText, string adifTag, KeyTypes keyType,
        FunctionGroups group, KeyScope scope = KeyScope.Global)
    {
        KeyDef = new KeyDefType(Keys.None, id);
        Handler = handler;
        HelpText = helpText;
        _menuTextStatic = menuText;
        ADIFTag = adifTag;
        KeyType = keyType;
        UseWhenLogging = false;
        Group = group;
        Scope = scope;
    }

    // For a macro
    public KeyTableEntry(CommandValues id, KeyTypes keyType,
        Action handler, string helpText, FunctionGroups group,
        KeyScope scope = KeyScope.Global)
    {
        KeyDef = new KeyDefType(Keys.None, id);
        KeyType = keyType;
        Handler = handler;
        HelpText = helpText;
        _menuTextStatic = null;
        ADIFTag = string.Empty;
        UseWhenLogging = true;
        Group = group;
        Scope = scope;
    }

    // For a non-logging key allowed during logging
    public KeyTableEntry(CommandValues id, KeyTypes keyType,
        Action handler, string helpText, string? menuText,
        bool useWhenLogging, FunctionGroups group,
        KeyScope scope = KeyScope.Global)
    {
        KeyDef = new KeyDefType(Keys.None, id);
        KeyType = keyType;
        Handler = handler;
        HelpText = helpText;
        _menuTextStatic = menuText;
        ADIFTag = string.Empty;
        UseWhenLogging = useWhenLogging;
        Group = group;
        Scope = scope;
    }

    // For a command with a dynamic menu text delegate
    public KeyTableEntry(CommandValues id, KeyTypes keyType,
        Action handler, string helpText, Func<string> menuTextFunc,
        bool useWhenLogging, FunctionGroups group,
        KeyScope scope = KeyScope.Global)
    {
        KeyDef = new KeyDefType(Keys.None, id);
        KeyType = keyType;
        Handler = handler;
        HelpText = helpText;
        _menuTextFunc = menuTextFunc;
        ADIFTag = string.Empty;
        UseWhenLogging = useWhenLogging;
        Group = group;
        Scope = scope;
    }
}

// ────────────────────────────────────────────────────────────────
//  KeyConfigType_V1 — persistence format for KeyDefs.xml
// ────────────────────────────────────────────────────────────────
/// <summary>
/// Serialized key configuration (version 5+). Saved to/loaded from KeyDefs.xml.
/// PathName must be set by the application at startup (it depends on the
/// user's AppData path which is a VB global).
/// </summary>
public class KeyConfigType_V1
{
    public KeyDefType[]? Items;
    public int Version;

    [XmlIgnore]
    public int TraceLevel; // enum can cause problems

    /// <summary>
    /// Path to KeyDefs.xml. Must be set by the application at startup.
    /// </summary>
    [XmlIgnore]
    public static string PathName { get; set; } = string.Empty;

    public KeyConfigType_V1()
    {
        Version = 0;
    }

    public KeyConfigType_V1(int size)
    {
        Items = new KeyDefType[size + 1]; // VB ReDim is inclusive
        Version = 0;
    }
}

// ────────────────────────────────────────────────────────────────
//  KeyConfigData — deprecated legacy format (pre-V1)
// ────────────────────────────────────────────────────────────────
/// <summary>
/// Old keydefs format. Kept for migration from ancient config files.
/// </summary>
public class KeyConfigData
{
    public Keys[]? Items;
}
