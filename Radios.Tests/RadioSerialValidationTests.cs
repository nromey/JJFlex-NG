using System;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Task #245 — the phantom row. A profile with the serial "2222" and a real
    /// radio's nickname sat in Noel's roster on 2026-08-25, took a
    /// preferred-account setting, and could never connect to anything. Its
    /// origin was never found.
    ///
    /// <para>That is the argument for these tests. The roster's data source is
    /// DIRECTORY NAMES under radios\ (RadioConfig.ListKnownRadioIds), so
    /// anything that acquires a config.xml becomes a radio. Rather than hunt
    /// every writer, the shape of a radio id is checked at the one place every
    /// write funnels through — including the first rewrite of a profile that
    /// arrived inside an imported settings zip.</para>
    ///
    /// <para>The fixtures are deliberately the real thing: the serials below are
    /// the shape of every per-radio profile on the operator's own machine.</para>
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RadioSerialValidationTests : IDisposable
    {
        // The scope owns the whole of this state: BaseDirectory, the
        // roster cache directory, the lexicon overlay override, and the
        // caches derived from all three. Hand-rolling it saved and
        // restored BaseDirectory alone, so the other two leaked into
        // whichever class ran next. See task #232.
        private readonly RadioConfigStaticsScope _scope;

        private string _dir => _scope.Directory;

        public RadioSerialValidationTests()
        {
            _scope = new RadioConfigStaticsScope(nameof(RadioSerialValidationTests));
        }

        public void Dispose() => _scope.Dispose();

        // ------------------------------------------------------------------
        // The shape
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("4925-1213-8600-6245")]  // Noel's 8600
        [InlineData("1315-4176-6300-7236")]  // a 6300
        [InlineData("0123-4567-8600-0002")]  // a delete-test fixture
        public void RealSerialsAreWellFormed(string serial)
        {
            Assert.True(RadioConfig.IsWellFormedRadioId(serial));
        }

        [Theory]
        [InlineData("2222")]                      // THE phantom
        [InlineData("1111")]
        [InlineData("1234-5678")]                 // two groups, not four
        [InlineData("0123-4567-8600")]            // three groups
        [InlineData("0123-4567-8600-0002-0003")]  // five
        [InlineData("0123-4567-860O-0002")]       // letter O for zero
        [InlineData("0123 4567 8600 0002")]       // spaces, not dashes
        [InlineData("012-4567-8600-0002")]        // short group
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("..")]                        // the Sprint 33 Track J id
        [InlineData("_unknown")]                  // what an empty serial sanitises to
        [InlineData("k5ner_Noel_Romey")]          // a real non-radio folder under radios\
        public void ImplausibleIdsAreNotWellFormed(string serial)
        {
            Assert.False(RadioConfig.IsWellFormedRadioId(serial));
        }

        [Fact]
        public void SurroundingWhitespaceIsToleratedRatherThanTreatedAsADifferentRadio()
        {
            Assert.True(RadioConfig.IsWellFormedRadioId("  4925-1213-8600-6245  "));
        }

        // ------------------------------------------------------------------
        // The gate
        // ------------------------------------------------------------------

        [Fact]
        public void SaveRefusesAPhantomSerialAndWritesNothing()
        {
            var cfg = new RadioConfig { Nickname = "6300inshack" };

            Assert.False(cfg.Save(_dir, "2222"));

            // The whole defect is that a directory becomes a roster row. No
            // directory, no row — whatever wrote it and however it got here.
            Assert.False(Directory.Exists(Path.Combine(_dir, "radios", "2222")));
            Assert.Empty(RadioConfig.ListKnownRadioIds(_dir));
        }

        [Fact]
        public void SaveForRadioRefusesAPhantomSerialWithoutTheTransientRetryStory()
        {
            // SaveForRadio's failure report says the setting "is in effect right
            // now, but will not be there the next time you start JJ Flex" — a
            // sentence about a busy disk. A rejected serial is a verdict, not a
            // transient, so it must not travel that path.
            Assert.False(new RadioConfig { Nickname = "6300inshack" }.SaveForRadio("2222"));
            Assert.Empty(RadioConfig.ListKnownRadioIds(_dir));
        }

        [Fact]
        public void AWellFormedSerialStillSaves()
        {
            const string serial = "4925-1213-8600-6245";
            Assert.True(new RadioConfig { Nickname = "the 8600" }.Save(_dir, serial));
            Assert.Equal(new[] { serial }, RadioConfig.ListKnownRadioIds(_dir));
            Assert.Equal("the 8600", RadioConfig.Load(_dir, serial).Nickname);
        }

        // ------------------------------------------------------------------
        // The model cross-check, and the two ways it deliberately does nothing
        // ------------------------------------------------------------------

        [Fact]
        public void CreatingAProfileWhoseModelContradictsItsSerialIsRefused()
        {
            var cfg = new RadioConfig { Model = "FLEX-6300" };
            Assert.False(cfg.Save(_dir, "4925-1213-8600-6245"));
            Assert.Empty(RadioConfig.ListKnownRadioIds(_dir));
        }

        [Fact]
        public void AModelMismatchNeverBreaksPersistenceForARadioWeAlreadyKnow()
        {
            // The asymmetry that matters. Refusing to CREATE stops a phantom;
            // refusing to UPDATE would silently stop a real radio's settings
            // persisting, triggered by some other bug writing a wrong Model —
            // the lying-receipt failure. Model is a mutable observation; the
            // serial is the identity.
            const string serial = "4925-1213-8600-6245";
            Assert.True(new RadioConfig { Nickname = "the 8600" }.Save(_dir, serial));

            var wrong = RadioConfig.Load(_dir, serial);
            wrong.Model = "FLEX-6300";
            wrong.UserNickname = "still mine";
            Assert.True(wrong.Save(_dir, serial));
            Assert.Equal("still mine", RadioConfig.Load(_dir, serial).UserNickname);
        }

        [Fact]
        public void NoModelMeansNoCrossCheck()
        {
            // Noel's own delete-test fixtures are well-formed and carry no
            // Model. A strict check would reject his test data.
            Assert.True(new RadioConfig().Save(_dir, "0123-4567-8600-0002"));
            Assert.True(new RadioConfig().Save(_dir, "0123-4567-8600-9001"));
        }

        [Fact]
        public void AnAuroraModelYieldsNoModelNumberSoItIsNeverCrossChecked()
        {
            // "AU-510" has three digits, not four, so we have never seen what
            // an Aurora puts in group three. Asserting a rule about a radio we
            // have never held would refuse to save that operator's settings.
            Assert.True(new RadioConfig { Model = "AU-510" }.Save(_dir, "1234-5678-0510-0001"));
            Assert.True(new RadioConfig { Model = "AU-520M" }.Save(_dir, "1234-5678-9999-0002"));
        }

        [Theory]
        [InlineData("FLEX-6300", "6300")]
        [InlineData("FLEX-6400M", "6400")]
        [InlineData("FLEX-6700R", "6700")]
        [InlineData("FLEX-8600M", "8600")]
        [InlineData("AU-510", null)]
        [InlineData("AU-520M", null)]
        [InlineData("", null)]
        [InlineData("Unknown", null)]
        [InlineData("12345", null)]  // a longer run is not a model number
        public void ModelNumberIsReadOnlyWhenItIsUnambiguous(string model, string? expected)
        {
            Assert.Equal(expected, RadioConfig.ModelNumberOf(model));
        }
    }
}
