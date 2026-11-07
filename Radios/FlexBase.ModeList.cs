#nullable enable
using System;
using System.Collections.Generic;

namespace Radios;

public partial class FlexBase
{
    /// <summary>
    /// The demodulation modes the radio reports for the active slice (its
    /// <c>mode_list</c>), or an empty list before it has reported one or with
    /// no slice. Read by <see cref="ModeCycle.KnownModes"/>, so the mode cycle
    /// and its settings page offer what this radio actually does.
    /// </summary>
    /// <remarks>
    /// A copy: FlexLib's <c>Slice.ModeList</c> getter already returns a new
    /// list. Guarded because a dropping connection can take the slice away
    /// between the two reads, the same way <see cref="FilterLow"/> is.
    /// </remarks>
    public IReadOnlyList<string> RadioModeList
    {
        get
        {
            try
            {
                return (IReadOnlyList<string>?)theRadio?.ActiveSlice?.ModeList ?? Array.Empty<string>();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }
}
