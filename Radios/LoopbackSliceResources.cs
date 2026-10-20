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
    internal string SavedRxAnt { get; private set; }
    internal int SavedRFGain { get; private set; }
    /// <summary>The RF gain sent to the ears slice; the restore's expected value.</summary>
    internal int EarsRFGain { get; set; } = 32;
    internal double SavedFrequency { get; private set; }
    internal string SavedMode { get; private set; }
    internal double SavedPanCenter { get; private set; }
    private uint? _replyPan;
    private bool _requireReply;
    internal void ExpectCreationReply() => _requireReply = true;

    internal void CreatedPanReply(uint code, string text)
    {
        string id = text?.Split(',')[0].Trim();
        if (code == 0 && id != null && uint.TryParse(id.Replace("0x", "", StringComparison.OrdinalIgnoreCase),
            System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint pan))
            _replyPan = pan;
    }

    internal LoopbackSliceResources(IEnumerable<Slice> slices, IEnumerable<Panadapter> pans)
    {
        _beforeSlices = new(slices);
        _beforePans = new(pans);
    }

    internal bool Capture(IEnumerable<Slice> slices, IEnumerable<Panadapter> pans)
    {
        if (Slice != null) return IsPresent(slices, pans);
        if (_requireReply && _replyPan == null) return false;
        if (_replyPan is { } id)
        {
            Pan ??= pans.SingleOrDefault(p => p.StreamID == id && !_beforePans.Contains(p));
            var matches = slices.Where(s => !_beforeSlices.Contains(s) && Pan != null && ReferenceEquals(s.Panadapter, Pan)).ToArray();
            if (matches.Length != 1) return false;
            Slice = matches[0];
            return true;
        }
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
        SavedRxAnt = Slice.RXAnt;
        SavedRFGain = Slice.RFGain;
        SavedFrequency = Slice.Freq;
        SavedMode = Slice.DemodMode;
        SavedPanCenter = Pan.CenterFreq;
        Slice.Freq = transmit.Freq;
        Slice.DemodMode = transmit.DemodMode;
        Slice.RXAnt = antenna;
        return true;
    }

    internal bool Release(IEnumerable<Slice> slices, IEnumerable<Panadapter> pans, Slice active, Slice transmit)
    {
        // Object membership protects a resource whose numeric ID was reused.
        var roster = slices.ToArray();
        bool hasSlice = Slice != null && roster.Contains(Slice);
        bool hasPan = Pan != null && pans.Contains(Pan);
        if (hasSlice && (!ReferenceEquals(Slice.Panadapter, Pan) || ReferenceEquals(Slice, active) || ReferenceEquals(Slice, transmit))) return false;
        // A surviving pan can outlive our slice; never close it if another
        // slice has since adopted it.
        if (hasPan && roster.Any(s => !ReferenceEquals(s, Slice) && ReferenceEquals(s.Panadapter, Pan))) return false;
        if (hasSlice) Slice.Close();
        if (hasPan) Pan.Close();
        return true;
    }
}
