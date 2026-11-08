using System;
using System.Collections.Generic;
using System.Linq;
using Radios;

namespace JJFlexWpf;

/// <summary>
/// The Settings box "warn before a recording on the radio is overwritten"
/// (#999, repair round 1), without its window: which radio it shows, the
/// operator's edits for each radio until OK or Apply, and saving them. Settings
/// are intents: an edit survives a disconnect, and a choice made before any
/// radio is known waits for the first one.
/// </summary>
internal sealed class SliceOverwriteSettingEdits
{
    /// <summary>Edits not yet saved, by the serial they were made for; "" means the next radio connected.</summary>
    private readonly Dictionary<string, bool> _edits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The radio the box is for, by serial; "" for the next radio connected.</summary>
    public string Target { get; private set; } = "";

    /// <summary>Unsaved edits, for tests and the trace.</summary>
    public IReadOnlyDictionary<string, bool> Pending => _edits;

    /// <summary>
    /// Point the box at <paramref name="serial"/> ("" for none known) and return
    /// what it should show: that radio's unsaved edit, else what its config says
    /// (<paramref name="saved"/>), else, with no radio, a choice already held for
    /// the next one, else on. An edit made for "the next radio" moves to the
    /// first radio that becomes known.
    /// </summary>
    public bool Show(string serial, Func<string, bool> saved, bool? heldForNext)
    {
        serial ??= "";
        if (serial.Length > 0 && _edits.TryGetValue("", out bool forNext) && !_edits.ContainsKey(serial))
        {
            _edits.Remove("");
            _edits[serial] = forNext;
        }
        Target = serial;
        if (_edits.TryGetValue(serial, out bool edited)) return edited;
        return serial.Length > 0 ? saved(serial) : heldForNext ?? true;
    }

    /// <summary>The operator changed the box: an edit for the radio it is showing.</summary>
    public void Edit(bool warn) => _edits[Target] = warn;

    /// <summary>
    /// Save every edit to its radio (<paramref name="save"/>, true when saved or
    /// already so), and hand an edit made with no radio to <paramref name="holdForNext"/>.
    /// An edit that could not be saved stays pending.
    /// </summary>
    public void Commit(Func<string, bool, bool> save, Action<bool> holdForNext)
    {
        foreach (var (serial, warn) in _edits.ToList())
        {
            if (serial.Length == 0) { holdForNext(warn); _edits.Remove(serial); continue; }
            if (save(serial, warn)) _edits.Remove(serial);
        }
    }
}
