namespace Radios
{
    /// <summary>
    /// Whether the title still names a session that has gone (#848). The title
    /// moves to no radio only when it names a session AND the connection is
    /// down. So a power report on a live connection, such as a profile import,
    /// changes nothing, and neither does a connection falling during a connect
    /// that has never put a slice on the title, where a write would be one more
    /// thing spoken over the connect (#550).
    /// </summary>
    public sealed class SessionTitle
    {
        private bool _namesASession;

        /// <summary>The title has just been written with a slice on it.</summary>
        public void SliceTitleShown() => _namesASession = true;

        /// <summary>The title has just been written with no radio.</summary>
        public void NoRadioTitleShown() => _namesASession = false;

        /// <summary>Should the title go to no radio now?</summary>
        public bool OwesNoRadioTitle(bool connectedNow) => _namesASession && !connectedNow;
    }

    /// <summary>
    /// The main window's title with no radio. One home, because a screen
    /// reader reads this line on Alt+Tab and on window focus, and every route
    /// to "no radio" must say the same thing: a fresh launch, a disconnect, a
    /// lost radio and the rescue page.
    /// </summary>
    public static class MainWindowTitle
    {
        public const string Product = "JJ Flexible Radio Access";

        /// <param name="stationName">The station, or empty when there is none yet.</param>
        /// <param name="programInstance">Which running copy this is: a second
        /// copy carries its number, as the station name always has.</param>
        public static string NoRadio(string stationName, int programInstance)
        {
            string station = (stationName ?? "").Trim();
            if (station.Length > 0 && programInstance > 1) station += programInstance;
            return Product + (station.Length > 0 ? " " + station : "") + " — no radio connected";
        }
    }
}
