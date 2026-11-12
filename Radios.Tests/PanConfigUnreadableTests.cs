using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// An operator whose pan adapter file cannot be read starts from the
    /// default pan settings, and the file is set aside rather than written
    /// over. Until then a switch to that operator kept the previous operator's
    /// settings, and the next save wrote them into the new operator's folder.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class PanConfigUnreadableTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-pancfg-" + Guid.NewGuid().ToString("N"));

        public PanConfigUnreadableTests()
        {
            Directory.CreateDirectory(_root);
            OperatorChange.ResetForTests();
        }

        public void Dispose()
        {
            OperatorChange.ResetForTests();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private FlexBase RigFor(Func<string> currentOperator)
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };
            rig.Callouts.ConfigDirectory = _root;
            rig.Callouts.GetOperatorName = () => currentOperator();
            return rig;
        }

        [Fact]
        public void A_switch_to_an_operator_whose_file_is_unreadable_starts_them_from_the_defaults_and_keeps_the_file()
        {
            string op = "A";
            var rig = RigFor(() => op);
            try
            {
                var pan = new PanAdapterManager(rig, rig.ConfigDirectory + '\\' + rig.OperatorName, 40, (_, _) => { });
                pan.OpsConfigInfo.AutoProc = "ssb";   // A's own, not the default
                pan.writeConfig();

                string bFolder = Path.Combine(_root, "B");
                Directory.CreateDirectory(bFolder);
                string bFile = Path.Combine(bFolder, "configinfo.xml");
                const string corrupt = "<ConfigInfo><AutoProc>ssb";   // cut short
                File.WriteAllText(bFile, corrupt);

                op = "B";
                pan.FollowCurrentOperator();

                Assert.Equal(bFolder, pan.OperatorsFolder);   // positive control: B's folder was read
                Assert.Equal("off", pan.OpsConfigInfo.AutoProc);   // the default, not A's "ssb"

                var kept = Directory.GetFiles(bFolder, "configinfo.xml.unreadable-*");
                Assert.Single(kept);
                Assert.Equal(corrupt, File.ReadAllText(kept[0]));
                Assert.False(File.Exists(bFile), "the unreadable file is set aside, not left to be written over");

                // B's next save writes B's defaults, never A's value.
                pan.writeConfig();
                Assert.DoesNotContain("ssb", File.ReadAllText(bFile));
            }
            finally
            {
                rig.theRadio = null;
                rig.Dispose();
            }
        }

        [Fact]
        public void A_readable_file_is_read_as_before()
        {
            string op = "A";
            var rig = RigFor(() => op);
            try
            {
                var pan = new PanAdapterManager(rig, rig.ConfigDirectory + '\\' + rig.OperatorName, 40, (_, _) => { });
                pan.OpsConfigInfo.AutoProc = "ssb";
                pan.writeConfig();

                pan.OpsConfigInfo.AutoProc = "off";
                pan.FollowCurrentOperator();

                Assert.Equal("ssb", pan.OpsConfigInfo.AutoProc);
                Assert.Empty(Directory.GetFiles(Path.Combine(_root, "A"), "*.unreadable-*"));
            }
            finally
            {
                rig.theRadio = null;
                rig.Dispose();
            }
        }

        [Fact]
        public void The_filter_presets_set_an_unreadable_file_aside_under_the_same_name()
        {
            string op = "Op_" + Guid.NewGuid().ToString("N");
            string file = Path.Combine(_root, op + "_filterPresets.xml");
            File.WriteAllText(file, "<FilterPresets><");

            FilterPresets.Load(_root, op, out string? kept);

            Assert.NotNull(kept);
            Assert.StartsWith(file + ".unreadable-", kept);
            Assert.True(File.Exists(kept));
            Assert.False(File.Exists(file));
        }
    }
}
