namespace JJFlexWpf;

/// <summary>
/// Data class for a single row in the Recent QSOs grid.
/// </summary>
public class RecentQsoRow
{
    public string Time { get; set; } = "";
    public string Call { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Freq { get; set; } = "";
    public string RSTSent { get; set; } = "";
    public string RSTRcvd { get; set; } = "";
    public string Name { get; set; } = "";

    public RecentQsoRow() { }

    public RecentQsoRow(string time, string call, string mode, string freq,
                        string rstSent, string rstRcvd, string name)
    {
        Time = time;
        Call = call;
        Mode = mode;
        Freq = freq;
        RSTSent = rstSent;
        RSTRcvd = rstRcvd;
        Name = name;
    }
}
