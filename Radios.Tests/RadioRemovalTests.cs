using System;
using System.IO;
using System.Linq;
using Radios;
using Radios.DiscoveryChain;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 31 Track P, task #98: two ways to remove a radio, and what each
    /// one actually does to the two stores the roster reads.
    ///
    /// <para>The test that matters most is
    /// <see cref="SafeRemovalPurgesTheCacheToo"/>. Removal has an obvious
    /// implementation that is wrong in a way no one notices until the next
    /// launch: deal with the per-radio profile, forget that the roster ALSO
    /// merges radioConnectionCacheV1.xml by serial, and the row repaints itself
    /// from the source nobody thought to look at. The operator watches their
    /// removal undo itself and has no way to tell whether it ever worked.</para>
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RadioRemovalTests : IDisposable
    {
        private const string Serial = "0123-4567-8600-7001";

        private readonly string _dir;
        private readonly string? _savedBase;
        private readonly string? _savedCache;

        public RadioRemovalTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "jjflex-removal-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _savedBase = RadioConfig.BaseDirectory;
            _savedCache = KnownRadioRoster.CacheDirectory;
            RadioConfig.BaseDirectory = _dir;
            KnownRadioRoster.CacheDirectory = _dir;
        }

        public void Dispose()
        {
            RadioConfig.BaseDirectory = _savedBase;
            KnownRadioRoster.CacheDirectory = _savedCache;
            try { Directory.Delete(_dir, recursive: true); } catch { /* temp dir */ }
        }

        /// <summary>
        /// Put a radio in an account's cached list. RecordAccountRadioList
        /// takes FlexLib Radio objects, which cannot be constructed in a test,
        /// so the on-disk shape is written directly — the same approach
        /// KnownRadioRosterTests already takes for this file.
        /// </summary>
        private void GiveTheAccountACachedListing(string account, params string[] serials)
        {
            var rows = string.Concat(serials.Select(s =>
                "        <CachedRadioListItem>\n" +
                $"          <Serial>{s}</Serial>\n" +
                "          <Nickname>listed</Nickname>\n" +
                "          <Model>FLEX-8600</Model>\n" +
                "        </CachedRadioListItem>\n"));

            File.WriteAllText(Path.Combine(_dir, "radioConnectionCacheV1.xml"),
                "<?xml version=\"1.0\"?>\n" +
                "<RadioConnectionCache xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <Entries />\n" +
                "  <AccountLists>\n" +
                "    <AccountRadioListEntry>\n" +
                $"      <AccountEmail>{account}</AccountEmail>\n" +
                $"      <FetchedUtc>{DateTime.UtcNow.AddSeconds(-1):yyyy-MM-ddTHH:mm:ss}Z</FetchedUtc>\n" +
                "      <Radios>\n" + rows +
                "      </Radios>\n" +
                "    </AccountRadioListEntry>\n" +
                "  </AccountLists>\n" +
                "</RadioConnectionCache>\n");
        }

        private static string ProfileDir(string root, string serial) =>
            Path.Combine(root, "radios", serial);

        private void GiveTheRadioAProfileAndAHistory()
        {
            var cfg = new RadioConfig
            {
                Nickname = "junk from testing",
                UserNickname = "the one I never want to see again",
                Model = "FLEX-8600",
                IsFavorite = true,
                SmartLinkIntent = SmartLinkIntents.LocalOnly,
                RemOnOnConnect = RemOnOnConnectModes.TurnOn,
                PathChain = new System.Collections.Generic.List<ConnectPathKind>
                {
                    ConnectPathKind.SmartLink, ConnectPathKind.Local,
                },
            };
            Assert.True(cfg.Save(_dir, Serial));
            ConnectionHistory.Record(Serial, ConnectPathKind.SmartLink.ToString(),
                ConnectPathPolicy.ConnectedOutcome, 700);
        }

        // ------------------------------------------------------------------
        // The safe scope
        // ------------------------------------------------------------------

        [Fact]
        public void SafeRemovalTakesTheRowAwayAndKeepsEverySetting()
        {
            GiveTheRadioAProfileAndAHistory();
            Assert.Contains(KnownRadioRoster.Load(), e => e.Serial == Serial);

            Assert.True(KnownRadioRoster.Remove(Serial, deleteSettings: false));

            Assert.DoesNotContain(KnownRadioRoster.Load(), e => e.Serial == Serial);

            // Everything the destructive scope would have destroyed is still
            // here. This is the whole difference between the two scopes.
            var back = RadioConfig.LoadForRadio(Serial);
            Assert.True(back.HiddenFromList);
            Assert.Equal("the one I never want to see again", back.UserNickname);
            Assert.True(back.IsFavorite);
            Assert.Equal(SmartLinkIntents.LocalOnly, back.SmartLinkIntent);
            Assert.Equal(RemOnOnConnectModes.TurnOn, back.RemOnOnConnect);
            Assert.Equal(ConnectPathKind.SmartLink, back.PathChain[0]);
            Assert.Single(ConnectionHistory.Load(Serial));
        }

        [Fact]
        public void SafeRemovalPurgesTheCacheToo()
        {
            // The roster merges the per-radio profile AND
            // radioConnectionCacheV1.xml by serial. A removal that hides the
            // profile but leaves a cached account listing repaints the row on
            // the next open, from a source nobody thought to look at.
            GiveTheRadioAProfileAndAHistory();

            GiveTheAccountACachedListing("owner@example.com", Serial);
            Assert.Contains(KnownRadioRoster.Load("owner@example.com"), e => e.Serial == Serial);

            Assert.True(KnownRadioRoster.Remove(Serial, deleteSettings: false));

            Assert.DoesNotContain(KnownRadioRoster.Load("owner@example.com"), e => e.Serial == Serial);
            Assert.DoesNotContain(new RadioConnectionCache(_dir).GetAllAccountRadioLists()
                    .SelectMany(a => a.Radios), r => r.Serial == Serial);
        }

        [Fact]
        public void ASightingBringsAHiddenRadioBackWithItsSettings()
        {
            // A radio that answers is real. Keeping it hidden would lock the
            // operator out of their own rig with no explanation anywhere — and
            // it is why the confirmation never promises that an ONLINE radio
            // will stay gone.
            GiveTheRadioAProfileAndAHistory();
            Assert.True(KnownRadioRoster.Remove(Serial, deleteSettings: false));
            Assert.DoesNotContain(KnownRadioRoster.Load(), e => e.Serial == Serial);

            KnownRadioRoster.RecordSighting(Serial, "8600 in the shack", "FLEX-8600",
                isRemote: false, accountEmail: "");

            var row = KnownRadioRoster.Load().SingleOrDefault(e => e.Serial == Serial);
            Assert.NotNull(row);
            // Back with its identity, not as an anonymous rediscovery.
            Assert.Equal("the one I never want to see again", row!.UserNickname);
            Assert.True(row.IsFavorite);
        }

        [Fact]
        public void AnAccountThatStillListsTheRadioBringsItBackWithItsSettings()
        {
            // Removal cleared it from every cached list, so its reappearance
            // there means a fresh SmartLink pass put it back — the account
            // really does list it. Same rule as a local sighting.
            GiveTheRadioAProfileAndAHistory();
            Assert.True(KnownRadioRoster.Remove(Serial, deleteSettings: false));

            GiveTheAccountACachedListing("owner@example.com", Serial);

            var row = KnownRadioRoster.Load("owner@example.com").SingleOrDefault(e => e.Serial == Serial);
            Assert.NotNull(row);
            Assert.Equal("the one I never want to see again", row!.UserNickname);
            Assert.True(row.IsFavorite);
        }

        // ------------------------------------------------------------------
        // The destructive scope
        // ------------------------------------------------------------------

        [Fact]
        public void DestructiveRemovalDeletesTheDIRECTORYNotJustTheRow()
        {
            // If the directory survives, every setting resurrects the moment
            // the radio is re-discovered — which looks exactly like the app
            // ignoring the operator.
            GiveTheRadioAProfileAndAHistory();
            Assert.True(Directory.Exists(ProfileDir(_dir, Serial)));

            Assert.True(KnownRadioRoster.Remove(Serial, deleteSettings: true));

            Assert.False(Directory.Exists(ProfileDir(_dir, Serial)));
            Assert.DoesNotContain(KnownRadioRoster.Load(), e => e.Serial == Serial);
            Assert.Empty(ConnectionHistory.Load(Serial));
        }

        [Fact]
        public void AfterDestructiveRemovalARediscoveredRadioComesBackWithNothing()
        {
            // The stated bargain: the radio can come back, the configuration
            // cannot. This is the test that the destructive scope's warning is
            // telling the truth.
            GiveTheRadioAProfileAndAHistory();
            Assert.True(KnownRadioRoster.Remove(Serial, deleteSettings: true));

            KnownRadioRoster.RecordSighting(Serial, "8600 in the shack", "FLEX-8600",
                isRemote: false, accountEmail: "");

            var row = KnownRadioRoster.Load().SingleOrDefault(e => e.Serial == Serial);
            Assert.NotNull(row);
            Assert.Equal("", row!.UserNickname);
            Assert.False(row.IsFavorite);
            Assert.Empty(row.PathChain);
        }

        // ------------------------------------------------------------------
        // Edges
        // ------------------------------------------------------------------

        [Fact]
        public void RemovingARadioThatOnlyExistsInTheCacheNeedsNoProfile()
        {
            // "2222" and friends: an entry with nothing configured for it. The
            // cache purge IS the entire removal, and it must not fail for want
            // of a profile to flag.
            const string cacheOnly = "2222";
            GiveTheAccountACachedListing("owner@example.com", cacheOnly);
            Assert.Contains(KnownRadioRoster.Load("owner@example.com"), e => e.Serial == cacheOnly);

            Assert.True(KnownRadioRoster.Remove(cacheOnly, deleteSettings: false));
            Assert.DoesNotContain(KnownRadioRoster.Load("owner@example.com"), e => e.Serial == cacheOnly);
            Assert.False(Directory.Exists(ProfileDir(_dir, cacheOnly)));
        }

        [Fact]
        public void RemovingARadioNobodyHasEverHeardOfIsNotAnError()
        {
            Assert.True(KnownRadioRoster.Remove("9999-9999-9999-9999", deleteSettings: false));
            Assert.True(KnownRadioRoster.Remove("9999-9999-9999-9999", deleteSettings: true));
        }

        [Fact]
        public void AnEmptySerialIsRefusedRatherThanTakenAsAWildcard()
        {
            GiveTheRadioAProfileAndAHistory();
            Assert.False(KnownRadioRoster.Remove("", deleteSettings: true));
            Assert.False(KnownRadioRoster.Remove(null!, deleteSettings: true));
            Assert.True(Directory.Exists(ProfileDir(_dir, Serial)));
        }

        [Fact]
        public void ADottedSerialCannotClimbOutOfTheRadiosFolder()
        {
            // Sprint 33 Track J. '.' is a legal character in a sanitised radio
            // id, so ".." used to survive sanitising unchanged and
            // Path.Combine(base, "radios", "..") resolves to the base directory
            // itself — the parent of every radio's folder. Under the
            // destructive scope that is a recursive delete of the operator's
            // entire settings tree instead of one radio's, and the destructive
            // scope only became reachable by keyboard in Sprint 32 Track G.
            //
            // The sibling test above covers the empty serial. This is the same
            // class of bug through a serial that is not empty at all.
            GiveTheRadioAProfileAndAHistory();
            var somethingElse = Path.Combine(_dir, "personal-data.xml");
            File.WriteAllText(somethingElse, "not a radio, must survive");

            foreach (var climber in new[] { "..", ".", "...", " .. " })
            {
                KnownRadioRoster.Remove(climber, deleteSettings: true);

                Assert.True(Directory.Exists(_dir));
                Assert.True(File.Exists(somethingElse));
                Assert.True(Directory.Exists(Path.Combine(_dir, "radios")));
                Assert.True(Directory.Exists(ProfileDir(_dir, Serial)));
            }

            // And the real radio still removes normally afterwards.
            Assert.True(KnownRadioRoster.Remove(Serial, deleteSettings: true));
            Assert.False(Directory.Exists(ProfileDir(_dir, Serial)));
            Assert.True(File.Exists(somethingElse));
        }

        [Fact]
        public void AConfigWrittenBeforeRemovalExistedIsNotHidden()
        {
            // HiddenFromList defaulting to false is what makes the upgrade a
            // no-op. If it ever defaulted the other way, every radio on every
            // install would vanish at once.
            var path = Path.Combine(_dir, "radios", "8888-0003", "config.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                "<?xml version=\"1.0\"?>\n" +
                "<RadioConfig xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <Version>1</Version>\n" +
                "  <RadioId>8888-0003</RadioId>\n" +
                "  <Nickname>older rig</Nickname>\n" +
                "</RadioConfig>\n");

            Assert.False(RadioConfig.Load(_dir, "8888-0003").HiddenFromList);
            Assert.Contains(KnownRadioRoster.Load(), e => e.Serial == "8888-0003");
        }
    }
}
