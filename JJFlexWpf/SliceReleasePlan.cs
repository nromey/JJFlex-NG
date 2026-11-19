namespace JJFlexWpf;

/// <summary>
/// #1177 (Sprint 49 Track H2, rulings repair 1): where the active and the
/// transmit slice go before a slice is released. The radio refuses to release
/// the active slice or the transmit slice (<c>FlexBase.RemoveSlice</c>), so a
/// row that releases a slice which is either must move it first. Release Active
/// Slice always did; the plain Release Slice row (which releases the LAST
/// slice) did not, so with the last slice active it said only that the release
/// failed. Both rows now ask this one plan, so they cannot drift apart again.
/// </summary>
internal static class SliceReleasePlan
{
    /// <summary>The slice the active (and if needed the transmit) slice moves to: the first of ours that is not the one released. Null when there is none.</summary>
    internal static int? MoveTo(int toRemove, int sliceCount)
    {
        for (int j = 0; j < sliceCount; j++)
            if (j != toRemove) return j;
        return null;
    }

    /// <summary>
    /// The active and transmit slices once the one being released is no longer
    /// either: each that is the released slice moves to <see cref="MoveTo"/>;
    /// the other stays. Null when there is nowhere to move.
    /// </summary>
    internal static (int Active, int Transmit)? Before(int toRemove, int sliceCount, int active, int transmit, bool canTransmit)
    {
        if (MoveTo(toRemove, sliceCount) is not int to) return null;
        return (active == toRemove ? to : active, canTransmit && transmit == toRemove ? to : transmit);
    }
}
