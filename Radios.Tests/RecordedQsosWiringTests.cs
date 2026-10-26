#nullable enable
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Where the recordings dialog is reached from (#787, Tape Deck track 4): the
/// Audio menu, the flat Ctrl+Alt+Shift+F11 (Noel, 2026-10-24 15:49) and the
/// JJ key's Alt+F (15:50). The help pages are held by LeaderDocCoverageTests.
/// </summary>
/// <remarks>
/// What these cannot see: a key press. Pressing each one, under NVDA and JAWS,
/// is a bench step in the track's report.
/// </remarks>
public sealed class RecordedQsosWiringTests
{
    [Fact]
    public void The_recordings_dialog_is_on_the_audio_menu_and_needs_no_radio()
    {
        var entry = AudioMenuLayout.Entries.Single(e => e.Id == "recorded-qsos");
        Assert.False(entry.NeedsRadio);
        Assert.Equal("Ctrl+Alt+Shift+F11", entry.Accelerator);
        string keys = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "KeyCommands.cs"));
        Assert.Contains("new(Keys.F11 | Keys.Control | Keys.Alt | Keys.Shift, CommandValues.OpenRecordedQsos, KeyScope.Global)", keys);
        Assert.Contains("case Keys.F | Keys.Alt:\n                OpenRecordedQsosHandler();", keys.Replace("\r\n", "\n"));
    }
}
