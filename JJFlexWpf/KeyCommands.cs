using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Serialization;
using JJTrace;
using Radios;

namespace JJFlexWpf;

// Quasi-modal "work mode" features (filter-edge grab, RIT/XIT scale-adjust,
// future siblings) should reach for JJFlexWpf.Modes.StickyAnnouncedMode rather
// than rolling their own enter/exit/timeout scaffolding. The helper bakes in
// the mode-enter / mode-exit earcons, the dispatcher-safe inactivity watchdog,
// and the IsActive / Exit lifecycle that screen-reader users expect to be
// uniform across the app. See its file header for usage rules.

/// <summary>
/// C# replacement for KeyCommands.vb.
/// Owns the key table, dictionaries, dispatch, leader key system, and config persistence.
/// Handler methods are wired via the KeyCommandContext delegate bag — no direct VB dependency.
/// Sprint 24 Phase 2 (skeleton), Phase 3 (handlers).
/// </summary>
public class KeyCommands
{
    // ── Context (provides access to VB globals via delegates) ──
    private readonly KeyCommandContext _context;

    // ── Leader key state (Ctrl+J → second key). No timeout — cancel with Escape only. ──
    private bool _leaderKeyActive;

    // ── Volume mode state (Ctrl+J, V — Audio Arc Track A, 2026-08-11). ──
    // A mode WITHIN the leader: pick a target letter, ride Up/Down, switch
    // targets freely, Escape exits. It persists across adjustments —
    // JAWS/NVDA layered-keystroke muscle memory, not a three-key one-shot.
    private bool _volumeModeActive;
    private VolumeTarget _volumeTarget = VolumeTarget.None;
    // True once a PC-volume adjustment happened this volume-mode session, so
    // exit persists the app-level setting exactly once.
    private bool _volumeModePcDirty;

    private enum VolumeTarget
    {
        None,
        Headphone,      // H — on-radio headphone jack
        PcOutput,       // P — PC output volume (dB)
        MicLevel,       // M — mic level (radio mic gain, PC audio included)
        Lineout,        // L — on-radio line out
        CompanderLevel, // C — compander level
        ProcessorMode,  // S — speech processor setting (Normal/DX/DX+)
    }

    // ── Command ID tracking — handlers can read this to know which command triggered them. ──
    public CommandValues CommandId { get; set; }

    // ── ADIF pseudotags (used by log field entries in the key table) ──
    public const string IADIF_Logform = "$LOGFORM";
    public const string IADIF_Logwrite = "$LOGWRITE";
    public const string IADIF_Logfile = "$LOGFILE";
    public const string IADIF_LogNewEntry = "$LOGNEWENTRY";
    public const string IADIF_Logsearch = "$LOGSEARCH";

    // ── Config version — increment when keybindings are reshuffled ──
    // v5 = Sprint 23: unified hotkey dispatch, expander keys, scope cleanup
    private const int KeyConfigVersion = 5;

    // ────────────────────────────────────────────────────────────────
    //  Key Table — one entry per bindable action
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Master key table. It's in logical order, not CommandValues order.
    /// </summary>
    public KeyTableEntry[] KeyTable = null!; // Initialized in BuildKeyTable()

    /// <summary>
    /// Build the key table. Called from constructors after _context is set.
    /// Separated from field initializer because handlers reference _context.
    /// </summary>
    private void BuildKeyTable()
    {
        KeyTable = new KeyTableEntry[]
        {
            // ── Help ──
            new(CommandValues.ShowHelp, ShowHelpHandler,
                "Show keys help", null, FunctionGroups.Help, KeyScope.Global)
                { Keywords = new[] { "help", "keys", "hotkeys", "shortcuts", "keyboard" }, ShortActionLabel = "show keys help" },
            new(CommandValues.ShowContextHelp, KeyTypes.Command, ShowContextHelpHandler,
                "Open help file", "Help file", false, FunctionGroups.Help, KeyScope.Global)
                { Keywords = new[] { "help", "file", "chm", "documentation", "manual", "f1" }, ShortActionLabel = "open help file" },

            // ── Routing / Scan ──
            new(CommandValues.ShowFreq, DisplayFreqHandler,
                "Go to Home", null, FunctionGroups.RoutingScan, KeyScope.Radio)
                { Keywords = new[] { "home", "frequency", "focus", "display", "tune", "tuning" }, ShortActionLabel = "go to home" },
            new(CommandValues.ResumeTheScan, ResumeScanHandler,
                "Resume the scan.", "resume scan", FunctionGroups.Scan, KeyScope.Radio)
                { Keywords = new[] { "scan", "resume", "continue", "scanning" }, ShortActionLabel = "resume scan" },
            new(CommandValues.ShowReceived, GotoReceiveHandler,
                "goto the received text window", null, FunctionGroups.Routing, KeyScope.Radio)
                { Keywords = new[] { "receive", "text", "window", "cw", "morse", "focus" }, ShortActionLabel = "go to received text" },
            new(CommandValues.ShowSend, GotoSendHandler,
                "go to the send text window", null, FunctionGroups.Routing, KeyScope.Radio)
                { Keywords = new[] { "send", "text", "window", "cw", "morse", "focus" }, ShortActionLabel = "go to send text" },
            new(CommandValues.ShowSendDirect, GotoSendDirectHandler,
                "go to the send text window and send direct from keyboard", null, FunctionGroups.Routing, KeyScope.Radio)
                { Keywords = new[] { "send", "direct", "keyboard", "cw", "morse", "type" }, ShortActionLabel = "send direct from keyboard" },

            // ── General ──
            new(CommandValues.SmeterDBM, KeyTypes.Command, SmeterDisplayHandler,
                "Display SMeter in DBM or S-units", _context.SMeterMenuString, false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "s meter", "signal", "strength", "dbm", "s-units", "meter" }, ShortActionLabel = "switch S meter units" },
            new(CommandValues.ReadSMeter, ReadSMeterHandler,
                "Read the S-meter value aloud", null, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "s meter", "signal", "strength", "read", "speak", "announce" }, ShortActionLabel = "read S meter" },

