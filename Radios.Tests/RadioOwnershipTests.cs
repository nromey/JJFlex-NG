using System;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 31 Track S, task #94: the per-radio ownership flag, and the
    /// wording of the silent-transmit warning it gates (#99).
    ///
    /// <para>Two things are worth pinning down in tests here and neither is the
    /// happy path. The first is the upgrade guarantee: a config.xml written
    /// before this field existed must load as Unset — guest behaviour — because
    /// the alternative is an upgrade silently arming writes to radios nobody
    /// has answered the question about. The second is that seeding never
    /// decides: <see cref="RadioConfig.SuggestOwnership"/> may propose "mine"
    /// and must never propose "someone else's", since teaching an operator to
    /// dismiss this question is how a good prompt becomes a bad one.</para>
    ///
    /// <para>Shares the statics collection because it writes
    /// <see cref="RadioConfig.BaseDirectory"/>.</para>
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class RadioOwnershipTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(RadioOwnershipTests));
        private string _dir => _scope.Directory;

        public void Dispose() => _scope.Dispose();

        // ------------------------------------------------------------------
        // The safe default
        // ------------------------------------------------------------------

        [Fact]
        public void NewConfig_IsUnsetAndMayNotCreateRadioSideState()
        {
            var cfg = new RadioConfig();
            Assert.Equal(RadioOwnership.Unset, cfg.Ownership);
            Assert.False(cfg.MayCreateRadioSideState);
            Assert.False(cfg.OwnershipAnswered);
        }

        [Fact]
        public void ConfigWrittenBeforeOwnershipExisted_LoadsAsUnset()
        {
            // Exactly the shape an older install has on disk: no Ownership
            // element at all. It must deserialise to guest behaviour rather
            // than to whatever happens to be first in the enum.
            var dir = Path.Combine(_dir, "radios", "1234-5678-9012-3456");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.xml"),
                "<?xml version=\"1.0\"?>\n" +
                "<RadioConfig xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <Version>1</Version>\n" +
                "  <RadioId>1234-5678-9012-3456</RadioId>\n" +
                "  <Nickname>shack rig</Nickname>\n" +
                "</RadioConfig>\n");

            var cfg = RadioConfig.LoadForRadio("1234-5678-9012-3456");
            Assert.Equal("shack rig", cfg.Nickname);
            Assert.Equal(RadioOwnership.Unset, cfg.Ownership);
            Assert.False(cfg.MayCreateRadioSideState);
        }

        [Fact]
        public void Ownership_RoundTripsThroughDisk()
        {
            var cfg = new RadioConfig { Ownership = RadioOwnership.Mine };
            Assert.True(cfg.SaveForRadio("0000-1111-2222-3333"));

            var back = RadioConfig.LoadForRadio("0000-1111-2222-3333");
            Assert.Equal(RadioOwnership.Mine, back.Ownership);
            Assert.True(back.MayCreateRadioSideState);
            Assert.True(back.OwnershipAnswered);
        }

        [Fact]
        public void SomeoneElses_IsAnAnswerButNotAPermission()
        {
            // The distinction the three-state enum exists for: "not mine" stops
            // the question being asked again, and still refuses the write.
            var cfg = new RadioConfig { Ownership = RadioOwnership.SomeoneElses };
            Assert.True(cfg.OwnershipAnswered);
            Assert.False(cfg.MayCreateRadioSideState);
        }

        // ------------------------------------------------------------------
        // Recording an answer
        // ------------------------------------------------------------------

        [Fact]
        public void RecordOwnership_PersistsAndIsReadableWithoutLoadingTheConfig()
        {
            Assert.Equal(RadioOwnership.Unset, RadioConfig.OwnershipOf("4444-5555-6666-7777"));

            Assert.True(RadioConfig.RecordOwnership("4444-5555-6666-7777", RadioOwnership.Mine));
            Assert.Equal(RadioOwnership.Mine, RadioConfig.OwnershipOf("4444-5555-6666-7777"));

            Assert.True(RadioConfig.RecordOwnership("4444-5555-6666-7777", RadioOwnership.SomeoneElses));
            Assert.Equal(RadioOwnership.SomeoneElses, RadioConfig.OwnershipOf("4444-5555-6666-7777"));
        }

        [Fact]
        public void RecordOwnership_LeavesEveryOtherSettingAlone()
        {
            var cfg = new RadioConfig
            {
                UserNickname = "the 8600",
                RemOnOnConnect = RemOnOnConnectModes.TurnOn,
                SmartLinkIntent = SmartLinkIntents.LocalOnly,
                FixedHolePunchPort = 5599,
            };
            Assert.True(cfg.SaveForRadio("8888-8888-8888-8888"));

            RadioConfig.RecordOwnership("8888-8888-8888-8888", RadioOwnership.Mine);

            var back = RadioConfig.LoadForRadio("8888-8888-8888-8888");
            Assert.Equal(RadioOwnership.Mine, back.Ownership);
            Assert.Equal("the 8600", back.UserNickname);
            Assert.Equal(RemOnOnConnectModes.TurnOn, back.RemOnOnConnect);
            Assert.Equal(SmartLinkIntents.LocalOnly, back.SmartLinkIntent);
            Assert.Equal(5599, back.FixedHolePunchPort);
        }

        [Fact]
        public void RecordOwnership_IgnoresAnEmptyRadioId()
        {
            Assert.False(RadioConfig.RecordOwnership("", RadioOwnership.Mine));
            Assert.Equal(RadioOwnership.Unset, RadioConfig.OwnershipOf(""));
        }

        // ------------------------------------------------------------------
        // Seeding proposes; it never decides
        // ------------------------------------------------------------------

        [Fact]
        public void Suggest_TheMargaretCase_ProposesNothing()
        {
            // The case that killed derive-from-registration: the radio was last
            // listed through an account that is not the operator's own. That is
            // weak evidence of "not yours" — and weak evidence must produce NO
            // suggestion, never a "someone else's" the operator might accept
            // without reading.
            var cfg = new RadioConfig
            {
                LastSeenViaAccount = "mmgaffney@comcast.net",
                LastSeenRemote = true,
                LastSeenUtc = new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Utc),
            };
            Assert.Equal(RadioOwnership.Unset, cfg.SuggestOwnership("nromey@gmail.com"));
        }

        [Fact]
        public void Suggest_SeenThroughTheOperatorsOwnAccount_ProposesMine()
        {
            var cfg = new RadioConfig
            {
                LastSeenViaAccount = "nromey@gmail.com",
                LastSeenRemote = true,
                LastSeenUtc = new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Utc),
            };
            Assert.Equal(RadioOwnership.Mine, cfg.SuggestOwnership("nromey@gmail.com"));
        }

        [Fact]
        public void Suggest_LanOnlySighting_ProposesMine()
        {
            // A LAN-only radio has no registration to reason from at all. Being
            // on the operator's own network is the only signal there is, and it
            // is enough to pre-select — not to store.
            var cfg = new RadioConfig
            {
                LastSeenRemote = false,
                LastSeenUtc = new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Utc),
            };
            Assert.Equal(RadioOwnership.Mine, cfg.SuggestOwnership(null));
        }

        [Fact]
        public void Suggest_NeverSeen_ProposesNothing()
        {
            Assert.Equal(RadioOwnership.Unset, new RadioConfig().SuggestOwnership(null));
        }

        [Fact]
        public void Suggest_NeverProposesSomeoneElses()
        {
            // Guarding the rule rather than one case of it: no combination of
            // observations may produce a pre-selected "not yours".
            foreach (var account in new string?[] { null, "", "nromey@gmail.com" })
            {
                foreach (var seenVia in new[] { "", "nromey@gmail.com", "mmgaffney@comcast.net" })
                {
                    foreach (var remote in new[] { true, false })
                    {
                        foreach (var seen in new[] { DateTime.MinValue, new DateTime(2026, 8, 18) })
                        {
                            var cfg = new RadioConfig
                            {
                                LastSeenViaAccount = seenVia,
                                LastSeenRemote = remote,
                                LastSeenUtc = seen,
                            };
                            Assert.NotEqual(RadioOwnership.SomeoneElses,
                                cfg.SuggestOwnership(account));
                        }
                    }
                }
            }
        }

        [Fact]
        public void Suggest_IsAdviceOnly_AndChangesNothingOnDisk()
        {
            var cfg = new RadioConfig
            {
                LastSeenRemote = false,
                LastSeenUtc = new DateTime(2026, 8, 18, 9, 0, 0, DateTimeKind.Utc),
            };
            Assert.True(cfg.SaveForRadio("2222-3333-4444-5555"));

            Assert.Equal(RadioOwnership.Mine, cfg.SuggestOwnership(null));

            // The suggestion must not have leaked into the stored answer.
            Assert.Equal(RadioOwnership.Unset, cfg.Ownership);
            Assert.Equal(RadioOwnership.Unset, RadioConfig.OwnershipOf("2222-3333-4444-5555"));
        }

        // ------------------------------------------------------------------
        // The silent-transmit warning (#99)
        // ------------------------------------------------------------------

        [Fact]
        public void SilentTxWarning_SaysSomethingAtEveryVerbosity()
        {
            // Spoken at Critical, so it plays even with speech switched off —
            // which is exactly why every form has to carry the consequence and
            // not just the symptom.
            foreach (var level in new[]
                     {
                         VerbosityLevel.Critical, VerbosityLevel.Terse,
                         VerbosityLevel.Chatty, VerbosityLevel.Diagnostic,
                     })
            {
                string msg = FlexBase.SilentTxSpokenWarning(level);
                Assert.False(string.IsNullOrWhiteSpace(msg));
                Assert.Contains("mic profile", msg, StringComparison.OrdinalIgnoreCase);

                // "your computer", not "this computer" — Noel's wording,
                // 2026-08-19, and the phrase is load-bearing rather than
                // decorative. Every form has to name the computer as the source
                // of the audio, because an operator who hears only "no mic
                // profile" has no way to know which end of the path is broken.
                // This assertion went red when the wording changed and the test
                // did not; that is the assertion doing its job.
                Assert.Contains("your computer", msg, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void SilentTxWarning_GetsShorterAsSpeechGetsQuieter()
        {
            int off = FlexBase.SilentTxSpokenWarning(VerbosityLevel.Critical).Length;
            int terse = FlexBase.SilentTxSpokenWarning(VerbosityLevel.Terse).Length;
            int chatty = FlexBase.SilentTxSpokenWarning(VerbosityLevel.Chatty).Length;
            Assert.True(off < terse, "the speech-off form must be the shortest");
            Assert.True(terse < chatty, "the terse form must be shorter than the chatty one");
        }
    }
}
