using System;
using System.IO;
using System.Linq;
using Radios;
using Radios.DiscoveryChain;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Queue-burn Track E. Covers the two on-disk stores the known-radios
    /// roster reads: the serial-keyed per-radio profile and the account-keyed
    /// half of radioConnectionCacheV1.xml.
    ///
    /// <para>Backward compatibility is the point of half these tests. Both files
    /// exist on every install already; a config.xml or cache written before
    /// these fields shipped must load without complaint, because the alternative
    /// is a user losing their per-radio network settings on upgrade.</para>
    ///
    /// <para>One class, not several: <see cref="RadioConfig.BaseDirectory"/> and
    /// <see cref="KnownRadioRoster.CacheDirectory"/> are process-wide statics, so
    /// everything that touches them shares a collection and cannot race.</para>
    /// </summary>
    public sealed class KnownRadioRosterTests : IDisposable
    {
        private readonly string _dir;
        private readonly string? _savedBase;
        private readonly string? _savedCache;

        public KnownRadioRosterTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "jjflex-roster-tests-" + Guid.NewGuid().ToString("N"));
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

        // ------------------------------------------------------------------
        // RadioConfig: the appended roster fields
        // ------------------------------------------------------------------

        [Fact]
        public void RadioConfig_RoundTripsRosterFields()
        {
            var seen = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
            var cfg = new RadioConfig
            {
                Nickname = "6300inshack",
                Model = "FLEX-6300",
                IsFavorite = true,
                LastSeenUtc = seen,
                LastSeenRemote = true,
                LastSeenViaAccount = "dbreda@example.com",
                // A Track C field, set to prove the two concerns coexist in one file.
                ConnectionPreference = RadioConnectionPreference.ForwardOnly,
                FixedHolePunchPort = 40420,
            };
            Assert.True(cfg.Save(_dir, "1234-5678"));

            var back = RadioConfig.Load(_dir, "1234-5678");
            Assert.Equal("6300inshack", back.Nickname);
            Assert.Equal("FLEX-6300", back.Model);
            Assert.True(back.IsFavorite);
            Assert.Equal(seen, back.LastSeenUtc);
            Assert.True(back.LastSeenRemote);
            Assert.Equal("dbreda@example.com", back.LastSeenViaAccount);
            Assert.Equal(RadioConnectionPreference.ForwardOnly, back.ConnectionPreference);
            Assert.Equal(40420, back.FixedHolePunchPort);
        }

        [Fact]
        public void RadioConfig_PreRosterFileStillLoads()
        {
            // Exactly what a config.xml written before the roster fields existed
            // looks like. Every install has one of these.
            var path = Path.Combine(_dir, "radios", "0000-0001", "config.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                "<?xml version=\"1.0\"?>\n" +
                "<RadioConfig xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <Version>1</Version>\n" +
                "  <RadioId>0000-0001</RadioId>\n" +
                "  <Nickname>old rig</Nickname>\n" +
                "  <ConnectionPreference>HolePunch</ConnectionPreference>\n" +
                "  <FixedHolePunchPort>40420</FixedHolePunchPort>\n" +
                "</RadioConfig>\n");

            var cfg = RadioConfig.Load(_dir, "0000-0001");
            Assert.Equal("old rig", cfg.Nickname);
            Assert.Equal(RadioConnectionPreference.HolePunch, cfg.ConnectionPreference);
            Assert.Equal(40420, cfg.FixedHolePunchPort);
            // New fields take their defaults rather than blowing up the load.
            Assert.False(cfg.IsFavorite);
            Assert.Equal("", cfg.Model);
            Assert.Equal(default, cfg.LastSeenUtc);
            Assert.Equal("", cfg.LastSeenViaAccount);
        }

        [Fact]
        public void RadioConfig_RoundTripsNoPhysicalAccessAndRemOnFields()
        {
            // Track C (settings that stick): the geography flag, its
            // explicit-decision marker, and the REM ON queued intent all
            // survive a save/load cycle.
            var cfg = new RadioConfig
            {
                NoPhysicalAccess = true,
                NoPhysicalAccessDecided = true,
                RemOnOnConnect = RemOnOnConnectModes.TurnOn,
            };
            Assert.True(cfg.Save(_dir, "9999-0001"));

            var back = RadioConfig.Load(_dir, "9999-0001");
            Assert.True(back.NoPhysicalAccess);
            Assert.True(back.NoPhysicalAccessDecided);
            Assert.Equal(RemOnOnConnectModes.TurnOn, back.RemOnOnConnect);
        }

        [Fact]
        public void RadioConfig_PreTrackCFileDefaultsToReachableAndLeaveAlone()
        {
            // A config.xml written before the Track C fields existed must load
            // with the do-nothing defaults: no decision recorded on physical
            // access, and REM ON left alone at connect.
            var path = Path.Combine(_dir, "radios", "0000-0002", "config.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path,
                "<?xml version=\"1.0\"?>\n" +
                "<RadioConfig xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <Version>1</Version>\n" +
                "  <RadioId>0000-0002</RadioId>\n" +
                "  <Nickname>older rig</Nickname>\n" +
                "</RadioConfig>\n");

            var cfg = RadioConfig.Load(_dir, "0000-0002");
            Assert.False(cfg.NoPhysicalAccess);
            Assert.False(cfg.NoPhysicalAccessDecided);
            Assert.Equal(RemOnOnConnectModes.LeaveAlone, cfg.RemOnOnConnect);
        }

        [Fact]
        public void RadioConfig_LoadAllKnown_ReturnsEverySavedProfile()
        {
            new RadioConfig { Nickname = "a" }.Save(_dir, "1111");
            new RadioConfig { Nickname = "b" }.Save(_dir, "2222");

            var all = RadioConfig.LoadAllKnown(_dir);
            Assert.Equal(2, all.Count);
            Assert.Contains(all, c => c.RadioId == "1111" && c.Nickname == "a");
            Assert.Contains(all, c => c.RadioId == "2222" && c.Nickname == "b");
        }

        // ------------------------------------------------------------------
        // RadioConnectionCache: the account-keyed list
        // ------------------------------------------------------------------

        [Fact]
        public void Cache_PreAccountListFileStillLoads()
        {
            // A cache written before AccountLists existed. Proves the additive
            // element does not break the parity-locked Entries schema.
            var path = Path.Combine(_dir, "radioConnectionCacheV1.xml");
            File.WriteAllText(path,
                "<?xml version=\"1.0\"?>\n" +
                "<RadioConnectionCache xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <Entries>\n" +
                "    <RadioConnectionCacheEntry>\n" +
                "      <Serial>1234-5678</Serial>\n" +
                "      <Nickname>6300inshack</Nickname>\n" +
                "      <Model>FLEX-6300</Model>\n" +
                "    </RadioConnectionCacheEntry>\n" +
                "  </Entries>\n" +
                "</RadioConnectionCache>\n");

            var cache = new RadioConnectionCache(_dir);
            var entries = cache.GetAllEntries();
            Assert.Single(entries);
            Assert.Equal("FLEX-6300", entries[0].Model);
            Assert.Null(cache.LookupAccountRadioList("nobody@example.com"));
        }

        [Fact]
        public void Cache_UnknownFutureElementsAreIgnored()
        {
            // The mirror of the test above: this file has an element THIS build
            // does not know. It must still load, because that is the promise
            // being made to the 4.2 line in the other direction.
            var path = Path.Combine(_dir, "radioConnectionCacheV1.xml");
            File.WriteAllText(path,
                "<?xml version=\"1.0\"?>\n" +
                "<RadioConnectionCache xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <Entries />\n" +
                "  <SomethingFromTheFuture><Nonsense>yes</Nonsense></SomethingFromTheFuture>\n" +
                "</RadioConnectionCache>\n");

            var cache = new RadioConnectionCache(_dir);
            Assert.Empty(cache.GetAllEntries());
        }

        [Fact]
        public void Cache_AccountListLookupIsCaseInsensitiveAndTimestamped()
        {
            var cache = new RadioConnectionCache(_dir);
            var before = DateTime.UtcNow.AddSeconds(-1);
            // RecordAccountRadioList takes FlexLib Radio objects, which cannot be
            // constructed here; the on-disk shape is exercised directly instead.
            var file = Path.Combine(_dir, "radioConnectionCacheV1.xml");
            File.WriteAllText(file,
                "<?xml version=\"1.0\"?>\n" +
                "<RadioConnectionCache xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <Entries />\n" +
                "  <AccountLists>\n" +
                "    <AccountRadioListEntry>\n" +
                "      <AccountEmail>DBreda@Example.com</AccountEmail>\n" +
                $"      <FetchedUtc>{before:yyyy-MM-ddTHH:mm:ss}Z</FetchedUtc>\n" +
                "      <Radios>\n" +
                "        <CachedRadioListItem>\n" +
                "          <Serial>1234-5678</Serial>\n" +
                "          <Nickname>6300inshack</Nickname>\n" +
                "          <Model>FLEX-6300</Model>\n" +
                "        </CachedRadioListItem>\n" +
                "      </Radios>\n" +
                "    </AccountRadioListEntry>\n" +
                "  </AccountLists>\n" +
                "</RadioConnectionCache>\n");

            cache = new RadioConnectionCache(_dir);
            var acct = cache.LookupAccountRadioList("dbreda@example.com");
            Assert.NotNull(acct);
            Assert.Single(acct!.Radios);
            Assert.Equal("6300inshack", acct.Radios[0].Nickname);
            Assert.Null(cache.LookupAccountRadioList(""));
        }

        // ------------------------------------------------------------------
        // The roster itself
        // ------------------------------------------------------------------

        [Fact]
        public void Roster_MergesProfileAndCacheBySerial_FavoritesFirst()
        {
            new RadioConfig
            {
                Nickname = "8600",
                Model = "FLEX-8600",
                LastSeenUtc = DateTime.UtcNow.AddHours(-1),
            }.Save(_dir, "1111");

            new RadioConfig
            {
                Nickname = "6300inshack",
                IsFavorite = true,
                LastSeenRemote = true,
                LastSeenViaAccount = "dbreda@example.com",
                LastSeenUtc = DateTime.UtcNow.AddDays(-3),
            }.Save(_dir, "2222");

            // The cache supplies a model the profile never learned, plus a radio
            // that has no profile at all (connected before the roster shipped).
            File.WriteAllText(Path.Combine(_dir, "radioConnectionCacheV1.xml"),
                "<?xml version=\"1.0\"?>\n" +
                "<RadioConnectionCache xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <Entries>\n" +
                "    <RadioConnectionCacheEntry>\n" +
                "      <Serial>2222</Serial><Nickname>6300inshack</Nickname><Model>FLEX-6300</Model>\n" +
                "    </RadioConnectionCacheEntry>\n" +
                "    <RadioConnectionCacheEntry>\n" +
                "      <Serial>3333</Serial><Nickname>orphan</Nickname><Model>FLEX-6500</Model>\n" +
                "    </RadioConnectionCacheEntry>\n" +
                "  </Entries>\n" +
                "</RadioConnectionCache>\n");

            var roster = KnownRadioRoster.Load();

            Assert.Equal(3, roster.Count);
            // Favorite sorts first, whatever its last-seen age.
            Assert.Equal("2222", roster[0].Serial);
            Assert.True(roster[0].IsFavorite);
            // Model filled in from the cache where the profile had none.
            Assert.Equal("FLEX-6300", roster[0].Model);
            Assert.Equal("dbreda@example.com", roster[0].LastSeenViaAccount);
            // A cache-only radio still makes the roster.
            Assert.Contains(roster, r => r.Serial == "3333" && r.Model == "FLEX-6500");
        }

        [Fact]
        public void Roster_MarksRowsFromTheRequestedAccountsCachedList()
        {
            new RadioConfig { Nickname = "6300inshack" }.Save(_dir, "2222");
            var fetched = DateTime.UtcNow.AddMinutes(-20);
            File.WriteAllText(Path.Combine(_dir, "radioConnectionCacheV1.xml"),
                "<?xml version=\"1.0\"?>\n" +
                "<RadioConnectionCache xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <Entries />\n" +
                "  <AccountLists>\n" +
                "    <AccountRadioListEntry>\n" +
                "      <AccountEmail>dbreda@example.com</AccountEmail>\n" +
                $"      <FetchedUtc>{fetched:yyyy-MM-ddTHH:mm:ss}Z</FetchedUtc>\n" +
                "      <Radios><CachedRadioListItem><Serial>2222</Serial>" +
                "<Nickname>6300inshack</Nickname><Model>FLEX-6300</Model></CachedRadioListItem></Radios>\n" +
                "    </AccountRadioListEntry>\n" +
                "  </AccountLists>\n" +
                "</RadioConnectionCache>\n");

            var mine = KnownRadioRoster.Load("dbreda@example.com");
            var row = Assert.Single(mine);
            Assert.True(row.InAccountCache);
            Assert.True(row.LastSeenRemote);
            Assert.Equal("dbreda@example.com", row.LastSeenViaAccount);

            // A different account must not inherit someone else's list.
            var theirs = KnownRadioRoster.Load("someone@else.com");
            Assert.False(theirs.Single().InAccountCache);

            // And no account at all is not the same as every account.
            Assert.False(KnownRadioRoster.Load().Single().InAccountCache);
        }

        [Fact]
        public void Roster_AttributesRadiosFromEveryAccountList_NotJustTheCurrentOne()
        {
            // Don's 6300 exists only in DON'S cached list, and the selector is
            // open under Noel's account. The row must still name Don's account
            // — a foreign radio is the one case where naming the owner is
            // load-bearing — while InAccountCache stays false, because Noel's
            // account cannot see it now (phase 1 of the unified roster; before
            // this, the label was inverted relative to need).
            var fetched = DateTime.UtcNow.AddHours(-2);
            File.WriteAllText(Path.Combine(_dir, "radioConnectionCacheV1.xml"),
                "<?xml version=\"1.0\"?>\n" +
                "<RadioConnectionCache xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <Entries />\n" +
                "  <AccountLists>\n" +
                "    <AccountRadioListEntry>\n" +
                "      <AccountEmail>dbreda@example.com</AccountEmail>\n" +
                $"      <FetchedUtc>{fetched:yyyy-MM-ddTHH:mm:ss}Z</FetchedUtc>\n" +
                "      <Radios><CachedRadioListItem><Serial>2222</Serial>" +
                "<Nickname>6300inshack</Nickname><Model>FLEX-6300</Model></CachedRadioListItem></Radios>\n" +
                "    </AccountRadioListEntry>\n" +
                "  </AccountLists>\n" +
                "</RadioConnectionCache>\n");

            var roster = KnownRadioRoster.Load("nromey@example.com");
            var row = Assert.Single(roster);
            Assert.Equal("dbreda@example.com", row.LastSeenViaAccount);
            Assert.False(row.InAccountCache);
            Assert.True(row.LastSeenRemote);
            Assert.True(row.LastSeenUtc > DateTime.MinValue);
        }

        [Fact]
        public void Roster_LatestListWinsAttribution_AndProfileOutranksCache()
        {
            // A club rig both accounts can list: the LATER fetch attributes it,
            // because last-listed is the freshest observation available.
            var early = DateTime.UtcNow.AddDays(-3);
            var late = DateTime.UtcNow.AddHours(-1);
            File.WriteAllText(Path.Combine(_dir, "radioConnectionCacheV1.xml"),
                "<?xml version=\"1.0\"?>\n" +
                "<RadioConnectionCache xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <Entries />\n" +
                "  <AccountLists>\n" +
                "    <AccountRadioListEntry>\n" +
                "      <AccountEmail>later@example.com</AccountEmail>\n" +
                $"      <FetchedUtc>{late:yyyy-MM-ddTHH:mm:ss}Z</FetchedUtc>\n" +
                "      <Radios><CachedRadioListItem><Serial>2222</Serial></CachedRadioListItem></Radios>\n" +
                "    </AccountRadioListEntry>\n" +
                "    <AccountRadioListEntry>\n" +
                "      <AccountEmail>earlier@example.com</AccountEmail>\n" +
                $"      <FetchedUtc>{early:yyyy-MM-ddTHH:mm:ss}Z</FetchedUtc>\n" +
                "      <Radios><CachedRadioListItem><Serial>2222</Serial></CachedRadioListItem></Radios>\n" +
                "    </AccountRadioListEntry>\n" +
                "  </AccountLists>\n" +
                "</RadioConnectionCache>\n");

            Assert.Equal("later@example.com",
                KnownRadioRoster.Load().Single().LastSeenViaAccount);

            // A profile-written attribution (a real sighting) outranks the
            // cache: the cache fills blanks, it never overwrites.
            KnownRadioRoster.RecordSighting("2222", "club", "FLEX-6600",
                isRemote: true, accountEmail: "profile@example.com");
            Assert.Equal("profile@example.com",
                KnownRadioRoster.Load().Single().LastSeenViaAccount);
        }

        [Fact]
        public void Roster_RecordSighting_DoesNotEraseTheKnownAccountOnALanSighting()
        {
            KnownRadioRoster.RecordSighting("2222", "6300inshack", "FLEX-6300",
                isRemote: true, accountEmail: "dbreda@example.com");
            Assert.Equal("dbreda@example.com", RadioConfig.Load(_dir, "2222").LastSeenViaAccount);

            // Seen on the LAN a moment later. A local sighting says nothing about
            // which account can see the radio remotely, so the answer we already
            // have must survive.
            KnownRadioRoster.RecordSighting("2222", "6300inshack", "FLEX-6300",
                isRemote: false, accountEmail: "");
            var cfg = RadioConfig.Load(_dir, "2222");
            Assert.Equal("dbreda@example.com", cfg.LastSeenViaAccount);
            Assert.False(cfg.LastSeenRemote);
        }

        [Fact]
        public void Roster_SightingNeverOverwritesThePreferredAccount()
        {
            // The choice/observation split: PreferredAccount is operator-set
            // and sticky; sightings write LastSeenViaAccount and must never
            // touch it. Conflating them lets an incidental listing destroy a
            // deliberate decision with no event anyone could hear.
            Assert.True(KnownRadioRoster.SetPreferredAccount("2222", "club@example.com"));
            KnownRadioRoster.RecordSighting("2222", "club", "FLEX-6600",
                isRemote: true, accountEmail: "other@example.com");

            var cfg = RadioConfig.Load(_dir, "2222");
            Assert.Equal("club@example.com", cfg.PreferredAccount);
            Assert.Equal("other@example.com", cfg.LastSeenViaAccount);

            // Resolution order: the choice outranks the observation.
            Assert.Equal("club@example.com", KnownRadioRoster.Load().Single().ResolvedAccount);

            // Clearing the preference falls back to the observation.
            Assert.True(KnownRadioRoster.SetPreferredAccount("2222", ""));
            Assert.Equal("other@example.com", KnownRadioRoster.Load().Single().ResolvedAccount);
        }

        [Fact]
        public void Roster_SetFavorite_PersistsAndReportsSuccess()
        {
            new RadioConfig { Nickname = "6300inshack" }.Save(_dir, "2222");

            Assert.True(KnownRadioRoster.SetFavorite("2222", true));
            Assert.True(KnownRadioRoster.IsFavorite("2222"));

            Assert.True(KnownRadioRoster.SetFavorite("2222", false));
            Assert.False(KnownRadioRoster.IsFavorite("2222"));

            // No serial, nothing to save, and the caller is told so — announcing
            // success here would promise something the next launch breaks.
            Assert.False(KnownRadioRoster.SetFavorite("", true));
        }

        // ------------------------------------------------------------------
        // Spoken age
        // ------------------------------------------------------------------

        [Fact]
        public void DescribeAge_SpeaksAgesNotTimestamps()
        {
            Assert.Equal("last seen unknown", KnownRadioRoster.DescribeAge(default));
            Assert.Equal("last seen unknown", KnownRadioRoster.DescribeAge(DateTime.MinValue));
            Assert.Equal("last seen just now", KnownRadioRoster.DescribeAge(DateTime.UtcNow));
            // A clock that ran backwards must not produce "last seen -4 minutes ago".
            Assert.Equal("last seen just now", KnownRadioRoster.DescribeAge(DateTime.UtcNow.AddMinutes(5)));
            Assert.Equal("last seen 20 minutes ago", KnownRadioRoster.DescribeAge(DateTime.UtcNow.AddMinutes(-20)));
            Assert.Equal("last seen 1 hour ago", KnownRadioRoster.DescribeAge(DateTime.UtcNow.AddMinutes(-61)));
            Assert.Equal("last seen 3 hours ago", KnownRadioRoster.DescribeAge(DateTime.UtcNow.AddHours(-3)));
            Assert.Equal("last seen 1 day ago", KnownRadioRoster.DescribeAge(DateTime.UtcNow.AddDays(-1)));
            Assert.Equal("last seen 3 days ago", KnownRadioRoster.DescribeAge(DateTime.UtcNow.AddDays(-3)));
            Assert.Equal("last seen 2 months ago", KnownRadioRoster.DescribeAge(DateTime.UtcNow.AddDays(-70)));
        }
    }
}
