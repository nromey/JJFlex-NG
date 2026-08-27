using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

// Folder note: this lives in Radios/FixerEvidence/, NOT Radios/Fixer/, on
// purpose — Sprint 35 Track A owns Radios/Fixer/* while it restructures the
// page, and the evidence layer must be mergeable without touching a file that
// is being rewritten underneath it. The namespace still nests under
// Radios.Fixer because that is what this is: the Fixer's evidence.

namespace Radios.Fixer.Evidence
{
    /// <summary>
    /// One Fixer run as it is written to disk — the durable form of the thing
    /// the Test ID has always promised (#251).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The results list is append-only.</b> The live run keeps only the
    /// latest result per stage; the record keeps every result it ever saw, in
    /// sequence order, because "this stage ran, then was re-run" is evidence
    /// and a replaced measurement that cost a transmission must not vanish
    /// from the durable copy the way it vanishes from the dictionary. The
    /// current state is derived (<see cref="LatestResultsPerStage"/>), never
    /// stored twice.
    /// </para>
    /// <para>
    /// <b>Both rendered report forms travel inside the record.</b> They are
    /// produced by <c>FixerReport</c> — the same renderer the live page uses —
    /// at the moment of the last recording, so a saved run can be read, shown
    /// and exported years later without rebuilding a live engine, and without
    /// this layer ever growing a second renderer. The structured fields are
    /// for machines: resume, comparison, and the settings-fingerprint check.
    /// </para>
    /// <para>
    /// Everything here is plain strings and numbers, serialized as indented
    /// JSON. An operator can open their own run file in Notepad and read it;
    /// that is a feature, not an accident (the project prefers auditable
    /// file-based records).
    /// </para>
    /// </remarks>
    public sealed class FixerRunRecord : IEvidenceRecord
    {
        /// <summary>Bumped when the shape changes incompatibly. Readers skip
        /// files from the future rather than misreading them.</summary>
        public int Schema { get; set; } = 1;

        public string RunId { get; set; } = "";
        public string StageSetId { get; set; } = "";
        public string StageSetName { get; set; } = "";
        public DateTime StartedUtc { get; set; }

        /// <summary>When the record was last written to. Every recording
        /// updates it, which is what "persisted as it goes" means.</summary>
        public DateTime LastRecordedUtc { get; set; }

        /// <summary>Set when the run ended in an orderly way. A record with
        /// results but no end stamp belonged to a run that crashed or is still
        /// live — which is exactly what the record exists to survive.</summary>
        public DateTime? EndedUtc { get; set; }

        /// <summary>Why it ended, in a word or two: "closed", "abandoned".
        /// Empty until <see cref="EndedUtc"/> is set.</summary>
        public string EndReason { get; set; } = "";

        /// <summary>The stage set as it stood, so the record can be read
        /// without the live set: numbers, titles, and which stages transmit.</summary>
        public List<RecordedStageInfo> Stages { get; set; } = new List<RecordedStageInfo>();

        /// <summary>Every stage result ever recorded, in sequence order.
        /// Append-only — see the class remarks.</summary>
        public List<RecordedStage> Results { get; set; } = new List<RecordedStage>();

        /// <summary>Every fix applied or attempted, in sequence order.</summary>
        public List<RecordedFix> Fixes { get; set; } = new List<RecordedFix>();

        /// <summary>The operator's per-run declarations (the transmit set's
        /// antenna-load answer is the founding case).</summary>
        public List<RecordedDeclaration> Declarations { get; set; } = new List<RecordedDeclaration>();

        /// <summary>What happened about the diagnostic capture, in words the
        /// report can carry: started for this run, already running, or not
        /// available. Never empty once a capture scope has reported in.</summary>
        public string CaptureNote { get; set; } = "";

        /// <summary>Where the capture's archive landed, when one was made for
        /// this run. This is the one string that joins a run to its trace —
        /// the correlation #252 calls half-built.</summary>
        public string CaptureArchivePath { get; set; } = "";

        /// <summary>The plain-text report as of the last recording, from
        /// <c>FixerReport.PlainText</c>.</summary>
        public string ReportText { get; set; } = "";

        /// <summary>The HTML report fragment as of the last recording, from
        /// <c>FixerReport.HtmlFragment</c> at heading level 2 — the export
        /// shell supplies the h1.</summary>
        public string ReportHtml { get; set; } = "";

        // -------- derived state --------

