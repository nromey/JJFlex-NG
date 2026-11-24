#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// What a SmartLink sign-in that ended without a token SAYS (#1244), run
    /// through the rig's one ending, <c>SignInEndedWithoutAToken</c>, which every
    /// such ending of <c>PerformNewLogin</c> returns. The operator's cancel says
    /// "Login cancelled" once and records the cancel; every other ending says
    /// nothing here and records none.
    /// </summary>
    /// <remarks>
    /// The speech recorder runs with rendering OFF, so nothing reaches a screen
    /// reader; the transcript is a temporary file. No window is built: the
    /// method is handed how the windows closed.
    /// </remarks>
    [Collection(SpeechOutputStaticsCollection.Name)]
    public sealed class SignInCancelSpeechTests : IDisposable
    {
        private readonly string _path;
        private readonly FlexBase _rig = new(new FlexBase.OpenParms { ProgramName = "JJFlexTests" });

        public SignInCancelSpeechTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "jjflex-recorder-tests");
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".jsonl");
            ScreenReaderOutput.ResetTransientSpeechStateForTest();
            OutputChannelRecorder.Configure(render: false, record: true, explicitPath: _path);
            // Terse: the level the words are said at, so a pass here is a pass
            // at the operator's ordinary setting.
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Terse;
        }

        public void Dispose()
        {
            OutputChannelRecorder.Configure(render: false, record: false);
            ScreenReaderOutput.CurrentVerbosity = VerbosityLevel.Chatty;
            try { File.Delete(_path); } catch { }
            try { _rig.Dispose(); } catch { }
        }

        private JsonElement[] Spoken()
        {
            if (!File.Exists(_path)) return Array.Empty<JsonElement>();
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => JsonDocument.Parse(l).RootElement)
                .Where(e => e.GetProperty("event").GetString() == "speech")
                .ToArray();
        }

        [Theory]
        [InlineData(DialogResult.Cancel, null, false)]                 // JJ Flexible's own form
        [InlineData(DialogResult.Retry, DialogResult.Cancel, true)]    // the browser page, closed by the operator
        public void The_operators_cancel_says_Login_cancelled_once_and_records_it(
            DialogResult native, DialogResult? browser, bool closedByOperator)
        {
            Assert.Null(_rig.SignInEndedWithoutAToken(native, browser, closedByOperator));

            var said = Assert.Single(Spoken());
            Assert.Equal("Login cancelled", said.GetProperty("text").GetString());
            Assert.False(said.GetProperty("gated").GetBoolean(), "the words were dropped by the verbosity filter");
            Assert.True(said.GetProperty("interrupt").GetBoolean());
            Assert.True(_rig.SignInCancelledByOperator);
        }

        [Theory]
        [InlineData(DialogResult.Abort, null, false)]                  // a failure on the form's own thread
        [InlineData(DialogResult.Retry, DialogResult.Cancel, false)]   // an error box, or a failed token exchange
        [InlineData(DialogResult.Retry, DialogResult.Abort, false)]    // WebView2 could not start
        public void Any_other_ending_says_nothing_and_is_not_a_cancel(
            DialogResult native, DialogResult? browser, bool closedByOperator)
        {
            Assert.Null(_rig.SignInEndedWithoutAToken(native, browser, closedByOperator));

            Assert.Empty(Spoken());
            Assert.False(_rig.SignInCancelledByOperator);
        }

        /// <summary>
        /// A rig whose speech is suppressed (a background connection test) says
        /// nothing, but the cancel is still recorded, so its callers still keep
        /// their failure words back.
        /// </summary>
        [Fact]
        public void Suppressed_speech_still_records_the_cancel()
        {
            _rig.SuppressSpeech = true;

            Assert.Null(_rig.SignInEndedWithoutAToken(DialogResult.Cancel, null, false));

            Assert.Empty(Spoken());
            Assert.True(_rig.SignInCancelledByOperator);
        }

        /// <summary>The next ending decides afresh: a cancel is not sticky.</summary>
        [Fact]
        public void A_later_ending_that_is_not_a_cancel_clears_it()
        {
            _rig.SignInEndedWithoutAToken(DialogResult.Cancel, null, false);
            Assert.True(_rig.SignInCancelledByOperator);

            _rig.SignInEndedWithoutAToken(DialogResult.Abort, null, false);

            Assert.False(_rig.SignInCancelledByOperator);
            Assert.Single(Spoken());   // only the first said anything
        }
    }
}
