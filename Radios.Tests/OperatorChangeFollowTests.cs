using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #1072: changing operators while connected leaves no per-operator store
    /// holding the previous operator's data. One mechanism every store follows
    /// (<see cref="OperatorChange"/>), and the pan adapter, which kept the first
    /// operator's folder, follows it.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class OperatorChangeFollowTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjflex-1072-" + Guid.NewGuid().ToString("N"));

        public OperatorChangeFollowTests()
        {
            Directory.CreateDirectory(_root);
            OperatorChange.ResetForTests();
        }

        public void Dispose()
        {
            OperatorChange.ResetForTests();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---- the one mechanism ----

        [Fact]
        public void Every_follower_hears_the_change_in_the_order_it_registered()
        {
            var heard = new List<string>();
            OperatorChange.Follow("first", op => heard.Add("first:" + op.OperatorName));
            OperatorChange.Follow("second", op => heard.Add("second:" + op.OperatorName));

            OperatorChange.Announce(_root, "B");

            Assert.Equal(new[] { "first:B", "second:B" }, heard);
            Assert.Equal("B", OperatorChange.Current.OperatorName);
            Assert.Equal(_root, OperatorChange.Current.ConfigDirectory);
        }

        [Fact]
        public void A_follower_that_throws_does_not_stop_the_ones_after_it()
        {
            var heard = new List<string>();
            OperatorChange.Follow("breaks", _ => throw new IOException("disk"));
            OperatorChange.Follow("after", op => heard.Add(op.OperatorName!));

            OperatorChange.Announce(_root, "B");

            Assert.Equal(new[] { "B" }, heard);
        }

        [Fact]
        public void Registering_a_name_again_replaces_it_in_place_and_does_not_run_it_twice()
        {
            var heard = new List<string>();
            OperatorChange.Follow("stores", _ => heard.Add("old"));
            OperatorChange.Follow("after", _ => heard.Add("after"));
            OperatorChange.Follow("stores", _ => heard.Add("new"));

            OperatorChange.Announce(_root, "B");

            Assert.Equal(new[] { "new", "after" }, heard);
            Assert.Equal(new[] { "stores", "after" }, OperatorChange.Followers);
        }

        [Fact]
        public void Startup_records_the_operator_without_notifying_anyone()
        {
            bool heard = false;
            OperatorChange.Follow("store", _ => heard = true);

            OperatorChange.Establish(_root, "A");

            Assert.False(heard);
            Assert.Equal("A", OperatorChange.Current.OperatorName);
            Assert.True(OperatorChange.Current.HasOperator);
        }

        [Fact]
        public void No_operator_reads_as_none()
        {
            OperatorChange.Announce(_root, null);
            Assert.False(OperatorChange.Current.HasOperator);
            OperatorChange.Establish(_root, "");
            Assert.False(OperatorChange.Current.HasOperator);
        }

        /// <summary>
        /// The application has ONE operator-change handler, and it announces
        /// through <see cref="OperatorChange"/>; every store that used to
        /// hang its own handler on the event registers as a follower instead,
        /// after the operator in use at startup is recorded. Read from the
        /// source: the VB application cannot be run here.
        /// </summary>
        [Fact]
        public void The_application_has_one_operator_change_handler_and_every_store_follows_through_it()
        {
            string repo = FieldKeyMapScan.RepoRoot();
            // The whole VB tree, not just the root (#1072 repair): a second
            // subscription in a nested file, or one spelled with other spacing,
            // case or a line continuation, counts. Exactly one, in globals.vb.
            var subscribers = VbSources(repo)
                .SelectMany(f => Enumerable.Repeat(Path.GetRelativePath(repo, f), OperatorChangeSubscription.Matches(File.ReadAllText(f)).Count))
                .ToList();
            Assert.Equal(new[] { "globals.vb" }, subscribers);

            string globals = File.ReadAllText(Path.Combine(repo, "globals.vb")).Replace("\r\n", "\n");
            Assert.Matches(new Regex(@"Private Sub operatorChanged\(sender As Object, e As ConfigArgs\)\s*\n\s*Radios\.OperatorChange\.Announce\(BaseConfigDir, currentOperatorUniqueName\(\)\)\s*\n\s*End Sub"), globals);
            int establish = globals.IndexOf("Radios.OperatorChange.Establish(BaseConfigDir, currentOperatorUniqueName())", StringComparison.Ordinal);
            int follow = globals.IndexOf("FollowTheOperatorHere()\n", establish, StringComparison.Ordinal);
            int wire = globals.IndexOf("AddHandler Operators.ConfigEvent, AddressOf operatorChanged", StringComparison.Ordinal);
            Assert.True(establish > 0 && follow > establish && wire > follow,
                "the startup operator is recorded, then this module's followers, then the handler is wired");

            // Every store that holds operator data follows, by name.
            string events = File.ReadAllText(Path.Combine(repo, "ApplicationEvents.vb"));
            foreach (string name in new[] { "contact log", "the connected radio (keyer setup, pan adapter)", "UI mode" })
                Assert.Contains("Radios.OperatorChange.Follow(\"" + name + "\"", globals);
            foreach (string name in new[] { "accessibility choices", "PTT settings", "Home's filter presets, band memory and license", "tuning steps" })
                Assert.Contains("Radios.OperatorChange.Follow(\"" + name + "\"", events);
        }

        /// <summary>
        /// Every way VB subscribes to the operator event: AddHandler with any
        /// spacing, case or line continuation, and a Handles clause. VB is not
        /// case-sensitive, so neither is this.
        /// </summary>
        private static readonly Regex OperatorChangeSubscription = new(
            @"\b(?:AddHandler\s+(?:_\s*)?Operators\s*(?:_\s*)?\.\s*(?:_\s*)?ConfigEvent\b|Handles\s+(?:_\s*)?Operators\s*\.\s*ConfigEvent\b)",
            RegexOptions.IgnoreCase);

        /// <summary>Every .vb file under the repository on disk, tracked or not, outside bin, obj and vendor trees.</summary>
        private static List<string> VbSources(string repo)
        {
            var skip = new[] { "bin", "obj", ".git", ".vs", "node_modules", "packages", "FlexLib_API" };
            return Directory.EnumerateFiles(repo, "*.vb", SearchOption.AllDirectories)
                .Where(f => !Path.GetRelativePath(repo, f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(part => skip.Contains(part, StringComparer.OrdinalIgnoreCase)))
                .ToList();
        }

        /// <summary>
        /// The guard's positive controls: the pattern sees each spelling it
        /// claims to, and the walk reaches a nested folder. Without these a
        /// green guard could mean it saw nothing at all.
        /// </summary>
        [Fact]
        public void The_one_handler_guard_sees_every_spelling_and_the_nested_folders()
        {
            foreach (string spelled in new[]
            {
                "AddHandler Operators.ConfigEvent, AddressOf operatorChanged",
                "addhandler   operators.configevent ,AddressOf x",
                "AddHandler Operators . ConfigEvent, AddressOf x",
                "AddHandler _\n    Operators.ConfigEvent, AddressOf x",
                "Private Sub x(sender As Object, e As ConfigArgs) Handles Operators.ConfigEvent",
            })
                Assert.True(OperatorChangeSubscription.IsMatch(spelled), "the guard does not see: " + spelled);
            Assert.False(OperatorChangeSubscription.IsMatch("RemoveHandler Operators.ConfigEvent, AddressOf x"));
            Assert.False(OperatorChangeSubscription.IsMatch("AddHandler Operators.ConfigEventually, AddressOf x"));

            string repo = FieldKeyMapScan.RepoRoot();
            var files = VbSources(repo);
            Assert.Contains(files, f => Path.GetRelativePath(repo, f).StartsWith("My Project" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(files, f => Path.GetFileName(f).Equals("globals.vb", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(files, f => f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }

        // ---- the pan adapter ----

        private FlexBase RigFor(Func<string> currentOperator)
        {
            var rig = new FlexBase(new FlexBase.OpenParms { ProgramName = "JJFlexTests" }) { SuppressSpeech = true };
            rig.Callouts.ConfigDirectory = _root;
            rig.Callouts.GetOperatorName = () => currentOperator();
            return rig;
        }

        /// <summary>
        /// Radio, Operators, B, while connected: the pan adapter reads B's
        /// folder, and a later save writes B's file and leaves A's as A left it.
        /// Until #1072 it went on reading and writing the first operator's.
        /// </summary>
        [Fact]
        public void After_an_operator_switch_the_pan_adapter_reads_and_saves_the_new_operators_folder()
        {
            string op = "A";
            var rig = RigFor(() => op);
            try
            {
                var pan = new PanAdapterManager(rig, rig.ConfigDirectory + '\\' + rig.OperatorName, 40, (_, _) => { });
                pan.OpsConfigInfo.AutoProc = "ssb";
                pan.writeConfig();
                string aFile = Path.Combine(_root, "A", "configinfo.xml");
                string aBefore = File.ReadAllText(aFile);

                op = "B";
                pan.FollowCurrentOperator();

                Assert.Equal(Path.Combine(_root, "B"), pan.OperatorsFolder);
                Assert.Equal("off", pan.OpsConfigInfo.AutoProc);   // B's own (none saved): the default
                Assert.True(File.Exists(Path.Combine(_root, "B", "PanRanges.xml")), "B's pan ranges were not read from B's folder");

                pan.OpsConfigInfo.AutoProc = "ssb";
                pan.writeConfig();
                Assert.True(File.Exists(Path.Combine(_root, "B", "configinfo.xml")));
                Assert.Equal(aBefore, File.ReadAllText(aFile));

                op = "A";
                pan.FollowCurrentOperator();
                Assert.Equal("ssb", pan.OpsConfigInfo.AutoProc);
            }
            finally
            {
                rig.theRadio = null;
                rig.Dispose();
            }
        }

        /// <summary>The handler the operator change reaches calls the follow before anything else.</summary>
        [Fact]
        public void The_pan_adapters_operator_change_handler_follows_the_current_operator_first()
        {
            string src = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "Radios", "PanAdapterManager.cs")).Replace("\r\n", "\n");
            int handler = src.IndexOf("public void OperatorChangeHandler()", StringComparison.Ordinal);
            Assert.True(handler > 0);
            string body = src.Substring(handler, src.IndexOf("\n        }", handler, StringComparison.Ordinal) - handler);
            Assert.Matches(new Regex(@"\{\s*FollowCurrentOperator\(\);"), body);
        }
    }
}