        /// <summary>The latest result per stage — the live run's view.</summary>
        public IReadOnlyDictionary<string, RecordedStage> LatestResultsPerStage()
        {
            var latest = new Dictionary<string, RecordedStage>(StringComparer.OrdinalIgnoreCase);
            foreach (RecordedStage r in Results.OrderBy(r => r.Sequence))
                latest[r.StageId ?? ""] = r;
            return latest;
        }

        /// <summary>How many of the set's stages have any result at all.</summary>
        public int ResolvedStageCount()
        {
            IReadOnlyDictionary<string, RecordedStage> latest = LatestResultsPerStage();
            return Stages.Count(s => latest.ContainsKey(s.Id ?? ""));
        }

        /// <summary>True when every stage in the set has a result — run,
        /// skipped or could-not-run all count; each is a recorded answer.</summary>
        public bool IsComplete()
            => Stages.Count > 0 && ResolvedStageCount() == Stages.Count;

        /// <summary>
        /// One line for a list of saved runs, leading with the Test ID because
        /// that is the thing a support thread quotes.
        /// </summary>
        public string Summary()
        {
            string when = StartedUtc == default
                ? "start time unknown"
                : "started " + StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

            string progress = Stages.Count == 0
                ? "no stages recorded"
                : ResolvedStageCount() + " of " + Stages.Count + " stages have results";

            string state = EndedUtc == null
                ? (IsComplete() ? "" : ", not finished")
                : IsComplete() ? ", finished" : ", stopped part-way";

            return RunId + " — " + StageSetName + " checks, " + when + ", "
                 + progress + state + ".";
        }

        // -------- construction from a live run --------

        /// <summary>A fresh record for a run that has just begun. Results
        /// arrive through the journal, one at a time, as they happen.</summary>
        public static FixerRunRecord NewFor(FixerRun run)
        {
            if (run == null) throw new ArgumentNullException(nameof(run));

            var record = new FixerRunRecord
            {
                RunId = run.RunId,
                StageSetId = run.Set.Id,
                StageSetName = run.Set.Name,
                StartedUtc = run.StartedUtc,
                LastRecordedUtc = run.StartedUtc,
            };
            foreach (FixerStage s in run.Set.Stages)
            {
                record.Stages.Add(new RecordedStageInfo
                {
                    Id = s.Id,
                    Number = s.Number,
                    Title = s.Title,
                    Transmits = s.Transmits,
                });
            }
            return record;
        }

        // -------- serialization --------

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            // MicCheckFacts carries NaN for "not measured"; named literals keep
            // that honest instead of throwing or silently zeroing it.
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

        /// <summary>Null when the text is not a readable record — including a
        /// record from a future schema, which must be skipped, never guessed
        /// at.</summary>
        public static FixerRunRecord FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                FixerRunRecord r = JsonSerializer.Deserialize<FixerRunRecord>(json, JsonOptions);
                if (r == null || r.Schema > 1) return null;
                if (string.IsNullOrWhiteSpace(r.RunId)) return null;
                return r;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>A stage as the set described it when the run started.</summary>
    public sealed class RecordedStageInfo
    {
        public string Id { get; set; } = "";
        public int Number { get; set; }
        public string Title { get; set; } = "";
        public bool Transmits { get; set; }
    }

    /// <summary>One recorded stage result, plus the settings fingerprint taken
    /// the moment it was recorded (#252 part 1).</summary>
    public sealed class RecordedStage
    {
        public string StageId { get; set; } = "";
        public DateTime AtUtc { get; set; }
        public int Sequence { get; set; }
        public bool WasReRun { get; set; }

        /// <summary>The <c>FixerStageStatus</c> name: Ran, Skipped, CouldNotRun.</summary>
        public string Status { get; set; } = "";

        public string SkipChoiceId { get; set; } = "";
        public string SkipLabel { get; set; } = "";
        public string SkipEffect { get; set; } = "";
        public string SkipEffectText { get; set; } = "";

        public string Answer { get; set; } = "";
        public string Evidence { get; set; } = "";
        public List<RecordedFinding> Findings { get; set; } = new List<RecordedFinding>();

        /// <summary>The declared settings this stage depended on, at the values
        /// they held when it ran. A declared dependency list, deliberately NOT
        /// a snapshot of everything — see <c>FixerSettingProbeSet</c>.</summary>
        public List<RecordedSetting> Settings { get; set; } = new List<RecordedSetting>();

