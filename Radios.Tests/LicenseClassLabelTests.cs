#nullable enable
using System;
using System.IO;
using System.Linq;
using HamBands;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The operator form and Settings offer the same four licence classes, in
    /// Settings' order and with Settings' words (#1239). Apart from
    /// <see cref="OperatorLicenseClassTests"/> because it reads the Lexicon,
    /// which resolves the settings root, so it belongs in the settings-statics
    /// collection; that class belongs in the built-app one.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class LicenseClassLabelTests
    {
        public LicenseClassLabelTests() => Lexicon.Load(Lexicon.Partitions);

        [Fact]
        public void The_four_offered_classes_are_Settings_four_in_Settings_order_with_its_words()
        {
            Assert.Equal(new[] { Bands.Licenses.extra, Bands.Licenses.advanced, Bands.Licenses.general, Bands.Licenses.technition },
                LicenseConfig.OfferedClasses.ToArray());
            Assert.Equal(Lexicon.Get("settings.license.class_extra"), LicenseConfig.ClassLabel(Bands.Licenses.extra));
            Assert.Equal(Lexicon.Get("settings.license.class_advanced"), LicenseConfig.ClassLabel(Bands.Licenses.advanced));
            Assert.Equal(Lexicon.Get("settings.license.class_general"), LicenseConfig.ClassLabel(Bands.Licenses.general));
            Assert.Equal(Lexicon.Get("settings.license.class_technician"), LicenseConfig.ClassLabel(Bands.Licenses.technition));

            // Settings' own map, read from its source, holds the same four in the same order.
            string settings = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs"));
            int map = settings.IndexOf("LicenseClassMap =", StringComparison.Ordinal);
            Assert.True(map > 0);
            string body = settings.Substring(map, settings.IndexOf("};", map, StringComparison.Ordinal) - map);
            string[] order = { "Licenses.extra", "Licenses.advanced", "Licenses.general", "Licenses.technition" };
            int[] at = order.Select(o => body.IndexOf(o, StringComparison.Ordinal)).ToArray();
            Assert.True(at.All(i => i > 0) && at.SequenceEqual(at.OrderBy(i => i)), "Settings' map is not Extra, Advanced, General, Technician");
        }

    }
}
