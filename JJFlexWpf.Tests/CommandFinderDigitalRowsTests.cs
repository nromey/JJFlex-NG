using System;
using System.IO;
using System.Linq;
using System.Reflection;
using JJFlexWpf.Dialogs;
using JJFlexWpf.Tests.Infrastructure;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// #1025 repair one (ruled by Noel 2026-11-11 11:55): the Command Finder's
/// rows for the digital modes read "switch to digital USB" and "switch to
/// digital LSB", and are found by "USB" and "LSB" as well as by the old words
/// ("upper", "lower", "digital", "DIGU", "DIGL").
/// </summary>
/// <remarks>
/// <para>
/// The first build changed the key table's <c>ShortActionLabel</c>, which the
/// Finder never reads: its rows are built in <c>ApplicationEvents.vb</c> from
/// each entry's <c>HelpText</c> (the row's Description), <c>MenuText</c>,
/// <c>Keywords</c> and <c>Group</c>, and searched by
/// <c>CommandFinderDialog.MatchesQuery</c>. So this test goes through those:
/// the real key table, the same projection (pinned against the VB source), and
/// the real matcher.
/// </para>
/// <para>
/// <b>Windowless.</b> MTA thread; a default-only <see cref="KeyCommands"/>
/// with an empty context, and the matcher called as a static method. No
/// dialog is constructed.
/// </para>
/// </remarks>
public sealed class CommandFinderDigitalRowsTests
{
    private static CommandFinderItem Row(CommandValues id)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Assert.True(QuietRun.Silenced, "Refusing: " + QuietRun.Describe());
        Assert.True(TestSettingsRoot.Isolated, "Refusing: " + TestSettingsRoot.Failure);
        var commands = new KeyCommands(new KeyCommandContext(), setDefault: true);
        var kt = commands.CurrentKeys().Single(k => k.KeyDef.Id == id);
        // The projection ApplicationEvents.vb makes (pinned below).
        return new CommandFinderItem
        {
            Description = kt.HelpText,
            Scope = kt.Scope.ToString(),
            Group = kt.Group.ToString(),
            MenuText = kt.MenuText,
            Keywords = kt.Keywords,
            Tag = kt.KeyDef.Id,
        };
    }

    private static bool Matches(CommandFinderItem item, string query)
    {
        var m = typeof(CommandFinderDialog).GetMethod("MatchesQuery", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(m);
        return (bool)m!.Invoke(null, new object[] { item, query })!;
    }

    [Theory]
    [InlineData(CommandValues.ModeDIGU, "Switch to digital USB", "USB", "upper", "DIGU")]
    [InlineData(CommandValues.ModeDIGL, "Switch to digital LSB", "LSB", "lower", "DIGL")]
    public void The_row_reads_the_ruled_label_and_is_found_by_the_new_and_the_old_words(
        CommandValues id, string label, string sideband, string oldWord, string mode)
    {
        var row = Row(id);
        Assert.Equal(label, row.Description);
        Assert.StartsWith(label, row.ToString());   // what a screen reader reads for the row
        foreach (var query in new[] { sideband, sideband.ToLowerInvariant(), oldWord, "digital", mode, mode.ToLowerInvariant() })
            Assert.True(Matches(row, query), "the " + id + " row is not found by \"" + query + "\"");
        // Control: the matcher is not answering yes to everything.
        Assert.False(Matches(row, "amplitude"), "the matcher matched a word the row does not carry");
    }

    [Fact]
    public void The_projection_this_test_makes_is_the_one_the_application_makes()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        string vb = File.ReadAllText(Path.Combine(Repo(), "ApplicationEvents.vb"));
        int callback = vb.IndexOf("GetCommandFinderItemsCallback = Function()", StringComparison.Ordinal);
        Assert.True(callback > 0, "the Command Finder's row builder has moved");
        string body = vb.Substring(callback, 1500);
        Assert.Contains(".Description = kt.HelpText,", body);
        Assert.Contains(".Group = kt.Group.ToString(),", body);
        Assert.Contains(".MenuText = kt.MenuText,", body);
        Assert.Contains(".Keywords = kt.Keywords,", body);
    }

    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
        Assert.NotNull(d);
        return d!.FullName;
    }
}
