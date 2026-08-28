using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 37 Track E, #161 and #182: the two rules Noel handed down on
    /// 2026-08-27, enforced at the <see cref="ScreenReaderOutput.SendCwText"/>
    /// choke point where no caller can forget them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The trigger rule (#161):</b> CW is driven by message changes or
    /// slice changes, and the test is a comparison against WHAT WAS LAST
    /// SENT, not a list of events that fire — those look identical until
    /// something fires twice. The slice is embedded in the message text
    /// ("SL A USB"), so text equality is state equality, and one string
    /// comparison carries the whole rule.
    /// </para>
    /// <para>
    /// <b>Close, do not queue (#182):</b> a message that does send first
    /// supersedes whatever is pending, through
    /// <see cref="ScreenReaderOutput.SupersedePendingCw"/>. The mechanics of
    /// WHERE a superseded sequence stops (character boundary, never
    /// mid-symbol) live on the JJFlexWpf side and are pinned by
    /// JJFlexWpf.Tests; what is pinned here is that the delegate fires for
    /// every real send, before the play, and never for a suppressed one.
    /// </para>
    /// <para>
    /// These tests use unique message texts throughout because the CW
    /// history and the last-sent comparison are process-wide statics shared
    /// with any other test that sends CW; a colliding literal would couple
    /// two tests through state neither can see.
    /// </para>
    /// </remarks>
    public sealed class CwNotificationTriggerTests : IDisposable
    {
        private readonly Func<string, Task>? _savedPlay = ScreenReaderOutput.PlayCwText;
        private readonly Action? _savedSupersede = ScreenReaderOutput.SupersedePendingCw;

        private readonly List<string> _events = new();

        public CwNotificationTriggerTests()
        {
            ScreenReaderOutput.ResetCwLastSent();
            ScreenReaderOutput.PlayCwText = text =>
            {
                _events.Add("play:" + text);
                return Task.CompletedTask;
            };
            ScreenReaderOutput.SupersedePendingCw = () => _events.Add("supersede");
        }

        public void Dispose()
        {
            ScreenReaderOutput.PlayCwText = _savedPlay;
            ScreenReaderOutput.SupersedePendingCw = _savedSupersede;
            ScreenReaderOutput.ResetCwLastSent();
        }

        [Fact]
        public void AnUnchangedMessageDoesNotResend()
        {
            ScreenReaderOutput.SendCwText("SL 4/4 T1");
            ScreenReaderOutput.SendCwText("SL 4/4 T1");

            Assert.Equal(new[] { "supersede", "play:SL 4/4 T1" }, _events);
        }

        [Fact]
        public void AChangedMessageSendsAndARevisitIsAChange()
        {
            // A -> B -> A: the third send matches the FIRST message, but not
            // the LAST one sent, so it is news. This is exactly the case an
            // event-list design gets wrong and the last-sent comparison gets
            // right for free.
            ScreenReaderOutput.SendCwText("SL A USB T2");
            ScreenReaderOutput.SendCwText("SL B CW T2");
            ScreenReaderOutput.SendCwText("SL A USB T2");

            Assert.Equal(3, _events.FindAll(e => e.StartsWith("play:")).Count);
        }

        [Fact]
        public void TheComparisonResetsWithTheSession()
        {
            // Disconnect clears the comparison: a reconnect deserves its
            // opening census even when the numbers match last session's.
            ScreenReaderOutput.SendCwText("SL 2/4 T3");
            ScreenReaderOutput.ResetCwLastSent();
            ScreenReaderOutput.SendCwText("SL 2/4 T3");

            Assert.Equal(2, _events.FindAll(e => e.StartsWith("play:")).Count);
        }

        [Fact]
        public void EverySendSupersedesBeforeItPlays()
        {
            ScreenReaderOutput.SendCwText("SL C AM T4");

            Assert.Equal(new[] { "supersede", "play:SL C AM T4" }, _events);
        }

        [Fact]
        public void ASuppressedSendDoesNotSupersede()
        {
            // The duplicate must not close what is pending either: nothing is
            // being said, so nothing already saying it should be cut.
            ScreenReaderOutput.SendCwText("SL D FM T5");
            _events.Clear();

            ScreenReaderOutput.SendCwText("SL D FM T5");

            Assert.Empty(_events);
        }

        [Fact]
        public void ASuppressedSendLeavesTheHistoryAlone()
        {
            ScreenReaderOutput.SendCwText("SL 1/2 T6");
            int before = ScreenReaderOutput.RecentCwMessages.Count;

            ScreenReaderOutput.SendCwText("SL 1/2 T6");

            Assert.Equal(before, ScreenReaderOutput.RecentCwMessages.Count);
        }

        [Fact]
        public void WithNoPlayerWiredNothingIsRememberedAsSent()
        {
            // An unwired build (CW side not started) must not poison the
            // comparison: the first message after wiring is still news.
            ScreenReaderOutput.PlayCwText = null;
            ScreenReaderOutput.SendCwText("SL 3/4 T7");

            ScreenReaderOutput.PlayCwText = text =>
            {
                _events.Add("play:" + text);
                return Task.CompletedTask;
            };
            ScreenReaderOutput.SendCwText("SL 3/4 T7");

            Assert.Contains("play:SL 3/4 T7", _events);
        }
    }
}
