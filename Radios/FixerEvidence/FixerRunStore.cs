using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Radios.Fixer.Evidence
{
    /// <summary>
    /// Where Fixer runs live on disk: one JSON file per run, written whole and
    /// atomically on every recording.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Location:</b> <c>FixerRuns</c> under the settings root, resolved
    /// from <see cref="RadioConfig.AppDataRoot"/> — never from
    /// SpecialFolder.ApplicationData plus "JJFlexRadio", which is invisible to
    /// JJFLEX_CONFIG_DIR relocation (nineteen sites made that mistake; see
    /// CLAUDE.md). A test run against a throwaway tree keeps its run records
    /// in the throwaway tree.
    /// </para>
    /// <para>
    /// <b>Retention, decided up front (#252, on #92's precedent):</b> the
    /// newest <see cref="MaxRunsKept"/> runs are kept, oldest deleted beyond
    /// that. A count cap rather than an age cap, deliberately: a run record is
    /// evidence, and "it worked Tuesday" gains value with age — but "small and
    /// unbounded" is how AppData reached 2.2 GB, so the bound exists from day
    /// one. Two hundred runs at a few tens of KB each is a couple of MB.
    /// </para>
    /// <para>
    /// The mechanics — atomic writes, pruning, the unreadable-file census —
    /// live in <see cref="EvidenceFileStore{TRecord}"/>, shared with the QSO
    /// signal capture store. This class contributes only what is Fixer-shaped:
    /// the folder, the caps, and the resume window.
    /// </para>
    /// </remarks>
    public sealed class FixerRunStore : EvidenceFileStore<FixerRunRecord>
    {
        /// <summary>See the class remarks for why a count, and why this count.</summary>
        public const int MaxRunsKept = 200;

        /// <summary>How far back the resume list reaches. A run stopped months
        /// ago is still viewable evidence, but offering it for resumption is
        /// noise — the station has moved on, and the fingerprint check would
        /// say little else.</summary>
        public const int ResumeWindowDays = 14;

        public const string FolderName = "FixerRuns";

        /// <summary>The store at an explicit root. Tests use this; the app
        /// uses <see cref="Default"/>.</summary>
        public FixerRunStore(string rootDir)
            : base(rootDir, "run-", MaxRunsKept, "FixerRunStore")
        {
        }

        /// <summary>The store under the settings root.</summary>
        public static FixerRunStore Default()
            => new FixerRunStore(Path.Combine(RadioConfig.AppDataRoot, FolderName));

        protected override string IdOf(FixerRunRecord record) => record.RunId;
        protected override DateTime StartedUtcOf(FixerRunRecord record) => record.StartedUtc;
        protected override string Serialize(FixerRunRecord record) => record.ToJson();
        protected override FixerRunRecord Deserialize(string json) => FixerRunRecord.FromJson(json);

        /// <summary>
        /// Runs that stopped part-way and are recent enough to offer for
        /// resumption (#252 part 2): incomplete, and started within
        /// <see cref="ResumeWindowDays"/> of <paramref name="nowUtc"/>.
        /// Newest first, so the most likely candidate leads the list.
        /// </summary>
        public IReadOnlyList<FixerRunRecord> StoppedRuns(DateTime nowUtc)
        {
            DateTime cutoff = nowUtc.AddDays(-ResumeWindowDays);
            return LoadAll(out _)
                .Where(r => !r.IsComplete() && r.StartedUtc >= cutoff)
                .ToList();
        }
    }
}
