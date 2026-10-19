using System;
using System.Collections.Generic;
using System.Linq;
using Flex.Smoothlake.FlexLib;

namespace Radios;

/// <summary>The resources added by one loopback arrangement, never VFO positions (#781).</summary>
internal sealed class LoopbackSliceResources
{
    private readonly HashSet<Slice> _beforeSlices;
    private readonly HashSet<Panadapter> _beforePans;
    internal Slice Slice { get; private set; }
    internal Panadapter Pan { get; private set; }

    internal LoopbackSliceResources(IEnumerable<Slice> slices, IEnumerable<Panadapter> pans)
    {
        _beforeSlices = new(slices);
        _beforePans = new(pans);
    }

    internal bool Capture(IEnumerable<Slice> slices, IEnumerable<Panadapter> pans)
    {
        var added = slices.Where(s => !_beforeSlices.Contains(s)).ToArray();
        // Refuse an ambiguous or incomplete creation rather than configuring
        // an operator slice. Both resources must be new objects in this roster.
        if (added.Length != 1 || added[0].Panadapter == null ||
            _beforePans.Contains(added[0].Panadapter) || !pans.Contains(added[0].Panadapter)) return false;
        Slice = added[0];
        Pan = Slice.Panadapter;
        return true;
    }

    internal bool IsPresent(IEnumerable<Slice> slices, IEnumerable<Panadapter> pans) =>
        Slice != null && slices.Contains(Slice) && pans.Contains(Pan) && ReferenceEquals(Slice.Panadapter, Pan);

    internal bool Configure(IEnumerable<Slice> slices, IEnumerable<Panadapter> pans, Slice transmit, string antenna)
    {
        if (!IsPresent(slices, pans) || transmit == null || ReferenceEquals(Slice, transmit)) return false;
        Slice.Freq = transmit.Freq;
        Slice.DemodMode = transmit.DemodMode;
        Slice.RXAnt = antenna;
        return true;
    }

    internal bool Release(IEnumerable<Slice> slices, IEnumerable<Panadapter> pans, Slice active, Slice transmit)
    {
        // Object membership protects a resource whose numeric ID was reused.
        if (!IsPresent(slices, pans)) return true;
        if (ReferenceEquals(Slice, active) || ReferenceEquals(Slice, transmit)) return false;
        Slice.Close();
        Pan.Close();
        return true;
    }
}
