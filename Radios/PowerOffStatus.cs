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
    /// known.</para>
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
            /// <summary>Reading the radio's status failed while it read as on.</summary>
            StatusUnreadable,
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
