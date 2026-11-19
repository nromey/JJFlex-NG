using System;
using System.Diagnostics;
using Radios;

namespace JJFlexWpf
{
    /// <summary>
    /// S17, ruled by Noel 2026-11-18 21:35: "yes, if receive does it, it should
    /// operate the same way". The live transmit noise choices (both slots' on,
    /// engine and every engine's settings, and the gate) persist across a
    /// restart through the SAME mechanism receive's computer noise reduction
    /// uses, not a parallel one.
    /// </summary>
    /// <remarks>
    /// <para><b>How receive does it, read from the code (2026-11-18):</b> the
    /// receive slots are saved into <see cref="AudioOutputConfig"/>
    /// (<c>ReceiveNeural</c>, <c>ReceiveSpectral</c>) by
    /// <see cref="ReceiveDenoiseConfig.Map"/>, from
    /// <c>MainWindow.PersistDspSettings</c>, on every change, into
    /// <c>audioConfig.xml</c> at the settings ROOT. That file is app-wide: one
    /// per installation, not per radio (its loader normalizes any directory to
    /// the root, <c>AudioOutputConfig.CanonicalDir</c>). It is read back at each
    /// connect (<c>ScreenFieldsPanel.ApplyDspConfig</c>). Receive's computer
    /// noise reduction has NO no-radio intent: its slots exist only while a
    /// radio is attached, and a change asked for with no radio is refused with
    /// "needs a radio" (<c>NoiseController.NoRadio</c>). The radio's OWN noise
    /// levels are a different store, per radio by serial
    /// (<see cref="RadioConfigNoiseIntents"/>).</para>
    /// <para><b>So transmit, app-wide too.</b> The brief said "per radio"; the
    /// mechanism it named is not, and the ruling's words choose the mechanism.
    /// The per-radio choice on transmit already exists one level up, in named
    /// transmit setups and the microphone setup that points at one; applying
    /// one still replaces these live choices, as it always did.</para>
    /// <para><b>With no radio.</b> The transmit chain lives for the whole
    /// session and holds a choice made with no radio (#1077), unlike receive.
    /// It is saved straight to the root file, so it is what the next radio
    /// connected gets, and what the next launch starts with.</para>
    /// </remarks>
    public static class TransmitNoiseConfig
    {
        /// <summary>The live choices, as a setup with no name (the shape a transmit setup already has).</summary>
        public static TxNoiseSetup CaptureLive() => TxAudioConditioning.CaptureSetup("");

        /// <summary>
        /// Write the live transmit choices into <paramref name="config"/>. True
        /// when they differ from what it held (the caller then saves it).
        /// </summary>
        public static bool Capture(AudioOutputConfig config)
        {
            if (config == null) return false;
            var live = CaptureLive();
            if (config.TransmitNoise != null && config.TransmitNoise.SameContentAs(live)) return false;
            config.TransmitNoise = live;
            return true;
        }

        /// <summary>
        /// Put the saved transmit choices back on the chain, as one change (it
        /// waits for the unkey when keyed, like any whole setup). Nothing saved
        /// leaves the chain as shipped: both slots off.
        /// </summary>
        public static void Restore(AudioOutputConfig config)
        {
            var saved = config?.TransmitNoise;
            if (saved == null) return;
            try
            {
                TxAudioConditioning.ApplySetup(saved);
                Trace.WriteLine("TransmitNoiseConfig: the saved transmit noise choices are back (spectral "
                    + (saved.Spectral?.Enabled == true ? "on" : "off") + ", neural "
                    + (saved.Neural?.Enabled == true ? "on" : "off") + ", gate "
                    + (saved.Gate?.Enabled == true ? "on" : "off") + ")");
            }
            catch (Exception ex)
            {
                Trace.WriteLine("TransmitNoiseConfig: the saved transmit noise choices could not be restored: " + ex.Message);
            }
        }

        /// <summary>
        /// Save the live choices: into the session's loaded config when there is
        /// one (so its next save carries them), and to the root file either way.
        /// With no config loaded yet (no radio connected this session) the root
        /// file is read, updated and written, so the choice is there for the
        /// next radio and the next launch.
        /// </summary>
        /// <param name="loaded">The session's loaded config, or null before the first connect.</param>
        /// <param name="root">The settings root (<see cref="RadioConfig.AppDataRoot"/>).</param>
        /// <returns>True when something was written.</returns>
        public static bool Persist(AudioOutputConfig? loaded, string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            var config = loaded ?? AudioOutputConfig.Load(root);
            if (!Capture(config)) return false;
            config.Save(root);
            return true;
        }
    }
}
