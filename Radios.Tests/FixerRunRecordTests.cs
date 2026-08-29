using System;
using System.Collections.Generic;
using System.Linq;
using Radios.Fixer;
using Radios.Fixer.Evidence;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The persisted run record: identity snapshotted from a live run, an
    /// append-only history, honest JSON round-trips, and refusal to guess at
    /// files it cannot read.
    /// </summary>
    public class FixerRunRecordTests
    {
        [Fact]
        public void NewFor_snapshots_identity_and_stage_list()
        {
            var run = new FixerRun(FixerTestKit.Kettle(), FixerTestKit.Clock(TimeSpan.Zero));
            FixerRunRecord record = FixerRunRecord.NewFor(run);

            Assert.Equal(run.RunId, record.RunId);
            Assert.Equal("kettle", record.StageSetId);
            Assert.Equal("Kettle", record.StageSetName);
            Assert.Equal(run.StartedUtc, record.StartedUtc);
            Assert.Equal(2, record.Stages.Count);
            Assert.Equal("fill", record.Stages[0].Id);
            Assert.Equal(0, record.Stages[0].Number);
            Assert.False(record.Stages[0].Transmits);
            Assert.True(record.Stages[1].Transmits);
        }

        [Fact]
        public void Json_round_trip_preserves_everything_that_matters()
        {
            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.Results.Add(EvidenceRecords.Ran("fill", 1,
                EvidenceRecords.Setting("tap", "Tap", "open")));
            record.Results[0].Findings.Add(new RecordedFinding
            {
                Id = "dry",
                Owner = "Us",
                Critical = true,
                WhatIsWrong = "The kettle is dry.",
                WhatToDo = "Turn the tap",
                FixActionId = "turn-tap",
            });
            record.Fixes.Add(new RecordedFix
            {
                StageId = "fill",
                FindingId = "dry",
                AtUtc = EvidenceRecords.T0.AddMinutes(2),
                Sequence = 2,
                Succeeded = true,
                WhatWasWrong = "The kettle is dry.",
                WhatItBecame = "water flowing",
            });
            record.Declarations.Add(new RecordedDeclaration
            {
                Id = "power-source",
                AnswerId = "mains",
                AnswerLabel = "The mains",
                AtUtc = EvidenceRecords.T0,
            });
            record.CaptureNote = "A diagnostic recording ran alongside this run.";
            record.CaptureArchivePath = @"C:\somewhere\trace.zip";
            record.ReportText = "Test ID: AAA-222";
            record.ReportHtml = "<p>Test ID: AAA-222</p>";
            record.EndedUtc = EvidenceRecords.T0.AddMinutes(3);
            record.EndReason = "closed";

            FixerRunRecord back = FixerRunRecord.FromJson(record.ToJson());

            Assert.NotNull(back);
            Assert.Equal(record.RunId, back!.RunId);
            Assert.Equal(record.StartedUtc, back.StartedUtc);
            Assert.Equal(record.EndedUtc, back.EndedUtc);
            Assert.Equal("closed", back.EndReason);
            Assert.Equal(2, back.Stages.Count);

            RecordedStage r = Assert.Single(back.Results);
            Assert.Equal("fill", r.StageId);
            Assert.Equal("Ran", r.Status);
            RecordedSetting s = Assert.Single(r.Settings);
            Assert.Equal("tap", s.Key);
            Assert.Equal("open", s.Value);
            RecordedFinding f = Assert.Single(r.Findings);
            Assert.Equal("dry", f.Id);
            Assert.True(f.Critical);

            RecordedFix fix = Assert.Single(back.Fixes);
            Assert.Equal("water flowing", fix.WhatItBecame);
            RecordedDeclaration d = Assert.Single(back.Declarations);
            Assert.Equal("mains", d.AnswerId);
            Assert.Equal(record.CaptureArchivePath, back.CaptureArchivePath);
            Assert.Equal(record.ReportText, back.ReportText);
            Assert.Equal(record.ReportHtml, back.ReportHtml);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not json at all")]
        [InlineData("{}")]                          // no run id
        [InlineData("{\"Schema\":99,\"RunId\":\"AAA-222\"}")] // from the future
        public void FromJson_refuses_what_it_cannot_honestly_read(string? json)
        {
            Assert.Null(FixerRunRecord.FromJson(json!));
        }

        [Fact]
        public void Latest_per_stage_is_the_highest_sequence_and_history_is_kept()
        {
            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.Results.Add(EvidenceRecords.Ran("fill", 1));
            var rerun = EvidenceRecords.Ran("fill", 3);
            rerun.WasReRun = true;
            rerun.Answer = "Yes, again.";
            record.Results.Add(rerun);
            record.Results.Add(EvidenceRecords.Ran("boil", 2));

            IReadOnlyDictionary<string, RecordedStage> latest = record.LatestResultsPerStage();

            Assert.Equal("Yes, again.", latest["fill"].Answer);
            Assert.Equal(3, record.Results.Count);   // the superseded result survives
            Assert.True(record.IsComplete());
            Assert.Equal(2, record.ResolvedStageCount());
        }

        [Fact]
        public void An_unfinished_record_says_so()
        {
            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.Results.Add(EvidenceRecords.Ran("fill", 1));

            Assert.False(record.IsComplete());
            Assert.Equal(1, record.ResolvedStageCount());
            Assert.Contains("1 of 2 stages", record.Summary());
            Assert.Contains(record.RunId, record.Summary());
            Assert.Contains("not finished", record.Summary());
        }

        [Fact]
        public void A_stopped_record_reads_as_stopped_and_a_finished_one_as_finished()
        {
            FixerRunRecord stopped = EvidenceRecords.TwoStages();
            stopped.Results.Add(EvidenceRecords.Ran("fill", 1));
            stopped.EndedUtc = EvidenceRecords.T0.AddMinutes(5);
            stopped.EndReason = "abandoned";
            Assert.Contains("stopped part-way", stopped.Summary());

            FixerRunRecord finished = EvidenceRecords.TwoStages();
            finished.Results.Add(EvidenceRecords.Ran("fill", 1));
            finished.Results.Add(EvidenceRecords.Ran("boil", 2));
            finished.EndedUtc = EvidenceRecords.T0.AddMinutes(5);
            finished.EndReason = "closed";
            Assert.Contains("finished", finished.Summary());
            Assert.DoesNotContain("stopped", finished.Summary());
        }

        // -------- the payload codec --------

        [Fact]
        public void MicCheckFacts_survive_the_codec_including_NaN()
        {
            var facts = new MicCheckFacts
            {
                Measured = true,
                AudioArrived = true,
                Device = "EVO8",
                HostApi = "WASAPI",
                PeakDb = -6.5,
                NoiseFloorDb = double.NaN,
                Detail = "steady",
            };

            (string type, string json) = FixerPayloadCodec.Encode(facts);
            var back = FixerPayloadCodec.Decode(type, json) as MicCheckFacts;

            Assert.NotNull(back);
            Assert.Equal("EVO8", back!.Device);
            Assert.Equal(-6.5, back.PeakDb);
            Assert.True(double.IsNaN(back.NoiseFloorDb));
        }

        [Fact]
        public void Unknown_payloads_are_not_persisted_and_decode_to_null()
        {
            (string type, string json) = FixerPayloadCodec.Encode(new object());
            Assert.Equal("", type);
            Assert.Equal("", json);
            Assert.Null(FixerPayloadCodec.Decode("NoSuchType", "{}"));
            Assert.Null(FixerPayloadCodec.Decode("", ""));
        }

        // -------- the export shell --------

        [Fact]
        public void Standalone_html_wraps_the_recorded_fragment_without_rerendering_it()
        {
            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.ReportHtml = "<h2>What was found</h2><p>Nothing &amp; nobody.</p>";

            string html = FixerRunExport.StandaloneHtml(record);

            Assert.Contains("<!DOCTYPE html>", html);
            Assert.Contains("<h1>Kettle test report — run AAA-222</h1>", html);
            Assert.Contains(record.ReportHtml, html);   // verbatim — a shell, not a renderer
            Assert.Contains("<title>Kettle test report — run AAA-222</title>", html);
        }

        [Fact]
        public void Standalone_html_can_carry_a_lead_section_for_the_staleness_note()
        {
            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.ReportHtml = "<p>body</p>";

            string html = FixerRunExport.StandaloneHtml(record,
                "<h2>Since this run stopped</h2><p>Tune power changed.</p>");

            int lead = html.IndexOf("Since this run stopped", StringComparison.Ordinal);
            int body = html.IndexOf("<p>body</p>", StringComparison.Ordinal);
            Assert.True(lead > 0 && body > lead, "the lead section must precede the report");
        }

        [Fact]
        public void Export_file_names_carry_the_check_the_id_and_the_date()
        {
            FixerRunRecord record = EvidenceRecords.TwoStages();
            Assert.Equal("kettle-check-AAA-222-20260101-1200",
                         FixerRunExport.FileBaseName(record));
        }

        [Fact]
        public void Written_files_hold_the_two_forms()
        {
            using var dir = new TempFolder();
            FixerRunRecord record = EvidenceRecords.TwoStages();
            record.ReportText = "plain form";
            record.ReportHtml = "<p>html form</p>";

            string htmlPath = System.IO.Path.Combine(dir.Path, "r.html");
            string textPath = System.IO.Path.Combine(dir.Path, "r.txt");
            Assert.True(FixerRunExport.WriteHtml(record, htmlPath));
            Assert.True(FixerRunExport.WriteText(record, textPath));

            // Both forms carry the recorded report verbatim. They are no longer
            // ONLY the report — an exported run is the vendor-facing document
            // and wraps it in the radio's identity, the conditions and the
            // provenance (#217, and FixerRunDocumentTests owns that contract).
            Assert.Contains("<p>html form</p>", System.IO.File.ReadAllText(htmlPath));
            Assert.Contains("plain form", System.IO.File.ReadAllText(textPath));
        }

        [Fact]
        public void A_failed_write_reports_false_rather_than_throwing()
        {
            FixerRunRecord record = EvidenceRecords.TwoStages();
            using var dir = new TempFolder();
            // The destination is a directory, so the write must fail.
            Assert.False(FixerRunExport.WriteText(record, dir.Path));
        }
    }
}
