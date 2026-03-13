using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Serialization;
using JJTrace;
using Radios;

namespace JJFlexWpf;

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

    // ── Command ID tracking — handlers can read this to know which command triggered them. ──
    public CommandValues CommandId { get; private set; }

    // ── Internal ADIF pseudotags (used by log field entries in the key table) ──
    internal const string IADIF_Logform = "$LOGFORM";
    internal const string IADIF_Logwrite = "$LOGWRITE";
    internal const string IADIF_Logfile = "$LOGFILE";
    internal const string IADIF_LogNewEntry = "$LOGNEWENTRY";
    internal const string IADIF_Logsearch = "$LOGSEARCH";

    // ── Config version — increment when keybindings are reshuffled ──
    // v5 = Sprint 23: unified hotkey dispatch, expander keys, scope cleanup
    private const int KeyConfigVersion = 5;

    // ────────────────────────────────────────────────────────────────
    //  Key Table — one entry per bindable action. Handlers are null
    //  in this skeleton; Phase 3 fills them in.
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Master key table. It's in logical order, not CommandValues order.
    /// </summary>
    internal KeyTableEntry[] KeyTable = null!; // Initialized in BuildKeyTable()

    /// <summary>
    /// Build the key table. Called from constructors after _context is set.
    /// Separated from field initializer because handlers reference _context.
    /// </summary>
    private void BuildKeyTable()
    {
        KeyTable = new KeyTableEntry[]
        {
            // ── Help ──
            new(CommandValues.ShowHelp, null!,
                "Show keys help", null, FunctionGroups.Help, KeyScope.Global)
                { Keywords = new[] { "help", "keys", "hotkeys", "shortcuts", "keyboard" } },
            new(CommandValues.ShowContextHelp, KeyTypes.Command, null!,
                "Open help file", "Help file", false, FunctionGroups.Help, KeyScope.Global)
                { Keywords = new[] { "help", "file", "chm", "documentation", "manual", "f1" } },

            // ── Routing / Scan ──
            new(CommandValues.ShowFreq, null!,
                "Focus frequency display", null, FunctionGroups.RoutingScan, KeyScope.Radio)
                { Keywords = new[] { "frequency", "focus", "display", "tune", "tuning" } },
            new(CommandValues.ResumeTheScan, null!,
                "Resume the scan.", "resume scan", FunctionGroups.Scan, KeyScope.Radio)
                { Keywords = new[] { "scan", "resume", "continue", "scanning" } },
            new(CommandValues.ShowReceived, null!,
                "goto the received text window", null, FunctionGroups.Routing, KeyScope.Radio)
                { Keywords = new[] { "receive", "text", "window", "cw", "morse", "focus" } },
            new(CommandValues.ShowSend, null!,
                "go to the send text window", null, FunctionGroups.Routing, KeyScope.Radio)
                { Keywords = new[] { "send", "text", "window", "cw", "morse", "focus" } },
            new(CommandValues.ShowSendDirect, null!,
                "go to the send text window and send direct from keyboard", null, FunctionGroups.Routing, KeyScope.Radio)
                { Keywords = new[] { "send", "direct", "keyboard", "cw", "morse", "type" } },

            // ── General ──
            new(CommandValues.SmeterDBM, KeyTypes.Command, null!,
                "Display SMeter in DBM or S-units", _context.SMeterMenuString, false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "s meter", "signal", "strength", "dbm", "s-units", "meter" } },
            new(CommandValues.ReadSMeter, null!,
                "Read the S-meter value aloud", null, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "s meter", "signal", "strength", "read", "speak", "announce" } },

            // ── Audio / Meter ──
            new(CommandValues.ToggleMeterTones, null!,
                "Toggle meter sonification tones", null, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "meter", "tone", "sonification", "audio", "pitch", "toggle" } },
            new(CommandValues.CycleMeterPreset, null!,
                "Cycle meter tone preset (RX, TX, Full Monitor)", null, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "meter", "preset", "cycle", "rx", "tx", "monitor" } },
            new(CommandValues.SpeakMeters, null!,
                "Speak current meter values", null, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "meter", "speak", "read", "alc", "swr", "power", "signal" } },

            // ── CW ──
            new(CommandValues.StopCW, KeyTypes.Command, null!,
                "Stop sending CW", "cw stop", true, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "cw", "morse", "stop", "abort", "sending" } },

            // ── Frequency / Memory ──
            new(CommandValues.SetFreq, null!,
                "Enter frequency", "frequency", FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "frequency", "enter", "type", "tune", "tuning", "dial" } },
            new(CommandValues.ShowMemory, null!,
                "Bring up the memory dialogue", "memories", FunctionGroups.Dialog, KeyScope.Radio)
                { Keywords = new[] { "memory", "memories", "store", "recall", "save", "channel" } },
            new(CommandValues.CycleContinuous, null!,
                "Toggle continuous frequency display", null, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "continuous", "frequency", "display", "toggle" } },

            // ── Logging ──
            new(CommandValues.LogDateTime, null!,
                "Set log date/time", "log date/time", "QSO_DATE", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "date", "time", "contact", "logging" } },
            new(CommandValues.LogFinalize, null!,
                "Write log entry", "log write", IADIF_Logwrite, KeyTypes.Command, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "write", "save", "entry", "contact", "finalize", "logging" } },
            new(CommandValues.LogFileName, null!,
                "Enter log file name", "log file name", IADIF_Logfile, KeyTypes.Command, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "file", "name", "logging" } },
            new(CommandValues.LogMode, null!,
                "Log the mode", "log mode", "MODE", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "mode", "contact", "logging" } },
            new(CommandValues.LogCall, null!,
                "Log callsign", "log call", "CALL", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "call", "callsign", "contact", "logging" } },
            new(CommandValues.LogHisRST, null!,
                "Log his RST", "log his RST", "RST_SENT", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "rst", "signal", "report", "his", "contact", "logging" } },
            new(CommandValues.LogMyRST, null!,
                "Log my RST", "log my RST", "RST_RCVD", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "rst", "signal", "report", "my", "contact", "logging" } },
            new(CommandValues.LogQTH, null!,
                "Log QTH", "log QTH", "QTH", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "qth", "location", "contact", "logging" } },
            new(CommandValues.LogState, null!,
                "Log state/province", "log state", "STATE", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "state", "province", "contact", "logging" } },
            new(CommandValues.LogGrid, null!,
                "Log Grid square", "log Grid", "GRIDSQUARE", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "grid", "square", "locator", "contact", "logging" } },
            new(CommandValues.LogHandle, null!,
                "Log name", "log name", "NAME", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "name", "handle", "operator", "contact", "logging" } },
            new(CommandValues.LogRig, null!,
                "Log rig", "log rig", "RIG", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "rig", "radio", "contact", "logging" } },
            new(CommandValues.LogAnt, null!,
                "Log antenna", "log antenna", "ANTENNA", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "antenna", "contact", "logging" } },
            new(CommandValues.LogComments, null!,
                "Log comments", "log comments", "COMMENT", KeyTypes.Log, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "comments", "notes", "contact", "logging" } },
            new(CommandValues.NewLogEntry, null!,
                "New log entry", "new log entry", IADIF_LogNewEntry, KeyTypes.Command, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "new", "entry", "contact", "logging" } },
            new(CommandValues.SearchLog, null!,
                "Find a log entry", "log search", IADIF_Logsearch, KeyTypes.Command, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "search", "find", "contact", "logging" } },

            // ── Navigation / Panning ──
            new(CommandValues.DoPanning, null!,
                "Focus to panning", "panning", FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "pan", "panning", "stereo", "audio", "balance" } },

            // ── Scan ──
            new(CommandValues.StartScan, null!,
                "Start/stop scan", "start scan", FunctionGroups.Scan, KeyScope.Radio)
                { Keywords = new[] { "scan", "start", "stop", "search", "scanning" } },
            new(CommandValues.SavedScan, null!,
                "Use a saved scan", "saved scan", FunctionGroups.Scan, KeyScope.Radio)
                { Keywords = new[] { "scan", "saved", "preset", "scanning" } },
            new(CommandValues.StopScan, null!,
                "Stop the current scan", "stop scan", FunctionGroups.Scan, KeyScope.Radio)
                { Keywords = new[] { "scan", "stop", "halt", "scanning" } },
            new(CommandValues.MemoryScan, null!,
                "Memory scan", "memory scan", FunctionGroups.Scan, KeyScope.Radio)
                { Keywords = new[] { "scan", "memory", "memories", "scanning", "channel" } },

            // ── Dialogs ──
            new(CommandValues.ShowMenus, null!,
                "Show the rig's menus.", "menus", FunctionGroups.Dialog, KeyScope.Radio)
                { Keywords = new[] { "menu", "menus", "rig", "radio", "settings" } },

            // ── Audio volume ──
            new(CommandValues.AudioGainUp, KeyTypes.Command, null!,
                "Raise RF gain or Flex slice gain.", string.Empty, true, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "volume", "gain", "audio", "louder", "up", "slice" } },
            new(CommandValues.AudioGainDown, KeyTypes.Command, null!,
                "Lower RF gain or Flex slice gain.", string.Empty, true, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "volume", "gain", "audio", "quieter", "down", "slice" } },
            new(CommandValues.HeadphonesUp, KeyTypes.Command, null!,
                "If supported, raise headphone gain.", string.Empty, true, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "headphones", "volume", "audio", "louder", "gain" } },
            new(CommandValues.HeadphonesDown, KeyTypes.Command, null!,
                "If supported, lower headphone gain.", string.Empty, true, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "headphones", "volume", "audio", "quieter", "gain" } },
            new(CommandValues.LineoutUp, KeyTypes.Command, null!,
                "Raise audio gain or Flex lineout gain.", string.Empty, true, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "lineout", "volume", "audio", "gain", "output" } },
            new(CommandValues.LineoutDown, KeyTypes.Command, null!,
                "lower audio gain or Flex lineout gain.", string.Empty, true, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "lineout", "volume", "audio", "gain", "output" } },

            // ── CW / RIT / Beacon / Cluster ──
            new(CommandValues.CWZeroBeat, KeyTypes.Command, null!,
                "Zerobeat CW signal.", "Zerobeat CW signal", true, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "cw", "morse", "zerobeat", "zero beat", "tune" } },
            new(CommandValues.ClearRIT, KeyTypes.Command, null!,
                "Clear RIT.", "Clear Rit", true, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "rit", "clear", "offset", "receive", "incremental" } },
            new(CommandValues.ReverseBeacon, KeyTypes.Command, null!,
                "Bring up a reverse beacon site for a call.", "Reverse Beacon", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "beacon", "reverse", "spots", "dx", "rbn" } },
            new(CommandValues.ArCluster, KeyTypes.Command, null!,
                "Bring up the DX spotting cluster.", "DX cluster", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "cluster", "dx", "spots", "spotting" } },

            // ── Logging (continued) ──
            new(CommandValues.LogStats, KeyTypes.Command, null!,
                "Show log statistics", "Show log statistics", false, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "statistics", "stats", "contact", "count", "logging" } },

            // ── Audio features ──
            new(CommandValues.RemoteAudio, KeyTypes.Command, null!,
                "PC audio on/off", _context.AudioMenuString, false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "audio", "remote", "pc", "mute", "unmute", "on", "off" } },
            new(CommandValues.AudioSetup, KeyTypes.Command, null!,
                "Select audio device", "Select Audio Device", false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "audio", "device", "setup", "settings", "configure", "preferences", "sound" } },

            // ── Lookups / Debug / ATU / Reboot / TX ──
            new(CommandValues.StationLookup, KeyTypes.Command, null!,
                "Station lookup", "Station lookup", false, FunctionGroups.Logging, KeyScope.Global)
                { Keywords = new[] { "station", "lookup", "callsign", "qrz", "search" } },
            new(CommandValues.GatherDebug, KeyTypes.Command, null!,
                "Collect debug info", "Collect debug info", false, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "debug", "info", "diagnostic", "troubleshoot" } },
            new(CommandValues.ATUMemories, KeyTypes.Command, null!,
                "Tuner memories", "Tuner memories", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "tuner", "atu", "antenna", "memories", "tune" } },
            new(CommandValues.Reboot, KeyTypes.Command, null!,
                "Reboot the radio", "Reboot the radio", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "reboot", "restart", "radio", "reset" } },
            new(CommandValues.TXControls, KeyTypes.Command, null!,
                "Transmit controls", "Transmit controls", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "transmit", "tx", "power", "controls", "watts", "ptt" } },

            // ── Logging-only actions ──
            new(CommandValues.LogPaneSwitchF6, KeyTypes.Command, null!,
                "Switch between radio and log panes", "Switch panes", false, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "pane", "switch", "focus", "logging" } },
            new(CommandValues.LogCharacteristicsDialog, KeyTypes.Command, null!,
                "Open log characteristics dialog", "Log characteristics", false, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "characteristics", "settings", "configure", "logging" } },
            new(CommandValues.LogOpenFullForm, KeyTypes.Command, null!,
                "Open full log entry form", "Full log form", false, FunctionGroups.Logging, KeyScope.Logging)
                { Keywords = new[] { "log", "full", "form", "entry", "logging" } },

            // ── Context help / Status ──
            new(CommandValues.ContextHelp, KeyTypes.Command, null!,
                "Context-aware command finder", "Command finder", false, FunctionGroups.Help, KeyScope.Global)
                { Keywords = new[] { "help", "context", "command", "finder", "search", "keys" } },
            new(CommandValues.SpeakStatus, KeyTypes.Command, null!,
                "Speak radio status summary", "Speak status", false, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "status", "speak", "info", "radio", "summary" } },
            new(CommandValues.ShowStatusDialog, KeyTypes.Command, null!,
                "Show radio status dialog", "Status dialog", false, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "status", "dialog", "info", "radio", "show" } },
            new(CommandValues.SpeakTxStatus, KeyTypes.Command, null!,
                "Speak transmit status and time remaining", "Transmit status", false, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "transmit", "ptt", "push to talk", "status", "tx", "time" } },

            // ── Band jumps ──
            new(CommandValues.BandJump160, KeyTypes.Command, null!,
                "Jump to 160 meter band", "160m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "160", "meter", "jump", "frequency" } },
            new(CommandValues.BandJump80, KeyTypes.Command, null!,
                "Jump to 80 meter band", "80m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "80", "meter", "jump", "frequency" } },
            new(CommandValues.BandJump60, KeyTypes.Command, null!,
                "Jump to 60 meter band", "60m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "60", "meter", "jump", "frequency" } },
            new(CommandValues.BandJump40, KeyTypes.Command, null!,
                "Jump to 40 meter band", "40m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "40", "meter", "jump", "frequency" } },
            new(CommandValues.BandJump30, KeyTypes.Command, null!,
                "Jump to 30 meter band", "30m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "30", "meter", "jump", "frequency", "warc" } },
            new(CommandValues.BandJump20, KeyTypes.Command, null!,
                "Jump to 20 meter band", "20m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "20", "meter", "jump", "frequency" } },
            new(CommandValues.BandJump17, KeyTypes.Command, null!,
                "Jump to 17 meter band", "17m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "17", "meter", "jump", "frequency", "warc" } },
            new(CommandValues.BandJump15, KeyTypes.Command, null!,
                "Jump to 15 meter band", "15m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "15", "meter", "jump", "frequency" } },
            new(CommandValues.BandJump12, KeyTypes.Command, null!,
                "Jump to 12 meter band", "12m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "12", "meter", "jump", "frequency", "warc" } },
            new(CommandValues.BandJump10, KeyTypes.Command, null!,
                "Jump to 10 meter band", "10m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "10", "meter", "jump", "frequency" } },
            new(CommandValues.BandJump6, KeyTypes.Command, null!,
                "Jump to 6 meter band", "6m", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "6", "meter", "jump", "frequency", "vhf" } },
            new(CommandValues.BandUp, KeyTypes.Command, null!,
                "Next higher band", "Band up", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "up", "next", "higher", "navigate" } },
            new(CommandValues.BandDown, KeyTypes.Command, null!,
                "Next lower band", "Band down", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "band", "down", "previous", "lower", "navigate" } },

            // ── Mode switching ──
            new(CommandValues.ModeNext, KeyTypes.Command, null!,
                "Cycle to next mode", "Next mode", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "mode", "next", "cycle", "usb", "lsb", "cw", "am", "fm", "digu", "digl" } },
            new(CommandValues.ModePrev, KeyTypes.Command, null!,
                "Cycle to previous mode", "Previous mode", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "mode", "previous", "back", "cycle", "usb", "lsb", "cw" } },
            new(CommandValues.ModeUSB, KeyTypes.Command, null!,
                "Switch to USB mode", "USB", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "mode", "usb", "upper", "sideband", "ssb", "phone" } },
            new(CommandValues.ModeLSB, KeyTypes.Command, null!,
                "Switch to LSB mode", "LSB", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "mode", "lsb", "lower", "sideband", "ssb", "phone" } },
            new(CommandValues.ModeCW, KeyTypes.Command, null!,
                "Switch to CW mode", "CW", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "mode", "cw", "morse", "code", "continuous wave" } },

            // ── TX Filter ──
            new(CommandValues.TXFilterLowDown, KeyTypes.Command, null!,
                "Nudge TX filter low edge down", (string?)null, false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "tx", "filter", "low", "down", "transmit", "sculpt" } },
            new(CommandValues.TXFilterLowUp, KeyTypes.Command, null!,
                "Nudge TX filter low edge up", (string?)null, false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "tx", "filter", "low", "up", "transmit", "sculpt" } },
            new(CommandValues.TXFilterHighDown, KeyTypes.Command, null!,
                "Nudge TX filter high edge down", (string?)null, false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "tx", "filter", "high", "down", "transmit", "sculpt" } },
            new(CommandValues.TXFilterHighUp, KeyTypes.Command, null!,
                "Nudge TX filter high edge up", (string?)null, false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "tx", "filter", "high", "up", "transmit", "sculpt" } },
            new(CommandValues.SpeakTXFilter, KeyTypes.Command, null!,
                "Speak TX filter width", (string?)null, false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "tx", "filter", "width", "bandwidth", "speak", "transmit", "sculpt" } },

            // ── Dialog launchers ──
            new(CommandValues.OpenAudioWorkshop, KeyTypes.Command, null!,
                "Open Audio Workshop dialog", "Audio Workshop", false, FunctionGroups.Dialog, KeyScope.Global)
                { Keywords = new[] { "audio", "workshop", "tx", "transmit", "mic", "compander", "preset", "earcon" } },

            // ── Tuning ──
            new(CommandValues.TuneToggle, KeyTypes.Command, null!,
                "Toggle tune carrier on or off", "Tune carrier", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "tune", "carrier", "toggle", "cw", "manual" } },
            new(CommandValues.ATUTune, KeyTypes.Command, null!,
                "Start ATU tune cycle", "ATU Tune", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "atu", "tune", "antenna", "tuner", "auto", "match", "swr" } },
            new(CommandValues.ToggleMeters, KeyTypes.Command, null!,
                "Toggle meter tones on or off", "Toggle Meters", false, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "meter", "tones", "sonification", "audio", "s-meter", "alc", "swr" } },

            // ── 60m channels ──
            new(CommandValues.SixtyMeterChannelUp, KeyTypes.Command, null!,
                "Next 60 meter channel", "60m Channel Up", false, FunctionGroups.Tuning, KeyScope.Radio)
                { Keywords = new[] { "60", "meter", "channel", "up", "next", "five", "navigate" } },
            new(CommandValues.SixtyMeterChannelDown, KeyTypes.Command, null!,
                "Previous 60 meter channel", "60m Channel Down", false, FunctionGroups.Tuning, KeyScope.Radio)
                { Keywords = new[] { "60", "meter", "channel", "down", "previous", "five", "navigate" } },

            // ── ScreenFields expanders ──
            new(CommandValues.ToggleDspExpander, KeyTypes.Command, null!,
                "Toggle DSP expander in ScreenFields panel", "DSP Expander", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "dsp", "noise", "reduction", "expander", "screenfields", "panel" } },
            new(CommandValues.ToggleAudioExpander, KeyTypes.Command, null!,
                "Toggle Audio expander in ScreenFields panel", "Audio Expander", false, FunctionGroups.Audio, KeyScope.Radio)
                { Keywords = new[] { "audio", "expander", "screenfields", "panel" } },
            new(CommandValues.ToggleReceiverExpander, KeyTypes.Command, null!,
                "Toggle Receiver expander in ScreenFields panel", "Receiver Expander", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "receiver", "rx", "expander", "screenfields", "panel" } },
            new(CommandValues.ToggleTransmissionExpander, KeyTypes.Command, null!,
                "Toggle Transmission expander in ScreenFields panel", "Transmission Expander", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "transmission", "tx", "expander", "screenfields", "panel" } },
            new(CommandValues.ToggleAntennaExpander, KeyTypes.Command, null!,
                "Toggle Antenna expander in ScreenFields panel", "Antenna Expander", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "antenna", "ant", "expander", "screenfields", "panel" } },

            // ── Speak / Repeat ──
            new(CommandValues.SpeakFrequency, KeyTypes.Command, null!,
                "Speak current frequency and mode", "Speak Frequency", false, FunctionGroups.General, KeyScope.Radio)
                { Keywords = new[] { "frequency", "freq", "speak", "readback" } },
            new(CommandValues.RepeatLastMessage, KeyTypes.Command, null!,
                "Repeat the last spoken message", "Repeat Last Message", false, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "repeat", "last", "message", "speech", "again" } },
        };
    }

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
        new(Keys.None, CommandValues.SetFreq, KeyScope.Radio),
        new(Keys.None, CommandValues.ShowMemory, KeyScope.Radio),
        new(Keys.M | Keys.Control | Keys.Shift, CommandValues.MemoryScan, KeyScope.Radio),
        new(Keys.None, CommandValues.SmeterDBM, KeyScope.Radio),
        new(Keys.S | Keys.Control, CommandValues.ReadSMeter, KeyScope.Radio),
        new(Keys.M | Keys.Control | Keys.Alt, CommandValues.ToggleMeterTones, KeyScope.Radio),
        new(Keys.P | Keys.Control | Keys.Alt, CommandValues.CycleMeterPreset, KeyScope.Radio),
        new(Keys.V | Keys.Control | Keys.Alt, CommandValues.SpeakMeters, KeyScope.Radio),
        new(Keys.None, CommandValues.CycleContinuous, KeyScope.Radio),
        new(Keys.None, CommandValues.LogForm, KeyScope.Radio),
        new(Keys.C | Keys.Control | Keys.Shift, CommandValues.ClearRIT, KeyScope.Radio),
        new(Keys.S | Keys.Control | Keys.Alt, CommandValues.StartScan, KeyScope.Radio),
        new(Keys.D | Keys.Alt, CommandValues.ArCluster, KeyScope.Radio),
        new(Keys.R | Keys.Control | Keys.Alt, CommandValues.ReverseBeacon, KeyScope.Radio),
        new(Keys.P | Keys.Control, CommandValues.DoPanning, KeyScope.Radio),
        new(Keys.None, CommandValues.SavedScan, KeyScope.Radio),
        new(Keys.Z | Keys.Control, CommandValues.StopScan, KeyScope.Radio),
        new(Keys.None, CommandValues.ShowMenus, KeyScope.Radio),
        new(Keys.PageUp | Keys.Alt, CommandValues.AudioGainUp, KeyScope.Radio),
        new(Keys.PageDown | Keys.Alt, CommandValues.AudioGainDown, KeyScope.Radio),
        new(Keys.PageUp | Keys.Alt | Keys.Shift, CommandValues.HeadphonesUp, KeyScope.Radio),
        new(Keys.PageDown | Keys.Alt | Keys.Shift, CommandValues.HeadphonesDown, KeyScope.Radio),
        new(Keys.PageUp | Keys.Shift, CommandValues.LineoutUp, KeyScope.Radio),
        new(Keys.PageDown | Keys.Shift, CommandValues.LineoutDown, KeyScope.Radio),
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
        new(Keys.None, CommandValues.ShowStatusDialog, KeyScope.Global),
        new(Keys.S | Keys.Alt | Keys.Shift, CommandValues.SpeakTxStatus, KeyScope.Global),

        // TX Filter
        new(Keys.OemOpenBrackets | Keys.Control | Keys.Shift, CommandValues.TXFilterLowDown, KeyScope.Radio),
        new(Keys.OemCloseBrackets | Keys.Control | Keys.Shift, CommandValues.TXFilterLowUp, KeyScope.Radio),
        new(Keys.OemOpenBrackets | Keys.Control | Keys.Alt, CommandValues.TXFilterHighDown, KeyScope.Radio),
        new(Keys.OemCloseBrackets | Keys.Control | Keys.Alt, CommandValues.TXFilterHighUp, KeyScope.Radio),
        new(Keys.None, CommandValues.SpeakTXFilter, KeyScope.Radio),

        // Audio Workshop, Tune, ATU, Meters
        new(Keys.W | Keys.Control | Keys.Shift, CommandValues.OpenAudioWorkshop, KeyScope.Global),
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

        // Speak frequency, Repeat last message
        new(Keys.F | Keys.Control, CommandValues.SpeakFrequency, KeyScope.Radio),
        new(Keys.F4 | Keys.Control, CommandValues.RepeatLastMessage, KeyScope.Global),
    };

    // ────────────────────────────────────────────────────────────────
    //  Dictionaries
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Dictionary to access the key table using a key.
    /// Each key maps to a list of KeyTableEntry entries (one per scope).
    /// </summary>
    internal Dictionary<Keys, List<KeyTableEntry>> KeyDictionary = null!;

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
    internal bool AddToKeyDictionary(KeyTableEntry item)
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
    internal KeyTableEntry? Lookup(Keys k)
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
                return item; // Scoped match wins over Global (more specific).
            globalFallback = item;
        }

        return globalFallback;
    }

    /// <summary>
    /// Get all KeyTableEntry entries across all keys (flattened).
    /// </summary>
    internal IEnumerable<KeyTableEntry> AllKeyDictionaryEntries()
    {
        var result = new List<KeyTableEntry>();
        foreach (var entries in KeyDictionary.Values)
            result.AddRange(entries);
        return result;
    }

    /// <summary>
    /// Add to the CommandValue dictionary if not already added.
    /// </summary>
    internal bool AddToKeydefDictionary(KeyTableEntry item)
    {
        CommandValues k = item.KeyDef.Id;
        if (Lookup(k) != null) return false;
        _keydefDictionary.Add(k, item);
        return true;
    }

    /// <summary>
    /// Look for a defined CommandValue.
    /// </summary>
    internal KeyTableEntry? Lookup(CommandValues k)
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

        Stream? cfgFile = null;
        try
        {
            cfgFile = File.Open(KeyConfigType_V1.PathName, FileMode.Open);
        }
        catch (Exception)
        {
            // No key file or error — create one with defaults.
            KeyTableToDefault(true);
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
    internal void KeyTableToDefault(bool save)
    {
        _context.Trace("keyTableToDefault(" + save + ")");
        SetValues(_defaultKeys, KeyTypes.AllKeys, save);
    }

    /// <summary>
    /// Set key values for the commands. If CW messages are present,
    /// UpdateCWText() must be called after this.
    /// </summary>
    internal void SetValues(KeyDefType[] defs, KeyTypes mask, bool wrt)
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
    internal KeyDefType? GetDefaultKey(CommandValues cmdId)
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
            if (currentDefault == null) continue;

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

        // === LEADER KEY DISPATCH ===
        if (_leaderKeyActive)
        {
            _leaderKeyActive = false;
            if (k == Keys.Escape)
            {
                EarconPlayer.LeaderCancelTone();
                Radios.ScreenReaderOutput.Speak("Cancelled", true);
                return true;
            }
            return DoLeaderCommand(k);
        }

        // Check for leader key trigger (Ctrl+J).
        if (k == (Keys.J | Keys.Control))
        {
            _leaderKeyActive = true;
            EarconPlayer.LeaderEnterTone();
            Radios.ScreenReaderOutput.Speak("JJ", true);
            return true;
        }

        // Look in KeyDictionary.
        var kt = Lookup(k);
        if (kt != null)
        {
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
                Radios.ScreenReaderOutput.Speak("No CW messages configured", true);
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
    //  Current keys and help text
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Get the current key table entries (commands, log fields, and CW messages).
    /// </summary>
    internal KeyTableEntry[] CurrentKeys()
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
    internal void HelpText(out KeyDefType[]? keyCommandValues, out KeyDefType[]? keyTextValues,
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
    internal void HelpText(out string[] keyNames, out string[] actions)
    {
        HelpText(out _, out _, out keyNames, out actions);
    }

    // ────────────────────────────────────────────────────────────────
    //  CW message handling
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Update the dictionaries with new CW text messages.
    /// </summary>
    internal void UpdateCWText()
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
    internal void UpdateCWText(KeyDefType[] items)
    {
        var cwText = _context.GetCWText();
        for (int i = 0; i < items.Length; i++)
            cwText[i].Key = items[i].Key;
        UpdateCWText();
    }

    private void SendCWMessage()
    {
        int id = (int)CommandId - KeyCommandConstants.FirstMessageCommandValue;
        var cwText = _context.GetCWText();
        if (id < 0 || id >= cwText.Length)
        {
            Radios.ScreenReaderOutput.Speak("No CW message at this position", true);
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
                Radios.ScreenReaderOutput.Speak("Sending " + label, false);
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
                else if (!rig.AdvancedNRHardwareSupported)
                {
                    EarconPlayer.LeaderInvalidTone();
                    Radios.ScreenReaderOutput.Speak("Neural NR not available on this radio");
                }
                else
                    ToggleLeaderDSP("Neural NR",
                        () => rig.NeuralNoiseReduction, v => rig.NeuralNoiseReduction = v);
                break;
            case Keys.S:
                if (rig == null)
                    LeaderNoRadio();
                else if (!rig.AdvancedNRHardwareSupported)
                {
                    EarconPlayer.LeaderInvalidTone();
                    Radios.ScreenReaderOutput.Speak("Spectral NR not available on this radio");
                }
                else
                    ToggleLeaderDSP("Spectral NR",
                        () => rig.SpectralNoiseReduction, v => rig.SpectralNoiseReduction = v);
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
                        Radios.ScreenReaderOutput.Speak("Audio Peak Filter is CW only");
                    }
                    else
                        ToggleLeaderDSP("Audio Peak Filter",
                            () => rig.APF, v => rig.APF = v);
                }
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

            // Help
            case Keys.Oem2: // ? key (forward slash)
                LeaderKeyHelp();
                break;
            case Keys.H:
                LeaderKeyHelp();
                break;

            default:
                EarconPlayer.LeaderInvalidTone();
                Radios.ScreenReaderOutput.Speak("Unknown command. Press H for help.", true);
                break;
        }

        return true; // Always consume the key in leader mode
    }

    private void LeaderNoRadio()
    {
        EarconPlayer.LeaderInvalidTone();
        Radios.ScreenReaderOutput.Speak("No radio connected");
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
            Radios.ScreenReaderOutput.Speak(label + " on");
        }
        else
        {
            EarconPlayer.FeatureOffTone();
            Radios.ScreenReaderOutput.Speak(label + " off");
        }
    }

    private void ToggleTuneDebounce()
    {
        var mainWindow = _context.GetMainWindow();
        var config = mainWindow?.CurrentAudioConfig;
        if (config == null)
        {
            EarconPlayer.LeaderInvalidTone();
            Radios.ScreenReaderOutput.Speak("No audio configuration loaded");
            return;
        }

        config.TuneDebounceEnabled = !config.TuneDebounceEnabled;
        if (config.TuneDebounceEnabled)
        {
            EarconPlayer.FeatureOnTone();
            Radios.ScreenReaderOutput.Speak("Tuning debounce on");
        }
        else
        {
            EarconPlayer.FeatureOffTone();
            Radios.ScreenReaderOutput.Speak("Tuning debounce off");
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
            Radios.ScreenReaderOutput.Speak("No radio connected");
            return;
        }
        int low = rig.FilterLow;
        int high = rig.FilterHigh;
        int width = high - low;
        string widthKHz = (width / 1000.0).ToString("F1");
        Radios.ScreenReaderOutput.Speak($"RX filter {low} to {high}, {widthKHz} kilohertz");
    }

    private void SpeakTXFilterWidth()
    {
        var rig = _context.GetRigControl();
        if (rig == null)
        {
            Radios.ScreenReaderOutput.Speak("No radio connected");
            return;
        }
        int low = rig.TXFilterLow;
        int high = rig.TXFilterHigh;
        int width = high - low;
        string widthKHz = (width / 1000.0).ToString("F1");
        Radios.ScreenReaderOutput.Speak($"TX filter {low} to {high}, {widthKHz} kilohertz");
    }

    private void LeaderKeyHelp()
    {
        EarconPlayer.LeaderHelpTone();
        var help = "Leader key commands: " +
            "N legacy noise reduction, B noise blanker, W wideband NB, " +
            "R neural NR, S spectral NR, A auto notch, P audio peak filter, " +
            "M memories, D tuning debounce, F speak TX filter, Shift F speak RX filter, L log statistics. " +
            "H for this help. Escape to cancel.";
        Radios.ScreenReaderOutput.Speak(help);
    }
}
