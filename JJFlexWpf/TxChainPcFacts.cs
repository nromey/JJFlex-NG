using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using JJTrace;
using Radios;
using Radios.ChainChecks;

namespace JJFlexWpf;

/// <summary>
/// The half of the transmit chain that lives on this computer, stated as facts
/// the rule engine can reason about.
/// </summary>
/// <remarks>
/// <para>
/// This is here rather than beside <see cref="TxChainFacts"/> because the radio
/// layer sits BELOW this one and cannot see a Windows audio endpoint. Splitting
/// the collection along that line keeps the layering honest and costs nothing:
/// the engine takes facts from anywhere, and a fact says where it came from.
/// </para>
/// <para>
/// The Windows microphone mute is the reason this exists at all. A mute here is
/// completely invisible to every radio-side observable — the radio simply hears
/// nothing and reports a floor — so a transmit diagnostic that only asked the
/// radio would send an operator to look at their rig for a problem sitting in
/// their sound settings.
/// </para>
/// </remarks>
internal static class TxChainPcFacts
{
    /// <summary>
    /// Read this computer's microphone selection and its Windows level.
    /// </summary>
    /// <param name="audioDevicesPath">Full path to audioDevices.xml. Null or
    /// missing yields facts that say the selection could not be read, which is
    /// a different answer from "no microphone is chosen".</param>
    public static IReadOnlyList<DiagnosticFact> Collect(string? audioDevicesPath)
    {
        var facts = new List<DiagnosticFact>();

        string? name = null;
        int hostApiTypeId = -1;
        string hostApiName = "";
        string readFailure = "";

        try
        {
            if (string.IsNullOrEmpty(audioDevicesPath))
            {
                readFailure = "this computer's audio device settings file could not be located";
            }
            else if (!File.Exists(audioDevicesPath))
            {
                readFailure = "this computer's audio device settings have not been saved yet";
            }
            else
            {
                var devices = new JJPortaudio.Devices(audioDevicesPath);
                devices.LoadSavedSelection();
                name = devices.InputDevice?.Name;
                hostApiTypeId = devices.InputDevice?.hostApiTypeId ?? -1;
                hostApiName = devices.InputDevice?.hostApiName ?? "";
            }
        }
        catch (Exception ex)
        {
            readFailure = "reading this computer's audio device settings failed: " + ex.Message;
            Tracing.TraceLine("TxChainPcFacts: could not read the chosen input device — " + ex.Message,
                              TraceLevel.Warning);
        }

        const string label = "Microphone chosen on this computer";

        if (readFailure.Length != 0)
        {
            // Unreadable, not unconfigured. Collapsing the two would let a
            // file-read hiccup masquerade as "you never chose a microphone",
            // and send a fully set-up operator back to step one.
            facts.Add(DiagnosticFact.Absent("pc-input-device", label, readFailure, "this computer"));
            facts.Add(DiagnosticFact.Absent("pc-input-device-present",
                "The chosen microphone is present", readFailure, "this computer"));
            facts.Add(DiagnosticFact.Absent("pc-mic-muted",
                "Windows has the microphone muted", readFailure, "this computer"));
            facts.Add(DiagnosticFact.Absent("pc-mic-level",
                "Windows input level for the microphone", readFailure, "this computer"));
            return facts;
        }

        facts.Add(DiagnosticFact.Text("pc-input-device", label, name ?? "", "this computer"));
        if (hostApiName.Length != 0)
        {
            facts.Add(DiagnosticFact.Text("pc-audio-driver",
                "Audio driver this computer uses for the microphone", hostApiName, "this computer"));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            const string none = "no microphone has been chosen on this computer, so there is none to look at";
            facts.Add(DiagnosticFact.Absent("pc-input-device-present",
                "The chosen microphone is present", none, "this computer"));
            facts.Add(DiagnosticFact.Absent("pc-mic-muted",
                "Windows has the microphone muted", none, "this computer"));
            facts.Add(DiagnosticFact.Absent("pc-mic-level",
                "Windows input level for the microphone", none, "this computer"));
            return facts;
        }

        WindowsMicLevel? level = null;
        string whyNot = "";
        try
        {
            level = WindowsMicLevel.TryFindByName(name, hostApiTypeId, out whyNot);
        }
        catch (Exception ex)
        {
            whyNot = "looking the microphone up in Windows failed: " + ex.Message;
            Tracing.TraceLine("TxChainPcFacts: WindowsMicLevel lookup threw — " + ex.Message,
                              TraceLevel.Warning);
        }

        if (level == null)
        {
            // Windows cannot find the endpoint the saved selection names. That
            // is a real, reportable fault — the microphone is gone or moved —
            // and it is why this is a flag rather than an absence.
            facts.Add(DiagnosticFact.Flag("pc-input-device-present",
                "The chosen microphone is present", false, "this computer"));
            facts.Add(DiagnosticFact.Text("pc-input-device-missing-reason",
                "Why the chosen microphone could not be found",
                whyNot.Length != 0 ? whyNot : "Windows did not report it", "this computer"));
            const string gone = "the chosen microphone could not be found in Windows";
            facts.Add(DiagnosticFact.Absent("pc-mic-muted",
                "Windows has the microphone muted", gone, "this computer"));
            facts.Add(DiagnosticFact.Absent("pc-mic-level",
                "Windows input level for the microphone", gone, "this computer"));
            return facts;
        }

        using (level)
        {
            facts.Add(DiagnosticFact.Flag("pc-input-device-present",
                "The chosen microphone is present", true, "this computer"));

            Probe(facts, "pc-mic-muted", "Windows has the microphone muted",
                  () => DiagnosticFact.Flag("pc-mic-muted", "Windows has the microphone muted",
                                            level.Muted, "Windows"));
            Probe(facts, "pc-mic-level", "Windows input level for the microphone",
                  () => DiagnosticFact.Measure("pc-mic-level", "Windows input level for the microphone",
                                               Math.Round(level.Percent), "percent", "Windows"));
            if (level.HasBoost)
            {
                Probe(facts, "pc-mic-boost", "Microphone boost in Windows",
                      () => DiagnosticFact.Measure("pc-mic-boost", "Microphone boost in Windows",
                                                   level.BoostDb, "dB", "Windows"));
            }
        }

        return facts;
    }

