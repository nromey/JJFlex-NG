using System.IO;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #1339: speech verbosity chosen in Settings with no radio connected was
/// applied live and then lost at the next start, though Settings said it saved,
/// because the root save (<see cref="AudioOutputConfig.PersistUserScope"/>) did
/// not carry it. It is now in that list and in
/// <see cref="AudioOutputConfig.TakeOperatorChoicesFrom"/>, so a connect does
/// not load the radio's older copy over it.
/// </summary>
/// <remarks>
/// <b>Windowless and silent.</b> MTA thread throughout; only config objects and
/// files in a temporary folder, never the operator's settings.
/// </remarks>
public sealed class VerbosityWithNoRadioTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jjflex-verbosity-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Off_chosen_with_no_radio_is_kept_through_Settings_and_a_restart()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string dir = TempDir();
        try
        {
            var chosen = AudioOutputConfig.Load(dir);
            Assert.Equal(2, chosen.SpeechVerbosity);   // Chatty, the default
            chosen.SpeechVerbosity = 0;                // Off: only Critical is spoken
            AudioOutputConfig.PersistUserScope(chosen, dir);
            Assert.Equal(0, AudioOutputConfig.Load(dir).SpeechVerbosity);

            chosen.SpeechVerbosity = 1;                // Terse
            AudioOutputConfig.PersistUserScope(chosen, dir);
            Assert.Equal(1, AudioOutputConfig.Load(dir).SpeechVerbosity);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void At_connect_a_radios_older_copy_does_not_undo_it()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        var root = new AudioOutputConfig { SpeechVerbosity = 0 };
        var perRadio = new AudioOutputConfig { SpeechVerbosity = 2, CwSpeedWpm = 31 };
        perRadio.TakeOperatorChoicesFrom(root);
        Assert.Equal(0, perRadio.SpeechVerbosity);
        Assert.Equal(31, perRadio.CwSpeedWpm);   // positive control: nothing else is taken
    }
}
