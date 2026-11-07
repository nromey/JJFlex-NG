using System;
using System.Collections.Generic;

namespace Radios
{
    /// <summary>
    /// Where Alt with a bracket is in the filter preset list: the operator's
    /// own position, kept across presses (bench item 7 of the 2026-11-06 junk
    /// box).
    /// </summary>
    /// <remarks>
    /// <para><b>The cause it fixes.</b> <see cref="FilterPresets.CyclePreset"/>
    /// works out the position from the LIVE filter on every press. A mode that
    /// refuses a preset (FM refuses every filter change) leaves the live filter
    /// where it was, so each press recognised the same preset and stepped to
    /// the same neighbour: in FM on Wide Open, Alt+[ said "Normal" every time
    /// and Alt+] said "Widest preset".</para>
    /// <para><b>The rule.</b> The cursor remembers the preset it last chose,
    /// for which rig and mode, and the filter edges that choice left behind
    /// (before the write completes, the edges at the press; after, the
    /// applied edges). While the live filter is still one of those, the next
    /// press steps from the remembered preset, whether or not the radio took
    /// it, and each refused preset still says "unchanged". Once the filter has
    /// been changed some other way (brackets, the layer, another client), the
    /// position comes from the live filter again, exactly as before.</para>
    /// <para>Called on the window's thread; the write's completion arrives on
    /// the command loop, hence the lock.</para>
    /// </remarks>
    public sealed class PresetCycleCursor
    {
        private readonly object _gate = new object();
        private FlexBase? _rig;
        private string? _mode;
        private FilterPresets? _presets;
        private string? _listShape;
        private int _index = -1;
        private long _press;
        private readonly HashSet<(int Low, int High)> _leftBehind = new();

        /// <summary>
        /// One Alt+bracket press: choose the next preset in
        /// <paramref name="direction"/> and put it on the radio. <paramref name="say"/>
        /// gets the preset's applied sentence; <paramref name="boundary"/> is
        /// called with true at the widest end, false at the narrowest.
        /// </summary>
        public void Cycle(FlexBase rig, FilterPresets presets, string mode, int direction, Action<string> say, Action<bool> boundary)
            => Cycle(rig, presets, mode, direction, (said, _) => say(said), boundary);

        private static string Shape(List<FilterPreset> list)
            => string.Join("|", list.ConvertAll(p => p.Name + ":" + p.Low + ":" + p.High));

        /// <summary>As above, handing the caller the applied result too.</summary>
        public void Cycle(FlexBase rig, FilterPresets presets, string mode, int direction, Action<string, FilterApplied> say, Action<bool> boundary)
        {
            var list = presets.GetPresetsForMode(mode);
            // The default lists are built fresh on every call, so the list is
            // recognised by its contents, not by reference.
            string shape = Shape(list);
            int liveLow = rig.FilterLow, liveHigh = rig.FilterHigh;
            FilterPreset? preset;
            long press;
            lock (_gate)
            {
                bool ours = ReferenceEquals(rig, _rig) && string.Equals(mode, _mode, StringComparison.OrdinalIgnoreCase)
                    && ReferenceEquals(presets, _presets) && shape == _listShape && _index >= 0 && _index < list.Count
                    && _leftBehind.Contains((liveLow, liveHigh));
                int next;
                if (ours)
                {
                    next = Math.Clamp(_index + direction, 0, list.Count - 1);
                    if (next == _index) { preset = null; next = -1; }
                    else preset = list[next];
                }
                else
                {
                    preset = presets.CyclePreset(mode, liveLow, liveHigh, direction);
                    next = preset == null ? -1 : list.FindIndex(p => p.Name == preset.Name && p.Low == preset.Low && p.High == preset.High);
                }

                if (preset == null)
                {
                    press = 0;
                }
                else
                {
                    _rig = rig;
                    _mode = mode;
                    _presets = presets;
                    _listShape = shape;
                    _index = next;
                    _leftBehind.Clear();
                    _leftBehind.Add((liveLow, liveHigh));   // until the write lands
                    press = ++_press;
                }
            }

            if (preset == null) { boundary(direction > 0); return; }

            rig.ApplyFilterPreset(preset, mode, (said, applied) =>
            {
                lock (_gate)
                {
                    // Only the newest press moves what the cursor expects to see.
                    if (press == _press && ReferenceEquals(rig, _rig))
                    {
                        _leftBehind.Clear();
                        _leftBehind.Add((applied.Low, applied.High));
                    }
                }
                say(said, applied);
            });
        }
    }
}
