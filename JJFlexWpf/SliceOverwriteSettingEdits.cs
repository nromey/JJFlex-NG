using System;
using System.Collections.Generic;
using System.Linq;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// The Settings box "warn before a recording on the radio is overwritten"
/// (#999, repair round 1), without its window: which radio it shows, the
/// operator's edits for each radio until OK or Apply, and saving them. Settings
/// are intents: an edit survives a disconnect and is saved to the radio it was
/// made for. With nothing connected the box is for the radio this program last
/// CONNECTED to (#999 round 4, ruled C2 and C3 by Noel 2026-11-09), and only
/// when no radio has ever connected does a choice wait, across restarts, for
/// the first one that connects.
/// </summary>
internal sealed class SliceOverwriteSettingEdits
{
    /// <summary>Edits not yet saved, by the serial they were made for; "" means the first radio that connects.</summary>
    private readonly Dictionary<string, bool> _edits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The radio a waiting choice was shown against (round 4, item 3), whose
    /// save on OK or Apply spends that choice; "" when none was.
    /// </summary>
    private string _waitingChoiceShownFor = "";

    /// <summary>The radio the box is for, by serial; "" for the first radio that connects.</summary>
    public string Target { get; private set; } = "";

    /// <summary>
    /// True when <see cref="Target"/> is connected now, from the last
    /// <see cref="ResolveTarget(string, string)"/>. A waiting choice is never
    /// shown against a connected radio: the connect has its own rule for it.
    /// </summary>
    public bool TargetIsConnected { get; private set; }

    /// <summary>Unsaved edits, for tests and the trace.</summary>
    public IReadOnlyDictionary<string, bool> Pending => _edits;

    /// <summary>An unsaved edit made for the first radio that connects.</summary>
    public bool HoldsEditForNextRadio => _edits.ContainsKey("");

    /// <summary>
    /// Which radio the box is for, reading the radio last connected from
    /// <see cref="KnownRadioRoster.LastConnectedSerial"/> when none is connected.
    /// What Settings calls.
    /// </summary>
    public string ResolveTarget(string connectedSerial)
    {
        connectedSerial ??= "";
        return ResolveTarget(connectedSerial,
            connectedSerial.Length > 0 ? "" : KnownRadioRoster.LastConnectedSerial());
    }

    /// <summary>
    /// Which radio the box is for (#999 round 4, ruled C2 and C3 by Noel
    /// 2026-11-09): the connected radio; else the radio this program last
    /// connected to (<paramref name="lastConnectedSerial"/>); else "", the first
    /// radio that connects, which is only when no radio has ever connected.
    /// </summary>
    /// <remarks>
    /// <para>A radio that was only discovered is never named: "last connected"
    /// comes from <see cref="KnownRadioRoster.LastConnectedSerial"/>, which no
    /// sighting stamps. Round 3 fell back to the radio last SEEN here; C3 ruled
    /// that out.</para>
    /// <para>This decides only the target. It spends no waiting choice: the
    /// connect does that (<c>FlexBase.ApplyChoicesWaitingForAConnect</c>), and
    /// so does <see cref="Commit"/> for a choice <see cref="Show"/> put against
    /// the radio last connected.</para>
    /// </remarks>
    public string ResolveTarget(string connectedSerial, string lastConnectedSerial)
    {
        connectedSerial ??= "";
        TargetIsConnected = connectedSerial.Length > 0;
        return TargetIsConnected ? connectedSerial : lastConnectedSerial ?? "";
    }

    /// <summary>
    /// Point the box at <paramref name="serial"/> ("" for none) and return what
    /// it should show: that radio's unsaved edit; else, for the radio last
    /// connected with nothing connected, a waiting choice
    /// (<paramref name="waiting"/>); else what its config says
    /// (<paramref name="saved"/>); else, with no radio, the waiting choice, else on.
    /// </summary>
    /// <remarks>
    /// <para>An edit made for "the first radio that connects" moves to the radio
    /// the box is next pointed at. <see cref="ResolveTarget(string, string)"/>
    /// names a radio only once one has connected, and the first to connect since
    /// that edit is the one it was for.</para>
    /// <para>A waiting choice shown against the radio last connected (round 4,
    /// item 3) belongs to that radio, as ruled C2: OK or Apply saves it there and
    /// clears it, even untouched. Cancel leaves it waiting, and the connect still
    /// hands it to that radio.</para>
    /// </remarks>
    public bool Show(string serial, Func<string, bool> saved, bool? waiting)
    {
        serial ??= "";
        if (serial.Length > 0 && _edits.TryGetValue("", out bool forNext) && !_edits.ContainsKey(serial))
        {
            _edits.Remove("");
            _edits[serial] = forNext;
        }
        Target = serial;
        if (serial.Length > 0 && !TargetIsConnected && waiting is bool held)
        {
            _waitingChoiceShownFor = serial;
            if (!_edits.ContainsKey(serial)) _edits[serial] = held;
        }
        if (_edits.TryGetValue(serial, out bool edited)) return edited;
        return serial.Length > 0 ? saved(serial) : waiting ?? true;
    }

    /// <summary>The operator changed the box: an edit for the radio it is showing.</summary>
    public void Edit(bool warn) => _edits[Target] = warn;

    /// <summary>
    /// Save every edit to its radio (<paramref name="save"/>, true when saved or
    /// already so), and hand an edit made with no radio to <paramref name="holdForNext"/>.
    /// An edit that could not be saved stays pending. Once the radio a waiting
    /// choice was shown against has been saved, <paramref name="clearWaiting"/>
    /// clears that choice.
    /// </summary>
    public void Commit(Func<string, bool, bool> save, Action<bool> holdForNext, Action? clearWaiting = null)
    {
        foreach (var (serial, warn) in _edits.ToList())
        {
            if (serial.Length == 0) { holdForNext(warn); _edits.Remove(serial); continue; }
            if (!save(serial, warn)) continue;
            _edits.Remove(serial);
            if (string.Equals(serial, _waitingChoiceShownFor, StringComparison.OrdinalIgnoreCase))
            {
                clearWaiting?.Invoke();
                _waitingChoiceShownFor = "";
            }
        }
    }
}
