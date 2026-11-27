using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Repair round 2 of junk box batch 31 (#1282): one band key for every
    /// logged contact, used by the ADIF export and by duplicate checking.
    /// </summary>
    /// <remarks>
    /// <para>The log stores the receive frequency in FREQ and the transmit
    /// frequency in TXFREQ, and the logger sets BAND from the transmit one.
    /// Round 1's export recomputed BAND from FREQ, so a cross-band contact
    /// exported its receive band; and duplicate checking joined the call to the
    /// stored BAND as it is, so an old 35cm entry, or an old entry the parent
    /// logged with no BAND at all at 475 kHz, missed a new 33cm or 630m one.</para>
    /// <para>The logger is VB in the app assembly, which no test project
    /// references, so these load the BUILT app (Debug x64) into a collectible
    /// load context, as <see cref="NewLogHeaderTests"/> does, and call the real
    /// export formatter (<c>ExportForm.LogrecToADIF</c>, on a form built as an
    /// uninitialized object, so no window) and the real duplicate key
    /// (<c>LogDupChecking.keyElement</c>) on a session whose field dictionary is
    /// built in memory. No form is shown, and no file or log is opened.</para>
    /// </remarks>
    [Collection(BuiltAppAssemblyCollection.Name)]
    public sealed class LogBandKeyTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        /// <summary>The app, loaded once per test into its own context, and what the tests call in it.</summary>
        private sealed class App
        {
            public readonly Type Session, Export, Dup, Key, DupTypes;
            public App(Assembly app)
            {
                Type[] types = app.GetTypes();
                Session = types.Single(t => t.Name == "LogSession");
                Export = types.Single(t => t.Name == "ExportForm");
                Dup = types.Single(t => t.Name == "LogDupChecking");
                Key = Dup.GetNestedType("keyElement", Any)!;
                DupTypes = Dup.GetNestedType("DupTypes", Any)!;
            }

            /// <summary>A session holding one record, its fields in DefaultLog's order.</summary>
            public object Record(string call, string freq, string txFreq, string band, string mode = "USB")
            {
                object session = RuntimeHelpers.GetUninitializedObject(Session);
                FieldInfo dictField = Session.GetField("FieldDictionary", Any)!;
                Type elementType = dictField.FieldType.GetGenericArguments()[1];
                var dict = (IDictionary)Activator.CreateInstance(dictField.FieldType)!;
                void Add(string tag, string value)
                {
                    object el = Activator.CreateInstance(elementType, tag)!;
                    SetData(el, value);
                    dict.Add(tag, el);
                }
                Add("CALL", call);
                Add("QSO_DATE", "11/27/2026");
                Add("TIME_ON", "0100");
                Add("FREQ", freq);
                Add("TXFREQ", txFreq);
                Add("MODE", mode);
                Add("BAND", band);
                Add("STX", "1");   // keyElement reads the serial as a required field
                dictField.SetValue(session, dict);
                return session;
            }

            private static void SetData(object element, string value)
            {
                var t = element.GetType();
                if (t.GetField("Data", Any) is { } f) f.SetValue(element, value);
                else t.GetProperty("Data", Any)!.SetValue(element, value);
            }

            public string Exported(object session)
            {
                object form = RuntimeHelpers.GetUninitializedObject(Export);
                Export.GetField("session", Any)!.SetValue(form, session);
                return (string)Export.GetMethod("LogrecToADIF", Any)!.Invoke(form, null)!;
            }

            public string DupKey(object session, string type) =>
                (string)Key.GetField("Value", Any)!.GetValue(
                    Activator.CreateInstance(Key, session, Enum.Parse(DupTypes, type)))!;

            public string BandKey(object session) =>
                (string)Dup.GetMethod("BandKey", Any, null, new[] { Session }, null)!.Invoke(null, new[] { session })!;
        }

        private static void WithApp(Action<App> body)
        {
            string? app = BuiltAppAssembly();
            Assert.True(app != null,
                "Build JJFlexRadio.vbproj (Debug, x64) first: this test calls the logger's real export and duplicate key.");
            WeakReference unloaded = Run(app!, body);
            for (int i = 0; unloaded.IsAlive && i < 20; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference Run(string app, Action<App> body)
        {
            var context = new AssemblyLoadContext("log-band-key", isCollectible: true);
            string folder = Path.GetDirectoryName(app)!;
            context.Resolving += (ctx, name) =>
            {
                string candidate = Path.Combine(folder, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };
            try { body(new App(context.LoadFromAssemblyPath(app))); }
            finally { context.Unload(); }
            return new WeakReference(context);
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

        private static string BandTag(string adif)
        {
            int at = adif.IndexOf("<BAND:", StringComparison.Ordinal);
            if (at < 0) return "";
            int close = adif.IndexOf('>', at);
            int length = int.Parse(adif.Substring(at + 6, close - at - 6));
            return adif.Substring(close + 1, length);
        }

        // ---- the export ----

        [Fact]
        public void A_cross_band_contact_exports_its_logged_transmit_band_never_its_receive_band() => WithApp(a =>
        {
            // Receive 145.900 MHz, transmit 435.100 MHz, logged BAND 70cm.
            string adif = a.Exported(a.Record("W1AW", "145.900.000", "435.100.000", "70cm"));
            Assert.Equal("70CM", BandTag(adif));
            Assert.Contains("<FREQ:", adif, StringComparison.Ordinal);   // positive control: the record was formatted
        });

        [Fact]
        public void An_empty_band_is_derived_from_the_transmit_frequency_and_from_the_receive_one_only_when_transmit_is_missing() => WithApp(a =>
        {
            Assert.Equal("70CM", BandTag(a.Exported(a.Record("W1AW", "145.900.000", "435.100.000", ""))));
            Assert.Equal("2M", BandTag(a.Exported(a.Record("W1AW", "145.900.000", "0.000.000", ""))));
            Assert.Equal("2M", BandTag(a.Exported(a.Record("W1AW", "145.900.000", "", ""))));
            // A nonzero transmit frequency with no ADIF band never borrows the receive band.
            Assert.Equal("", BandTag(a.Exported(a.Record("W1AW", "145.900.000", "219.500.000", ""))));
        });

        [Fact]
        public void A_label_ADIF_does_not_define_exports_its_alias_and_one_with_no_alias_exports_as_logged() => WithApp(a =>
        {
            Assert.Equal("33CM", BandTag(a.Exported(a.Record("W1AW", "915.000.000", "915.000.000", "35cm"))));
            Assert.Equal("ABOVE 275GHZ", BandTag(a.Exported(a.Record("W1AW", "", "", "above 275GHz"))));
        });

        [Fact]
        public void The_export_and_the_duplicate_check_give_the_same_key_for_the_same_record() => WithApp(a =>
        {
            foreach (var (freq, tx, band) in new[] { ("145.900.000", "435.100.000", ""), ("0.475.000", "0.475.000", ""),
                                                     ("915.000.000", "915.000.000", "35cm"), ("145.900.000", "0.000.000", "") })
            {
                object record = a.Record("W1AW", freq, tx, band);
                string stored = a.BandKey(record);   // the stored mm.kkk.hhh form, as duplicate checking reads it
                string exported = BandTag(a.Exported(record));   // the export rewrites FREQ and TXFREQ to MHz first
                Assert.Equal(stored.ToUpperInvariant(), exported);
                Assert.Equal(stored, a.BandKey(record));   // and after the rewrite, the key is unchanged
                Assert.NotEqual("", stored);
            }
        });

        // ---- duplicate checking ----

        [Theory]
        [InlineData("callAndBand")]
        [InlineData("callAndBandAndMode")]
        public void An_old_35cm_entry_is_a_duplicate_of_a_new_33cm_contact(string type) => WithApp(a =>
        {
            string old = a.DupKey(a.Record("W1AW", "915.000.000", "915.000.000", "35cm"), type);
            string now = a.DupKey(a.Record("W1AW", "915.000.000", "915.000.000", "33cm"), type);
            Assert.Equal(old, now);
            Assert.NotEqual(old, a.DupKey(a.Record("W1AW", "432.100.000", "432.100.000", "70cm"), type));   // positive control
        });

        [Theory]
        [InlineData("callAndBand")]
        [InlineData("callAndBandAndMode")]
        public void An_old_entry_logged_with_no_band_at_475_kilohertz_is_a_duplicate_of_a_new_630m_contact(string type) => WithApp(a =>
        {
            // The parent logged no BAND here: its table had no band at 472 to 479 kHz.
            string old = a.DupKey(a.Record("W1AW", "0.475.000", "0.475.000", ""), type);
            string now = a.DupKey(a.Record("W1AW", "0.475.000", "0.475.000", "630m"), type);
            Assert.Equal(old, now);
            Assert.Equal("w1aw630m", a.DupKey(a.Record("W1AW", "0.475.000", "0.475.000", ""), "callAndBand"));
        });
    }
}
