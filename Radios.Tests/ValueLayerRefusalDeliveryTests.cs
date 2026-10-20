using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #778b round four (Codex's B4): a value layer's refusal is never silent,
    /// and that is asserted PAST the production speech gate rather than at the
    /// engine's speech seam.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What went wrong, so the shape of this test makes sense.</b> The
    /// engine's refusal promised that when the tone cannot sound the words are
    /// spoken at every level. Every earlier test of that promise counted the
    /// engine's speech callback, and the engine did call it. But the production
    /// callback tagged the words Terse, and <see cref="ScreenReaderOutput"/>
    /// drops Terse when the operator's verbosity is Off. So at Off, with the
    /// sounds switched off, a refused push to talk produced no tone and no
    /// words, while every test was green. Noel, 2026-10-19 21:18, asked whether
    /// it should speak anyway: <i>"yes, we'd need to let the user know
    /// somehow"</i>.
    /// </para>
    /// <para>
    /// <b>So this drives the production route end to end.</b> The layer is
    /// opened with <see cref="ValueSubLayer.Enter"/>, not the test entry, so
    /// its speech goes through the real <see cref="ScreenReaderOutput.Speak(string, Speech.SpeechIntent, VerbosityLevel, string?, Speech.SpeechCoalesceKind, string?, bool, Func{bool}?, string, int, string)"/>
    /// and its real verbosity gate. What reached the far side of the gate is
    /// read from the silent verification channel's transcript (#171), which
    /// records every utterance with a <c>gated</c> flag, and from the recent
    /// speech history, which is filled only when text reaches the backend.
    /// </para>
    /// <para>
    /// <b>Desk safety.</b> Render is switched off BEFORE anything speaks, and
    /// each test refuses to go on unless the live backend is the diverted one:
    /// with render off Prism is never loaded, so nothing reaches NVDA. If some
    /// earlier test in the process had already brought up a real backend, the
    /// guard fails the test instead of speaking. No earcon player exists in this
    /// assembly; the tone is a counter.
    /// </para>
    /// <para>
    /// <b>What this cannot see:</b> the host's own answer to "will the tone
    /// sound". The three shipped layers wire it to the earcon master switch and
    /// the Commands and confirmations category
    /// (<c>EarconPlayer.IsOn(EarconCategory.CommandsAndConfirmations)</c>);
    /// that wiring is pinned from source in
    /// <c>ValueSubLayerTests.The_earcon_gate_is_the_sound_switches_and_not_the_speech_verbosity</c>,
    /// not driven here.
    /// </para>
    /// </remarks>
    [Collection(SpeechOutputStaticsCollection.Name)]
    public sealed class ValueLayerRefusalDeliveryTests : IDisposable
    {
        private readonly string _path;
        private readonly VerbosityLevel _verbosityBefore;

        public ValueLayerRefusalDeliveryTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "jjflex-refusal-delivery-tests");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".jsonl");

            _verbosityBefore = ScreenReaderOutput.CurrentVerbosity;
            OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _path);
            ScreenReaderOutput.ResetTransientSpeechStateForTest();
        }

        public void Dispose()
        {
            OutputChannelRecorder.Configure(render: false, record: false);
            ScreenReaderOutput.CurrentVerbosity = _verbosityBefore;
            try { File.Delete(_path); } catch { }
        }

        private static void AssertTheDeskIsSafe()
        {
            Assert.False(OutputChannelRecorder.RenderEnabled, "Refusing: render is on, so speech could reach the screen reader");
            Assert.Equal("diverted", ScreenReaderOutput.BackendName);
        }

        private JsonElement[] Speech()
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => JsonDocument.Parse(l).RootElement)
                .Where(e => e.GetProperty("event").GetString() == "speech")
                .ToArray();
        }

        /// <summary>Delivered: the utterance passed the verbosity gate.</summary>
        private bool Delivered(string text) => Speech().Any(e =>
            e.GetProperty("text").GetString() == text && !e.GetProperty("gated").GetBoolean());

        private sealed class Tone
        {
            public int Played;
            public bool Audible;
            public ValueLayerCues Cues => new ValueLayerCues
            {
                Invalid = () => Played++,
                Audible = () => Audible,
            };
        }

        // Each refusal shape the engine has, with the sentence a host gives it.
        // Literal sentences: this is about delivery, not wording.
        private const string Unavailable = "Unavailable marker sentence.";
        private const string UnknownKey = "Unknown key marker sentence.";
        private const string TypedEntry = "Typed entry marker sentence.";
        private const string WrongAxis = "Wrong axis marker sentence.";

        private static ValueSubLayer Open(Tone tone)
        {
            int value = 40;
            return ValueSubLayer.Enter(new ValueSubLayerDefinition
            {
                Id = "refusal-delivery",
                Read = () => value,
                Apply = v => value = v,
                Min = 0, Max = 100, Step = 5, FineStep = 1,
                Number = v => "Level " + v,
                DescribeEntry = (cur, entry) => "Level layer.",
                DescribeClosed = () => "Level layer closed",
                DescribeUnavailableHere = k => k == (Keys.Space | Keys.Control) ? Unavailable : null,
                DescribeUnknownKey = (k, why) => why == ValueLayerRefusal.TypedEntry ? TypedEntry : UnknownKey,
                WrongAxisHint = () => WrongAxis,
                Cues = tone.Cues,
            });
        }

        public static TheoryData<Keys, string> Refusals => new()
        {
            { Keys.Space | Keys.Control, Unavailable },  // push to talk where nothing here keys the radio
            { Keys.Q, UnknownKey },                      // a key the layer does not bind
            { Keys.D4, TypedEntry },                     // a typed number
            { Keys.Left, WrongAxis },                    // the wrong arrow pair for the target
        };

        [Theory]
        [MemberData(nameof(Refusals))]
        public void At_off_with_the_tone_silent_the_refusal_reaches_the_screen_reader(Keys key, string sentence)
        {
            AssertTheDeskIsSafe();
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Critical;   // Off
            var tone = new Tone { Audible = false };
            var layer = Open(tone);
            string marker = "marker " + Guid.NewGuid().ToString("N");
            ScreenReaderOutput.Speak(marker, VerbosityLevel.Critical, true);
            Assert.Equal(marker, ScreenReaderOutput.RecentMessages.FirstOrDefault());

            Assert.Equal(ValueLayerKeyResult.Handled, layer.HandleKey(key));

            Assert.True(layer.IsLive);
            Assert.Equal(1, tone.Played);   // the cue was asked to play; the switches keep it quiet
            Assert.True(Delivered(sentence),
                "the refusal was gated at Off with the tone silent: the operator heard nothing");
            Assert.Equal(sentence, ScreenReaderOutput.RecentMessages.FirstOrDefault());
        }

        [Theory]
        [MemberData(nameof(Refusals))]
        public void At_off_with_the_tone_audible_the_tone_is_the_whole_answer(Keys key, string sentence)
        {
            // The control. Noel's 20:55 ruling is unchanged: when the tone can
            // sound, below Chatty it is the answer and no words follow. Without
            // this, a fix that simply spoke every refusal at Off would pass the
            // test above.
            AssertTheDeskIsSafe();
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Critical;   // Off
            var tone = new Tone { Audible = true };
            var layer = Open(tone);

            Assert.Equal(ValueLayerKeyResult.Handled, layer.HandleKey(key));

            Assert.True(layer.IsLive);
            Assert.Equal(1, tone.Played);
            Assert.DoesNotContain(Speech(), e => e.GetProperty("text").GetString() == sentence);
        }

        [Theory]
        [InlineData(VerbosityLevel.Terse)]
        [InlineData(VerbosityLevel.Chatty)]
        public void Above_off_with_the_tone_silent_the_refusal_is_delivered_too(VerbosityLevel level)
        {
            AssertTheDeskIsSafe();
            ScreenReaderOutput.CurrentVerbosity = level;
            var layer = Open(new Tone { Audible = false });

            layer.HandleKey(Keys.Space | Keys.Control);

            Assert.True(Delivered(Unavailable));
        }

        [Fact]
        public void At_chatty_with_the_tone_audible_the_short_sentence_follows_the_tone()
        {
            AssertTheDeskIsSafe();
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
            var tone = new Tone { Audible = true };
            var layer = Open(tone);

            layer.HandleKey(Keys.Space | Keys.Control);

            Assert.Equal(1, tone.Played);
            Assert.True(Delivered(Unavailable));
        }

        [Fact]
        public void The_layers_other_speech_is_still_gated_at_off()
        {
            // The refusal got its own route; the layer's ordinary speech did
            // not move. Its entry sentence is Terse, so at Off it is recorded
            // as gated, exactly as before round four.
            AssertTheDeskIsSafe();
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Critical;
            Open(new Tone { Audible = false });

            var entry = Speech().Single(e => e.GetProperty("text").GetString() == "Level layer.");
            Assert.True(entry.GetProperty("gated").GetBoolean());
        }
    }
}
