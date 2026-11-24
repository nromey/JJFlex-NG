namespace Radios
{
    /// <summary>
    /// How a SmartLink account pass the radio picker started ended: the
    /// answer its discovery and refresh callbacks hand back (#1244).
    /// </summary>
    /// <remarks>
    /// <para>This was a Boolean, "is the SmartLink session live", and a
    /// Boolean cannot tell the operator closing the sign-in window from
    /// SmartLink being unreachable. So a pending connect (Enter on a
    /// remembered remote radio before the remote list was live) heard "Login
    /// cancelled" and then "Could not reach SmartLink to look for" the
    /// radio: the failure the cancel was meant to replace.</para>
    /// <para>Only the operator closing a sign-in window is
    /// <see cref="SignInCancelled"/>. An error, a refused sign-in or a
    /// missing token stays <see cref="NotLive"/>.</para>
    /// </remarks>
    public enum RemotePassOutcome
    {
        /// <summary>The session is live, so the remote list is.</summary>
        Live,

        /// <summary>The pass failed: no session, for any reason but the operator's cancel.</summary>
        NotLive,

        /// <summary>The operator closed a sign-in window during the pass. Nothing failed; the pass was abandoned.</summary>
        SignInCancelled,
    }

    /// <summary>The rule that turns a pass's two facts into its <see cref="RemotePassOutcome"/>.</summary>
    public static class RemotePassOutcomes
    {
        /// <summary>
        /// The operator's cancel decides first: they said stop, so a pending
        /// connect ends quietly even if some session happens to be live.
        /// Otherwise a live session is <see cref="RemotePassOutcome.Live"/>
        /// and anything else is <see cref="RemotePassOutcome.NotLive"/>.
        /// </summary>
        public static RemotePassOutcome For(bool sessionLive, bool signInCancelledByOperator) =>
            signInCancelledByOperator ? RemotePassOutcome.SignInCancelled
            : sessionLive ? RemotePassOutcome.Live
            : RemotePassOutcome.NotLive;
    }
}
