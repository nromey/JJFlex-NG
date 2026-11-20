#nullable enable

using System;
using Radios.SmartLink;

namespace Radios
{
    /// <summary>
    /// The one voice of the connect walk: every move from a failed path or a
    /// failed SmartLink method to the next is said here, once, in the walk's
    /// existing sentence, "Could not connect over {legName}. Trying
    /// {nextName}."
    /// </summary>
    /// <remarks>
    /// <para><b>One implementation, three callers.</b> The selector's first
    /// pass, the walk resumed after a failed open (both in the app's connect
    /// driver), and the SmartLink method walk inside the radio layer
    /// (<see cref="SmartLinkLeg"/>). A fallback must sound the same whichever
    /// of them made it, and saying it in three places is how one of them ends
    /// up saying it twice or not at all.</para>
    /// <para><b>Critical, and interrupting.</b> A path that failed and another
    /// being tried is a state change the operator must hear at any verbosity.
    /// Because the call interrupts, nothing may be said over it: no
    /// "connected" before the connection is proven, no status line. The
    /// SmartLink leg holds its "connected" until proof for that reason.</para>
    /// <para>The speech goes out through <see cref="Speak"/>, which a test
    /// replaces to hear exactly what was said and how many times.</para>
    /// </remarks>
    public static class ConnectWalkAnnouncer
    {
        /// <summary>
        /// The speech seam. The product's is the screen reader at Critical
        /// with interrupt; a test supplies its own and puts this one back.
        /// </summary>
        public static Action<string> Speak { get; set; } = SpeakToScreenReader;

        /// <summary>The product's seam, for a test to restore.</summary>
        public static void SpeakToScreenReader(string sentence) =>
            ScreenReaderOutput.Speak(sentence, VerbosityLevel.Critical, true);

        /// <summary>The operator-facing name of one path.</summary>
        public static string LegName(ConnectPathKind path) =>
            path == ConnectPathKind.SmartLink
                ? Lexicon.Get("connect.walk.leg_smartlink")
                : Lexicon.Get("connect.walk.leg_local");

        /// <summary>The operator-facing name of one SmartLink method. DRAFT words.</summary>
        public static string MethodLegName(SmartLinkMethod method)
        {
            switch (method)
            {
                case SmartLinkMethod.ManualPorts: return Lexicon.Get("connect.walk.leg_smartlink_manual");
                case SmartLinkMethod.UpnpPorts: return Lexicon.Get("connect.walk.leg_smartlink_upnp");
                default: return Lexicon.Get("connect.walk.leg_smartlink_punch");
            }
        }

        /// <summary>The sentence for one move, assembled. Says nothing.</summary>
        public static string MoveSentence(string legName, string nextName) =>
            Lexicon.Get("connect.walk.falling_back", ("legName", legName), ("nextName", nextName));

        /// <summary>Say one move between paths. Returns the sentence said.</summary>
        public static string SayMove(ConnectPathKind from, ConnectPathKind to) =>
            Say(MoveSentence(LegName(from), LegName(to)));

        /// <summary>Say one move between SmartLink methods. Returns the sentence said.</summary>
        public static string SayMethodMove(SmartLinkMethod from, SmartLinkMethod to) =>
            Say(MoveSentence(MethodLegName(from), MethodLegName(to)));

        private static string Say(string sentence)
        {
            Speak(sentence);
            return sentence;
        }
    }
}
