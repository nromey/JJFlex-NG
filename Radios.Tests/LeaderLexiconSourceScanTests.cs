using System;
using Xunit;

namespace Radios.Tests;

// LEXICON_SCANNER_EXEMPT: this test quotes synthetic source, including a
// deliberate missing key. Real inventory call sites remain scanned.
public sealed class LeaderLexiconSourceScanTests
{
    [Fact]
    public void Keyboard_scanner_reads_lexicon_descriptions_and_refuses_unknown_keys()
    {
        const string sample = "FixedKeyEntry[] LeaderCommands = { new(\"Leader\",\"JJ key\",\"Ctrl+J, Ctrl+X\",Radios.Lexicon.Get(\"audio.full_duplex.command\"),new[] { \"toggle\" },\"Radio\",\"Audio\") };";
        var entry = Assert.Single(LeaderSourceScan.InventoryEntries(sample));
        Assert.Equal("Ctrl+J, Ctrl+X",entry.Display); Assert.Equal("Toggle full duplex",entry.Description);
        Assert.ThrowsAny<Exception>(() => LeaderSourceScan.InventoryEntries(sample.Replace("audio.full_duplex.command","missing.key.for.test")));
    }

}
