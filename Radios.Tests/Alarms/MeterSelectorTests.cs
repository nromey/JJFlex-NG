using System.Collections.Generic;
using Flex.Smoothlake.FlexLib;
using Radios.Alarms;
using Xunit;

namespace Radios.Tests.Alarms
{
    /// <summary>
    /// Identity is re-discovered per connection through every copy of the
    /// name, never through a persisted index and never through a first match.
    /// </summary>
    public sealed class MeterSelectorTests
    {
        private static MeterDescriptor D(int index, string name, string source, int sourceIndex,
            MeterUnits units = MeterUnits.DegreesC, string description = "")
            => new MeterDescriptor(index, name, description, source, sourceIndex, units, 0, 120);

        [Fact]
        public void A_selector_resolves_by_name_source_source_index_and_units_whatever_the_index_is()
        {
            // The 6300 of the 2026-09-06 trace publishes PATEMP at index 9; the bench 8600 at 11. Same identity.
            var selector = MeterSelector.From(D(9, "PATEMP", "TX-", 4, description: "PA Temperature"));
            var resolution = selector.Resolve(new[] { D(11, "PATEMP", "TX-", 4) });

            Assert.Equal(MeterSelectorStatus.Resolved, resolution.Status);
            Assert.Equal(11, resolution.Match!.Index);
        }

        [Fact]
        public void Duplicate_full_descriptors_are_ambiguous_and_nothing_is_chosen()
        {
            // The 8600's four byte-identical SC_MIC copies, all TX-:0.
            var selector = MeterSelector.From(D(24, "SC_MIC", "TX-", 0, MeterUnits.Dbfs));
            var inventory = new List<MeterDescriptor>
            {
                D(24, "SC_MIC", "TX-", 0, MeterUnits.Dbfs), D(48, "SC_MIC", "TX-", 0, MeterUnits.Dbfs),
                D(72, "SC_MIC", "TX-", 0, MeterUnits.Dbfs), D(91, "SC_MIC", "TX-", 0, MeterUnits.Dbfs),
            };
            var resolution = selector.Resolve(inventory);

            Assert.Equal(MeterSelectorStatus.Ambiguous, resolution.Status);
            Assert.Null(resolution.Match);
            Assert.Equal(4, resolution.Candidates.Count);
        }

        [Fact]
        public void A_name_published_from_a_different_source_needs_reselection()
        {
            var selector = MeterSelector.From(D(9, "PATEMP", "TX-", 4));
            var resolution = selector.Resolve(new[] { D(9, "PATEMP", "TX-", 8) });
            Assert.Equal(MeterSelectorStatus.SourceChanged, resolution.Status);
            Assert.Single(resolution.Candidates);
        }

        [Fact]
        public void A_unit_change_is_named_not_silently_accepted()
        {
            var selector = MeterSelector.From(D(9, "PATEMP", "TX-", 4, MeterUnits.DegreesC));
            var resolution = selector.Resolve(new[] { D(9, "PATEMP", "TX-", 4, MeterUnits.DegreesF) });
            Assert.Equal(MeterSelectorStatus.UnitChanged, resolution.Status);
            Assert.Null(resolution.Match);
        }

        [Fact]
        public void An_absent_meter_is_missing()
        {
            var selector = MeterSelector.From(D(208, "+13.8A", "RAD", 208, MeterUnits.Volts));
            var resolution = selector.Resolve(new[] { D(9, "PATEMP", "TX-", 4) });
            Assert.Equal(MeterSelectorStatus.Missing, resolution.Status);
        }

        [Fact]
        public void The_two_supply_meters_resolve_separately_by_source_index()
        {
            // The 6300 trace: +13.8A RAD:208 before the fuse, +13.8B RAD:210 after it.
            var a = MeterSelector.From(D(208, "+13.8A", "RAD", 208, MeterUnits.Volts));
            var b = MeterSelector.From(D(210, "+13.8B", "RAD", 210, MeterUnits.Volts));
            var inventory = new[]
            {
                D(208, "+13.8A", "RAD", 208, MeterUnits.Volts, "Main radio input voltage before fuse"),
                D(210, "+13.8B", "RAD", 210, MeterUnits.Volts, "Main radio input voltage after fuse"),
            };
            Assert.Equal(208, a.Resolve(inventory).Match!.Index);
            Assert.Equal(210, b.Resolve(inventory).Match!.Index);
        }

        [Fact]
        public void Name_matching_ignores_case_because_the_radio_does()
        {
            var selector = MeterSelector.From(D(9, "patemp", "tx-", 4));
            Assert.Equal(MeterSelectorStatus.Resolved, selector.Resolve(new[] { D(9, "PATEMP", "TX-", 4) }).Status);
        }
    }
}
