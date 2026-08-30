using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using JJPortaudio;
using JJTrace;
using Radios;
using Radios.Fixer;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// The host measurements for the Fixer Tool's two RF-silent stages: what the
/// audio system is actually doing (stage 0) and whether the microphone is
/// producing audio in this computer (stage 1).
/// </summary>
/// <remarks>
/// <para>
/// <b>The host measures, the engine interprets</b> — the same split
/// <see cref="Radios.ChainChecks.FixerTransmitBoundary"/> keeps on the
/// transmitting side. Nothing here decides what a reading means:
/// <c>AudioSetupCheck.Analyze</c> and <c>TransmitStages.Microphone</c> own
/// every word the operator hears, and this file owns only getting honest
/// numbers to them.
/// </para>
/// <para>
/// <b>No FlexBase, structurally.</b> Stages 0 and 1 are the Fixer's two
/// independent positive controls, and their value rests on involving no radio
/// at all — a microphone verdict that needed the radio to exist would prove
/// nothing about the microphone when the radio is the thing that is broken.
/// So nothing in this file names FlexBase, and three fields of
/// <c>AudioSetupFacts</c> are deliberately NOT populated here because only
/// the radio session can answer them: <c>PcAudioOn</c> (live
/// <c>FlexBase.PCAudio</c>), <c>RemoteRadio</c> (<c>FlexBase.RemoteRig</c>),
/// and <c>MicProfileEmpty</c> (<c>FlexBase.MicProfileSelectionEmpty</c>, the
/// pcap-confirmed silent-transmit fault). The coordinator, which holds the
/// dialog's <c>Func&lt;FlexBase?&gt;</c>, is the right place to overlay those
/// three onto the facts this returns. Their defaults are chosen so no finding
/// can FIRE falsely while they are unwired: <c>RemoteRadio == false</c>
/// suppresses the PC-audio-off finding, and <c>MicProfileEmpty == false</c>
/// raises nothing.
/// </para>
/// <para>
/// <b>Never throws.</b> This runs when something is already broken, so what
/// it asks for is exactly what is likely to fail. Every read is guarded on
/// its own — one unreadable fact must not cost the other eleven — and an
/// unknown stays unknown: empty, zero, NaN or null, exactly as each field's
/// comment specifies, never a plausible stand-in.
/// </para>
/// </remarks>
internal static class FixerHostWiring
{
    // ================================================================
    // Stage 0: audio setup
    // ================================================================

    /// <summary>
    /// Build the host's audio-setup reader: the live device table on one side,
    /// audioDevices.xml on the other, so the analyzer can see where they
    /// disagree — which is the stage's whole reason to exist.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What "open" means here, precisely.</b> The Open* fields are read
    /// from the audio system — a fresh PortAudio enumeration, resolved through
    /// the same matching rules the engine itself binds with
    /// (<see cref="Devices.FindLive"/>) — and never from the configuration
    /// file. So <c>OpenHostApi</c> is the host API the configured device
    /// actually resolves under TODAY, which is not always the one the file
    /// asked for: a device that has vanished from WASAPI and survives only
    /// under MME resolves cross-API, exactly as the engine and
    /// <see cref="MicProbe"/> would open it, and that disagreement is the
    /// finding. What this read cannot see is the inside of a stream the radio
    /// path already holds open — that state lives behind the radio boundary —
    /// so a rate renegotiated at open time is reported as the rate the audio
    /// system quotes for the device now. The two coincide except in the
    /// narrow window after a device changes under a running stream.
    /// </para>
    /// <para>
    /// Under MME the reported sample rate is the resampler's tidy answer, not
    /// the hardware's. That is not worked around here on purpose: reporting
    /// the number the audio system gives, and letting the analyzer's MME
    /// finding explain why it may be lying, is the honest division of labour.
    /// </para>
    /// </remarks>
    public static Func<AudioSetupFacts> AudioSetup()
    {
        return () =>
        {
            var facts = new AudioSetupFacts();
            try
            {
                ReadAudioSetup(facts);
            }
            catch (Exception ex)
            {
                // Belt and braces over the per-read guards below. Whatever was
                // filled in before the failure is still honest; hand it over.
                Tracing.TraceLine("FixerHostWiring: audio setup read failed — " + ex.Message,
                                  TraceLevel.Warning);
            }
            return facts;
        };
    }

