using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Radios;

/// <summary>
/// Detects a key map whose bindings have slipped onto the wrong commands.
/// </summary>
/// <remarks>
/// <para>
/// KeyDefs.xml stores each binding against <c>(int)CommandValues</c>. Before
/// 2026-08-21 those numbers were positional, so inserting a member mid-enum
/// shifted every later one and a file written by the older build loaded with
/// bindings attributed to the wrong commands. Commit 40307951 (2026-08-18)
/// inserted SpeakContextHelp at position 96 and did exactly that; c9a4b984
/// froze the numbers afterwards.
/// </para>
/// <para>
/// <b>The freeze prevents future damage. It does not repair a file already
/// written in the old numbering.</b> Measured 2026-08-24 against a NAS AppData
/// snapshot: 22 ids differed, starting at 96, each holding the previous
/// entry's key.
/// </para>
/// <para>
/// <b>Why not just read the version field.</b> Both the damaged snapshot and a
/// healthy live file carry <c>Version 5</c>. The version tracked the schema and
/// not its meaning, so it did not move when the meaning changed — a smoke
/// detector wired to the light switch. The evidence used here is per-entry and
/// provable instead: every saved binding records <c>SavedDefaultKey</c>, the
/// default that command had when the file was written. If that no longer
/// matches the current default for the id it is filed under, but DOES match the
/// default for the id one below, the entry slipped.
/// </para>
/// <para>
/// <b>This class REPORTS. It never repairs.</b> A silent transformation that is
/// wrong in some unconsidered case corrupts a working configuration and the
/// operator has no way to tell. Deciding what to do about a slipped map is a
/// separate, deliberate act.
/// </para>
/// </remarks>
public static class KeyMapIntegrity
{
    /// <summary>One saved binding, reduced to what the check needs.</summary>
    public readonly struct SavedBinding
    {
        /// <summary>The command number the entry is filed under.</summary>
        public readonly int Id;
        /// <summary>The default this command had when the file was written.</summary>
        public readonly Keys SavedDefault;

        public SavedBinding(int id, Keys savedDefault)
        {
            Id = id;
            SavedDefault = savedDefault;
        }
    }

    /// <summary>What the check concluded.</summary>
    public readonly struct Verdict
    {
        /// <summary>Entries whose SavedDefault matches the current default for their id.</summary>
        public readonly int Consistent;
        /// <summary>Entries whose SavedDefault matches the current default for id-1.</summary>
        public readonly int SlippedByOne;
        /// <summary>Entries that match neither — changed defaults, renames, genuine drift.</summary>
        public readonly int Unexplained;
        /// <summary>Entries skipped because SavedDefault was never recorded.</summary>
        public readonly int Untracked;
        /// <summary>Lowest id that slipped, or -1. The insertion point, when there is one.</summary>
        public readonly int FirstSlippedId;

        public Verdict(int consistent, int slippedByOne, int unexplained, int untracked, int firstSlippedId)
        {
            Consistent = consistent;
            SlippedByOne = slippedByOne;
            Unexplained = unexplained;
            Untracked = untracked;
            FirstSlippedId = firstSlippedId;
        }

        /// <summary>
        /// True when the map looks shifted rather than merely out of date.
        /// </summary>
        /// <remarks>
        /// Requires a RUN of slipped entries, not one or two. A single command
        /// whose default was deliberately reassigned produces one mismatch and
        /// must not be reported as a corrupted map — that is an ordinary change
        /// and SmartMergeDefaults already handles it. An off-by-one insertion
        /// shifts everything after the insertion point at once, so the real
        /// signal is bulk.
        /// </remarks>
        public bool LooksShifted => SlippedByOne >= SlipRunThreshold;

        /// <summary>Plain-language summary for the trace. Never speaks on its own.</summary>
        public string Describe()
        {
            if (LooksShifted)
            {
                return "key map looks SHIFTED: " + SlippedByOne + " binding(s) carry the default of the"
                    + " command one number below them, first at id " + FirstSlippedId
                    + ". A file written before the command numbers were frozen loads its bindings onto"
                    + " the wrong commands. " + Consistent + " consistent, " + Unexplained + " unexplained, "
                    + Untracked + " untracked.";
            }
            return "key map consistent: " + Consistent + " matched, " + SlippedByOne + " slipped, "
                + Unexplained + " unexplained, " + Untracked + " untracked (no shift indicated).";
        }
    }

    /// <summary>
    /// How many slipped entries it takes before this is called a shift rather
    /// than ordinary default churn. The measured 2026-08-18 insertion moved 22.
    /// Five is comfortably above routine reassignment and far below that.
    /// </summary>
    public const int SlipRunThreshold = 5;

    /// <summary>
    /// Compare a loaded key map against the current defaults.
    /// </summary>
    /// <param name="saved">Bindings as they came off disk, unmodified.</param>
    /// <param name="currentDefaultFor">
    /// Current default key for a command number, or <see cref="Keys.None"/> when
    /// that command has no default. Must not throw for unknown numbers.
    /// </param>
    public static Verdict Check(IEnumerable<SavedBinding> saved, Func<int, Keys> currentDefaultFor)
    {
        if (saved == null || currentDefaultFor == null)
            return new Verdict(0, 0, 0, 0, -1);

        int consistent = 0, slipped = 0, unexplained = 0, untracked = 0, firstSlipped = -1;

        foreach (var b in saved)
        {
            // Older builds, and intermediate ones that did not track it cleanly,
            // store None. No evidence either way — do not guess from silence.
            if (b.SavedDefault == Keys.None) { untracked++; continue; }

            Keys hereNow = currentDefaultFor(b.Id);
            if (b.SavedDefault == hereNow) { consistent++; continue; }

            // The signature of an insertion: this entry carries what the command
            // one NUMBER below it has as its default today. Numeric, because the
            // damage was numeric — the enum's source order is irrelevant.
            Keys oneBelowNow = currentDefaultFor(b.Id - 1);
            if (oneBelowNow != Keys.None && b.SavedDefault == oneBelowNow)
            {
                slipped++;
                if (firstSlipped < 0 || b.Id < firstSlipped) firstSlipped = b.Id;
                continue;
            }

            unexplained++;
        }

        return new Verdict(consistent, slipped, unexplained, untracked, firstSlipped);
    }
}
