using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The Settings radio picker's data: which radios appear, what each row is
    /// called, what order they come in, and what the arrow-key announcement
    /// leads with (#391's Settings half, 2026-08-30).
    ///
    /// <para><b>Why the presence tests exist.</b> The operator reported a radio
    /// MISSING from this combo on the day he needed it to arm a safety hold
    /// before a factory reset. The radio was in the list the whole time — the
    /// selection-change announcement led with "Profile:" and interrupted the
    /// screen reader's own reading of the item, so arrowing through four
    /// radios was heard as "profile: automatic" three times and no utterance
    /// anywhere named the radio. Two lessons, both pinned here: a radio with a
    /// config.xml on disk appears in the list (the assertion nobody had), and
    /// the spoken sentence leads with WHICH RADIO before anything else.</para>
    ///
    /// <para><b>Why the label tests exist.</b> Every unnamed row was a bare
    /// sixteen-digit serial, and a blind operator was left disambiguating four
    /// of them by ear. The ladder — name, else model, else bare serial as a
    /// last resort — is behaviour, so it is pinned as behaviour.</para>
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RadioProfilePickerModelTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(RadioProfilePickerModelTests));
        private string _dir => _scope.Directory;

        public void Dispose() => _scope.Dispose();

        // The fixture mirrors the operator's real store on 2026-08-30: three
        // named radios with sightings, one never-seen unnamed test fixture
        // that is also hidden from the connect roster, a directory with no
        // config.xml, an operator folder that has no business in the radio
        // store but is really there, and a stray empty nested "radios".
        private const string Own8600 = "4925-1213-8600-6245";
        private const string Testers6300 = "1315-4176-6300-7236";
        private const string Borrowed6300 = "2116-5319-6300-6334";
        private const string Fixture8600 = "0123-4567-8600-0002";

        private void WriteTheOperatorsStore()
        {
            Assert.True(new RadioConfig
            {
                Nickname = "K5NER",
                Model = "FLEX-8600",
                LastSeenUtc = new DateTime(2026, 8, 30, 22, 0, 0, DateTimeKind.Utc),
            }.Save(_dir, Own8600));

            Assert.True(new RadioConfig
            {
                Nickname = "6300inshack",
                Model = "FLEX-6300",
                LastSeenUtc = new DateTime(2026, 8, 29, 23, 0, 0, DateTimeKind.Utc),
            }.Save(_dir, Testers6300));

            Assert.True(new RadioConfig
            {
                Nickname = "MargaretGaffney",
                Model = "FLEX-6300",
                LastSeenUtc = new DateTime(2026, 8, 18, 23, 0, 0, DateTimeKind.Utc),
            }.Save(_dir, Borrowed6300));

            Assert.True(new RadioConfig
            {
                HiddenFromList = true,
            }.Save(_dir, Fixture8600));

            // A radio directory holding only connect history — no config.xml,
            // so not a profile, so not a row.
            Directory.CreateDirectory(Path.Combine(_dir, "radios", "0123-4567-8600-9001"));
            File.WriteAllText(
                Path.Combine(_dir, "radios", "0123-4567-8600-9001", "connect-history.json"), "{}");

            // Really on the operator's disk: an operator folder inside the
            // radio store, and an empty nested radios directory. Neither has
            // a config.xml; neither may become a row.
            Directory.CreateDirectory(Path.Combine(_dir, "radios", "k5ner_Noel_Romey"));
            File.WriteAllText(
                Path.Combine(_dir, "radios", "k5ner_Noel_Romey", "PanRanges.xml"), "<x/>");
            Directory.CreateDirectory(Path.Combine(_dir, "radios", "radios"));
        }

        // ------------------------------------------------------------------
        // Presence — the assertion nobody had
        // ------------------------------------------------------------------

        [Fact]
        public void EveryProfileOnDiskAppearsExactlyOnce()
        {
            WriteTheOperatorsStore();

            var entries = RadioProfilePickerModel.Build(_dir, connectedSerial: null);

            Assert.Equal(4, entries.Count);
            foreach (var id in new[] { Own8600, Testers6300, Borrowed6300, Fixture8600 })
            {
                Assert.Single(entries, e => e.Id == id);
            }
        }

        [Fact]
        public void FoldersWithoutAProfileAreNotRows()
        {
            WriteTheOperatorsStore();

            var entries = RadioProfilePickerModel.Build(_dir, connectedSerial: null);

            Assert.DoesNotContain(entries, e => e.Id == "0123-4567-8600-9001");
            Assert.DoesNotContain(entries, e => e.Id == "k5ner_Noel_Romey");
            Assert.DoesNotContain(entries, e => e.Id == "radios");
        }

        /// <summary>
        /// Hidden-from-list (task #98) takes a radio off the CONNECT roster
        /// while keeping its settings. This tab is where those surviving
        /// settings live, so the hidden radio stays listed here — hiding it
        /// would leave its settings with no door anywhere.
        /// </summary>
        [Fact]
        public void AProfileHiddenFromTheConnectRosterStillAppearsHere()
        {
            WriteTheOperatorsStore();

            var entries = RadioProfilePickerModel.Build(_dir, connectedSerial: null);

            Assert.Contains(entries, e => e.Id == Fixture8600);

            // And the roster's own contract holds both ways: the connect
            // roster still filters it by default, and only an explicit
            // includeHidden brings it back.
            Assert.DoesNotContain(KnownRadioRoster.Load(), r => r.Serial == Fixture8600);
            Assert.Contains(KnownRadioRoster.Load(includeHidden: true),
                r => r.Serial == Fixture8600);
        }

        // ------------------------------------------------------------------
        // The naming ladder
        // ------------------------------------------------------------------

        [Fact]
        public void ABareSerialIsNeverTheLabelWhenANameIsKnown()
        {
            WriteTheOperatorsStore();

            var entries = RadioProfilePickerModel.Build(_dir, connectedSerial: null);

            var named = new[]
            {
                (Id: Own8600, Name: "K5NER"),
                (Id: Testers6300, Name: "6300inshack"),
                (Id: Borrowed6300, Name: "MargaretGaffney"),
            };
            foreach (var (id, name) in named)
            {
                var row = entries.Single(e => e.Id == id);
                Assert.NotEqual(id, row.Label);
                Assert.StartsWith(name, row.Label, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void ANamedRadioLeadsWithItsNameAndCarriesModelAndSerial()
        {
            WriteTheOperatorsStore();

            var row = RadioProfilePickerModel.Build(_dir, connectedSerial: null)
                .Single(e => e.Id == Testers6300);

            Assert.StartsWith("6300inshack", row.Label, StringComparison.Ordinal);
            Assert.Contains("FLEX-6300", row.Label, StringComparison.Ordinal);
            Assert.Contains(Testers6300, row.Label, StringComparison.Ordinal);
        }

        /// <summary>
        /// The fixture profile stores no name and no model, but its serial's
        /// own third group says 8600 — so the row reads as a radio, not as
        /// sixteen digits. This is the rung that turned the operator's four
        /// spoken serials into at most one.
        /// </summary>
        [Fact]
        public void AnUnnamedRadioLeadsWithTheModelItsSerialCarries()
        {
            WriteTheOperatorsStore();

            var row = RadioProfilePickerModel.Build(_dir, connectedSerial: null)
                .Single(e => e.Id == Fixture8600);

            Assert.StartsWith("FLEX-8600", row.Label, StringComparison.Ordinal);
            Assert.Contains(Fixture8600, row.Label, StringComparison.Ordinal);
        }

        /// <summary>
        /// The last resort really is last: an unnamed radio whose serial group
        /// we have never seen (an Aurora's, say) gets its bare serial, not a
        /// fabricated model. Refusing to guess is the point — a wrong model on
        /// a row would be worse than digits.
        /// </summary>
        [Fact]
        public void AnUnknownModelGroupRefusesToGuessAndFallsBackToTheSerial()
        {
            Assert.True(new RadioConfig().Save(_dir, "1234-5678-9999-0001"));

            var row = RadioProfilePickerModel.Build(_dir, connectedSerial: null)
                .Single(e => e.Id == "1234-5678-9999-0001");

            Assert.Equal("1234-5678-9999-0001", row.Label);
        }

        [Fact]
        public void ModelFromSerialKnowsTheFleetAndRefusesTheRest()
        {
            Assert.Equal("FLEX-6300", RadioProfilePickerModel.ModelFromSerial(Testers6300));
            Assert.Equal("FLEX-8600", RadioProfilePickerModel.ModelFromSerial(Own8600));
            Assert.Equal("", RadioProfilePickerModel.ModelFromSerial("1234-5678-9999-0001"));
            Assert.Equal("", RadioProfilePickerModel.ModelFromSerial("not-a-serial"));
            Assert.Equal("", RadioProfilePickerModel.ModelFromSerial(null));
        }

        /// <summary>
        /// A profile written before the roster fields shipped has no nickname
        /// of its own, but the connection cache remembers one — the row uses
        /// it rather than falling to the serial.
        /// </summary>
        [Fact]
        public void TheConnectionCacheFillsANameTheProfileNeverLearned()
        {
            Assert.True(new RadioConfig().Save(_dir, "2222-2222-6300-0002"));
            File.WriteAllText(Path.Combine(_dir, "radioConnectionCacheV1.xml"),
                "<?xml version=\"1.0\"?>\n" +
                "<RadioConnectionCache xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <Entries>\n" +
                "    <RadioConnectionCacheEntry>\n" +
                "      <Serial>2222-2222-6300-0002</Serial><Nickname>old rig</Nickname><Model>FLEX-6300</Model>\n" +
                "    </RadioConnectionCacheEntry>\n" +
                "  </Entries>\n" +
                "</RadioConnectionCache>\n");

            var row = RadioProfilePickerModel.Build(_dir, connectedSerial: null)
                .Single(e => e.Id == "2222-2222-6300-0002");

            Assert.StartsWith("old rig", row.Label, StringComparison.Ordinal);
        }

        /// <summary>
        /// The cache knowing a radio does not put it in this list — only a
        /// saved profile (or the live connection) does. Widening what this
        /// picker means is a decision, not a side effect; the editable combo
        /// accepts a typed serial for the radio nobody has configured yet.
        /// </summary>
        [Fact]
        public void ACacheOnlyRadioDoesNotWidenTheList()
        {
            Assert.True(new RadioConfig { Nickname = "real" }.Save(_dir, "1111-1111-6300-0001"));
            File.WriteAllText(Path.Combine(_dir, "radioConnectionCacheV1.xml"),
                "<?xml version=\"1.0\"?>\n" +
                "<RadioConnectionCache xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <Entries>\n" +
                "    <RadioConnectionCacheEntry>\n" +
                "      <Serial>3333-3333-6500-0003</Serial><Nickname>orphan</Nickname><Model>FLEX-6500</Model>\n" +
                "    </RadioConnectionCacheEntry>\n" +
                "  </Entries>\n" +
                "</RadioConnectionCache>\n");

            var entries = RadioProfilePickerModel.Build(_dir, connectedSerial: null);

            Assert.Single(entries);
            Assert.Equal("1111-1111-6300-0001", entries[0].Id);
        }

        // ------------------------------------------------------------------
        // Order — the one a person looks in
        // ------------------------------------------------------------------

        [Fact]
        public void MostRecentlySeenComesFirstAndNeverSeenComesLast()
        {
            WriteTheOperatorsStore();

            var ids = RadioProfilePickerModel.Build(_dir, connectedSerial: null)
                .Select(e => e.Id).ToList();

            Assert.Equal(new[] { Own8600, Testers6300, Borrowed6300, Fixture8600 }, ids);
        }

        [Fact]
        public void AFavoriteOutranksRecency()
        {
            WriteTheOperatorsStore();
            var cfg = RadioConfig.Load(_dir, Borrowed6300);
            cfg.IsFavorite = true;
            Assert.True(cfg.Save(_dir, Borrowed6300));

            var ids = RadioProfilePickerModel.Build(_dir, connectedSerial: null)
                .Select(e => e.Id).ToList();

            Assert.Equal(Borrowed6300, ids[0]);
        }

        [Fact]
        public void TheConnectedRadioComesFirstSaysSoAndIsNotDuplicated()
        {
            WriteTheOperatorsStore();

            var entries = RadioProfilePickerModel.Build(_dir, connectedSerial: Borrowed6300);

            Assert.Equal(4, entries.Count);
            Assert.Equal(Borrowed6300, entries[0].Id);
            Assert.True(entries[0].IsConnected);
            Assert.Contains("connected", entries[0].Label, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void AConnectedRadioWithNoProfileYetIsListedByItsLiveNameNotItsSerial()
        {
            WriteTheOperatorsStore();

            var entries = RadioProfilePickerModel.Build(_dir,
                connectedSerial: "5555-6666-6600-0001",
                connectedNickname: "BenchMule",
                connectedModel: "FLEX-6600");

            Assert.Equal(5, entries.Count);
            Assert.Equal("5555-6666-6600-0001", entries[0].Id);
            Assert.True(entries[0].IsConnected);
            Assert.StartsWith("BenchMule", entries[0].Label, StringComparison.Ordinal);
            Assert.Contains("FLEX-6600", entries[0].Label, StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------
        // What the arrow-key announcement leads with
        // ------------------------------------------------------------------

        [Fact]
        public void TheSpokenNameLeadsWithIdentityAndDoesNotReciteTheFullSerial()
        {
            WriteTheOperatorsStore();

            var named = RadioConfig.Load(_dir, Testers6300);
            string spokenNamed = RadioProfilePickerModel.SpokenNameFor(named);
            Assert.StartsWith("6300inshack", spokenNamed, StringComparison.Ordinal);
            Assert.DoesNotContain(Testers6300, spokenNamed, StringComparison.Ordinal);

            var unnamed = RadioConfig.Load(_dir, Fixture8600);
            string spokenUnnamed = RadioProfilePickerModel.SpokenNameFor(unnamed);
            Assert.StartsWith("FLEX-8600", spokenUnnamed, StringComparison.Ordinal);
            Assert.Contains("0002", spokenUnnamed, StringComparison.Ordinal);
            Assert.DoesNotContain(Fixture8600, spokenUnnamed, StringComparison.Ordinal);
        }

        /// <summary>
        /// The spoken profile description opens with the radio's identity.
        /// The template is the contract: the dialog speaks it with interrupt
        /// on every selection change, which flushes the screen reader's own
        /// announcement of the item — so if these words do not name the radio,
        /// nothing does, and a radio becomes "missing" while sitting in the
        /// list. That is not hypothetical; it is the 2026-08-30 report.
        /// </summary>
        [Fact]
        public void TheProfileDescriptionTemplateLeadsWithWhichRadio()
        {
            Assert.StartsWith("{who}", Lexicon.Get("settings.profile.describe"),
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Source pins for the dialog half, which cannot be constructed in a test
    /// (it is a real WPF window and the desk guard is right to refuse it).
    /// Same shape as <see cref="CountdownKeyUpRuleTests"/>: assert the wiring
    /// in the one file that owns it, with a positive control so a scan that
    /// reads the wrong file cannot pass for the wrong reason.
    /// </summary>
    public sealed class RadioProfilePickerWiringTests
    {
        private const string Dialog = "JJFlexWpf/Dialogs/SettingsDialog.RadioProfile.cs";

        /// <summary>
        /// The picker paints what the model builds — one ladder, one order,
        /// pinned by the behavioural tests above. An inline rebuild here is
        /// how a second vocabulary for the same radio starts.
        /// </summary>
        [Fact]
        public void ThePickerIsPopulatedFromTheModel()
        {
            string source = Read(Dialog);
            Assert.Contains("RadioProfilePickerModel.Build(", source, StringComparison.Ordinal);
        }

        /// <summary>
        /// The selection announcement passes the radio's spoken identity into
        /// the description. Without this, {who} in the template renders as a
        /// literal brace token — and the defect this all fixes comes back with
        /// the words "left brace who right brace" in front of it.
        /// </summary>
        [Fact]
        public void TheDescriptionIsGivenTheRadiosSpokenName()
        {
            string source = Read(Dialog);
            Assert.Contains("(\"who\", RadioProfilePickerModel.SpokenNameFor(cfg))",
                source, StringComparison.Ordinal);
        }

        /// <summary>
        /// Commit receipts name radios through the same ladder as the picker
        /// rows. Two vocabularies for one radio is the defect class the
        /// integration pass exists to catch; refuse it at the source.
        /// </summary>
        [Fact]
        public void CommitReceiptsUseTheSameLadder()
        {
            string source = Read(Dialog);
            Assert.Contains("RadioProfilePickerModel.LabelFor(cfg)", source, StringComparison.Ordinal);
        }

        /// <summary>
        /// The positive control: the reader finds what is known to be present
        /// and discriminates against what is known not to be.
        /// </summary>
        [Fact]
        public void TheSourceReaderFindsWhatIsThereAndNotWhatIsNot()
        {
            string source = Read(Dialog);
            Assert.Contains("PopulateRadioProfilePicker", source, StringComparison.Ordinal);
            Assert.DoesNotContain("NoSuchPickerSymbol", source, StringComparison.Ordinal);
        }

        private static string Read(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "Could not find " + relative + " (looked at " + path + "). A test that cannot find "
                + "its subject proves nothing about it.");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