    /// <summary>
    /// The software half of the evidence block. Reads
    /// <see cref="DiagnosticSnapshot"/> rather than assembling its own version
    /// strings, because two assemblers is how a report and an About page end up
    /// disagreeing about what is running.
    /// </summary>
    public static IReadOnlyList<string> BuildLines()
    {
        var lines = new List<string>();
        try
        {
            DiagnosticSnapshot snap = DiagnosticSnapshot.Capture();
            lines.Add("JJ Flexible version: " + snap.AppDisplayVersion);
            lines.Add("Build identity: " + snap.AppInformationalVersion);
            lines.Add("FlexLib version: " + snap.FlexLibVersion);
            lines.Add("Opus: " + snap.OpusVersion);
            lines.Add("PortAudio: " + snap.PortAudioVersionText);
            lines.Add("Windows: " + snap.OsVersion + ", " + snap.OsArchitecture);
            lines.Add("Running as: " + snap.ProcessArchitecture);
            lines.Add("Screen reader: " + snap.ScreenReader);
            lines.Add("Diagnostic log: " + (snap.TracingActive ? snap.TraceFilePath : "not running"));
        }
        catch (Exception ex)
        {
            lines.Add("The software details could not be read: " + ex.Message);
        }
        return lines;
    }

    private static void Probe(List<DiagnosticFact> into, string name, string label,
                              Func<DiagnosticFact> probe)
    {
        try
        {
            DiagnosticFact f = probe();
            if (f != null) into.Add(f);
        }
        catch (Exception ex)
        {
            into.Add(DiagnosticFact.Absent(name, label,
                "reading it from Windows failed: " + ex.Message, "Windows"));
        }
    }
}
