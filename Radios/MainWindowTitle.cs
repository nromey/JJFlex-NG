namespace Radios
{
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