    private static void ReadAudioSetup(AudioSetupFacts facts)
    {
        // ---- the live half: what the audio system reports right now ----
        //
        // A fresh sweep rather than the tables left by the last picker visit,
        // because a hot-unplugged microphone is exactly the kind of thing this
        // stage exists to catch. Enumerate is the same bounded
        // Pa_Initialize / walk / Pa_Terminate cycle the picker's Refresh runs,
        // reference-counted and locked against the probe, so it is safe while
        // the radio path holds its own streams open.
        bool enumerated = false;
        try
        {
            Devices.EnumerationStatus status = Devices.Enumerate(out string message);
            enumerated = status == Devices.EnumerationStatus.Ok;
            if (!enumerated)
            {
                Tracing.TraceLine("FixerHostWiring: audio enumeration failed (" + status
                                  + ") — " + message, TraceLevel.Warning);
            }
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerHostWiring: audio enumeration threw — " + ex.Message,
                              TraceLevel.Warning);
        }

        try
        {
            foreach (Devices.HostApi api in Devices.HostApis)
            {
                if (api.TypeId == Devices.WasapiTypeId)
                {
                    facts.WasapiAvailable = true;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerHostWiring: host API list unreadable — " + ex.Message,
                              TraceLevel.Warning);
        }

        // ---- the configured half: what audioDevices.xml says ----
        //
        // Read through the Devices class itself — a second parser here would
        // be the drift this project keeps finding. But loading a config file
        // has a side effect this stage must not keep: readCFG applies the
        // file's host-API selection to the PROCESS-WIDE live selection, which
        // is both a mutation from what is supposed to be a look, and the
        // destruction of the very open-versus-configured distinction being
        // measured. So the live value is captured first and put back after.
        Devices.Device? savedIn = null;
        Devices.Device? savedOut = null;
        int savedApiId = -1;
        try
        {
            // Same settings root every other store resolves through, so a run
            // under JJFLEX_CONFIG_DIR stays inside its isolation — the exact
            // path FixerDialog.OpenDevicePicker hands the picker.
            string file = Path.Combine(RadioConfig.AppDataRoot, "audioDevices.xml");
            if (File.Exists(file))
            {
                int liveApi = Devices.SelectedHostApiTypeId;
                var devices = new Devices(file);
                bool loaded;
                try
                {
                    loaded = devices.LoadSavedSelection();
                }
                finally
                {
                    if (Devices.SelectedHostApiTypeId != liveApi)
                        Devices.ApplyHostApiSelection(liveApi);
                }

                if (loaded)
                {
                    savedIn = devices.InputDevice;
                    savedOut = devices.OutputDevice;
                    savedApiId = devices.SavedHostApiTypeId;
                }
                else
                {
                    // Unreadable is NOT unconfigured — the TxChainPcFacts
                    // lesson. AudioSetupFacts has no slot for the difference
                    // (empty strings read as "none"), so the trace carries it.
                    Tracing.TraceLine("FixerHostWiring: audioDevices.xml exists but could not "
                                      + "be read; the configured half of stage 0 will read as "
                                      + "unconfigured", TraceLevel.Warning);
                }
            }
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerHostWiring: reading audioDevices.xml failed — " + ex.Message,
                              TraceLevel.Warning);
        }

        facts.ConfiguredInputDevice = savedIn?.Name ?? "";
        // The machine-scope host-API selection is the configured API; files
        // from before that field existed carry -1 but still record the API on
        // the device entry itself, which is then the file's only claim.
        facts.ConfiguredHostApi = savedApiId >= 0
            ? Devices.NameOfHostApi(savedApiId)
            : (savedIn?.hostApiName ?? "");
        facts.InputDeviceSelected = !string.IsNullOrEmpty(savedIn?.Name);

