using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #914, the press half: jjprobe's INPUT structure must be Windows' own
    /// sizeof(INPUT), 40 bytes in a 64-bit process and 28 in a 32-bit one,
    /// or SendInput rejects every key and the probe reports "silent".
    /// </summary>
    /// <remarks>
    /// The tool has no test project and is not referenced here, so this
    /// compiles its REAL <c>tools/uia-probe/NativeInput.cs</c> with Roslyn and
    /// measures the type with <see cref="Marshal.SizeOf(Type)"/> in this
    /// process. Radios.Tests runs x64 only (as does jjprobe), so the 32-bit
    /// size is asserted through the same expression the tool uses and measured
    /// only when a 32-bit run exists.
    /// </remarks>
    public sealed class JjprobeInputLayoutTests
    {
        // NativeInput.cs is one half of a partial class; these are the two
        // constants it uses from the other half, Native.cs.
        private const string OtherHalf = @"
global using System;
namespace JJFlex.UiaProbe;
internal static partial class Native
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
}";

        private static Type CompileNative(string source)
        {
            var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(x => MetadataReference.CreateFromFile(x)).ToList();
            var compilation = CSharpCompilation.Create("JjprobeInput_" + Guid.NewGuid().ToString("N"),
                new[] { CSharpSyntaxTree.ParseText(OtherHalf), CSharpSyntaxTree.ParseText(source) }, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            using var output = new MemoryStream();
            var emitted = compilation.Emit(output);
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            return Assembly.Load(output.ToArray()).GetType("JJFlex.UiaProbe.Native")!;
        }

        private static string NativeInputSource() =>
            File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), "tools", "uia-probe", "NativeInput.cs"));

        [Fact]
        public void The_INPUT_structure_is_the_size_SendInput_requires()
        {
            Type native = CompileNative(NativeInputSource());
            Type input = native.GetNestedType("INPUT", BindingFlags.NonPublic)!;
            Assert.NotNull(input);

            int size = Marshal.SizeOf(input);
            Assert.Equal(IntPtr.Size == 8 ? 40 : 28, size);
            if (Environment.Is64BitProcess) Assert.Equal(40, size);

            // The tool's own expectation agrees with Windows', and the size it
            // passes is the size it measures.
            BindingFlags any = BindingFlags.Static | BindingFlags.NonPublic;
            Assert.Equal(size, (int)native.GetProperty("ExpectedInputSize", any)!.GetValue(null)!);
            Assert.Equal(size, (int)native.GetProperty("InputSize", any)!.GetValue(null)!);
        }

        [Fact]
        public void The_measurement_would_catch_the_old_keyboard_only_union()
        {
            // Positive control: the 2026-11-06 shape, a union holding
            // KEYBDINPUT alone, measures short in a 64-bit process, so the test
            // above is not green by construction.
            string old = NativeInputSource()
                .Replace("[FieldOffset(0)] public MOUSEINPUT mi;", "")
                .Replace("[FieldOffset(0)] public HARDWAREINPUT hi;", "");
            Assert.DoesNotContain("public MOUSEINPUT mi", old, StringComparison.Ordinal);
            Type native = CompileNative(old);
            int size = Marshal.SizeOf(native.GetNestedType("INPUT", BindingFlags.NonPublic)!);
            Assert.True(Environment.Is64BitProcess, "Radios.Tests runs x64; this control measures the 64-bit fault");
            Assert.Equal(32, size);
        }

        [Fact]
        public void A_refused_send_is_an_error_and_never_a_silent_press()
        {
            string native = NativeInputSource();
            Assert.Contains("uint sent = SendInput(1, new[] { inp }, InputSize);", native, StringComparison.Ordinal);
            Assert.Contains("if (sent != 1)", native, StringComparison.Ordinal);
            Assert.Contains("Marshal.GetLastWin32Error()", native, StringComparison.Ordinal);
            Assert.Contains("throw new SendInputFailedException(", native, StringComparison.Ordinal);

            string press = File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), "tools", "uia-probe", "Press.cs"));
            int at = press.IndexOf("catch (SendInputFailedException ex)", StringComparison.Ordinal);
            Assert.True(at > 0, "Press.Send no longer turns a refused send into a verdict");
            Assert.Contains("result.Verdict = \"not-sent\";", press.Substring(at, 600), StringComparison.Ordinal);
        }
    }
}
