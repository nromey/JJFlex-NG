using System;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 35 Track I, task #227 (the pre-reset text export) and the
    /// offline edges of #225 (the widened provisional-change receipt).
    ///
    /// <para>No radio is available under test, and that is the point of what
    /// CAN be pinned here: the export must still produce every section — a
    /// section that vanishes when unreadable is the exact defect its gaps
    /// section exists to prevent — and the receipt machinery must treat "not
    /// connected" as "nothing happened", because a setter touched before or
    /// after a connection must never arm the disconnect save offer.</para>
    /// </summary>
    public sealed class StationSettingsExportTests
    {
        private static FlexBase MakeRig()
            => new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

        // ------------------------------------------------------------------
        // #227 — the export with nothing to read
        // ------------------------------------------------------------------

        [Fact]
        public void Export_WithNoRadio_StillCarriesEverySection()
        {
            var rig = MakeRig();
            string text = ProfileReporter.GenerateStationSettingsExport(rig);

            // Every heading, in reading order. A screen reader user moves
            // through this file top to bottom; a missing section is silent
            // data loss discovered after the reset.
            foreach (var heading in new[]
            {
                "The radio",
                "Profiles stored on the radio",
                "Slices",
                "Transmit settings",
                "CW settings",
                "Memories",
                "Antennas and tuner",
                "What this file cannot carry",
            })
            {
                Assert.Contains(heading + Environment.NewLine, text);
            }
            Assert.Contains("End of export.", text);
        }

        [Fact]
        public void Export_NamesWhatItCannotCapture()
        {
            var rig = MakeRig();
            string text = ProfileReporter.GenerateStationSettingsExport(rig);

            // The gaps section is load-bearing: DVK audio and unloaded
            // profile contents are the two things an operator would most
            // painfully discover missing after a reset.
            Assert.Contains("DVK voice recordings", text);
            Assert.Contains("contents of profiles other than the ones loaded", text);
        }

        [Fact]
        public void Export_ContainsNoTabsAndNoTableRuling()
        {
            // The export is read linearly by a screen reader. Column layouts
            // and box ruling are the anti-pattern the project's accessibility
            // rules name outright; labelled lines only.
            var rig = MakeRig();
            string text = ProfileReporter.GenerateStationSettingsExport(rig);
            Assert.DoesNotContain("\t", text);
            Assert.DoesNotContain("|", text);
        }

        // ------------------------------------------------------------------
        // #225 — the receipt's offline edge
        // ------------------------------------------------------------------

        [Fact]
        public void SettingChangeBeforeAnyConnection_DoesNotArmTheSaveOffer()
        {
            var rig = MakeRig();
            Assert.False(rig.OperatorChangedStationThisSession);

            // Offline, the setter's radio write is dropped by the queue and
            // the receipt note must treat it as nothing: there is no radio
            // whose profile the change could fail to survive in.
            rig.XmitPower = 50;
            rig.TunePower = 10;

            Assert.False(rig.OperatorChangedStationThisSession);
        }

        [Fact]
        public void AppInitiatedScope_NestsAndDisposesCleanly()
        {
            var rig = MakeRig();
            var outer = rig.AppInitiatedSettingChanges();
            var inner = rig.AppInitiatedSettingChanges();
            inner.Dispose();
            // Double-dispose must be harmless: the scope may sit in a using
            // block AND be disposed early by a restore path.
            inner.Dispose();
            outer.Dispose();

            // No observable state to assert beyond "nothing threw" — the
            // depth counter is private on purpose. This test exists so a
            // future refactor that makes disposal throw or double-count
            // fails here rather than in a dialog.
        }
    }
}