        // ---- resolve the saved choices against the live table ----
        Devices.DeviceInfo? liveIn = null;
        Devices.DeviceInfo? liveOut = null;
        try { liveIn = Devices.FindLive(savedIn); }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerHostWiring: resolving the input device failed — " + ex.Message,
                              TraceLevel.Warning);
        }
        try { liveOut = Devices.FindLive(savedOut); }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerHostWiring: resolving the output device failed — " + ex.Message,
                              TraceLevel.Warning);
        }

        facts.OpenInputDevice = liveIn?.Name ?? "";
        facts.OpenOutputDevice = liveOut?.Name ?? "";
        // The input row's API when there is one — transmit audio is what this
        // stage serves — else the output row's, because received audio still
        // actually runs through it. Empty when nothing resolves at all: with
        // no device to open, no host API is in use, and claiming one would put
        // "running via WASAPI" over a system that is running nothing.
        facts.OpenHostApi = FirstNonEmpty(liveIn?.HostApiName, liveOut?.HostApiName);
        facts.OpenSampleRateHz = liveIn?.Info.defaultSampleRate ?? 0;
        facts.OpenChannels = liveIn?.OpenChannels ?? 0;

        // Unplugged has a subject only when something is chosen; and when the
        // sweep itself failed, "not in the table" is a fact about the sweep,
        // not the cable, so the answer stays null rather than becoming a
        // finding built on a failed read.
        if (facts.InputDeviceSelected)
            facts.InputDeviceUnplugged = enumerated ? (bool?)(liveIn == null) : null;

        try
        {
            facts.SuggestedInputDevice = SuggestInput() ?? "";
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerHostWiring: input suggestion failed — " + ex.Message,
                              TraceLevel.Warning);
        }

        // ---- the Windows-side facts: observed here, fixed there ----
        try
        {
            WindowsMicLevel? level = null;
            string whyNot = "";
            if (liveIn != null)
                level = WindowsMicLevel.TryFind(liveIn, out whyNot);
            else if (!string.IsNullOrEmpty(savedIn?.Name))
                level = WindowsMicLevel.TryFindByName(savedIn!.Name, savedIn.hostApiTypeId, out whyNot);

            if (level != null)
            {
                using (level) facts.WindowsInputMuted = level.Muted;
            }
            else if (whyNot.Length > 0)
            {
                Tracing.TraceLine("FixerHostWiring: Windows mute unreadable — " + whyNot,
                                  TraceLevel.Info);
            }
        }
        catch (Exception ex)
        {
            // Null, never false: a mute we could not read is not a mute that
            // is off.
            Tracing.TraceLine("FixerHostWiring: Windows mute lookup threw — " + ex.Message,
                              TraceLevel.Warning);
        }

        try
        {
            MicrophonePrivacy.Access access = MicrophonePrivacy.Check(out _);
            if (access != MicrophonePrivacy.Access.Unknown)
                facts.MicrophonePrivacyBlocked = MicrophonePrivacy.IsBlocked(access);
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerHostWiring: privacy check threw — " + ex.Message,
                              TraceLevel.Warning);
        }

        // NOT populated, on purpose — see the class remarks. PcAudioOn,
        // RemoteRadio and MicProfileEmpty are radio-session facts only the
        // coordinator's FlexBase accessor can answer, and this file involving
        // FlexBase would put the radio back inside the stage whose value is
        // that it involves no radio. Their false defaults raise no finding.
    }

    /// <summary>
    /// The input this host would nominate when none is chosen: the engine's
    /// own fallback preference — usable, on the selected host API, Windows
    /// default first — read WITHOUT the write-through.
    /// </summary>
    /// <remarks>
    /// This mirrors <see cref="Devices.AdoptSystemDefault"/>'s pick loop
    /// rather than calling it, because AdoptSystemDefault SAVES its pick into
    /// audioDevices.xml — right for the connect-time fallback it serves,
    /// wrong here, where merely looking at the setup must never change it.
    /// Stage 0 offers; only a pressed fix button changes anything.
    /// </remarks>
    private static string? SuggestInput()
    {
        Devices.DeviceInfo? pick = null;     // best so far on the selected host API
        Devices.DeviceInfo? anyApi = null;   // best so far anywhere
        foreach (Devices.DeviceInfo d in Devices.InputDevices)
        {
            if (!d.UsableForRadioAudio) continue;
            if (anyApi == null || (d.IsDefault && !anyApi.IsDefault)) anyApi = d;
            if (Devices.SelectedHostApiTypeId >= 0
                && d.HostApiTypeId != Devices.SelectedHostApiTypeId) continue;
            pick ??= d;
            if (d.IsDefault) { pick = d; break; }
        }
        pick ??= anyApi;
        return pick?.Name;
    }

    // ================================================================
    // Stage 1: microphone check
    // ================================================================

    /// <summary>
    /// A peak this far down is an electrical noise floor, not a voice — the
    /// same figure AudioDevicesDialog.NoiseFloorDb draws its "only the noise
    /// floor" verdict at, so one microphone can never pass here and fail
    /// there. (That constant is private to the dialog; if it ever moves,
    /// move this with it — or better, give the figure one shared home.)
    /// </summary>
    private const float MicHeardFloorDb = -75f;

    /// <summary>
    /// How long the SPEECH window listens. The loudness meter's short-term
    /// window is three seconds and a slow interface may hand over its first
    /// buffers up to a second late (the devices dialog grants exactly that
    /// grace before judging silence), so four covers both. Bounded, because
    /// the stage runs to its own end — Stop cannot reach into it.
    /// </summary>
    private const int MicListenMs = 4000;

    /// <summary>
    /// The QUIET window, before the countdown: the noise floor is measured
    /// here, in genuine silence (#261). The floor wants quiet by definition,
    /// which is exactly why the old single window — taken while the operator
    /// might already be talking — was the wrong place to take it from. Long
    /// enough for a slow interface's first buffers; short enough that the
    /// operator is not left wondering.
    /// </summary>
    private const int MicQuietWindowMs = 1200;

    /// <summary>
    /// How long the countdown sound lasts before the speech window may open:
    /// three count tones a beat apart and the ringing "go" note. A ringing
    /// tone inside the speech sample makes a quiet shack read as noisy, so the
    /// capture waits it out (#261).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>ASKED, not written down (#396).</b> This was a hand-copied 1000 whose
    /// own comment described "three 150 ms steps and a 500 ms ring — 950 ms",
    /// and the sound had been 1,600 ms since long before anyone noticed. The
    /// wait was therefore 600 ms short: the record landing was still ringing
    /// when the capture opened, inside the very sample the wait exists to keep
    /// clean, and a quiet shack read as a noisy one. Nothing failed and nothing
    /// could have — a number copied out of another file's comment cannot notice
    /// that file changing.
    /// </para>
    /// <para>
    /// A property rather than a const so it is read at the moment it is used.
    /// The countdown is now four seconds, which is the whole point of #396 and
    /// which this follows without being told.
    /// </para>
    /// </remarks>
    private static int MicCountdownSoundMs => EarconPlayer.CountdownDurationMs(transmit: false);

    /// <summary>The cues the microphone check speaks and sounds around its
    /// measurement (#255, #261). Any may be null; a missing cue cues nothing
    /// and the measurement still runs.</summary>
    public sealed class MicCueHooks
    {
        /// <summary>Starts the record countdown tones. Fire-and-forget.</summary>
        public Action? Countdown { get; set; }

        /// <summary>Spoken when the speech window opens: the moment to talk
        /// has arrived, and a blind operator has no other way to know it.</summary>
        public Action? SpeakListenNow { get; set; }

        /// <summary>Spoken after the measurement ends — the reciprocal end
        /// signal, so nobody is left talking into silence (#261).</summary>
        public Action? SpeakListenDone { get; set; }
    }

    /// <summary>
    /// Build the host's microphone measurement: resolve the configured
    /// microphone, measure the noise floor in a quiet window, count the
    /// operator in, listen to their speech, and report what it heard.
    /// </summary>
    /// <remarks>
    /// Built on <see cref="MicProbe"/> and
    /// <see cref="RecordingNarrator.ResolveMicrophone"/> rather than a second
    /// capture path, deliberately: the probe already owns its own stream,
    /// resolves the device the way the transmit path binds it, survives the
    /// device disappearing mid-check, and knows a quiet room from Windows
    /// handing over digital zeroes. A measurement written fresh here would
    /// have to learn all of that again, and would learn it worse.
    /// </remarks>
    public static Func<MicCheckFacts> Microphone(MicCueHooks? cues = null)
    {
        return () =>
        {
            var facts = new MicCheckFacts();
            try
            {
                MeasureMicrophone(facts, cues ?? new MicCueHooks());
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("FixerHostWiring: microphone measurement failed — " + ex.Message,
                                  TraceLevel.Warning);
                if (facts.Detail.Length == 0)
                    facts.Detail = "The measurement failed unexpectedly: " + ex.Message;
                // Measured stays false — "could not be said either way" is the
                // engine's honest wording for exactly this.
            }
            return facts;
        };
    }

    /// <summary>Tell a cue something happened, and never let it break the
    /// measurement — a screen reader or an audio device that throws must not
    /// cost the operator the reading.</summary>
    private static void Cue(Action? cue, string which)
    {
        if (cue == null) return;
        try { cue(); }
        catch (Exception ex)
        {
            Tracing.TraceLine("FixerHostWiring: " + which + " cue threw and was ignored — "
                              + ex.Message, TraceLevel.Warning);
        }
    }

    private static void MeasureMicrophone(MicCheckFacts facts, MicCueHooks cues)
    {
        // The same resolver the voice-note recorder uses: it distinguishes,
        // in reviewed words, "never chosen" from "your saved microphone is
        // not connected" from "the audio system would not start" — three
        // different problems an operator has to be told apart.
        Devices.DeviceInfo? row = RecordingNarrator.ResolveMicrophone(out string trouble);
        if (row == null)
        {
            facts.Detail = trouble;
            return;
        }

        facts.Device = row.Name;
        facts.HostApi = row.HostApiName ?? "";

        // Read the privacy switches for CONTEXT, never as a gate — a registry
        // read is evidence about a switch, not about a microphone, and if
        // audio arrives anyway the audio wins the argument. Same stance as
        // the devices dialog's check.
        bool privacyBlocked = false;
        string privacyWhy = "";
        try
        {
            privacyBlocked = MicrophonePrivacy.IsBlocked(MicrophonePrivacy.Check(out privacyWhy));
        }
        catch { /* unknown stays unknown */ }

        using var probe = new MicProbe();
        MicProbe.StartOutcome outcome = probe.Start(row, out string failure);
        if (outcome != MicProbe.StartOutcome.Started)
        {
            // A privacy block explains an opaque host error far better than
            // the error explains itself; give both when we have both.
            facts.Detail = privacyBlocked && privacyWhy.Length > 0
                ? privacyWhy + " " + failure
                : failure;
            Tracing.TraceLine("FixerHostWiring: mic probe did not start (" + outcome + ") — "
                              + failure, TraceLevel.Warning);
            return;
        }

        // TWO WINDOWS, TWO PURPOSES (#261). First the QUIET window: the noise
        // floor is measured before anything has told the operator to speak,
        // in genuine silence — the floor wants quiet by definition, so the
        // old single window, shared with the speech, was always the wrong
        // place to take it from.
        bool faulted = QuietListen(probe, MicQuietWindowMs);
        MicProbe.Reading floor = probe.Read();

        // Count the operator in: three tones, then the ringing "go". Tones,
        // not speech, so the count cannot be flushed by the spoken cue's
        // interrupt. The capture waits out the ring — a ringing tone in the
        // sample makes a quiet shack read as noisy.
        if (!faulted)
        {
            Cue(cues.Countdown, "countdown");
            faulted = QuietListen(probe, MicCountdownSoundMs);
        }

        // The moment to talk, spoken — a blind operator has no recording
        // light (#194, #255). Then the levels reset, so the speech figures
        // describe the speech and not the silence or the countdown's bleed.
        bool cued = false;
        if (!faulted)
        {
            Cue(cues.SpeakListenNow, "listen-now");
            cued = true;
            probe.ResetLevels();
            faulted = QuietListen(probe, MicListenMs);
        }

        MicProbe.Reading final = probe.Read();
        probe.Stop();

        // The end signal is the countdown's reciprocal: told only if the
        // start cue went out, so nobody is told a check finished that they
        // were never asked to speak for — and always before any early
        // return, or a fault would leave the operator talking into silence.
        if (cued) Cue(cues.SpeakListenDone, "listen-done");

        if (faulted || final.Faulted)
        {
            // A capture that died mid-way is not a measurement; saying "it
            // heard nothing" about it would turn a driver hiccup into a
            // verdict on the microphone.
            facts.Detail = "The measurement stopped early: " + final.FaultMessage;
            Tracing.TraceLine("FixerHostWiring: mic probe faulted — " + final.FaultMessage,
                              TraceLevel.Warning);
            return;
        }

        facts.Measured = true;
        facts.PeakDb = final.HoldPeakDb;
        // The verdict is two conditions because the two failures differ: an
        // unbroken run of exact zeroes is Windows feeding silence (a real
        // microphone always has a noise floor), while non-zero samples that
        // never clear the electrical floor mean the interface is alive and
        // nothing is reaching it. Neither is "sound arrived".
        facts.AudioArrived = final.AnySound && final.HoldPeakDb > MicHeardFloorDb;
        // The API the check ACTUALLY opened through — not always the one the
        // chosen row named, and the honest one to report when they differ.
        if (!string.IsNullOrEmpty(final.HostApiName)) facts.HostApi = final.HostApiName;
        // The floor is a REAL measurement now, from a window that was silent
        // by design — the earlier refusal to derive one from the quiet gaps
        // of the speech window ("an invention wearing a measurement's
        // clothes") applied to that window, not to this one.
        if (!floor.Faulted && floor.Frames > 0) facts.NoiseFloorDb = floor.HoldPeakDb;
        facts.Detail = BuildMicDetail(final, floor, privacyBlocked, privacyWhy);

        Tracing.TraceLine("FixerHostWiring: mic check on \"" + facts.Device + "\" ("
                          + facts.HostApi + ") — peak "
                          + final.HoldPeakDb.ToString("0.#", CultureInfo.InvariantCulture)
                          + " dBFS, anySound=" + final.AnySound
                          + ", arrived=" + facts.AudioArrived, TraceLevel.Info);
    }

    /// <summary>A bounded wait on the running probe, polled only for an
    /// early fault. True when the probe faulted mid-window.</summary>
    private static bool QuietListen(MicProbe probe, int windowMs)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < windowMs)
        {
            Thread.Sleep(250);
            if (probe.Read().Faulted) return true;
        }
        return false;
    }

    /// <summary>
    /// Whatever else the measurement reported, verbatim — the probe's own
    /// figures, in sentences a screen reader can carry.
    /// </summary>
    private static string BuildMicDetail(MicProbe.Reading final, MicProbe.Reading floor,
                                         bool privacyBlocked, string privacyWhy)
    {
        var sb = new StringBuilder();
        sb.Append("Listened for ")
          .Append(final.Seconds.ToString("0.0", CultureInfo.InvariantCulture))
          .Append(" seconds");
        if (final.SampleRate > 0)
            sb.Append(" at ").Append(final.SampleRate.ToString(CultureInfo.InvariantCulture))
              .Append(" Hz");
        if (final.Channels > 0)
            sb.Append(final.Channels == 1 ? ", mono" : ", " + final.Channels + " channels");
        sb.Append('.');

        // The quiet window's own figures — measured before the countdown, in
        // silence by design, which is what makes them a floor rather than a
        // guess (#261).
        if (!floor.Faulted && floor.Frames > 0)
        {
            sb.Append(" Noise floor, measured in the quiet moment before the countdown: "
                    + "peak ")
              .Append(floor.HoldPeakDb.ToString("0.#", CultureInfo.InvariantCulture))
              .Append(" dBFS");
            if (floor.IntegratedLufs > LufsMeter.Floor)
                sb.Append(", ")
                  .Append(floor.IntegratedLufs.ToString("0.#", CultureInfo.InvariantCulture))
                  .Append(" LUFS");
            sb.Append('.');
        }

        if (final.IntegratedLufs > LufsMeter.Floor)
        {
            sb.Append(" Loudness over the speech window: ")
              .Append(final.IntegratedLufs.ToString("0.#", CultureInfo.InvariantCulture))
              .Append(" LUFS.");
        }

        if (!final.AnySound)
        {
            // Exactly zero, every sample — Windows delivering silence, not a
            // quiet room. The single most useful thing this probe can say.
            sb.Append(" Every sample was exact digital zero — the device was open,"
                      + " but Windows delivered silence rather than a quiet room.");
            if (privacyBlocked && privacyWhy.Length > 0) sb.Append(' ').Append(privacyWhy);
        }

        if (final.Overflows > 0)
            sb.Append(" Dropped input reads: ").Append(final.Overflows).Append('.');

        return sb.ToString();
    }

    // ---------------- plumbing ----------------

    private static string FirstNonEmpty(string? a, string? b)
        => !string.IsNullOrEmpty(a) ? a! : (!string.IsNullOrEmpty(b) ? b! : "");
}
