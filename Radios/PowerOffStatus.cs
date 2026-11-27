namespace Radios
{
    /// <summary>
    /// What Home's status line says when the app stops treating the radio as
    /// powered on (#564). Until this, every cause read "Radio connected — power
    /// off" with "Off" in the power field, which claims a connection and a
    /// powered-off radio when what the app knows may only be that it stopped
    /// hearing from it.
    /// </summary>
    /// <remarks>
    /// <para>The causes are the ones that really reach that status line, read
    /// from the code at batch 29: a profile import's deliberate power-off
    /// (FlexBase.ImportProfile), a restart the operator confirmed (the reboot
    /// routes call MainWindow.powerNowOff), the command loop ending (it ends only
    /// after a disconnect), and the status poll failing while the radio read as
    /// on. The connect-failure sentences already hedge ("It may be powered
    /// off"); the status line now does the same where the cause is not
    /// known. A confirmed local restart reaches the line twice (see
    /// <see cref="CauseLatch"/>).</para>
    /// <para>It does NOT claim to tell "another client took the session" from
    /// "the network dropped": nothing on these paths can.</para>
    /// </remarks>
    public static class PowerOffStatus
    {
        public enum Cause
        {
            /// <summary>The command loop ended: the app's own disconnect, or a
            /// power-off notice whose reason it was not given.</summary>
            Disconnected,
            /// <summary>A profile import powers the radio's state off on a live
            /// connection, deliberately, and powers it on again afterwards.</summary>
            ProfileImport,
            /// <summary>A restart the operator confirmed.</summary>
            Restart,
            /// <summary>Home's status update failed while the radio read as on.
            /// The update covers local display work as well as reading the
            /// radio, so this says only that the status is unavailable, never
            /// that the radio stopped answering.</summary>
            StatusUnreadable,
        }

        /// <summary>
        /// Which cause the next power-off notice carries (#564, repair round 1).
        /// A confirmed local restart produces TWO notices on one connection: the
        /// restart route's own, then the command loop's end when the restart's
        /// disconnect stops it. A one-shot cause lost the restart at the second
        /// notice, so Home ended on "Radio disconnected". The restart is
        /// therefore held for the connection it was confirmed on, and only
        /// there, until that connection's teardown completes or the radio comes
        /// back on; a cause the status update sets stays one-shot.
        /// </summary>
        /// <remarks>Used on the window's dispatcher only. The rig is compared
        /// by reference, so a later connection never inherits a restart.</remarks>
        public sealed class CauseLatch
        {
            private Cause? _once;
            private object? _restartRig;

            /// <summary>The next notice, and only the next, carries this cause.</summary>
            public void Once(Cause cause) => _once = cause;

            /// <summary>A restart was confirmed on <paramref name="rig"/>: every
            /// teardown notice from that connection says so until <see cref="Boundary"/>.</summary>
            public void RestartConfirmed(object? rig) => _restartRig = rig;

            /// <summary>
            /// The cause for a power-off notice now arriving for <paramref name="rig"/>.
            /// A profile import's own notice says so and uses up a pending one-shot;
            /// otherwise a one-shot is used up first, then a restart held for this
            /// same connection; anything else is the command loop ending.
            /// </summary>
            public Cause Take(object? rig, bool importing)
            {
                var once = _once;
                _once = null;
                if (importing) return Cause.ProfileImport;
                if (once.HasValue) return once.Value;
                if (_restartRig != null && ReferenceEquals(_restartRig, rig)) return Cause.Restart;
                return Cause.Disconnected;
            }

            /// <summary>The connection's teardown completed, or the radio came back
            /// on: nothing held so far may label a later notice.</summary>
            public void Boundary()
            {
                _once = null;
                _restartRig = null;
            }
        }

        /// <summary>Home's status sentence for this cause. DRAFT words for
        /// the new causes, for Codex.</summary>
        public static string StatusText(Cause cause) => cause switch
        {
            Cause.ProfileImport => Lexicon.Get("connect.home.status_power_off"),
            Cause.Restart => Lexicon.Get("connect.home.status_power_off"),
            Cause.StatusUnreadable => Lexicon.Get("connect.home.status_not_responding"),
            _ => Lexicon.Get("connect.home.status_disconnected"),
        };

        /// <summary>The power field's value for this cause: off where the
        /// app knows the radio's state is going off, unknown where it does
        /// not.</summary>
        public static string PowerField(Cause cause) => cause switch
        {
            Cause.ProfileImport => Lexicon.Get("connect.home.status_field_power_off"),
            Cause.Restart => Lexicon.Get("connect.home.status_field_power_off"),
            _ => Lexicon.Get("connect.home.status_field_power_unknown"),
        };
    }
}
