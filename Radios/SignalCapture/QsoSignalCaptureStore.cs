#nullable enable
using System;
using System.IO;
using Radios.Fixer.Evidence;

namespace Radios.SignalCapture
{
    /// <summary>
    /// Where QSO signal captures live on disk: one JSON file per capture,
    /// written once, at the moment the capture stops.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The mechanics — atomic writes, name-order-is-date-order, pruning, the
    /// unreadable-file census — are <see cref="EvidenceFileStore{TRecord}"/>,
    /// shared with the Fixer's run store per #252's ruling that evidence
    /// artifacts share one shape. Own folder, because the schema version
    /// inside a record file is per-family.
    /// </para>
    /// <para>
    /// <b>Retention, decided up front:</b> the newest
    /// <see cref="MaxCapturesKept"/> captures are kept, oldest deleted beyond
    /// that — labelled or not, because a label must not quietly turn the cap
    /// off. Fifty, not the Fixer's two hundred: a capture carries its raw
    /// sample series, so files run hundreds of KB where a Fixer run is tens.
    /// Export is the road to keeping something forever, and the list dialog
    /// says so.
    /// </para>
    /// </remarks>
    public sealed class QsoSignalCaptureStore : EvidenceFileStore<QsoSignalCaptureRecord>
    {
        /// <summary>See the class remarks for why a count, and why this count.</summary>
        public const int MaxCapturesKept = 50;

        public const string FolderName = "SignalCaptures";

        /// <summary>The store at an explicit root. Tests use this; the app
        /// uses <see cref="Default"/>.</summary>
        public QsoSignalCaptureStore(string rootDir)
            : base(rootDir, "capture-", MaxCapturesKept, "QsoSignalCaptureStore")
        {
        }

        /// <summary>The store under the settings root.</summary>
        public static QsoSignalCaptureStore Default()
            => new QsoSignalCaptureStore(Path.Combine(RadioConfig.AppDataRoot, FolderName));

        protected override string IdOf(QsoSignalCaptureRecord record) => record.CaptureId;
        protected override DateTime StartedUtcOf(QsoSignalCaptureRecord record) => record.StartedUtc;
        protected override string Serialize(QsoSignalCaptureRecord record) => record.ToJson();
        protected override QsoSignalCaptureRecord? Deserialize(string json)
            => QsoSignalCaptureRecord.FromJson(json);
    }
}
