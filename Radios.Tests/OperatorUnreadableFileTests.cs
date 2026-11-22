#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// An operator file that cannot be read at start-up no longer stops every
    /// operator loading behind an "Exception" box with the .NET stack (#1253).
    /// A read-only file now loads; a file that cannot be opened or parsed is
    /// traced, skipped, named once in a plain notice and left where it is; and
    /// nothing later deletes it, or writes over it without first keeping a copy.
    /// </summary>
    /// <remarks>
    /// <para>Drives the built app's <c>PersonalData</c> (its constructor, its
    /// per-file read, its writes and <c>RemoveAt</c>) in a collectible load
    /// context against a temporary folder, as
    /// <see cref="OperatorSaveKeepsOldFileTests"/> does. Needs
    /// <c>JJFlexRadio.vbproj</c> built first and says so if it is not.</para>
    /// <para><b>No test here may reach a message box</b> (one would put a window
    /// on the live desk and hang the run). So: both new notices and the
    /// not-saved notice are replaced with recorders before anything runs; every
    /// folder holds at least one well-formed operator file, so the constructor
    /// never opens the add-operator dialog; the folder exists, so its
    /// create-folder box cannot fire; and <c>Write</c>, whose ordinary failure
    /// still shows the box, is only called on a file the test has first proved
    /// read-only through the same check <c>Write</c> uses. Every other write
    /// goes through <c>TryWrite</c>, which never shows anything.</para>
    /// <para><b>Never <c>Add</c> or <c>Update</c>:</b> both open the WinForms
    /// operator dialog first. <c>Update</c>'s guard is pinned by source.</para>
    /// </remarks>
    [Collection(BuiltAppAssemblyCollection.Name)]
    public sealed class OperatorUnreadableFileTests : IDisposable
    {
        private readonly string _base = Path.Combine(Path.GetTempPath(), "jjf-opunread-" + Guid.NewGuid().ToString("N"));
        private string OpsDir => Path.Combine(_base, "Operators");
        private readonly List<FileStream> _held = new();

        public OperatorUnreadableFileTests() => Directory.CreateDirectory(OpsDir);

        public void Dispose()
        {
            foreach (var h in _held) h.Dispose();
            try
            {
                foreach (string f in Directory.GetFiles(_base, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(_base, recursive: true);
            }
            catch { }
        }

        // ---- files ----

        private static string OperatorXml(string fileName, bool isDefault, string handle, string extra = "") =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
            + "<personal_v1 xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">\r\n"
            + "  <fileName>" + fileName + "</fileName>\r\n"
            + "  <DefaultFlag>" + (isDefault ? "true" : "false") + "</DefaultFlag>\r\n"
            + "  <callSign>TEST" + handle.Length + "</callSign>\r\n"
            + "  <handl>" + handle + "</handl>\r\n"
            + "  <fullName>" + handle + " Operator</fullName>\r\n"
            + extra
            + "</personal_v1>\r\n";

        /// <summary>A well-formed operator file whose inner name matches its own.</summary>
        private string Good(string name, bool isDefault = false, string extra = "") =>
            Put(name, OperatorXml(name, isDefault, Path.GetFileNameWithoutExtension(name), extra));

        private string Put(string name, string text)
        {
            string path = Path.Combine(OpsDir, name);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return path;
        }

        private string ReadOnly(string path)
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
            return path;
        }

        /// <summary>Another program holding the file with no sharing at all.</summary>
        private void Hold(string path) =>
            _held.Add(new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None));

        private string[] KeptCopies() =>
            Directory.GetFiles(OpsDir, "*.unreadable-*.bak").Select(Path.GetFileName).OrderBy(n => n).ToArray()!;

        // ---- a read-only file loads ----

        [Fact]
        public void A_read_only_operator_file_now_loads_with_the_others_and_nothing_is_said_at_start_up()
        {
            Good("A_First.xml", isDefault: true);
            string b = ReadOnly(Good("B_Second.xml"));
            byte[] before = File.ReadAllBytes(b);

            var seen = Load(app => app.Handles());

            Assert.Equal(new[] { "A_First", "B_Second" }, seen.Handles);
            Assert.Empty(seen.NotLoaded);
            Assert.Empty(seen.ReadOnlyTold);
            Assert.Equal(before, File.ReadAllBytes(b));
        }

        [Fact]
        public void A_change_to_a_read_only_operator_is_traced_and_said_once_never_boxed()
        {
            Good("A_First.xml", isDefault: true);
            string b = ReadOnly(Good("B_Second.xml"));
            byte[] before = File.ReadAllBytes(b);

            var seen = Load(app =>
            {
                // Write's ordinary failure still shows the box. Prove, through
                // the very check Write makes, that this write takes the
                // read-only branch before calling it at all.
                Assert.Equal(Path.GetFullPath(b), Path.GetFullPath(app.PathName(1)));
                Assert.True(app.IsReadOnlyFile(app.PathName(1)), "the test's precondition: Write must see this file as read-only");
                app.Write(1);
                app.Write(1);
            });

            Assert.Equal(new[] { "B_Second.xml" }, seen.ReadOnlyTold);
            Assert.Empty(seen.NotLoaded);
            Assert.Equal(before, File.ReadAllBytes(b));
            Assert.Empty(AtomicFile.TempFilesFor(b));   // the failed save left no temporary file
        }

        // ---- a file that cannot be read is skipped, and the rest load ----

        [Fact]
        public void A_corrupt_file_is_skipped_and_the_operator_after_it_still_loads()
        {
            string bad = Put("A_Broken.xml", "this is not an operator file");
            Good("B_Good.xml", isDefault: true);
            Good("C_Third.xml");
            byte[] before = File.ReadAllBytes(bad);

            var seen = Load(app => app.Handles());

            Assert.Equal(new[] { "B_Good", "C_Third" }, seen.Handles);
            Assert.Equal(new[] { "A_Broken.xml" }, seen.NotLoaded);
            Assert.Equal(1, seen.NotLoadedCalls);
            Assert.Equal(before, File.ReadAllBytes(bad));   // left exactly where and as it was
            Assert.Empty(KeptCopies());
        }

        [Fact]
        public void A_file_another_program_holds_is_skipped_and_named()
        {
            Good("A_Good.xml", isDefault: true);
            string held = Good("B_Held.xml");
            Hold(held);

            var seen = Load(app => app.Handles());

            Assert.Equal(new[] { "A_Good" }, seen.Handles);
            Assert.Equal(new[] { "B_Held.xml" }, seen.NotLoaded);
        }

        [Fact]
        public void Every_file_that_could_not_be_read_is_named_in_one_notice()
        {
            Put("A_Broken.xml", "<personal_v1><fileName>A_Broken.xml");   // truncated
            Good("B_Good.xml", isDefault: true);
            Put("C_Broken.xml", "");
            var seen = Load(app => app.Handles());
            Assert.Equal(new[] { "B_Good" }, seen.Handles);
            Assert.Equal(new[] { "A_Broken.xml", "C_Broken.xml" }, seen.NotLoaded);
            Assert.Equal(1, seen.NotLoadedCalls);
        }

        [Fact]
        public void A_file_that_reads_but_cannot_be_set_up_is_skipped_too()
        {
            // A negative log count throws in the set-up step, after the read.
            Good("A_Odd.xml", extra: "  <NumberOfLogs>-1</NumberOfLogs>\r\n");
            Good("B_Good.xml", isDefault: true);
            var seen = Load(app => app.Handles());
            Assert.Equal(new[] { "B_Good" }, seen.Handles);
            Assert.Equal(new[] { "A_Odd.xml" }, seen.NotLoaded);
        }

        [Fact]
        public void The_per_file_read_says_what_happened_and_never_throws()
        {
            string good = Good("A_Good.xml", isDefault: true);
            string corrupt = Put("B_Corrupt.xml", "<personal_v1><fileName>B");
            string oldBad = Put("C_OldBad.xml", "<personal><DefaultFlag>not a boolean</DefaultFlag></personal>");
            string old = Put("D_Old.xml", "<?xml version=\"1.0\"?><personal><fileName>D_Old.xml</fileName><handl>D</handl><fullName>Old</fullName></personal>");
            string readOnly = ReadOnly(Good("E_ReadOnly.xml"));
            string held = Good("F_Held.xml");
            Hold(held);

            WithApp(asm =>
            {
                var good1 = Read(asm, good);
                Assert.NotNull(good1.Op); Assert.False(good1.Converted); Assert.Null(good1.Failure);

                var corrupt1 = Read(asm, corrupt);
                Assert.Null(corrupt1.Op); Assert.Contains("neither operator format", corrupt1.Failure);

                // The old format's own failure is caught too: it used to
                // escape the constructor from inside a Catch block.
                var oldBad1 = Read(asm, oldBad);
                Assert.Null(oldBad1.Op); Assert.Contains("neither operator format", oldBad1.Failure);

                // Positive control: an old-format file still converts.
                var old1 = Read(asm, old);
                Assert.NotNull(old1.Op); Assert.True(old1.Converted);

                var ro = Read(asm, readOnly);
                Assert.NotNull(ro.Op);

                var held1 = Read(asm, held);
                Assert.Null(held1.Op); Assert.Contains("could not be opened", held1.Failure);
            });
        }

        // ---- nothing writes over, or deletes, a file that could not be loaded ----

        [Fact]
        public void A_save_onto_a_file_that_could_not_be_loaded_keeps_a_copy_of_it_first()
        {
            // A's inner name points at B's file, as a hand-copied operator's
            // can; B could not be read. A save of A then targets B.
            Put("A_Copied.xml", OperatorXml("B_Broken.xml", isDefault: true, handle: "Copied"));
            string b = Put("B_Broken.xml", "B's unreadable but possibly salvageable record");
            byte[] before = File.ReadAllBytes(b);

            var seen = Load(app =>
            {
                Assert.True(app.TryWrite(0));
                Assert.False(app.LastWriteKeptUnreadableFile);
                Assert.True(app.TryWrite(0));   // the second save keeps no second copy
            });

            Assert.Equal(new[] { "B_Broken.xml" }, seen.NotLoaded);
            string[] kept = KeptCopies();
            Assert.Single(kept);
            Assert.StartsWith("B_Broken.xml.unreadable-", kept[0]);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(OpsDir, kept[0])));
            Assert.Contains("<handl>Copied</handl>", File.ReadAllText(b));   // the save itself went ahead
        }

        [Fact]
        public void A_save_onto_a_file_that_could_not_be_loaded_or_copied_does_not_happen()
        {
            Put("A_Copied.xml", OperatorXml("B_Held.xml", isDefault: true, handle: "Copied"));
            string b = Good("B_Held.xml");
            byte[] before = File.ReadAllBytes(b);
            Hold(b);   // held at start-up and still held: it can be neither read nor copied

            var seen = Load(app =>
            {
                Assert.False(app.TryWrite(0));
                Assert.True(app.LastWriteKeptUnreadableFile, "the refusal is the protection's, not an ordinary failure");
            });

            Assert.Equal(new[] { "B_Held.xml" }, seen.NotLoaded);
            Assert.Empty(KeptCopies());
            foreach (var h in _held) h.Dispose();
            _held.Clear();
            Assert.Equal(before, File.ReadAllBytes(b));
        }

        [Fact]
        public void A_start_up_write_cannot_reach_a_bad_file_that_loads_later_in_the_folder()
        {
            // A, read first, has a plaintext password, so start-up's credential
            // migration writes it at once, to the path inside it: Z's file.
            // Every file is read before any is set up, so Z is already known
            // as unreadable when that write comes, and is kept first.
            Put("A_Early.xml", OperatorXml("Z_Broken.xml", isDefault: true, handle: "Early",
                extra: "  <CallbookPassword>plaintext</CallbookPassword>\r\n"));
            string z = Put("Z_Broken.xml", "Z's unreadable record");
            byte[] before = File.ReadAllBytes(z);

            var seen = Load(app => app.Handles());

            Assert.Equal(new[] { "Z_Broken.xml" }, seen.NotLoaded);
            string[] kept = KeptCopies();
            Assert.Single(kept);
            Assert.Equal(before, File.ReadAllBytes(Path.Combine(OpsDir, kept[0])));
        }

        [Fact]
        public void Deleting_a_read_only_operator_says_so_and_never_throws()
        {
            Good("A_First.xml", isDefault: true);
            string b = ReadOnly(Good("B_Second.xml"));

            var seen = Load(app =>
            {
                Assert.True(app.RemoveAt(1));
                Assert.Equal(new[] { "A_First" }, app.Handles());
            });

            Assert.True(File.Exists(b), "positive control: the read-only file really could not be deleted");
            Assert.Equal(new[] { "B_Second.xml" }, seen.ReadOnlyTold);
        }

        [Fact]
        public void Deleting_an_operator_whose_file_could_not_be_loaded_leaves_that_file()
        {
            Good("A_Good.xml", isDefault: true);
            Put("B_Pointer.xml", OperatorXml("C_Broken.xml", isDefault: false, handle: "Pointer"));
            string c = Put("C_Broken.xml", "C's unreadable record");

            var seen = Load(app => Assert.True(app.RemoveAt(1)));

            Assert.True(File.Exists(c), "a file that could not be loaded was deleted");
            Assert.Equal("C's unreadable record", File.ReadAllText(c));
            Assert.Equal(new[] { "C_Broken.xml", "C_Broken.xml" }, seen.NotLoaded);   // at start-up, then at the delete
            Assert.Equal(2, seen.NotLoadedCalls);
        }

        [Fact]
        public void Deleting_an_ordinary_operator_still_deletes_its_file()
        {
            Good("A_First.xml", isDefault: true);
            string b = Good("B_Second.xml");
            var seen = Load(app => Assert.True(app.RemoveAt(1)));
            Assert.False(File.Exists(b));
            Assert.Empty(seen.ReadOnlyTold);
            Assert.Empty(seen.NotLoaded);
        }

        [Fact]
        public void A_well_formed_folder_loads_exactly_as_before_and_saves_in_place()
        {
            string a = Good("A_First.xml", isDefault: true);
            Good("B_Second.xml");
            var seen = Load(app =>
            {
                Assert.Equal(new[] { "A_First", "B_Second" }, app.Handles());
                Assert.True(app.TryWrite(0));
            });
            Assert.Empty(seen.NotLoaded);
            Assert.Empty(KeptCopies());
            Assert.Contains("<handl>A_First</handl>", File.ReadAllText(a));
        }

        // ---- the kept copy's name ----

        [Fact]
        public void The_kept_copy_is_never_loaded_as_an_operator_nor_swept_as_a_temporary_file()
        {
            string good = Good("A_Good.xml", isDefault: true);
            string kept = "";
            WithApp(asm =>
            {
                Type pd = PersonalDataType(asm);
                kept = (string)pd.GetMethod("KeptCopyPath", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
                    .Invoke(null, new object[] { good, new DateTime(2026, 11, 22, 9, 15, 0, 123, DateTimeKind.Utc) })!;
                File.WriteAllText(kept, "kept");
                // Positive control for the temporary-file check: a real temporary name is found.
                File.WriteAllText(AtomicFile.NewTempPathFor(good), "temp");
                var toLoad = ((string[])pd.GetMethod("OperatorFilesToLoad", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
                    .Invoke(null, new object[] { OpsDir })!).Select(Path.GetFileName).ToArray();
                Assert.Equal(new[] { "A_Good.xml" }, toLoad);
            });
            Assert.Equal("A_Good.xml.unreadable-20261122T091500123Z.bak", Path.GetFileName(kept));
            Assert.False(kept.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            var temps = AtomicFile.TempFilesFor(good);
            Assert.Single(temps);
            Assert.DoesNotContain(kept, temps);
        }

        // ---- source pins: the paths a test cannot drive ----

        [Fact]
        public void Update_never_retires_a_file_that_could_not_be_loaded_and_Write_never_boxes_a_kept_refusal()
        {
            string src = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "PersonalData.vb")).Replace("\r\n", "\n");

            string update = SourceMember.VisualBasic(src, "Public Function Update(ByVal id As Integer) As Boolean");
            int guard = update.IndexOf("If FailedToLoadThisSession(oldPath)", StringComparison.Ordinal);
            int swap = update.IndexOf("oldPath = theOp.pathName", StringComparison.Ordinal);
            int save = update.IndexOf("If SaveThenReplace(ops, id, theOp, oldPath, theOp.pathName, Function() TryWrite(theOp)) Then", StringComparison.Ordinal);
            Assert.True(guard > 0 && swap > guard && save > swap,
                "Update must stop the rename from deleting an old file that could not be loaded, before it saves");

            string write = SourceMember.VisualBasic(src, "Friend Sub Write(ByVal op As personal_v1)");
            int kept = write.IndexOf("If LastWriteKeptUnreadableFile Then Return", StringComparison.Ordinal);
            int readOnly = write.IndexOf("If IsReadOnlyFile(op.pathName) Then", StringComparison.Ordinal);
            int box = write.IndexOf("Tracing.ErrMessageTrace(LastWriteFailure)", StringComparison.Ordinal);
            Assert.True(kept > 0 && readOnly > kept && box > readOnly,
                "Write must return quietly for a kept refusal and a read-only file before the box");

            string ctor = SourceMember.VisualBasic(src, "Public Sub New(ByVal baseDir As String)");
            Assert.DoesNotContain("File.Open(", ctor, StringComparison.Ordinal);
            Assert.DoesNotContain("Exit For", ctor, StringComparison.Ordinal);

            string read = SourceMember.VisualBasic(src, "Friend Shared Function ReadOperatorFile(fn As String) As OperatorFileRead");
            Assert.Equal(2, read.Split("New FileStream(fn, FileMode.Open, FileAccess.Read, FileShare.Read)").Length - 1);
            Assert.DoesNotContain("ErrMessageTrace", read, StringComparison.Ordinal);
            Assert.DoesNotContain("MessageBox", read, StringComparison.Ordinal);
        }

        // ---- plumbing ----

        private sealed class Seen
        {
            public List<string> NotLoaded { get; } = new();
            public int NotLoadedCalls { get; set; }
            public List<string> ReadOnlyTold { get; } = new();
            public int NotSavedCalls { get; set; }
            public string[] Handles { get; set; } = Array.Empty<string>();
        }

        /// <summary>The loaded <c>PersonalData</c>, through reflection.</summary>
        private sealed class App
        {
            private readonly object _pd;
            private readonly Type _type;
            public App(object pd) { _pd = pd; _type = pd.GetType(); }

            private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            private System.Collections.IList Ops => (System.Collections.IList)_type.GetField("ops", Any)!.GetValue(_pd)!;

            public string[] Handles() => Ops.Cast<object>()
                .Select(o => (string)o.GetType().GetField("handl")!.GetValue(o)!).ToArray();

            public string PathName(int id) => (string)Ops[id]!.GetType().GetProperty("pathName")!.GetValue(Ops[id])!;

            public bool IsReadOnlyFile(string path) =>
                (bool)_type.GetMethod("IsReadOnlyFile", Any)!.Invoke(null, new object[] { path })!;

            public void Write(int id) =>
                _type.GetMethod("Write", Any, new[] { Ops[id]!.GetType() })!.Invoke(_pd, new[] { Ops[id] });

            public bool TryWrite(int id) =>
                (bool)_type.GetMethod("TryWrite", Any)!.Invoke(_pd, new[] { Ops[id] })!;

            public bool LastWriteKeptUnreadableFile =>
                (bool)_type.GetProperty("LastWriteKeptUnreadableFile", Any)!.GetValue(_pd)!;

            public bool RemoveAt(int id) =>
                (bool)_type.GetMethod("RemoveAt", Any)!.Invoke(_pd, new object[] { id })!;
        }

        private sealed record ReadResult(object? Op, bool Converted, string? Failure);

        private static ReadResult Read(Assembly asm, string path)
        {
            Type pd = PersonalDataType(asm);
            object r = pd.GetMethod("ReadOperatorFile", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
                .Invoke(null, new object[] { path })!;
            Type rt = r.GetType();
            return new ReadResult(rt.GetField("Op")!.GetValue(r), (bool)rt.GetField("Converted")!.GetValue(r)!,
                                  (string?)rt.GetField("Failure")!.GetValue(r));
        }

        private static Type PersonalDataType(Assembly asm) => asm.GetTypes().Single(t => t.Name == "PersonalData");

        /// <summary>
        /// Replace every notice with a recorder, construct <c>PersonalData</c>
        /// over the test's folder, then run <paramref name="then"/> against it.
        /// </summary>
        private Seen Load(Action<App>? then = null)
        {
            var seen = new Seen();
            Assert.True(Directory.GetFiles(OpsDir, "*.xml").Length > 0,
                "every test folder must hold an operator file, or the constructor opens the add-operator dialog");
            WithApp(asm =>
            {
                Type pd = PersonalDataType(asm);
                const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                pd.GetField("OperatorFilesNotLoadedNotice", statics)!.SetValue(null,
                    new Action<IReadOnlyList<string>>(names => { seen.NotLoadedCalls++; seen.NotLoaded.AddRange(names); }));
                pd.GetField("OperatorFileReadOnlyNotice", statics)!.SetValue(null,
                    new Action<string>(name => seen.ReadOnlyTold.Add(name)));
                pd.GetField("OperatorNotSavedNotice", statics)!.SetValue(null, new Action(() => seen.NotSavedCalls++));

                object instance = pd.GetConstructor(new[] { typeof(string) })!.Invoke(new object[] { _base });
                var app = new App(instance);
                seen.Handles = app.Handles();
                then?.Invoke(app);
            });
            Assert.Equal(0, seen.NotSavedCalls);
            return seen;
        }

        private static void WithApp(Action<Assembly> body)
        {
            string? app = BuiltAppAssembly();
            Assert.True(app != null,
                "Build JJFlexRadio.vbproj (Debug, x64) first: this test drives the operator store "
                + "from the built app assembly.");
            WeakReference unloaded = RunInIsolatedContext(app!, body);
            for (int i = 0; unloaded.IsAlive && i < 20; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference RunInIsolatedContext(string app, Action<Assembly> body)
        {
            var context = new AssemblyLoadContext("operator-unreadable", isCollectible: true);
            string folder = Path.GetDirectoryName(app)!;
            context.Resolving += (ctx, name) =>
            {
                string candidate = Path.Combine(folder, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };
            try
            {
                Assembly assembly = context.LoadFromAssemblyPath(app);
                try { body(assembly); }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                }
            }
            finally
            {
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
    }
}
