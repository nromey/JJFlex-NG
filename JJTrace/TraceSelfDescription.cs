using System;
using System.Collections.Generic;

namespace JJTrace
{
    /// <summary>
    /// A kind of data record a writer puts into the trace file, together with
    /// that writer's own plain-English description of it.
    ///
    /// <para><b>Why this exists (#625, ruled by Noel 2026-09-25).</b> The drop
    /// window used to tell the operator what the archived file contained —
    /// "it holds the forward and reflected power readings and the amplifier
    /// temperature" — and three review rounds found three ways for that to be
    /// false: a receive-only session records no power; a second, opt-in writer
    /// put meter lines in that the count could not see; a power line can be
    /// written before the radio has reported any power. Each fix made the
    /// sentence more precise and each more precise sentence found a new way to
    /// be wrong. That is the signature of a claim made by an OBSERVER of
    /// several independent writers. The rule the ruling establishes: <i>the
    /// thing that WROTE the data is the thing that may describe it.</i></para>
    ///
    /// <para><b>So the description travels with the data.</b> A writer that
    /// emits a stream of records — the transmit meter line, the temperature
    /// window, the opt-in meter stream — owns a kind, and hands it to the sink
    /// with every record. The sink knows nothing about what the text means; it
    /// knows only whether this kind has introduced itself in the part it is
    /// writing, and if not, it writes the writer's introduction immediately
    /// before the first record, in the same part, under the same lock. A file
    /// therefore says what it holds exactly where it starts holding it, and a
    /// reader — Noel's whole point was <i>"reading a header blind or not"</i> —
    /// searches for one phrase to find every kind that has introduced itself.
    /// Only a DECLARED kind can: a writer that uses a bare <c>TraceLine</c>
    /// appears unexplained, which is why the reading guide promises no more
    /// than that (see <see cref="TraceSelfDescription.PartPreamble"/>). Not a summary at the end,
    /// which would be the observer problem one layer down; not a promise at
    /// the top of what the file WILL contain, which describes an intention
    /// rather than a fact.</para>
    ///
    /// <para><b>Per part, on purpose.</b> A session that rotated is several
    /// files, each archived and read on its own, so each introduces its own
    /// kinds afresh.</para>
    ///
    /// <para><b>The introduction is prose an operator reads.</b> Every
    /// <see cref="Introduction"/> is a DRAFT for Noel — written by an agent,
    /// never read aloud by a person — and is listed as such in the H16
    /// report. It is not lexicon: this text is written into a file that leaves
    /// the machine and is read by whoever diagnoses it, not spoken by the
    /// application, and the lexicon's job is what the application says to the
    /// operator.</para>
    /// </summary>
    public sealed class TraceRecordKind
    {
        /// <param name="key">A short stable identity, e.g. <c>txMeters</c>.
        /// The sink remembers introductions by this, so two writers must not
        /// share one unless they really write the same kind of line.</param>
        /// <param name="introduction">What the lines of this kind are, in
        /// plain English, written by the code that writes them. Without the
        /// <see cref="TraceSelfDescription.Marker"/>; the sink adds it.</param>
        public TraceRecordKind(string key, string introduction)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("a record kind needs a key", nameof(key));
            if (string.IsNullOrWhiteSpace(introduction)) throw new ArgumentException("a record kind needs an introduction", nameof(introduction));
            Key = key;
            Introduction = introduction.Trim();
        }

        /// <summary>The stable identity the sink remembers introductions by.</summary>
        public string Key { get; }

        /// <summary>The writer's description of its own lines, without the marker.</summary>
        public string Introduction { get; }

