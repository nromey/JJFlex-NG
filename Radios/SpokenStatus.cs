namespace Radios
{
    /// <summary>
    /// A spoken status and its own answer to "does this still describe the
    /// radio", both from one reading (<see cref="RadioStatusBuilder.CaptureSpokenStatus(FlexBase, StatusAsk?)"/>,
    /// Sprint 49 Track N2 repair 1). The answer is for the speech arbiter to
    /// ask before replaying an unheard copy; a sentence is true when built.
    /// </summary>
    public sealed record SpokenStatus(string Text, System.Func<bool> StillValid);

    /// <summary>
    /// What the app has just asked the radio for and may not yet have heard
    /// back: a memory recall's frequency and mode (#1131). The spoken status
    /// names them in place of what the radio reports.
    /// </summary>
    /// <param name="FrequencyHz">The asked-for frequency; 0 leaves the reported one.</param>
    /// <param name="Mode">The asked-for mode; empty leaves the reported one.</param>
    /// <param name="StillPending">
    /// Whether this request is still the live one: inside its lifetime and not
    /// superseded by a later request. While it is, the status allows the radio's
    /// separate reports on the way there; afterwards only the sentence itself
    /// holds.
    /// </param>
    public sealed record StatusAsk(ulong FrequencyHz, string Mode, System.Func<bool> StillPending);
}
