using System;
using System.IO;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>Track G review, section 8, first bullet: a successful import
    /// is reported as successful even when the station after it could not be
    /// confirmed; the two facts are said separately.</summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ImportReportTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(ImportReportTests));
        public void Dispose() => _scope.Dispose();

        [Fact]
        public void ACompletedImportWithAnEstablishedStation_SaysImportComplete()
        {
            var r = ImportReport.For(true, new StationResult { Outcome = StationOutcome.RestoredConfirmed });
            Assert.Equal(Lexicon.Get("settings.flexdb.imported"), r.Message);
        }

        [Fact]
        public void ACompletedImportWithAnUnconfirmedStation_SaysImportCompleteAndStationUnconfirmed_NeverImportFailed()
        {
            var r = ImportReport.For(true, new StationResult { Outcome = StationOutcome.Unconfirmed });
            Assert.True(r.ImportCompleted);
            Assert.False(r.StationEstablished);
            Assert.Equal(Lexicon.Get("settings.flexdb.imported_station_unconfirmed"), r.Message);
            Assert.NotEqual(Lexicon.Get("settings.flexdb.import_failed"), r.Message);
            Assert.StartsWith(Lexicon.Get("settings.flexdb.imported"), r.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AnImportThatDidNotComplete_SaysSo_WhateverTheStation()
        {
            var r = ImportReport.For(false, new StationResult { Outcome = StationOutcome.RestoredConfirmed });
            Assert.Equal(Lexicon.Get("settings.flexdb.import_failed"), r.Message);
        }

        [Fact]
        public void TheProductionPostImportPathReportsThroughIt()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var text = File.ReadAllText(Path.Combine(dir.FullName, "Radios", "FlexBase.StationConnect.cs"));
            Assert.Contains("ImportReport.For(importCompleted: true, station: result)", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ok ? importedMsg : importFailMsg", text, StringComparison.Ordinal);
        }
    }
}
