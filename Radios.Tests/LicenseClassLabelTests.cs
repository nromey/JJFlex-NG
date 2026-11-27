#nullable enable
using System;
using System.IO;
using System.Linq;
using HamBands;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The operator form and Settings offer the same five licence classes
    /// (#1309, ruled 2026-11-26; four until then, #1239), from one list
    /// with one set of words. Apart from
    /// <see cref="OperatorLicenseClassTests"/> because it reads the Lexicon,
    /// which resolves the settings root, so it belongs in the settings-statics
    /// collection; that class belongs in the built-app one.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class LicenseClassLabelTests
    {
        public LicenseClassLabelTests() => Lexicon.Load(Lexicon.Partitions);

        [Fact]
        public void The_five_offered_classes_are_the_issued_three_then_the_held_two_with_their_words()
        {
            // The order is the orchestrator's suggestion (batch 31), not a ruling.
            Assert.Equal(new[] { Bands.Licenses.extra, Bands.Licenses.general, Bands.Licenses.technition,
                                 Bands.Licenses.advanced, Bands.Licenses.novice },
                LicenseConfig.OfferedClasses.ToArray());
            Assert.Equal(Lexicon.Get("settings.license.class_extra"), LicenseConfig.ClassLabel(Bands.Licenses.extra));
            Assert.Equal(Lexicon.Get("settings.license.class_general"), LicenseConfig.ClassLabel(Bands.Licenses.general));
            Assert.Equal(Lexicon.Get("settings.license.class_technician"), LicenseConfig.ClassLabel(Bands.Licenses.technition));
            Assert.Equal(Lexicon.Get("settings.license.class_advanced_held"), LicenseConfig.ClassLabel(Bands.Licenses.advanced));
            Assert.Equal(Lexicon.Get("settings.license.class_novice_held"), LicenseConfig.ClassLabel(Bands.Licenses.novice));
            Assert.Equal(5, LicenseConfig.OfferedClasses.Select(LicenseConfig.ClassLabel).Distinct().Count());

            // Settings keeps no list of its own: it fills its combo from this one.
            string settings = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs"));
            Assert.DoesNotContain("LicenseClassMap", settings, StringComparison.Ordinal);
            Assert.Contains("LicenseClassCombo.Items.Add(LicenseConfig.ClassLabel(cls));", settings, StringComparison.Ordinal);
        }

    }
}
