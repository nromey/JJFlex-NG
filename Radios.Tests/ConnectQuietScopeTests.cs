#nullable enable

using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The connect-flow quiet scope's state machine (#395) — every exit
    /// pinned. Track D's original implementation shipped this logic as loose
    /// fields inside MainWindow with zero assertions over it, and the first
    /// stuck scope reached the operator as a two-minute total lockout
    /// (2026-08-30, three times in one day). A scope that opens must close on
    /// every path, and these are the assertions nobody had.
    ///
    /// The walks below mirror the real call sites by name: the menu command
    /// and the rescue button are DOORS (they bracket the whole flow in a
    /// try/finally), WireRadioEvents is the inner begin every connect leg
    /// makes, PowerNowOn and the teardown are the inner ends.
    /// </summary>
    public class ConnectQuietScopeTests
    {
        // ── Opening ──────────────────────────────────────────────────────

        [Fact]
        public void FreshBeginOpensTheScope()
        {
            var scope = new ConnectQuietScope();

            var kind = scope.Begin();

            Assert.Equal(ConnectQuietScope.BeginKind.Fresh, kind);
            Assert.True(scope.IsQuiet);
            Assert.False(scope.SawPowerOn);
            Assert.Equal(0, scope.DoorDepth);
        }

        [Fact]
        public void SecondBeginExtendsRatherThanReopens()
        {
            var scope = new ConnectQuietScope();
            scope.Begin(door: true);

            var kind = scope.Begin(); // WireRadioEvents inside the menu flow

            Assert.Equal(ConnectQuietScope.BeginKind.Extended, kind);
            Assert.True(scope.IsQuiet);
            Assert.Equal(1, scope.DoorDepth); // the door is still the door
        }

        [Fact]
        public void EveryBeginBumpsTheGeneration()
        {
            var scope = new ConnectQuietScope();
            int g0 = scope.Generation;

            scope.Begin();
            scope.Begin(); // extending still bumps — that is what invalidates
                           // a finish posted by the leg being superseded

            Assert.Equal(g0 + 2, scope.Generation);
        }

        // ── The doors ────────────────────────────────────────────────────

        [Fact]
        public void InnerEndWhileADoorIsOpenIsDeferred()
        {
            // PowerNowOn's end request arrives while the menu door's flow is
            // still pumping messages. Honoring it would run the finish while
            // the Connecting window is still up.
            var scope = new ConnectQuietScope();
            scope.Begin(door: true);

            var decision = scope.RequestEnd(); // power-on complete (inner)

            Assert.Equal(ConnectQuietScope.EndDecision.DeferredToDoor, decision);
            Assert.True(scope.IsQuiet);
        }

        [Fact]
        public void TheDoorsOwnEndMakesTheFinishDue()
        {
            var scope = new ConnectQuietScope();
            scope.Begin(door: true);

            var decision = scope.RequestEnd(door: true); // the finally

            Assert.Equal(ConnectQuietScope.EndDecision.FinishDue, decision);
        }

        [Fact]
        public void NestedDoorsEachGetTheirOwnEnd()
        {
            var scope = new ConnectQuietScope();
            scope.Begin(door: true);  // menu
            scope.Begin(door: true);  // a nested bracketing flow

            Assert.Equal(ConnectQuietScope.EndDecision.DeferredToDoor,
                scope.RequestEnd(door: true));
            Assert.Equal(ConnectQuietScope.EndDecision.FinishDue,
                scope.RequestEnd(door: true));
        }

        [Fact]
        public void EndWithNoScopeOpenIsNotOpen()
        {
            var scope = new ConnectQuietScope();

            Assert.Equal(ConnectQuietScope.EndDecision.NotOpen, scope.RequestEnd());
            Assert.Equal(ConnectQuietScope.EndDecision.NotOpen, scope.RequestEnd(door: true));
            Assert.Equal(0, scope.DoorDepth); // no underflow
        }

        [Fact]
        public void ADoorEndAfterTheFailsafeClosedTheScopeIsHarmless()
        {
            // The failsafe closes a scope the menu door still holds; minutes
            // later the operator closes the picker and the door's finally
            // runs. That end must be a clean no-op, not a negative depth or a
            // resurrected finish.
            var scope = new ConnectQuietScope();
            scope.Begin(door: true);
            scope.Close(); // the failsafe's forced finish

            var decision = scope.RequestEnd(door: true);

            Assert.Equal(ConnectQuietScope.EndDecision.NotOpen, decision);
            Assert.Equal(0, scope.DoorDepth);
        }

        [Fact]
        public void AFreshBeginResetsDoorDepthLeftByAForcedClose()
        {
            // A failsafe-closed scope can leave a door count behind (the
            // door's finally has not run yet). A FRESH scope — the operator
            // pressing Enter on a radio, WireRadioEvents beginning — must not
            // inherit it, or its inner end would defer to a door that
            // belongs to a scope that no longer exists.
            var scope = new ConnectQuietScope();
            scope.Begin(door: true);
            scope.Close();          // failsafe

            scope.Begin();          // radio events wired — a new scope

            Assert.Equal(0, scope.DoorDepth);
            Assert.Equal(ConnectQuietScope.EndDecision.FinishDue, scope.RequestEnd());
        }

        // ── The posted finish and the generation gate ────────────────────

        [Fact]
        public void APostedFinishRunsForItsOwnGeneration()
        {
            var scope = new ConnectQuietScope();
            scope.Begin();
            Assert.Equal(ConnectQuietScope.EndDecision.FinishDue, scope.RequestEnd());
            int gen = scope.Generation;

            Assert.True(scope.ShouldRunPostedFinish(gen));
        }

        [Fact]
        public void ANewerLegInvalidatesAPostedFinish()
        {
            // The retry ladder unwires (posting a finish) and rewires before
            // the Background queue drains. The stale finish must not end the
            // new leg's scope.
            var scope = new ConnectQuietScope();
            scope.Begin();
            scope.RequestEnd();
            int staleGen = scope.Generation;
            scope.Begin(); // the next leg

            Assert.False(scope.ShouldRunPostedFinish(staleGen));
            Assert.True(scope.ShouldRunPostedFinish(scope.Generation));
        }

        [Fact]
        public void APostedFinishAfterTheScopeClosedIsInvalid()
        {
            var scope = new ConnectQuietScope();
            scope.Begin();
            scope.RequestEnd();
            int gen = scope.Generation;
            scope.Close(); // the failsafe got there first

            Assert.False(scope.ShouldRunPostedFinish(gen));
        }

        // ── The finish ───────────────────────────────────────────────────

        [Fact]
        public void FinishWithoutPowerOnRunsTheLanding()
        {
            // The cancelled picker, the refused connect, the failed walk, the
            // rescued stuck scope: nothing powered on, so nothing else will
            // tell the operator where they are.
            var scope = new ConnectQuietScope();
            scope.Begin();

            Assert.Equal(ConnectQuietScope.FinishKind.NoPowerOnLanding, scope.DecideFinish());
        }

        [Fact]
        public void FinishAfterPowerOnNormalizesQuietly()
        {
            var scope = new ConnectQuietScope();
            scope.Begin();
            scope.NotePowerOn();

            Assert.Equal(ConnectQuietScope.FinishKind.PowerOnQuietNormalize, scope.DecideFinish());
        }

        [Fact]
        public void FinishWithNoScopeIsNotOpen()
        {
            var scope = new ConnectQuietScope();

            Assert.Equal(ConnectQuietScope.FinishKind.NotOpen, scope.DecideFinish());
        }

        [Fact]
        public void CloseEndsTheScopeAndIsIdempotent()
        {
            var scope = new ConnectQuietScope();
            scope.Begin();
            scope.NotePowerOn();

            scope.Close();
            scope.Close();

            Assert.False(scope.IsQuiet);
            Assert.False(scope.SawPowerOn);
            Assert.Equal(ConnectQuietScope.FinishKind.NotOpen, scope.DecideFinish());
        }

        [Fact]
        public void APowerOnNoteWithNoScopeOpenRecordsNothing()
        {
            // A re-raised power event mid-session must not make the NEXT
            // scope believe a radio arrived inside it.
            var scope = new ConnectQuietScope();
            scope.NotePowerOn();

            scope.Begin();

            Assert.False(scope.SawPowerOn);
            Assert.Equal(ConnectQuietScope.FinishKind.NoPowerOnLanding, scope.DecideFinish());
        }

        [Fact]
        public void PowerOnDoesNotLeakIntoTheNextScope()
        {
            var scope = new ConnectQuietScope();
            scope.Begin();
            scope.NotePowerOn();
            scope.Close();

            scope.Begin();

            Assert.Equal(ConnectQuietScope.FinishKind.NoPowerOnLanding, scope.DecideFinish());
        }

        // ── Whole-flow walks, one per real exit from the menu door ───────

        [Fact]
        public void Walk_MenuConnect_PickerCancelled()
        {
            // The exit the operator takes most often: Radio → Connect, browse,
            // Escape. The door's finally is the only end this path ever calls.
            var scope = new ConnectQuietScope();
            scope.Begin(door: true);                      // menu command

            var decision = scope.RequestEnd(door: true);  // the finally
            Assert.Equal(ConnectQuietScope.EndDecision.FinishDue, decision);
            Assert.True(scope.ShouldRunPostedFinish(scope.Generation));
            Assert.Equal(ConnectQuietScope.FinishKind.NoPowerOnLanding, scope.DecideFinish());

            scope.Close();
            Assert.False(scope.IsQuiet);
        }

        [Fact]
        public void Walk_MenuConnect_SuccessfulConnect()
        {
            var scope = new ConnectQuietScope();
            scope.Begin(door: true);                      // menu command
            scope.Begin();                                // radio events wired
            scope.NotePowerOn();                          // PowerNowOn
            Assert.Equal(ConnectQuietScope.EndDecision.DeferredToDoor,
                scope.RequestEnd());                      // power-on complete

            var decision = scope.RequestEnd(door: true);  // the finally
            Assert.Equal(ConnectQuietScope.EndDecision.FinishDue, decision);
            Assert.Equal(ConnectQuietScope.FinishKind.PowerOnQuietNormalize, scope.DecideFinish());

            scope.Close();
            Assert.False(scope.IsQuiet);
        }

        [Fact]
        public void Walk_MenuConnect_FailedConnectWithRetryLeg()
        {
            var scope = new ConnectQuietScope();
            scope.Begin(door: true);                      // menu command
            scope.Begin();                                // radio events wired
            Assert.Equal(ConnectQuietScope.EndDecision.DeferredToDoor,
                scope.RequestEnd());                      // radio events unwired
            scope.Begin();                                // rewired: retry leg
            Assert.Equal(ConnectQuietScope.EndDecision.DeferredToDoor,
                scope.RequestEnd());                      // unwired again, still failed

            var decision = scope.RequestEnd(door: true);  // the finally
            Assert.Equal(ConnectQuietScope.EndDecision.FinishDue, decision);
            Assert.Equal(ConnectQuietScope.FinishKind.NoPowerOnLanding, scope.DecideFinish());
        }

        [Fact]
        public void Walk_AutoConnect_NoDoorAtAll()
        {
            // Auto-connect and the retry ladder's fresh legs come through
            // WireRadioEvents alone; the teardown's end is honored at once.
            var scope = new ConnectQuietScope();
            scope.Begin();                                // radio events wired

            Assert.Equal(ConnectQuietScope.EndDecision.FinishDue, scope.RequestEnd());
        }

        // ── The deadlines are part of the contract ───────────────────────

        [Fact]
        public void TheFailsafeIsSecondsNotMinutes()
        {
            // The scope protects about one second of window churn, and the
            // menu route's own run-up (discovery settling plus the picker
            // arriving) measures about 6.2 seconds — so the floor. The
            // ceiling is the defect the 2026-08-30 lockouts named: a blind
            // operator cannot tell a silent application from a crashed one,
            // and 120 seconds of that was experienced as a hang and answered
            // with a process kill. Anyone raising this past a handful of
            // seconds is reintroducing that outage and must argue with this
            // test first.
            Assert.InRange(ConnectQuietScope.FailsafeMs, 7_000, 15_000);
        }

        [Fact]
        public void TheStrandedFocusRescueIsSubSecond()
        {
            // Long enough for the flow's next window to arrive and take the
            // foreground (the Connecting window shows immediately; the
            // picker's activation follows the settling window within a
            // beat); short enough that a stranded keyboard is repaired
            // before the operator concludes the application died.
            Assert.InRange(ConnectQuietScope.StrandedFocusRescueDelayMs, 250, 1_500);
        }
    }
}
