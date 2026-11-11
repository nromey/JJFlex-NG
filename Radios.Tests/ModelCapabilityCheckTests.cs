using System;
using System.IO;
using Flex.Smoothlake.FlexLib;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #429: a trace line when what FlexLib holds about a radio disagrees with
    /// FlexLib's own model table. Trace only; never a switch to the table.
    /// </summary>
    public sealed class ModelCapabilityCheckTests
    {
        [Fact]
        public void A_radio_that_matches_its_row_reports_no_disagreement()
        {
            Assert.Empty(ModelCapabilityCheck.Disagreements("FLEX-8600", 4, true, out string standDown));
            Assert.Null(standDown);
            Assert.Contains("agrees", ModelCapabilityCheck.TraceLine("FLEX-6300", 2, false), StringComparison.Ordinal);
        }

        [Fact]
        public void A_slice_or_diversity_disagreement_is_named()
        {
            var both = ModelCapabilityCheck.Disagreements("FLEX-8600", 2, false, out _);
            Assert.Equal(2, both.Count);
            Assert.Contains("slices", both[0], StringComparison.Ordinal);
            Assert.Contains("diversity", both[1], StringComparison.Ordinal);
            string line = ModelCapabilityCheck.TraceLine("FLEX-6300", 4, true);
            Assert.Contains("DISAGREES", line, StringComparison.Ordinal);
            Assert.Contains("The radio's own answer is the one in use.", line, StringComparison.Ordinal);
        }

        [Fact]
        public void Zero_slices_is_not_reported_and_never_a_disagreement()
        {
            // FlexLib sets MaxSlices only from local discovery, so a SmartLink
            // radio always reads 0.
            Assert.Empty(ModelCapabilityCheck.Disagreements("FLEX-6300", 0, false, out _));
            string line = ModelCapabilityCheck.TraceLine("FLEX-6300", 0, false);
            Assert.DoesNotContain("DISAGREES", line, StringComparison.Ordinal);
            Assert.Contains("not reported", line, StringComparison.Ordinal);
        }

        [Fact]
        public void An_unknown_model_compares_nothing_rather_than_against_the_default_row()
        {
            // Positive control: the trap is real. FlexLib hands back its
            // DEFAULT row (two slices, no diversity) for a name it lacks.
            Assert.Same(ModelInfo.GetModelInfoForModel("DEFAULT"), ModelInfo.GetModelInfoForModel("FLEX-9999"));

            Assert.Empty(ModelCapabilityCheck.Disagreements("FLEX-9999", 8, true, out string standDown));
            Assert.Contains("no row", standDown, StringComparison.Ordinal);
            Assert.Empty(ModelCapabilityCheck.Disagreements("", 4, true, out standDown));
            Assert.NotNull(standDown);
            Assert.StartsWith("ModelCapabilityCheck: nothing compared", ModelCapabilityCheck.TraceLine(null, 4, true), StringComparison.Ordinal);
        }

        [Fact]
        public void Our_slice_table_agrees_with_FlexLibs_for_every_model_it_names()
        {
            // Cheap, and it catches drift in either table.
            Assert.Equal(16, FlexBase.ModelsWithKnownSliceCounts.Length);
            foreach (string model in FlexBase.ModelsWithKnownSliceCounts)
            {
                ModelInfo row = ModelInfo.GetModelInfoForModel(model);
                Assert.False(ReferenceEquals(row, ModelInfo.GetModelInfoForModel("DEFAULT")), model + " has no row in ModelInfo");
                Assert.True(row.SliceList.Count == FlexBase.TotalMaxSlicesFor(model, 0),
                    model + ": TotalMaxSlices says " + FlexBase.TotalMaxSlicesFor(model, 0)
                    + ", FlexLib's ModelInfo lists " + row.SliceList.Count);
            }
            Assert.Equal(6, FlexBase.TotalMaxSlicesFor("ML-9600", 6));
            Assert.Equal(2, FlexBase.TotalMaxSlicesFor("ML-9600", 0));
        }

        [Fact]
        public void It_runs_once_per_connect_at_the_end_of_station_establishment_and_nowhere_else()
        {
            string src = FlexBasePartials.JoinedText();
            int calls = 0;
            for (int i = src.IndexOf("ModelCapabilityCheck.", StringComparison.Ordinal); i >= 0;
                 i = src.IndexOf("ModelCapabilityCheck.", i + 1, StringComparison.Ordinal)) calls++;
            Assert.Equal(1, calls);
            Assert.Contains("if (!postImport) ModelCapabilityCheck.TraceForConnect(theRadio);", src, StringComparison.Ordinal);

            string establish = src.Substring(src.IndexOf("internal StationResult EstablishStationOnConnect(bool postImport)", StringComparison.Ordinal));
            int run = establish.IndexOf("result = coordinator.Run();", StringComparison.Ordinal);
            int check = establish.IndexOf("ModelCapabilityCheck.TraceForConnect", StringComparison.Ordinal);
            Assert.True(run > 0 && check > run, "the check must run after the station is established");

            string checker = File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), "Radios", "ModelCapabilityCheck.cs"));
            Assert.DoesNotContain("Speak", checker, StringComparison.Ordinal);
        }
    }
}
