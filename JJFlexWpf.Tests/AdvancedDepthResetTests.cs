using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using JJFlexWpf.Controls;
using JJFlexWpf.Dialogs;
using Radios;
using Xunit;
using Xunit.Abstractions;

namespace JJFlexWpf.Tests;

/// <summary>
/// The helper's receive queue depths are app-wide statics, so the one test that
/// sets them runs alone, after the parallel classes, and never changes what
/// another class's helper engines are built with.
/// </summary>
[CollectionDefinition("HelperDepthStatics", DisableParallelization = true)]
public sealed class HelperDepthStaticsCollection { }

/// <summary>
/// Sprint 49 Track P, repair 1, B1 (#873, X6): Settings, Advanced, "Return to
/// defaults" puts the helper's receive queue depths back in all three places,
/// the fields' display, the saved settings and the depths in force, not the
/// display alone. Codex's probe, adopted: it failed on <c>c5c8e43b</c> with the
/// display at 2 and 3 while the saved and live depths stayed 1 and 1. Windowless:
/// the dialog is an uninitialized object and its controls are built unhosted on
/// a private STA thread with no presentation source; nothing is shown.
/// </summary>
[Collection("HelperDepthStatics")]
public sealed class AdvancedDepthResetTests
{
    private readonly ITestOutputHelper _out;

    public AdvancedDepthResetTests(ITestOutputHelper output)
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        _out = output;
    }

    [Fact]
    public void Return_to_defaults_restores_the_displayed_saved_and_live_depths()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var old = WdspPipelineDepth.ReceiveDepths;
            try
            {
                var type = typeof(SettingsDialog);
                var shell = (SettingsDialog)RuntimeHelpers.GetUninitializedObject(type);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                var cfg = new AudioOutputConfig { HelperQueueDepthStandard = 1, HelperQueueDepthPremium = 1, AdvancedSettingsUnderstood = true };
                type.GetField("_audioConfig", flags)!.SetValue(shell, cfg);
                type.GetField("SettingsTabs", flags)!.SetValue(shell, new CategoryTabHost());
                WdspPipelineDepth.SetReceiveDepths(1, 1);
                type.GetMethod("InitializeAdvancedTab", flags)!.Invoke(shell, null);

                var tabs = (TabControl)type.GetField("SettingsTabs", flags)!.GetValue(shell)!;
                var tab = (TabItem)tabs.Items[0];
                Assert.Null(PresentationSource.FromVisual(tab));
                var panel = (StackPanel)((ScrollViewer)tab.Content).Content;
                var reset = panel.Children.OfType<Button>().Single();
                var std = (ValueFieldControl)type.GetField("_advancedDepthStandard", flags)!.GetValue(shell)!;
                var pre = (ValueFieldControl)type.GetField("_advancedDepthPremium", flags)!.GetValue(shell)!;
                Assert.Equal(1, std.Value);
                Assert.Equal(1, pre.Value);

                reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                _out.WriteLine($"display {std.Value}/{pre.Value}, saved {cfg.HelperQueueDepthStandard}/{cfg.HelperQueueDepthPremium}, live {WdspPipelineDepth.ReceiveDepths}");

                // Displayed.
                Assert.Equal(WdspPipelineDepth.NnrStandard, std.Value);
                Assert.Equal(WdspPipelineDepth.NnrPremium, pre.Value);
                // Saved.
                Assert.Equal(WdspPipelineDepth.NnrStandard, cfg.HelperQueueDepthStandard);
                Assert.Equal(WdspPipelineDepth.NnrPremium, cfg.HelperQueueDepthPremium);
                // In force: what the next helper engine built on receive is given.
                Assert.Equal((WdspPipelineDepth.NnrStandard, WdspPipelineDepth.NnrPremium), WdspPipelineDepth.ReceiveDepths);
                Assert.Equal(WdspPipelineDepth.NnrPremium, WdspPipelineDepth.For("nnr", "premium", DenoiseDirection.Receive));
                Assert.Equal(WdspPipelineDepth.NnrStandard, WdspPipelineDepth.For("nnr", "standard", DenoiseDirection.Receive));
            }
            catch (Exception ex) { failure = ex; }
            finally { WdspPipelineDepth.SetReceiveDepths(old.Standard, old.Premium); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
