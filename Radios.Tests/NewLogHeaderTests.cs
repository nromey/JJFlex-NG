using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.CompilerServices;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #814 repair: a log created by the no-log-file choice gets a complete
    /// header, and the header reads back the way the full log form reads it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Found by the Codex check of round one. The Create path made the log
    /// directly instead of through Log Characteristics, whose OK handler used
    /// to write every header field itself. <c>LogSession.setHeaderDefaults</c>
    /// never set HDR_CallLookup, so the new log was written with that field
    /// empty, and <c>LogEntry.setupForm</c> compares it as a number:
    /// ToDouble("") threw when the full log form opened.
    /// </para>
    /// <para>
    /// The logger is VB in the app assembly, which no test project references,
    /// so the behavioural half loads the BUILT app assembly from the repo's
    /// Debug x64 output into its own load context and calls the real
    /// <c>setHeaderDefaults</c> on a session with the real header dictionary.
    /// It needs <c>JJFlexRadio.vbproj</c> built first, and says so if it is not.
    /// No form, window or file is created.
    /// </para>
    /// </remarks>
    public sealed class NewLogHeaderTests
    {
        private static string Read(string relative) =>
            File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), relative));

        /// <summary>The class, from source: every header tag has a default.</summary>
        [Fact]
        public void Every_header_tag_gets_a_default_value()
        {
            string logClass = Read("LogClass.vb");
            int at = logClass.IndexOf("Public Shared HeaderADIFTags As String()", StringComparison.Ordinal);
            Assert.True(at >= 0);
            int end = logClass.IndexOf('}', at);
            var tags = Regex.Matches(logClass.Substring(at, end - at), @"AdifTags\.(HDR_\w+)")
                .Select(m => m.Groups[1].Value).ToList();
            Assert.Equal(5, tags.Count);   // positive control: the list was read

            string session = Read("LogSession.vb");
            int body = session.IndexOf("Private Sub setHeaderDefaults()", StringComparison.Ordinal);
            Assert.True(body >= 0);
            string defaults = session.Substring(body, session.IndexOf("End Sub", body, StringComparison.Ordinal) - body);
            foreach (string tag in tags)
                Assert.True(defaults.Contains("setHeaderFieldText(AdifTags." + tag + ",", StringComparison.Ordinal),
                    "a new log's header has no default for " + tag);
        }

        /// <summary>
        /// The real defaults, from the built app: every header field is set,
        /// and each reads back the way its readers read it, the full log
        /// form's lookup comparison above all.
        /// </summary>
        [Fact]
        public void A_new_logs_header_is_complete_and_reads_back_as_the_log_form_reads_it()
        {
            string? app = BuiltAppAssembly();
            Assert.True(app != null,
                "Build JJFlexRadio.vbproj (Debug, x64) first: this test calls the logger's real "
                + "setHeaderDefaults from the built app assembly.");

            var context = new AssemblyLoadContext("new-log-header", isCollectible: true);
            string folder = Path.GetDirectoryName(app)!;
            context.Resolving += (ctx, name) =>
            {
                string candidate = Path.Combine(folder, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };
            try
            {
                Assembly assembly = context.LoadFromAssemblyPath(app!);
                Type sessionType = assembly.GetTypes().Single(t => t.Name == "LogSession");
                Type logClassType = assembly.GetTypes().Single(t => t.Name == "LogClass");
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

                var tags = (string[])logClassType.GetField("HeaderADIFTags", any)!.GetValue(null)!;
                Assert.Equal(5, tags.Length);

                // A session as LogSession.Start builds one, without opening a file.
                object session = RuntimeHelpers.GetUninitializedObject(sessionType);
                FieldInfo headerField = sessionType.GetField("HeaderDictionary", any)!;
                Type elementType = headerField.FieldType.GetGenericArguments()[1];
                var header = (IDictionary)Activator.CreateInstance(headerField.FieldType)!;
                foreach (string tag in tags)
                    header.Add(tag, Activator.CreateInstance(elementType, tag));
                headerField.SetValue(session, header);

                sessionType.GetMethod("setHeaderDefaults", any)!.Invoke(session, null);

                MethodInfo get = sessionType.GetMethod("GetHeaderFieldText", any)!;
                string Text(string tag) => (string)get.Invoke(session, new object[] { tag })!;
                foreach (string tag in tags)
                    Assert.False(string.IsNullOrEmpty(Text(tag)), "a new log's header leaves " + tag + " empty");

                // AdifTags.HDR_CallLookup, HDR_DupCheck and HDR_StartingSerial.
                const string lookupTag = "APP_JJRADIO_CallLookup";
                const string dupTag = "APP_JJRADIO_DUPCHK";
                const string serialTag = "APP_JJRADIO_HDRSTX";
                Assert.Contains(lookupTag, tags);
                Assert.Contains(dupTag, tags);
                Assert.Contains(serialTag, tags);

                // LogEntry.setupForm: header text compared with LookupChoices.Yes,
                // which VB compiles as Conversions.ToDouble(text).
                double lookup = Conversions.ToDouble(Text(lookupTag));
                Assert.NotEqual(0d, lookup);   // LookupChoices.Yes is 0: a new log does not look up
                // Log Characteristics reads the other two numeric fields with CInt.
                Assert.Equal(0, Conversions.ToInteger(Text(dupTag)));
                Assert.Equal(1, Conversions.ToInteger(Text(serialTag)));
            }
            finally
            {
                context.Unload();
            }
        }

        private static string? BuiltAppAssembly()
        {
            string root = FieldKeyMapScan.RepoRoot();
            foreach (string config in new[] { "Debug", "Release" })
            {
                string path = Path.Combine(root, "bin", "x64", config, "net10.0-windows", "win-x64", "jjflexible.dll");
                if (File.Exists(path)) return path;
            }
            return null;
        }
    }
}
