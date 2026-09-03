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

    // ── Narrow stickiness after an unknown leader key (#303, Sprint 37). ──
    // The layer stays armed for H, Shift slash and Escape ONLY. Every other
    // key releases it and travels on untouched, exactly as if the layer had
    // already closed — which, before this, it had.
    //
    // Why the message needed it. "Unknown command. Press H for help." was
    // true one keystroke earlier: by the time it finished speaking the layer
    // had exited, so H did whatever H does in the scope the operator was now
    // standing in. Keeping the layer alive for the three keys that LEAD OUT
    // of it makes plain "H" correct again — which is why the fix makes the
    // sentence SHORTER, not longer.
    //
    // Why narrow and not general: a modal layer you did not ask to stay in is
    // a trap, and for a blind operator "am I still in the layer?" is invisible
    // unless something says so. Two of these keys open help and the third
    // cancels, so there is no way to be held.
    private bool _leaderHelpArmed;

    // ── Value sub-layer state (Sprint 37 Track C, #305 — pan was the first
    // consumer, #304; Sprint 44 Track I put the audio layer (#514) and the
    // filter layer (#516) on it too, and retired the hand-rolled volume
    // mode that lived beside it). The ENGINE (Radios.ValueSubLayer) owns
    // the pattern's decisions — exits, cancel-restores, words-or-numbers
    // under verbosity, the coalesced move speech; this class only builds
    // definitions, routes keys to the live layer and wires the earcons.
    // Null when no layer is live. ──
    private Radios.ValueSubLayer? _valueLayer;

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
            new(CommandValues.SpeakContextHelp, KeyTypes.Command, SpeakContextHelpHandler,
                "Explain the focused control", "Explain this", false, FunctionGroups.Help, KeyScope.Global)
                { Keywords = new[] { "help", "explain", "context", "what", "this", "describe", "detail" }, ShortActionLabel = "explain this control" },

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
                { Keywords = new[] { "s meter", "signal", "strength", "dbm", "s-units", "units",
                                     "unit", "meter", "toggle", "switch", "change" },
                  ShortActionLabel = "switch S meter units" },
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
            // Ctrl+M shows the panel and nothing else. It used to ALSO switch
            // meter tones on, which is why this once read "toggle meter tones"
            // — see #126. The tone switch is Toggle Meter Tones, on Ctrl+J
            // then T, and stays where it is.
            new(CommandValues.ToggleMeters, KeyTypes.Command, ToggleMetersHandler,
                "Show or hide the meters panel", "Meters Panel", false, FunctionGroups.General, KeyScope.Global)
                // "sonification" deliberately does NOT appear here. It is what
                // the TONES command does, and leaving it on the PANEL command
                // sent anyone searching for the sound to the settings screen —
                // re-blurring in Command Finder the exact split #126 made in
                // the key map.
                { Keywords = new[] { "meter", "meters", "panel", "show", "hide", "configure", "settings", "s-meter", "alc", "swr" }, ShortActionLabel = "show meters panel" },

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
            // Sprint 32: the description and keywords follow Track H's rebuild
            // of this command from one stored string into a ten-deep history.
            // "Repeat the last spoken message" gave an operator no reason to
            // press it twice, which hid the entire new feature behind an
            // accurate-sounding sentence — the description-drift defect in its
            // purest form. Keywords gain the words someone would actually
            // search with once they know there is a history to walk.
            new(CommandValues.RepeatLastMessage, KeyTypes.Command, RepeatLastMessageHandler,
                "Repeat recent messages, pressing again for earlier ones", "Repeat Last Message", false, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "repeat", "last", "message", "messages", "speech", "again",
                                     "history", "recent", "earlier", "back", "previous", "missed",
                                     "said", "heard" },
                  ShortActionLabel = "repeat last message" },
            // Sprint 33 Track F (#153). The CW twin of the line above, and a
            // separate history on purpose: an operator running with speech off
            // and CW notifications on has CW to walk back through and no speech.
            new(CommandValues.RepeatLastCw, KeyTypes.Command, RepeatLastCwHandler,
                "Re-send recent CW notifications — press again for earlier ones", "Repeat Last CW", false, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "repeat", "cw", "morse", "last", "again", "history",
                                     "recent", "earlier", "back", "previous", "missed",
                                     "resend", "slice", "census", "code" },
                  ShortActionLabel = "repeat last CW" },
            // 2026-08-31 (#433). The other direction, on the ADJACENT key so the
            // pair reads left-to-right as back-then-forward. Overshooting used
            // to cost up to nine more presses to wrap round.
            new(CommandValues.RepeatNextMessage, KeyTypes.Command, RepeatNextMessageHandler,
                "Step forward through recent messages, toward the newest", "Repeat Next Message", false, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "repeat", "next", "forward", "newer", "message", "messages",
                                     "speech", "history", "recent", "later", "overshot",
                                     "ahead", "said", "heard" },
                  ShortActionLabel = "step forward through messages" },
            // 2026-08-31 (#433), Don's request by way of Noel. ONE generic copy
            // rather than a copy button on every report: a new report then gets
            // clipboard support for free, and nobody has to remember to add it.
            new(CommandValues.CopyRecentMessage, KeyTypes.Command, CopyRecentMessageHandler,
                "Copy what was just spoken to the clipboard", "Copy Spoken Message", false, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "copy", "clipboard", "paste", "spoken", "speech", "message",
                                     "report", "text", "log", "save", "share", "send",
                                     "transcript", "history" },
                  ShortActionLabel = "copy spoken message" },
            // Sprint 36 Track F (#269). "Which build are you on?" is the first
            // question of every tester conversation, and until now the only
            // answer was Help, About — a dialog you have to leave what you are
            // doing to open. The chord makes a report self-identifying.
            new(CommandValues.SpeakVersion, KeyTypes.Command, SpeakVersionHandler,
                "Speak the version and build date of this copy", "Speak Version", false, FunctionGroups.General, KeyScope.Global)
                { Keywords = new[] { "version", "build", "which", "number", "release", "debug",
                                     "nightly", "date", "built", "tester", "report", "identify",
                                     "about", "copy", "running", "installed", "update", "updated" },
                  ShortActionLabel = "speak version" },

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
                "Release every slice except the one you are on, back to one slice", "Release All Extra Slices", false, FunctionGroups.Audio, KeyScope.Radio)
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

    /// <summary>
    /// The element the current key event ORIGINATED from, stashed by the
    /// dialog-window routing handlers for the duration of one dispatch.
    ///
    /// This closes the #91 focus-boundary question. The Ctrl+F1 binding is a
    /// WinForms Keys value, and "what has focus" has more than one answer
    /// across the WinForms/WPF boundary: Keyboard.FocusedElement is
    /// per-thread input state that can be null (focus on a WinForms surface)
    /// or stale in principle. The keystroke's own KeyEventArgs.OriginalSource
    /// is the one answer that cannot be wrong — it IS the element the key was
    /// delivered to. The dialog routing path has that event in hand and
    /// stashes it here; the main-window ProcessCmdKey path never sees a WPF
    /// event, so there Keyboard.FocusedElement remains the best available
    /// answer and stays as the fallback.
    ///
    /// FOR THE RECORD (2026-08-19, from the 2026-08-18 session traces): the
    /// boundary was never the live defect. The 21:08 traces show
    /// DispatchFromDialogWindow firing with Keyboard.FocusedElement correctly
    /// on a Settings TextBox and the walk running to the dialog root — the
    /// three "no extra explanation" reports were presses on a TextBox that
    /// genuinely carried no HelpText. The 21:41 session, never examined,
    /// shows the SAME build finding and speaking the radio-name box's text on
    /// the first walk step. The defect was coverage, not focus resolution.
    /// OriginalSource-first is kept anyway because it is immune to the
    /// staleness class by construction, not by luck.
    /// </summary>
    private static System.Windows.DependencyObject? _dispatchOriginalSource;

    /// <summary>
    /// Ctrl+F1: explain the control that has focus.
    ///
    /// Speaks the focused element's JJFlexHelp.Text — the on-demand
    /// explanation channel — with AutomationProperties.HelpText as a second
    /// source at each step, so a control carrying only a short focus-time
    /// hint still answers the key. See JJFlexHelp for why the custom property
    /// exists (the #91 defect: NVDA reads UIA HelpText aloud as the control's
    /// description on every focus change, so parking long explanations there
    /// silenced nothing).
    ///
    /// F1 keeps opening the help file, which is the Windows convention;
    /// Ctrl+F1 is the usual context-sensitive companion. Noel's steer: "usually
    /// f1 brings up help file, ctrl+f1 often will do context sensitive".
    ///
    /// Interrupts deliberately. This is a question the operator just asked, and
    /// the answer supersedes whatever was being said when they asked it.
    /// </summary>
    private void SpeakContextHelpHandler()
    {
        // Prefer the keystroke's own origin over Keyboard.FocusedElement —
        // see _dispatchOriginalSource for the boundary reasoning.
        var focused = _dispatchOriginalSource
                      ?? System.Windows.Input.Keyboard.FocusedElement
                         as System.Windows.DependencyObject;

        JJTrace.Tracing.TraceLine(
            "SpeakContextHelp: origin=" + (_dispatchOriginalSource?.GetType().FullName ?? "null")
            + " keyboardFocus=" + (System.Windows.Input.Keyboard.FocusedElement?.GetType().FullName ?? "null"),
            System.Diagnostics.TraceLevel.Info);

        // Walk up: focus often sits on an inner part (a ListBoxItem, a TextBox
        // inside a composite) while the explanation belongs to the control the
        // operator would say they are "on". JJFlexHelp owns the walk,
        // including the popup boundary a ComboBox dropdown introduces.
        string? help = JJFlexHelp.FindExplanation(
            focused,
            step => JJTrace.Tracing.TraceLine(
                "SpeakContextHelp: " + step, System.Diagnostics.TraceLevel.Info));

        if (!string.IsNullOrWhiteSpace(help))
        {
            Radios.ScreenReaderOutput.Speak(
                help, Radios.Speech.SpeechIntent.Interrupt, Radios.VerbosityLevel.Critical);
            return;
        }

        // Say so rather than doing nothing. A key that is silent half the time
        // teaches the operator it is broken; a key that says "nothing here"
        // teaches them where the explanations are.
        Radios.ScreenReaderOutput.Speak(
            Radios.Lexicon.Get("help.context.none_here"),
            Radios.Speech.SpeechIntent.Interrupt, Radios.VerbosityLevel.Critical);
    }

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
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("settings.cw.stopped"), Radios.VerbosityLevel.Terse, false);
        }
        else
        {
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.command_needs_radio"), Radios.VerbosityLevel.Critical, true);
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

        // LATEST, keyed by the gain's own label, so holding the key speaks
        // where it landed rather than every value on the way.
        Radios.ScreenReaderOutput.Speak(
            Radios.Lexicon.Get("audio.gain.output_level", ("label", label), ("newVal", newVal)),
            Radios.Speech.SpeechIntent.Latest,
            Radios.VerbosityLevel.Terse,
            coalesceKey: $"gain:{label}");
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
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("settings.zerobeat.requires_cw"), Radios.VerbosityLevel.Critical, true);
            return;
        }
        rig.CWZeroBeat();
        Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("settings.zerobeat.done"), Radios.VerbosityLevel.Terse, true);
    }

    #endregion

    #region S-Meter / Meter Handlers

    /// <summary>
    /// Ctrl+J, Ctrl+S (and the Operations menu, and Settings): switch the
    /// S-meter between S-units and dBm, for this radio, for good.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A TOGGLE and not a second reading key (#337, replacing #306, ruled by
    /// Noel 2026-08-28 after talking to Don: <i>"I don't think that in talking
    /// to Don that we need to have both speakable, just add the toggle."</i>).
    /// One unit is live at a time and there is no second route to the other —
    /// two ways to ask one meter is duplication, and the deletion of the
    /// forced-dBm reading is as much the point of this change as the binding.
    /// </para>
    /// <para>
    /// The objection #306 was built on — that a mode is a state a blind
    /// operator can be in the wrong one of — is answered rather than ignored:
    /// the switch now says which way it went, it survives a restart, and
    /// Settings, Radios shows it without anyone pressing a key to find out.
    /// </para>
    /// <para>
    /// Ctrl+S because THE CHORD ECHOES THE KEY IT RELATES TO — Ctrl+S reads
    /// the S-meter, Ctrl+J then Ctrl+S changes what Ctrl+S reads in. Ctrl+Shift+S
    /// was the first suggestion and is taken (SpeakStatus), as is most of the
    /// S family.
    /// </para>
    /// </remarks>
    private void SmeterDisplayHandler()
    {
        var rig = _context.GetRigControl();
        if (rig == null)
        {
            // This handler returned SILENTLY on a null rig until #337 gave it
            // a key. A key that does nothing is indistinguishable from a key
            // that is broken, and the same message Ctrl+S gives is the honest
            // one: the missing thing is the radio, not the command.
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.command_needs_radio"),
                Radios.VerbosityLevel.Critical);
            return;
        }
        rig.SmeterInDBM = !rig.SmeterInDBM;
        _context.GetMainWindow()?.SetupOperationsMenu();
        // Sprint 32 Track E, #128. dBm is the "on" end of the switch by the
        // same reading the menu label uses -- it is the non-default, the thing
        // you turned on. The point is that the two ends differ audibly, not
        // which one is philosophically higher.
        EarconPlayer.ToggleTone(rig.SmeterInDBM);
        // Speak the result — this handler was silent when invoked by key,
        // violating no-silent-keystrokes. A stale keymap binding parked on
        // Ctrl+Shift+W dispatched here instead of the Audio Workshop and the
        // only clue was the S-meter quietly "changing units" (2026-08-07
        // live finding). Speech makes any future mis-dispatch self-diagnosing.
        Radios.ScreenReaderOutput.Speak(
            rig.SmeterInDBM
                ? Radios.Lexicon.Get("audio.smeter.in_dbm")
                : Radios.Lexicon.Get("audio.smeter.in_s_units"),
            Radios.VerbosityLevel.Terse, true);
    }

    private void ReadSMeterHandler() => SpeakSMeter();

    /// <summary>
    /// The S-meter readout. THE ONLY ONE — every route to a meter reading
    /// comes through here, so no two of them can ever describe the meter
    /// differently.
    /// </summary>
    /// <remarks>
    /// It took a <c>forceDbm</c> parameter until #337, for the one-shot dBm
    /// chord of #306. With the chord turned into a toggle there is exactly one
    /// live unit and no second way to ask for the other, so the parameter was
    /// removed rather than left as a road nothing drives down.
    /// </remarks>
    private void SpeakSMeter()
    {
        var rig = _context.GetRigControl();
        if (rig == null)
        {
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.command_needs_radio"), Radios.VerbosityLevel.Critical);
            return;
        }
        // Transmit is checked FIRST, and the unit does not change that: while
        // keyed, the S-meter is not describing anyone's signal, so either unit
        // answers with real forward power exactly as Ctrl+S always has.
        // The whole composition moved into SMeterReading.Spoken (#353) so the
        // Home S-meter field could stop rendering its own version. It read its
        // DISPLAY back verbatim, which in dBm produced "S meter -97" and above
        // S9 produced "S meter plus 4". Every branch below is preserved
        // exactly; the words simply live in one place now.
        //
        // Transmit is checked FIRST in there, and the unit does not change it:
        // while keyed the meter is not describing anyone's signal, so either
        // unit answers with real forward power — this read "Power 0" for 174 mW
        // of measured RF before it named a unit.
        //
        // dBm reads RawSMeter, not SMeter: the meter as the radio sent it.
        // SMeter returns dBm too when the mode is on, so the two agree — but
        // RawSMeter says what it means without depending on a mode being read
        // twice, and it answered a permanent zero until #295 fixed the field
        // that shadowed it.
        //
        // The over-S9 excess comes from the one place that knows the rule. It
        // was computed inline here and inline again in the status builder, and
        // the two drifted — this one multiplied by ten, that one by six, so
        // 4 dB over S9 was announced as "S9 plus 40" on one surface and
        // "S9 plus 24" on the other.
        string msg = Radios.SMeterReading.Spoken(
            rig.Transmit,
            Radios.FlexBase.FormatForwardPowerSpoken(rig.ForwardPowerWatts),
            rig.SmeterInDBM,
            rig.RawSMeter,
            (int)rig.SMeter);

        // LATEST: an S-meter reading superseded by a newer one has no value -
        // the operator wants the signal now, not a recital of the last five.
        //
        // QUERY, because this key asks a question - it does not sweep a value
        // (#264, from Don's "I hit ctrl+s and it just lags", 2026-08-26, and
        // measured again at the radio 2026-08-27). Classed as a value, a quick
        // second press was read as sweeping: each press pushed the settle timer
        // out, so hammering the key was SILENT until released, and a repeat of
        // the same reading was then dropped as a duplicate, so on a steady
        // signal the second press said nothing at all.
        //
        // Query says the three things this key needs in one word. A re-press is
        // never a sweep, so it answers straight away instead of waiting out a
        // settle. A newer press never defers the pending answer. And "still S9"
        // is spoken rather than swallowed - on a meter, the repetition IS the
        // information.
        //
        // This carried a repeatWhileHeld flag until 2026-08-27. It was the only
        // caller of it, and Query replaces it outright rather than sitting
        // beside it.
        //
        // ONE COALESCE KEY, and the collapse is a decision (#337). It used to
        // split on the unit - "smeter-dbm" or "smeter" - because #306 let both
        // readings be taken of one signal, and a shared key would have let the
        // second answer silence the first. With one unit live at a time there
        // is nothing to protect the other from, and the honest shape is the
        // one key: this reading, whatever unit it is in.
        //
        // THE SEAM THAT NEEDED CHECKING: press the toggle, then Ctrl+S at
        // once. Two readings in different units now share a key, so a
        // duplicate-drop would swallow the second - which is #264's silence
        // wearing a new coat. It does not, and Query is why: a query is exempt
        // from the duplicate-drop and never has its timer pushed out, so a
        // deliberate press always answers. SMeterUnitToggleTests pins that,
        // including the case where BOTH readings render identically.
        Radios.ScreenReaderOutput.Speak(
            msg,
            Radios.Speech.SpeechIntent.Latest,
            Radios.VerbosityLevel.Terse,
            coalesceKey: "smeter",
            kind: Radios.Speech.SpeechCoalesceKind.Query);
    }

    /// <summary>
    /// Ctrl+Alt+M. The same switch as Ctrl+J then T, so it goes through the
    /// same method.
    /// </summary>
    /// <remarks>
    /// This used to flip <c>MeterToneEngine.Enabled</c> itself and repeat the
    /// announcement inline, which is precisely the drift
    /// <c>MeterToneEngine.ToggleEnabled</c> was written to prevent — and it had
    /// already drifted: this copy spoke without interrupting and earconned
    /// before speaking, while the leader-layer copy interrupted and spoke
    /// first. Two keys for one switch is fine. Two keys that describe the
    /// switch differently is a bug report waiting to be filed.
    /// </remarks>
    private void ToggleMeterTonesHandler() => MeterToneEngine.ToggleEnabled();

    private void CycleMeterPresetHandler()
    {
        MeterToneEngine.CyclePreset();
        Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.meter.preset",
            ("preset", MeterToneEngine.CurrentPreset)), Radios.VerbosityLevel.Terse);
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
        Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.tx_filter.low_moved",
            ("newLow", newLow), ("width", width)), Radios.VerbosityLevel.Terse);
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
        Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.tx_filter.low_moved",
            ("newLow", newLow), ("width", width)), Radios.VerbosityLevel.Terse);
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
        Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.tx_filter.high_moved",
            ("newHigh", newHigh), ("width", width)), Radios.VerbosityLevel.Terse);
    }

    private void TXFilterHighUpHandler()
    {
        var rig = _context.GetRigControl();
        if (rig == null) return;
        int newHigh = Math.Min(10000, rig.TXFilterHigh + 50);
        rig.TXFilterHigh = newHigh;
        EarconPlayer.FilterEdgeMoveTone(false);
        int width = newHigh - rig.TXFilterLow;
        Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.tx_filter.high_moved",
            ("newHigh", newHigh), ("width", width)), Radios.VerbosityLevel.Terse);
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
                Radios.Lexicon.Get("settings.tuning_mode.in_logging_mode"),
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
    /// <summary>
    /// Turn PC audio on or off, and SAY what happened.
    /// </summary>
    /// <remarks>
    /// The context delegate changes the state and records the operator's
    /// choice; it does not speak, and until Sprint 32 Track G neither did
    /// this. The command had no key, so the only way to reach it was the
    /// Command Finder — where an operator chose "PC audio on/off", heard
    /// nothing at all, and had no way to learn whether the audio they could
    /// not hear was off on purpose. Task #130 puts a chord on this command
    /// (Ctrl+J, Ctrl+A then; Ctrl+J, Ctrl+P since Sprint 44), which would have
    /// shipped the same silence.
    ///
    /// <para>The wording is lifted from the Radio menu's own PC Audio item so
    /// the two surfaces speak with one vocabulary, INCLUDING its third case:
    /// the state is read back off the radio rather than assumed from the
    /// request, because turning PC audio on can fail — no usable sound
    /// device — and announcing the wish rather than the outcome is how you get
    /// "PC audio on" while nothing plays.</para>
    /// </remarks>
    private void PCAudioHandler()
    {
        var rig = _context.GetRigControl();
        if (rig == null)
        {
            Radios.ScreenReaderOutput.SpeakNoRadioConnected(
                Radios.Lexicon.Get("settings.pc_audio.action_label"));
            return;
        }

        bool wanted = !rig.PCAudio;
        // PCAudioToggle (globals.vb) is the state change and plays the tone
        // itself, from the radio's read-back. No tone here: until the #128
        // sweep audit (2026-08-21) this handler ALSO toned, so the chord road
        // sounded twice per press while the menu and Settings roads sounded
        // once — the exact inconsistency the sweep exists to remove. The tone
        // lives at the state change, not at the control.
        _context.PCAudioToggle();

        bool actual = rig.PCAudio;
        // Keyed pc-audio (#503): this outcome covers any earlier PC-audio
        // line still unheard, and is itself covered only by the next one.
        Radios.ScreenReaderOutput.Speak(
            actual ? Radios.Lexicon.Get("audio.pc_audio.on")
            : wanted ? Radios.Lexicon.Get("audio.pc_audio.could_not_start")
            : Radios.Lexicon.Get("audio.pc_audio.off"),
            Radios.Speech.SpeechIntent.Queue,
            Radios.VerbosityLevel.Terse,
            subject: Radios.Speech.SpeechSubject.PcAudio);
    }
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
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("settings.atu.none_on_radio"), Radios.VerbosityLevel.Critical);
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
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.command_needs_radio"), Radios.VerbosityLevel.Critical, true);
            return;
        }
        bool newMute = !rig.SliceMute;
        rig.SliceMute = newMute;
        if (newMute) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
        string letter = rig.VFOToLetter(rig.RXVFO);
        Radios.ScreenReaderOutput.Speak(
            newMute
                ? Radios.Lexicon.Get("audio.mute.slice_muted", ("letter", letter))
                : Radios.Lexicon.Get("audio.mute.slice_unmuted", ("letter", letter)),
            Radios.VerbosityLevel.Terse, true);
    }

    private void MuteAllSlicesHandler()
    {
        var rig = _context.GetRigControl();
        if (rig == null)
        {
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.command_needs_radio"), Radios.VerbosityLevel.Critical, true);
            return;
        }
        bool target = !rig.AllMySlicesMuted;
        rig.SetAllMySlicesMute(target);
        if (target) EarconPlayer.MuteAllOnTone();
        else EarconPlayer.MuteAllOffTone();
        Radios.ScreenReaderOutput.Speak(
            target
                ? Radios.Lexicon.Get("audio.mute.all_slices_muted")
                : Radios.Lexicon.Get("audio.mute.all_slices_unmuted"),
            Radios.VerbosityLevel.Terse, true);
    }

    private void ReleaseAllExtraSlicesHandler()
    {
        var rig = _context.GetRigControl();
        if (rig == null)
        {
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.command_needs_radio"), Radios.VerbosityLevel.Critical, true);
            return;
        }
        int before = rig.MyNumSlices;
        if (before <= 1)
        {
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("settings.slice.only_one_active"), Radios.VerbosityLevel.Terse, true);
            return;
        }
        if (rig.ReleaseAllExtraSlices())
        {
            EarconPlayer.MuteAllOnTone();
            int removed = before - 1;
            string keptLetter = rig.VFOToLetter(rig.RXVFO);
            Radios.ScreenReaderOutput.Speak(
                removed == 1
                    ? Radios.Lexicon.Get("settings.slice.released_extras_one",
                        ("removed", removed), ("keptLetter", keptLetter))
                    : Radios.Lexicon.Get("settings.slice.released_extras_many",
                        ("removed", removed), ("keptLetter", keptLetter)),
                Radios.VerbosityLevel.Terse, true);
        }
    }

    private void ShowStatusDialogHandler()
    {
        var rig = _context.GetRigControl();
        var mw = _context.GetMainWindow();
        // #320: tuning mode and active preset, from the same two accessors
        // SpeakStatusHandler reads. Handed over as suppliers because the
        // dialog rebuilds itself every five seconds and both values move
        // while it is open.
        var dialog = new Dialogs.StatusDialog
        {
            Rig = rig,
            TuningModeStatus = mw == null ? null : mw.GetTuningModeStatus,
            FilterPresetStatus = mw == null ? null : mw.GetFilterPresetStatus,
        };
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
                : Radios.Lexicon.Get("audio.mic.no_reading_yet");
            var status = mw?.GetPttStatusText();
            string text = status != null ? verdict + ". " + status : verdict;
            Radios.ScreenReaderOutput.Speak(text, Radios.VerbosityLevel.Terse, true);
            return;
        }

        Radios.ScreenReaderOutput.Speak(
            mw?.GetPttStatusText() ?? Radios.Lexicon.Get("settings.ptt.receiving"),
            Radios.VerbosityLevel.Terse, true);
    }

    /// <summary>
    /// Format a mic-audio reading for speech. The wording, the figures, and
    /// the room observation all come from <see cref="MicAudioReport"/> so this
    /// key and the two reading fields can never drift apart.
    /// </summary>
    private string FormatMicVerdict(float peakDb, bool lastTransmit = false)
    {
        return MicAudioReport.Compose(
            _context.GetRigControl(),
            lastTransmit
                ? Radios.Lexicon.Get("audio.mic.label_last_transmit")
                : Radios.Lexicon.Get("audio.mic.label_live"),
            peakDb,
            live: !lastTransmit);
    }

    // #70: this used to read a single stored string, which was one press too
    // shallow to be useful — anything spoken between hearing something and
    // asking for it again had already overwritten it. The walk now goes back
    // through the last ten utterances, and pressing again promptly steps
    // further back. The whole mechanism lives in ScreenReaderOutput, where the
    // history is recorded; this stays a one-liner so the key and the memory
    // cannot drift apart.
    private void RepeatLastMessageHandler()
    {
        if (!Radios.ScreenReaderOutput.RepeatRecent())
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("settings.repeat.no_previous_message"));
    }

    // #153. Mirrors the speech walk one modifier away, and the empty case
    // SPEAKS rather than sending anything in CW: an operator who presses this
    // and hears nothing cannot tell "no history" from "the key is dead", and
    // answering a question about CW in CW would be a poor joke when the answer
    // is that there is no CW to give.
    private void RepeatLastCwHandler()
    {
        if (!Radios.ScreenReaderOutput.RepeatRecentCw())
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("settings.repeat.no_recent_cw"), Radios.VerbosityLevel.Critical);
    }

    // #433. The forward twin of RepeatLastMessageHandler, and it shares its
    // empty-case sentence: "nothing recorded yet" is the same fact whichever
    // way you were walking.
    private void RepeatNextMessageHandler()
    {
        if (!Radios.ScreenReaderOutput.RepeatRecentNewer())
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("settings.repeat.no_previous_message"));
    }

    // #433. Copies the message the walk is sitting on. CurrentRecent() does not
    // move the cursor and refreshes the walk's clock, so you can walk back,
    // think, and copy the one you actually heard rather than whatever the
    // six-second timeout reset you to.
    //
    // Spoken confirmation is CRITICAL and deliberate: a clipboard is invisible.
    // A copy that says nothing is indistinguishable from a key that did
    // nothing, and the operator would only find out at the paste.
    private void CopyRecentMessageHandler()
    {
        string text = Radios.ScreenReaderOutput.CurrentRecent();
        if (string.IsNullOrEmpty(text))
        {
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("settings.repeat.no_previous_message"));
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(text);
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("settings.repeat.copied"), Radios.VerbosityLevel.Critical);
        }
        catch (Exception ex)
        {
            // The clipboard is genuinely refusable - another process can hold
            // it open - so this is a real outcome, not a defensive shrug, and
            // it must be SAID rather than swallowed.
            JJTrace.Tracing.TraceLine("CopyRecentMessage: clipboard refused: " + ex.Message,
                System.Diagnostics.TraceLevel.Warning);
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("settings.repeat.copy_failed"), Radios.VerbosityLevel.Critical);
        }
    }

    // #269. Reads DiagnosticSnapshot.BuildStamp — the same assembler the About
    // page uses — rather than reaching for the assembly itself, because a
    // second version-reporting path is how About and the spoken answer end up
    // disagreeing about what is running.
    //
    // NO COMMIT HASH, deliberately, and this departs from the task's own
    // suggested wording. feedback_numeric_identifiers is explicit: say
    // "version 4.1.16.3", not "commit 9410f7dc" — Eloquence drops into NATO
    // phonetics for hex and the operator has to ask for repeats. The SHA is
    // still in Help, About and in every diagnostic capture, which is where hex
    // is the authoritative identifier and belongs. This chord exists to be read
    // back down a phone.
    //
    // Critical verbosity: someone who asks which build they are on wants the
    // answer even with speech turned down.
    private void SpeakVersionHandler()
    {
        var build = Radios.DiagnosticSnapshot.BuildStamp;

        string text;
        if (string.IsNullOrEmpty(build.Version))
        {
            text = Radios.Lexicon.Get("settings.build.unavailable");
        }
        else
        {
            string configuration = string.IsNullOrEmpty(build.Configuration)
                ? "unknown build type" : build.Configuration;
            text = string.IsNullOrEmpty(build.DateSpoken)
                ? Radios.Lexicon.Get("settings.build.spoken_no_date",
                    ("version", build.Version), ("configuration", configuration))
                : Radios.Lexicon.Get("settings.build.spoken",
                    ("version", build.Version), ("configuration", configuration),
                    ("date", build.DateSpoken));
        }

        _context.Trace("Leader:speak version — " + text);
        Radios.ScreenReaderOutput.Speak(text, Radios.VerbosityLevel.Critical);
    }

    private void CycleVerbosityHandler()
    {
        var newLevel = Radios.ScreenReaderOutput.CycleVerbosity();
        // Persist immediately
        SaveVerbositySetting();
    }

    // Ctrl+J then T. The wording and the earcons live on the engine so this and
    // the Meter Tones menu item say the same thing (Sprint 32 Track B).
    private void ToggleMeterTonesGlobalHandler() => MeterToneEngine.ToggleEnabled();

    private void ToggleEarconMute()
    {
        EarconPlayer.EarconsEnabled = !EarconPlayer.EarconsEnabled;
        // Sprint 32 Track E, #128, and this one is asymmetric on purpose.
        // Turning alert sounds ON gets a tone -- the tone IS the proof the
        // thing you just switched on is working, which no other toggle can
        // offer. Turning them off gets silence, because playing an alert sound
        // to confirm that alert sounds are off would be the joke it sounds
        // like. The speech below covers the off direction.
        if (EarconPlayer.EarconsEnabled) EarconPlayer.ToggleTone(true);
        Radios.ScreenReaderOutput.Speak(
            EarconPlayer.EarconsEnabled
                ? Radios.Lexicon.Get("earcon.alerts_on")
                : Radios.Lexicon.Get("earcon.alerts_off"),
            Radios.VerbosityLevel.Terse, true);
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
    /// How long the verbosity save waits for the operator to stop cycling.
    /// </summary>
    ///
    /// Long enough to ride out a run of presses - the cycle is three-way, so
    /// getting from Chatty to Off is two taps and changing your mind is a
    /// third - and short enough that closing the application straight after a
    /// press still lands the write.
    private const int VerbositySaveDebounceMs = 1500;

    private static System.Threading.Timer? _verbositySaveTimer;
    private static readonly object _verbositySaveLock = new object();

    /// <summary>
    /// Persist current verbosity to audio config, once the operator has
    /// settled on a level.
    ///
    /// Debounced and off the UI thread. Cycling is a three-way toggle, so a
    /// deliberate change is often several presses in a second; saving on each
    /// one meant a read-modify-write of the whole audio config per keystroke,
    /// synchronously, while the operator was still pressing the key.
    ///
    /// The write itself is a full CaptureFromEngine so the rest of the audio
    /// config is not clobbered by a partial save.
    /// </summary>
    private void SaveVerbositySetting()
    {
        var configDir = _context.GetConfigDirectory?.Invoke();
        if (configDir == null)
        {
            // Silent failure here is how a setting appears not to persist, so
            // say so in the trace rather than just returning.
            JJTrace.Tracing.TraceLine(
                "SaveVerbositySetting: no config directory - verbosity NOT saved.",
                System.Diagnostics.TraceLevel.Warning);
            return;
        }

        lock (_verbositySaveLock)
        {
            _verbositySaveTimer?.Dispose();
            _verbositySaveTimer = new System.Threading.Timer(
                _ => FlushVerbositySave(configDir),
                null,
                VerbositySaveDebounceMs,
                System.Threading.Timeout.Infinite);
        }
    }

    private static void FlushVerbositySave(string configDir)
    {
        try
        {
            var config = AudioOutputConfig.Load(configDir);
            config.CaptureFromEngine();
            config.Save(configDir);
            JJTrace.Tracing.TraceLine(
                $"Verbosity saved: {Radios.ScreenReaderOutput.CurrentVerbosity}",
                System.Diagnostics.TraceLevel.Verbose);
        }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine(
                $"SaveVerbositySetting failed: {ex.Message}",
                System.Diagnostics.TraceLevel.Warning);
        }
    }

    #endregion

    // ────────────────────────────────────────────────────────────────
    //  Why a command has no key — the unbound roster (task #130)
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Why a registry command ships with no default key.
    /// </summary>
    /// <remarks>
    /// Twenty-nine commands defaulted to <c>Keys.None</c> and only two carried
    /// any explanation, so nothing in the source told "menu-only on purpose"
    /// apart from "nobody ever assigned a key" — and the two want opposite
    /// treatment. Sprint 32 Track G annotated all of them. The distinction is
    /// the whole deliverable: it is what makes a future pass possible, and it
    /// is why this is an enum rather than a paragraph.
    /// </remarks>
    public enum UnboundReason
    {
        /// <summary>Reachable on a Ctrl+J leader chord. Bound, just not here —
        /// leader chords live in DoLeaderCommand, not in the registry, so the
        /// registry row honestly reads "no key".</summary>
        LeaderLayer,

        /// <summary>Reachable from a menu or another dialog, deliberately. A
        /// chord would be a second door to a room with one.</summary>
        MenuOrDialog,

        /// <summary>Command Finder and Hotkey Editor only, on purpose. Rare or
        /// slow enough that a reserved chord would cost more than it returns,
        /// and anyone who wants one can bind it.</summary>
        CommandFinderOnly,

        /// <summary>The slot is deliberately held EMPTY to protect a design
        /// decision. Do not fill it without reading why.</summary>
        Reserved,

        /// <summary>Had a chord on paper; something else consumed it first, so
        /// the chord never arrived. Now honestly unbound rather than
        /// pretending.</summary>
        Shadowed,

        /// <summary>The feature is gone. The command survives in the registry
        /// and answers with a "no longer supported" message. Binding a key to
        /// it would be binding a key to an apology.</summary>
        Retired,

        /// <summary>A default-key row with no command behind it at all — no
        /// KeyTable entry, so no handler, no description, and nothing in the
        /// Command Finder. Dead weight, kept only because removing rows from
        /// this table interacts with saved-default reconciliation.</summary>
        Vestigial,

        /// <summary>Nobody ever assigned one and nothing says why not. THE
        /// CANDIDATE STATE — if a command sits here, the next pass should
        /// either give it a key or move it to one of the reasons above.</summary>
        Unassigned,
    }

    /// <summary>One command's explanation for having no key.</summary>
    public sealed record UnboundNote(UnboundReason Reason, string Detail);

    /// <summary>
    /// Every command that defaults to <c>Keys.None</c>, and why.
    /// </summary>
    /// <remarks>
    /// Kept beside <see cref="_defaultKeys"/> and CHECKED AGAINST IT at startup
    /// by ValidateKeyBindings, so a new unbound command cannot slip in without
    /// an explanation and an old one cannot linger here after being bound.
    /// A table nobody verifies rots into a description-drift defect within two
    /// sprints; this one fails loudly in the trace instead.
    /// </remarks>
    private static readonly Dictionary<CommandValues, UnboundNote> _unboundNotes = new()
    {
        // ── Reachable on the leader layer. Bound in every sense that matters
        //    to an operator; the registry row is just not where it lives. ──
        [CommandValues.ShowMemory] = new(UnboundReason.LeaderLayer,
            "Ctrl+J, M opens the memories dialog."),
        [CommandValues.LogStats] = new(UnboundReason.LeaderLayer,
            "Ctrl+J, L speaks log statistics. NOTE for whoever owns the logging menu: "
            + "the Logging menu's own 'Log Statistics' item is still an AddNotImplemented "
            + "stub that answers 'not yet implemented in this version' while this chord "
            + "has worked for sprints. Reported by Sprint 32 Track G, not fixed here — "
            + "the logging menu is not this track's file."),
        // Sprint 44 Track J: plain F became the filter layer's door under the
        // four-tier grammar (#512, #515), and this readout moves inside the
        // layer, on its S key, with Track I. Until the layer lands the menu
        // is the only route, so the reason is honest for this build.
        [CommandValues.SpeakTXFilter] = new(UnboundReason.MenuOrDialog,
            "The Radio menu's TX Filter submenu has a 'Read TX Filter' item, which reaches "
            + "the same answer by its own inline route rather than through this command. The "
            + "JJ key chord this once had, plain F, is the filter layer's door now, and the "
            + "readout lives inside that layer."),
        [CommandValues.ToggleMeterTonesGlobal] = new(UnboundReason.LeaderLayer,
            "Ctrl+J, T toggles meter tones."),
        [CommandValues.RepeatLastCw] = new(UnboundReason.LeaderLayer,
            "Ctrl+J, E re-sends recent CW notifications — E for echo. The flat chord "
            + "that would have mirrored the speech repeat on Ctrl+F4 is Ctrl+Shift+F4, "
            + "and that already focuses the CW send text box."),
        // #433. Leader-layer for the same reason: a global Ctrl+letter for
        // "copy the last thing said" would take a chord out of every dialog's
        // hands for a command used occasionally and deliberately.
        [CommandValues.CopyRecentMessage] = new(UnboundReason.LeaderLayer,
            "Ctrl+J, Ctrl+C copies what was just spoken. Ctrl+C alone belongs to "
            + "whatever control has focus, and taking it globally would break copying "
            + "out of every text box in the application."),
        [CommandValues.SpeakVersion] = new(UnboundReason.LeaderLayer,
            "Ctrl+J, Alt+V speaks the version, build type and build date. Alt rather than "
            + "bare V because V is volume mode, and V is the letter you reach for when you "
            + "want the Version. Help, About carries the same facts in full, including the "
            + "commit."),
        [CommandValues.RemoteAudio] = new(UnboundReason.LeaderLayer,
            "Ctrl+J, Ctrl+P turns PC audio on and off — P for PC, ruled in Sprint 44; it was "
            + "Ctrl+A from Sprint 32 Track G until then. Noel named this one specifically: "
            + "'No hotkey for PC audio on and off available that I know of, you have to do it "
            + "in the menu.' Also on the Radio menu under Audio."),
        // Task #337 made this the unit toggle. The citation is HERE and not in
        // the Detail string below: KeyManifest writes every Detail verbatim
        // into the key list the operator exports from the Hotkey Editor, so a
        // task number in it lands in a file the operator reads and may share,
        // pointing into a register no reader can open. See task #389.
        [CommandValues.SmeterDBM] = new(UnboundReason.LeaderLayer,
            "Ctrl+J, Ctrl+S switches the S-meter between S-units and dBm, and the choice is "
            + "remembered for that radio. The chord echoes the key it changes: Ctrl+S "
            + "reads the meter, Ctrl+J then Ctrl+S changes what it reads in. Also on the "
            + "Operations menu, and on the Radios tab in Settings, which is where the answer "
            + "can be seen without pressing anything. Carried a flat Keys.None and a "
            + "'Command Finder only' note until that shipped, which is how a toggle Noel "
            + "remembered having became one he could not reach."),

        // ── Menu or dialog, deliberately. These open something; the thing they
        //    open is where the operator already goes. ──
        [CommandValues.AudioSetup] = new(UnboundReason.MenuOrDialog,
            "Radio menu, Audio, Audio Devices. A one-page picker you visit when you change "
            + "hardware, not during a QSO."),
        [CommandValues.ATUMemories] = new(UnboundReason.MenuOrDialog,
            "Radio menu, ATU Memories."),
        [CommandValues.Reboot] = new(UnboundReason.MenuOrDialog,
            "Radio menu, Reboot Radio (and Settings, Radio Setup, step 7). Deliberately NOT "
            + "on a chord: it interrupts everyone on a MultiFlex radio, and the confirmation "
            + "that names the other connected stations is the point of the slow route."),
        [CommandValues.TXControls] = new(UnboundReason.MenuOrDialog,
            "Opens the transmit-controls dialog. Its individual controls are what an operator "
            + "reaches for mid-QSO, and those have their own keys."),
        [CommandValues.LogFileName] = new(UnboundReason.MenuOrDialog,
            "Log file name is set from the logging surfaces, not mid-contact."),
        [CommandValues.LogMode] = new(UnboundReason.MenuOrDialog,
            "A log FIELD jump. The full log form is where these are filled in, and Tab reaches "
            + "them there; the lettered Alt chords cover the fields worth jumping to."),
        [CommandValues.LogRig] = new(UnboundReason.MenuOrDialog,
            "A log FIELD jump, as LogMode. Rig is usually constant for a session."),
        [CommandValues.LogAnt] = new(UnboundReason.MenuOrDialog,
            "A log FIELD jump, as LogMode. Antenna is usually constant for a session."),

        // ── Command Finder and Hotkey Editor only, on purpose. ──
        [CommandValues.StartAudioCheck] = new(UnboundReason.CommandFinderOnly,
            "Starts the Audio Check. Ctrl+Enter does it from inside the Audio Workshop, which "
            + "is where you are when you want it; a global chord that keys the transmitter "
            + "from anywhere is not something to hand out by default."),
        [CommandValues.GatherDebug] = new(UnboundReason.CommandFinderOnly,
            "Collects a debug snapshot. Settings, Diagnostics is the fuller route now — it "
            + "records sessions and builds problem-report bundles — so this stays available "
            + "without spending a chord."),
        [CommandValues.StartScan] = new(UnboundReason.CommandFinderOnly,
            "Starts or stops a scan. Ctrl+Z stops one and Ctrl+Shift+F2 resumes, which are the "
            + "two an operator needs in a hurry."),
        [CommandValues.SavedScan] = new(UnboundReason.CommandFinderOnly,
            "Runs a saved scan by name, which means choosing one — a picking act, not a chord."),

        // ── Reserved. The empty slot IS the decision. ──
        [CommandValues.AudioGainUp] = new(UnboundReason.Reserved,
            "One of six audio-gain slots freed in Sprint 29 Track F when levels moved into the "
            + "Audio expander (Ctrl+Shift+U) and volume mode (Ctrl+J, V). Left unbound and "
            + "RESERVED per the 2026-05-02 ACK, option 2, so a later sprint cannot claim the "
            + "chord without meeting the argument: audio levels are not real-time controls "
            + "during a QSO, and values belong in fields, not on toggle keys."),
        [CommandValues.AudioGainDown] = new(UnboundReason.Reserved,
            "Reserved with AudioGainUp — see that note."),
        [CommandValues.HeadphonesUp] = new(UnboundReason.Reserved,
            "Reserved with AudioGainUp. The level itself rides on Ctrl+J, V, H."),
        [CommandValues.HeadphonesDown] = new(UnboundReason.Reserved,
            "Reserved with AudioGainUp. The level itself rides on Ctrl+J, V, H."),
        [CommandValues.LineoutUp] = new(UnboundReason.Reserved,
            "Reserved with AudioGainUp. The level itself rides on Ctrl+J, V, L."),
        [CommandValues.LineoutDown] = new(UnboundReason.Reserved,
            "Reserved with AudioGainUp. The level itself rides on Ctrl+J, V, L."),

        // ── Shadowed: had a chord that never arrived. ──
        [CommandValues.MemoryScan] = new(UnboundReason.Shadowed,
            "Carried Ctrl+Shift+M for sprints and never once received it — the hard-wired "
            + "ToggleUIMode meta-command consumed the chord at window level first (QB Track H "
            + "shadow sweep, 2026-08-07). ToggleTuningMode owns Ctrl+Shift+M in the registry "
            + "now, so this is honestly unbound instead of falsely bound."),
        [CommandValues.SpeakFrequency] = new(UnboundReason.Shadowed,
            "Claimed Ctrl+Shift+F by design intent from Sprint 15 and never received it — "
            + "ToggleFreqReadout consumed it at window level (same 2026-08-07 sweep). The F "
            + "key on the Frequency field speaks the frequency, which is the working route."),

        // ── Retired: the feature is gone, the command is an apology. ──
        [CommandValues.CycleContinuous] = new(UnboundReason.Retired,
            "Answers 'This feature is no longer supported.' and nothing else. Still listed in "
            + "the Command Finder, which is worth someone's attention — but it must never be "
            + "given a key, and this note exists so nobody reads the blank slot as an "
            + "invitation. Reported by Sprint 32 Track G; removal is not a keyboard change."),
        [CommandValues.ShowMenus] = new(UnboundReason.Retired,
            "Answers 'Not available for this radio.' — a leftover from the multi-brand era "
            + "before the app became Flex-only. Same treatment as CycleContinuous."),

        // ── Vestigial: no command behind the row at all. ──
        [CommandValues.LogForm] = new(UnboundReason.Vestigial,
            "There is NO KeyTable entry for this id, so it has no handler, no description and "
            + "no Command Finder presence — this default-key row points at nothing. Found by "
            + "Sprint 32 Track G while annotating; left in place because deleting rows from "
            + "the default table interacts with saved-default reconciliation, which is a "
            + "config-migration change rather than a keyboard one."),
    };

    /// <summary>
    /// Why this command has no key, or null if it has one (or is not a
    /// registry command). Public so the Keys dialog, the exported key list and
    /// any future audit can show the reason rather than a blank.
    /// </summary>
    public static UnboundNote? GetUnboundNote(CommandValues id) =>
        _unboundNotes.TryGetValue(id, out var note) ? note : null;

    // ────────────────────────────────────────────────────────────────
    //  Default key bindings — scope-aware
    //
    //  Every `Keys.None` row carries a one-line reason tag; the full
    //  explanation lives in _unboundNotes above, and ValidateKeyBindings
    //  checks the two agree at startup.
    // ────────────────────────────────────────────────────────────────

    private readonly KeyDefType[] _defaultKeys =
    {
        // --- Global scope ---
        new(Keys.F1, CommandValues.ShowContextHelp, KeyScope.Global),
        new(Keys.F1 | Keys.Control, CommandValues.SpeakContextHelp, KeyScope.Global),
        new(Keys.F12, CommandValues.StopCW, KeyScope.Global),
        new(Keys.L | Keys.Control, CommandValues.StationLookup, KeyScope.Global),
        new(Keys.None, CommandValues.GatherDebug, KeyScope.Global), // unbound: CommandFinderOnly

        // --- Radio scope ---
        new(Keys.F2, CommandValues.ShowFreq, KeyScope.Radio),
        new(Keys.F | Keys.Control, CommandValues.SetFreq, KeyScope.Radio),
        new(Keys.None, CommandValues.ShowMemory, KeyScope.Radio), // unbound: LeaderLayer — Ctrl+J, M
        new(Keys.None, CommandValues.MemoryScan, KeyScope.Radio), // unbound: Shadowed
        new(Keys.None, CommandValues.SmeterDBM, KeyScope.Radio), // unbound: LeaderLayer — Ctrl+J, Ctrl+S
        new(Keys.S | Keys.Control, CommandValues.ReadSMeter, KeyScope.Radio),
        new(Keys.M | Keys.Control | Keys.Alt, CommandValues.ToggleMeterTones, KeyScope.Radio),
        new(Keys.P | Keys.Control | Keys.Alt, CommandValues.CycleMeterPreset, KeyScope.Radio),
        new(Keys.V | Keys.Control | Keys.Alt, CommandValues.SpeakMeters, KeyScope.Radio),
        new(Keys.None, CommandValues.CycleContinuous, KeyScope.Radio), // unbound: Retired — never bind this
        new(Keys.None, CommandValues.LogForm, KeyScope.Radio), // unbound: Vestigial — no KeyTable entry behind it
        new(Keys.C | Keys.Control | Keys.Shift, CommandValues.ClearRIT, KeyScope.Radio),
        new(Keys.None, CommandValues.StartScan, KeyScope.Radio), // unbound: CommandFinderOnly
        new(Keys.X | Keys.Alt | Keys.Shift, CommandValues.ArCluster, KeyScope.Radio),
        new(Keys.R | Keys.Control | Keys.Alt, CommandValues.ReverseBeacon, KeyScope.Radio),
        new(Keys.P | Keys.Control, CommandValues.DoPanning, KeyScope.Radio),
        new(Keys.None, CommandValues.SavedScan, KeyScope.Radio), // unbound: CommandFinderOnly
        new(Keys.Z | Keys.Control, CommandValues.StopScan, KeyScope.Radio),
        new(Keys.None, CommandValues.ShowMenus, KeyScope.Radio), // unbound: Retired — never bind this
        // The six RESERVED audio-gain slots. Read the AudioGainUp note in
        // _unboundNotes before claiming any of them — the empty slot is the
        // decision, not an oversight.
        new(Keys.None, CommandValues.AudioGainUp, KeyScope.Radio), // unbound: Reserved
        new(Keys.None, CommandValues.AudioGainDown, KeyScope.Radio), // unbound: Reserved
        new(Keys.None, CommandValues.HeadphonesUp, KeyScope.Radio), // unbound: Reserved
        new(Keys.None, CommandValues.HeadphonesDown, KeyScope.Radio), // unbound: Reserved
        new(Keys.None, CommandValues.LineoutUp, KeyScope.Radio), // unbound: Reserved
        new(Keys.None, CommandValues.LineoutDown, KeyScope.Radio), // unbound: Reserved
        new(Keys.None, CommandValues.RemoteAudio, KeyScope.Radio), // unbound: LeaderLayer — Ctrl+J, Ctrl+P
        new(Keys.None, CommandValues.AudioSetup, KeyScope.Radio), // unbound: MenuOrDialog
        new(Keys.None, CommandValues.ATUMemories, KeyScope.Radio), // unbound: MenuOrDialog
        new(Keys.None, CommandValues.Reboot, KeyScope.Radio), // unbound: MenuOrDialog
        new(Keys.None, CommandValues.TXControls, KeyScope.Radio), // unbound: MenuOrDialog

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
        new(Keys.None, CommandValues.LogFileName, KeyScope.Logging), // unbound: MenuOrDialog
        new(Keys.None, CommandValues.LogMode, KeyScope.Logging), // unbound: MenuOrDialog
        new(Keys.None, CommandValues.LogRig, KeyScope.Logging), // unbound: MenuOrDialog
        new(Keys.None, CommandValues.LogAnt, KeyScope.Logging), // unbound: MenuOrDialog
        new(Keys.F | Keys.Control | Keys.Shift, CommandValues.SearchLog, KeyScope.Logging),
        new(Keys.None, CommandValues.LogStats, KeyScope.Logging), // unbound: LeaderLayer — Ctrl+J, L
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
        new(Keys.None, CommandValues.SpeakTXFilter, KeyScope.Radio), // unbound: MenuOrDialog — Radio menu, Read TX Filter; the JJ key F chord is the filter layer's door now

        // Audio Workshop, Tune, ATU, Meters
        new(Keys.W | Keys.Control | Keys.Shift, CommandValues.OpenAudioWorkshop, KeyScope.Global),
        new(Keys.None, CommandValues.StartAudioCheck, KeyScope.Radio), // unbound: CommandFinderOnly
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

        // ToggleFreqReadout owns Ctrl+Shift+F in the registry (Radio scope;
        // co-binds with SearchLog in Logging scope, non-conflicting because
        // the modes are mutually exclusive) — which is why SpeakFrequency,
        // which claimed that chord on paper, is Shadowed. Full story in
        // _unboundNotes.
        new(Keys.None, CommandValues.SpeakFrequency, KeyScope.Radio), // unbound: Shadowed
        new(Keys.F4 | Keys.Control, CommandValues.RepeatLastMessage, KeyScope.Global),
        // #433: forward on the ADJACENT key, so the pair reads left-to-right as
        // back-then-forward. Ctrl+F3 / Ctrl+F4 would have given the identical
        // ergonomics while reversing a documented, shipped binding.
        new(Keys.F5 | Keys.Control, CommandValues.RepeatNextMessage, KeyScope.Global),
        new(Keys.None, CommandValues.CopyRecentMessage, KeyScope.Global), // unbound: LeaderLayer — Ctrl+J, Ctrl+C
        new(Keys.None, CommandValues.RepeatLastCw, KeyScope.Global), // unbound: LeaderLayer — Ctrl+J, E
        new(Keys.None, CommandValues.SpeakVersion, KeyScope.Global), // unbound: LeaderLayer — Ctrl+J, Alt+V

        // Former hard-wired meta-commands (QB Track H, 2026-08-07) — same
        // chords they always had, now registry-owned and visible.
        new(Keys.M | Keys.Control | Keys.Shift, CommandValues.ToggleTuningMode, KeyScope.Global),
        new(Keys.L | Keys.Control | Keys.Shift, CommandValues.ToggleLoggingMode, KeyScope.Global),
        new(Keys.F | Keys.Control | Keys.Shift, CommandValues.ToggleFreqReadout, KeyScope.Radio),
        new(Keys.F | Keys.Control | Keys.Alt, CommandValues.SpeakRXFilter, KeyScope.Radio),

        // Verbosity (Sprint 24 Phase 6)
        new(Keys.V | Keys.Control | Keys.Shift, CommandValues.CycleVerbosity, KeyScope.Global),
        new(Keys.None, CommandValues.ToggleMeterTonesGlobal, KeyScope.Global), // unbound: LeaderLayer — Ctrl+J, T

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

            // Before ANY of it is applied: has this map slipped onto the wrong
            // commands? A file written before 2026-08-18 is in the old
            // positional numbering, so 22 bindings load against the command one
            // number below where they belong. SmartMergeDefaults cannot see it
            // — it only acts when a default was REMOVED, and here every current
            // default is a real present key, so its branch never runs. See
            // Radios/KeyMapIntegrity.cs and task #209.
            bool repairedCleanly = RepairSlippedKeyMap(kData.Items);

            // v5+: Load saved bindings, then smart-merge changed defaults.
            SetValues(kData.Items!, KeyTypes.AllKeys, false);
            SmartMergeDefaults(kData.Items!);
            MergeNewDefaults();

            // Persist a CLEAN repair, so the file on disk is healed rather
            // than re-repaired on every launch (#209, Sprint 35 Track E).
            // Written HERE and not inside RepairSlippedKeyMap: at that point
            // the key table still holds defaults (SetValues has not run), so
            // a Write there would discard every customisation in the file.
            // Only after the merges is the table the truth worth persisting.
            //
            // Only when NOTHING was customised. Write() stamps every entry's
            // SavedDefaultKey with the current default, which would convert a
            // left-alone customised-but-slipped entry into what reads as a
            // deliberate customisation of the wrong command — destroying the
            // one piece of evidence a support conversation about "my key does
            // the wrong thing" would need. When customisations exist the file
            // stays untouched, the Error-level trace re-fires each launch,
            // and that is the record surviving on purpose.
            if (repairedCleanly)
            {
                if (Write())
                    Tracing.TraceLine("KeyCommands: repaired key map persisted — the file on disk"
                        + " is healed and the repair will not need to run again (#209).",
                        System.Diagnostics.TraceLevel.Error);
            }
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
    /// <summary>
    /// Correct a key map whose bindings slipped onto the wrong commands, and
    /// say loudly that it happened. Silent to the operator; loud in the trace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only bindings the operator never customised are touched. A slipped entry
    /// carries the previous command's key AND that command's recorded default,
    /// so <c>Key == SavedDefaultKey</c> proves it was never chosen by anyone —
    /// it is a default, merely the wrong command's default, and replacing it
    /// with the right one loses nothing. Where the two differ the operator did
    /// pick that key, and only they know what they meant by it, so those are
    /// named in the trace and left exactly as they are.
    /// </para>
    /// <para>
    /// <b>Both Key and SavedDefaultKey are rewritten</b>, not just Key. Leaving
    /// the stale SavedDefaultKey behind would make the entry read as
    /// <c>Key != SavedDefaultKey</c> — an explicit customisation — to
    /// SmartMergeDefaults immediately afterwards, which is the opposite of what
    /// just happened.
    /// </para>
    /// <para>
    /// Noel's call, 2026-08-24, and the reason nothing is spoken: the operator
    /// cannot act on this and has lost nothing, so an announcement would only
    /// alarm. The trace carries it at Error level so it survives the Normal
    /// detail a real session runs at — "just in case it falls over."
    /// </para>
    /// </remarks>
    /// <returns>
    /// True when the repair fixed at least one binding AND touched nothing the
    /// operator customised — the caller persists that state once the merges
    /// finish, so the file heals instead of being re-repaired every launch.
    /// False otherwise, including the customised case, where the unwritten
    /// file IS the evidence and must stay as it is.
    /// </returns>
    private bool RepairSlippedKeyMap(KeyDefType[]? items)
    {
        if (items == null) return false;

        var saved = new List<KeyMapIntegrity.SavedBinding>(items.Length);
        foreach (var it in items)
        {
            if (it != null)
                saved.Add(new KeyMapIntegrity.SavedBinding((int)it.Id, it.Key, it.SavedDefaultKey));
        }

        var verdict = KeyMapIntegrity.Check(
            saved, id => GetDefaultKey((CommandValues)id)?.Key ?? Keys.None);

        if (!verdict.LooksShifted)
        {
            _context.Trace("KeyCommands: " + verdict.Describe());
            return false;
        }

        Tracing.TraceLine("KeyCommands: " + verdict.Describe(), System.Diagnostics.TraceLevel.Error);

        var toFix = new HashSet<int>(verdict.RepairableIds);
        int fixedCount = 0;
        foreach (var it in items)
        {
            if (it == null || !toFix.Contains((int)it.Id)) continue;
            var correct = GetDefaultKey(it.Id);
            if (correct == null) continue;   // no default to restore — leave it

            Tracing.TraceLine("KeyCommands:repaired " + it.Id + " " + it.Key
                + " -> " + correct.Key + " (was the default of the command one number below)",
                System.Diagnostics.TraceLevel.Error);
            it.Key = correct.Key;
            it.SavedDefaultKey = correct.Key;
            fixedCount++;
        }

        foreach (int id in verdict.CustomisedIds)
        {
            Tracing.TraceLine("KeyCommands: id " + id + " slipped but carries a key the operator chose"
                + " — left alone, it may now be on the wrong command", System.Diagnostics.TraceLevel.Error);
        }

        Tracing.TraceLine("KeyCommands: repaired " + fixedCount + " slipped binding(s), left "
            + verdict.CustomisedIds.Count + " customised one(s) alone. Nothing was spoken;"
            + " the operator lost no binding they chose.", System.Diagnostics.TraceLevel.Error);

        if (verdict.CustomisedIds.Count > 0)
        {
            Tracing.TraceLine("KeyCommands: file left unwritten so the slipped-but-customised"
                + " evidence survives; this repair re-runs each launch until the operator"
                + " resolves those bindings (Keys dialog) or any other save rewrites the file.",
                System.Diagnostics.TraceLevel.Error);
        }

        return fixedCount > 0 && verdict.CustomisedIds.Count == 0;
    }

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
    public bool DoCommand(Keys k) => DoCommand(k, fromDialog: false);

    /// <summary>
    /// The dispatch core. <paramref name="fromDialog"/> is true when the call
    /// arrived through the WPF dialog routing (preview/bubble class handlers)
    /// rather than the main window's ProcessCmdKey — the value sub-layer's
    /// confirm-and-pass-through needs to know, because a key released from
    /// the layer must reach the DIALOG pipeline there (scope discipline) and
    /// be re-dispatched here on the main path.
    /// </summary>
    internal bool DoCommand(Keys k, bool fromDialog)
    {
        _context.Trace("DoCommand:" + ((int)k).ToString("x8"));
        bool rv = false;

        // Just return if this is just the shift, alt, or control key.
        int theKey = (int)(k & Keys.KeyCode);
        if (theKey == (int)Keys.Menu || theKey == (int)Keys.ControlKey ||
            theKey == (int)Keys.ShiftKey || theKey == 0)
            return rv;

        // === VALUE SUB-LAYER DISPATCH (#305 pattern — the audio and filter
        // layers today) ===
        // Ahead of the one-shot leader: the layer stays live across keys.
        // The engine decides; this block only routes.
        if (_valueLayer != null)
        {
            switch (DoValueLayerKey(k))
            {
                case Radios.ValueLayerKeyResult.PassThrough:
                    // Alt chords, F1, the verbosity cycle: fall through to
                    // the rest of this dispatch with the layer still live, so
                    // a whitelisted chord does its normal job (and the next
                    // nudge speaks in the other form).
                    break;

                case Radios.ValueLayerKeyResult.ClosedPassThrough:
                    // The layer confirmed and closed; the key now means what
                    // it always means. From a dialog, returning false lets
                    // the dialog and then the bubble handler take it (Global
                    // scope discipline intact). On the main path, re-dispatch
                    // so a registry chord fires exactly as if the layer had
                    // already been closed.
                    return fromDialog ? false : DoCommand(k, fromDialog);

                default:
                    return true; // consumed — handled, closed, or handed off
            }
        }

        // === LEADER KEY DISPATCH ===
        if (_leaderKeyActive)
        {
            _leaderKeyActive = false;
            if (k == Keys.Escape)
            {
                LeaderCancel();
                return true;
            }
            return DoLeaderCommand(k);
        }

        // === LEADER HELP-ARMED DISPATCH (#303) ===
        // Reached only after the unknown-key answer, which is the answer that
        // TELLS the operator H and Escape are still live: at Chatty the
        // sentence that names them, below Chatty the invalid tone that the
        // sentence taught them (#528). The layer is never silently sticky: it
        // stays armed exactly when it has just said so, and for nothing but
        // the keys it named. The arming is the same at every verbosity —
        // verbosity changes what is said, never what happens.
        if (_leaderHelpArmed)
        {
            _leaderHelpArmed = false;
            if (k == Keys.Escape)
            {
                LeaderCancel();
                return true;
            }
            if (IsLeaderHelpKey(k))
            {
                // H lists, the slash key explores — the same two doors the
                // switch offers, so the armed layer cannot answer a key
                // differently from the open one.
                if (k == Keys.H) LeaderKeyHelp(); else OpenKeyExplorer();
                return true;
            }
            // Anything else: the layer has let go, and this key is NOT
            // consumed. It goes on to do exactly what it would have done had
            // the layer never lingered — which is what "exits exactly as
            // today" has to mean, or a keystroke the operator meant for the
            // field would vanish into a mode they did not ask for.
        }

        // Check for leader key trigger (Ctrl+J).
        if (k == (Keys.J | Keys.Control))
        {
            _leaderKeyActive = true;
            EarconPlayer.LeaderEnterTone();
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("leader.armed"), Radios.VerbosityLevel.Terse, true);
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
                Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("settings.cw.no_messages_configured"), Radios.VerbosityLevel.Critical, true);
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
    /// Preview (tunnel) phase: active ONLY while a leader or a value layer
    /// is armed. Then every key routes through DoCommand ahead of the
    /// dialog's own handling — exact parity with the main window, where
    /// ProcessCmdKey feeds an armed mode before any control sees the key.
    /// Without this, a focused TextBox or list would eat a layer's arrows
    /// at its own KeyDown, and a leader follow-on letter could both
    /// dispatch AND type into the field. The modes stay polite on their
    /// own: the value layer passes Alt chords and F1 through untouched, and
    /// bare modifier presses fall out of DoCommand unhandled.
    /// </summary>
    private static void AnyWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var kc = _globalRoutingOwner;
        if (kc == null || e.Handled) return;
        if (!kc._leaderKeyActive && kc._valueLayer == null)
        {
            // TWO live modes: the leader itself and a value sub-layer (#305 —
            // the audio and filter layers, and whatever follows them; volume
            // mode was the third until Sprint 44 Track I folded it into the
            // audio layer). Either falls through to be handled; only when
            // NEITHER is live do we reach the help-armed question below.
            // Sprint 37 merged Track C's value-layer condition with Track D's
            // help-armed block — each was correct alone and neither was
            // sufficient.
            //
            // Help-armed (#303) is a much thinner claim on the keyboard than a
            // fully armed leader: only the three keys that lead OUT of the
            // layer are ours. Everything else releases the state HERE and is
            // left completely alone — it never enters DoCommand, so a dialog's
            // own key cannot be re-routed through the main-window registry on
            // its way past a mode the operator did not ask for.
            if (!kc._leaderHelpArmed) return;
            var pressed = WpfKeyConverter.ToWinFormsKeys(e);
            if (pressed != Keys.Escape && !IsLeaderHelpKey(pressed))
            {
                kc._leaderHelpArmed = false;
                return;
            }
        }
        else if (KeyHelpSurfaces.IsOpen && !kc._leaderKeyActive)
        {
            // Sprint 44 Track K: a key help surface is open over a persistent
            // mode (volume mode, a value sub-layer). The surface owns the
            // keyboard — its arrows move its rows, its letters jump, its
            // Escape closes IT — and the mode is still there when it closes.
            // Without this, a list of volume mode's keys opened from inside
            // volume mode would have its arrows adjust the volume. Only the
            // one-shot leader still tunnels, so Ctrl+J inside the surface
            // works exactly as it does in every other dialog.
            return;
        }
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
                kc._leaderHelpArmed = false;
                // A forced drop KEEPS the value, never restores: a restore is
                // a write, and mid-transmit is no time to write (the same
                // rule #187's power layer will lean on).
                if (kc._valueLayer != null) { kc._valueLayer.Drop(); kc._valueLayer = null; }
                return;
            }
        }

        // Stash the keystroke's origin for handlers that need to know which
        // element the operator is on (Ctrl+F1) — see _dispatchOriginalSource.
        _dispatchOriginalSource = e.OriginalSource as System.Windows.DependencyObject;
        try
        {
            if (kc.DoCommand(WpfKeyConverter.ToWinFormsKeys(e), fromDialog: true))
                e.Handled = true;
        }
        finally
        {
            _dispatchOriginalSource = null;
        }
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
        // Stash the keystroke's origin for handlers that need to know which
        // element the operator is on (Ctrl+F1) — see _dispatchOriginalSource.
        _dispatchOriginalSource = e.OriginalSource as System.Windows.DependencyObject;
        try
        {
            if (kc.DispatchFromDialogWindow(k))
                e.Handled = true;
        }
        finally
        {
            _dispatchOriginalSource = null;
        }
    }

    /// <summary>
    /// The dialog-side dispatch core. Consumes: leader and value-layer keys
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
        if (_leaderKeyActive || _valueLayer != null)
        {
            // Sprint 44 Track K: while a key help surface is open, a persistent
            // mode does not take the surface's keys on the bubble path either
            // — same rule as the preview handler, stated for the path a dialog
            // reaches when it leaves a key unhandled. Ctrl+J itself still
            // arms, and an armed leader still fires.
            if (KeyHelpSurfaces.IsOpen && !_leaderKeyActive && k != (Keys.J | Keys.Control))
                return false;
            return DoCommand(k, fromDialog: true);
        }

        // Help-armed (#303) claims only the three keys that lead out of the
        // layer. The preview handler above normally gets there first and
        // releases the state on anything else; this is the same rule stated
        // again for the bubble path, so a dialog that swallowed the preview
        // cannot leave the layer armed behind it.
        if (_leaderHelpArmed)
        {
            if (k == Keys.Escape || IsLeaderHelpKey(k)) return DoCommand(k);
            _leaderHelpArmed = false;
        }

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
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("settings.cw.no_message_at_position"), Radios.VerbosityLevel.Critical, true);
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
                Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("settings.cw.sending", ("label", label)), Radios.VerbosityLevel.Terse, false);
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

        // Trace every leader follow-on. "I pressed Ctrl+J and then something
        // and nothing happened" is a report we get, and until now the leader
        // layer was the one dispatch path that left no trace at all — so a
        // diagnostic capture of the exact complaint contained no evidence of
        // it. Both arms are needed: this line says the chord arrived, and the
        // default case below says it arrived and meant nothing.
        _context.Trace("Leader:" + k);

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
            // "On-Radio" prefix (DSP controls track, 2026-08-11): these two
            // have PC-side namesakes on Shift+R/Shift+S — the spoken names
            // now say which side of the wire each one lives on.
            case Keys.R:
                if (rig == null)
                    LeaderNoRadio();
                else if (!rig.NeuralNRHardwareSupported)
                {
                    EarconPlayer.LeaderInvalidTone();
                    Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.nr.neural_unsupported"), Radios.VerbosityLevel.Critical);
                }
                else
                    ToggleLeaderDSP("On-Radio Neural NR",
                        () => rig.NeuralNoiseReduction, v => rig.NeuralNoiseReduction = v);
                break;
            case Keys.S:
                if (rig == null)
                    LeaderNoRadio();
                else if (!rig.NeuralNRHardwareSupported)
                {
                    EarconPlayer.LeaderInvalidTone();
                    Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.nr.spectral_unsupported"), Radios.VerbosityLevel.Critical);
                }
                else
                    ToggleLeaderDSP("On-Radio Spectral NR",
                        () => rig.SpectralNoiseReduction, v => rig.SpectralNoiseReduction = v);
                break;
            case Keys.N | Keys.Shift:
                if (rig == null)
                    LeaderNoRadio();
                else if (!rig.NeuralNRHardwareSupported)
                {
                    EarconPlayer.LeaderInvalidTone();
                    Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.nr.filter_unsupported"), Radios.VerbosityLevel.Critical);
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
                        Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.pc_pipeline.not_ready"), Radios.VerbosityLevel.Critical);
                    }
                    else
                    {
                        pipeline.RnnEnabled = !pipeline.RnnEnabled;
                        if (pipeline.RnnEnabled) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
                        Radios.ScreenReaderOutput.Speak(
                            pipeline.RnnEnabled
                                ? Radios.Lexicon.Get("audio.pc_nr.neural_on")
                                : Radios.Lexicon.Get("audio.pc_nr.neural_off"),
                            Radios.VerbosityLevel.Terse);
                        _context.GetMainWindow()?.PersistDspSettings();
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
                        Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.pc_pipeline.not_ready"), Radios.VerbosityLevel.Critical);
                    }
                    else
                    {
                        pipeline.SpectralEnabled = !pipeline.SpectralEnabled;
                        if (pipeline.SpectralEnabled) EarconPlayer.FeatureOnTone(); else EarconPlayer.FeatureOffTone();
                        // The no-profile message names the exit now (DSP
                        // controls track) — it used to announce a dead end
                        // nothing in the app could resolve.
                        string msg = pipeline.SpectralEnabled
                            ? (pipeline.HasNoiseProfile
                                ? Radios.Lexicon.Get("audio.pc_nr.spectral_on")
                                : Radios.Lexicon.Get("audio.pc_nr.spectral_on_no_profile"))
                            : Radios.Lexicon.Get("audio.pc_nr.spectral_off");
                        Radios.ScreenReaderOutput.Speak(msg, Radios.VerbosityLevel.Terse);
                        _context.GetMainWindow()?.PersistDspSettings();
                    }
                }
                break;

            // DSP controls track (2026-08-11): Q = capture a noise profile
            // for PC Spectral NR — Q for "quiet", the thing you're capturing
            // (hams may prefer to read it as QRN). Press Q again while the
            // capture runs to cancel it. The narrator speaks start, each
            // second, and the result; a completed capture auto-saves and is
            // reloaded on the next connect. Works on every radio — the
            // pipeline runs on this computer.
            case Keys.Q:
                {
                    var win = _context.GetMainWindow();
                    var pipeline = win?.FieldsPanel.AudioPipeline;
                    if (rig == null)
                        LeaderNoRadio();
                    else if (pipeline == null)
                    {
                        EarconPlayer.LeaderInvalidTone();
                        Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.pc_pipeline.not_ready"), Radios.VerbosityLevel.Critical);
                    }
                    else
                        NoiseCaptureNarrator.Toggle(rig, pipeline,
                            win?.CurrentAudioConfig?.SpectralSubSampleDuration ?? 3);
                }
                break;

            // Sprint 36 Track C (#271): the QSO signal analyzer — watch a
            // contact's S-meter, then hear what the signal did, QSB and all.
            // Ctrl+Q because plain Q is the noise capture and Q is the letter
            // "QSO" reaches for; Ctrl+F, Ctrl+D and Ctrl+R are the precedent
            // for the Ctrl-modified form when the letter you want is taken.
            // The two capture chords live side by side on purpose.
            //
            // A toggle: the same chord stops the capture and speaks the
            // headline; the full report lands under Tools, Signal captures.
            // Runs until told — no auto-stop, ruled 2026-08-26 — and the
            // running-cost registration is what makes that safe (Ctrl+J, O
            // reports it, thresholds speak up, exit asks about it).
            //
            // No rig gate at the case: STOPPING must work even after the
            // radio has gone away, or a capture could only be ended by
            // exiting. The handler gates starting on its own.
            case Keys.Q | Keys.Control:
                ToggleQsoSignalCaptureFromChord();
                break;

            // #433, 2026-08-31. Don asked for it, Noel scoped it: ONE generic
            // copy rather than a copy button on every report. Ctrl+C is the
            // copy chord everywhere, so it is the copy chord here too - and it
            // is safe on the leader layer, where plain Ctrl+C still belongs to
            // whatever control has focus.
            //
            // No rig gate: what was said is ours, not the radio's, and copying
            // it must work after the radio has gone away - which is exactly
            // when somebody wants to paste the error into a message.
            case Keys.C | Keys.Control:
                CopyRecentMessageHandler();
                break;

            // Sprint 38 Track C (#337): switch the S-meter between S-units and
            // dBm, for this radio, remembered. Ctrl+S because the chord ECHOES
            // the key it relates to — Ctrl+S reads the S-meter, Ctrl+J then
            // Ctrl+S changes what it reads in — and because Ctrl+Shift+S, the
            // obvious flat alternative, is SpeakStatus.
            //
            // This chord took a ONE-SHOT dBm reading for one day (#306, Sprint
            // 37 Track G). Noel ruled the second reading out of scope on
            // 2026-08-28 after talking to Don, so the chord is now the toggle
            // he remembered having and could not reach.
            //
            // KNOWN ADJACENCY, recorded rather than discovered: plain S is the
            // radio's own Spectral NR and Shift+S is the PC one, so this sits
            // one modifier from two unrelated DSP toggles. All three are
            // toggles and a second press undoes any of them, so a slip is
            // recoverable — and all three announce themselves by name, so a
            // slip is audible.
            //
            // No rig gate at the case: the handler speaks the no-radio message
            // itself, the same one Ctrl+S gives, rather than the generic
            // leader one.
            case Keys.S | Keys.Control:
                SmeterDisplayHandler();
                break;

            // JJ key A — the audio layer (#514, #515). Under the four-tier
            // grammar a plain letter opens a layer, and A is ruled for audio.
            // Auto Notch, which held plain A, is on Ctrl+A below. The layer
            // itself is Sprint 44 Track I's, wired here by the integration
            // pass: neither track could do it from inside its own worktree,
            // and a layer with no door is a complete feature nobody can reach.
            case Keys.A:
                EnterAudioLayer(onPan: false);
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
                        Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.apf.cw_only"), Radios.VerbosityLevel.Critical);
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

            // Sprint 37 Track C (#304): Alt+P enters pan mode — the value
            // sub-layer pattern (#305), proven on pan. Plain P is the Audio
            // Peak Filter and Shift+P the Speech Processor. Ctrl+P was skipped
            // here in Sprint 37 because flat Ctrl+P is the FREQUENCY panning
            // field and the two share only a word; #513 has since put PC
            // audio on Ctrl+P by ruling, which settles it. Under #514 pan
            // belongs inside the audio layer (JJ key A) — Track I's move; this
            // door stays open until it lands. Alt+V is the in-layer precedent
            // for an Alt-modified follow-on, and WpfKeyConverter resolves
            // Key.System before this switch sees the press.
            case Keys.P | Keys.Alt:
                EnterPanMode();
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

            // Ctrl+P = PC audio on or off. Ruled #513, Noel 2026-09-01: "p for
            // pc makes more sense." Under the four-tier grammar (#515) Ctrl is
            // the toggle tier and the letter is the toggle's own initial — P
            // for PC audio — so the chord is derived, not memorised.
            //
            // It was Ctrl+A from Sprint 32 Track G (#130) until Sprint 44.
            // Noel, on the survey of commands with no key: "No hotkey for PC
            // audio on and off available that I know of, you have to do it in
            // the menu." Of the twenty-nine unbound commands this is the one
            // that earns a key — it is a toggle you reach for mid-QSO, in the
            // dark, when you cannot hear something, and a menu is the wrong
            // instrument for that.
            //
            // #513 kept Ctrl+A as a courtesy alias "with the caveat that if we
            // need A somewhere else we use it." We need it: plain A is the
            // audio layer's door now, and Auto Notch — the toggle whose
            // initial IS A — had to leave it. So Ctrl+A is Auto Notch, below,
            // and the alias expired the day it was granted. A slip onto it is
            // audible and reversible: it announces itself by name and a second
            // press undoes it.
            //
            // Flat Ctrl+P is the FREQUENCY panning field. Different layer,
            // shares a word and nothing else; the pan-mode comment below
            // records the earlier worry, now overridden by the ruling. Note
            // Ctrl+J, V, P rides the PC output LEVEL — this is the on/off
            // switch, and they sit one keystroke apart on purpose.
            //
            // Nothing is duplicated here: the handler is the registry command's
            // own, so this chord, the Command Finder and the Hotkey Editor all
            // do the identical thing and say the identical words.
            case Keys.P | Keys.Control:
                if (rig == null) LeaderNoRadio();
                else PCAudioHandler();
                break;

            // Ctrl+A = Auto Notch. Moved from plain A in Sprint 44 Track J,
            // when the four-tier grammar (#515) made plain letters layer doors
            // and A became the audio layer. Ctrl is the toggle tier and A is
            // Auto Notch's own initial, so the chord derives itself. It will
            // also live inside the noise layer once that layer has a letter —
            // a common toggle gets two doors, the rule of threes.
            case Keys.A | Keys.Control:
                if (rig == null) LeaderNoRadio();
                else ToggleLeaderDSP("Auto Notch",
                    () => rig.AutoNotchFFT, v => rig.AutoNotchFFT = v);
                break;

            case Keys.P | Keys.Shift:
                if (rig == null) LeaderNoRadio();
                else ToggleLeaderDSP("Speech Processor",
                    () => rig.ProcessorOn, v => rig.ProcessorOn = v);
                break;

            // Ctrl+F = enter a frequency. Predates the four-tier grammar and
            // is not a toggle — on the grammar it belongs to the Alt tier.
            // Left where every operator's fingers know it; Noel walks the
            // letters one at a time and this one has not come up.
            case Keys.F | Keys.Control:
                if (rig == null) LeaderNoRadio();
                else _context.WriteFreq();
                break;

            // JJ key F — the filter layer (#512, #516). Plain F spoke the TX
            // filter width and Shift+F the RX width until Sprint 44; both
            // readouts move INSIDE the layer, on its S key, and that freed
            // Shift+F for slice F (#504 — in the slice row below). The layer
            // itself is Sprint 44 Track I's, wired here by the integration
            // pass. The RX width still answers to the flat Ctrl+Alt+F as well,
            // and the TX width to the Radio menu's Read TX Filter item.
            case Keys.F:
                EnterFilterLayer();
                break;

            // Tuning debounce toggle
            case Keys.D:
                ToggleTuneDebounce();
                break;

            // Ctrl+D = Diagnostics: start or stop a detailed capture from
            // anywhere, including from inside whatever dialog is misbehaving.
            //
            // Ctrl+D rather than plain D because plain D has been tuning speech
            // debounce since before the diagnostic-log design was written; and
            // rather than Shift+D because that sits inside the Shift+A-Shift+H
            // slice-jump range. Ctrl+F (enter a frequency) is the in-layer
            // precedent for a Ctrl-modified follow-on key.
            //
            // Works with no radio connected on purpose — "it will not connect"
            // is precisely the problem worth capturing.
            case Keys.D | Keys.Control:
                ToggleDetailedCaptureFromChord();
                break;

            // Ctrl+R = Recorded problems: read everything that has gone wrong
            // this session. The other half of Ctrl+D — that one starts
            // recording evidence, this one reads what already went wrong.
            //
            // Ctrl+R rather than plain R (On-Radio Neural NR since the DSP
            // controls track) or Shift+R (its PC namesake), which makes Ctrl+R
            // the only free R in the layer anyway.
            //
            // Works with no radio connected on purpose: a connect that failed
            // is the commonest reason to press this, and by definition there is
            // no radio when it happens.
            case Keys.R | Keys.Control:
                ShowRecordedProblemsFromChord();
                break;

            // O = what is On — the on-demand read of the running-cost register
            // (#253). Third member of the diagnostics family that already holds
            // Ctrl+D and Ctrl+R: that one starts recording evidence, that one
            // reads what went wrong, and this one answers "what is running and
            // costing me something right now".
            //
            // Plain O, not Ctrl+O: O is one of the very few letters still free
            // in the layer in every form, so there is no taken letter to reach
            // around — and the sighted equivalent of this question is a glance,
            // which should not cost two modifiers.
            //
            // Works with no radio connected on purpose. Every registrant is a
            // property of THIS APPLICATION, not of the radio, and instrumentation
            // left running through a failed connect is exactly the case worth
            // asking about.
            case Keys.O:
                SpeakRunningCostsFromChord();
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

            // E = Echo the recent CW notifications (#153, Sprint 33 Track F).
            //
            // In the leader layer rather than as a flat chord, and not only
            // because that is the house rule: the flat chord anyone would reach
            // for is Ctrl+Shift+F4, one modifier from the speech repeat on
            // Ctrl+F4 — and it is already taken, by the CW send text box. Every
            // other free flat chord near F4 would have been arbitrary.
            //
            // E because echo, and because E is a single dit — the smallest
            // character in Morse, for the one command in the layer that only
            // ever answers in Morse. Plain E is free; only Shift+E is spoken
            // for, by the slice-jump row.
            //
            // Works with no radio on purpose. The history outlives the
            // connection, and "what did that say?" is asked most often just
            // after something went away.
            case Keys.E:
                RepeatLastCwHandler();
                break;

            // #269. V for Version, Alt to clear the collision — bare V has been
            // volume mode since the Audio Arc, and it is not moving.
            //
            // ALT ON THIS PATH IS SAFE, and that is a checked claim rather than
            // an assumption. WPF reports Key.System when Alt is held and hides
            // the real key in e.SystemKey, which is how the Alt+L binding of
            // 2026-08-13 shipped completely dead. WpfKeyConverter resolves it
            // before this switch ever sees the press (`e.Key == Key.System ?
            // e.SystemKey : e.Key`), so what arrives here is a proper
            // Keys.V | Keys.Alt. Ctrl+D, Ctrl+F and Ctrl+R are the in-layer
            // precedent for a modified follow-on key; this is the first Alt one.
            //
            // Static verification is necessary and not sufficient: press it.
            case Keys.V | Keys.Alt:
                SpeakVersionHandler();
                break;

            // The two help doors every layer has (#514, #519): H lists this
            // layer's commands, the slash key opens the JJ key explorer.
            //
            // BOTH arrival forms of the slash key, and the Shift one is the
            // one "?" produces. "?" is Shift+/ on a US keyboard, so it arrives
            // here as Keys.Oem2 | Keys.Shift — this switch carries modifier
            // bits (see the slice-jump row below). A bare Keys.Oem2 case alone
            // therefore never matched, and every "?" fell through to the
            // unknown-command arm from the day it was written until
            // 2026-08-22, while the help text advertised "H or ?" the whole
            // time. Found by Noel pressing it; same family as the Alt+L
            // binding that shipped dead on 2026-08-13: it compiles, it reads
            // correctly, and only the keypress tells you. The physical key
            // means one thing whether or not Shift is down.
            case Keys.Oem2:
            case Keys.Oem2 | Keys.Shift:
                OpenKeyExplorer();
                break;
            case Keys.H:
                LeaderKeyHelp();
                break;

            // The Shift tier (#515): Shift+<letter> jumps to that slice, from
            // any focus position, and means nothing else. All eight, A to H —
            // the letter IS the radio's slice index, so the row is complete by
            // construction, and JjKeyGrammarTests holds it to that.
            //
            // Slice F was missing from this row from the day it was written
            // until Sprint 44 (#504). Shift+F was the RX filter readout, bound
            // earlier in this switch, so it won outright — and the comment
            // here claimed A-H regardless. Only a six-slice radio, a 6700 or
            // 6700R, can reach index 5; neither test radio can, so no bench
            // session would ever have found it. The readout moved into the
            // filter layer (#512) and Shift+F came back for free.
            case Keys.A | Keys.Shift: JumpToSlice(0); break;
            case Keys.B | Keys.Shift: JumpToSlice(1); break;
            case Keys.C | Keys.Shift: JumpToSlice(2); break;
            case Keys.D | Keys.Shift: JumpToSlice(3); break;
            case Keys.E | Keys.Shift: JumpToSlice(4); break;
            case Keys.F | Keys.Shift: JumpToSlice(5); break;
            case Keys.G | Keys.Shift: JumpToSlice(6); break;
            case Keys.H | Keys.Shift: JumpToSlice(7); break;

            default:
                _context.Trace("Leader:no command for " + k);
                EarconPlayer.LeaderInvalidTone();

                // #528: below Chatty the thunk IS the answer and the sentence
                // that follows it at Chatty stays unspoken — the operator who
                // knows what the thunk means turns verbosity down and stops
                // hearing the lesson. The words come back at every level if
                // the thunk cannot sound (earcons off), because a refused key
                // that produces nothing at all is the invisible failure. Only
                // what is SAID changes below: the disarm on a near miss and
                // the arm on an unknown key are the same at every verbosity.
                bool toneStandsAlone = Radios.Speech.RefusalVoice.ToneStandsAlone(
                    Radios.ScreenReaderOutput.CurrentVerbosity,
                    EarconPlayer.IsOn(EarconPlayer.EarconCategory.CommandsAndConfirmations));

                // #206: a near-miss gets named instead of a dead end. The
                // layer mixes bare, Shift and Ctrl tiers on the same letters
                // (D vs Ctrl+D, Q vs Ctrl+Q), so a slipped modifier is the
                // layer's own most predictable mistake — and the recovery
                // information is already in the inventory. "Ctrl+G is not a
                // command. G: arm or disarm the TX test tone" turns a
                // re-enter-and-hunt into a one-chord retry, and teaches the
                // layer while the operator is standing in it. One alternative
                // at most, bare form first. The layer still disarms — this
                // changes what is SAID, not what happens. The alternative is
                // the Chatty tier; the fallback tiers say only that the chord
                // is not a command, because naming what to press instead is a
                // hint, and Terse is values and transitions, not hints.
                if (KeyInventory.TryFindLeaderNearMiss(k, out string nearKey, out string nearWhat))
                {
                    if (!toneStandsAlone)
                    {
                        Radios.ScreenReaderOutput.Speak(
                            Radios.Lexicon.Get("leader.near_miss",
                                Radios.ScreenReaderOutput.CurrentVerbosity,
                                ("pressed", KeyManifest.FormatKey(k)),
                                ("alt", nearKey),
                                ("what", nearWhat)),
                            Radios.Speech.SpeechIntent.Interrupt,
                            Radios.VerbosityLevel.Critical,
                            subject: Radios.Speech.SpeechSubject.JjKeyHelp);
                    }
                }
                else
                {
                    // #303. The layer stays armed for H, the slash key and
                    // Escape — but ONLY on this branch, because this is the
                    // branch that says so. The near-miss above names a chord
                    // to retry and says nothing about help, so leaving the
                    // layer armed there would be a mode the operator was never
                    // told they were in. Two situations, two vocabularies, and
                    // stickiness follows the sentence that earns it — and,
                    // below Chatty, the thunk that sentence taught (#528).
                    // The arm is deliberately OUTSIDE the verbosity gate.
                    _leaderHelpArmed = true;

                    // Verbosity picks the wording. Terse: "H for the list."
                    // Chatty adds the slash key and what it opens. Both name
                    // the KEYSTROKE and never the glyph — a literal "?" may
                    // not be voiced at all with punctuation set low, which
                    // would silently drop the very key being recommended.
                    // Tagged Critical on purpose: when this speaks at all it
                    // is because the thunk could not, and a sticky mode with
                    // no stated exit is the trap #303 exists to close.
                    if (!toneStandsAlone)
                    {
                        Radios.ScreenReaderOutput.Speak(
                            Radios.Lexicon.Get("leader.unknown_key",
                                Radios.ScreenReaderOutput.CurrentVerbosity),
                            Radios.Speech.SpeechIntent.Interrupt,
                            Radios.VerbosityLevel.Critical,
                            subject: Radios.Speech.SpeechSubject.JjKeyHelp);
                    }
                }
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
                Radios.Lexicon.Get("settings.slice.active", ("letter", rig.VFOToLetter(vfo))),
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
                Radios.Lexicon.Get("settings.slice.not_available_on_radio",
                    ("letter", letter), ("totalCap", totalCap)),
                Radios.VerbosityLevel.Critical, true);
        }
        else if (rig.SliceIndexOwnedByOther(sliceIndex))
        {
            EarconPlayer.LeaderInvalidTone();
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("settings.slice.in_use_by_another", ("letter", letter)),
                Radios.VerbosityLevel.Critical, true);
        }
        else
        {
            EarconPlayer.LeaderInvalidTone();
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("settings.slice.not_yet_created", ("letter", letter)),
                Radios.VerbosityLevel.Critical, true);
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  The audio layer — Sprint 44 Track I, #514. Pan (Sprint 37 Track C,
    //  #304) and volume mode (Audio Arc Track A, 2026-08-11) were two
    //  modes: one on the value sub-layer engine, one hand-rolled beside
    //  it, which #305's closing note recorded as debt. They are ONE layer
    //  now, on the engine (Radios.ValueSubLayer), which owns every pattern
    //  decision — exits, cancel-restores, words-or-numbers under
    //  verbosity, the coalesced move speech. This class builds the
    //  definition, wires the earcons and routes keys.
    //
    //  Letters pick the target and Up/Down adjust everything; Left/Right
    //  ALSO adjust pan, because for that one target direction means
    //  something real. Plain H is help in every layer, so headphone wears
    //  Ctrl; plain P is PC output, so pan wears Ctrl too — the #515 rule
    //  for a letter already spoken for. Shift+letter jumps to a slice
    //  from inside the layer and pan follows; the layer never spends A-F
    //  on slices.
    //
    //  Doors: Ctrl+J, V opens it with nothing picked; Ctrl+J, Alt+P opens
    //  it with pan picked — the chords an operator's fingers know. The
    //  four-tier allocation (#515) gives the audio layer JJ key A; that
    //  letter, and what V becomes, are Track J's. EnterAudioLayer is the
    //  method to wire.
    // ────────────────────────────────────────────────────────────────

    private static string ProcessorSettingName(int setting) => setting switch
    {
        1 => Radios.Lexicon.Get("audio.processor.name_dx"),
        2 => Radios.Lexicon.Get("audio.processor.name_dx_plus"),
        _ => Radios.Lexicon.Get("audio.processor.name_normal"),
    };

    /// <summary>The door Ctrl+J, V opens. Kept under its old name so the
    /// leader switch is untouched; what it opens is the audio layer.</summary>
    private void EnterVolumeMode() => EnterAudioLayer(onPan: false);

    /// <summary>The door Ctrl+J, Alt+P opens: the audio layer with pan
    /// already picked, so "pan mode" is one keystroke shorter than it was
    /// rather than gone.</summary>
    private void EnterPanMode() => EnterAudioLayer(onPan: true);

    /// <summary>
    /// Enter the audio layer. Targets: on-radio headphone (Ctrl+H), PC
    /// output (P), mic level (M), on-radio line out (L), compander level
    /// (C), speech processor mode (S) — all radio-wide or app-wide — and
    /// pan (Ctrl+P), which is per slice and follows a Shift+letter jump.
    /// Up and Down adjust the picked target (Shift by one); Left and Right
    /// also adjust pan; Home centres pan. Enter keeps everything, Escape
    /// puts back everything that moved, out loud.
    /// </summary>
    internal void EnterAudioLayer(bool onPan)
    {
        var rig = _context.GetRigControl();
        if (rig == null) { LeaderNoRadio(); return; }

        // The PC output volume is app-level state, persisted by the main
        // window; it is written exactly once, on ANY exit, if it moved —
        // the volume-mode behaviour, now on the engine's exit hook.
        bool pcTouched = false;
        int centre = (FlexBase.MaxPan - FlexBase.MinPan) / 2;

        Radios.ValueTarget Level(string id, string nameKey, Keys select,
            Func<int> read, Action<int> apply, int min, int max, int step,
            string numberKey, string? selectedKey = null, Func<string>? note = null)
        {
            string name = Radios.Lexicon.Get(nameKey);
            return new Radios.ValueTarget
            {
                Id = id,
                Name = name,
                SelectKey = select,
                Read = read,
                Apply = apply,
                Min = min,
                Max = max,
                Step = step,
                FineStep = 1,
                Axes = Radios.ValueLayerAxes.UpDown,
                Number = v => Radios.Lexicon.Get(numberKey, ("value", v)),
                DescribeSelected = selectedKey == null
                    ? null
                    : v => Radios.Lexicon.Get(selectedKey, ("value", v)),
                Note = note,
                WrongAxisHint = () => Radios.Lexicon.Get("audio.audio_layer.uses_up_down", ("target", name)),
            };
        }

        var headphone = Level("headphone", "audio.audio_layer.name_headphone", Keys.H | Keys.Control,
            () => rig.HeadphoneGain, v => rig.HeadphoneGain = v, 0, 100, 5,
            "audio.audio_layer.headphone", "audio.audio_layer.headphone_selected");
        var pcOutput = Level("pc-output", "audio.audio_layer.name_pc_output", Keys.P,
            () => rig.PcOutputVolumeDb, v => { rig.PcOutputVolumeDb = v; pcTouched = true; },
            FlexBase.PcOutputVolumeDbMin, FlexBase.PcOutputVolumeDbMax, 1,
            "audio.audio_layer.pc_output");
        var mic = Level("mic", "audio.audio_layer.name_mic", Keys.M,
            () => rig.MicGain, v => rig.MicGain = v, FlexBase.MicGainMin, FlexBase.MicGainMax, 5,
            "audio.audio_layer.mic");
        var lineout = Level("lineout", "audio.audio_layer.name_lineout", Keys.L,
            () => rig.LineoutGain, v => rig.LineoutGain = v, 0, 100, 5,
            "audio.audio_layer.lineout", "audio.audio_layer.lineout_selected");
        var compander = Level("compander", "audio.audio_layer.name_compander", Keys.C,
            () => rig.CompanderLevel, v => rig.CompanderLevel = v,
            FlexBase.CompanderLevelMin, FlexBase.CompanderLevelMax, FlexBase.CompanderLevelIncrement,
            "audio.audio_layer.compander",
            note: () => rig.Compander == FlexBase.OffOnValues.on
                ? "" : Radios.Lexicon.Get("audio.audio_layer.compander_is_off_suffix"));

        // Up = stronger (Normal → DX → DX+), Down = gentler. Clamps at the
        // ends — wrapping on an arrow key is disorienting speech.
        string processorName = Radios.Lexicon.Get("audio.audio_layer.name_processor");
        var processor = new Radios.ValueTarget
        {
            Id = "processor",
            Name = processorName,
            SelectKey = Keys.S,
            Read = () => (int)rig.ProcessorSetting,
            Apply = v => rig.ProcessorSetting = (FlexBase.ProcessorSettings)v,
            Min = 0,
            Max = 2,
            Step = 1,
            FineStep = 1,
            Axes = Radios.ValueLayerAxes.UpDown,
            Number = v => Radios.Lexicon.Get("audio.audio_layer.processor", ("name", ProcessorSettingName(v))),
            Note = () => rig.ProcessorOn == FlexBase.OffOnValues.on
                ? "" : Radios.Lexicon.Get("audio.audio_layer.processor_is_off_suffix"),
            WrongAxisHint = () => Radios.Lexicon.Get("audio.audio_layer.uses_up_down", ("target", processorName)),
        };

        // Pan binds to whichever slice is active AT EACH PRESS, so a
        // Shift+letter jump inside the layer moves it to the new slice;
        // the engine re-seeds it after the jump (PerSlice). The number
        // form is the SAME string the Slice Operations field speaks for
        // its incremental pan — one vocabulary.
        var pan = new Radios.ValueTarget
        {
            Id = "pan",
            Name = "",
            SelectKey = Keys.P | Keys.Control,
            PerSlice = true,
            Read = () => { int vfo = rig.RXVFO; return rig.ValidVFO(vfo) ? rig.GetVFOPan(vfo) : centre; },
            Apply = v => { int vfo = rig.RXVFO; if (rig.ValidVFO(vfo)) rig.SetVFOPan(vfo, v); },
            Min = FlexBase.MinPan,
            Max = FlexBase.MaxPan,
            Step = 5,
            FineStep = 1,
            Axes = Radios.ValueLayerAxes.Both,
            Anchor = centre,
            Number = v => Radios.Lexicon.Get("settings.pan.level", ("level", v)),
            Words = Radios.PanPhrase.Words,
            DescribeSelected = v =>
            {
                int vfo = rig.RXVFO;
                if (!rig.ValidVFO(vfo)) return Radios.Lexicon.Get("audio.audio_layer.pan_no_slice");
                return Radios.Lexicon.Get("audio.audio_layer.pan_selected",
                    Radios.ScreenReaderOutput.CurrentVerbosity,
                    ("letter", rig.VFOToLetter(vfo)), ("level", v),
                    ("position", Radios.PanPhrase.Words(v)));
            },
        };

        var def = new Radios.ValueSubLayerDefinition
        {
            Id = "audio",
            Selection = Radios.ValueLayerSelection.ByLetter,
            Targets = new List<Radios.ValueTarget> { headphone, pcOutput, mic, lineout, compander, processor, pan },
            InitialTarget = onPan ? 6 : -1,
            DescribeLayerEntry = layer => layer.CurrentTarget == pan
                ? Radios.Lexicon.Get("audio.audio_layer.entered_on_pan",
                    Radios.ScreenReaderOutput.CurrentVerbosity, ("target", layer.DescribeTarget(pan)))
                : Radios.Lexicon.Get("audio.audio_layer.entered", Radios.ScreenReaderOutput.CurrentVerbosity),
            DescribeLayerHelp = layer => KeyInventory.LayerHelpSpeech(
                KeyInventory.AudioLayerContext,
                Radios.Lexicon.Get("audio.audio_layer.name") + ", "
                + (layer.CurrentTarget != null
                    ? layer.DescribeTarget(layer.CurrentTarget)
                    : Radios.Lexicon.Get("audio.audio_layer.help_no_target"))),
            DescribeClosed = () => Radios.Lexicon.Get("audio.audio_layer.closed"),
            DescribeLayerRestored = (layer, restored) => restored.Count == 0
                ? Radios.Lexicon.Get("audio.audio_layer.restored_nothing")
                : Radios.Lexicon.Get("audio.audio_layer.restored", ("list", string.Join(", ",
                    restored.Select(r => r.Target == pan
                        ? Radios.Lexicon.Get("audio.audio_layer.pan_restore_item",
                            Radios.ScreenReaderOutput.CurrentVerbosity,
                            ("level", r.RestoredTo), ("position", Radios.PanPhrase.Words(r.RestoredTo)))
                        : layer.FormOf(r.Target, r.RestoredTo))))),
            PickTargetHint = () => Radios.Lexicon.Get("audio.audio_layer.pick_target_first"),
            // The verbosity cycle travels through the live layer, looked up
            // from the registry so a remapped chord is still honoured — an
            // operator can flip words-versus-numbers mid-hunt.
            PassThroughKeys = key => Lookup(key)?.KeyDef.Id == CommandValues.CycleVerbosity,
            HostKeys = LayerSliceJump,
            ListCommands = () => TryShowLayerCommandList(KeyInventory.AudioLayerContext),
            OpenExplorer = () => TryOpenLayerExplorer(KeyInventory.AudioLayerContext),
            Exited = why => { if (pcTouched) _context.GetMainWindow()?.PersistPcOutputVolume(); },
            Cues = new Radios.ValueLayerCues
            {
                Entered = EarconPlayer.LeaderEnterTone,
                Closed = EarconPlayer.LeaderCancelTone,
                Invalid = EarconPlayer.LeaderInvalidTone,
                Help = EarconPlayer.LeaderHelpTone,
                // #528: below Chatty a refused key is the thunk alone, so the
                // engine must know whether the thunk will be heard.
                Audible = () => EarconPlayer.IsOn(EarconPlayer.EarconCategory.CommandsAndConfirmations),
            },
        };

        _valueLayer = Radios.ValueSubLayer.Enter(def);
    }

    // ────────────────────────────────────────────────────────────────
    //  The filter layer — Sprint 44 Track I, #516. JJ key F once Track J
    //  wires the door; EnterFilterLayer is the method to wire.
    //
    //  The modifier picks the TARGET and the key picks the VERB: Left
    //  Shift is the low edge, Right Shift the high edge, no modifier the
    //  whole filter. Left and Right walk the addressed target; Up and Down
    //  slide the whole filter; Ctrl+Up and Ctrl+Down widen and narrow it
    //  about its centre; S speaks the addressed target; T and R switch
    //  between the transmit filter (radio-wide) and the receive filter
    //  (per slice, where the layer lands). The edges step explicitly and
    //  never accelerate — an edge is placed by number, and the ear will not
    //  say when 2,700 hertz has been reached.
    //
    //  The four targets on each side are COORDINATES on one low/high pair
    //  the host holds (FilterBank), so they are Linked: the engine re-reads
    //  each before stepping, and Escape restores through one snapshot of
    //  both pairs rather than coordinate by coordinate. The receive rails
    //  and step are the bracket chords' own (FreqOutHandlers), so the two
    //  doors into the same filter step by one rule; the transmit rails are
    //  the radio's (TXFilterLowMax = TXFilterHigh - 50), and a rail is said
    //  out loud because a control that silently refuses to move is
    //  indistinguishable from a broken one.
    //
    //  Not removed: the bracket chords and the double-tap edge grab. Don
    //  has learned them and they work; this is a second door. Not built:
    //  transmit presets — FilterPresets is driven from the slice's receive
    //  filter and has nothing to offer the transmit side (reported, not
    //  invented).
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// One side's low/high pair as the layer holds it — the shadow the
    /// four coordinate targets read and write, and the entry pair Escape
    /// puts back. Seeded from the radio once, at entry (and again for the
    /// receive side after a slice jump), then stepped locally.
    /// </summary>
    private sealed class FilterBank
    {
        public int Low, High, EntryLow, EntryHigh;
        public bool Touched;
        public int Width => High - Low;
        public void Seed(int low, int high)
        {
            Low = EntryLow = low;
            High = EntryHigh = high;
            Touched = false;
        }
    }

    /// <summary>Everything one side (receive or transmit) contributes.</summary>
    private sealed class FilterSide
    {
        public string Group = "";
        public string IdPrefix = "";
        public bool PerSlice;
        public FilterBank Bank = new FilterBank();
        public Func<int> LowMin = () => 0;
        public Func<int> HighMax = () => 0;
        public int MinWidth = 50;
        public Func<int> StepNow = () => 50;
        public Action<int, int> Apply = (l, h) => { };
        public Func<int, string> LowEdge = v => v.ToString();
        public Func<int, string> HighEdge = v => v.ToString();
        public Func<string> Range = () => "";
        public Func<string> Width = () => "";
        public Func<string> Report = () => "";
    }

    private FilterSide? _filterRx;

    private static int ClampSafe(int value, int min, int max)
        => max < min ? min : Math.Clamp(value, min, max);

    private static string AtLimit(string what)
        => Radios.Lexicon.Get("audio.filter_layer.at_limit", ("what", what));

    /// <summary>
    /// The edges a width would land on about the current centre, kept
    /// inside the side's bounds and never narrower than its minimum.
    /// </summary>
    private static (int low, int high) EdgesForWidth(FilterSide s, int width)
    {
        int centre = (s.Bank.Low + s.Bank.High) / 2;
        int half = width / 2;
        int low = centre - half;
        int high = centre + (width - half);
        int lowMin = s.LowMin(), highMax = s.HighMax();
        if (low < lowMin) low = lowMin;
        if (high > highMax) high = highMax;
        if (high - low < s.MinWidth)
        {
            high = low + s.MinWidth;
            if (high > highMax) { high = highMax; low = high - s.MinWidth; }
        }
        return (low, high);
    }

    /// <summary>The four coordinates of one side: low edge, high edge, whole, width.</summary>
    private static IEnumerable<Radios.ValueTarget> FilterTargetsFor(FilterSide s)
    {
        const int outer = 24000;
        yield return new Radios.ValueTarget
        {
            Id = s.IdPrefix + "-low",
            Group = s.Group,
            PerSlice = s.PerSlice,
            Linked = true,
            Axes = Radios.ValueLayerAxes.LeftRight,
            Shift = Radios.ShiftSide.Left,
            Min = -outer,
            Max = outer,
            StepNow = s.StepNow,
            Read = () => s.Bank.Low,
            Constrain = v => ClampSafe(v, s.LowMin(), s.Bank.High - s.MinWidth),
            Apply = v => s.Apply(v, s.Bank.High),
            Number = s.LowEdge,
            DescribeSelected = s.LowEdge,
            DescribeRail = v => AtLimit(s.LowEdge(v)),
        };
        yield return new Radios.ValueTarget
        {
            Id = s.IdPrefix + "-high",
            Group = s.Group,
            PerSlice = s.PerSlice,
            Linked = true,
            Axes = Radios.ValueLayerAxes.LeftRight,
            Shift = Radios.ShiftSide.Right,
            Min = -outer,
            Max = outer,
            StepNow = s.StepNow,
            Read = () => s.Bank.High,
            Constrain = v => ClampSafe(v, s.Bank.Low + s.MinWidth, s.HighMax()),
            Apply = v => s.Apply(s.Bank.Low, v),
            Number = s.HighEdge,
            DescribeSelected = s.HighEdge,
            DescribeRail = v => AtLimit(s.HighEdge(v)),
        };
        // The whole filter, positioned by its low edge with the width
        // carried along. Left/Right and Up/Down both slide it — no
        // modifier means the whole filter, whichever pair the hand is on.
        yield return new Radios.ValueTarget
        {
            Id = s.IdPrefix + "-filter",
            Group = s.Group,
            PerSlice = s.PerSlice,
            Linked = true,
            Axes = Radios.ValueLayerAxes.Both,
            Shift = Radios.ShiftSide.None,
            Min = -outer,
            Max = outer,
            StepNow = s.StepNow,
            Read = () => s.Bank.Low,
            Constrain = v => ClampSafe(v, s.LowMin(), s.HighMax() - s.Bank.Width),
            Apply = v => s.Apply(v, v + s.Bank.Width),
            Number = _ => s.Range(),
            DescribeSelected = _ => s.Report(),
            DescribeRail = _ => AtLimit(s.Range()),
        };
        // Width about the centre: one step on each side per press.
        yield return new Radios.ValueTarget
        {
            Id = s.IdPrefix + "-width",
            Group = s.Group,
            PerSlice = s.PerSlice,
            Linked = true,
            Axes = Radios.ValueLayerAxes.UpDown,
            Ctrl = true,
            Min = s.MinWidth,
            Max = outer,
            StepNow = () => 2 * s.StepNow(),
            Read = () => s.Bank.Width,
            Constrain = v => { var (l, h) = EdgesForWidth(s, v); return h - l; },
            Apply = v => { var (l, h) = EdgesForWidth(s, v); s.Apply(l, h); },
            Number = _ => s.Width(),
            DescribeSelected = _ => s.Width(),
            DescribeRail = _ => AtLimit(s.Width()),
        };
    }

    /// <summary>
    /// Write a transmit pair through the two radio setters. They queue in
    /// order and FlexLib clamps each edge against the OTHER edge's current
    /// value, so the edge that opens the gap goes first; an edge that has
    /// not moved is not written.
    /// </summary>
    private static void ApplyTxFilter(Radios.FlexBase rig, FilterBank tx, int low, int high)
    {
        bool lowMoves = low != tx.Low, highMoves = high != tx.High;
        if (low < tx.Low)
        {
            if (lowMoves) rig.TXFilterLow = low;
            if (highMoves) rig.TXFilterHigh = high;
        }
        else
        {
            if (highMoves) rig.TXFilterHigh = high;
            if (lowMoves) rig.TXFilterLow = low;
        }
        tx.Low = low;
        tx.High = high;
        tx.Touched = true;
    }

    /// <summary>
    /// The filter report — low, high, width in kilohertz — in the one
    /// wording both the layer and the Ctrl+J readouts use, so two doors
    /// into the same fact say the same sentence.
    /// </summary>
    private static string FilterReport(string key, int low, int high)
        => Radios.Lexicon.Get(key, ("low", low), ("high", high),
            ("widthKHz", ((high - low) / 1000.0).ToString("F1")));

    /// <summary>
    /// Enter the filter layer on the active slice's receive filter. See the
    /// region comment for the grammar.
    /// </summary>
    internal void EnterFilterLayer()
    {
        var rig = _context.GetRigControl();
        if (rig == null) { LeaderNoRadio(); return; }
        if (!rig.ValidVFO(rig.RXVFO))
        {
            EarconPlayer.LeaderInvalidTone();
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("audio.filter_layer.no_slice"),
                Radios.VerbosityLevel.Critical, true);
            return;
        }

        var rx = new FilterSide
        {
            Group = "receive",
            IdPrefix = "rx",
            PerSlice = true,
            LowMin = () => FreqOutHandlers.FilterBoundsForMode(rig.Mode).lowMin,
            HighMax = () => FreqOutHandlers.FilterBoundsForMode(rig.Mode).highMax,
            MinWidth = FreqOutHandlers.MinFilterWidthHz,
        };
        rx.StepNow = () => FreqOutHandlers.GetAdaptiveFilterStep(rx.Bank.Low, rx.Bank.High);
        rx.Apply = (l, h) => { rx.Bank.Low = l; rx.Bank.High = h; rx.Bank.Touched = true; rig.SetFilter(l, h); };
        rx.LowEdge = v => Radios.Lexicon.Get("audio.filter.low_edge", ("low", v));
        rx.HighEdge = v => Radios.Lexicon.Get("audio.filter.high_edge", ("high", v));
        rx.Range = () => Radios.Lexicon.Get("audio.filter.range", ("low", rx.Bank.Low), ("high", rx.Bank.High));
        rx.Width = () => Radios.Lexicon.Get("audio.filter_layer.width",
            ("width", rx.Bank.Width), ("low", rx.Bank.Low), ("high", rx.Bank.High));
        rx.Report = () => FilterReport("audio.filter.rx_report", rx.Bank.Low, rx.Bank.High);

        var tx = new FilterSide
        {
            Group = "transmit",
            IdPrefix = "tx",
            PerSlice = false,
            LowMin = () => rig.TXFilterLowMin,
            HighMax = () => rig.TXFilterHighMax,
            MinWidth = rig.TXFilterLowIncrement,
            StepNow = () => rig.TXFilterLowIncrement,
        };
        tx.Apply = (l, h) => ApplyTxFilter(rig, tx.Bank, l, h);
        tx.LowEdge = v => Radios.Lexicon.Get("audio.tx.filter_low", ("value", v));
        tx.HighEdge = v => Radios.Lexicon.Get("audio.tx.filter_high", ("value", v));
        tx.Range = () => Radios.Lexicon.Get("audio.tx_filter.range", ("low", tx.Bank.Low), ("high", tx.Bank.High));
        tx.Width = () => Radios.Lexicon.Get("audio.filter_layer.tx_width",
            ("width", tx.Bank.Width), ("low", tx.Bank.Low), ("high", tx.Bank.High));
        tx.Report = () => FilterReport("audio.filter.tx_report", tx.Bank.Low, tx.Bank.High);

        _filterRx = rx;

        var def = new Radios.ValueSubLayerDefinition
        {
            Id = "filter",
            Selection = Radios.ValueLayerSelection.ByModifier,
            Targets = FilterTargetsFor(rx).Concat(FilterTargetsFor(tx)).ToList(),
            GroupKeys = new Dictionary<Keys, string> { [Keys.R] = "receive", [Keys.T] = "transmit" },
            InitialGroup = "receive",
            DescribeGroup = g => g == "transmit" ? tx.Report() : rx.Report(),
            SpeakKey = Keys.S,
            ShiftSideNow = PhysicalKeys.ShiftSideNow,
            // Seeded from the radio ONCE, here, at entry; the restore puts
            // back only the side that moved.
            Snapshot = () =>
            {
                rx.Bank.Seed(rig.FilterLow, rig.FilterHigh);
                tx.Bank.Seed(rig.TXFilterLow, rig.TXFilterHigh);
                return () =>
                {
                    if (rx.Bank.Touched) rig.SetFilter(rx.Bank.EntryLow, rx.Bank.EntryHigh);
                    if (tx.Bank.Touched) ApplyTxFilter(rig, tx.Bank, tx.Bank.EntryLow, tx.Bank.EntryHigh);
                };
            },
            DescribeLayerEntry = layer => Radios.Lexicon.Get("audio.filter_layer.entered",
                Radios.ScreenReaderOutput.CurrentVerbosity, ("filter", rx.Report())),
            DescribeLayerHelp = layer => KeyInventory.LayerHelpSpeech(
                KeyInventory.FilterLayerContext,
                Radios.Lexicon.Get("audio.filter_layer.name") + ", "
                + (layer.CurrentGroup == "transmit" ? tx.Report() : rx.Report())),
            DescribeClosed = () => Radios.Lexicon.Get("audio.filter_layer.closed"),
            DescribeLayerRestored = (layer, restored) =>
            {
                var parts = new List<string>();
                if (rx.Bank.Touched)
                    parts.Add(Radios.Lexicon.Get("audio.filter_layer.restored_receive",
                        ("low", rx.Bank.EntryLow), ("high", rx.Bank.EntryHigh)));
                if (tx.Bank.Touched)
                    parts.Add(Radios.Lexicon.Get("audio.filter_layer.restored_transmit",
                        ("low", tx.Bank.EntryLow), ("high", tx.Bank.EntryHigh)));
                return parts.Count == 0
                    ? Radios.Lexicon.Get("audio.filter_layer.restored_nothing")
                    : Radios.Lexicon.Get("audio.filter_layer.restored", ("list", string.Join(", ", parts)));
            },
            WhichShiftHint = () => Radios.Lexicon.Get("audio.filter_layer.which_shift"),
            NoVerbHint = () => Radios.Lexicon.Get("audio.filter_layer.no_verb"),
            WrongAxisHint = () => Radios.Lexicon.Get("audio.filter_layer.no_verb"),
            PassThroughKeys = key => Lookup(key)?.KeyDef.Id == CommandValues.CycleVerbosity,
            HostKeys = LayerSliceJump,
            ListCommands = () => TryShowLayerCommandList(KeyInventory.FilterLayerContext),
            OpenExplorer = () => TryOpenLayerExplorer(KeyInventory.FilterLayerContext),
            Exited = why => { _filterRx = null; },
            Cues = new Radios.ValueLayerCues
            {
                Entered = EarconPlayer.LeaderEnterTone,
                Closed = EarconPlayer.LeaderCancelTone,
                Invalid = EarconPlayer.LeaderInvalidTone,
                Help = EarconPlayer.LeaderHelpTone,
                // #528: below Chatty a refused key is the thunk alone, so the
                // engine must know whether the thunk will be heard.
                Audible = () => EarconPlayer.IsOn(EarconPlayer.EarconCategory.CommandsAndConfirmations),
            },
        };

        _valueLayer = Radios.ValueSubLayer.Enter(def);
    }

    // ────────────────────────────────────────────────────────────────
    //  Hooks every layer shares
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The universal slice jump from inside a layer (#515): Shift+A through
    /// Shift+H jump to that slice and the layer stays live. Per-slice
    /// targets re-bind to the new slice — pan re-seeds and is announced
    /// again; the filter layer's receive side re-seeds from the new slice's
    /// filter and, if the receive side is what the operator is working on,
    /// says so. What was done on the old slice is kept, not restored: the
    /// operator confirmed it by leaving. The transmit filter is radio-wide,
    /// so on that side the jump changes the slice and nothing else.
    /// </summary>
    private bool LayerSliceJump(Keys k)
    {
        if ((k & Keys.Modifiers) != Keys.Shift) return false;
        Keys code = k & Keys.KeyCode;
        if (code < Keys.A || code > Keys.H) return false;

        var rig = _context.GetRigControl();
        int before = rig?.RXVFO ?? -1;
        JumpToSlice(code - Keys.A);
        if (rig == null || rig.RXVFO == before || _valueLayer == null) return true;

        if (_filterRx != null)
        {
            _filterRx.Bank.Seed(rig.FilterLow, rig.FilterHigh);
            _valueLayer.Rebind(t => t.PerSlice);
            if (_valueLayer.CurrentGroup == "receive") _valueLayer.SwitchGroup("receive");
        }
        else
        {
            _valueLayer.Rebind(t => t.PerSlice);
        }
        return true;
    }

    /// <summary>
    /// H inside a layer: show its commands as a NAVIGABLE LIST (#519).
    /// Returns false until Track K's list surface lands, and the engine
    /// then speaks the same rows, count first. MERGE POINT for Track K:
    /// call the list surface here with KeyInventory.LayerCommands(context)
    /// and return true once it is showing.
    /// </summary>
    private bool TryShowLayerCommandList(string context)
    {
        _context.Trace("Layer help list requested: " + context);
        return false;
    }

    /// <summary>
    /// Shift+slash inside a layer: open the JJ key tree explorer on this
    /// layer (#519). Returns false until Track K's explorer lands, and the
    /// engine then speaks the same rows, count first — Shift+slash stays
    /// help in every layer (#158). MERGE POINT for Track K.
    /// </summary>
    private bool TryOpenLayerExplorer(string context)
    {
        _context.Trace("Layer explorer requested: " + context);
        return false;
    }

    /// <summary>
    /// Route one key to the live value sub-layer, handling what only the
    /// host can: a vanished radio, and the Ctrl+J hand-off to a fresh
    /// leader chord (volume mode's precedent).
    /// </summary>
    private Radios.ValueLayerKeyResult DoValueLayerKey(Keys k)
    {
        var layer = _valueLayer!;

        // Radio gone under the layer — close it out loud, write nothing.
        if (_context.GetRigControl() == null)
        {
            _valueLayer = null;
            layer.Drop();
            LeaderNoRadio();
            return Radios.ValueLayerKeyResult.Handled;
        }

        var result = layer.HandleKey(k);
        switch (result)
        {
            case Radios.ValueLayerKeyResult.Closed:
            case Radios.ValueLayerKeyResult.ClosedPassThrough:
                _valueLayer = null;
                break;

            case Radios.ValueLayerKeyResult.ClosedHandOff:
                // Ctrl+J: the layer confirmed silently; arm the leader
                // exactly as if pressed from anywhere else.
                _valueLayer = null;
                _leaderKeyActive = true;
                EarconPlayer.LeaderEnterTone();
                Radios.ScreenReaderOutput.Speak(
                    Radios.Lexicon.Get("leader.armed"),
                    Radios.VerbosityLevel.Terse, true);
                return Radios.ValueLayerKeyResult.Handled;
        }
        return result;
    }

    private void LeaderNoRadio()
    {
        EarconPlayer.LeaderInvalidTone();
        Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.command_needs_radio"), Radios.VerbosityLevel.Critical);
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
            msg = Radios.Lexicon.Get("audio.mic.no_reading_yet");
        else if (rig.ScMicMaxDb > -140f)
            msg = FormatMicVerdict(rig.ScMicMaxDb, lastTransmit: true);
        else
            msg = Radios.Lexicon.Get("audio.mic.transmit_to_measure");
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
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.tx_tone.disarmed"),
                Radios.VerbosityLevel.Critical, true);
            return;
        }

        string trouble = rig.TxTonePathTrouble;
        if (!string.IsNullOrEmpty(trouble))
        {
            EarconPlayer.LeaderInvalidTone();
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("audio.tx_tone.not_armed", ("trouble", trouble)),
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

        string line = Radios.Lexicon.Get("audio.tx_tone.armed", ("freq", freq), ("level", level));
        if (freq < rig.TXFilterLow || freq > rig.TXFilterHigh)
        {
            line += Radios.Lexicon.Get("audio.tx_tone.outside_filter_warning",
                ("freq", freq), ("low", rig.TXFilterLow), ("high", rig.TXFilterHigh));
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
            return Radios.Lexicon.Get("audio.tx_tone.armed_not_going_out", ("trouble", trouble));
        int freq = (int)rig.TxToneFrequency;
        string line = Radios.Lexicon.Get("audio.tx_tone.riding_transmit", ("freq", freq));
        if (freq < rig.TXFilterLow || freq > rig.TXFilterHigh)
            line += Radios.Lexicon.Get("audio.tx_tone.riding_outside_filter_warning");
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
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("audio.dsp.toggled_on", ("label", label)), Radios.VerbosityLevel.Terse);
        }
        else
        {
            EarconPlayer.FeatureOffTone();
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("audio.dsp.toggled_off", ("label", label)), Radios.VerbosityLevel.Terse);
        }
    }

    private void ToggleTuneDebounce()
    {
        var mainWindow = _context.GetMainWindow();
        var config = mainWindow?.CurrentAudioConfig;
        if (config == null)
        {
            EarconPlayer.LeaderInvalidTone();
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("settings.tune_debounce.no_audio_config"), Radios.VerbosityLevel.Critical);
            return;
        }

        config.TuneDebounceEnabled = !config.TuneDebounceEnabled;
        if (config.TuneDebounceEnabled)
        {
            EarconPlayer.FeatureOnTone();
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("settings.tune_debounce.on"), Radios.VerbosityLevel.Terse);
        }
        else
        {
            EarconPlayer.FeatureOffTone();
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("settings.tune_debounce.off"), Radios.VerbosityLevel.Terse);
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
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.command_needs_radio"), Radios.VerbosityLevel.Critical);
            return;
        }
        Radios.ScreenReaderOutput.Speak(
            FilterReport("audio.filter.rx_report", rig.FilterLow, rig.FilterHigh),
            Radios.VerbosityLevel.Terse);
    }

    private void SpeakTXFilterWidth()
    {
        var rig = _context.GetRigControl();
        if (rig == null)
        {
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.command_needs_radio"), Radios.VerbosityLevel.Critical);
            return;
        }
        Radios.ScreenReaderOutput.Speak(
            FilterReport("audio.filter.tx_report", rig.TXFilterLow, rig.TXFilterHigh),
            Radios.VerbosityLevel.Terse);
    }

    /// <summary>
    /// Ctrl+J, Ctrl+D — start or stop a detailed capture.
    ///
    /// The chord exists because the Settings dialog may be part of the problem
    /// being captured, and because the moment worth recording is rarely a
    /// moment when you can go looking for a menu. Same implementation, same
    /// spoken confirmations, as the Diagnostics tab's button and the Command
    /// Finder command — one behaviour with three doors, not three behaviours.
    /// </summary>
    private void ToggleDetailedCaptureFromChord()
    {
        if (!DiagnosticsBridge.IsAvailable)
        {
            EarconPlayer.LeaderInvalidTone();
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("settings.diagnostics.capture_unavailable"),
                Radios.VerbosityLevel.Critical);
            return;
        }
        // The tone moved into ToggleCapture itself (#128 sweep audit,
        // 2026-08-21). It used to live here, which meant the chord toned and
        // the Diagnostics-tab button and Command Finder command — the other
        // two doors into the same choke — were silent. All three doors now
        // inherit the earcon-first behaviour from the one place the state
        // actually changes.
        DiagnosticsBridge.ToggleCapture("Ctrl+J Ctrl+D");
    }

    /// <summary>
    /// Ctrl+J, Ctrl+R — read the problems recorded this session.
    ///
    /// This chord is the whole reason the failure-moment window could be
    /// deleted (#100). A failure now announces itself once, quietly, over the
    /// top of nothing — and if the operator misses that announcement, this key
    /// is how they ask what they missed. Noel's objection to Windows toast was
    /// never that it is quiet; it is that it is EPHEMERAL, so the answer is
    /// retrievability, not volume.
    ///
    /// An empty list opens no window and says so. A window whose entire content
    /// is "nothing to see" is a window the operator has to close for no reason —
    /// and opening one would flush the speech queue to deliver less information
    /// than the sentence it destroyed.
    /// </summary>
    private void ShowRecordedProblemsFromChord()
    {
        try
        {
            var mw = _context.GetMainWindow();
            if (mw != null)
                mw.Dispatcher.Invoke(() => Dialogs.ProblemsDialog.ShowOrSpeakEmpty());
            else
                Dialogs.ProblemsDialog.ShowOrSpeakEmpty();
        }
        catch
        {
            EarconPlayer.LeaderInvalidTone();
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("settings.diagnostics.problems_list_failed"),
                Radios.VerbosityLevel.Critical);
        }
    }

    /// <summary>
    /// Ctrl+J, O — read out everything expensive that is currently running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The on-demand read of the register (#253). A sighted operator answers
    /// this question by looking at a recording indicator, a moving meter and a
    /// panel that is obviously open. This is that glance.
    /// </para>
    /// <para>
    /// <b>It does not poll for thresholds first.</b> Tempting — the operator is
    /// right here — but a bound crossing would then be announced on top of the
    /// answer they actually asked for, and the threshold read exists precisely
    /// so that nobody has to ask.
    /// </para>
    /// <para>
    /// Interrupts, because it is an answer to a keypress. Never silent: an
    /// empty register still says so, since silence reads as the key not
    /// working.
    /// </para>
    /// </remarks>
    private void SpeakRunningCostsFromChord()
    {
        try
        {
            _context.Trace("Leader:running costs");
            Radios.ScreenReaderOutput.Speak(
                Radios.RunningCostRegister.DescribeForSpeech(),
                Radios.VerbosityLevel.Critical, true);
        }
        catch
        {
            EarconPlayer.LeaderInvalidTone();
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("logging.running.unavailable"),
                Radios.VerbosityLevel.Critical);
        }
    }

    /// <summary>
    /// Ctrl+J, Ctrl+Q — start or stop the QSO signal analyzer (#271).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Starting needs a radio; stopping deliberately does not, so a capture
    /// can always be ended — including after a disconnect, when the buffer
    /// still holds a window worth reporting on.
    /// </para>
    /// <para>
    /// The stop headline interrupts at Critical, because it is the answer to
    /// a keypress — the same contract as Ctrl+J, O. The detail report is
    /// baked into the saved capture; nothing here renders it.
    /// </para>
    /// </remarks>
    private void ToggleQsoSignalCaptureFromChord()
    {
        try
        {
            if (Radios.SignalCapture.QsoSignalCaptureController.IsRunning)
            {
                _context.Trace("Leader:QSO capture stop");
                var result = Radios.SignalCapture.QsoSignalCaptureController.Stop(
                    "stopped by you", out bool saved);
                EarconPlayer.FeatureOffTone();
                // A lost race with the exit-path stop leaves nothing to report.
                if (result == null) return;
                Radios.ScreenReaderOutput.Speak(
                    Radios.SignalCapture.QsoSignalHeadline.Compose(
                        result.Analysis, result.Record.CaptureId, saved,
                        // The capture's own band, so the headline and the saved
                        // report can never name different S-units for it (#296).
                        Radios.SMeterReading.BandFor(result.Record.FrequencyHz)),
                    Radios.VerbosityLevel.Critical, true);
                return;
            }

            var rig = _context.GetRigControl();
            if (rig == null)
            {
                LeaderNoRadio();
                return;
            }

            _context.Trace("Leader:QSO capture start");
            Radios.SignalCapture.QsoSignalCaptureController.Start(rig);
            EarconPlayer.FeatureOnTone();
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.qso.started"),
                Radios.VerbosityLevel.Critical, true);
        }
        catch
        {
            EarconPlayer.LeaderInvalidTone();
            Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("audio.qso.failed"),
                Radios.VerbosityLevel.Critical);
        }
    }

    /// <summary>
    /// The keys that open the JJ key's two help doors (#514, #519): H lists
    /// the commands, the slash key opens the explorer. These, and Escape, are
    /// the only keys the layer stays armed for after an unknown key (#303),
    /// because they are the keys the unknown-key sentence names — and all of
    /// them lead OUT.
    /// </summary>
    /// <remarks>
    /// Both arrival forms of the slash key, deliberately. "?" is Shift+/ on a
    /// US layout and arrives as <c>Oem2 | Shift</c>; the bare <c>Oem2</c> twin
    /// is carried for the same reason <see cref="DoLeaderCommand"/> carries
    /// it — a bare Oem case alone never fires, which is exactly how the
    /// advertised "?" sat dead for months (#183).
    /// </remarks>
    private static bool IsLeaderHelpKey(Keys k) =>
        k == Keys.H || k == Keys.Oem2 || k == (Keys.Oem2 | Keys.Shift);

    /// <summary>
    /// Close the leader layer the way Escape closes it: descending tone, then
    /// "Cancelled". One place, so the help-armed exit and the ordinary exit
    /// can never start sounding different from each other.
    /// </summary>
    private void LeaderCancel()
    {
        EarconPlayer.LeaderCancelTone();
        Radios.ScreenReaderOutput.Speak(
            Radios.Lexicon.Get("leader.cancelled"), Radios.VerbosityLevel.Terse, true);
    }

    /// <summary>
    /// JJ key slash — the JJ key explorer (#519): a map of every layer and
    /// its keys that an operator moves through at their own pace, the answer
    /// to "what can I press?" that a one-breath recitation cannot be (#158).
    /// </summary>
    /// <remarks>
    /// <para><b>Sprint 44 Track K built the explorer on its own branch, and
    /// its published entry point is <c>KeyExplorerDialog.Open()</c>; this
    /// method is where that call goes at the merge.</b> Track J owns the
    /// binding (the slash key in <see cref="DoLeaderCommand"/> and in the
    /// help-armed dispatch), Track K owns the surface, and the two branches
    /// cannot see each other — so until they meet, the slash key gives the
    /// same list H gives, the explorer's content in the only shape this
    /// branch has, rather than dead-ending. Replace the body, keep the name:
    /// both call sites and JjKeyGrammarTests depend on it. Marshal onto the
    /// main window's dispatcher the way Track K's <c>LeaderKeyHelp</c> does,
    /// because this can be reached from a dialog's key handler.</para>
    /// <para>The inventory row already describes the destination, so the
    /// Command Finder, the Keys dialog and the explorer itself (which reads
    /// the inventory) need no re-wording when the body changes.</para>
    /// </remarks>
    private void OpenKeyExplorer()
    {
        LeaderKeyHelp();
    }

    /// <summary>
    /// JJ key H — this layer's commands. Under #519 (Sprint 44 Track K) this
    /// becomes a navigable list rather than a recitation.
    /// </summary>
    private void LeaderKeyHelp()
    {
        EarconPlayer.LeaderHelpTone();
        // Sprint 44 Track K (#158, #519): a navigable list, no longer a
        // recitation. Until now this spoke KeyInventory.LeaderHelpSpeech() —
        // 1,576 characters, 255 words, thirty items, 51 to 85 seconds of
        // speech with no way back. KeyLayerHelp reads the same table, says
        // the count first, speaks a short layer and lists a long one. This
        // layer is long. The tone stays here: it is this chord's cue, and a
        // layer's own H plays its own.
        var mw = _context.GetMainWindow();
        if (mw != null)
            mw.Dispatcher.Invoke(() => KeyLayerHelp.Present(KeyLayerHelp.LeaderContext));
        else
            KeyLayerHelp.Present(KeyLayerHelp.LeaderContext);
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

        clean &= ValidateUnboundAnnotations();

        return clean;
    }

    /// <summary>
    /// Every command that defaults to no key must say why, and nothing that
    /// HAS a key may still be claiming it does not.
    /// </summary>
    /// <remarks>
    /// This is what stops task #130 from being a one-off tidy-up. The whole
    /// problem was twenty-nine unbound commands with no way to tell a
    /// deliberate silence from an oversight; a documentation table that
    /// nothing verifies becomes exactly that again in two sprints, because
    /// adding a command is easy and remembering a table is not.
    ///
    /// <para>Traced rather than thrown: a mis-annotated key table is a
    /// housekeeping fault, and refusing to start the radio over it would be a
    /// wildly disproportionate response for an operator who just wants to get
    /// on the air.</para>
    /// </remarks>
    private bool ValidateUnboundAnnotations()
    {
        bool clean = true;

        foreach (var d in _defaultKeys)
        {
            if (d.Key == Keys.None)
            {
                if (!_unboundNotes.ContainsKey(d.Id))
                {
                    _context.Trace(
                        $"UNANNOTATED UNBOUND COMMAND: {d.Id} has no default key and no entry "
                        + "in _unboundNotes. Say why it has no key — 'menu-only on purpose' and "
                        + "'nobody ever assigned one' need opposite treatment (task #130).");
                    clean = false;
                }
            }
            else if (_unboundNotes.ContainsKey(d.Id))
            {
                _context.Trace(
                    $"STALE UNBOUND NOTE: {d.Id} is bound to {d.Key} but still has an entry in "
                    + "_unboundNotes. Remove the note.");
                clean = false;
            }
        }

        var ids = new HashSet<CommandValues>(_defaultKeys.Select(d => d.Id));
        foreach (var id in _unboundNotes.Keys)
        {
            if (ids.Contains(id)) continue;
            _context.Trace(
                $"ORPHANED UNBOUND NOTE: {id} has an entry in _unboundNotes but no row in "
                + "_defaultKeys.");
            clean = false;
        }

        if (clean)
            _context.Trace(
                $"ValidateKeyBindings: all {_unboundNotes.Count} unbound commands annotated");

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
