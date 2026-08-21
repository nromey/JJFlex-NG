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
    // EVERY MEMBER CARRIES AN EXPLICIT VALUE ON PURPOSE. DO NOT REMOVE THEM,
    // and do not renumber to "tidy up".
    //
    // These numbers are the on-disk identity of a key binding. KeyDefType.i is
    // (int)CommandValues, and %AppData%\JJFlexRadio\KeyDefs.xml stores that
    // integer. With implicit numbering, inserting a member anywhere but the end
    // shifts every later member by one — and a KeyDefs.xml written by the older
    // build then loads against the newer enum with every binding after the
    // insertion point attributed to the WRONG COMMAND.
    //
    // That is not hypothetical. Commit 40307951 (2026-08-18) inserted
    // SpeakContextHelp mid-list, and diffing two KeyDefs.xml files as a
    // command-id-to-key MAPPING showed 22 commands each inheriting the key the
    // previous command had held.
    //
    // Nothing was damaged, only because the operator had zero customised
    // bindings — every key equalled its default, so rewriting to new defaults
    // was correct. Anyone WITH customisations would have had them silently
    // attach to different commands. Nothing errors, the file loads, the keys
    // just do other things now. For an operator whose whole interaction model
    // is the keyboard, that is severe and nearly undiagnosable from the inside.
    //
    // A NEW COMMAND TAKES THE NEXT UNUSED NUMBER, regardless of where you place
    // it in this list. Source order and numeric value are now independent, which
    // is the entire point. KeyCommandIdStabilityTests guards this.
    //
    // Same defect family as #34 (saved audio devices keyed on PortAudio index):
    // an unstable ordinal used as a durable identifier.
    NotACommand = -1,
    ShowHelp = 0,
    ShowFreq = 1,
    SetFreq = 2,
    ShowMemory = 3,
    CycleContinuous = 4,
    LogForm = 5,
    LogDateTime = 6,
    LogFinalize = 7,
    LogFileName = 8,
    LogCall = 9,
    LogHisRST = 10,
    LogMyRST = 11,
    LogQTH = 12,
    LogState = 13,
    LogHandle = 14,
    LogRig = 15,
    LogAnt = 16,
    LogComments = 17,
    NewLogEntry = 18,
    StartScan = 19,
    MemoryScan = 20,
    SavedScan = 21,
    StopScan = 22,
    LogMode = 23,
    SearchLog = 24,
    ShowMenus = 25,
    ShowReceived = 26,
    ShowSend = 27,
    StopCW = 28,
    SendLoggedCall = 29,
    SendLoggedName = 30,
    DoPanning = 31,
    AudioGainUp = 32,
    AudioGainDown = 33,
    HeadphonesUp = 34,
    HeadphonesDown = 35,
    LineoutUp = 36,
    LineoutDown = 37,
    ResumeTheScan = 38,
    CWZeroBeat = 39,
    ClearRIT = 40,
    ReverseBeacon = 41,
    ArCluster = 42,
    LogGrid = 43,
    Toggle1 = 44,
    LogStats = 45,
    RemoteAudio = 46,  // PCAudio
    AudioSetup = 47,
    StationLookup = 48,
    GatherDebug = 49,
    ATUMemories = 50,
    Reboot = 51,
    TXControls = 52,
    ShowSendDirect = 53,
    SmeterDBM = 54,
    // Logging-only actions (added for scope-aware hotkeys)
    LogPaneSwitchF6 = 55,
    LogCharacteristicsDialog = 56,
    LogOpenFullForm = 57,
    ContextHelp = 58,
    SpeakStatus = 59,
    ShowStatusDialog = 60,
    SpeakTxStatus = 61,
    BandJump160 = 62,
    BandJump80 = 63,
    BandJump60 = 64,
    BandJump40 = 65,
    BandJump30 = 66,
    BandJump20 = 67,
    BandJump17 = 68,
    BandJump15 = 69,
    BandJump12 = 70,
    BandJump10 = 71,
    BandJump6 = 72,
    BandJump2 = 73,
    BandUp = 74,
    BandDown = 75,
    ModeNext = 76,
    ModePrev = 77,
    ModeUSB = 78,
    ModeLSB = 79,
    ModeCW = 80,
    ModeAM = 81,
    ModeFM = 82,
    ModeDIGU = 83,
    ModeDIGL = 84,
    ReadSMeter = 85,
    ToggleMeterTones = 86,
    CycleMeterPreset = 87,
    SpeakMeters = 88,
    TXFilterLowDown = 89,
    TXFilterLowUp = 90,
    TXFilterHighDown = 91,
    TXFilterHighUp = 92,
    SpeakTXFilter = 93,
    OpenAudioWorkshop = 94,
    ShowContextHelp = 95,
    SpeakContextHelp = 96,
    TuneToggle = 97,
    ATUTune = 98,
    ToggleMeters = 99,
    SixtyMeterChannelUp = 100,
    SixtyMeterChannelDown = 101,
    ToggleDspExpander = 102,
    ToggleAudioExpander = 103,
    ToggleReceiverExpander = 104,
    ToggleTransmissionExpander = 105,
    ToggleAntennaExpander = 106,
    SpeakFrequency = 107,
    RepeatLastMessage = 108,
    CycleVerbosity = 109,
    ToggleMeterTonesGlobal = 110,
    MuteSlice = 111,
    MuteAllSlices = 112,
    ReleaseAllExtraSlices = 113,
    ToggleBrailleStatus = 114,
    StartAudioCheck = 115,
    // QB Track H (2026-08-07) — former hard-wired MainWindow meta-commands,
    // promoted into the registry so the Keys surface / manifest can see them
    // and so their chords stop shadowing registry bindings invisibly.
    ToggleTuningMode = 116,
    ToggleLoggingMode = 117,
    ToggleFreqReadout = 118,
    SpeakRXFilter = 119,
    // Sprint 33 Track F (#153). Appended rather than slotted next to
    // RepeatLastMessage on purpose: these are ordinals and a stored KeyDefs.xml
    // references them by value, so inserting in the middle would silently
    // rebind every operator's customised key past the insertion point.
    RepeatLastCw = 120,
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

    /// <summary>
    /// When true, the dispatcher's no-radio guard skips the "no radio connected"
    /// announcement and routes the keystroke to the handler. The handler is then
    /// responsible for any no-radio behavior (e.g., SetFreq still opens its
    /// dialog so the cqtest easter egg and calibration-ref entry keep working).
    /// Default false: most Radio-scope commands need a real radio.
    /// </summary>
    public bool RunsWithoutRadio;

    /// <summary>
    /// Verb-led short label naming this command's action (e.g. "change band",
    /// "toggle tune"). Used by SpeakNoRadioConnected to produce action-aware
    /// announcements ("Unable to change band, JJ Flexible Home no radio
    /// connected"). Sprint 28 design followup; Sprint 29 picks up bulk
    /// population — see memory/project_short_action_labels_vocabulary.md.
    /// Null falls back to today's plain-form output.
    /// </summary>
    public string? ShortActionLabel;

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
        RunsWithoutRadio = source.RunsWithoutRadio;
        ShortActionLabel = source.ShortActionLabel;
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
