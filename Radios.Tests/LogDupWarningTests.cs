using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Repair round 3 of junk box batch 31 (#1282): the full log form's
    /// duplicate warning never borrows the previous contact's band.
    /// </summary>
    /// <remarks>
    /// <para>Since repair round 2 the duplicate key takes an empty BAND from the
    /// session's TXFREQ, else FREQ (<c>LogDupChecking.BandKey</c>). But
    /// <c>LogEntry.CallSign_Leave</c> primed the session's BAND, MODE, serial and
    /// call before building the key, never its frequencies, which still held the
    /// previous record's. So a radio-off W1AW after a 20 m W1AW was warned as a
    /// duplicate, a radio-off duplicate the parent caught was missed, and a
    /// contact at a frequency ADIF has no band for keyed to the previous band.
    /// BAND is empty at that moment on every new contact today, because the
    /// form's own frequency and band fill-in is gated on a power flag nothing
    /// sets (a separate register entry; not touched here).</para>
    /// <para>The logger is VB in the app, so this loads the BUILT app (Debug x64)
    /// into a collectible context, as <see cref="LogBandKeyTests"/> does, and
    /// runs the REAL <c>CallSign_Leave</c>, <c>entryFieldsToSession</c>,
    /// <c>clear</c> and duplicate dictionary on an uninitialized <c>LogEntry</c>
    /// over a real <c>JJLogLib.DefaultLog</c> field set (its controls are created
    /// but no window handle is), on an STA thread. The duplicate sound's gate,
    /// <c>OutputChannelRecorder.RenderEnabled</c>, is turned off first and put
    /// back after. The settings root is the test process's throwaway tree. No
    /// log file is opened or written: a write runs only its in-memory half.</para>
    /// </remarks>
    [Collection(BuiltAppAssemblyCollection.Name)]
    public sealed class LogDupWarningTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        private sealed class LogForm
        {
            private readonly Type _entry, _session, _dup, _key, _dupTypes, _globals;
            private readonly object _le, _sessionObj, _form;
            public string DupType = "callAndBand";

            /// <summary>Disposes the field set's controls on this thread, so nothing of
            /// theirs is left to finalize after the load context unloads.</summary>
            public void Dispose()
            {
                if (Get(_le, "TheForm") is IDisposable form) form.Dispose();
                Set(_globals, "Dups", null);
            }

            public LogForm(AssemblyLoadContext ctx, Assembly app)
            {
                Type[] types = app.GetTypes();
                _entry = types.Single(t => t.Name == "LogEntry");
                _session = types.Single(t => t.Name == "LogSession");
                _dup = types.Single(t => t.Name == "LogDupChecking");
                _key = _dup.GetNestedType("keyElement", Any)!;
                _dupTypes = _dup.GetNestedType("DupTypes", Any)!;
                _globals = types.Single(t => t.Name == "globals");
                Assert.Null(Get(_globals, "RigControl"));   // no radio: the form fills in nothing itself

                Assembly logLib = ctx.LoadFromAssemblyName(new AssemblyName("JJLogLib"));
                Type logElement = logLib.GetType("JJLogLib.Logs+LogElement", true)!;
                Type defaultLog = logLib.GetType("JJLogLib.DefaultLog", true)!;
                Type logField = logLib.GetType("JJLogLib.LogField", true)!;
                Type fieldElement = ctx.LoadFromAssemblyName(new AssemblyName("adif")).GetType("adif.LogFieldElement", true)!;

                // The form's field set, built as Logs.GetLog builds it.
                _le = Activator.CreateInstance(logElement, "DefaultLog", defaultLog)!;
                Set(_le, "Fields", Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), logField))!);
                Set(_le, "TheForm", Activator.CreateInstance(defaultLog, _le)!);
                Call(_le, "procSetup", null, _le);

                // The session, its logged fields as LogSession.Start builds them.
                _sessionObj = RuntimeHelpers.GetUninitializedObject(_session);
                Set(_sessionObj, "FormData", _le);
                var dict = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), fieldElement))!;
                foreach (object fld in ((IDictionary)Get(_le, "Fields")!).Values)
                    if ((bool)Get(fld, "IsLogged")!)
                        dict.Add((string)Get(fld, "ADIFTag")!, Activator.CreateInstance(fieldElement, (string)Get(fld, "ADIFTag")!)!);
                Set(_sessionObj, "FieldDictionary", dict);
                foreach (string need in new[] { "CALL", "MODE", "BAND", "STX", "TXFREQ", "FREQ" })
                    Assert.True(dict.Contains(need), "the default log has no " + need);

                // The form, uninitialized: no constructor, no window.
                _form = RuntimeHelpers.GetUninitializedObject(_entry);
                Set(_form, "session", _sessionObj);
                Set(_form, "oldCall", "");
            }

            public void FreshLog()
            {
                Call(_sessionObj, "Clear", Type.EmptyTypes);
                Set(_sessionObj, "serial", 1);
                Set(_globals, "Dups", Activator.CreateInstance(_dup, Enum.Parse(_dupTypes, DupType))!);
                Set(_le, "onFile", false);
                Call(_form, "clear", Type.EmptyTypes);
            }

            public string Screen(string tag)
            {
                var m = _le.GetType().GetMethod("getScreenText", Any, null, new[] { typeof(string), typeof(string).MakeByRefType() }, null)!;
                object?[] a = { tag, null };
                return (bool)m.Invoke(_le, a)! ? (string?)a[1] ?? "" : "";
            }

            public void TypeIn(string tag, string text)
            {
                bool was = (bool)Get(_le, "IgnoreTextChange")!;
                Set(_le, "IgnoreTextChange", true);
                Call(_le, "setScreenText", new[] { typeof(string), typeof(string) }, tag, text);
                Set(_le, "IgnoreTextChange", was);
            }

            public string Freq(ulong hz) => (string)Call(_globals, "FormatFreq", new[] { typeof(ulong) }, hz)!;

            public string SessionKey() =>
                (string)Get(Activator.CreateInstance(_key, _sessionObj, Enum.Parse(_dupTypes, DupType))!, "Value")!;

            /// <summary>
            /// One new contact: the call and mode typed; for "radio on", what the
            /// form's fill-in would put on the screen (both frequencies, and ADIF's
            /// band); then the REAL CallSign_Leave, whose key and count are read;
            /// then, when written, the in-memory half of the write in its own order
            /// (entryFieldsToSession, the key into the dictionary, serial, clear).
            /// </summary>
            public (string Key, string Count, string StoredKey) Contact(string call, ulong? radioHz, bool write = true,
                string? rx = null, string? tx = null)
            {
                TypeIn("CALL", call);
                TypeIn("MODE", "SSB");
                if (radioHz is { } hz)
                {
                    TypeIn("FREQ", Freq(hz));
                    TypeIn("TXFREQ", Freq(hz));
                    string band = HamBands.Bands.AdifBand(hz);
                    if (band != "") TypeIn("BAND", band);
                }
                if (rx != null) TypeIn("FREQ", rx);
                if (tx != null) TypeIn("TXFREQ", tx);
                Call(_form, "CallSign_Leave", null, new object?[] { null, EventArgs.Empty });
                string key = SessionKey(), count = Screen("$DUPCOUNT"), stored = "";
                if (write)
                {
                    Call(_form, "entryFieldsToSession", System.Type.EmptyTypes);
                    object k = Activator.CreateInstance(_key, _sessionObj, Enum.Parse(_dupTypes, DupType))!;
                    Call(Get(_globals, "Dups")!, "AddToDictionary", null, k);
                    stored = (string)Get(k, "Value")!;
                    Set(_sessionObj, "serial", (int)Get(_sessionObj, "serial")! + 1);
                    Call(_form, "clear", System.Type.EmptyTypes);
                }
                return (key, count, stored);
            }
        }

        private static object? Get(object o, string name)
        {
            Type t = o as Type ?? o.GetType();
            object? target = o is Type ? null : o;
            for (Type? c = t; c != null; c = c.BaseType)
            {
                if (c.GetField(name, Any | BindingFlags.DeclaredOnly) is { } f) return f.GetValue(target);
                if (c.GetProperty(name, Any | BindingFlags.DeclaredOnly) is { } p) return p.GetValue(target);
            }
            throw new MissingMemberException(t.Name, name);
        }

        private static void Set(object o, string name, object? v)
        {
            Type t = o as Type ?? o.GetType();
            object? target = o is Type ? null : o;
            for (Type? c = t; c != null; c = c.BaseType)
            {
                if (c.GetField(name, Any | BindingFlags.DeclaredOnly) is { } f) { f.SetValue(target, v); return; }
                if (c.GetProperty(name, Any | BindingFlags.DeclaredOnly) is { } p) { p.SetValue(target, v); return; }
            }
            throw new MissingMemberException(t.Name, name);
        }

        private static object? Call(object o, string name, Type[]? sig, params object?[] args)
        {
            Type t = o as Type ?? o.GetType();
            object? target = o is Type ? null : o;
            for (Type? c = t; c != null; c = c.BaseType)
            {
                MethodInfo? m = sig == null
                    ? c.GetMethods(Any | BindingFlags.DeclaredOnly).SingleOrDefault(x => x.Name == name)
                    : c.GetMethod(name, Any | BindingFlags.DeclaredOnly, null, sig, null);
                if (m != null) return m.Invoke(target, args);
            }
            throw new MissingMethodException(t.Name, name);
        }

        private static void WithForm(Action<LogForm> body)
        {
            Assert.True(TestSettingsRoot.Failure == null, "Refusing: " + TestSettingsRoot.Failure);
            string? app = BuiltAppAssembly();
            Assert.True(app != null,
                "Build JJFlexRadio.vbproj (Debug, x64) first: this test runs the log form's real CallSign_Leave.");
            Exception? failure = null;
            WeakReference? unloaded = null;
            var sta = new Thread(() =>
            {
                try { unloaded = Run(app!, body); }
                catch (Exception ex) { failure = ex; }
            });
            sta.SetApartmentState(ApartmentState.STA);
            sta.Start();
            sta.Join();
            for (int i = 0; unloaded != null && unloaded.IsAlive && i < 20; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            if (failure != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(
                    failure is TargetInvocationException { InnerException: { } inner } ? inner : failure).Throw();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference Run(string app, Action<LogForm> body)
        {
            var context = new AssemblyLoadContext("log-dup-warning", isCollectible: true);
            string folder = Path.GetDirectoryName(app)!;
            context.Resolving += (ctx, name) =>
            {
                string candidate = Path.Combine(folder, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };
            // The duplicate sound's gate, on the Radios the app binds to; put back after.
            Type recorder = context.LoadFromAssemblyName(new AssemblyName("Radios")).GetType("Radios.OutputChannelRecorder", true)!;
            bool renderWas = (bool)recorder.GetProperty("RenderEnabled")!.GetValue(null)!;
            bool recordWas = (bool)recorder.GetProperty("RecordEnabled")!.GetValue(null)!;
            if (!recordWas) recorder.GetMethod("Configure")!.Invoke(null, new object?[] { false, false, null });
            try
            {
                Assert.False((bool)recorder.GetProperty("RenderEnabled")!.GetValue(null)!, "the duplicate sound could not be silenced");
                var form = new LogForm(context, context.LoadFromAssemblyPath(app));
                try { body(form); }
                finally { form.Dispose(); }   // the form's controls, before their assembly unloads
            }
            finally
            {
                if (!recordWas) recorder.GetMethod("Configure")!.Invoke(null, new object?[] { renderWas, false, null });
                context.Unload();
            }
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

        private const ulong M20 = 14_250_000, M40 = 7_150_000, M20b = 14_200_000, NoAdifBand = 10_000_000;

        [Fact]
        public void Positive_control_the_harness_sees_the_key_follow_the_band_and_counts_a_real_duplicate() => WithForm(f =>
        {
            f.FreshLog();
            var a = f.Contact("W1AW", M20);
            var b = f.Contact("W1AW", M40);
            var c = f.Contact("W1AW", M20b);
            Assert.Equal("w1aw20m", a.Key);
            Assert.Equal("w1aw40m", b.Key);
            Assert.Equal("1", b.Count);
            Assert.Equal("w1aw20m", c.Key);
            Assert.Equal("2", c.Count);
        });

        [Theory]
        [InlineData("callAndBand", "w1aw")]
        [InlineData("callAndBandAndMode", "w1awssb")]
        public void A_contact_with_no_band_does_not_borrow_the_previous_contact_s_band(string dupType, string key) => WithForm(f =>
        {
            f.DupType = dupType;
            f.FreshLog();
            f.Contact("W1AW", M20);
            var off = f.Contact("W1AW", null);
            Assert.Equal(key, off.Key);
            Assert.Equal("1", off.Count);   // no false warning
        });

        [Fact]
        public void A_radio_off_duplicate_is_caught_again_after_a_contact_on_another_band() => WithForm(f =>
        {
            f.FreshLog();
            f.Contact("W1AW", null);
            f.Contact("K1ABC", M20);
            var again = f.Contact("W1AW", null);
            Assert.Equal("w1aw", again.Key);
            Assert.Equal("2", again.Count);
        });

        [Fact]
        public void A_contact_where_ADIF_has_no_band_is_keyed_without_one() => WithForm(f =>
        {
            f.FreshLog();
            f.Contact("W1AW", M20);
            var wwv = f.Contact("W1AW", NoAdifBand);
            Assert.Equal("w1aw", wwv.Key);
            Assert.Equal("1", wwv.Count);
        });

        [Fact]
        public void A_typed_cross_band_contact_is_keyed_to_its_transmit_band_and_the_warning_matches_what_it_stores() => WithForm(f =>
        {
            f.FreshLog();
            f.Contact("W1AW", M20);
            var cross = f.Contact("W1AW", null, rx: f.Freq(145_900_000), tx: f.Freq(435_100_000));
            Assert.Equal("w1aw70cm", cross.Key);
            Assert.Equal(cross.Key, cross.StoredKey);
        });
    }
}
