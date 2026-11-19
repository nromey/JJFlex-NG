using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// Sprint 49 Track N2: the receive follow-ups from the integration walk at
/// f3beb60b. Windowless: the noise controller over fakes (an MTA test thread,
/// asserted), and for the Settings list, controls built unhosted on a private
/// STA thread with no presentation source.
/// </summary>
public sealed partial class ReceiveWalkFollowUpTests
{
    public ReceiveWalkFollowUpTests() => NoiseReductionPerChannelTests.AssertWindowless();

    private static void RunOnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The test thread did not finish.");
        if (failure != null) throw failure;
    }

    private static string Source(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, Path.Combine(path))).Replace("\r\n", "\n");
    }

    private static string Body(string source, string from, string to)
    {
        int at = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(at >= 0, "not found: " + from);
        int end = source.IndexOf(to, at + from.Length, StringComparison.Ordinal);
        Assert.True(end > at, "not found after it: " + to);
        return source.Substring(at, end - at);
    }
}
