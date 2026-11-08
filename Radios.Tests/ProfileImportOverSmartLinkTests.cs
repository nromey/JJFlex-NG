using Radios.Tests.StationConnect;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Profile import is refused over SmartLink, as export is (ruled by Noel
    /// 2026-11-08, <i>"Refuse it over SmartLink"</i>): refused up front,
    /// before the file dialog and before anything is queued, so the radio's
    /// power-off notice and the PC audio stop never happen for an import that
    /// could only fail. The file dialog and archive step is stood in for, so
    /// no window can open even if the refusal is broken.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class ProfileImportOverSmartLinkTests : System.IDisposable
    {
        private readonly RadioConfigStaticsScope _scope = new(nameof(ProfileImportOverSmartLinkTests));
        private readonly RigOnVendorRadio _vendorRig = new("1111-2222-6300-4444");
        private int _runs;

        private static readonly System.Reflection.PropertyInfo IsWan =
            typeof(Flex.Smoothlake.FlexLib.Radio).GetProperty(nameof(Flex.Smoothlake.FlexLib.Radio.IsWan))!;

        public void Dispose()
        {
            _vendorRig.Dispose();
            _scope.Dispose();
        }

        /// <summary>The rig over the house vendor radio, LAN or SmartLink, with the file step stood in for.</summary>
        private (FlexBase Rig, System.Func<int> Runs) Rig(bool remote)
        {
            IsWan.SetValue(_vendorRig.Vendor.Radio, remote);
            var rig = _vendorRig.Rig;
            Assert.Equal(remote, rig.RemoteRig);   // the property the refusal reads follows the radio
            rig.ProfileImportRunner = () => { _runs++; return true; };
            return (rig, () => _runs);
        }

        [Fact]
        public void Import_over_SmartLink_is_refused_and_nothing_is_run_or_queued()
        {
            var (rig, runs) = Rig(remote: true);
            int queued = rig.q.Count;
            Assert.Equal(GuardedOutcome.Refused, rig.ImportProfileDatabase());
            Assert.Equal(0, runs());
            Assert.Equal(queued, rig.q.Count);
        }

        [Fact]
        public void Import_on_the_LAN_still_runs()
        {
            var (rig, runs) = Rig(remote: false);
            Assert.Equal(GuardedOutcome.Done, rig.ImportProfileDatabase());
            Assert.Equal(1, runs());
        }

        [Fact]
        public void On_the_LAN_the_change_nothing_hold_still_refuses_it()
        {
            var (rig, runs) = Rig(remote: false);
            rig.SetChangeNothingActive(true);
            Assert.Equal(GuardedOutcome.Refused, rig.ImportProfileDatabase());
            Assert.Equal(0, runs());
            rig.SetChangeNothingActive(false);
        }

        [Fact]
        public void The_refusal_names_SmartLink_and_the_LAN()
        {
            string said = Lexicon.Get("settings.flexdb.import_only_lan");
            Assert.Contains("SmartLink", said);
            Assert.Contains("LAN", said);
        }
    }
}
