using System;
using System.Diagnostics;
using JJTrace;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// Turns the callsign check (#1306) on or off from somewhere other than
/// Settings, everywhere at once.
/// </summary>
/// <remarks>
/// <para>The check is held in three places, and a writer that changes fewer
/// than all three is undone by the one it missed:</para>
/// <list type="bullet">
/// <item>the live value, <see cref="FlexBase.CallsignCheckOn"/>, which every
/// key asks;</item>
/// <item>the root settings file, which startup reads and every connect copies
/// over a radio's own (<see cref="AudioOutputConfig.TakeOperatorChoicesFrom"/>);</item>
/// <item>the connected radio's settings in memory, <c>CurrentAudioConfig</c>,
/// which Settings opens on and whose OK would write the old value back.</item>
/// </list>
/// <para>Settings itself needs none of this: its OK writes all three through
/// <see cref="AudioOutputConfig.Apply"/> and
/// <see cref="AudioOutputConfig.PersistUserScope"/>. Today the only other writer
/// is Check your audio's "Turn off the callsign check" button.</para>
/// </remarks>
internal static class CallsignCheckSwitch
{
    /// <summary>
    /// Set the check to <paramref name="on"/> everywhere. True when the root
    /// file now holds it; false when it could not be written, in which case the
    /// live value and <paramref name="current"/> still hold it for this session.
    /// </summary>
    /// <param name="on">The value to set.</param>
    /// <param name="current">The connected radio's settings in memory, or null.</param>
    /// <param name="rootConfigDir">The settings root (<see cref="RadioConfig.AppDataRoot"/>).</param>
    internal static bool Set(bool on, AudioOutputConfig? current, string? rootConfigDir)
    {
        FlexBase.CallsignCheckOn = on;
        if (current != null) current.RequireCallsignToTransmit = on;
        Tracing.TraceLine("CallsignCheckSwitch: the callsign check turned " + (on ? "on" : "off"), TraceLevel.Info);

        if (string.IsNullOrEmpty(rootConfigDir)) return false;
        try
        {
            // Through the same list Settings' OK persists, with the root's own
            // values for everything else, so only this one field changes.
            var root = AudioOutputConfig.Load(rootConfigDir);
            root.RequireCallsignToTransmit = on;
            AudioOutputConfig.PersistUserScope(root, rootConfigDir);
            // Save reports nothing, so read it back: a value that did not land
            // must not be described as kept.
            bool kept = AudioOutputConfig.Load(rootConfigDir).RequireCallsignToTransmit == on;
            if (!kept)
                Tracing.TraceLine("CallsignCheckSwitch: the settings file did not keep the change", TraceLevel.Warning);
            return kept;
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("CallsignCheckSwitch: the settings file could not be written: " + ex.Message, TraceLevel.Warning);
            return false;
        }
    }
}
