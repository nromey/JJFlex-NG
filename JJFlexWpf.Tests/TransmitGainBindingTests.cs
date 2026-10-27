using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using JJFlexWpf.Dialogs;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// A microphone change reaches every live Windows-level binding, not only the
/// one in the window where it was made (#835, fix round 1, Codex blocker 4).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> The Audio Workshop and Check your audio host one
/// implementation, <see cref="TransmitGainControls"/>, but each window has its
/// own binding to a Windows endpoint. A microphone changed through the
/// Workshop's Change Audio Devices told only the Workshop's binding, so Check
/// your audio's Windows Input Level went on moving the previous microphone.
/// </para>
/// <para>
/// <b>Windowless.</b> It builds two <see cref="TransmitGainControls"/> on an STA
/// thread, which constructs value controls and a text box and no
/// <see cref="Window"/>; the test asserts no window exists afterwards. Same
/// shape as RecorderRoundEightTests, which is in the desk-safe filter.
/// </para>
/// <para>
/// <b>What it cannot see:</b> a real Core Audio endpoint being rebound. The
/// notice reaching each binding is what is pinned; the rebind itself is the
/// existing BindPcLevel, and the bench step changes microphones with both
/// windows open.
/// </para>
/// </remarks>
public sealed class TransmitGainBindingTests
{
    [Fact]
    public void A_microphone_change_in_one_window_reaches_the_other_windows_binding()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                int windowsBefore = Application.Current?.Windows.Count ?? 0;
                var workshop = new TransmitGainControls(Dispatcher.CurrentDispatcher, () => null, () => false, () => null);
                var check = new TransmitGainControls(Dispatcher.CurrentDispatcher, () => null, () => false, () => null);
                try
                {
                    // The Workshop's Change Audio Devices is where the change is made.
                    TransmitGainControls.MicrophoneChoiceMayHaveChanged(workshop);
                    Assert.Equal(1, workshop.DeviceChangesSeen);
                    Assert.Equal(1, check.DeviceChangesSeen);

                    // A closed window's binding is no longer told.
                    check.Dispose();
                    TransmitGainControls.MicrophoneChoiceMayHaveChanged(workshop);
                    Assert.Equal(2, workshop.DeviceChangesSeen);
                    Assert.Equal(1, check.DeviceChangesSeen);
                }
                finally
                {
                    workshop.Dispose();
                    check.Dispose();
                }
                Assert.Equal(windowsBefore, Application.Current?.Windows.Count ?? 0);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "The binding thread did not finish.");
        if (failure != null) throw failure;
    }
}
