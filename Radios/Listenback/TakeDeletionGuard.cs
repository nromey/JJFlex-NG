using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Radios.Listenback;

/// <summary>Whether a take may be deleted now, and if not, why.</summary>
/// <param name="Reason">"" when allowed; otherwise one of the <see cref="TakeDeletionGuard"/> reason constants.</param>
public sealed record TakeDeletionVerdict(bool Allowed, string Reason);

/// <summary>
/// The ONE guard on deleting a take (Unified takes, design section 8): a take
/// is not deleted while a radio setting it changed is still owed a put-back,
/// on ANY radio, connected or not, and an unreadable store refuses rather
/// than reading as "nothing owed".
/// </summary>
/// <remarks>
/// <para>
/// <b>Checked at the moment of deletion</b>, under the same lock the test
/// registry uses, never only when a button is enabled: a capture or a
/// restore can begin between the two.
/// </para>
/// <para>
/// <b>What counts as owed.</b> An ACTIVE recovery record naming the folder; a
/// half-written (<c>.pending</c>) record anywhere, which may be intent; a
/// record the next connect took into its put-back (<c>.json.put-back-*</c>)
/// that names the folder while that radio still has any setting to put back
/// (current data cannot say which settings came from which take, so the block
/// is conservative); and a live obligation the caller knows of (a capture
/// running, a restore registered in this run). A historical
/// <c>prior-radio-state</c> line in take.txt is NOT proof of current debt and
/// is not read. Acknowledged and unreadable archives are retired evidence.
/// </para>
/// <para>
/// <b>Deletes only the chosen folder.</b> Never the recovery store, the
/// put-back store or the test registry. A deleted take's number stays held
/// as a tombstone.
/// </para>
/// </remarks>
public static class TakeDeletionGuard
{
    public const string OutsideRoot = "outside-root";
    public const string Missing = "missing";
    public const string ReparsePoint = "reparse-point";
    public const string RestoreOwed = "restore-owed";
    public const string PutBackOwed = "put-back-owed";
    public const string StoreUnreadable = "store-unreadable";
    public const string Busy = "busy";
    public const string DeleteFailed = "delete-failed";

    /// <summary>The folder, canonical, if it is a take folder directly under the root; otherwise null.</summary>
    public static string Canonical(string takesRoot, string takeFolder)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(takesRoot));
        string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(takeFolder));
        if (!string.Equals(Path.GetDirectoryName(folder), root, StringComparison.OrdinalIgnoreCase)) return null;
        return TakeCatalog.IsTakeFolder(folder) ? folder : null;
    }

    /// <summary>Whether <paramref name="takeFolder"/> may be deleted now.</summary>
    /// <param name="recoveryRoot">The listen-back recovery store's folder.</param>
    /// <param name="reminders">The put-back store, read strictly.</param>
    /// <param name="busy">A live obligation in this run: a capture, a render, a restore registered for the folder.</param>
    public static TakeDeletionVerdict Check(string takesRoot, string takeFolder, string recoveryRoot,
        CheckRestoreReminderStore reminders, Func<string, bool> busy = null)
    {
        string folder = Canonical(takesRoot, takeFolder);
        if (folder == null) return new(false, OutsideRoot);
        if (!Directory.Exists(folder)) return new(false, Missing);
        if (new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint)) return new(false, ReparsePoint);
        try { if (busy?.Invoke(folder) == true) return new(false, Busy); }
        catch (Exception) { return new(false, Busy); }
        try
        {
            if (Directory.Exists(recoveryRoot))
            {
                foreach (string path in Directory.EnumerateFiles(recoveryRoot))
                {
                    string name = Path.GetFileName(path);
                    if (name.EndsWith(".pending", StringComparison.OrdinalIgnoreCase)) return new(false, StoreUnreadable);
                    bool active = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
                    bool takenIntoPutBack = name.Contains(".json.put-back-", StringComparison.OrdinalIgnoreCase);
                    if (!active && !takenIntoPutBack) continue; // acknowledged or unreadable archives: retired evidence
                    var snapshot = JsonSerializer.Deserialize<RadioRecoverySnapshot>(File.ReadAllBytes(path));
                    if (snapshot == null || string.IsNullOrEmpty(snapshot.TakeDirectory) || string.IsNullOrEmpty(snapshot.Serial))
                        return new(false, StoreUnreadable);
                    if (!SameFolder(snapshot.TakeDirectory, folder)) continue;
                    if (active) return new(false, RestoreOwed);
                    if (reminders.PutBacksForStrict(snapshot.Serial).Count > 0) return new(false, PutBackOwed);
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
        { return new(false, StoreUnreadable); }
        return new(true, "");
    }

    /// <summary>
    /// Check again and, only if allowed, delete the folder, then hold its
    /// number as a tombstone in its test. A partial deletion is reported as
    /// a failure, never as success.
    /// </summary>
    public static TakeDeletionVerdict Delete(string takesRoot, string takeFolder, string recoveryRoot,
        CheckRestoreReminderStore reminders, Func<string, bool> busy = null)
    {
        string folder = Canonical(takesRoot, takeFolder);
        var membership = folder == null ? null : TakeSession.Membership(takesRoot, folder);
        // The final check and the deletion happen under the store's lock.
        var verdict = TakeSession.WithStoreLock(takesRoot, () =>
        {
            var checkedNow = Check(takesRoot, takeFolder, recoveryRoot, reminders, busy);
            if (!checkedNow.Allowed) return checkedNow;
            try { Directory.Delete(folder, recursive: true); return new TakeDeletionVerdict(true, ""); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                JJTrace.Tracing.TraceLine("TakeDeletionGuard: " + folder + ": " + ex.Message, System.Diagnostics.TraceLevel.Warning);
                return new TakeDeletionVerdict(false, DeleteFailed);
            }
        });
        if (!verdict.Allowed) return verdict;
        if (membership is { } member)
            try { TakeSession.MarkReservation(takesRoot, member.Test.Id, folder, TakeReservation.Deleted); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is InvalidOperationException)
            { JJTrace.Tracing.TraceLine("TakeDeletionGuard tombstone: " + ex.Message, System.Diagnostics.TraceLevel.Warning); }
        return new(true, "");
    }

    private static bool SameFolder(string recorded, string folder)
    {
        try { return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(recorded)), folder, StringComparison.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        { return true; } // a path we cannot read could be this one: fail closed
    }
}
