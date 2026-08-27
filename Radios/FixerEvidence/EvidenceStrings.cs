namespace Radios.Fixer.Evidence;

/// <summary>
/// The words and formats shared by every evidence surface — the Fixer's past
/// runs, the QSO signal captures, and anything that joins them later.
/// </summary>
/// <remarks>
/// <para>
/// These lived twice, once in each dialog, until the integration pass caught
/// them on the Sprint 36 merge (2026-08-27). Neither copy was wrong and no
/// merge conflicted, which is exactly why a duplicate survives: two agents
/// implementing one idea in two files produce working code, a clean merge and
/// a green build. Correcting the sentence in one dialog would simply have left
/// the other saying something different.
/// </para>
/// <para>
/// A viewer that gains a third evidence family should read these, not copy
/// them. If the receipt below ever moves into the keyed utterance store, it
/// moves from HERE and both callers follow — which is the whole point of it
/// having one home first.
/// </para>
/// </remarks>
public static class EvidenceStrings
{
    /// <summary>
    /// Spoken after a report is copied. It names the FORMAT on purpose: a
    /// blind operator pasting into an email needs to know they are getting
    /// text rather than the rendered page they were just reading.
    /// </summary>
    public const string CopiedToClipboard = "The report is on the clipboard, as plain text.";

    /// <summary>
    /// The save-dialog filter for an exported report. HTML leads because the
    /// rendered page is the one a recipient can read without our software —
    /// which is the requirement an exported report exists to meet.
    /// </summary>
    public const string ExportFilter = "Web page (*.html)|*.html|Plain text (*.txt)|*.txt";
}
