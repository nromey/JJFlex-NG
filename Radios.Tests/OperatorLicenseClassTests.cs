#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HamBands;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// One licence class per operator, the operator form and Settings the same
    /// (#1239, ruled by Noel 2026-11-23). The form offers Settings' four
    /// classes, opens on the class in the operator's licence file (Settings
    /// wins), starts a new operator on General, and writes ONLY the class into
    /// that file, after the operator itself is saved and before the change is
    /// reported. An existing operator with no file, or a file with no class,
    /// still reads Extra: nothing already on disk changes meaning.
    /// </summary>
    /// <remarks>
    /// <para>The form's decisions are the app's Shared seams
    /// (<c>PersonalData.LicenseClassAtOpen</c>, <c>RecordLicenseClass</c> and
    /// <c>SaveOperatorLicense</c>), called from the built app assembly in a
    /// collectible context, as <see cref="OperatorSaveKeepsOldFileTests"/>
    /// does. <b>Never <c>PersonalData.Add</c> or <c>Update</c>, and never the
    /// form:</b> both open a WinForms dialog. Where they call the seams, and in
    /// what order, is pinned by source. Files live in a temporary folder. Needs
    /// <c>JJFlexRadio.vbproj</c> built first and says so if it is not.</para>
    /// </remarks>
    [Collection(BuiltAppAssemblyCollection.Name)]
    public sealed class OperatorLicenseClassTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jjf-oplicense-" + Guid.NewGuid().ToString("N"));
        private string RadiosDir => Path.Combine(_root, "Radios");
        private const string Op = "K1ABC_Test_Operator";

        public OperatorLicenseClassTests() => Directory.CreateDirectory(RadiosDir);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private string FileOf(string name) => Path.Combine(RadiosDir, name + "_licenseConfig.xml");

        private LicenseConfig Live(string name = Op) => LicenseConfig.LoadOrCarry(RadiosDir, _root, name, out _);

        private void Existing(Bands.Licenses cls, string name = Op)
        {
            new LicenseConfig
            {
                LicenseClass = cls,
                TxLockout = true,
                BoundaryNotifications = false,
                Country = "CA",
                EnforceTxRules = false,
            }.Save(RadiosDir, name);
        }

        // ---------------- what an operator reads ----------------

        [Fact]
        public void An_existing_operator_with_no_licence_file_reads_Extra_on_the_form_and_in_the_lockout()
        {
            Assert.False(File.Exists(FileOf(Op)));
            Assert.Equal(Bands.Licenses.extra, Live().LicenseClass);
            var (stored, shown) = AtOpen(Op);
            Assert.Equal(Bands.Licenses.extra, stored);
            Assert.Equal(Bands.Licenses.extra, shown);
        }

        [Fact]
        public void A_licence_file_with_no_class_element_reads_Extra()
        {
            File.WriteAllText(FileOf(Op),
                "<?xml version=\"1.0\"?>\r\n<LicenseConfig><TxLockout>true</TxLockout><Country>CA</Country></LicenseConfig>\r\n");
            var live = Live();
            Assert.Equal(Bands.Licenses.extra, live.LicenseClass);
            Assert.True(live.TxLockout, "positive control: the file really was read");
            Assert.Equal(Bands.Licenses.extra, AtOpen(Op).Shown);
        }

        [Fact]
        public void A_corrupt_licence_file_reads_as_before_and_a_form_save_leaves_it_exactly_as_it_is()
        {
            File.WriteAllText(FileOf(Op), "<LicenseConfig><LicenseClass>general");   // cut short
            byte[] before = File.ReadAllBytes(FileOf(Op));
            Assert.Equal(Bands.Licenses.extra, Live().LicenseClass);   // what Load has always given it
            Assert.Equal(Bands.Licenses.extra, AtOpen(Op).Shown);

            Assert.Equal("CorruptLeftInPlace", Save(Op, Op, shown: Bands.Licenses.extra, chosen: Bands.Licenses.general, isNew: false));
            Assert.Equal(before, File.ReadAllBytes(FileOf(Op)));
        }

        [Fact]
        public void A_new_operator_from_the_form_opens_on_General_and_reads_General()
        {
            var (stored, shown) = AtOpen(null);
            Assert.Equal(Bands.Licenses.general, stored);
            Assert.Equal(Bands.Licenses.general, shown);

            Assert.Equal("Saved", Save(null, Op, shown, chosen: shown, isNew: true));
            Assert.Equal(Bands.Licenses.general, Live().LicenseClass);
            LicenseConfig.LoadOrCarry(RadiosDir, _root, Op, out bool firstRun);
            Assert.False(firstRun, "the new operator's class is already chosen, so start-up does not call it a first run");
            // Everything else is the defaults a first run would have saved.
            var fresh = new LicenseConfig();
            Assert.Equal(fresh.TxLockout, Live().TxLockout);
            Assert.Equal(fresh.Country, Live().Country);
        }

        [Fact]
        public void A_form_edit_changes_only_the_class_and_keeps_the_rest_of_the_licence_file()
        {
            Existing(Bands.Licenses.extra);
            Assert.Equal("Saved", Save(Op, Op, shown: Bands.Licenses.extra, chosen: Bands.Licenses.general, isNew: false));
            var live = Live();
            Assert.Equal(Bands.Licenses.general, live.LicenseClass);
            Assert.True(live.TxLockout, "a form edit switched the transmit lockout off");
            Assert.Equal("CA", live.Country);
            Assert.False(live.BoundaryNotifications);
            Assert.False(live.EnforceTxRules);
        }

        [Fact]
        public void An_untouched_class_writes_nothing_and_a_stored_class_the_form_cannot_offer_stays()
        {
            Existing(Bands.Licenses.novice);
            byte[] before = File.ReadAllBytes(FileOf(Op));
            var (stored, shown) = AtOpen(Op);
            Assert.Equal(Bands.Licenses.novice, stored);
            // #1309 (batch 31): a held Novice reads as Novice. It read as
            // Technician until then, which Part 97 makes too permissive above
            // 30 MHz; changed on purpose.
            Assert.Equal(Bands.Licenses.novice, shown);

            Assert.Equal("Unchanged", Save(Op, Op, shown, chosen: shown, isNew: false));
            Assert.Equal(before, File.ReadAllBytes(FileOf(Op)));
            // The record's own class stays in step with the file.
            Assert.Equal(Bands.Licenses.novice, Record(stored, shown, shown, isNew: false));
        }

        [Fact]
        public void An_untouched_existing_operator_with_no_file_gets_none_so_start_up_still_asks()
        {
            var (stored, shown) = AtOpen(Op);
            Assert.Equal("Unchanged", Save(Op, Op, shown, chosen: shown, isNew: false));
            Assert.False(File.Exists(FileOf(Op)));
            Assert.Equal(Bands.Licenses.extra, Record(stored, shown, shown, isNew: false));
        }

        [Fact]
        public void A_rename_carries_the_whole_licence_file_to_the_new_name()
        {
            Existing(Bands.Licenses.advanced);
            const string renamed = "K1XYZ_Test_Operator";
            Assert.Equal("Saved", Save(Op, renamed, shown: Bands.Licenses.advanced, chosen: Bands.Licenses.advanced, isNew: false));
            var live = Live(renamed);
            Assert.Equal(Bands.Licenses.advanced, live.LicenseClass);
            Assert.True(live.TxLockout);
            Assert.Equal("CA", live.Country);
        }

        [Fact]
        public void The_record_carries_the_chosen_class_when_one_was_chosen()
        {
            Assert.Equal(Bands.Licenses.general, Record(Bands.Licenses.extra, Bands.Licenses.extra, Bands.Licenses.general, isNew: false));
            Assert.Equal(Bands.Licenses.general, Record(Bands.Licenses.general, Bands.Licenses.general, Bands.Licenses.general, isNew: true));
        }

        // ---------------- the five classes, one vocabulary ----------------

        [Theory]
        [InlineData(Bands.Licenses.novice, Bands.Licenses.novice)]   // #1309: was Technician
        [InlineData(Bands.Licenses.technition, Bands.Licenses.technition)]
        [InlineData(Bands.Licenses.general, Bands.Licenses.general)]
        [InlineData(Bands.Licenses.advanced, Bands.Licenses.advanced)]
        [InlineData(Bands.Licenses.extra, Bands.Licenses.extra)]
        [InlineData(Bands.Licenses.none, Bands.Licenses.extra)]
        public void A_held_class_reads_as_one_of_the_five(Bands.Licenses stored, Bands.Licenses shown)
        {
            Assert.Equal(shown, LicenseConfig.ShownClass(stored));
        }

        // #1309 (batch 31): Settings' own four-entry map is gone; the list is
        // LicenseConfig's five, and a held Novice opens on Novice.
        [Fact]
        public void Settings_shows_a_held_Novice_as_Novice_from_the_one_class_list()
        {
            string settings = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs")).Replace("\r\n", "\n");
            Assert.DoesNotContain("LicenseClassMap", settings, StringComparison.Ordinal);
            int loop = settings.IndexOf("foreach (var cls in LicenseConfig.OfferedClasses)", StringComparison.Ordinal);
            Assert.True(loop > 0);
            string populate = settings.Substring(loop, settings.IndexOf("LicenseClassCombo.SelectedIndex = licIdx;", loop, StringComparison.Ordinal) - loop);
            Assert.Contains("LicenseClassCombo.Items.Add(LicenseConfig.ClassLabel(cls));", populate, StringComparison.Ordinal);
            Assert.Contains("LicenseConfig.ShownClass(_licenseConfig.LicenseClass)", populate, StringComparison.Ordinal);
            Assert.Contains("LicenseConfig.OfferedClasses[i] == shownClass", populate, StringComparison.Ordinal);
        }

        [Fact]
        public void Settings_OK_writes_the_class_only_when_the_list_was_moved()
        {
            string settings = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "JJFlexWpf", "Dialogs", "SettingsDialog.xaml.cs")).Replace("\r\n", "\n");
            // Repair round 1: these two lines pinned the bug, a baseline captured at
            // open and never advanced. The commit now goes through the pure
            // CommitClassFromList, whose returned position is the next baseline
            // (behaviour in NoviceHeldClassTests), and nothing else writes the class.
            Assert.Contains("_licenseClassIndexCommitted = licIdx;", settings, StringComparison.Ordinal);
            Assert.Contains("_licenseClassIndexCommitted = LicenseConfig.CommitClassFromList(_licenseConfig, selIdx, _licenseClassIndexCommitted);", settings, StringComparison.Ordinal);
            Assert.DoesNotContain("_licenseClassIndexAtOpen", settings, StringComparison.Ordinal);
            Assert.Equal(0, Occurrences(settings, "_licenseConfig.LicenseClass = "));
        }

        // ---------------- where the app saves it, by source ----------------

        [Fact]
        public void The_form_save_writes_the_licence_after_the_operator_is_saved_and_before_the_reload()
        {
            string src = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "PersonalData.vb")).Replace("\r\n", "\n");

            // An edit: after the save succeeded, BEFORE reportEvent, whose
            // OperatorChange.Announce reloads the live licence.
            string update = SourceMember.VisualBasic(src, "Public Function Update(ByVal id As Integer) As Boolean");
            int saved = update.IndexOf("If SaveThenReplace(ops, id, theOp, oldPath, theOp.pathName, Function() TryWrite(theOp)) Then", StringComparison.Ordinal);
            int license = update.IndexOf("SaveFormLicense(oldName, theOp, isNew:=False)", StringComparison.Ordinal);
            int report = update.IndexOf("reportEvent()", StringComparison.Ordinal);
            int failed = update.IndexOf("Else", saved, StringComparison.Ordinal);
            Assert.True(saved > 0 && license > saved && report > license && failed > report,
                "the licence is written inside the save's success, before the change is reported");
            Assert.True(update.IndexOf("Dim oldName As String = UniqueOpName(ops(id))", StringComparison.Ordinal) is int n && n > 0 && n < saved,
                "the old name is taken before the edited operator replaces it");

            // An add: only when the save succeeded, so a failed add leaves no licence file.
            string add = SourceMember.VisualBasic(src, "Public Function Add() As Integer");
            int added = add.IndexOf("rv = SaveThenAdd(", StringComparison.Ordinal);
            int addLicense = add.IndexOf("If rv >= 0 Then SaveFormLicense(Nothing, theOp, isNew:=True)", StringComparison.Ordinal);
            Assert.True(added > 0 && addLicense > added, "a new operator's licence is written only after its save succeeded");

            // The reload the edit relies on: the operator change reloads the licence the lockout reads.
            string globals = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "globals.vb"));
            Assert.Contains("Radios.OperatorChange.Announce(BaseConfigDir, currentOperatorUniqueName())", globals, StringComparison.Ordinal);
            string events = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "ApplicationEvents.vb"));
            Assert.Contains("handlers.License = Radios.LicenseConfig.LoadOrCarry(", events, StringComparison.Ordinal);
            Assert.Contains("Radios.OperatorChange.Follow(\"Home's filter presets, band memory and license\",", events, StringComparison.Ordinal);
        }

        [Fact]
        public void A_form_save_on_the_current_operator_is_what_the_reload_reads()
        {
            // The reload (loadHomeOperatorStores) reads LoadOrCarry from the
            // Radios folder and the settings root; that read sees the class
            // the form just wrote.
            Existing(Bands.Licenses.extra);
            Assert.Equal(Bands.Licenses.extra, Live().LicenseClass);
            Save(Op, Op, shown: Bands.Licenses.extra, chosen: Bands.Licenses.technition, isNew: false);
            Assert.Equal(Bands.Licenses.technition, LicenseConfig.LoadOrCarry(RadiosDir, _root, Op, out _).LicenseClass);
        }

        [Fact]
        public void The_form_lists_the_five_and_records_through_the_seams()
        {
            string src = File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "PersonalInfo.vb")).Replace("\r\n", "\n");
            Assert.Contains("For Each l As Bands.Licenses In LicenseConfig.OfferedClasses", src, StringComparison.Ordinal);
            Assert.Contains("LicenseList.Items.Add(LicenseConfig.ClassLabel(l))", src, StringComparison.Ordinal);
            Assert.DoesNotContain("[Enum].GetValues(GetType(Bands.Licenses))", src, StringComparison.Ordinal);
            Assert.DoesNotContain("LicenseList.SelectedIndex = .License", src, StringComparison.Ordinal);
            Assert.Contains("PersonalData.LicenseClassAtOpen(", src, StringComparison.Ordinal);
            Assert.Contains(".License = PersonalData.RecordLicenseClass(licenseStored, LicenseShown, LicenseChosen, Not updateFlag)", src, StringComparison.Ordinal);
        }

        // ---------------- the form's list and its save, line by line ----------------
        //
        // Repair 1: three ordinary regressions in the form layer left every test
        // above green. The list position and the class are mapped by two app
        // seams, tested by behaviour; the three form lines that call them, and
        // the save that hands the opened and chosen classes on, can only be
        // reached through the form itself, so they are pinned by source.

        [Theory]
        [InlineData(0, Bands.Licenses.extra)]
        [InlineData(1, Bands.Licenses.general)]
        [InlineData(2, Bands.Licenses.technition)]
        [InlineData(3, Bands.Licenses.advanced)]
        [InlineData(4, Bands.Licenses.novice)]
        public void Each_list_position_saves_the_class_it_shows(int index, Bands.Licenses cls)
        {
            // Settings' order, so position 0 is Extra: read as the enum value it
            // would save none, which with the lockout on refuses every band.
            Assert.Equal(cls, AtListIndex(index, shown: Bands.Licenses.technition));
            Assert.Equal(cls, LicenseConfig.OfferedClasses[index]);
        }

        [Theory]
        [InlineData(Bands.Licenses.general)]
        [InlineData(Bands.Licenses.technition)]
        public void No_selection_keeps_the_class_the_list_opened_on(Bands.Licenses shown)
        {
            Assert.Equal(shown, AtListIndex(-1, shown));
        }

        // #1309 (batch 31): five classes, the three issued first, then the two
        // held but no longer issued. Changed on purpose from #1239's four.
        [Theory]
        [InlineData(Bands.Licenses.extra, 0)]
        [InlineData(Bands.Licenses.general, 1)]
        [InlineData(Bands.Licenses.technition, 2)]
        [InlineData(Bands.Licenses.advanced, 3)]
        [InlineData(Bands.Licenses.novice, 4)]
        public void The_list_opens_on_the_operators_own_class(Bands.Licenses cls, int index)
        {
            // An untouched edit of a General operator must save General, not
            // the first row, Extra.
            Assert.Equal(index, ListIndex(cls));
            Assert.Equal(cls, AtListIndex(ListIndex(cls), shown: Bands.Licenses.none));
        }

        // These two pins read CODE, not text (#1284): comments are stripped
        // first, so a real line commented out no longer satisfies its own pin,
        // and all whitespace (line breaks and VB's " _" continuations included)
        // is removed from both the source and the pinned text, so a harmless
        // line break inside an argument list no longer fails one.

        [Fact]
        public void The_form_opens_and_closes_its_list_through_the_seams_and_nowhere_else()
        {
            string src = VbCodeWithoutComments(File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "PersonalInfo.vb")));

            string load = Flat(SourceMember.VisualBasic(src, "Private Sub PersonalInfo_Load("));
            Assert.Contains(Flat("LicenseList.SelectedIndex = PersonalData.LicenseListIndex(lic.Shown)"), load, StringComparison.Ordinal);
            Assert.Equal(1, Occurrences(Flat(src), Flat("LicenseList.SelectedIndex = ")));

            string ok = Flat(SourceMember.VisualBasic(src, "Private Sub OKButton_Click("));
            Assert.Contains(Flat("LicenseChosen = PersonalData.LicenseAtListIndex(LicenseList.SelectedIndex, LicenseShown)"), ok, StringComparison.Ordinal);
            Assert.Equal(1, Occurrences(ok, Flat("LicenseChosen = ")));
            Assert.DoesNotContain(Flat("CType(LicenseList.SelectedIndex"), Flat(src), StringComparison.Ordinal);
            // The chosen class is set before the record's own class is taken from it.
            Assert.True(ok.IndexOf(Flat("LicenseChosen = "), StringComparison.Ordinal)
                        < ok.IndexOf(Flat(".License = PersonalData.RecordLicenseClass("), StringComparison.Ordinal));
        }

        [Fact]
        public void The_form_save_hands_on_the_opened_class_as_shown_and_the_chosen_class_as_chosen()
        {
            // Traded, SaveClass sees the old class as the choice and every class
            // change on the form is silently dropped.
            string src = VbCodeWithoutComments(File.ReadAllText(Path.Combine(FieldKeyMapScan.RepoRoot(), "PersonalData.vb")));
            string save = Flat(SourceMember.VisualBasic(src, "Private Shared Sub SaveFormLicense("));
            Assert.Contains(Flat("shown:=PersonalInfo.LicenseShown, chosen:=PersonalInfo.LicenseChosen, isNew:=isNew)"), save, StringComparison.Ordinal);
            Assert.Equal(1, Occurrences(save, Flat("SaveOperatorLicense(")));
            Assert.Equal(1, Occurrences(save, "PersonalInfo.LicenseShown"));
            Assert.Equal(1, Occurrences(save, "PersonalInfo.LicenseChosen"));
        }

        [Fact]
        public void The_code_reader_drops_comments_and_whitespace_but_keeps_strings()
        {
            // Instrument control for the two pins above.
            string src = "    x = 1 ' LicenseChosen = gone\n"
                + "    ' SaveOperatorLicense(a)\n"
                + "    s = \"it's ' kept\" ' gone\n"
                + "    REM gone too\n"
                + "    Call(a, _\n"
                + "         b)\n";
            string code = VbCodeWithoutComments(src);
            Assert.DoesNotContain("LicenseChosen", code, StringComparison.Ordinal);
            Assert.DoesNotContain("SaveOperatorLicense", code, StringComparison.Ordinal);
            Assert.DoesNotContain("gone", code, StringComparison.Ordinal);
            Assert.Contains("\"it's ' kept\"", code, StringComparison.Ordinal);
            Assert.Contains(Flat("Call(a, b)"), Flat(code), StringComparison.Ordinal);
            Assert.Equal(6, code.Split('\n').Length - 1);   // line structure kept, for SourceMember
        }

        /// <summary>
        /// VB source with every comment removed (from an apostrophe outside a
        /// string literal, or a REM statement, to the end of its line). Line
        /// structure is kept, so <see cref="SourceMember.VisualBasic"/> can still
        /// find a member's End line.
        /// </summary>
        private static string VbCodeWithoutComments(string source)
        {
            var lines = source.Replace("\r\n", "\n").Split('\n');
            for (int n = 0; n < lines.Length; n++)
            {
                string line = lines[n];
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("REM ", StringComparison.OrdinalIgnoreCase)
                    || trimmed.TrimEnd().Equals("REM", StringComparison.OrdinalIgnoreCase))
                {
                    lines[n] = "";
                    continue;
                }
                bool inString = false;
                for (int i = 0; i < line.Length; i++)
                {
                    if (line[i] == '"') inString = !inString;   // "" inside a string toggles twice
                    else if (line[i] == '\'' && !inString) { line = line.Substring(0, i); break; }
                }
                lines[n] = line;
            }
            return string.Join("\n", lines);
        }

        /// <summary>All whitespace removed, so line breaks and continuations cannot break a match.</summary>
        private static string Flat(string text)
        {
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (char c in text.Replace(" _\n", " "))
                if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }

        private static int Occurrences(string text, string what)
        {
            int n = 0;
            for (int i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + what.Length, StringComparison.Ordinal)) n++;
            return n;
        }

        // ---------------- the app's seams ----------------

        private Bands.Licenses AtListIndex(int index, Bands.Licenses shown) =>
            Enum.Parse<Bands.Licenses>((string)Call("LicenseAtListIndex", index, shown)!);

        private static int ListIndex(Bands.Licenses cls) => (int)Call("LicenseListIndex", cls)!;

        private (Bands.Licenses Stored, Bands.Licenses Shown) AtOpen(string? opName)
        {
            object? r = Call("LicenseClassAtOpen", RadiosDir, _root, opName);
            Type t = r!.GetType();
            return ((Bands.Licenses)Convert.ToInt32(t.GetField("Item1")!.GetValue(r)),
                    (Bands.Licenses)Convert.ToInt32(t.GetField("Item2")!.GetValue(r)));
        }

        private string Save(string? oldName, string newName, Bands.Licenses shown, Bands.Licenses chosen, bool isNew) =>
            Call("SaveOperatorLicense", RadiosDir, _root, oldName, newName, shown, chosen, isNew)!.ToString()!;

        private Bands.Licenses Record(Bands.Licenses stored, Bands.Licenses shown, Bands.Licenses chosen, bool isNew) =>
            Enum.Parse<Bands.Licenses>((string)Call("RecordLicenseClass", stored, shown, chosen, isNew)!);

        /// <summary>
        /// Call a Shared member of the built app's PersonalData. The app's own
        /// copy of HamBands is a different assembly from this test's, so an
        /// enum argument is converted to the parameter's own type.
        /// </summary>
        private static object? Call(string method, params object?[] args)
        {
            string? app = BuiltAppAssembly();
            Assert.True(app != null,
                "Build JJFlexRadio.vbproj (Debug, x64) first: this test calls the operator store "
                + "from the built app assembly.");
            object? result = null;
            WeakReference unloaded = CallInIsolatedContext(app!, method, args, r => result = r);
            for (int i = 0; unloaded.IsAlive && i < 20; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CallInIsolatedContext(string app, string method, object?[] args, Action<object?> report)
        {
            var context = new AssemblyLoadContext("operator-license", isCollectible: true);
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
                MethodInfo? step = personal.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                Assert.True(step != null, "PersonalData." + method + " is missing");
                ParameterInfo[] ps = step!.GetParameters();
                object?[] converted = new object?[args.Length];
                for (int i = 0; i < args.Length; i++)
                    converted[i] = args[i] is Enum e && ps[i].ParameterType.IsEnum
                        ? Enum.ToObject(ps[i].ParameterType, Convert.ToInt32(e))
                        : args[i];
                object? r;
                try { r = step.Invoke(null, converted); }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                    throw;
                }
                // Hand back plain values, never the isolated context's own types.
                if (r is Enum en) report(en.ToString());
                else if (r != null && r.GetType().IsGenericType && r.GetType().Name.StartsWith("ValueTuple", StringComparison.Ordinal))
                {
                    Type t = r.GetType();
                    report((Convert.ToInt32(t.GetField("Item1")!.GetValue(r)), Convert.ToInt32(t.GetField("Item2")!.GetValue(r))));
                }
                else report(r);
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
