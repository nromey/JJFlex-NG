namespace Radios.Fixer.Evidence
{
    /// <summary>
    /// What every stored evidence record has in common: a report, already
    /// written, in the words it had when the thing being recorded happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="EvidenceFileStore{TRecord}"/> is generic over the record but
    /// never constrained it, so nothing in the shared machinery could say "a
    /// record carries a report". Each family therefore wrote its own
    /// <c>PlainText</c>, three identical lines apiece — caught by the
    /// integration pass on the Sprint 36 merge (2026-08-27), because a
    /// duplicate implementation conflicts with nothing and builds cleanly.
    /// </para>
    /// <para>
    /// The report text is BAKED, not rendered on demand. A stored capture is
    /// never re-analysed and a past run is never re-run, so what comes back
    /// out is what a reader would have been told at the time — which is the
    /// property that makes it evidence rather than a recalculation.
    /// </para>
    /// </remarks>
    public interface IEvidenceRecord
    {
        /// <summary>The report as plain text, exactly as it was recorded.</summary>
        string ReportText { get; }
    }
}
