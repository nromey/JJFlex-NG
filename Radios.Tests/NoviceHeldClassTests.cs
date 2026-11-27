#nullable enable
using System;
using System.IO;
using System.Linq;
using HamBands;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1309, ruled by Noel 2026-11-26 (option C): a stored Novice is shown,
    /// kept and judged as Novice. Until then it read as Technician, and any
    /// Settings OK, even with the License tab untouched, saved Technician, after
    /// which the lockout admitted 6 m and (with #1282's table) every VHF and UHF
    /// band, which Part 97 does not give a Novice (97.301(a) and (e)).
    /// </summary>
    /// <remarks>
    /// The licence file lives in a temporary folder, never the operator's
    /// settings. Settings' OK is modelled by the seam it calls,
    /// <see cref="LicenseConfig.ClassChosenInList"/>; its call site is pinned by
    /// source in <see cref="OperatorLicenseClassTests"/>.
    /// </remarks>
    public sealed class NoviceHeldClassTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "jjf-novice-" + Guid.NewGuid().ToString("N"));
        private const string Op = "Test_Novice_Operator";

        public NoviceHeldClassTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private void WriteFile(string cls) =>
            File.WriteAllText(Path.Combine(_dir, Op + "_licenseConfig.xml"),
                "<?xml version=\"1.0\"?>\r\n<LicenseConfig><LicenseClass>" + cls
                + "</LicenseClass><TxLockout>true</TxLockout></LicenseConfig>\r\n");

        /// <summary>What Settings does from opening to OK, with the list position the operator leaves it on (null: untouched).</summary>
        private LicenseConfig OpenAndOk(int? movedTo)
        {
            var config = LicenseConfig.Load(_dir, Op);
            var shown = LicenseConfig.ShownClass(config.LicenseClass);
            int atOpen = LicenseConfig.OfferedClasses.ToList().IndexOf(shown);
            Assert.True(atOpen >= 0, shown + " is not in the offered list, so Settings could not show it");
            if (LicenseConfig.ClassChosenInList(movedTo ?? atOpen, atOpen) is { } chosen)
                config.LicenseClass = chosen;
            config.Save(_dir, Op);
            return LicenseConfig.Load(_dir, Op);
        }

        [Fact]
        public void A_licence_file_holding_novice_loads_as_Novice_and_survives_an_untouched_OK()
        {
            WriteFile("novice");
            Assert.Equal(Bands.Licenses.novice, LicenseConfig.Load(_dir, Op).LicenseClass);
            Assert.Equal(Bands.Licenses.novice, LicenseConfig.ShownClass(Bands.Licenses.novice));
            Assert.Equal(Bands.Licenses.novice, OpenAndOk(movedTo: null).LicenseClass);
        }

        [Fact]
        public void A_stored_none_survives_an_untouched_OK_too()
        {
            WriteFile("none");
            Assert.Equal(Bands.Licenses.none, OpenAndOk(movedTo: null).LicenseClass);
        }

        [Fact]
        public void Moving_the_list_writes_the_class_it_was_moved_to()
        {
            WriteFile("novice");
            int general = LicenseConfig.OfferedClasses.ToList().IndexOf(Bands.Licenses.general);
            Assert.Equal(Bands.Licenses.general, OpenAndOk(movedTo: general).LicenseClass);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(99)]
        public void No_valid_position_writes_nothing(int position) =>
            Assert.Null(LicenseConfig.ClassChosenInList(position, 0));

        [Fact]
        public void The_lockout_refuses_6_metres_for_a_Novice()
        {
            var judged = LicenseConfig.JudgedClass(Bands.Licenses.novice);
            Assert.Equal(Bands.Licenses.novice, judged);
            Assert.False(TransmitBandLockout.Allows(50_200_000, "USB", 100, 2800, judged));
            Assert.True(TransmitBandLockout.Allows(50_200_000, "USB", 100, 2800, Bands.Licenses.technition),
                "positive control: a Technician may transmit there");
        }

        [Fact]
        public void A_stored_none_is_judged_as_the_Extra_Settings_shows()
        {
            Assert.Equal(Bands.Licenses.extra, LicenseConfig.JudgedClass(Bands.Licenses.none));
            Assert.True(TransmitBandLockout.Allows(14_250_000, "USB", 100, 2800, LicenseConfig.JudgedClass(Bands.Licenses.none)));
            Assert.False(TransmitBandLockout.Allows(14_250_000, "USB", 100, 2800, Bands.Licenses.none),
                "positive control: none itself is on no division, which is why it is judged as Extra");
        }
    }
}