        /// <summary>The payload's type name when it was one the codec knows,
        /// else empty. See <see cref="FixerPayloadCodec"/>.</summary>
        public string PayloadType { get; set; } = "";
        public string PayloadJson { get; set; } = "";

        public static RecordedStage From(FixerStageResult result,
                                         IReadOnlyList<RecordedSetting> settings)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            var r = new RecordedStage
            {
                StageId = result.StageId,
                AtUtc = result.AtUtc,
                Sequence = result.Sequence,
                WasReRun = result.WasReRun,
                Status = result.Status.ToString(),
                Answer = result.Answer,
                Evidence = result.Evidence,
            };
            if (result.Skip != null)
            {
                r.SkipChoiceId = result.Skip.Id;
                r.SkipLabel = result.Skip.Label;
                r.SkipEffect = result.Skip.Effect.ToString();
                r.SkipEffectText = result.Skip.EffectText;
            }
            foreach (FixerFinding f in result.Findings)
                r.Findings.Add(RecordedFinding.From(f));
            if (settings != null)
                r.Settings.AddRange(settings);

            (r.PayloadType, r.PayloadJson) = FixerPayloadCodec.Encode(result.Payload);
            return r;
        }
    }

    public sealed class RecordedFinding
    {
        public string Id { get; set; } = "";
        public string Owner { get; set; } = "";
        public bool Critical { get; set; }
        public string WhatIsWrong { get; set; } = "";
        public string WhatToDo { get; set; } = "";
        public string FixActionId { get; set; } = "";

        public static RecordedFinding From(FixerFinding f) => new RecordedFinding
        {
            Id = f.Id,
            Owner = f.Owner.ToString(),
            Critical = f.Critical,
            WhatIsWrong = f.WhatIsWrong,
            WhatToDo = f.WhatToDo,
            FixActionId = f.FixActionId ?? "",
        };
    }

    public sealed class RecordedFix
    {
        public string StageId { get; set; } = "";
        public string FindingId { get; set; } = "";
        public DateTime AtUtc { get; set; }
        public int Sequence { get; set; }
        public bool Succeeded { get; set; }
        public string WhatWasWrong { get; set; } = "";
        public string WhatItBecame { get; set; } = "";

        public static RecordedFix From(FixerFixRecord fix) => new RecordedFix
        {
            StageId = fix.StageId,
            FindingId = fix.FindingId,
            AtUtc = fix.AtUtc,
            Sequence = fix.Sequence,
            Succeeded = fix.Succeeded,
            WhatWasWrong = fix.WhatWasWrong,
            WhatItBecame = fix.WhatItBecame,
        };
    }

    /// <summary>One per-run declaration the operator answered.</summary>
    public sealed class RecordedDeclaration
    {
        public string Id { get; set; } = "";
        public string AnswerId { get; set; } = "";
        public string AnswerLabel { get; set; } = "";
        public DateTime AtUtc { get; set; }
    }

    /// <summary>One setting a stage declared it depended on, at the value it
    /// held when the stage ran. Value is display text — the same words the
    /// operator would read — and empty means it could not be read.</summary>
    public sealed class RecordedSetting
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
    }

    /// <summary>
    /// Persists the few stage payload types worth carrying across a resume.
    /// </summary>
    /// <remarks>
    /// Payloads are engine-opaque (<c>FixerOutcome.Payload</c> is object), but
    /// losing them all on resume would silently weaken stage 4, which reads
    /// stage 1's <c>MicCheckFacts</c> as its baseline. A whitelist, not
    /// reflection over arbitrary types: a payload the codec does not know is
    /// simply not persisted, and the stage that reads it must already handle
    /// null (a skipped stage 1 produces the same null in a live run).
    /// </remarks>
    public static class FixerPayloadCodec
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public static (string TypeName, string Json) Encode(object payload)
        {
            try
            {
                if (payload is MicCheckFacts mic)
                    return (nameof(MicCheckFacts), JsonSerializer.Serialize(mic, Options));
            }
            catch (Exception)
            {
                // An unserializable payload costs the resume baseline, never
                // the recording.
            }
            return ("", "");
        }

        public static object Decode(string typeName, string json)
        {
            if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(json)) return null;
            try
            {
                return typeName switch
                {
                    nameof(MicCheckFacts) => JsonSerializer.Deserialize<MicCheckFacts>(json, Options),
                    _ => null,
                };
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
