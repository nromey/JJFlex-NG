using System.IO;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The callsign check's setting (#1306): one switch for the whole computer,
/// kept in the root audio settings with the other choices that are not a
/// radio's, and off by default. Ruled by Noel 2026-11-24: per computer, not per
/// operator (13:40); off by default (13:43-13:44); on Settings, License, beside
/// TX band lockout (13:46).
/// </summary>
/// <remarks>
/// <para>The trap these hold, hit at least three times before (#642, #855,
/// #856): a field in <see cref="AudioOutputConfig"/> that is not in BOTH
/// <see cref="AudioOutputConfig.PersistUserScope"/> and
/// <see cref="AudioOutputConfig.TakeOperatorChoicesFrom"/> applies live and then
/// reverts at the next start or the next connect.</para>
/// <para><b>Windowless and silent.</b> MTA thread throughout; only config
/// objects, files in a temporary folder, and the source text.</para>
/// </remarks>
public sealed class CallsignCheckSettingTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-callsigncheck-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void It_starts_off_and_a_settings_file_written_before_it_existed_reads_off()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.False(new AudioOutputConfig().RequireCallsignToTransmit, "the check must start off (ruled 2026-11-24)");
        string dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "audioConfig.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><AudioOutputConfig><CwSpeedWpm>27</CwSpeedWpm></AudioOutputConfig>");
            var existing = AudioOutputConfig.Load(dir);
            Assert.Equal(27, existing.CwSpeedWpm);   // positive control: the file was read, not defaulted
            Assert.False(existing.RequireCallsignToTransmit);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Chosen_with_no_radio_it_survives_Settings_and_a_restart_both_ways()
    {
        // The path Settings OK takes with no radio: the dialog edits the root
        // audio settings and the menu persists the computer-wide list to root.
        NoiseReductionPerChannelTests.AssertWindowless();
        string dir = TempDir();
        try
        {
            var chosen = AudioOutputConfig.Load(dir);
            chosen.RequireCallsignToTransmit = true;
            AudioOutputConfig.PersistUserScope(chosen, dir);
            Assert.True(AudioOutputConfig.Load(dir).RequireCallsignToTransmit);

            chosen.RequireCallsignToTransmit = false;
            AudioOutputConfig.PersistUserScope(chosen, dir);
            Assert.False(AudioOutputConfig.Load(dir).RequireCallsignToTransmit);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void At_connect_a_radios_older_copy_does_not_undo_it_either_way()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var perRadio = new AudioOutputConfig { RequireCallsignToTransmit = false, CwSpeedWpm = 31 };
        perRadio.TakeOperatorChoicesFrom(new AudioOutputConfig { RequireCallsignToTransmit = true });
        Assert.True(perRadio.RequireCallsignToTransmit);
        perRadio.TakeOperatorChoicesFrom(new AudioOutputConfig { RequireCallsignToTransmit = false });
        Assert.False(perRadio.RequireCallsignToTransmit);
        Assert.Equal(31, perRadio.CwSpeedWpm);   // nothing else is taken
    }

    [Fact]
    public void Settings_reads_and_writes_it_on_the_License_tab_and_it_is_made_live_at_every_road()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string repo = Repo();
        string dialog = File.ReadAllText(Path.Combine(repo, "JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs"));
        Assert.Contains("RequireCallsignCheckbox.IsChecked = _audioConfig.RequireCallsignToTransmit;", dialog);
        Assert.Contains("_audioConfig.RequireCallsignToTransmit = RequireCallsignCheckbox.IsChecked == true;", dialog);
        // Its name and its Ctrl+F1 text are set, from the Lexicon.
        Assert.Contains("AutomationProperties.SetName(RequireCallsignCheckbox,", dialog);
        Assert.Contains("JJFlexHelp.SetText(RequireCallsignCheckbox, Lexicon.Get(", dialog);

        // On the License tab, right after TX band lockout.
        string xaml = File.ReadAllText(Path.Combine(repo, "JJFlexWpf", "Dialogs", "SettingsDialog.xaml"));
        int license = xaml.IndexOf("Header=\"License\"", System.StringComparison.Ordinal);
        int lockout = xaml.IndexOf("x:Name=\"TxLockoutCheckbox\"", System.StringComparison.Ordinal);
        int box = xaml.IndexOf("x:Name=\"RequireCallsignCheckbox\"", System.StringComparison.Ordinal);
        int country = xaml.IndexOf("x:Name=\"CountryCombo\"", System.StringComparison.Ordinal);
        Assert.True(license >= 0 && lockout > license && box > lockout && box < country,
            "the callsign check is not beside TX band lockout on the License tab");

        // Live: Apply (Settings OK and every connect), and startup from root.
        string config = File.ReadAllText(Path.Combine(repo, "JJFlexWpf", "AudioOutputConfig.cs"));
        int apply = config.IndexOf("public void Apply()", System.StringComparison.Ordinal);
        int live = config.IndexOf("Radios.FlexBase.CallsignCheckOn = RequireCallsignToTransmit;", System.StringComparison.Ordinal);
        Assert.True(apply > 0 && live > apply, "Apply does not make the switch live");
        Assert.Contains("rootConfig.RequireCallsignToTransmit = chosen.RequireCallsignToTransmit;", config);
        Assert.Contains("RequireCallsignToTransmit = root.RequireCallsignToTransmit;", config);
        string main = File.ReadAllText(Path.Combine(repo, "JJFlexWpf", "MainWindow.xaml.cs"));
        Assert.Contains("Radios.FlexBase.CallsignCheckOn = userConfig.RequireCallsignToTransmit;", main);
    }

    private static string Repo()
    {
        var d = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
