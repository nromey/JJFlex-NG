using System;
using System.Collections.Generic;
using System.Linq;
using JJTrace;

namespace JJFlexWpf.Dialogs;

// The radio list's held SmartLink lists (#1094): moved out of
// RigSelectorDialog, unchanged, once Radio Associations became the second
// caller (#405), so a third caller reuses them rather than copying them.
// Read outside the radio list's _radiosLock; an unreadable table answers
// HeldLists.Unread. The trace lines keep their "RigSelector." prefix, as
// before the move.

/// <summary>
/// Every held SmartLink session's account and radio list snapshot,
/// read ONCE (#1059): the one reader of the held lists, used by the
/// rows, the Network identity card, the replan of the rows' accounts
/// and the presence settle loop. <see cref="Read"/> is false when the
/// session table could not be read; each caller then gives its own
/// safe answer (see the members, and <see cref="HeldRadioLists.ListsKey"/>).
/// </summary>
/// <remarks>
/// <para><b>Why one reader.</b> The merge of junk box batches 4 and 5
/// left two (<c>SessionListsNow</c> for the card and the replan's own
/// walk of the sessions), beside <c>LiveSessionAccounts</c> for the
/// rows: three readings of one table per repaint, each able to see a
/// different moment. The snapshot carries <c>SessionConnected</c>, the
/// same fact <c>IsConnected</c> gave, so nothing is lost by reading
/// only it.</para>
/// <para>A session with no account is kept, under "", because the
/// replan's change check compared it; the per-account views skip it,
/// as their predecessors did.</para>
/// </remarks>
internal readonly record struct HeldLists(
    IReadOnlyList<(string Account, Radios.SmartLink.SessionRadioListSnapshot List)> Sessions, bool Read)
{
    /// <summary>The answer when the table could not be read.</summary>
    public static HeldLists Unread { get; } =
        new(Array.Empty<(string, Radios.SmartLink.SessionRadioListSnapshot)>(), false);

    /// <summary>
    /// The accounts holding a connected session (#340): whether
    /// anything is in a position to hear from a radio at all. Empty
    /// when unread: we certainly cannot claim to have heard anything,
    /// and every affected row falls back to saying we have not asked.
    /// </summary>
    public HashSet<string> LiveAccounts() => Accounts(l => l.SessionConnected);

    /// <summary>
    /// The accounts whose session has ANSWERED: connected, and its list
    /// arrived on that live connection (#1059; the predicate the card's
    /// <see cref="Radios.NetworkIdentityInfo.SmartLinkAnsweredWithout"/>
    /// and the plan's "every saved list has answered" use). A session
    /// is connected before its first list, and again after every
    /// reconnect until the new list lands. Empty when unread.
    /// </summary>
    public HashSet<string> AnsweredAccounts() =>
        Accounts(l => l.SessionConnected && l.ArrivedOnTheLiveConnection);

    /// <summary>
    /// Each named account's list, for the card's answered-without test
    /// (#398, repair three). Empty when unread, so nothing reads as
    /// answered: wrong in the safe direction.
    /// </summary>
    public List<(string Account, Radios.SmartLink.SessionRadioListSnapshot List)> NamedLists() =>
        Sessions.Where(x => !string.IsNullOrWhiteSpace(x.Account)).ToList();

    private HashSet<string> Accounts(Func<Radios.SmartLink.SessionRadioListSnapshot, bool> keep)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (account, list) in Sessions)
            if (!string.IsNullOrWhiteSpace(account) && keep(list)) set.Add(account);
        return set;
    }
}

/// <summary>
/// The one reader of the held lists, and the change check both the radio list
/// and Radio Associations compare them by.
/// </summary>
internal static class HeldRadioLists
{
    /// <summary>
    /// Read the held lists once (<see cref="HeldLists"/>). Takes the
    /// session coordinator's lock, so callers ask it outside
    /// <c>_radiosLock</c>. Failure answers <see cref="HeldLists.Unread"/>,
    /// never a partial reading.
    /// </summary>
    internal static HeldLists ReadHeldLists()
    {
        var sessions = new List<(string Account, Radios.SmartLink.SessionRadioListSnapshot List)>();
        try
        {
            foreach (var session in Radios.SmartLink.SmartLinkServices.Coordinator.AllSessions)
            {
                if (session == null) continue;
                sessions.Add((session.AccountId ?? "", session.RadioListSnapshot));
            }
        }
        catch (Exception ex)
        {
            Tracing.TraceLine($"RigSelector.ReadHeldLists: {ex.Message}",
                System.Diagnostics.TraceLevel.Warning);
            return HeldLists.Unread;
        }
        return new HeldLists(sessions, true);
    }

    /// <summary>
    /// What the replan compares (#405 repair three): every held session's
    /// account and whole list snapshot, then the saved accounts. Null when
    /// either could not be read, which reads as moved, so the rows are
    /// asked every time (that caller's safe answer: asked once too often,
    /// never once too few). From the one reading (#1059).
    /// </summary>
    internal static List<object>? ListsKey(HeldLists lists)
    {
        if (!lists.Read) return null;
        var key = new List<object>();
        foreach (var (account, list) in lists.Sessions)
        {
            key.Add(account);
            key.Add(list);
        }
        try
        {
            foreach (var account in Radios.FlexBase.SharedAccountManager.Accounts)
                key.Add(account?.Email ?? "");
        }
        catch (Exception ex)
        {
            Tracing.TraceLine($"RigSelector.ListsKey: {ex.Message}",
                System.Diagnostics.TraceLevel.Warning);
            return null;
        }
        return key;
    }

    /// <summary>The replan's change check: moved unless both readings
    /// exist and are equal, element by element, by each snapshot's own
    /// equality.</summary>
    internal static bool ListsMovedSince(List<object>? now, List<object>? askedAt) =>
        now == null || askedAt == null || !now.SequenceEqual(askedAt);
}