            // ── Audio / Meter ──
            new(CommandValues.ToggleMeterTones, ToggleMeterTonesHandler,
                "Toggle meter sonification tones", null, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "meter", "tone", "sonification", "audio", "pitch", "toggle" }, ShortActionLabel = "toggle meter tones" },
            new(CommandValues.CycleMeterPreset, CycleMeterPresetHandler,
                "Cycle meter tone preset (RX, TX, Full Monitor)", null, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "meter", "preset", "cycle", "rx", "tx", "monitor" }, ShortActionLabel = "cycle meter preset" },
            new(CommandValues.SpeakMeters, SpeakMetersHandler,
                "Speak current meter values", null, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "meter", "speak", "read", "alc", "swr", "power", "signal" }, ShortActionLabel = "speak meter values" },

            // ── CW ──
            new(CommandValues.StopCW, KeyTypes.Command, StopCWHandler,
                "Stop sending CW", "cw stop", true, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "cw", "morse", "stop", "abort", "sending" }, ShortActionLabel = "stop sending C W" },

            // ── Frequency / Memory ──
            // SetFreq opts in to RunsWithoutRadio: the freq-input dialog still
            // works without a connected radio (cqtest easter egg, calibration
            // reference entry, just-typing-a-frequency-to-remember). The handler
            // speaks an apply-time error if the user tries to actually tune.
            new(CommandValues.SetFreq, WriteFreqHandler,
                "Enter frequency", "frequency", FunctionGroups.General, KeyScope.Radio)
                {
                    Keywords = new[] { "frequency", "enter", "type", "tune", "tuning", "dial" },
                    RunsWithoutRadio = true,
                    ShortActionLabel = "enter frequency",
                },
            // ShowMemory opts in to RunsWithoutRadio: the dialog is a viewer.
            // Handler speaks an action-aware no-radio message when there's no
            // memory data to show, so the keystroke isn't silent.
            new(CommandValues.ShowMemory, DisplayMemoryHandler,
                "Bring up the memory dialogue", "memories", FunctionGroups.Dialog, KeyScope.Radio)
                {
                    Keywords = new[] { "memory", "memories", "store", "recall", "save", "channel" },
                    RunsWithoutRadio = true,
                    ShortActionLabel = "show memories",
                },
            new(CommandValues.CycleContinuous, CycleContinuousHandler,
                "Toggle continuous frequency display", null, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "continuous", "frequency", "display", "toggle" }, ShortActionLabel = "toggle continuous frequency" },

            // ── Logging ──
            new(CommandValues.LogDateTime, SetLogDateTimeHandler,
                "Set log date/time", "log date/time", "QSO_DATE", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "date", "time", "contact", "logging" }, ShortActionLabel = "log date and time" },
            new(CommandValues.LogFinalize, FinalizeLogHandler,
                "Write log entry", "log write", IADIF_Logwrite, KeyTypes.Command, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "write", "save", "entry", "contact", "finalize", "logging" }, ShortActionLabel = "save log entry" },
            new(CommandValues.LogFileName, GetLogFileNameHandler,
                "Enter log file name", "log file name", IADIF_Logfile, KeyTypes.Command, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "file", "name", "logging" }, ShortActionLabel = "set log file name" },
            new(CommandValues.LogMode, BringUpLogFormHandler,
                "Log the mode", "log mode", "MODE", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "mode", "contact", "logging" }, ShortActionLabel = "log mode" },
            new(CommandValues.LogCall, BringUpLogFormHandler,
                "Log callsign", "log call", "CALL", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "call", "callsign", "contact", "logging" }, ShortActionLabel = "log callsign" },
            new(CommandValues.LogHisRST, BringUpLogFormHandler,
                "Log his RST", "log his RST", "RST_SENT", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "rst", "signal", "report", "his", "contact", "logging" }, ShortActionLabel = "log his signal report" },
            new(CommandValues.LogMyRST, BringUpLogFormHandler,
                "Log my RST", "log my RST", "RST_RCVD", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "rst", "signal", "report", "my", "contact", "logging" }, ShortActionLabel = "log my signal report" },
            new(CommandValues.LogQTH, BringUpLogFormHandler,
                "Log QTH", "log QTH", "QTH", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "qth", "location", "contact", "logging" }, ShortActionLabel = "log location" },
            new(CommandValues.LogState, BringUpLogFormHandler,
                "Log state/province", "log state", "STATE", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "state", "province", "contact", "logging" }, ShortActionLabel = "log state" },
            new(CommandValues.LogGrid, BringUpLogFormHandler,
                "Log Grid square", "log Grid", "GRIDSQUARE", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "grid", "square", "locator", "contact", "logging" }, ShortActionLabel = "log grid square" },
            new(CommandValues.LogHandle, BringUpLogFormHandler,
                "Log name", "log name", "NAME", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "name", "handle", "operator", "contact", "logging" }, ShortActionLabel = "log name" },
            new(CommandValues.LogRig, BringUpLogFormHandler,
                "Log rig", "log rig", "RIG", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "rig", "radio", "contact", "logging" }, ShortActionLabel = "log rig" },
            new(CommandValues.LogAnt, BringUpLogFormHandler,
                "Log antenna", "log antenna", "ANTENNA", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "antenna", "contact", "logging" }, ShortActionLabel = "log antenna" },
            new(CommandValues.LogComments, BringUpLogFormHandler,
                "Log comments", "log comments", "COMMENT", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "comments", "notes", "contact", "logging" }, ShortActionLabel = "log comments" },
            new(CommandValues.NewLogEntry, BringUpLogFormHandler,
                "New log entry", "new log entry", IADIF_LogNewEntry, KeyTypes.Command, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "new", "entry", "contact", "logging" }, ShortActionLabel = "start new log entry" },
            new(CommandValues.SearchLog, SearchLogHandler,
                "Find a log entry", "log search", IADIF_Logsearch, KeyTypes.Command, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "search", "find", "contact", "logging" }, ShortActionLabel = "search log" },

            // ── Navigation / Panning ──
            new(CommandValues.DoPanning, StartPanningHandler,
                "Focus to panning", "panning", FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "pan", "panning", "stereo", "audio", "balance" }, ShortActionLabel = "go to panning" },

            // ── Scan ──
            new(CommandValues.StartScan, BeginScanHandler,
                "Start/stop scan", "start scan", FunctionGroups.Scan, KeyScope.Radio)
                { Keywords = new[] { "scan", "start", "stop", "search", "scanning" }, ShortActionLabel = "start scan" },
            new(CommandValues.SavedScan, UseSavedScanHandler,
                "Use a saved scan", "saved scan", FunctionGroups.Scan, KeyScope.Radio)
                { Keywords = new[] { "scan", "saved", "preset", "scanning" }, ShortActionLabel = "use saved scan" },
            new(CommandValues.StopScan, StopScanHandler,
                "Stop the current scan", "stop scan", FunctionGroups.Scan, KeyScope.Radio)
                { Keywords = new[] { "scan", "stop", "halt", "scanning" }, ShortActionLabel = "stop scan" },
            new(CommandValues.MemoryScan, MemoryScanHandler,
                "Memory scan", "memory scan", FunctionGroups.Scan, KeyScope.Radio)
                { Keywords = new[] { "scan", "memory", "memories", "scanning", "channel" }, ShortActionLabel = "start memory scan" },

            // ── Dialogs ──
            new(CommandValues.ShowMenus, ShowMenusHandler,
                "Show the rig's menus.", "menus", FunctionGroups.Dialog, KeyScope.Radio)
                { Keywords = new[] { "menu", "menus", "rig", "radio", "settings" }, ShortActionLabel = "show radio menus" },

            // ── Audio volume ──
            new(CommandValues.AudioGainUp, KeyTypes.Command, AudioGainUpHandler,
                "Raise RF gain or Flex slice gain.", string.Empty, true, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "volume", "gain", "audio", "louder", "up", "slice" }, ShortActionLabel = "raise audio gain" },
            new(CommandValues.AudioGainDown, KeyTypes.Command, AudioGainDownHandler,
                "Lower RF gain or Flex slice gain.", string.Empty, true, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "volume", "gain", "audio", "quieter", "down", "slice" }, ShortActionLabel = "lower audio gain" },
            // "On-radio" in these four is deliberate (Audio Arc Track A): they
            // move the radio's own jacks, which a PC-audio operator cannot
            // hear — the label is the fix for a very real confusion.
            new(CommandValues.HeadphonesUp, KeyTypes.Command, HeadphonesUpHandler,
                "Raise the on-radio headphone volume (the radio's own jack).", string.Empty, true, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "headphones", "volume", "audio", "louder", "gain", "on-radio", "jack" }, ShortActionLabel = "raise on-radio headphone volume" },
            new(CommandValues.HeadphonesDown, KeyTypes.Command, HeadphonesDownHandler,
                "Lower the on-radio headphone volume (the radio's own jack).", string.Empty, true, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "headphones", "volume", "audio", "quieter", "gain", "on-radio", "jack" }, ShortActionLabel = "lower on-radio headphone volume" },
            new(CommandValues.LineoutUp, KeyTypes.Command, LineoutUpHandler,
                "Raise the on-radio line out volume (the radio's own jacks).", string.Empty, true, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "lineout", "volume", "audio", "gain", "output", "on-radio", "jack" }, ShortActionLabel = "raise on-radio line out" },
            new(CommandValues.LineoutDown, KeyTypes.Command, LineoutDownHandler,
                "Lower the on-radio line out volume (the radio's own jacks).", string.Empty, true, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "lineout", "volume", "audio", "gain", "output", "on-radio", "jack" }, ShortActionLabel = "lower on-radio line out" },

            // ── CW / RIT / Beacon / Cluster ──
            new(CommandValues.CWZeroBeat, KeyTypes.Command, ZerobeatHandler,
                "Zerobeat CW signal.", "Zerobeat CW signal", true, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "cw", "morse", "zerobeat", "zero beat", "tune" }, ShortActionLabel = "zero beat C W signal" },
            new(CommandValues.ClearRIT, KeyTypes.Command, ClearRitHandler,
                "Clear RIT.", "Clear Rit", true, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "rit", "clear", "offset", "receive", "incremental" }, ShortActionLabel = "clear RIT" },
            new(CommandValues.ReverseBeacon, KeyTypes.Command, ReverseBeaconHandler,
                "Bring up a reverse beacon site for a call.", "Reverse Beacon", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "beacon", "reverse", "spots", "dx", "rbn" }, ShortActionLabel = "look up reverse beacon" },
            new(CommandValues.ArCluster, KeyTypes.Command, DXClusterHandler,
                "Bring up the DX spotting cluster.", "DX cluster", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "cluster", "dx", "spots", "spotting" }, ShortActionLabel = "open D X cluster" },

            // ── Logging (continued) ──
            new(CommandValues.LogStats, KeyTypes.Command, LogStatsHandler,
                "Show log statistics", "Show log statistics", false, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "statistics", "stats", "contact", "count", "logging" }, ShortActionLabel = "show log statistics" },

            // ── Audio features ──
            new(CommandValues.RemoteAudio, KeyTypes.Command, PCAudioHandler,
                "PC audio on/off", _context.AudioMenuString, false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "audio", "remote", "pc", "mute", "unmute", "on", "off" }, ShortActionLabel = "toggle P C audio" },
            // QB Track B, 2026-08-07: retargeted at the rebuilt one-page picker.
            // Keywords keep every old term so existing habits still find it, and
            // add the words for what it now also covers — someone searching
            // "where do I set my microphone" should land here rather than
            // nowhere. Binding is unchanged (none by default).
            new(CommandValues.AudioSetup, KeyTypes.Command, AudioSetupHandler,
                "Audio devices", "Audio Devices", false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "audio", "device", "devices", "setup", "settings", "configure",
                                     "preferences", "sound", "soundcard", "microphone", "mic", "speaker",
                                     "headphone", "playback", "output", "input", "alert" },
                  ShortActionLabel = "choose audio devices" },

            // ── Lookups / Debug / ATU / Reboot / TX ──
            new(CommandValues.StationLookup, KeyTypes.Command, StationLookupHandler,
                "Station lookup", "Station lookup", false, FunctionGroups.Logging, KeyScope.Global)
                { Keywords = new[] { "station", "lookup", "callsign", "qrz", "search" }, ShortActionLabel = "look up station" },
            new(CommandValues.GatherDebug, KeyTypes.Command, GatherDebugHandler,
                "Collect debug info", "Collect debug info", false, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "debug", "info", "diagnostic", "troubleshoot" }, ShortActionLabel = "collect debug info" },
            new(CommandValues.ATUMemories, KeyTypes.Command, ATUMemoriesHandler,
                "Tuner memories", "Tuner memories", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "tuner", "atu", "antenna", "memories", "tune" }, ShortActionLabel = "open ATU memories" },
            new(CommandValues.Reboot, KeyTypes.Command, RebootHandler,
                "Reboot the radio", "Reboot the radio", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "reboot", "restart", "radio", "reset" }, ShortActionLabel = "reboot radio" },
            new(CommandValues.TXControls, KeyTypes.Command, TXControlsHandler,
                "Transmit controls", "Transmit controls", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "transmit", "tx", "power", "controls", "watts", "ptt" }, ShortActionLabel = "open transmit controls" },

            // ── Logging-only actions ──
            new(CommandValues.LogPaneSwitchF6, KeyTypes.Command, LogPaneSwitchHandler,
                "Switch between radio and log panes", "Switch panes", false, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "pane", "switch", "focus", "logging" }, ShortActionLabel = "switch log pane" },
            new(CommandValues.LogCharacteristicsDialog, KeyTypes.Command, LogCharacteristicsHandler,
                "Open log characteristics dialog", "Log characteristics", false, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "characteristics", "settings", "configure", "logging" }, ShortActionLabel = "open log settings" },
            new(CommandValues.LogOpenFullForm, KeyTypes.Command, LogOpenFullFormHandler,
                "Open full log entry form", "Full log form", false, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "full", "form", "entry", "logging" }, ShortActionLabel = "open full log form" },

            // ── Context help / Status ──
            new(CommandValues.ContextHelp, KeyTypes.Command, ContextHelpHandler,
                "Context-aware command finder", "Command finder", false, FunctionGroups.Help, KeyScope.Global)
                { Keywords = new[] { "help", "context", "command", "finder", "search", "keys" }, ShortActionLabel = "open command finder" },
            new(CommandValues.SpeakStatus, KeyTypes.Command, SpeakStatusHandler,
                "Speak radio status summary", "Speak status", false, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "status", "speak", "info", "radio", "summary" }, ShortActionLabel = "speak radio status" },
            new(CommandValues.ShowStatusDialog, KeyTypes.Command, ShowStatusDialogHandler,
                "Show radio status dialog", "Status dialog", false, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "status", "dialog", "info", "radio", "show" }, ShortActionLabel = "show status dialog" },
            new(CommandValues.SpeakTxStatus, KeyTypes.Command, SpeakTxStatusHandler,
                "Speak transmit status and time remaining", "Transmit status", false, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "transmit", "ptt", "push to talk", "status", "tx", "time" }, ShortActionLabel = "speak transmit status" },

            // ── Band jumps ──
            new(CommandValues.BandJump160, KeyTypes.Command, () => _context.GetMainWindow()?.BandJump(HamBands.Bands.BandNames.m160),
                "Jump to 160 meter band", "160m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "160", "meter", "jump", "frequency" }, ShortActionLabel = "jump to 160 meters" },
            new(CommandValues.BandJump80, KeyTypes.Command, () => _context.GetMainWindow()?.BandJump(HamBands.Bands.BandNames.m80),
                "Jump to 80 meter band", "80m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "80", "meter", "jump", "frequency" }, ShortActionLabel = "jump to 80 meters" },
            new(CommandValues.BandJump60, KeyTypes.Command, () => _context.GetMainWindow()?.BandJump(HamBands.Bands.BandNames.m60),
                "Jump to 60 meter band", "60m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "60", "meter", "jump", "frequency" }, ShortActionLabel = "jump to 60 meters" },
            new(CommandValues.BandJump40, KeyTypes.Command, () => _context.GetMainWindow()?.BandJump(HamBands.Bands.BandNames.m40),
                "Jump to 40 meter band", "40m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "40", "meter", "jump", "frequency" }, ShortActionLabel = "jump to 40 meters" },
            new(CommandValues.BandJump30, KeyTypes.Command, () => _context.GetMainWindow()?.BandJump(HamBands.Bands.BandNames.m30),
                "Jump to 30 meter band", "30m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "30", "meter", "jump", "frequency", "warc" }, ShortActionLabel = "jump to 30 meters" },
            new(CommandValues.BandJump20, KeyTypes.Command, () => _context.GetMainWindow()?.BandJump(HamBands.Bands.BandNames.m20),
                "Jump to 20 meter band", "20m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "20", "meter", "jump", "frequency" }, ShortActionLabel = "jump to 20 meters" },
            new(CommandValues.BandJump17, KeyTypes.Command, () => _context.GetMainWindow()?.BandJump(HamBands.Bands.BandNames.m17),
                "Jump to 17 meter band", "17m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "17", "meter", "jump", "frequency", "warc" }, ShortActionLabel = "jump to 17 meters" },
            new(CommandValues.BandJump15, KeyTypes.Command, () => _context.GetMainWindow()?.BandJump(HamBands.Bands.BandNames.m15),
                "Jump to 15 meter band", "15m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "15", "meter", "jump", "frequency" }, ShortActionLabel = "jump to 15 meters" },
            new(CommandValues.BandJump12, KeyTypes.Command, () => _context.GetMainWindow()?.BandJump(HamBands.Bands.BandNames.m12),
                "Jump to 12 meter band", "12m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "12", "meter", "jump", "frequency", "warc" }, ShortActionLabel = "jump to 12 meters" },
            new(CommandValues.BandJump10, KeyTypes.Command, () => _context.GetMainWindow()?.BandJump(HamBands.Bands.BandNames.m10),
                "Jump to 10 meter band", "10m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "10", "meter", "jump", "frequency" }, ShortActionLabel = "jump to 10 meters" },
            new(CommandValues.BandJump6, KeyTypes.Command, () => _context.GetMainWindow()?.BandJump(HamBands.Bands.BandNames.m6),
                "Jump to 6 meter band", "6m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "6", "meter", "jump", "frequency", "vhf" }, ShortActionLabel = "jump to 6 meters" },
            new(CommandValues.BandUp, KeyTypes.Command, () => _context.GetMainWindow()?.BandNavigate(1),
                "Next higher band", "Band up", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "up", "next", "higher", "navigate" }, ShortActionLabel = "change band" },
            new(CommandValues.BandDown, KeyTypes.Command, () => _context.GetMainWindow()?.BandNavigate(-1),
                "Next lower band", "Band down", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "down", "previous", "lower", "navigate" }, ShortActionLabel = "change band" },

            // ── Mode switching ──
            new(CommandValues.ModeNext, KeyTypes.Command, () => _context.GetMainWindow()?.CycleMode(1),
                "Cycle to next mode", "Next mode", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "mode", "next", "cycle", "usb", "lsb", "cw", "am", "fm", "digu", "digl" }, ShortActionLabel = "change mode" },
            new(CommandValues.ModePrev, KeyTypes.Command, () => _context.GetMainWindow()?.CycleMode(-1),
                "Cycle to previous mode", "Previous mode", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "mode", "previous", "back", "cycle", "usb", "lsb", "cw" }, ShortActionLabel = "change mode" },
            new(CommandValues.ModeUSB, KeyTypes.Command, () => _context.GetMainWindow()?.SetMode("USB"),
                "Switch to USB mode", "USB", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "mode", "usb", "upper", "sideband", "ssb", "phone" }, ShortActionLabel = "switch to USB" },
            new(CommandValues.ModeLSB, KeyTypes.Command, () => _context.GetMainWindow()?.SetMode("LSB"),
                "Switch to LSB mode", "LSB", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "mode", "lsb", "lower", "sideband", "ssb", "phone" }, ShortActionLabel = "switch to LSB" },
            new(CommandValues.ModeCW, KeyTypes.Command, () => _context.GetMainWindow()?.SetMode("CW"),
                "Switch to CW mode", "CW", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "mode", "cw", "morse", "code", "continuous wave" }, ShortActionLabel = "switch to CW" },
            new(CommandValues.ModeAM, KeyTypes.Command, () => _context.GetMainWindow()?.SetMode("AM"),
                "Switch to AM mode", "AM", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "mode", "am", "amplitude", "modulation", "broadcast" }, ShortActionLabel = "switch to A M" },
            new(CommandValues.ModeFM, KeyTypes.Command, () => _context.GetMainWindow()?.SetMode("FM"),
                "Switch to FM mode", "FM", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "mode", "fm", "frequency", "modulation", "repeater" }, ShortActionLabel = "switch to F M" },
            new(CommandValues.ModeDIGU, KeyTypes.Command, () => _context.GetMainWindow()?.SetMode("DIGU"),
                "Switch to DIGU mode", "DIGU", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "mode", "digu", "digital", "upper", "ft8", "rtty", "psk" }, ShortActionLabel = "switch to digital upper" },
            new(CommandValues.ModeDIGL, KeyTypes.Command, () => _context.GetMainWindow()?.SetMode("DIGL"),
                "Switch to DIGL mode", "DIGL", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "mode", "digl", "digital", "lower", "ft8", "rtty", "psk" }, ShortActionLabel = "switch to digital lower" },

            // ── TX Filter ──
            new(CommandValues.TXFilterLowDown, KeyTypes.Command, TXFilterLowDownHandler,
                "Nudge TX filter low edge down", (string?)null, false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "tx", "filter", "low", "down", "transmit", "sculpt" }, ShortActionLabel = "lower T X filter low edge" },
            new(CommandValues.TXFilterLowUp, KeyTypes.Command, TXFilterLowUpHandler,
                "Nudge TX filter low edge up", (string?)null, false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "tx", "filter", "low", "up", "transmit", "sculpt" }, ShortActionLabel = "raise T X filter low edge" },
            new(CommandValues.TXFilterHighDown, KeyTypes.Command, TXFilterHighDownHandler,
                "Nudge TX filter high edge down", (string?)null, false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "tx", "filter", "high", "down", "transmit", "sculpt" }, ShortActionLabel = "lower T X filter high edge" },
            new(CommandValues.TXFilterHighUp, KeyTypes.Command, TXFilterHighUpHandler,
                "Nudge TX filter high edge up", (string?)null, false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "tx", "filter", "high", "up", "transmit", "sculpt" }, ShortActionLabel = "raise T X filter high edge" },
            new(CommandValues.SpeakTXFilter, KeyTypes.Command, SpeakTXFilterHandler,
                "Speak TX filter width", (string?)null, false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "tx", "filter", "width", "bandwidth", "speak", "transmit", "sculpt" }, ShortActionLabel = "speak T X filter" },

            // ── Dialog launchers ──
            new(CommandValues.OpenAudioWorkshop, KeyTypes.Command, OpenAudioWorkshopHandler,
                "Open Audio Workshop dialog", "Audio Workshop", false, FunctionGroups.Dialog, KeyScope.Global)
                { Keywords = new[] { "audio", "workshop", "tx", "transmit", "mic", "compander", "preset", "earcon" }, ShortActionLabel = "open audio workshop" },
            // Audio Check: "check my transmit audio" — opens the workshop and
            // keys through the PTT safety controller with the safety line
            // first. Command Finder only, no key binding (QB Track G).
            new(CommandValues.StartAudioCheck, KeyTypes.Command, StartAudioCheckHandler,
                "Check my transmit audio (Audio Check session)", "Audio Check", false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "audio", "check", "monitor", "hear", "myself", "transmit", "tx", "test" }, ShortActionLabel = "start audio check" },

            // ── Tuning ──
            new(CommandValues.TuneToggle, KeyTypes.Command, TuneToggleHandler,
                "Toggle tune carrier on or off", "Tune carrier", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "tune", "carrier", "toggle", "cw", "manual" }, ShortActionLabel = "toggle tune" },
            new(CommandValues.ATUTune, KeyTypes.Command, ATUTuneHandler,
                "Start ATU tune cycle", "ATU Tune", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "atu", "tune", "antenna", "tuner", "auto", "match", "swr" }, ShortActionLabel = "start ATU tune" },
            new(CommandValues.ToggleMeters, KeyTypes.Command, ToggleMetersHandler,
                "Toggle meter tones on or off", "Toggle Meters", false, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "meter", "tones", "sonification", "audio", "s-meter", "alc", "swr" }, ShortActionLabel = "toggle meter tones" },

            // ── 60m channels ──
            new(CommandValues.SixtyMeterChannelUp, KeyTypes.Command, () => _context.GetMainWindow()?.SixtyMeterChannelNavigate(1),
                "Next 60 meter channel", "60m Channel Up", false, FunctionGroups.Tuning, KeyScope.Radio)
                { Keywords = new[] { "60", "meter", "channel", "up", "next", "five", "navigate" }, ShortActionLabel = "next 60 meter channel" },
            new(CommandValues.SixtyMeterChannelDown, KeyTypes.Command, () => _context.GetMainWindow()?.SixtyMeterChannelNavigate(-1),
                "Previous 60 meter channel", "60m Channel Down", false, FunctionGroups.Tuning, KeyScope.Radio)
                { Keywords = new[] { "60", "meter", "channel", "down", "previous", "five", "navigate" }, ShortActionLabel = "previous 60 meter channel" },

            // ── ScreenFields expanders ──
            new(CommandValues.ToggleDspExpander, KeyTypes.Command, () => _context.GetMainWindow()?.ToggleScreenFieldsCategory(0),
                "Toggle DSP expander in ScreenFields panel", "DSP Expander", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "dsp", "noise", "reduction", "expander", "screenfields", "panel" }, ShortActionLabel = "toggle D S P expander" },
            new(CommandValues.ToggleAudioExpander, KeyTypes.Command, () => _context.GetMainWindow()?.ToggleScreenFieldsCategory(1),
                "Toggle Audio expander in ScreenFields panel", "Audio Expander", false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "audio", "expander", "screenfields", "panel" }, ShortActionLabel = "toggle audio expander" },
            new(CommandValues.ToggleReceiverExpander, KeyTypes.Command, () => _context.GetMainWindow()?.ToggleScreenFieldsCategory(2),
                "Toggle Receiver expander in ScreenFields panel", "Receiver Expander", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "receiver", "rx", "expander", "screenfields", "panel" }, ShortActionLabel = "toggle receiver expander" },
            new(CommandValues.ToggleTransmissionExpander, KeyTypes.Command, () => _context.GetMainWindow()?.ToggleScreenFieldsCategory(3),
                "Toggle Transmission expander in ScreenFields panel", "Transmission Expander", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "transmission", "tx", "expander", "screenfields", "panel" }, ShortActionLabel = "toggle transmission expander" },
            new(CommandValues.ToggleAntennaExpander, KeyTypes.Command, () => _context.GetMainWindow()?.ToggleScreenFieldsCategory(4),
                "Toggle Antenna expander in ScreenFields panel", "Antenna Expander", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "antenna", "ant", "expander", "screenfields", "panel" }, ShortActionLabel = "toggle antenna expander" },

            // Sprint 28 Phase 3.10 diagnostic — toggle BrailleStatusEngine on/off
            // for cursor routing investigation. When off, braille display naturally
            // reflects the focused control (FreqOut), allowing cursor routing to
            // reach DisplayBox's SelectionStart and fire SelectionChanged.
            new(CommandValues.ToggleBrailleStatus, KeyTypes.Command, () => _context.GetMainWindow()?.ToggleBrailleStatus(),
                "Toggle braille status line (diagnostic for cursor routing)", "Braille Status Toggle", false, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "braille", "status", "toggle", "cursor", "routing", "diagnostic" }, ShortActionLabel = "toggle braille status" },

            // ── Speak / Repeat ──
            new(CommandValues.SpeakFrequency, KeyTypes.Command, () => _context.GetMainWindow()?.SpeakFrequency(),
                "Speak current frequency and mode", "Speak Frequency", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "frequency", "freq", "speak", "readback" }, ShortActionLabel = "speak frequency" },
            new(CommandValues.RepeatLastMessage, KeyTypes.Command, RepeatLastMessageHandler,
                "Repeat the last spoken message", "Repeat Last Message", false, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "repeat", "last", "message", "speech", "again" }, ShortActionLabel = "repeat last message" },

            // ── Verbosity (Sprint 24 Phase 6) ──
            new(CommandValues.CycleVerbosity, KeyTypes.Command, CycleVerbosityHandler,
                "Cycle speech verbosity (Chatty/Terse/Off)", "Cycle Verbosity", false, FunctionGroups.Audio, KeyScope.Global)
                { Keywords = new[] { "verbosity", "speech", "level", "chatty", "terse", "off", "verbose" }, ShortActionLabel = "cycle verbosity" },
            new(CommandValues.ToggleMeterTonesGlobal, KeyTypes.Command, ToggleMeterTonesGlobalHandler,
                "Toggle meter tones on/off", "Toggle Meter Tones", false, FunctionGroups.Audio, KeyScope.Global)
                { Keywords = new[] { "meter", "tones", "toggle", "audio", "sonification" }, ShortActionLabel = "toggle meter tones globally" },

            // ── Slice (Sprint 24 Phase 8) ──
            new(CommandValues.MuteSlice, KeyTypes.Command, MuteSliceHandler,
                "Mute or unmute current slice", "Mute Slice", false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "mute", "slice", "audio", "unmute", "silence" }, ShortActionLabel = "mute slice" },
            new(CommandValues.MuteAllSlices, KeyTypes.Command, MuteAllSlicesHandler,
                "Mute or unmute every slice at once", "Mute All Slices", false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "mute", "all", "slices", "audio", "unmute", "silence", "fleet" }, ShortActionLabel = "mute all slices" },
            new(CommandValues.ReleaseAllExtraSlices, KeyTypes.Command, ReleaseAllExtraSlicesHandler,
                "Release every slice except the first, back to one slice", "Release All Extra Slices", false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "release", "all", "slices", "extra", "clean", "reset", "single" }, ShortActionLabel = "release extra slices" },

            // ── Former hard-wired meta-commands (QB Track H, 2026-08-07) ──
            // These lived as hard-wired chords in MainWindow_PreviewKeyDown,
            // invisible to the registry, the Command Finder, and the Keys
            // surface — and their chords silently shadowed registry bindings
            // (Ctrl+Shift+M ate MemoryScan; Ctrl+Shift+F ate SpeakFrequency
            // AND SearchLog-in-Logging). Registered here they are visible,
            // rebindable, and scope-checked like everything else.
            new(CommandValues.ToggleTuningMode, KeyTypes.Command, ToggleTuningModeHandler,
                "Switch between Classic and Modern tuning mode", "Tuning mode", false, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "tuning", "mode", "classic", "modern", "switch", "toggle" }, ShortActionLabel = "switch tuning mode" },
            new(CommandValues.ToggleLoggingMode, KeyTypes.Command, ToggleLoggingModeHandler,
                "Enter or exit Logging mode", "Logging mode", false, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "logging", "log", "mode", "enter", "exit", "toggle" }, ShortActionLabel = "switch logging mode" },
            new(CommandValues.ToggleFreqReadout, KeyTypes.Command, ToggleFreqReadoutHandler,
                "Toggle frequency speech readout on or off", "Frequency readout", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "frequency", "readout", "speech", "announce", "quiet", "toggle" }, ShortActionLabel = "toggle frequency readout" },
            new(CommandValues.SpeakRXFilter, KeyTypes.Command, SpeakRXFilterHandler,
                "Speak RX filter low, high, and width", "Speak RX filter", false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "rx", "filter", "width", "bandwidth", "speak", "receive" }, ShortActionLabel = "speak R X filter" },
        };
    }

    // ────────────────────────────────────────────────────────────────
    //  Handler Methods — Sprint 24 Phase 3
    //  Each handler wraps VB functionality via _context delegates
    //  or calls MainWindow/EarconPlayer/MeterToneEngine directly.
    // ────────────────────────────────────────────────────────────────

    #region Help Handlers

    private void ShowHelpHandler() => _context.GetMainWindow()?.DisplayHelp();
    private void ShowContextHelpHandler() => HelpLauncher.ShowHelp();

    #endregion

    #region Navigation / Routing Handlers

    private void DisplayFreqHandler()
    {
        // If scanning, pause first.
        if (_context.GetScanRunning())
            _context.StopScan();
        _context.DisplayFreq();
    }

    private void ResumeScanHandler() => _context.ResumeScan();
    private void GotoReceiveHandler() => _context.GotoReceive();
    private void GotoSendHandler() => _context.GotoSend();
    private void GotoSendDirectHandler() => _context.GotoSendDirect();
    private void WriteFreqHandler() => _context.WriteFreq();
    private void DisplayMemoryHandler() => _context.DisplayMemory();
    private void CycleContinuousHandler() => _context.CycleContinuous();
    private void StartPanningHandler() => _context.StartPanning();

    #endregion

    #region Scan Handlers

    private void BeginScanHandler() => _context.BeginScan();
    private void UseSavedScanHandler() => _context.UseSavedScan("");
    private void StopScanHandler() => _context.StopScan();
    private void MemoryScanHandler() => _context.MemoryScan();

    #endregion

    #region Logging Handlers

    private void BringUpLogFormHandler()
    {
        // Get the ADIF tag for the current command.
        var kt = Lookup(CommandId);
        var adifTag = kt?.ADIFTag ?? string.Empty;
        _context.BringUpLogForm(adifTag);
    }

    private void SetLogDateTimeHandler() => _context.SetLogDateTime();
    private void FinalizeLogHandler() => _context.FinalizeLog();
    private void GetLogFileNameHandler() => _context.GetLogFileName();
    private void SearchLogHandler() => _context.SearchLog();
    private void LogStatsHandler() => _context.LogStats();
    private void LogPaneSwitchHandler() => _context.LogPaneSwitch();
    private void LogCharacteristicsHandler() => _context.ShowLogCharacteristics();
    private void LogOpenFullFormHandler() => _context.LogOpenFullForm();

    #endregion

    #region CW Handlers

    private void StopCWHandler()
    {
        var rig = _context.GetRigControl();
        if (rig != null)
        {
            rig.StopCW();
            Radios.ScreenReaderOutput.Speak("CW stopped", Radios.VerbosityLevel.Terse, false);
        }
        else
        {
            Radios.ScreenReaderOutput.Speak("No radio connected", Radios.VerbosityLevel.Critical, true);
        }
    }

    #endregion

    #region Audio Volume Handlers

    private void AudioGainUpHandler()
    {
        var rig = _context.GetRigControl();
        if (rig != null) rig.AudioGain += 5;
    }

    private void AudioGainDownHandler()
    {
        var rig = _context.GetRigControl();
        if (rig != null) rig.AudioGain -= 5;
    }

    // Headphone/lineout gain handlers compute the clamped target locally and
    // speak it (matching the menu's AdjustValue pattern) — the FlexBase setters
    // enqueue the change asynchronously, so reading the property back right
    // after the set would announce the stale value.
    private void HeadphonesUpHandler() => AdjustOutputGain("On-radio headphone",
        r => r.HeadphoneGain, (r, v) => r.HeadphoneGain = v, +5);

    private void HeadphonesDownHandler() => AdjustOutputGain("On-radio headphone",
        r => r.HeadphoneGain, (r, v) => r.HeadphoneGain = v, -5);

    // QB Track A (2026-08-07): these used to refuse to run while PC audio was
    // on (!rig.PCAudio). The lineout jacks and the PC audio stream are
    // independent outputs — the radio drives both at once — so the gate was
    // wrong, and it was also silent (a bound key that did nothing). The
    // headphone handlers never gated; now the pair behaves identically.
    private void LineoutUpHandler() => AdjustOutputGain("On-radio line out",
        r => r.LineoutGain, (r, v) => r.LineoutGain = v, +5);

    private void LineoutDownHandler() => AdjustOutputGain("On-radio line out",
        r => r.LineoutGain, (r, v) => r.LineoutGain = v, -5);

    private void AdjustOutputGain(string label, Func<Radios.FlexBase, int> getter,
        Action<Radios.FlexBase, int> setter, int delta)
    {
        var rig = _context.GetRigControl();
        if (rig == null) return; // dispatcher already announced "no radio connected"
        int newVal = Math.Clamp(getter(rig) + delta, 0, 100);
        setter(rig, newVal);
        Radios.ScreenReaderOutput.Speak($"{label} {newVal}", Radios.VerbosityLevel.Terse, true);
    }

    #endregion

    #region RIT / Zerobeat Handlers

    private void ClearRitHandler()
    {
        var rig = _context.GetRigControl();
        if (rig == null) return;
        var r = rig.RIT;
        r.Value = 0;
        rig.RIT = r;
    }

    private void ZerobeatHandler()
    {
        var rig = _context.GetRigControl();
        if (rig == null) return;
        if (rig.Mode != "CW")
        {
            Radios.ScreenReaderOutput.Speak("Zerobeat requires CW mode", Radios.VerbosityLevel.Critical, true);
            return;
        }
        rig.CWZeroBeat();
        Radios.ScreenReaderOutput.Speak("Zerobeat", Radios.VerbosityLevel.Terse, true);
    }

    #endregion

    #region S-Meter / Meter Handlers

    private void SmeterDisplayHandler()
    {
        var rig = _context.GetRigControl();
        if (rig == null) return;
        rig.SmeterInDBM = !rig.SmeterInDBM;
        _context.GetMainWindow()?.SetupOperationsMenu();
        // Speak the result — this handler was silent when invoked by key,
        // violating no-silent-keystrokes. A stale keymap binding parked on
        // Ctrl+Shift+W dispatched here instead of the Audio Workshop and the
        // only clue was the S-meter quietly "changing units" (2026-08-07
        // live finding). Speech makes any future mis-dispatch self-diagnosing.
        Radios.ScreenReaderOutput.Speak(
            rig.SmeterInDBM ? "S meter in dBm" : "S meter in S units",
            Radios.VerbosityLevel.Terse, true);
    }

    private void ReadSMeterHandler()
    {
        var rig = _context.GetRigControl();
        if (rig == null)
        {
            Radios.ScreenReaderOutput.Speak("No radio connected", Radios.VerbosityLevel.Critical);
            return;
        }
        int smeter = (int)rig.SMeter;
        string msg;
        if (rig.Transmit)
            msg = $"Power {smeter}";
        else if (rig.SmeterInDBM)
            msg = $"S meter {smeter} dBm";
        else if (smeter > 9)
            msg = $"S 9 plus {(smeter - 9) * 10}";
        else
            msg = $"S {smeter}";
        Radios.ScreenReaderOutput.Speak(msg, Radios.VerbosityLevel.Terse, true);
    }

    private void ToggleMeterTonesHandler()
    {
        MeterToneEngine.Enabled = !MeterToneEngine.Enabled;
        var state = MeterToneEngine.Enabled ? "on" : "off";
        if (MeterToneEngine.Enabled)
            EarconPlayer.FeatureOnTone();
        else
            EarconPlayer.FeatureOffTone();
        Radios.ScreenReaderOutput.Speak($"Meter tones {state}", Radios.VerbosityLevel.Terse);
    }

    private void CycleMeterPresetHandler()
    {
        MeterToneEngine.CyclePreset();
        Radios.ScreenReaderOutput.Speak($"Meter preset: {MeterToneEngine.CurrentPreset}", Radios.VerbosityLevel.Terse);
    }

    private void SpeakMetersHandler() => MeterToneEngine.SpeakMeters();

    #endregion

    #region TX Filter Handlers

    private void TXFilterLowDownHandler()
    {
        var rig = _context.GetRigControl();
        if (rig == null) return;
        int newLow = Math.Max(0, rig.TXFilterLow - 50);
        rig.TXFilterLow = newLow;
        EarconPlayer.FilterEdgeMoveTone(true);
        int width = rig.TXFilterHigh - newLow;
        Radios.ScreenReaderOutput.Speak($"TX low {newLow}, width {width}", Radios.VerbosityLevel.Terse);
    }

    private void TXFilterLowUpHandler()
    {
        var rig = _context.GetRigControl();
        if (rig == null) return;
        int newLow = rig.TXFilterLow + 50;
        if (newLow >= rig.TXFilterHigh - 50)
        {
            newLow = rig.TXFilterHigh - 50;
            EarconPlayer.FilterBoundaryHitTone(true);
        }
        else
        {
            EarconPlayer.FilterEdgeMoveTone(true);
        }
        rig.TXFilterLow = newLow;
        int width = rig.TXFilterHigh - newLow;
        Radios.ScreenReaderOutput.Speak($"TX low {newLow}, width {width}", Radios.VerbosityLevel.Terse);
    }

    private void TXFilterHighDownHandler()
    {
        var rig = _context.GetRigControl();
        if (rig == null) return;
        int newHigh = rig.TXFilterHigh - 50;
        if (newHigh <= rig.TXFilterLow + 50)
        {
            newHigh = rig.TXFilterLow + 50;
            EarconPlayer.FilterBoundaryHitTone(false);
        }
        else
        {
            EarconPlayer.FilterEdgeMoveTone(false);
        }
        rig.TXFilterHigh = newHigh;
        int width = newHigh - rig.TXFilterLow;
        Radios.ScreenReaderOutput.Speak($"TX high {newHigh}, width {width}", Radios.VerbosityLevel.Terse);
    }

    private void TXFilterHighUpHandler()
    {
        var rig = _context.GetRigControl();
        if (rig == null) return;
        int newHigh = Math.Min(10000, rig.TXFilterHigh + 50);
        rig.TXFilterHigh = newHigh;
        EarconPlayer.FilterEdgeMoveTone(false);
        int width = newHigh - rig.TXFilterLow;
        Radios.ScreenReaderOutput.Speak($"TX high {newHigh}, width {width}", Radios.VerbosityLevel.Terse);
    }

    private void SpeakTXFilterHandler() => SpeakTXFilterWidth();

    #endregion

    #region Dialog / Feature Handlers

    private void ShowMenusHandler() => _context.ShowMenus();

    // ── Former hard-wired meta-command handlers (QB Track H) ──

    private void ToggleTuningModeHandler()
    {
        // ToggleUIMode silently no-ops in Logging mode — speak instead
        // (no-silent-keystrokes: the chord did press, tell the user why
        // nothing changed and what gets them out).
        if (_context.GetActiveUIMode() == 2)
        {
            Radios.ScreenReaderOutput.Speak(
                "In Logging mode. Press Control Shift L to return to tuning.",
                Radios.VerbosityLevel.Terse, true);
            return;
        }
        _context.GetMainWindow()?.ToggleUIMode();
    }

    private void ToggleLoggingModeHandler()
    {
        var mw = _context.GetMainWindow();
        if (mw == null) return;
        if (_context.GetActiveUIMode() == 2) // Logging
            mw.ExitLoggingMode();
        else
            mw.EnterLoggingMode();
    }

    private void ToggleFreqReadoutHandler() => _context.GetMainWindow()?.ToggleFreqReadoutCommand();

    private void SpeakRXFilterHandler() => SpeakRXFilterWidth();
    private void ReverseBeaconHandler() => _context.ShowReverseBeacon();
    private void DXClusterHandler() => _context.ShowDXCluster();
    private void StationLookupHandler() => _context.StationLookup();
    private void GatherDebugHandler() => _context.GatherDebug();
    private void ATUMemoriesHandler() => _context.ShowATUMemories();
    private void RebootHandler() => _context.RebootRadio();
    private void TXControlsHandler() => _context.ShowTXControls();
    private void PCAudioHandler() => _context.PCAudioToggle();
    private void AudioSetupHandler() => _context.AudioSetup();
    private void ContextHelpHandler() => _context.GetMainWindow()?.ShowCommandFinder();

    private void OpenAudioWorkshopHandler()
    {
        var rig = _context.GetRigControl();
        var mw = _context.GetMainWindow();
        mw?.Dispatcher.Invoke(() => Dialogs.AudioWorkshopDialog.ShowOrFocus(rig, 0));
    }

    private void StartAudioCheckHandler()
    {
        var rig = _context.GetRigControl();
        var mw = _context.GetMainWindow();
        mw?.Dispatcher.Invoke(() => Dialogs.AudioWorkshopDialog.ShowOrFocusAndStartCheck(rig));
    }

    #endregion

    #region Tuning Handlers

    private void TuneToggleHandler()
    {
        if (_context.GetRigControl() == null) return;
        _context.GetMainWindow()?.ToggleTuneCarrier();
    }

    private void ATUTuneHandler()
    {
        var rig = _context.GetRigControl();
        if (rig == null) return;
        if (!rig.HasATU)
        {
            EarconPlayer.LeaderInvalidTone();
            Radios.ScreenReaderOutput.Speak("No antenna tuner on this radio", Radios.VerbosityLevel.Critical);
            return;
        }
        _context.GetMainWindow()?.StartATUTuneCycle();
    }

    private void ToggleMetersHandler() => _context.GetMainWindow()?.ToggleMetersPanel();

    #endregion

    #region Status Handlers

    private void SpeakStatusHandler()
    {
        var rig = _context.GetRigControl();
        var mw = _context.GetMainWindow();
        var msg = RadioStatusBuilder.BuildFullSliceStatus(rig);

        // Append PTT detail if transmitting
        var pttStatus = mw?.GetPttStatusText();
        if (pttStatus != null) msg = msg + ", " + pttStatus;

        // Append filter edge mode if active
        var filterEdge = mw?.GetFilterEdgeStatus();
        if (filterEdge != null) msg = msg + ", " + filterEdge;

        // Append tuning mode
        var tuningMode = mw?.GetTuningModeStatus();
        if (tuningMode != null) msg = msg + ", " + tuningMode;

        // Append frequency readout state if off
        var freqReadout = mw?.GetFreqReadoutStatus();
        if (freqReadout != null) msg = msg + ", " + freqReadout;

        // Append filter preset if on a named preset
        var filterPreset = mw?.GetFilterPresetStatus();
        if (filterPreset != null) msg = msg + ", " + filterPreset;

        // Append meter tone state if active
        var meterStatus = mw?.GetMeterStatus();
        if (meterStatus != null) msg = msg + ", " + meterStatus;

        Radios.ScreenReaderOutput.Speak(msg, Radios.VerbosityLevel.Terse, true);
    }

    private void MuteSliceHandler()
    {
        var rig = _context.GetRigControl();
        if (rig == null)
        {
            Radios.ScreenReaderOutput.Speak("No radio connected", Radios.VerbosityLevel.Critical, true);
            return;
        }
        bool newMute = !rig.SliceMute;
        rig.SliceMute = newMute;
        if (newMute) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
        string letter = rig.VFOToLetter(rig.RXVFO);
        Radios.ScreenReaderOutput.Speak(
            newMute ? $"Slice {letter} muted" : $"Slice {letter} unmuted",
            Radios.VerbosityLevel.Terse, true);
    }

    private void MuteAllSlicesHandler()
    {
        var rig = _context.GetRigControl();
        if (rig == null)
        {
            Radios.ScreenReaderOutput.Speak("No radio connected", Radios.VerbosityLevel.Critical, true);
            return;
        }
        bool target = !rig.AllMySlicesMuted;
        rig.SetAllMySlicesMute(target);
        if (target) EarconPlayer.MuteAllOnTone();
        else EarconPlayer.MuteAllOffTone();
        Radios.ScreenReaderOutput.Speak(
            target ? "All slices muted" : "All slices unmuted",
            Radios.VerbosityLevel.Terse, true);
    }

    private void ReleaseAllExtraSlicesHandler()
    {
        var rig = _context.GetRigControl();
        if (rig == null)
        {
            Radios.ScreenReaderOutput.Speak("No radio connected", Radios.VerbosityLevel.Critical, true);
            return;
        }
        int before = rig.MyNumSlices;
        if (before <= 1)
        {
            Radios.ScreenReaderOutput.Speak("Only one slice active", Radios.VerbosityLevel.Terse, true);
            return;
        }
        if (rig.ReleaseAllExtraSlices())
        {
            EarconPlayer.MuteAllOnTone();
            int removed = before - 1;
            string keptLetter = rig.VFOToLetter(rig.RXVFO);
            Radios.ScreenReaderOutput.Speak(
                $"Released {removed} extra {(removed == 1 ? "slice" : "slices")}, slice {keptLetter} active",
                Radios.VerbosityLevel.Terse, true);
        }
    }

    private void ShowStatusDialogHandler()
    {
        var rig = _context.GetRigControl();
        var dialog = new Dialogs.StatusDialog { Rig = rig };
        dialog.ShowDialog();
    }

    private void SpeakTxStatusHandler()
    {
        var mw = _context.GetMainWindow();
        var rig = _context.GetRigControl();

        // Context-aware ordering (Noel, field use 2026-08-11: "I really don't
        // need to hear my TX before mic if we're going to use this to monitor
        // audio"). Transmit state is only information when the operator does
        // not already know it — and they just keyed the radio. So while
        // transmitting, LEAD with the live mic-audio verdict (the one thing
        // they lack) and let the status follow; while receiving, the status
        // is the entire point and stays exactly as before.
        //
        // The recent (~1.5 s) peak follows the level back down, unlike the
        // whole-transmit peak, so riding mic gain hears the effect live.
        if (rig != null && rig.Transmit)
        {
            string verdict = rig.ScMicRecentDb > -140f
                ? FormatMicVerdict(rig.ScMicRecentDb)
                : "Mic audio, no reading yet";
            var status = mw?.GetPttStatusText();
            string text = status != null ? verdict + ". " + status : verdict;
            Radios.ScreenReaderOutput.Speak(text, Radios.VerbosityLevel.Terse, true);
            return;
        }

        Radios.ScreenReaderOutput.Speak(mw?.GetPttStatusText() ?? "Receiving",
            Radios.VerbosityLevel.Terse, true);
    }

    /// <summary>
    /// Format a mic-audio reading for speech, honoring the operator's
    /// verdict-output preference (Settings → Notifications): plain English,
    /// dBFS numbers, or both. Conservative default is both — exactly what
    /// shipped before the setting existed.
    /// </summary>
    private string FormatMicVerdict(float peakDb, bool lastTransmit = false)
    {
        var mode = (MicVerdictOutputMode?)_context.GetMainWindow()?.CurrentAudioConfig?.MicVerdictOutput
            ?? MicVerdictOutputMode.Both;
        string lead = lastTransmit ? "Mic audio last transmit" : "Mic audio";
        string verdict = Dialogs.AudioWorkshopDialog.MicAudioVerdict(peakDb);
        return mode switch
        {
            MicVerdictOutputMode.Plain => $"{lead} {verdict}",
            MicVerdictOutputMode.Numbers => $"{lead} peak {peakDb:F0} dBFS",
            _ => $"{lead} {verdict}, peak {peakDb:F0} dBFS",
        };
    }

    private void RepeatLastMessageHandler()
    {
        var last = Radios.ScreenReaderOutput.LastMessage;
        if (string.IsNullOrEmpty(last))
            Radios.ScreenReaderOutput.Speak("No previous message");
        else
            Radios.ScreenReaderOutput.Speak(last, true);
    }

    private void CycleVerbosityHandler()
    {
        var newLevel = Radios.ScreenReaderOutput.CycleVerbosity();
        // Persist immediately
        SaveVerbositySetting();
    }

    private void ToggleMeterTonesGlobalHandler()
    {
        MeterToneEngine.Enabled = !MeterToneEngine.Enabled;
        string state = MeterToneEngine.Enabled ? "on" : "off";
        Radios.ScreenReaderOutput.Speak($"Meter tones {state}", Radios.VerbosityLevel.Terse, true);
        if (MeterToneEngine.Enabled)
            EarconPlayer.FeatureOnTone();
        else
            EarconPlayer.FeatureOffTone();
    }

    private void ToggleEarconMute()
    {
        EarconPlayer.EarconsEnabled = !EarconPlayer.EarconsEnabled;
        string state = EarconPlayer.EarconsEnabled ? "on" : "off";
        Radios.ScreenReaderOutput.Speak($"Alert sounds {state}", Radios.VerbosityLevel.Terse, true);
        // Save to config
        var configDir = _context.GetConfigDirectory?.Invoke();
        if (configDir != null)
        {
            var config = AudioOutputConfig.Load(configDir);
            config.EarconsEnabled = EarconPlayer.EarconsEnabled;
            config.Save(configDir);
        }
    }

    /// <summary>
    /// Persist current verbosity to audio config.
    /// </summary>
    private void SaveVerbositySetting()
    {
        var configDir = _context.GetConfigDirectory?.Invoke();
        if (configDir == null) return;
        var config = AudioOutputConfig.Load(configDir);
        config.CaptureFromEngine();
        config.Save(configDir);
    }

    #endregion

    // ────────────────────────────────────────────────────────────────
    //  Default key bindings — scope-aware
    // ────────────────────────────────────────────────────────────────

    private readonly KeyDefType[] _defaultKeys =
    {
        // --- Global scope ---
        new(Keys.F1, CommandValues.ShowContextHelp, KeyScope.Global),
        new(Keys.F12, CommandValues.StopCW, KeyScope.Global),
        new(Keys.L | Keys.Control, CommandValues.StationLookup, KeyScope.Global),
        new(Keys.None, CommandValues.GatherDebug, KeyScope.Global),

        // --- Radio scope ---
        new(Keys.F2, CommandValues.ShowFreq, KeyScope.Radio),
        new(Keys.F | Keys.Control, CommandValues.SetFreq, KeyScope.Radio),
        new(Keys.None, CommandValues.ShowMemory, KeyScope.Radio),
        // MemoryScan was "bound" to Ctrl+Shift+M for sprints, but the chord
        // never reached it — the hard-wired ToggleUIMode meta-command consumed
        // Ctrl+Shift+M at window level first (QB Track H shadow sweep,
        // 2026-08-07). Now that ToggleTuningMode owns Ctrl+Shift+M in the
        // registry (Global scope), MemoryScan is honestly unbound: reachable
        // via Command Finder, bindable in the Hotkey Editor.
        new(Keys.None, CommandValues.MemoryScan, KeyScope.Radio),
        new(Keys.None, CommandValues.SmeterDBM, KeyScope.Radio),
        new(Keys.S | Keys.Control, CommandValues.ReadSMeter, KeyScope.Radio),
        new(Keys.M | Keys.Control | Keys.Alt, CommandValues.ToggleMeterTones, KeyScope.Radio),
        new(Keys.P | Keys.Control | Keys.Alt, CommandValues.CycleMeterPreset, KeyScope.Radio),
        new(Keys.V | Keys.Control | Keys.Alt, CommandValues.SpeakMeters, KeyScope.Radio),
        new(Keys.None, CommandValues.CycleContinuous, KeyScope.Radio),
        new(Keys.None, CommandValues.LogForm, KeyScope.Radio),
        new(Keys.C | Keys.Control | Keys.Shift, CommandValues.ClearRIT, KeyScope.Radio),
        new(Keys.None, CommandValues.StartScan, KeyScope.Radio),
        new(Keys.X | Keys.Alt | Keys.Shift, CommandValues.ArCluster, KeyScope.Radio),
        new(Keys.R | Keys.Control | Keys.Alt, CommandValues.ReverseBeacon, KeyScope.Radio),
        new(Keys.P | Keys.Control, CommandValues.DoPanning, KeyScope.Radio),
        new(Keys.None, CommandValues.SavedScan, KeyScope.Radio),
        new(Keys.Z | Keys.Control, CommandValues.StopScan, KeyScope.Radio),
        new(Keys.None, CommandValues.ShowMenus, KeyScope.Radio),
        // Sprint 29 Track F (tuning unity) — these six audio-gain pairs were
        // bound to Alt/Shift PageUp/PageDown. They moved into the Audio expander
        // (Ctrl+Shift+U → arrow to Volume / Headphone Level / Line Out Level)
        // because audio levels aren't real-time controls during a QSO and
        // hotkey toggles-vs-values discipline says values live in their fields.
        // Slots intentionally left unbound and reserved per the 2026-05-02 ACK
        // (option 2) — leave in place so a future sprint doesn't accidentally
        // claim them without thinking about this design.
        new(Keys.None, CommandValues.AudioGainUp, KeyScope.Radio),
        new(Keys.None, CommandValues.AudioGainDown, KeyScope.Radio),
        new(Keys.None, CommandValues.HeadphonesUp, KeyScope.Radio),
        new(Keys.None, CommandValues.HeadphonesDown, KeyScope.Radio),
        new(Keys.None, CommandValues.LineoutUp, KeyScope.Radio),
        new(Keys.None, CommandValues.LineoutDown, KeyScope.Radio),
        new(Keys.None, CommandValues.RemoteAudio, KeyScope.Radio),
        new(Keys.None, CommandValues.AudioSetup, KeyScope.Radio),
        new(Keys.None, CommandValues.ATUMemories, KeyScope.Radio),
        new(Keys.None, CommandValues.Reboot, KeyScope.Radio),
        new(Keys.None, CommandValues.TXControls, KeyScope.Radio),

        // Band jumps
        new(Keys.F3, CommandValues.BandJump160, KeyScope.Radio),
        new(Keys.F4, CommandValues.BandJump80, KeyScope.Radio),
        new(Keys.F5, CommandValues.BandJump40, KeyScope.Radio),
        new(Keys.F6, CommandValues.BandJump20, KeyScope.Radio),
        new(Keys.F7, CommandValues.BandJump15, KeyScope.Radio),
        new(Keys.F8, CommandValues.BandJump10, KeyScope.Radio),
        new(Keys.F9, CommandValues.BandJump6, KeyScope.Radio),
        new(Keys.F3 | Keys.Shift, CommandValues.BandJump60, KeyScope.Radio),
        new(Keys.F4 | Keys.Shift, CommandValues.BandJump30, KeyScope.Radio),
        new(Keys.F5 | Keys.Shift, CommandValues.BandJump17, KeyScope.Radio),
        new(Keys.F6 | Keys.Shift, CommandValues.BandJump12, KeyScope.Radio),
        new(Keys.Up | Keys.Alt, CommandValues.BandUp, KeyScope.Radio),
        new(Keys.Down | Keys.Alt, CommandValues.BandDown, KeyScope.Radio),

        // Mode switching
        new(Keys.M | Keys.Alt, CommandValues.ModeNext, KeyScope.Radio),
        new(Keys.M | Keys.Alt | Keys.Shift, CommandValues.ModePrev, KeyScope.Radio),
        new(Keys.U | Keys.Alt, CommandValues.ModeUSB, KeyScope.Radio),
        new(Keys.L | Keys.Alt, CommandValues.ModeLSB, KeyScope.Radio),
        new(Keys.C | Keys.Alt, CommandValues.ModeCW, KeyScope.Radio),
        new(Keys.A | Keys.Alt, CommandValues.ModeAM, KeyScope.Radio),
        new(Keys.F | Keys.Alt, CommandValues.ModeFM, KeyScope.Radio),
        new(Keys.D | Keys.Alt, CommandValues.ModeDIGU, KeyScope.Radio),
        new(Keys.D | Keys.Alt | Keys.Shift, CommandValues.ModeDIGL, KeyScope.Radio),
        new(Keys.Z | Keys.Alt, CommandValues.CWZeroBeat, KeyScope.Radio),

        // Routing
        new(Keys.F2 | Keys.Control | Keys.Shift, CommandValues.ResumeTheScan, KeyScope.Radio),
        new(Keys.F3 | Keys.Control | Keys.Shift, CommandValues.ShowReceived, KeyScope.Radio),
        new(Keys.F4 | Keys.Control | Keys.Shift, CommandValues.ShowSend, KeyScope.Radio),
        new(Keys.F5 | Keys.Control | Keys.Shift, CommandValues.ShowSendDirect, KeyScope.Radio),

        // --- Logging scope ---
        new(Keys.C | Keys.Alt, CommandValues.LogCall, KeyScope.Logging),
        new(Keys.T | Keys.Alt, CommandValues.LogHisRST, KeyScope.Logging),
        new(Keys.R | Keys.Alt, CommandValues.LogMyRST, KeyScope.Logging),
        new(Keys.N | Keys.Alt, CommandValues.LogHandle, KeyScope.Logging),
        new(Keys.Q | Keys.Alt, CommandValues.LogQTH, KeyScope.Logging),
        new(Keys.S | Keys.Alt, CommandValues.LogState, KeyScope.Logging),
        new(Keys.G | Keys.Alt, CommandValues.LogGrid, KeyScope.Logging),
        new(Keys.E | Keys.Alt, CommandValues.LogComments, KeyScope.Logging),
        new(Keys.D | Keys.Alt, CommandValues.LogDateTime, KeyScope.Logging),
        new(Keys.W | Keys.Control, CommandValues.LogFinalize, KeyScope.Logging),
        new(Keys.N | Keys.Control, CommandValues.NewLogEntry, KeyScope.Logging),
        new(Keys.None, CommandValues.LogFileName, KeyScope.Logging),
        new(Keys.None, CommandValues.LogMode, KeyScope.Logging),
        new(Keys.None, CommandValues.LogRig, KeyScope.Logging),
        new(Keys.None, CommandValues.LogAnt, KeyScope.Logging),
        new(Keys.F | Keys.Control | Keys.Shift, CommandValues.SearchLog, KeyScope.Logging),
        new(Keys.None, CommandValues.LogStats, KeyScope.Logging),
        new(Keys.F6, CommandValues.LogPaneSwitchF6, KeyScope.Logging),
        new(Keys.N | Keys.Control | Keys.Shift, CommandValues.LogCharacteristicsDialog, KeyScope.Logging),
        new(Keys.L | Keys.Control | Keys.Alt, CommandValues.LogOpenFullForm, KeyScope.Logging),

        // --- Back to Global ---
        new(Keys.Oem2 | Keys.Control, CommandValues.ContextHelp, KeyScope.Global),
        new(Keys.S | Keys.Control | Keys.Shift, CommandValues.SpeakStatus, KeyScope.Global),
        new(Keys.S | Keys.Control | Keys.Alt, CommandValues.ShowStatusDialog, KeyScope.Global),
        new(Keys.S | Keys.Alt | Keys.Shift, CommandValues.SpeakTxStatus, KeyScope.Global),

        // TX Filter
        new(Keys.OemOpenBrackets | Keys.Control | Keys.Shift, CommandValues.TXFilterLowDown, KeyScope.Radio),
        new(Keys.OemCloseBrackets | Keys.Control | Keys.Shift, CommandValues.TXFilterLowUp, KeyScope.Radio),
        new(Keys.OemOpenBrackets | Keys.Control | Keys.Alt, CommandValues.TXFilterHighDown, KeyScope.Radio),
        new(Keys.OemCloseBrackets | Keys.Control | Keys.Alt, CommandValues.TXFilterHighUp, KeyScope.Radio),
        new(Keys.None, CommandValues.SpeakTXFilter, KeyScope.Radio),

        // Audio Workshop, Tune, ATU, Meters
        new(Keys.W | Keys.Control | Keys.Shift, CommandValues.OpenAudioWorkshop, KeyScope.Global),
        new(Keys.None, CommandValues.StartAudioCheck, KeyScope.Radio), // Command Finder only
        new(Keys.T | Keys.Control | Keys.Shift, CommandValues.TuneToggle, KeyScope.Radio),
        new(Keys.T | Keys.Control, CommandValues.ATUTune, KeyScope.Radio),
        new(Keys.M | Keys.Control, CommandValues.ToggleMeters, KeyScope.Global),

        // 60m channels
        new(Keys.Up | Keys.Alt | Keys.Shift, CommandValues.SixtyMeterChannelUp, KeyScope.Radio),
        new(Keys.Down | Keys.Alt | Keys.Shift, CommandValues.SixtyMeterChannelDown, KeyScope.Radio),

        // ScreenFields expanders
        new(Keys.N | Keys.Control | Keys.Shift, CommandValues.ToggleDspExpander, KeyScope.Radio),
        new(Keys.U | Keys.Control | Keys.Shift, CommandValues.ToggleAudioExpander, KeyScope.Radio),
        new(Keys.R | Keys.Control | Keys.Shift, CommandValues.ToggleReceiverExpander, KeyScope.Radio),
        new(Keys.X | Keys.Control | Keys.Shift, CommandValues.ToggleTransmissionExpander, KeyScope.Radio),
        new(Keys.A | Keys.Control | Keys.Shift, CommandValues.ToggleAntennaExpander, KeyScope.Radio),
        new(Keys.B | Keys.Control | Keys.Shift, CommandValues.ToggleBrailleStatus, KeyScope.Global),

        // SpeakFrequency claimed Ctrl+Shift+F (Sprint 15+21 design intent) but
        // never actually received it — the hard-wired ToggleFreqReadout
        // meta-command consumed Ctrl+Shift+F at window level first (QB Track H
        // shadow sweep, 2026-08-07). ToggleFreqReadout now owns Ctrl+Shift+F
        // in the registry (Radio scope; co-binds with SearchLog in Logging
        // scope, non-conflicting because the modes are mutually exclusive).
        // SpeakFrequency is honestly unbound: the F key on the Frequency
        // field speaks the frequency, and the command stays in the Command
        // Finder / Hotkey Editor for anyone who wants a chord on it.
        new(Keys.None, CommandValues.SpeakFrequency, KeyScope.Radio),
        new(Keys.F4 | Keys.Control, CommandValues.RepeatLastMessage, KeyScope.Global),

        // Former hard-wired meta-commands (QB Track H, 2026-08-07) — same
        // chords they always had, now registry-owned and visible.
        new(Keys.M | Keys.Control | Keys.Shift, CommandValues.ToggleTuningMode, KeyScope.Global),
        new(Keys.L | Keys.Control | Keys.Shift, CommandValues.ToggleLoggingMode, KeyScope.Global),
        new(Keys.F | Keys.Control | Keys.Shift, CommandValues.ToggleFreqReadout, KeyScope.Radio),
        new(Keys.F | Keys.Control | Keys.Alt, CommandValues.SpeakRXFilter, KeyScope.Radio),

        // Verbosity (Sprint 24 Phase 6)
        new(Keys.V | Keys.Control | Keys.Shift, CommandValues.CycleVerbosity, KeyScope.Global),
        new(Keys.None, CommandValues.ToggleMeterTonesGlobal, KeyScope.Global), // leader key T

        // Slice (Sprint 24 Phase 8)
        // Shift+M mute-all / Shift+Comma release-all — multi-slice universal
        // Home actions added 2026-04-24 as test findings from Don's F2+M bug.
        // The older Shift+M = MuteSlice (single-slice) binding was removed;
        // plain M still mutes the active slice from any JJ Flexible Home field,
        // and "Mute Slice" stays available in Command Finder.
        new(Keys.M | Keys.Shift, CommandValues.MuteAllSlices, KeyScope.Radio),
        new(Keys.Oemcomma | Keys.Shift, CommandValues.ReleaseAllExtraSlices, KeyScope.Radio),
    };

    // ────────────────────────────────────────────────────────────────
    //  Dictionaries
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Dictionary to access the key table using a key.
    /// Each key maps to a list of KeyTableEntry entries (one per scope).
    /// </summary>
    public Dictionary<Keys, List<KeyTableEntry>> KeyDictionary = null!;

    /// <summary>
    /// Dictionary to access the key table using a CommandValues.
    /// </summary>
    private Dictionary<CommandValues, KeyTableEntry> _keydefDictionary = null!;

    // ── CW message tracking ──
    private KeyDefType[]? _cwMessageDefs;

    // ────────────────────────────────────────────────────────────────
    //  Dictionary operations
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Add to the key dictionary. Rejects duplicate scope on same key.
    /// </summary>
    public bool AddToKeyDictionary(KeyTableEntry item)
    {
        Keys k = item.KeyDef.Key;
        if (k == Keys.None) return false;

        if (!KeyDictionary.TryGetValue(k, out var entries))
        {
            entries = new List<KeyTableEntry>();
            KeyDictionary.Add(k, entries);
        }
        else
        {
            // Reject duplicate scope on same key.
            foreach (var existing in entries)
            {
                if (existing.Scope == item.Scope) return false;
            }
        }

        entries.Add(item);
        return true;
    }

    /// <summary>
    /// Check if the given scope matches the current ActiveUIMode.
    /// 5-scope matching:
    ///   Classic mode (0) matches: Global, Radio, Classic
    ///   Modern mode (1) matches:  Global, Radio, Modern
    ///   Logging mode (2) matches: Global, Logging
    /// </summary>
    private bool ScopeMatchesMode(KeyScope scope)
    {
        int mode = _context.GetActiveUIMode();
        return scope switch
        {
            KeyScope.Global => true,
            KeyScope.Radio => mode == 0 || mode == 1, // Classic or Modern
            KeyScope.Classic => mode == 0,
            KeyScope.Modern => mode == 1,
            KeyScope.Logging => mode == 2,
            _ => false
        };
    }

    /// <summary>
    /// Look for a defined key, resolved by current scope.
    /// </summary>
    public KeyTableEntry? Lookup(Keys k)
    {
        if (!KeyDictionary.TryGetValue(k, out var entries))
            return null;

        if (entries.Count == 1)
        {
            // Single entry: check scope match.
            return ScopeMatchesMode(entries[0].Scope) ? entries[0] : null;
        }

        // Multiple entries: find exact scope match first, then Global fallback.
        KeyTableEntry? globalFallback = null;
        foreach (var item in entries)
        {
            if (!ScopeMatchesMode(item.Scope)) continue;
            if (item.Scope != KeyScope.Global)
            {
                // Scoped match wins over Global (more specific). In the
                // DEFAULT table this pairing never exists (the validator
                // treats Global + anything as a conflict), so a scoped entry
                // sharing a chord with a Global one means a user keymap put
                // it there — and the Global command is now unreachable in
                // this mode. Trace it so the next Ctrl+Shift+W-style shadow
                // hunt starts from evidence, not archaeology. (2026-08-07:
                // a stale SmeterDBM binding shadowed OpenAudioWorkshop.)
                foreach (var other in entries)
                {
                    if (other.Scope == KeyScope.Global)
                    {
                        _context.Trace($"Lookup: {k} scoped {item.KeyDef.Id} ({item.Scope}) shadows Global {other.KeyDef.Id}");
                        break;
                    }
                }
                return item;
            }
            globalFallback = item;
        }

        return globalFallback;
    }

    /// <summary>
    /// Get all KeyTableEntry entries across all keys (flattened).
    /// </summary>
    public IEnumerable<KeyTableEntry> AllKeyDictionaryEntries()
    {
        var result = new List<KeyTableEntry>();
        foreach (var entries in KeyDictionary.Values)
            result.AddRange(entries);
        return result;
    }

    /// <summary>
    /// Add to the CommandValue dictionary if not already added.
    /// </summary>
    public bool AddToKeydefDictionary(KeyTableEntry item)
    {
        CommandValues k = item.KeyDef.Id;
        if (Lookup(k) != null) return false;
        _keydefDictionary.Add(k, item);
        return true;
    }

    /// <summary>
    /// Look for a defined CommandValue.
    /// </summary>
    public KeyTableEntry? Lookup(CommandValues k)
    {
        _keydefDictionary.TryGetValue(k, out var rv);
        return rv;
    }

    // ────────────────────────────────────────────────────────────────
    //  Setup and construction
    // ────────────────────────────────────────────────────────────────

    private void SetupData()
    {
        KeyDictionary = new Dictionary<Keys, List<KeyTableEntry>>();
        _keydefDictionary = new Dictionary<CommandValues, KeyTableEntry>();
        foreach (var k in KeyTable)
        {
            _keydefDictionary.Add(k.KeyDef.Id, k);
        }
    }

    /// <summary>
    /// Load the key definitions.
    /// </summary>
    public KeyCommands(KeyCommandContext context)
    {
        _context = context;
        _context.Trace("KeyCommands new()");
        BuildKeyTable();
        SetupData();
        InstallGlobalWindowRouting();

        Stream? cfgFile = null;
        try
        {
            cfgFile = File.Open(KeyConfigType_V1.PathName, FileMode.Open);
        }
        catch (Exception)
        {
            // No key file or error — create one with defaults.
            KeyTableToDefault(true);
            ValidateKeyBindings();
            cfgFile?.Dispose();
            return;
        }

        // Read any customizations.
        KeyTableToDefault(false); // Put default keys into key table.
        var xs = new XmlSerializer(typeof(KeyConfigType_V1));
        try
        {
            var kData = (KeyConfigType_V1)xs.Deserialize(cfgFile)!;
            cfgFile.Close();

            // Pre-v5 configs: force reset (one-time migration to per-key tracking).
            if (kData.Version < 5)
            {
                _context.Trace("KeyCommands: config version " + kData.Version + " < 5, resetting to defaults");
                KeyTableToDefault(true);
                return;
            }

            // v5+: Load saved bindings, then smart-merge changed defaults.
            SetValues(kData.Items!, KeyTypes.AllKeys, false);
            SmartMergeDefaults(kData.Items!);
            MergeNewDefaults();
        }
        catch (Exception ex)
        {
            _context.Trace("KeyCommands new:" + ex.Message);
            // See if it's an old format file.
            var oldxs = new XmlSerializer(typeof(KeyConfigData));
            try
            {
                cfgFile.Close();
                cfgFile = File.Open(KeyConfigType_V1.PathName, FileMode.Open);
                var oldkData = (KeyConfigData)oldxs.Deserialize(cfgFile)!;
                cfgFile.Close();
                // oldkData.Items is in CommandValues order.
                var newDefs = new KeyDefType[oldkData.Items!.Length];
                for (int i = 0; i < newDefs.Length; i++)
                    newDefs[i] = new KeyDefType(oldkData.Items[i], (CommandValues)i);
                // This reformats the keydefs file.
                SetValues(newDefs, KeyTypes.AllKeys, true);
                MergeNewDefaults();
            }
            catch (Exception ex2)
            {
                // Unknown format — create a valid keydefs file.
                KeyTableToDefault(true);
                _context.Trace("KeyCommands old format error:" + ex2.Message);
            }
            finally
            {
                cfgFile?.Close();
                cfgFile?.Dispose();
            }
        }
        finally
        {
            cfgFile?.Close();
            cfgFile?.Dispose();
        }

        ValidateKeyBindings();
    }

    /// <summary>
    /// Constructor for testing / default-only scenarios.
    /// </summary>
    public KeyCommands(KeyCommandContext context, bool setDefault)
    {
        _context = context;
        _context.Trace("KeyCommands new(" + setDefault + ")");
        BuildKeyTable();
        SetupData();
        if (setDefault)
            KeyTableToDefault(false);
    }

    // ────────────────────────────────────────────────────────────────
    //  Config persistence
    // ────────────────────────────────────────────────────────────────

    private bool Write()
    {
        _context.Trace("KeyCommands write");
        Stream? cfgFile = null;
        try
        {
            cfgFile = File.Open(KeyConfigType_V1.PathName, FileMode.Create);
        }
        catch (Exception ex)
        {
            _context.Trace("KeyCommands write error:" + ex.Message);
            cfgFile?.Dispose();
            return false;
        }

        bool rv;
        var ktbl = CurrentKeys();
        var kData = new KeyConfigType_V1(ktbl.Length - 1)
        {
            Version = KeyConfigVersion
        };
        for (int i = 0; i < ktbl.Length; i++)
        {
            kData.Items![i] = ktbl[i].KeyDef;
            // Store current default key alongside user's key for per-key smart merge on load.
            var defKey = GetDefaultKey(ktbl[i].KeyDef.Id);
            if (defKey != null)
                kData.Items[i].SavedDefaultKey = defKey.Key;
        }

        var xs = new XmlSerializer(typeof(KeyConfigType_V1));
        try
        {
            xs.Serialize(cfgFile, kData);
            rv = true;
        }
        catch (Exception ex)
        {
            _context.Trace("KeyCommands write serialize error:" + ex.Message);
            rv = false;
        }
        finally
        {
            cfgFile.Close();
            cfgFile.Dispose();
        }

        return rv;
    }

    // ────────────────────────────────────────────────────────────────
    //  Key table defaults and merging
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Set/reset the key table to the default values.
    /// </summary>
    public void KeyTableToDefault(bool save)
    {
        _context.Trace("keyTableToDefault(" + save + ")");
        SetValues(_defaultKeys, KeyTypes.AllKeys, save);
    }

    /// <summary>
    /// Set key values for the commands. If CW messages are present,
    /// UpdateCWText() must be called after this.
    /// </summary>
    public void SetValues(KeyDefType[] defs, KeyTypes mask, bool wrt)
    {
        _context.Trace("SetValues:" + mask + " " + wrt);
        if (mask == KeyTypes.AllKeys)
        {
            KeyDictionary.Clear();
        }
        else
        {
            // Only clear the desired values to be replaced.
            var delCol = new List<KeyTableEntry>();
            foreach (var entries in KeyDictionary.Values)
            {
                foreach (var item in entries)
                {
                    if ((item.KeyType & mask) == item.KeyType)
                        delCol.Add(item);
                }
            }

            foreach (var item in delCol)
            {
                if (KeyDictionary.TryGetValue(item.KeyDef.Key, out var entries))
                {
                    entries.Remove(item);
                    if (entries.Count == 0) KeyDictionary.Remove(item.KeyDef.Key);
                }
            }
        }

        // Now add in the keys.
        bool restoredDefaults = false;
        foreach (var def in defs)
        {
            var t = Lookup(def.Id);
            if (t != null)
            {
                // Migrate legacy KeyDefs.xml: if a key was saved without scope info it
                // deserializes as Global (the field default). When the command's built-in
                // default scope is NOT Global, honour the built-in scope.
                var effectiveScope = def.Scope;
                var effectiveKey = def.Key;
                var builtIn = GetDefaultKey(def.Id);

                if (effectiveScope == KeyScope.Global)
                {
                    if (builtIn != null && builtIn.Scope != KeyScope.Global)
                        effectiveScope = builtIn.Scope;
                }

                // BUG-014 contamination guard: if the file has Keys.None but the
                // built-in default has a real binding, preserve the default.
                if (effectiveKey == Keys.None && builtIn != null && builtIn.Key != Keys.None)
                {
                    effectiveKey = builtIn.Key;
                    restoredDefaults = true;
                    _context.Trace("SetValues:restored default key for " + def.Id + " = " + effectiveKey);
                }

                t.KeyDef.Key = effectiveKey;
                t.KeyDef.Scope = effectiveScope;
                t.Scope = effectiveScope;
                AddToKeyDictionary(t);
            }
            // else: probably an old format KeyDefs file with a deprecated command.
        }

        if (wrt || restoredDefaults)
            Write();
    }

    /// <summary>
    /// Get the default KeyDefType for a given command.
    /// Used by the Reset button in DefineCommands.
    /// </summary>
    public KeyDefType? GetDefaultKey(CommandValues cmdId)
    {
        foreach (var def in _defaultKeys)
        {
            if (def.Id == cmdId) return def;
        }
        return null;
    }

    /// <summary>
    /// Merge new defaults: find any command from _keydefDictionary that
    /// isn't yet in KeyDictionary and add it (with its default key).
    /// </summary>
    private void MergeNewDefaults()
    {
        // Build checkDict from KeyDictionary.
        var checkDict = new Dictionary<CommandValues, KeyTableEntry>();
        foreach (var entries in KeyDictionary.Values)
        {
            foreach (var item in entries)
            {
                if (!checkDict.ContainsKey(item.KeyDef.Id))
                    checkDict.Add(item.KeyDef.Id, item);
            }
        }

        bool needWrite = false;
        foreach (var item in _keydefDictionary.Values)
        {
            if (!checkDict.ContainsKey(item.KeyDef.Id))
            {
                AddToKeyDictionary(item);
                needWrite = true;
                _context.Trace("KeyCommands:merged new item:" + item.KeyDef.Id + " " + item.KeyDef.Key);
            }
        }

        if (needWrite)
            Write();
    }

    /// <summary>
    /// Smart-merge changed defaults: for each command in the saved config,
    /// compare the savedDefaultKey to the current default. If the default changed
    /// and the user never customized (their key == old default), apply the new default.
    /// If they customized (their key != old default), keep their binding.
    /// </summary>
    private void SmartMergeDefaults(KeyDefType[] savedItems)
    {
        if (savedItems == null) return;
        bool needWrite = false;

        foreach (var saved in savedItems)
        {
            var currentDefault = GetDefaultKey(saved.Id);

            // Default-was-removed case: a command that used to have a default
            // key binding no longer does. If another command now claims this
            // key as its default — i.e., the key was reassigned in the
            // defaults table — clear the stale user binding so the new owner
            // can take over via MergeNewDefaults. Preserve the binding only
            // if the user explicitly customised it to a non-default key
            // (detected via SavedDefaultKey tracking when available).
            //
            // The "key is taken over" detection is the primary signal —
            // SavedDefaultKey-match is only used as a secondary check to
            // preserve verified customisations. XMLs written by older
            // versions (or by intermediate builds that didn't track
            // SavedDefaultKey cleanly) store SavedDefaultKey as Keys.None,
            // which made the original `saved.Key == saved.SavedDefaultKey`
            // strict check miss these cases. This revised logic treats an
            // untracked SavedDefaultKey as "assume user was on old default"
            // because a coincidental customisation that exactly matches a
            // new command's default is much less likely than a stale-binding
            // migration scenario.
            //
            // Concrete case driving this: Sprint 28 moved Shift+M from
            // MuteSlice (single-slice mute) to MuteAllSlices (multi-slice
            // mute). Users who had never customised Shift+M should get the
            // new behaviour automatically on first launch after the upgrade.
            //
            // 2026-08-07 extension (Tracks G and H, same gate independently):
            // the takeover check also runs when the command still EXISTS but
            // its current default is unbound (Keys.None) and its saved-default
            // history is untracked. Track G's case — SmeterDBM (default None
            // since Jim's original) sat on Ctrl+Shift+W and silently shadowed
            // the Global OpenAudioWorkshop chord via Lookup's scoped-wins
            // rule, because the old `currentDefault == null` gate never fired
            // for a still-present command. Track H's case — MemoryScan's
            // Ctrl+Shift+M and SpeakFrequency's Ctrl+Shift+F moved to the
            // promoted meta-commands (ToggleTuningMode / ToggleFreqReadout);
            // untracked files would compare None == None, keep the stale
            // binding, and shadow the new owner's default. Commands whose
            // default CHANGED from a real key to None keep the normal
            // default-changed path below (SavedDefaultKey is non-None there,
            // with the same outcome: stale never-customized bindings clear,
            // explicit customizations stay).
            if (currentDefault == null ||
                (currentDefault.Key == Keys.None && saved.SavedDefaultKey == Keys.None))
            {
                if (saved.Key != Keys.None)
                {
                    bool keyTakenByAnotherDefault = false;
                    foreach (var otherDef in _defaultKeys)
                    {
                        if (otherDef.Id != saved.Id && otherDef.Key == saved.Key)
                        {
                            keyTakenByAnotherDefault = true;
                            break;
                        }
                    }

                    // If SavedDefaultKey IS tracked (non-None) and doesn't
                    // match the user's current key, the user explicitly
                    // customised — preserve their binding. Otherwise we
                    // treat them as being on the old default and migrate.
                    bool userExplicitlyCustomised =
                        saved.SavedDefaultKey != Keys.None && saved.Key != saved.SavedDefaultKey;

                    if (keyTakenByAnotherDefault && !userExplicitlyCustomised)
                    {
                        _context.Trace($"KeyCommands:SmartMerge: {saved.Id} default removed and key {saved.Key} taken over by another command's default, clearing user's stale binding (SavedDefaultKey={saved.SavedDefaultKey})");
                        if (KeyDictionary.TryGetValue(saved.Key, out var staleEntries))
                        {
                            staleEntries.RemoveAll(e => e.KeyDef.Id == saved.Id);
                            if (staleEntries.Count == 0) KeyDictionary.Remove(saved.Key);
                        }
                        var staleKt = Lookup(saved.Id);
                        if (staleKt != null) staleKt.KeyDef.Key = Keys.None;
                        needWrite = true;
                    }
                }
                continue;
            }

            // If saved default matches current default, nothing changed.
            if (saved.SavedDefaultKey == currentDefault.Key) continue;

            // Default changed. Did the user customize this key?
            if (saved.Key == saved.SavedDefaultKey)
            {
                // User never customized — they had the old default. Apply new default.
                _context.Trace($"KeyCommands:SmartMerge: {saved.Id} default changed {saved.SavedDefaultKey} -> {currentDefault.Key}, updating");

                // Remove old key binding from KeyDictionary.
                if (saved.Key != Keys.None && KeyDictionary.TryGetValue(saved.Key, out var oldEntries))
                {
                    oldEntries.RemoveAll(e => e.KeyDef.Id == saved.Id);
                    if (oldEntries.Count == 0) KeyDictionary.Remove(saved.Key);
                }

                // Update the KeyTableEntry and re-add to dictionary.
                var kt = Lookup(saved.Id);
                if (kt != null)
                {
                    kt.KeyDef.Key = currentDefault.Key;
                    kt.KeyDef.Scope = currentDefault.Scope;
                    if (currentDefault.Key != Keys.None)
                        AddToKeyDictionary(kt);
                }

                needWrite = true;
            }
            else
            {
                // User customized this key — keep their binding.
                _context.Trace($"KeyCommands:SmartMerge: {saved.Id} default changed but user has custom key {saved.Key}, keeping");
            }
        }

        if (needWrite)
            Write();
    }

    // ────────────────────────────────────────────────────────────────
    //  Keys surface editor API — QB Track H (2026-08-07)
    //  Live rebinding against the same KeyDictionary the dispatcher reads,
    //  persisted through the existing Write() path (KeyDefs.xml). The Keys
    //  dialog is the only intended caller.
    // ────────────────────────────────────────────────────────────────

    /// <summary>A binding that would collide with a proposed key.</summary>
    public sealed class BindingConflict
    {
        public CommandValues Id { get; init; }
        public string Description { get; init; } = "";
        public KeyScope Scope { get; init; }
        /// <summary>False for CW message keys — those are managed by the CW
        /// Messages editor and can't be stolen from here.</summary>
        public bool CanSteal { get; init; }
    }

    /// <summary>
    /// True when two scopes can be active at the same time, i.e. a shared
    /// key would be ambiguous. Mirrors ValidateKeyBindings and the runtime
    /// ScopeMatchesMode logic: Global collides with everything; Radio
    /// collides with Classic and Modern (they run simultaneously); Radio or
    /// Classic or Modern never collide with Logging; Classic never collides
    /// with Modern.
    /// </summary>
    public static bool ScopesCollide(KeyScope a, KeyScope b)
    {
        if (a == b) return true;
        if (a == KeyScope.Global || b == KeyScope.Global) return true;
        bool aRadioish = a is KeyScope.Radio or KeyScope.Classic or KeyScope.Modern;
        bool bRadioish = b is KeyScope.Radio or KeyScope.Classic or KeyScope.Modern;
        if (a == KeyScope.Logging || b == KeyScope.Logging) return false;
        // Both radio-ish here. Radio overlaps Classic and Modern;
        // Classic and Modern are mutually exclusive.
        if (aRadioish && bRadioish)
            return a == KeyScope.Radio || b == KeyScope.Radio;
        return false;
    }

    /// <summary>
    /// Find every existing binding that collides with putting
    /// <paramref name="key"/> on <paramref name="forCommand"/>.
    /// </summary>
    public List<BindingConflict> FindBindingConflicts(Keys key, CommandValues forCommand)
    {
        var result = new List<BindingConflict>();
        if (key == Keys.None) return result;
        var target = Lookup(forCommand);
        var targetScope = target?.Scope ?? KeyScope.Global;
        if (!KeyDictionary.TryGetValue(key, out var entries)) return result;
        foreach (var e in entries)
        {
            if (e.KeyDef.Id == forCommand) continue;
            if (!ScopesCollide(targetScope, e.Scope)) continue;
            result.Add(new BindingConflict
            {
                Id = e.KeyDef.Id,
                Description = e.KeyType == KeyTypes.CWText
                    ? "CW Message: " + e.HelpText
                    : e.HelpText,
                Scope = e.Scope,
                CanSteal = e.KeyType != KeyTypes.CWText,
            });
        }
        return result;
    }

    /// <summary>
    /// Apply a new key to a command — the live-rebind core. Removes the
    /// command's old binding, optionally unbinds colliding commands
    /// (steal), installs the new key into the dispatch dictionary, and
    /// persists. Keys.None unbinds. Returns false if a non-stealable
    /// conflict remains or the command is unknown; no changes are made in
    /// that case.
    /// </summary>
    public bool ApplyBinding(CommandValues id, Keys newKey, bool stealConflicts)
    {
        var entry = Lookup(id);
        if (entry == null) return false;

        var conflicts = FindBindingConflicts(newKey, id);
        if (conflicts.Count > 0 && (!stealConflicts || conflicts.Any(c => !c.CanSteal)))
            return false;

        // Steal: unbind each colliding command.
        foreach (var c in conflicts)
        {
            var victim = Lookup(c.Id);
            if (victim == null) continue;
            RemoveFromKeyDictionary(victim);
            victim.KeyDef.Key = Keys.None;
        }

        // Move this command onto the new key.
        RemoveFromKeyDictionary(entry);
        entry.KeyDef.Key = newKey;
        if (newKey != Keys.None)
            AddToKeyDictionary(entry);

        Write();
        _context.Trace($"KeyCommands:ApplyBinding {id} -> {newKey} (steal={stealConflicts})");
        return true;
    }

    /// <summary>
    /// Reset one command to its built-in default key. Same conflict
    /// semantics as ApplyBinding. Commands with no default entry unbind.
    /// </summary>
    public bool ResetBindingToDefault(CommandValues id, bool stealConflicts, out Keys defaultKey)
    {
        defaultKey = GetDefaultKey(id)?.Key ?? Keys.None;
        return ApplyBinding(id, defaultKey, stealConflicts);
    }

    /// <summary>
    /// Reset every binding to the defaults table and persist. CW message
    /// keys are re-installed afterwards (SetValues clears them).
    /// </summary>
    public void ResetAllBindingsToDefault()
    {
        KeyTableToDefault(true);
        UpdateCWText();
        _context.Trace("KeyCommands:ResetAllBindingsToDefault");
    }

    /// <summary>Remove a command's current key from the dispatch dictionary.</summary>
    private void RemoveFromKeyDictionary(KeyTableEntry entry)
    {
        var oldKey = entry.KeyDef.Key;
        if (oldKey == Keys.None) return;
        if (KeyDictionary.TryGetValue(oldKey, out var list))
        {
            list.RemoveAll(e => e.KeyDef.Id == entry.KeyDef.Id);
            if (list.Count == 0) KeyDictionary.Remove(oldKey);
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Static helpers
    // ────────────────────────────────────────────────────────────────

    private static readonly string[] NameTable = Enum.GetNames(typeof(CommandValues));
    private static readonly int[] IdTable = (int[])Enum.GetValues(typeof(CommandValues));

    /// <summary>
    /// Get the command id from the type name.
    /// </summary>
    public static CommandValues GetKeyFromTypename(string name)
    {
        for (int i = 0; i < NameTable.Length; i++)
        {
            if (NameTable[i] == name)
                return (CommandValues)IdTable[i];
        }
        return CommandValues.NotACommand;
    }

    // ────────────────────────────────────────────────────────────────
    //  Main dispatch
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Perform the command for this key.
    /// </summary>
    /// <returns>true if we handled the command</returns>
    public bool DoCommand(Keys k)
    {
        _context.Trace("DoCommand:" + ((int)k).ToString("x8"));
        bool rv = false;

        // Just return if this is just the shift, alt, or control key.
        int theKey = (int)(k & Keys.KeyCode);
        if (theKey == (int)Keys.Menu || theKey == (int)Keys.ControlKey ||
            theKey == (int)Keys.ShiftKey || theKey == 0)
            return rv;

        // === VOLUME MODE DISPATCH (Ctrl+J, V sub-mode) ===
        // Checked before the one-shot leader dispatch: unlike the leader, this
        // mode stays active across keys until Escape ends it.
        if (_volumeModeActive)
        {
            return DoVolumeModeKey(k);
        }

        // === LEADER KEY DISPATCH ===
        if (_leaderKeyActive)
        {
            _leaderKeyActive = false;
            if (k == Keys.Escape)
            {
                EarconPlayer.LeaderCancelTone();
                Radios.ScreenReaderOutput.Speak("Cancelled", Radios.VerbosityLevel.Terse, true);
                return true;
            }
            return DoLeaderCommand(k);
        }

        // Check for leader key trigger (Ctrl+J).
        if (k == (Keys.J | Keys.Control))
        {
            _leaderKeyActive = true;
            EarconPlayer.LeaderEnterTone();
            Radios.ScreenReaderOutput.Speak("JJ", Radios.VerbosityLevel.Terse, true);
            return true;
        }

        // Look in KeyDictionary.
        var kt = Lookup(k);
        if (kt != null)
        {
            // Radio/Classic/Modern-scope commands need a connected radio.
            // Without one, announce "no radio connected" instead of letting
            // the handler go silent (or worse, open a dialog with no radio,
            // which was the C.4d test failure 2026-04-28). Global scope works
            // without a radio by definition; Logging scope has its own guard.
            // Mirrors ApplicationEvents.vb ExecuteCommandCallback guard for
            // the Command Finder / menu path. This DoCommand path covers
            // direct keystrokes — the actual user-facing dispatch.
            //
            // RunsWithoutRadio opt-out: a small locked list of commands
            // (SetFreq, ShowMemory) work meaningfully with no radio — easter
            // eggs, calibration-ref entry, just-typing-a-frequency-to-remember.
            // For those, fall through to the handler; it owns the no-radio
            // behavior including any necessary speech.
            if (_context.GetRigControl() == null &&
                !kt.RunsWithoutRadio &&
                (kt.Scope == Radios.KeyScope.Radio ||
                 kt.Scope == Radios.KeyScope.Classic ||
                 kt.Scope == Radios.KeyScope.Modern))
            {
                Radios.ScreenReaderOutput.SpeakNoRadioConnected(kt.ShortActionLabel);
                return true; // consumed — don't leak to other handlers
            }

            CommandId = kt.KeyDef.Id;
            _context.Trace("DoCommand:" + CommandId);
            // Mark handled BEFORE calling the routine — even if it throws,
            // we still consumed the key so it doesn't leak to the MenuStrip.
            rv = true;
            try
            {
                kt.Handler?.Invoke();
            }
            catch (Exception ex)
            {
                var rig = _context.GetRigControl();
                if (rig == null || !_context.GetPower())
                    _context.Trace("DoCommand:no rig setup");
                else
                {
                    _context.Trace("DoCommand:" + ex.Message);
                }
            }
        }
        else
        {
            // If the key looks like a CW message hotkey (Ctrl+1-7) but no
            // CW messages are configured, give spoken feedback.
            var keyCode = k & Keys.KeyCode;
            var mods = k & Keys.Modifiers;
            var cwText = _context.GetCWText();
            if (mods == Keys.Control && keyCode >= Keys.D1 && keyCode <= Keys.D7 && cwText.Length == 0)
            {
                Radios.ScreenReaderOutput.Speak("No CW messages configured", Radios.VerbosityLevel.Critical, true);
                rv = true;
            }
            else
            {
                _context.Trace("DoCommand:key not found:" + k);
            }
        }

        return rv;
    }

    // ────────────────────────────────────────────────────────────────
    //  Global routing for WPF dialog windows — Audio Arc Keys Track
    //  (2026-08-11).
    //
    //  THE DEFECT: KeyScope.Global was only global inside the main window.
    //  The main content is a UserControl in an ElementHost, so every key it
    //  sees flows through ShellForm.ProcessCmdKey → DoCommand before WPF —
    //  Global commands work anywhere there. But dialogs are separate WPF
    //  Windows with their own input path: no ProcessCmdKey, no DoCommand,
    //  nothing. Every Global chord died inside every dialog, and worse,
    //  WPF access-key matching could STEAL a dead chord — Alt+Shift+S
    //  (Speak Transmit Status) fired Save Audio Preset inside the Audio
    //  Workshop via the Save button's S mnemonic. Field-confirmed by Noel
    //  2026-08-11, in the one dialog where he most needed the TX status.
    //
    //  THE FIX: class handlers on typeof(Window), registered once, so every
    //  WPF dialog — present and future — routes through the same registry:
    //
    //  - The BUBBLING KeyDown handler dispatches Global-scope registry
    //    bindings, the Ctrl+J leader trigger, and (while armed) leader /
    //    volume-mode follow-on keys. Bubbling is the deliberate choice:
    //    it runs AFTER every dialog-local Preview and control handler
    //    (dialog-owned keys like the Workshop's two-stage Escape, Ctrl+S
    //    Save, and plain typing all win), but BEFORE AccessKeyManager's
    //    mnemonic matching in PostProcessInput — marking the event handled
    //    is what stops the mnemonic steal.
    //  - The PREVIEW handler is inert until a leader or volume mode is
    //    ARMED; then it feeds every key to DoCommand ahead of the dialog —
    //    main-window parity (ProcessCmdKey does the same there), so
    //    volume-mode arrows are not eaten by a focused TextBox and Escape
    //    cancels the mode rather than closing the dialog. PTT safety
    //    carve-out: while the radio is transmitting, Escape belongs to
    //    unkey (Track A's rule stands) — the armed mode is dropped
    //    silently and the key travels on.
    //
    //  Scope discipline: ONLY Global-scope entries dispatch from dialogs.
    //  Radio/Classic/Modern/Logging chords stay inert so dialog-local keys
    //  (e.g. Ctrl+S in the Workshop) are never stolen. CW message keys are
    //  excluded too: their Scope is Global only by constructor default, the
    //  inventory documents them as Radio, and firing CW on the air from a
    //  dialog keystroke would be a surprise, not a fix.
    //
    //  Residual gap, documented honestly: legacy WinForms-hosted surfaces
    //  (e.g. the Flex filter forms, WebView2 auth) are not WPF Windows and
    //  are NOT covered by these handlers.
    // ────────────────────────────────────────────────────────────────

    private static KeyCommands? _globalRoutingOwner;
    private static bool _globalRoutingInstalled;

    /// <summary>
    /// Register the class handlers exactly once, owned by the first (main)
    /// KeyCommands instance. LogEntry's myKeyCommands subclass uses the
    /// two-arg constructor and never installs.
    /// </summary>
    private void InstallGlobalWindowRouting()
    {
        if (_globalRoutingInstalled) return;
        _globalRoutingInstalled = true;
        _globalRoutingOwner = this;
        System.Windows.EventManager.RegisterClassHandler(
            typeof(System.Windows.Window),
            System.Windows.Input.Keyboard.PreviewKeyDownEvent,
            new System.Windows.Input.KeyEventHandler(AnyWindowPreviewKeyDown));
        System.Windows.EventManager.RegisterClassHandler(
            typeof(System.Windows.Window),
            System.Windows.Input.Keyboard.KeyDownEvent,
            new System.Windows.Input.KeyEventHandler(AnyWindowKeyDown));
        _context.Trace("KeyCommands: global window routing installed");
    }

    /// <summary>
    /// Preview (tunnel) phase: active ONLY while a leader or volume mode is
    /// armed. Then every key routes through DoCommand ahead of the dialog's
    /// own handling — exact parity with the main window, where
    /// ProcessCmdKey feeds an armed mode before any control sees the key.
    /// Without this, a focused TextBox or list would eat volume mode's
    /// arrows at its own KeyDown, and a leader follow-on letter could both
    /// dispatch AND type into the field. The modes stay polite on their
    /// own: DoVolumeModeKey passes Alt chords and F1 through untouched, and
    /// bare modifier presses fall out of DoCommand unhandled.
    /// </summary>
    private static void AnyWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var kc = _globalRoutingOwner;
        if (kc == null || e.Handled) return;
        if (!kc._leaderKeyActive && !kc._volumeModeActive) return;
        var raw = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;

        // PTT safety wins over mode cancel: while transmitting, let Escape
        // travel to whoever unkeys (Audio Check two-stage Escape, transmit
        // lock). Drop the armed mode so it cannot fire later unexpectedly.
        if (raw == System.Windows.Input.Key.Escape)
        {
            var rig = kc._context.GetRigControl();
            if (rig != null && rig.Transmit)
            {
                kc._leaderKeyActive = false;
                if (kc._volumeModeActive) kc.ExitVolumeMode(speak: false);
                return;
            }
        }

        if (kc.DoCommand(WpfKeyConverter.ToWinFormsKeys(e)))
            e.Handled = true;
    }

    /// <summary>
    /// Bubble phase: dispatch Global registry commands and the leader from
    /// any WPF window. Runs only when nothing dialog-local handled the key.
    /// </summary>
    private static void AnyWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var kc = _globalRoutingOwner;
        if (kc == null || e.Handled) return;
        var k = WpfKeyConverter.ToWinFormsKeys(e);
        if (k == Keys.None) return;
        if (kc.DispatchFromDialogWindow(k))
            e.Handled = true;
    }

    /// <summary>
    /// The dialog-side dispatch core. Consumes: leader/volume-mode keys
    /// while armed, the Ctrl+J trigger, and chords bound to a Global-scope
    /// registry command (CW message keys excluded — see region comment).
    /// Returns false for everything else so the key stays with the dialog.
    /// </summary>
    internal bool DispatchFromDialogWindow(Keys k)
    {
        // Ignore bare modifier presses (same filter as DoCommand).
        int code = (int)(k & Keys.KeyCode);
        if (code == (int)Keys.Menu || code == (int)Keys.ControlKey ||
            code == (int)Keys.ShiftKey || code == 0)
            return false;

        // Armed modes own their follow-on keys wherever focus lives —
        // this is what makes the Ctrl+J mic check usable from inside the
        // Audio Workshop, precisely where an operator rides mic gain.
        if (_volumeModeActive || _leaderKeyActive)
            return DoCommand(k);

        // The leader trigger itself.
        if (k == (Keys.J | Keys.Control))
            return DoCommand(k);

        // Global-scope registry bindings only.
        if (!KeyDictionary.TryGetValue(k, out var entries)) return false;
        KeyTableEntry? global = null;
        foreach (var item in entries)
        {
            if (item.Scope == KeyScope.Global && item.KeyType != KeyTypes.CWText)
            {
                global = item;
                break;
            }
        }
        if (global == null) return false;

        CommandId = global.KeyDef.Id;
        _context.Trace("DispatchFromDialogWindow:" + CommandId);
        try
        {
            global.Handler?.Invoke();
        }
        catch (Exception ex)
        {
            _context.Trace("DispatchFromDialogWindow:" + ex.Message);
        }
        return true; // consumed — suppresses mnemonic matching too
    }

    // ────────────────────────────────────────────────────────────────
    //  Current keys and help text
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Get the current key table entries (commands, log fields, and CW messages).
    /// </summary>
    public KeyTableEntry[] CurrentKeys()
    {
        var rv = new List<KeyTableEntry>();
        // KeyTable contains only command and logging keys.
        foreach (var item in KeyTable)
            rv.Add(new KeyTableEntry(item));
        // CW messages.
        if (_cwMessageDefs != null)
        {
            foreach (var def in _cwMessageDefs)
            {
                if (_keydefDictionary.TryGetValue(def.Id, out var item))
                    rv.Add(item);
            }
        }
        return rv.ToArray();
    }

    /// <summary>
    /// Get the keys, key names and actions for commands in KeyTable plus CW macros.
    /// </summary>
    public void HelpText(out KeyDefType[]? keyCommandValues, out KeyDefType[]? keyTextValues,
                           out string[] keyNames, out string[] actions)
    {
        int len = AllKeyDictionaryEntries().Count();
        var commandCol = new List<KeyDefType>();
        var textCol = new List<KeyDefType>();
        keyNames = new string[len];
        actions = new string[len];

        int i = 0;
        // The command and log keys come first.
        foreach (var entries in KeyDictionary.Values)
        {
            foreach (var item in entries)
            {
                if (item.KeyType == KeyTypes.Command || item.KeyType == KeyTypes.Log)
                {
                    commandCol.Add(new KeyDefType(item.KeyDef.Key, item.KeyDef.Id));
                    keyNames[i] = _context.FormatKey(item.KeyDef.Key);
                    actions[i] = item.HelpText;
                    i++;
                }
            }
        }

        // CW text entries.
        var cwText = _context.GetCWText();
        foreach (var entries in KeyDictionary.Values)
        {
            foreach (var item in entries)
            {
                if (item.KeyType != KeyTypes.Command && item.KeyType != KeyTypes.Log)
                {
                    int j = i - commandCol.Count;
                    if (j >= 0 && j < cwText.Length && _cwMessageDefs != null)
                    {
                        var m = cwText[j];
                        textCol.Add(new KeyDefType(m.Key, _cwMessageDefs[j].Id));
                        keyNames[i] = _context.FormatKey(m.Key);
                        actions[i] = "CW Message: " + m.Label;
                        i++;
                    }
                }
            }
        }

        keyCommandValues = commandCol.Count > 0 ? commandCol.ToArray() : null;
        keyTextValues = textCol.Count > 0 ? textCol.ToArray() : null;
    }

    /// <summary>
    /// Get the key names and actions (simplified overload).
    /// </summary>
    public void HelpText(out string[] keyNames, out string[] actions)
    {
        HelpText(out _, out _, out keyNames, out actions);
    }

    // ────────────────────────────────────────────────────────────────
    //  CW message handling
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Update the dictionaries with new CW text messages.
    /// </summary>
    public void UpdateCWText()
    {
        var cwText = _context.GetCWText();
        if (_cwMessageDefs != null)
        {
            // Remove old dictionary entries.
            foreach (var def in _cwMessageDefs)
            {
                _keydefDictionary.Remove(def.Id);
                if (KeyDictionary.TryGetValue(def.Key, out var entries))
                {
                    entries.RemoveAll(e => e.KeyType == KeyTypes.CWText);
                    if (entries.Count == 0) KeyDictionary.Remove(def.Key);
                }
            }
        }

        // Remake the CWMessageDefs array, and update the dictionaries.
        _cwMessageDefs = new KeyDefType[cwText.Length];
        for (int i = 0; i < cwText.Length; i++)
        {
            _cwMessageDefs[i] = new KeyDefType(cwText[i].Key, (CommandValues)(KeyCommandConstants.FirstMessageCommandValue + i));
            var item = new KeyTableEntry(_cwMessageDefs[i].Id, KeyTypes.CWText, SendCWMessage, cwText[i].Label, FunctionGroups.CwMessage);
            item.KeyDef.Key = _cwMessageDefs[i].Key;
            AddToKeydefDictionary(item);
            AddToKeyDictionary(item);
        }
    }

    /// <summary>
    /// Update the dictionaries with new CW text (overload accepting new keys from DefineKeys).
    /// </summary>
    public void UpdateCWText(KeyDefType[] items)
    {
        var cwText = _context.GetCWText();
        for (int i = 0; i < items.Length; i++)
            cwText[i].Key = items[i].Key;
        UpdateCWText();
    }

    protected void SendCWMessage()
    {
        int id = (int)CommandId - KeyCommandConstants.FirstMessageCommandValue;
        var cwText = _context.GetCWText();
        if (id < 0 || id >= cwText.Length)
        {
            Radios.ScreenReaderOutput.Speak("No CW message at this position", Radios.VerbosityLevel.Critical, true);
        }
        else
        {
            string label = cwText[id].Label;
            string msg = cwText[id].Message;
            if (msg.Length > 0 && msg[^1] != ' ')
                msg += " ";
            _context.SendCW(msg);
            _context.WriteTextX(1, msg, 0, false); // WindowIDs.SendDataOut = 1
            if (!string.IsNullOrEmpty(label))
                Radios.ScreenReaderOutput.Speak("Sending " + label, Radios.VerbosityLevel.Terse, false);
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Leader Key System
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Dispatch the second key after Ctrl+J leader key activation.
    /// Always consumes the key — invalid keys get an error earcon.
    /// </summary>
    private bool DoLeaderCommand(Keys k)
    {
        var rig = _context.GetRigControl();

        switch (k)
        {
            // DSP Toggles
            case Keys.N:
                if (rig == null) LeaderNoRadio();
                else ToggleLeaderDSP("Legacy Noise Reduction",
                    () => rig.NoiseReduction, v => rig.NoiseReduction = v);
                break;
            case Keys.B:
                if (rig == null) LeaderNoRadio();
                else ToggleLeaderDSP("Noise Blanker",
                    () => rig.NoiseBlanker, v => rig.NoiseBlanker = v);
                break;
            case Keys.W:
                if (rig == null) LeaderNoRadio();
                else ToggleLeaderDSP("Wideband NB",
                    () => rig.WidebandNoiseBlanker, v => rig.WidebandNoiseBlanker = v);
                break;
            case Keys.R:
                if (rig == null)
                    LeaderNoRadio();
                else if (!rig.NeuralNRHardwareSupported)
                {
                    EarconPlayer.LeaderInvalidTone();
                    Radios.ScreenReaderOutput.Speak("Neural NR not available on this radio", Radios.VerbosityLevel.Critical);
                }
                else
                    ToggleLeaderDSP("Neural NR",
                        () => rig.NeuralNoiseReduction, v => rig.NeuralNoiseReduction = v);
                break;
            case Keys.S:
                if (rig == null)
                    LeaderNoRadio();
                else if (!rig.NeuralNRHardwareSupported)
                {
                    EarconPlayer.LeaderInvalidTone();
                    Radios.ScreenReaderOutput.Speak("Spectral NR not available on this radio", Radios.VerbosityLevel.Critical);
                }
                else
                    ToggleLeaderDSP("Spectral NR",
                        () => rig.SpectralNoiseReduction, v => rig.SpectralNoiseReduction = v);
                break;
            case Keys.N | Keys.Shift:
                if (rig == null)
                    LeaderNoRadio();
                else if (!rig.NeuralNRHardwareSupported)
                {
                    EarconPlayer.LeaderInvalidTone();
                    Radios.ScreenReaderOutput.Speak("NR Filter not available on this radio", Radios.VerbosityLevel.Critical);
                }
                else
                    ToggleLeaderDSP("NR Filter",
                        () => rig.NoiseReductionFilter, v => rig.NoiseReductionFilter = v);
                break;

            // PC-side NR (works on ALL radios — processing runs on the PC)
            case Keys.R | Keys.Shift:
                {
                    var pipeline = _context.GetMainWindow()?.FieldsPanel.AudioPipeline;
                    if (rig == null)
                        LeaderNoRadio();
                    else if (pipeline == null)
                    {
                        EarconPlayer.LeaderInvalidTone();
                        Radios.ScreenReaderOutput.Speak("PC audio pipeline not ready", Radios.VerbosityLevel.Critical);
                    }
                    else
                    {
                        pipeline.RnnEnabled = !pipeline.RnnEnabled;
                        if (pipeline.RnnEnabled) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
                        Radios.ScreenReaderOutput.Speak($"PC Neural NR {(pipeline.RnnEnabled ? "on" : "off")}", Radios.VerbosityLevel.Terse);
                    }
                }
                break;
            case Keys.S | Keys.Shift:
                {
                    var pipeline = _context.GetMainWindow()?.FieldsPanel.AudioPipeline;
                    if (rig == null)
                        LeaderNoRadio();
                    else if (pipeline == null)
                    {
                        EarconPlayer.LeaderInvalidTone();
                        Radios.ScreenReaderOutput.Speak("PC audio pipeline not ready", Radios.VerbosityLevel.Critical);
                    }
                    else
                    {
                        pipeline.SpectralEnabled = !pipeline.SpectralEnabled;
                        if (pipeline.SpectralEnabled) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
                        string msg = pipeline.SpectralEnabled
                            ? (pipeline.HasNoiseProfile ? "PC Spectral NR on" : "PC Spectral NR on, no noise profile loaded")
                            : "PC Spectral NR off";
                        Radios.ScreenReaderOutput.Speak(msg, Radios.VerbosityLevel.Terse);
                    }
                }
                break;

            case Keys.A:
                if (rig == null) LeaderNoRadio();
                else ToggleLeaderDSP("Auto Notch",
                    () => rig.AutoNotchFFT, v => rig.AutoNotchFFT = v);
                break;
            case Keys.P:
                if (rig == null)
                    LeaderNoRadio();
                else
                {
                    var mode = rig.Mode;
                    if (mode != null && !mode.StartsWith("CW", StringComparison.OrdinalIgnoreCase))
                    {
                        EarconPlayer.LeaderInvalidTone();
                        Radios.ScreenReaderOutput.Speak("Audio Peak Filter is CW only", Radios.VerbosityLevel.Critical);
                    }
                    else
                        ToggleLeaderDSP("Audio Peak Filter",
                            () => rig.APF, v => rig.APF = v);
                }
                break;

            // ── Audio Arc Track A (2026-08-11): "adjust how I sound and what
            // I hear" joins the leader. V enters the persistent volume mode;
            // C and Shift+P are the TX-processing toggles (their LEVELS live
            // inside volume mode as targets C and S).
            case Keys.V:
                if (rig == null) LeaderNoRadio();
                else EnterVolumeMode();
                break;

            // Audio Arc Keys Track (2026-08-11): K = mic check ("mic check,
            // one two"), the binding an operator rides while adjusting gain.
            // Speaks ONLY the verdict and level — no transmit-status preamble.
            case Keys.K:
                SpeakMicCheck();
                break;

            // G = the TX test-tone Generator, arm/disarm. Track C built the
            // engine on FlexBase and deliberately added no hotkey because
            // this track owns the key surface.
            case Keys.G:
                ToggleTxTone();
                break;

            case Keys.C:
                if (rig == null) LeaderNoRadio();
                else ToggleLeaderDSP("Compander",
                    () => rig.Compander, v => rig.Compander = v);
                break;

            case Keys.P | Keys.Shift:
                if (rig == null) LeaderNoRadio();
                else ToggleLeaderDSP("Speech Processor",
                    () => rig.ProcessorOn, v => rig.ProcessorOn = v);
                break;

            // TX Filter (F), RX Filter (Shift+F), Enter Frequency (Ctrl+F)
            case Keys.F | Keys.Control:
                if (rig == null) LeaderNoRadio();
                else _context.WriteFreq();
                break;
            case Keys.F | Keys.Shift:
                SpeakRXFilterWidth();
                break;
            case Keys.F:
                SpeakTXFilterWidth();
                break;

            // Tuning debounce toggle
            case Keys.D:
                ToggleTuneDebounce();
                break;

            // Log Stats (moved from Ctrl+Shift+T)
            case Keys.L:
                _context.LogStats();
                EarconPlayer.ConfirmTone();
                break;

            // Flex memories
            case Keys.M:
                if (rig == null) LeaderNoRadio();
                else _context.DisplayMemory();
                break;

            // Tones toggle (Sprint 24 Phase 6)
            case Keys.T:
                ToggleMeterTonesGlobalHandler();
                break;

            // Earcon mute toggle (Sprint 25 Phase 4)
            case Keys.T | Keys.Shift:
                ToggleEarconMute();
                break;

            // Help
            case Keys.Oem2: // ? key (forward slash)
                LeaderKeyHelp();
                break;
            case Keys.H:
                LeaderKeyHelp();
                break;

            // Universal slice-jump: Ctrl+J Shift+<A-H> jumps to that slice
            // from any focus position. Re-frames Ctrl+J as a "jump to" leader
            // when paired with a Shift-modified letter — see TODO entry.
            // Skipping Shift+F: collides with the existing Shift+F = RX filter
            // width binding at line 2010. Slice F (index 5) is only available
            // on FLEX-6700; current testers don't have one. Defer the F
            // collision until a 6700 user appears.
            case Keys.A | Keys.Shift: JumpToSlice(0); break;
            case Keys.B | Keys.Shift: JumpToSlice(1); break;
            case Keys.C | Keys.Shift: JumpToSlice(2); break;
            case Keys.D | Keys.Shift: JumpToSlice(3); break;
            case Keys.E | Keys.Shift: JumpToSlice(4); break;
            case Keys.G | Keys.Shift: JumpToSlice(6); break;
            case Keys.H | Keys.Shift: JumpToSlice(7); break;

            default:
                EarconPlayer.LeaderInvalidTone();
                Radios.ScreenReaderOutput.Speak("Unknown command. Press H for help.", true);
                break;
        }

        return true; // Always consume the key in leader mode
    }

    /// <summary>
    /// Universal slice-jump target. The parameter is the RADIO slice index
    /// (0 = A, 1 = B, ...) — the letter IS the identity, so Shift+D always
    /// means radio slice D. It is resolved to a list position through
    /// SliceIndexToVFO, never used as a position directly: after slice
    /// create/release churn, position and letter diverge, and the old
    /// positional jump activated whatever sat at that position while
    /// announcing a fabricated letter. Announces via speech + earcon and
    /// sets RXVFO without disturbing the current keyboard focus (so a user
    /// on, say, VOX field stays on VOX but is now operating on the new
    /// slice).
    ///
    /// Auto-create on jump-to-uncreated-slice is deferred until the
    /// FlexLib NewSlice flow is wrapped to support targeting a specific
    /// index — current NewSlice() always creates the next available, not
    /// a particular target. For now, missing slices announce a polite
    /// "not yet created" message and the user can create via the Slice
    /// field's period key.
    /// </summary>
    private void JumpToSlice(int sliceIndex)
    {
        var rig = _context.GetRigControl();
        if (rig == null) { LeaderNoRadio(); return; }

        int vfo = rig.SliceIndexToVFO(sliceIndex);
        if (vfo >= 0)
        {
            rig.RXVFO = vfo;
            EarconPlayer.ConfirmTone();
            // Announce the TRUE letter read back from the slice itself.
            Radios.ScreenReaderOutput.Speak(
                $"Slice {rig.VFOToLetter(vfo)} active",
                Radios.VerbosityLevel.Terse, true);
            return;
        }

        // We don't own a slice with this radio index. Letter arithmetic on a
        // RADIO index is identity-correct (index 3 is letter D whether or not
        // the slice exists), so it's safe for the miss messages. Three cases:
        //   - exceeds capacity:            not supported on this radio
        //   - exists, another client's:    in use by another station
        //   - within capacity, not there:  not yet created
        char letter = (char)('A' + sliceIndex);
        int totalCap = rig.TotalMaxSlices;
        if (sliceIndex >= totalCap)
        {
            EarconPlayer.LeaderInvalidTone();
            Radios.ScreenReaderOutput.Speak(
                $"Slice {letter} not available on this radio. Maximum {totalCap} slices.",
                Radios.VerbosityLevel.Critical, true);
        }
        else if (rig.SliceIndexOwnedByOther(sliceIndex))
        {
            EarconPlayer.LeaderInvalidTone();
            Radios.ScreenReaderOutput.Speak(
                $"Slice {letter} is in use by another station.",
                Radios.VerbosityLevel.Critical, true);
        }
        else
        {
            EarconPlayer.LeaderInvalidTone();
            Radios.ScreenReaderOutput.Speak(
                $"Slice {letter} not yet created. From the Slice field, press period to create the next slice.",
                Radios.VerbosityLevel.Critical, true);
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Volume mode (Ctrl+J, V) — Audio Arc Track A, 2026-08-11.
    //  A persistent sub-mode: target letter picks what the arrows adjust,
    //  targets switch freely, every adjustment speaks, Escape exits.
    // ────────────────────────────────────────────────────────────────

    // Local shadow of the selected target's value. The FlexBase radio setters
    // enqueue asynchronously, so reading a property straight back after a set
    // announces the stale value — and under key repeat it would announce the
    // SAME stale value over and over. The shadow is seeded when a target is
    // selected and stepped locally, so ramps are monotonic and honest.
    private int _volumeShadow;

    private void EnterVolumeMode()
    {
        _volumeModeActive = true;
        _volumeTarget = VolumeTarget.None;
        _volumeModePcDirty = false;
        EarconPlayer.LeaderEnterTone();
        Radios.ScreenReaderOutput.Speak(
            "Volume mode. H headphone, P PC output, M mic, L line out, " +
            "C compander, S processor. Up and down adjust. Escape exits.",
            Radios.VerbosityLevel.Terse, true);
    }

    private void ExitVolumeMode(bool speak)
    {
        _volumeModeActive = false;
        _volumeTarget = VolumeTarget.None;
        if (_volumeModePcDirty)
        {
            _volumeModePcDirty = false;
            _context.GetMainWindow()?.PersistPcOutputVolume();
        }
        if (speak)
        {
            EarconPlayer.LeaderCancelTone();
            Radios.ScreenReaderOutput.Speak("Volume mode closed", Radios.VerbosityLevel.Terse, true);
        }
    }

    private bool DoVolumeModeKey(Keys k)
    {
        // Never trap system-level chords: Alt combos (Alt+F4, menu
        // accelerators) and F1 help pass through untouched. The mode stays
        // active — it has no timeout, so it is still there afterwards.
        if ((k & Keys.Alt) != 0 || (k & Keys.KeyCode) == Keys.F1)
            return false;

        // Escape ends the mode — the one guaranteed exit, per house rule.
        if (k == Keys.Escape)
        {
            ExitVolumeMode(speak: true);
            return true;
        }

        // Ctrl+J hands off to a fresh leader chord instead of stranding the
        // operator: volume mode closes (persisting any PC-volume change) and
        // the leader arms exactly as if pressed from anywhere else.
        if (k == (Keys.J | Keys.Control))
        {
            ExitVolumeMode(speak: false);
            _leaderKeyActive = true;
            EarconPlayer.LeaderEnterTone();
            Radios.ScreenReaderOutput.Speak("JJ", Radios.VerbosityLevel.Terse, true);
            return true;
        }

        var rig = _context.GetRigControl();
        if (rig == null)
        {
            // Radio went away under the mode — close it out loud.
            ExitVolumeMode(speak: false);
            LeaderNoRadio();
            return true;
        }

        switch (k)
        {
            case Keys.H: SelectVolumeTarget(rig, VolumeTarget.Headphone); return true;
            case Keys.P: SelectVolumeTarget(rig, VolumeTarget.PcOutput); return true;
            case Keys.M: SelectVolumeTarget(rig, VolumeTarget.MicLevel); return true;
            case Keys.L: SelectVolumeTarget(rig, VolumeTarget.Lineout); return true;
            case Keys.C: SelectVolumeTarget(rig, VolumeTarget.CompanderLevel); return true;
            case Keys.S: SelectVolumeTarget(rig, VolumeTarget.ProcessorMode); return true;

            case Keys.Up: AdjustVolumeTarget(rig, +1); return true;
            case Keys.Down: AdjustVolumeTarget(rig, -1); return true;

            case Keys.Oem2: // ? — help without stealing H from headphone
                EarconPlayer.LeaderHelpTone();
                Radios.ScreenReaderOutput.Speak(
                    "Volume mode targets: H on-radio headphone, P PC output, M mic level, " +
                    "L on-radio line out, C compander level, S processor mode. " +
                    "Up and down adjust the picked target. Escape exits.",
                    Radios.VerbosityLevel.Terse, true);
                return true;

            default:
                EarconPlayer.LeaderInvalidTone();
                Radios.ScreenReaderOutput.Speak(
                    "Volume mode: H, P, M, L, C, or S picks a target, up and down adjust, Escape exits.",
                    Radios.VerbosityLevel.Terse, true);
                return true;
        }
    }

    private void SelectVolumeTarget(Radios.FlexBase rig, VolumeTarget target)
    {
        _volumeTarget = target;
        string announce;
        switch (target)
        {
            case VolumeTarget.Headphone:
                _volumeShadow = rig.HeadphoneGain;
                announce = $"On-radio headphone {_volumeShadow}";
                break;
            case VolumeTarget.PcOutput:
                _volumeShadow = rig.PcOutputVolumeDb;
                announce = $"PC volume {_volumeShadow} dB";
                break;
            case VolumeTarget.MicLevel:
                _volumeShadow = rig.MicGain;
                announce = $"Mic level {_volumeShadow}";
                break;
            case VolumeTarget.Lineout:
                _volumeShadow = rig.LineoutGain;
                announce = $"On-radio line out {_volumeShadow}";
                break;
            case VolumeTarget.CompanderLevel:
                _volumeShadow = rig.CompanderLevel;
                announce = $"Compander {_volumeShadow}";
                if (rig.Compander != FlexBase.OffOnValues.on)
                    announce += ", compander is off";
                break;
            case VolumeTarget.ProcessorMode:
                _volumeShadow = (int)rig.ProcessorSetting;
                announce = $"Processor {ProcessorSettingName(_volumeShadow)}";
                if (rig.ProcessorOn != FlexBase.OffOnValues.on)
                    announce += ", processor is off";
                break;
            default:
                return;
        }
        EarconPlayer.ConfirmTone();
        Radios.ScreenReaderOutput.Speak(announce, Radios.VerbosityLevel.Terse, true);
    }

    private void AdjustVolumeTarget(Radios.FlexBase rig, int direction)
    {
        if (_volumeTarget == VolumeTarget.None)
        {
            EarconPlayer.LeaderInvalidTone();
            Radios.ScreenReaderOutput.Speak(
                "Pick a target first: H, P, M, L, C, or S.",
                Radios.VerbosityLevel.Terse, true);
            return;
        }

        switch (_volumeTarget)
        {
            case VolumeTarget.Headphone:
                _volumeShadow = Math.Clamp(_volumeShadow + direction * 5, 0, 100);
                rig.HeadphoneGain = _volumeShadow;
                Radios.ScreenReaderOutput.Speak($"Headphone {_volumeShadow}", Radios.VerbosityLevel.Terse, true);
                break;
            case VolumeTarget.PcOutput:
                _volumeShadow = Math.Clamp(_volumeShadow + direction,
                    Radios.FlexBase.PcOutputVolumeDbMin, Radios.FlexBase.PcOutputVolumeDbMax);
                rig.PcOutputVolumeDb = _volumeShadow;
                _volumeModePcDirty = true;
                Radios.ScreenReaderOutput.Speak($"PC volume {_volumeShadow} dB", Radios.VerbosityLevel.Terse, true);
                break;
            case VolumeTarget.MicLevel:
                _volumeShadow = Math.Clamp(_volumeShadow + direction * 5, 0, 100);
                rig.MicGain = _volumeShadow;
                Radios.ScreenReaderOutput.Speak($"Mic level {_volumeShadow}", Radios.VerbosityLevel.Terse, true);
                break;
            case VolumeTarget.Lineout:
                _volumeShadow = Math.Clamp(_volumeShadow + direction * 5, 0, 100);
                rig.LineoutGain = _volumeShadow;
                Radios.ScreenReaderOutput.Speak($"Line out {_volumeShadow}", Radios.VerbosityLevel.Terse, true);
                break;
            case VolumeTarget.CompanderLevel:
                _volumeShadow = Math.Clamp(_volumeShadow + direction * FlexBase.CompanderLevelIncrement,
                    FlexBase.CompanderLevelMin, FlexBase.CompanderLevelMax);
                rig.CompanderLevel = _volumeShadow;
                Radios.ScreenReaderOutput.Speak($"Compander {_volumeShadow}", Radios.VerbosityLevel.Terse, true);
                break;
            case VolumeTarget.ProcessorMode:
                // Up = stronger (Normal → DX → DX+), Down = gentler. Clamps at
                // the ends — wrapping on an arrow key is disorienting speech.
                _volumeShadow = Math.Clamp(_volumeShadow + direction, 0, 2);
                rig.ProcessorSetting = (FlexBase.ProcessorSettings)_volumeShadow;
                Radios.ScreenReaderOutput.Speak($"Processor {ProcessorSettingName(_volumeShadow)}", Radios.VerbosityLevel.Terse, true);
                break;
        }
    }

    private static string ProcessorSettingName(int setting) => setting switch
    {
        1 => "DX",
        2 => "DX plus",
        _ => "Normal",
    };

    private void LeaderNoRadio()
    {
        EarconPlayer.LeaderInvalidTone();
        Radios.ScreenReaderOutput.Speak("No radio connected", Radios.VerbosityLevel.Critical);
    }

    /// <summary>
    /// Ctrl+J, K — the dedicated mic-audio query (Audio Arc Keys Track,
    /// 2026-08-11). Speaks ONLY verdict and level, context-aware: live
    /// recent peak while transmitting, the last transmit's peak while
    /// receiving. This is the binding an operator rides while adjusting mic
    /// gain, and because leader chords are not mnemonics it works inside
    /// the Audio Workshop where Alt+Shift+S used to get eaten.
    /// Wording mirrors the Home expander's readout field so the two
    /// surfaces never disagree.
    /// </summary>
    private void SpeakMicCheck()
    {
        var rig = _context.GetRigControl();
        if (rig == null) { LeaderNoRadio(); return; }

        string msg;
        if (rig.Transmit && rig.ScMicRecentDb > -140f)
            msg = FormatMicVerdict(rig.ScMicRecentDb);
        else if (rig.Transmit)
            msg = "Mic audio, no reading yet";
        else if (rig.ScMicMaxDb > -140f)
            msg = FormatMicVerdict(rig.ScMicMaxDb, lastTransmit: true);
        else
            msg = "Mic audio, transmit to measure";
        Radios.ScreenReaderOutput.Speak(msg, Radios.VerbosityLevel.Terse, true);
    }

    /// <summary>
    /// Ctrl+J, G — arm or disarm the TX test tone from anywhere (Audio Arc
    /// Keys Track, 2026-08-11). Drives the FlexBase engine Track C built
    /// (TxToneStart/Stop); the Audio Workshop is NOT touched. Behaviour
    /// deliberately mirrors the Workshop's checkbox: path trouble REFUSES
    /// to arm (arming "successfully" while something else keeps
    /// transmitting is the meter-that-lied failure class), a tone outside
    /// the TX filter passband arms-and-warns loudly, and every key-down
    /// while armed announces that the tone is riding it.
    /// </summary>
    private void ToggleTxTone()
    {
        var rig = _context.GetRigControl();
        if (rig == null) { LeaderNoRadio(); return; }

        if (rig.TxToneEngaged)
        {
            rig.TxToneStop();
            // Clear the key-down rider. If the Workshop installed its own
            // hook it is self-guarded (returns null once disengaged), so a
            // plain clear is safe either way; the Workshop re-installs on
            // its next arm.
            PttSafetyController.KeyDownAnnouncementExtra = null;
            EarconPlayer.FeatureOffTone();
            Radios.ScreenReaderOutput.Speak("Test tone off. Microphone restored.",
                Radios.VerbosityLevel.Critical, true);
            return;
        }

        string trouble = rig.TxTonePathTrouble;
        if (!string.IsNullOrEmpty(trouble))
        {
            EarconPlayer.LeaderInvalidTone();
            Radios.ScreenReaderOutput.Speak("Test tone not armed. " + trouble,
                Radios.VerbosityLevel.Critical, true);
            return;
        }

        // The operator's saved tone settings — per-operator on purpose:
        // the frequency is a hearing choice and does not change with the rig.
        var cfg = _context.GetMainWindow()?.CurrentAudioConfig;
        int freq = cfg?.TxToneFrequencyHz ?? 440;
        int level = cfg?.TxToneLevelDb ?? -10;
        rig.TxToneFrequency = freq;
        rig.TxToneLevelDb = level;
        rig.TxToneStart();

        // Same key-down honesty the Workshop provides: every transmit path
        // announces the tone is riding it. The closure reads the rig live
        // and returns null once disengaged, so a stale hook is harmless.
        PttSafetyController.KeyDownAnnouncementExtra =
            () => BuildLeaderToneAnnouncement(_context.GetRigControl());

        string line = $"Test tone armed: {freq} hertz at {level} dBFS. " +
            "It replaces your microphone while you transmit. Control J, G disarms.";
        if (freq < rig.TXFilterLow || freq > rig.TXFilterHigh)
        {
            line += $" Warning: {freq} hertz is outside your transmit filter, " +
                $"{rig.TXFilterLow} to {rig.TXFilterHigh}. Nothing will go out.";
            EarconPlayer.Warning2Beep();
        }
        else
        {
            EarconPlayer.FeatureOnTone();
        }
        Radios.ScreenReaderOutput.Speak(line, Radios.VerbosityLevel.Critical, true);
    }

    /// <summary>
    /// The key-down announcement for a leader-armed tone. Re-checks the
    /// path and passband at the moment of key-down, because both can have
    /// changed since arming. Null when the tone is not engaged.
    /// </summary>
    private static string? BuildLeaderToneAnnouncement(Radios.FlexBase? rig)
    {
        if (rig == null || !rig.TxToneEngaged) return null;
        string trouble = rig.TxTonePathTrouble;
        if (!string.IsNullOrEmpty(trouble))
            return "The test tone is armed but is not going out. " + trouble;
        int freq = (int)rig.TxToneFrequency;
        string line = $"Sending the {freq} hertz test tone instead of your microphone.";
        if (freq < rig.TXFilterLow || freq > rig.TXFilterHigh)
            line += " Warning: the tone is outside your transmit filter. Nothing is going out.";
        return line;
    }

    private void ToggleLeaderDSP(string label, Func<FlexBase.OffOnValues> getter, Action<FlexBase.OffOnValues> setter)
    {
        var rig = _context.GetRigControl();
        if (rig == null) { LeaderNoRadio(); return; }

        var current = getter();
        var newVal = rig.ToggleOffOn(current);
        setter(newVal);
        if (newVal == FlexBase.OffOnValues.on)
        {
            EarconPlayer.FeatureOnTone();
            Radios.ScreenReaderOutput.Speak(label + " on", Radios.VerbosityLevel.Terse);
        }
        else
        {
            EarconPlayer.FeatureOffTone();
            Radios.ScreenReaderOutput.Speak(label + " off", Radios.VerbosityLevel.Terse);
        }
    }

    private void ToggleTuneDebounce()
    {
        var mainWindow = _context.GetMainWindow();
        var config = mainWindow?.CurrentAudioConfig;
        if (config == null)
        {
            EarconPlayer.LeaderInvalidTone();
            Radios.ScreenReaderOutput.Speak("No audio configuration loaded", Radios.VerbosityLevel.Critical);
            return;
        }

        config.TuneDebounceEnabled = !config.TuneDebounceEnabled;
        if (config.TuneDebounceEnabled)
        {
            EarconPlayer.FeatureOnTone();
            Radios.ScreenReaderOutput.Speak("Tuning debounce on", Radios.VerbosityLevel.Terse);
        }
        else
        {
            EarconPlayer.FeatureOffTone();
            Radios.ScreenReaderOutput.Speak("Tuning debounce off", Radios.VerbosityLevel.Terse);
        }

        // Persist immediately.
        var configDir = _context.GetConfigDirectory();
        if (configDir != null)
            config.Save(configDir);
    }

    private void SpeakRXFilterWidth()
    {
        var rig = _context.GetRigControl();
        if (rig == null)
        {
            Radios.ScreenReaderOutput.Speak("No radio connected", Radios.VerbosityLevel.Critical);
            return;
        }
        int low = rig.FilterLow;
        int high = rig.FilterHigh;
        int width = high - low;
        string widthKHz = (width / 1000.0).ToString("F1");
        Radios.ScreenReaderOutput.Speak($"RX filter {low} to {high}, {widthKHz} kilohertz", Radios.VerbosityLevel.Terse);
    }

    private void SpeakTXFilterWidth()
    {
        var rig = _context.GetRigControl();
        if (rig == null)
        {
            Radios.ScreenReaderOutput.Speak("No radio connected", Radios.VerbosityLevel.Critical);
            return;
        }
        int low = rig.TXFilterLow;
        int high = rig.TXFilterHigh;
        int width = high - low;
        string widthKHz = (width / 1000.0).ToString("F1");
        Radios.ScreenReaderOutput.Speak($"TX filter {low} to {high}, {widthKHz} kilohertz", Radios.VerbosityLevel.Terse);
    }

    private void LeaderKeyHelp()
    {
        EarconPlayer.LeaderHelpTone();
        // Generated from KeyInventory.LeaderCommands — the same table that
        // feeds the Keys dialog, the Command Finder, and the exported key
        // list — so this announcement can no longer drift from reality. The
        // hand-written string it replaces was missing six commands
        // (2026-05-11 JJ+H audit, companion keyboard-reference audit).
        Radios.ScreenReaderOutput.Speak(KeyInventory.LeaderHelpSpeech());
    }

    // ────────────────────────────────────────────────────────────────
    //  Public API — Sprint 24 Phase 4 (for VB callers)
    // ────────────────────────────────────────────────────────────────

    // ────────────────────────────────────────────────────────────────
    //  Key binding validation — Sprint 24 Phase 5
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validate default key bindings for scope conflicts.
    /// Called at startup; logs any conflicts via trace output.
    /// Returns true if no conflicts found.
    /// </summary>
    public bool ValidateKeyBindings()
    {
        bool clean = true;
        var boundKeys = _defaultKeys.Where(d => d.Key != Keys.None).ToArray();

        for (int i = 0; i < boundKeys.Length; i++)
        {
            for (int j = i + 1; j < boundKeys.Length; j++)
            {
                if (boundKeys[i].Key != boundKeys[j].Key) continue;

                var s1 = boundKeys[i].Scope;
                var s2 = boundKeys[j].Scope;

                // Same scope = conflict.
                // Global + anything = conflict (Global is always active).
                // Radio + Logging = OK (never simultaneous).
                bool conflict = (s1 == s2) ||
                                (s1 == KeyScope.Global || s2 == KeyScope.Global);

                if (conflict)
                {
                    var msg = $"KEY CONFLICT: {boundKeys[i].Key} bound to " +
                              $"{boundKeys[i].Id} ({s1}) AND {boundKeys[j].Id} ({s2})";
                    _context.Trace(msg);
                    clean = false;
                }
            }
        }

        if (clean)
            _context.Trace("ValidateKeyBindings: all bindings clean, no conflicts");

        return clean;
    }

    /// <summary>
    /// Exposes the context for subclass construction (e.g. LogEntry.myKeyCommands).
    /// </summary>
    public KeyCommandContext Context => _context;

    /// <summary>
    /// Get the ADIF tag for this command ID.
    /// Used by LogEntry.vb's myKeyCommands subclass.
    /// </summary>
    public string CommandIDToADIF(CommandValues id)
    {
        var kt = Lookup(id);
        return kt?.ADIFTag ?? string.Empty;
    }

    /// <summary>
    /// Toggle1 — dispatches to FlexKnob's NextValue callback.
    /// </summary>
    public void Toggle1() => _context.Toggle1();

    /// <summary>
    /// Shut down any open cluster connections.
    /// </summary>
    public void ClusterShutdown() => _context.ClusterShutdown();

    /// <summary>
    /// Display decoded CW text in the received text window.
    /// </summary>
    public void DisplayDecodedText(string text) => _context.DisplayDecodedText(text);
}
