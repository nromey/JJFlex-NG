using System.IO;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Which slice F reads (#642) and what a door says when it keys a slice other
/// than the selected one (#863) are this operator's choices, decided in the
/// root settings. With no radio connected, Settings OK persists only the
/// user-scope list, and the first of these was missing from it: chosen, applied
/// live, and gone when Settings reopened (Codex's check of the batch).
/// </summary>
/// <remarks>
/// <b>Windowless and silent.</b> MTA thread throughout; only config objects and
/// files in a temporary folder.
/// </remarks>
public sealed class OperatorChoicesPersistTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-operatorchoices-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Choices_made_with_no_radio_connected_survive_a_save_and_reload()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string dir = TempDir();
        try
        {
            // A root file written before either setting existed.
            File.WriteAllText(Path.Combine(dir, "audioConfig.xml"),
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><AudioOutputConfig><CwSpeedWpm>25</CwSpeedWpm></AudioOutputConfig>");

            var chosen = AudioOutputConfig.Load(dir);
            Assert.Equal((int)Radios.FrequencyKeySlices.Both, chosen.FrequencyKeyReads);
            Assert.Equal((int)Radios.TransmitElsewhereNotice.SpeakAndTone, chosen.TransmitElsewhereNotice);
            chosen.FrequencyKeyReads = (int)Radios.FrequencyKeySlices.Selected;
            chosen.TransmitElsewhereNotice = (int)Radios.TransmitElsewhereNotice.ToneOnly;
            AudioOutputConfig.PersistUserScope(chosen, dir);

            var reopened = AudioOutputConfig.Load(dir);
            Assert.Equal((int)Radios.FrequencyKeySlices.Selected, reopened.FrequencyKeyReads);
            Assert.Equal((int)Radios.TransmitElsewhereNotice.ToneOnly, reopened.TransmitElsewhereNotice);
            Assert.Equal(25, reopened.CwSpeedWpm); // the rest of the root file as it was
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void A_radios_older_copy_does_not_undo_the_operators_choice_at_connect()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var root = new AudioOutputConfig
        {
            FrequencyKeyReads = (int)Radios.FrequencyKeySlices.Transmit,
            TransmitElsewhereNotice = (int)Radios.TransmitElsewhereNotice.Off,
        };
        var perRadio = new AudioOutputConfig
        {
            FrequencyKeyReads = (int)Radios.FrequencyKeySlices.Both,
            TransmitElsewhereNotice = (int)Radios.TransmitElsewhereNotice.SpeakAndTone,
            CwSpeedWpm = 31,
        };

        perRadio.TakeOperatorChoicesFrom(root);

        Assert.Equal((int)Radios.FrequencyKeySlices.Transmit, perRadio.FrequencyKeyReads);
        Assert.Equal((int)Radios.TransmitElsewhereNotice.Off, perRadio.TransmitElsewhereNotice);
        Assert.Equal(31, perRadio.CwSpeedWpm); // nothing else is taken
    }

    [Fact]
    public void The_connect_and_the_connected_Settings_both_take_the_root_choices()
    {
        // Source pin on the two callers: the method above is only half of it.
        NoiseReductionPerChannelTests.AssertWindowless();
        string repo = Repo();
        string main = File.ReadAllText(Path.Combine(repo, "JJFlexWpf", "MainWindow.xaml.cs"));
        string menu = File.ReadAllText(Path.Combine(repo, "JJFlexWpf", "NativeMenuBar.cs"));
        int load = main.IndexOf("CurrentAudioConfig = AudioOutputConfig.Load(OpenParms.ConfigDirectory);", System.StringComparison.Ordinal);
        int take = main.IndexOf("CurrentAudioConfig.TakeOperatorChoicesFrom(", System.StringComparison.Ordinal);
        int apply = main.IndexOf("CurrentAudioConfig.Apply();", System.StringComparison.Ordinal);
        Assert.True(load >= 0 && take > load && apply > take, "the connect takes the root choices between its load and its Apply");
        Assert.Contains("audioConfig.TakeOperatorChoicesFrom(rootConfig);", menu);
    }

    private static string Repo()
    {
        var d = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
