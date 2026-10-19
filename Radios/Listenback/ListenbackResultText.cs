using System.Globalization;

namespace Radios.Listenback;

public static class ListenbackResultText
{
    public static string Ratio(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio) && double.IsFinite(ratio)
        ? ratio.ToString("F4", CultureInfo.CurrentCulture) : Lexicon.Get("audio.iq.unknown");
}
