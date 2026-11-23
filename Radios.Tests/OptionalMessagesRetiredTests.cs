#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Xml.Serialization;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Jim's optional-message wiring is retired (#1108): nothing in the app
    /// shows one of those messages any more, and every "don't show again" goes
    /// through AdvisorySuppression. The setup call in <c>SetCurrentOp</c> and
    /// the two delegates in <c>PersonalData</c> are gone; the saved field
    /// stays, so an operator file that carries it still loads and re-saves
    /// with it intact (Jim's design is not replaced, only his code).
    /// </summary>
    /// <remarks>
    /// The round trip uses the operator type from the built app assembly, in
    /// a collectible context, as <see cref="OperatorSaveKeepsOldFileTests"/>
    /// does. Needs <c>JJFlexRadio.vbproj</c> built first and says so if not.
    /// </remarks>
    [Collection(BuiltAppAssemblyCollection.Name)]
    public sealed class OptionalMessagesRetiredTests
    {
        private static string Root => FieldKeyMapScan.RepoRoot();

        private static string Read(params string[] parts) =>
            File.ReadAllText(Path.Combine(Root, Path.Combine(parts))).Replace("\r\n", "\n");

        [Fact]
        public void Nothing_outside_MsgLib_sets_up_the_optional_message_processor()
        {
            foreach (string file in Directory.EnumerateFiles(Root, "*.vb", SearchOption.TopDirectoryOnly)
                         .Concat(Directory.EnumerateFiles(Path.Combine(Root, "Radios"), "*.cs", SearchOption.AllDirectories))
                         .Concat(Directory.EnumerateFiles(Path.Combine(Root, "JJFlexWpf"), "*.cs", SearchOption.AllDirectories)))
            {
                string text = File.ReadAllText(file);
                Assert.False(text.Contains("OptionalMessage.Setup(", StringComparison.Ordinal),
                    Path.GetFileName(file) + " still sets up Jim's optional messages");
            }
        }

        [Fact]
        public void Positive_control_the_scan_reaches_the_files_it_must_and_MsgLib_still_defines_Setup()
        {
            // If the scan above read no files, or read the wrong folder, it
            // would pass for nothing.
            Assert.Contains("Friend Sub SetCurrentOp(", Read("globals.vb"));
            Assert.Contains("Public Class personal_v1", Read("PersonalData.vb"));
            Assert.Contains("public static void Setup(", Read("MsgLib", "OptionalMessage.cs"));
        }

        [Fact]
        public void The_two_delegates_are_gone_and_the_saved_field_stays()
        {
            string src = Read("PersonalData.vb");
            Assert.DoesNotContain("Function UpdateOptionalMessages(", src);
            Assert.DoesNotContain("Function RetrieveOptionalMessages(", src);
            Assert.Contains("Public OptionalMessages As OptionalMessageConfig()", src);
            Assert.Contains("Public Class OptionalMessageConfig", src);
        }

        private const string OldFile =
            "<?xml version=\"1.0\"?>\n" +
            "<personal_v1 xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\n" +
            "  <fileName>TEST_Operator.xml</fileName>\n" +
            "  <callSign>TEST</callSign>\n" +
            "  <OptionalMessages>\n" +
            "    <OptionalMessageConfig>\n" +
            "      <Tag>someOldTag</Tag>\n" +
            "      <Result>Yes</Result>\n" +
            "      <Ignore>true</Ignore>\n" +
            "    </OptionalMessageConfig>\n" +
            "  </OptionalMessages>\n" +
            "</personal_v1>\n";

        [Fact]
        public void An_operator_file_carrying_the_old_field_loads_and_resaves_with_it_intact()
        {
            string resaved = RoundTrip(OldFile);
            Assert.Contains("<OptionalMessages>", resaved);
            Assert.Contains("<Tag>someOldTag</Tag>", resaved);
            Assert.Contains("<Result>Yes</Result>", resaved);
            Assert.Contains("<Ignore>true</Ignore>", resaved);
            Assert.Contains("<callSign>TEST</callSign>", resaved);   // control: the rest of the operator came through
        }

        private static string RoundTrip(string xml)
        {
            string root = Root;
            string? app = null;
            foreach (string config in new[] { "Debug", "Release" })
            {
                string path = Path.Combine(root, "bin", "x64", config, "net10.0-windows", "win-x64", "jjflexible.dll");
                if (File.Exists(path)) { app = path; break; }
            }
            Assert.True(app != null,
                "Build JJFlexRadio.vbproj (Debug, x64) first: this test reads an operator file "
                + "with the built app's own operator type.");
            string result = "";
            WeakReference unloaded = RoundTripInIsolatedContext(app!, xml, r => result = r);
            for (int i = 0; unloaded.IsAlive && i < 20; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference RoundTripInIsolatedContext(string app, string xml, Action<string> report)
        {
            var context = new AssemblyLoadContext("operator-optional-messages", isCollectible: true);
            string folder = Path.GetDirectoryName(app)!;
            context.Resolving += (ctx, name) =>
            {
                string candidate = Path.Combine(folder, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };
            try
            {
                Assembly assembly = context.LoadFromAssemblyPath(app);
                Type personal = assembly.GetTypes().Single(t => t.Name == "PersonalData");
                Type op = personal.GetNestedType("personal_v1")!;
                Assert.NotNull(op);
                var serializer = new XmlSerializer(op);
                object loaded;
                using (var reader = new StringReader(xml))
                    loaded = serializer.Deserialize(reader)!;
                var sb = new StringBuilder();
                using (var writer = new StringWriter(sb))
                    serializer.Serialize(writer, loaded);
                report(sb.ToString());
            }
            finally
            {
                context.Unload();
            }
            return new WeakReference(context);
        }
    }
}
