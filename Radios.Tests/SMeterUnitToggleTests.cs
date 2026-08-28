using System;
using System.Collections.Generic;
using System.IO;
using Radios;
using Radios.Speech;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The S-meter's unit as a remembered per-radio toggle (#337) — the two
    /// halves that could fail quietly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Persistence.</b> The unit was a bare field on <c>FlexBase</c>,
    /// written by one handler and saved by nothing: flip it, restart, gone. It
    /// now lives in the per-radio config keyed by serial, so what is pinned
    /// here is the round trip, the safe default for a config written before it
    /// shipped, and the promise that recording a unit disturbs nothing else in
    /// the file.
    /// </para>
    /// <para>
    /// <b>The coalesce seam.</b> The readout used to split its coalesce key by
    /// unit — <c>"smeter-dbm"</c> or <c>"smeter"</c> — so that #306's two
    /// simultaneously-reachable readings could not silence each other. With one
    /// unit live at a time that reason is gone and the key collapses to
    /// <c>"smeter"</c>. The risk the collapse creates is precise: toggle, then
    /// press Ctrl+S at once, and two readings in DIFFERENT units now arrive on
    /// ONE key, where a duplicate-drop or a pushed-out timer would swallow the
    /// second. That is #264's silence in a new coat, and a key that answers
    /// with nothing is indistinguishable from a key that is broken.
    /// </para>
    /// <para>
    /// It does not happen, and <see cref="SpeechCoalesceKind.Query"/> is why —
    /// a query is exempt from the duplicate-drop and never has its timer
    /// pushed out. The tests below prove that on the arbiter itself rather
    /// than asserting it in a comment, INCLUDING the pathological case where
    /// both units render the same characters. Each is paired with the
    /// <see cref="SpeechCoalesceKind.Value"/> control at identical timings, so
    /// a green result means the classification is doing the work and not the
    /// clock.
    /// </para>
    /// </remarks>
    public sealed class SMeterUnitCoalesceSeamTests
    {
        private sealed record SinkCall(string Message, double AtMs);

        private readonly FakeSpeechClock _clock = new();
        private readonly List<SinkCall> _calls = new();

        private SpeechArbiter NewArbiter() => new SpeechArbiter(
            _clock,
            () => VerbosityLevel.Chatty,
            (message, interrupt, intent, level, origin, salvaged) =>
            {
                _calls.Add(new SinkCall(message,
                    (_clock.UtcNow - _clock.Epoch).TotalMilliseconds));
                return true;
            },
            () => { },
            (message, level, intent, origin) => { });

        [Fact]
        public void TheHarnessSpeaksAtAll()
        {
            // Positive control. Every assertion below counts utterances, and a
            // sink that never fired would make "the second press answered"
            // fail loudly but "it was dropped" pass silently. Prove the
            // instrument first.
            var a = NewArbiter();
            a.Latest("smeter", "S 7", VerbosityLevel.Terse, SpeechCoalesceKind.Query, "t");

            var call = Assert.Single(_calls);
            Assert.Equal("S 7", call.Message);
            Assert.Equal(0, call.AtMs);
        }

        [Fact]
        public void ARePressRightAfterAToggle_AnswersInTheNewUnit_OnTheOneSharedKey()
        {
            // The seam itself. "S 7" earns the 700 ms anti-clip floor, so a
            // press at 800 ms is past the gap — and it is answered THERE,
            // synchronously, with no timer between the operator and the
            // reading, even though the previous utterance on this key was the
            // other unit entirely.
            var a = NewArbiter();
            a.Latest("smeter", "S 7", VerbosityLevel.Terse, SpeechCoalesceKind.Query, "t");
            Assert.Equal(700, SpeechArbiter.AntiClipGapMs("S 7"));

            _clock.Advance(800);
            a.Latest("smeter", "S meter minus 97 dBm", VerbosityLevel.Terse,
                     SpeechCoalesceKind.Query, "t");

            Assert.Equal(2, _calls.Count);
            Assert.Equal("S meter minus 97 dBm", _calls[1].Message);
            Assert.Equal(800, _calls[1].AtMs);
        }

        [Fact]
        public void TwoReadingsThatRenderIdentically_BothSpeak_OnTheOneSharedKey()
        {
            // The pathological case for a shared key, and the one a
            // duplicate-drop would eat: the two utterances are the same
            // characters. On a meter the repetition IS the information — it is
            // how an operator learns the signal has not moved — so the second
            // press must speak.
            var a = NewArbiter();
            a.Latest("smeter", "S 7", VerbosityLevel.Terse, SpeechCoalesceKind.Query, "t");

            _clock.Advance(800);
            a.Latest("smeter", "S 7", VerbosityLevel.Terse, SpeechCoalesceKind.Query, "t");

            Assert.Equal(2, _calls.Count);
            Assert.Equal(800, _calls[1].AtMs);
        }

        [Fact]
        public void TheSameIdenticalPairClassedAsAValue_IsStillDropped_TheControl()
        {
            // THE CONTROL, at timings identical to the test above. Classed as a
            // swept value, the identical second reading is dropped — which is
            // right for a value and was the #264 defect for a query. Without
            // this, the test above would pass on an arbiter that never drops
            // anything and would say nothing about the classification.
            var a = NewArbiter();
            a.Latest("smeter", "S 7", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");

            _clock.Advance(800);
            a.Latest("smeter", "S 7", VerbosityLevel.Terse, SpeechCoalesceKind.Value, "t");
            Assert.Single(_calls);

            _clock.Advance(10_000);
            Assert.Single(_calls);
        }

        [Fact]
        public void HammeringTheKeyAcrossAToggle_NeverDefersTheAnswerIndefinitely()
        {
            // A query's timer is never pushed out by the next press. Pressing
            // inside the anti-clip gap, repeatedly, must still be answered when
            // the gap expires — not pushed a further gap each time, which is
            // how "I hit Ctrl+S and it just lags" felt.
            var a = NewArbiter();
            a.Latest("smeter", "S 7", VerbosityLevel.Terse, SpeechCoalesceKind.Query, "t");

            _clock.Advance(200);
            a.Latest("smeter", "S meter minus 97 dBm", VerbosityLevel.Terse,
                     SpeechCoalesceKind.Query, "t");
            _clock.Advance(200);
            a.Latest("smeter", "S meter minus 96 dBm", VerbosityLevel.Terse,
                     SpeechCoalesceKind.Query, "t");

            _clock.Advance(299);            // t = 699, one short of the gap
            Assert.Single(_calls);
            _clock.Advance(1);              // t = 700 — the gap, and no more
            Assert.Equal(2, _calls.Count);
            Assert.Equal("S meter minus 96 dBm", _calls[1].Message);
            Assert.Equal(700, _calls[1].AtMs);
        }
    }

    /// <summary>
    /// The other half of #337: the unit survives a restart, because it is
    /// stored per radio rather than held in a field.
    /// </summary>
    /// <remarks>
    /// Shares the statics collection because it writes
    /// <see cref="RadioConfig.BaseDirectory"/>.
    /// </remarks>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class SMeterUnitPersistenceTests : IDisposable
    {
        private readonly RadioConfigStaticsScope _scope;

        private string Dir => _scope.Directory;

        public SMeterUnitPersistenceTests()
        {
            _scope = new RadioConfigStaticsScope(nameof(SMeterUnitPersistenceTests));
        }

        public void Dispose() => _scope.Dispose();

        [Fact]
        public void NewConfig_ReadsInSUnits()
        {
            // The default is the historical behaviour, and it must stay that:
            // an operator who never touches this hears exactly what they heard
            // before it shipped.
            Assert.False(new RadioConfig().SmeterInDbm);
        }

        [Fact]
        public void ConfigWrittenBeforeThisShipped_ReadsInSUnits()
        {
            const string id = "1234-5678-9012-3456";
            var dir = Path.Combine(Dir, "radios", id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.xml"),
                "<?xml version=\"1.0\"?>\n" +
                "<RadioConfig xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n" +
                "  <Version>1</Version>\n" +
                "  <RadioId>" + id + "</RadioId>\n" +
                "  <Nickname>Bench8600</Nickname>\n" +
                "</RadioConfig>\n");

            Assert.False(RadioConfig.LoadForRadio(id).SmeterInDbm);
            Assert.False(RadioConfig.SmeterInDbmOf(id));
        }

        [Fact]
        public void RecordSmeterInDbm_RoundTrips()
        {
            const string id = "1111-2222-3333-4444";
            RadioConfig.RecordSmeterInDbm(id, true);

            Assert.True(RadioConfig.LoadForRadio(id).SmeterInDbm);
            Assert.True(RadioConfig.SmeterInDbmOf(id));

            RadioConfig.RecordSmeterInDbm(id, false);
            Assert.False(RadioConfig.SmeterInDbmOf(id));
        }

        [Fact]
        public void TheUnitIsPerRadio_NotPerInstall()
        {
            // The whole reason it is keyed by serial: a rig used for antenna
            // comparison can sit in dBm while the casual second radio stays in
            // S-units, and neither answer moves the other.
            const string big = "1111-2222-3333-5555";
            const string casual = "1111-2222-3333-6666";
            RadioConfig.RecordSmeterInDbm(big, true);

            Assert.True(RadioConfig.SmeterInDbmOf(big));
            Assert.False(RadioConfig.SmeterInDbmOf(casual));
        }

        [Fact]
        public void RecordSmeterInDbm_SkipsTheWriteWhenNothingChanged()
        {
            const string id = "5555-6666-7777-8888";
            RadioConfig.RecordSmeterInDbm(id, true);
            var file = Path.Combine(Dir, "radios", id, "config.xml");
            var firstWrite = File.GetLastWriteTimeUtc(file);

            RadioConfig.RecordSmeterInDbm(id, true);
            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(file));
        }

        [Fact]
        public void RecordSmeterInDbm_RefusesAnEmptyRadioId()
        {
            // No serial means no radio to key on. Storing the preference
            // somewhere anyway would attach it to a radio nobody named.
            RadioConfig.RecordSmeterInDbm("", true);
            Assert.False(RadioConfig.SmeterInDbmOf(""));
        }

        [Fact]
        public void RecordSmeterInDbm_DoesNotDisturbTheRestOfTheConfig()
        {
            const string id = "9999-8888-7777-6666";
            var cfg = RadioConfig.LoadForRadio(id);
            cfg.UserNickname = "The remote base";
            cfg.Ownership = RadioOwnership.Mine;
            Assert.True(cfg.SaveForRadio(id));

            RadioConfig.RecordSmeterInDbm(id, true);

            var reloaded = RadioConfig.LoadForRadio(id);
            Assert.Equal("The remote base", reloaded.UserNickname);
            Assert.Equal(RadioOwnership.Mine, reloaded.Ownership);
            Assert.True(reloaded.SmeterInDbm);
        }
    }
}
