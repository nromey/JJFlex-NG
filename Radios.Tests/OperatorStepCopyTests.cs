#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The operator record's copy keeps the tuning steps (#346 J2). The
    /// Operators dialog edits a copy made by <c>PersonalData.personal_v1.New(p)</c>
    /// and writes the copy back; that constructor copied about thirty fields and
    /// not <c>CoarseTuneStep</c> or <c>FineTuneStep</c>, so saving any edit (a
    /// callsign, which the no-callsign gate now sends a VOX user to enter)
    /// wrote the 5000 Hz and 100 Hz defaults over the remembered steps.
    /// </summary>
    /// <remarks>
    /// Loads the built app (Debug or Release x64) into a collectible context,
    /// as <see cref="NewLogHeaderTests"/> does, and calls the real constructor.
    /// It needs <c>JJFlexRadio.vbproj</c> built first and says so if it is not.
    /// No form, window or file is created.
    /// </remarks>
    [Collection(BuiltAppAssemblyCollection.Name)]
    public sealed class OperatorStepCopyTests
    {
        [Fact]
        public void An_edited_copy_of_the_operator_keeps_the_tuning_steps()
        {
            string? app = BuiltAppAssembly();
            Assert.True(app != null,
                "Build JJFlexRadio.vbproj (Debug, x64) first: this test calls the operator record's "
                + "real copy constructor from the built app assembly.");

            WeakReference unloaded = CheckInIsolatedContext(app!);
            for (int i = 0; unloaded.IsAlive && i < 20; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference CheckInIsolatedContext(string app)
        {
            var context = new AssemblyLoadContext("operator-step-copy", isCollectible: true);
            string folder = Path.GetDirectoryName(app)!;
            context.Resolving += (ctx, name) =>
            {
                string candidate = Path.Combine(folder, name.Name + ".dll");
                return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
            };
            try
            {
                Assembly assembly = context.LoadFromAssemblyPath(app);
                Type record = assembly.GetTypes().Single(t => t.Name == "personal_v1");
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                object original = Activator.CreateInstance(record)!;
                record.GetField("CoarseTuneStep", any)!.SetValue(original, 2500);
                record.GetField("FineTuneStep", any)!.SetValue(original, 10);
                // ...and the step lists (#346 part two).
                record.GetField("CoarseStepList", any)!.SetValue(original, new[] { 250, 3000 });
                record.GetField("FineStepList", any)!.SetValue(original, new[] { 2, 20 });
                // Positive control that the fields are read: a field the
                // constructor has always copied survives too.
                record.GetField("callSign", any)!.SetValue(original, "N0CALL");

                ConstructorInfo copy = record.GetConstructor(any, null, new[] { record }, null)!;
                Assert.NotNull(copy);
                object edited = copy.Invoke(new[] { original });

                Assert.Equal("N0CALL", record.GetField("callSign", any)!.GetValue(edited));
                Assert.Equal(2500, record.GetField("CoarseTuneStep", any)!.GetValue(edited));
                Assert.Equal(10, record.GetField("FineTuneStep", any)!.GetValue(edited));
                Assert.Equal(new[] { 250, 3000 }, (int[])record.GetField("CoarseStepList", any)!.GetValue(edited)!);
                Assert.Equal(new[] { 2, 20 }, (int[])record.GetField("FineStepList", any)!.GetValue(edited)!);
                // A copy, not the same array: editing one must not edit the other.
                Assert.NotSame(record.GetField("CoarseStepList", any)!.GetValue(original),
                    record.GetField("CoarseStepList", any)!.GetValue(edited));
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
