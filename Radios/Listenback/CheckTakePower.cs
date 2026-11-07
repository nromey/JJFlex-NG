using System.Collections.Generic;
using System.Globalization;

namespace Radios.Listenback;

/// <summary>
/// The power a check COMMANDED for its take, just before keying (Unified takes,
/// round five, 7f). Not a power the radio acknowledged (round five repair,
/// 7h-4): see the last paragraph of the remarks.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the admission's power facts were wrong.</b> A playback take's header
/// is captured at recording admission, which comes BEFORE the check sets its
/// power (<c>AudioCheckSession.StartAsync</c>: the recorder arms, the take is
/// admitted, and only then does the power rule run). So a check run under
/// "Dummy load, no RF" wrote <c>dummy-load=False</c> and the operator's
/// ordinary <c>rf-power</c>, which is what the bench of 2026-11-04 found.
/// </para>
/// <para>
/// <b><c>dummy-load</c> keeps its meaning</b>: the app's own dummy load mode,
/// <c>FlexBase.DummyLoadMode</c>. It is the check's dummy load choice that
/// turns that mode on, everywhere a radio can run at zero, so read at the
/// right moment it says True. What it cannot say is the CHOICE: on a radio
/// that cannot run at zero the same choice writes the radio's floor instead
/// and leaves the mode off. So the choice is its own key, <c>check-power</c>,
/// and <c>rf-power</c> is the power the check commanded.
/// </para>
/// <para>
/// <b>Commanded, not acknowledged (Codex, round five F4).</b> This is the
/// check's plan, not a read-back: the dummy load's and the power's writes are
/// queued to the radio, and FlexLib's cache can still hold the old value at
/// the moment the check keys. Reading that cache later would not help either:
/// FlexLib's power setter writes the cache itself and sends the command
/// without asking for a reply, so the cache reads back what was asked for. If
/// the radio refused the write, or another client changed the power before
/// the key, <c>rf-power</c> and <c>dummy-load</c> still say what was commanded.
/// The header keys keep their names and values; this says what they mean.
/// </para>
/// </remarks>
/// <param name="Choice">The check's power choice, as a schema token: <see cref="DummyLoadChoice"/> or <see cref="LowPowerChoice"/>.</param>
/// <param name="CommandedRfPowerWatts">The transmit power the check commanded for the take.</param>
/// <param name="CommandedDummyLoad">Whether the check left the app's dummy load mode on, by its plan, as it keyed.</param>
public sealed record CheckTakePower(string Choice, int CommandedRfPowerWatts, bool CommandedDummyLoad)
{
    /// <summary>The header key for the check's power choice.</summary>
    public const string Key = "check-power";
    public const string DummyLoadChoice = "dummy-load";
    public const string LowPowerChoice = "low-power";

    /// <summary>What the check's power rule commanded, as the take's header says it.</summary>
    /// <param name="mode">The power choice the rule was given.</param>
    /// <param name="step">What the rule decided (<see cref="AudioCheckPowerRule.Plan"/>).</param>
    /// <param name="currentPower">Transmit power before the rule acted.</param>
    /// <param name="dummyLoadActive">Dummy load mode was already on before the rule acted.</param>
    public static CheckTakePower From(AudioCheckPowerModes mode, AudioCheckPowerStep step, int currentPower, bool dummyLoadActive)
    {
        string choice = mode == AudioCheckPowerModes.DummyLoad ? DummyLoadChoice : LowPowerChoice;
        return step.Act switch
        {
            AudioCheckPowerAct.EngageDummyLoad => new CheckTakePower(choice, 0, true),
            AudioCheckPowerAct.SetPower => new CheckTakePower(choice, step.Watts, dummyLoadActive),
            _ => new CheckTakePower(choice, dummyLoadActive ? 0 : currentPower, dummyLoadActive),
        };
    }

    /// <summary>
    /// Put these facts into a header: <c>rf-power</c> and <c>dummy-load</c>
    /// replaced where the admission wrote them, with the commanded values, and
    /// <c>check-power</c> added.
    /// </summary>
    public void WriteInto(List<KeyValuePair<string, string>> facts)
    {
        Replace(facts, "rf-power", CommandedRfPowerWatts.ToString(CultureInfo.InvariantCulture));
        Replace(facts, "dummy-load", CommandedDummyLoad.ToString());
        facts.Add(new(Key, Choice));
    }

    private static void Replace(List<KeyValuePair<string, string>> facts, string key, string value)
    {
        int at = facts.FindIndex(f => f.Key == key);
        if (at >= 0) facts[at] = new(key, value);
        else facts.Add(new(key, value));
    }
}