        public override string ToString() => Key;
    }

    /// <summary>
    /// One line handed to the trace boundary as a terminal record: its text and,
    /// when the line is a data record, the kind that describes it. A plain
    /// string converts implicitly, so a caller with an ordinary sentence writes
    /// what it always wrote; a writer with a data record attaches its kind.
    /// </summary>
    public sealed class TraceRecord
    {
        public TraceRecord(string text, TraceRecordKind kind = null)
        {
            Text = text ?? string.Empty;
            Kind = kind;
        }

        /// <summary>The line, without the trace prefix.</summary>
        public string Text { get; }

        /// <summary>The kind that introduces this line, or null for an ordinary sentence.</summary>
        public TraceRecordKind Kind { get; }

        public static implicit operator TraceRecord(string text) =>
            text == null ? null : new TraceRecord(text);

        public override string ToString() => Text;
    }

    /// <summary>
    /// The lines a trace file writes ABOUT ITSELF: the reading guide at the top
    /// of every part, the introduction each kind of record gets where it first
    /// appears, and the one line the archive writes to say why it closed the
    /// file. Every sentence here is a DRAFT for Noel (#625); see the H16 report.
    /// </summary>
    public static class TraceSelfDescription
    {
        /// <summary>
        /// The words every self-describing line begins with, after the trace
        /// prefix. The reading guide tells the operator to search for them,
        /// which is how a blind reader of a large text file lists what it
        /// holds — so this phrase is the whole navigation scheme, and a
        /// change to it changes what the guide must say.
        /// </summary>
        public const string Marker = "About this file:";

        /// <summary>
        /// The reading guide written at the top of every part, after the
        /// machine-readable header line that names the session and part. It
        /// says what the file is and how to find what it holds; it does not say
        /// what it holds, because at the moment it is written nothing has been
        /// written yet and a promise about the future is not a fact.
        /// </summary>
        /// <remarks>
        /// <para>DRAFT for Noel. Plain English for a screen reader, one
        /// sentence or two per line, each line complete in itself because a
        /// reader arrows through the file a line at a time.</para>
        /// <para><b>It claims only what the mechanism guarantees (H17, Sol's
        /// H16 review).</b> H16's wording said every kind of measurement
        /// introduces itself and that searching for the marker lists what the
        /// file carries. The sink introduces only a kind a writer DECLARED, and
        /// two measurement writers in a detailed capture did not declare one
        /// (<c>VoltsDataHandler:</c>, <c>paEffData:</c>). Declaring them fixed
        /// those two; it cannot fix the next one, because nothing stops a
        /// future writer using a bare <c>TraceLine</c>. So the guide now says
        /// what is true whatever any writer does: lines that introduce
        /// themselves are explained, other lines may appear unexplained, and
        /// the search lists what HAS introduced itself — never "everything".
        /// A missed writer is then an unexplained line the reader can see,
        /// not a false statement about the file.</para>
        /// <para><b>And "most" lines carry the prefix, not each.</b> A
        /// rotation's continuation header, a line handed straight to
        /// <c>Trace.WriteLine</c> or the radio library's <c>Debug.WriteLine</c>,
        /// and the second and later lines of a message that itself contains
        /// line breaks all reach the file without one. Those writers are left
        /// as they are; the sentence changed to match them. For the same
        /// reason it no longer says "one event per line".</para>
        /// </remarks>
        public static IReadOnlyList<string> PartPreamble() => new[]
        {
            Marker + " this is a JJ Flexible Radio Access diagnostic recording. It is plain text, in the order"
                   + " it was written. Most lines begin with the milliseconds since the program started and, in"
                   + " square brackets, the thread that wrote it. Some do not: for example a line passed straight"
                   + " through from the radio library, or the second and later lines of a long message such as"
                   + " an error report.",
            Marker + " some kinds of line explain themselves the first time they appear in this file, on a line"
                   + " that begins like this one. To find those explanations, search for the words 'About this"
                   + " file'. That finds only the kinds that have explained themselves; the file can also hold"
                   + " lines that carry no explanation. If the recording was able to say how it ended, that is on"
                   + " its last lines.",
        };

        /// <summary>The introduction line for a kind, as the sink writes it (without the trace prefix).</summary>
        public static string Introduction(TraceRecordKind kind)
        {
            if (kind == null) throw new ArgumentNullException(nameof(kind));
            return Marker + " " + kind.Introduction;
        }

        /// <summary>
        /// The line the archive writes as it closes the file, before the
        /// terminal state marker: why this recording ended, in the words the
        /// closer has. The closer is the thing that knows, and it writes it at
        /// the moment it acts — this is not a summary of the file's contents.
        /// </summary>
        /// <param name="outcome">The session outcome, e.g. <c>connection_dropped</c>.</param>
        /// <param name="detail">The sentence recorded with it, or null.</param>
        /// <remarks>DRAFT for Noel.</remarks>
        public static string Closing(string outcome, string detail)
        {
            string label = TraceOutcomeLabels.Display(outcome);
            string why = string.IsNullOrWhiteSpace(detail) ? label : label + ". " + detail.Trim().TrimEnd('.');
            return Marker + " this recording is closed. Why it ended: " + why + ".";
        }

        /// <summary>
        /// The line written when a part is frozen for a problem report and the
        /// recording carries on in the next part, so a reader of this part
        /// knows it is not the end of the session.
        /// </summary>
        /// <remarks>DRAFT for Noel.</remarks>
        public static string CheckpointClosing(int part) =>
            Marker + " this part ends here because a problem report took a snapshot of it. The recording"
                   + " was not stopped; it continues in part " + (part + 1).ToString("D3") + ".";
    }
}
