using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Flex.Smoothlake.FlexLib;
using Xunit;

namespace Radios.Tests.StationConnect
{
    /// <summary>
    /// The production adapter tells a discovery-driven client removal from a
    /// radio-status one by whether FlexLib is still holding
    /// <c>Radio.GuiClientsLockObj</c> when it raises <c>GUIClientRemoved</c>
    /// (<c>Radio.UpdateGuiClientsList</c> raises inside the lock;
    /// <c>Radio.RemoveGUIClient</c> releases it first). That is a vendor
    /// accident we depend on, so it is pinned HERE against the vendored code,
    /// with a real Radio object and no network: if a FlexLib update changes
    /// either raise site, this fails before the adapter starts misfiling
    /// removals.
    /// </summary>
    public sealed class RosterProvenanceTests
    {
        private static Radio NewRadio()
        {
            var radio = (Radio)Activator.CreateInstance(
                typeof(Radio),
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                args: new object[] { true },   // internal Radio(bool isWan)
                culture: null);
            Assert.NotNull(radio);
            return radio;
        }

        [Fact]
        public void ADiscoveryDrivenRemoval_IsRaisedWhileFlexLibHoldsItsRosterLock()
        {
            var radio = NewRadio();
            var client = new GUIClient(7, "some-id", "SmartSDR", "W1AW", is_local_ptt: false);
            lock (radio.GuiClientsLockObj) radio.GuiClients.Add(client);

            bool? heldAtRaise = null;
            radio.GUIClientRemoved += _ => heldAtRaise = Monitor.IsEntered(radio.GuiClientsLockObj);

            radio.UpdateGuiClientsList(new List<GUIClient>()); // discovery listed nobody

            Assert.True(heldAtRaise, "FlexLib's discovery sweep no longer raises GUIClientRemoved inside GuiClientsLockObj; " +
                "the adapter's origin discriminator (ObserveClientRemoved) is now wrong");
        }

        [Fact]
        public void ARadioStatusRemoval_IsRaisedAfterFlexLibReleasesItsRosterLock()
        {
            // The positive control for the discriminator: the OTHER raise
            // site, which the "client ... disconnected" status line reaches.
            var radio = NewRadio();
            var client = new GUIClient(7, "some-id", "SmartSDR", "W1AW", is_local_ptt: false);
            lock (radio.GuiClientsLockObj) radio.GuiClients.Add(client);

            bool? heldAtRaise = null;
            radio.GUIClientRemoved += _ => heldAtRaise = Monitor.IsEntered(radio.GuiClientsLockObj);

            var remove = typeof(Radio).GetMethod("RemoveGUIClient", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(remove);
            remove.Invoke(radio, new object[] { client });

            Assert.False(heldAtRaise, "FlexLib's status-driven RemoveGUIClient now raises GUIClientRemoved inside " +
                "GuiClientsLockObj; the adapter's origin discriminator (ObserveClientRemoved) is now wrong");
        }

        [Fact]
        public void TheDiscriminatorIsWhatTheAdapterUses()
        {
            // The source pin that ties the two facts above to the feed.
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var text = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "Radios", "FlexBase.StationConnect.cs"));
            int at = text.IndexOf("private void ObserveClientRemoved(ObservationBinding binding, GUIClient client)", StringComparison.Ordinal);
            Assert.True(at >= 0);
            string body = text.Substring(at, 700);
            Assert.Contains("System.Threading.Monitor.IsEntered(r.GuiClientsLockObj)", body, StringComparison.Ordinal);
            Assert.Contains("RosterRemovalOrigin.Discovery", body, StringComparison.Ordinal);
            Assert.Contains("RosterRemovalOrigin.RadioStatus", body, StringComparison.Ordinal);
        }
    }
}
