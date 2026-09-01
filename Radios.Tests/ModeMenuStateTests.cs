using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Sprint 43 Track E, #311. The slice Mode submenu marks the mode you are
    /// actually in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Raised by Noel 2026-05-11</b> and called one of the two big findings
    /// of that session. Every mode row went in through <c>AddWired</c>, which
    /// takes no state getter, so no row could ever carry MF_CHECKED — and a
    /// screen reader announces "checked" / "not checked" off exactly that
    /// flag. Arrowing the Mode menu was therefore a guess-and-exit loop: leave
    /// the menu to hear what mode you are in, then go back in.
    /// </para>
    /// <para>
    /// <b>Not cosmetic, and not a lone oversight.</b> Slice select, TX slice,
    /// Classic tuning and the band jumps all marked their state already, which
    /// is what made this submenu surprising rather than merely plain.
    /// </para>
    /// <para>
    /// <b>Source-scanned</b> because <c>NativeMenuBar</c> lives in JJFlexWpf,
    /// which this project does not reference and must not: constructing that
    /// assembly's types is what puts dialogs on the operator's desktop. The
    /// menu is also raw Win32 HMENU, so there is nothing to interrogate
    /// without a real window.
    /// </para>
    /// </remarks>
    public sealed class ModeMenuStateTests
    {
        private const string MenuFile = "JJFlexWpf/NativeMenuBar.cs";

        [Fact]
        public void TheModeSubmenuCarriesAStateGetter()
        {
            string source = Read(MenuFile);

            // Positive control: the submenu still exists and is still built in
            // a loop. Without this a renamed local would let every absence
            // check below pass while checking nothing.
            Assert.Contains("var modeSub = AddSubmenu(slice, \"Mode\");", source,
                StringComparison.Ordinal);

            var modeAdds = Regex.Matches(source, @"Add(\w+)\(modeSub,")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .ToList();

            Assert.NotEmpty(modeAdds);

            // The mode rows themselves must be radio-checked. Next Mode and
            // Previous Mode sit on the same submenu and are ACTIONS, not
            // states, so AddWired is right for those two and only those two.
            Assert.Contains("AddRadioChecked(modeSub,", source, StringComparison.Ordinal);
            Assert.True(modeAdds.Count(x => x == "Wired") <= 2,
                "More than two plain AddWired rows on the Mode submenu. Only Next Mode and "
                + "Previous Mode are actions; a mode itself is a state and must be marked, "
                + "or the menu cannot tell an operator what mode they are in (#311).");
        }

        [Fact]
        public void ARadioGroupIsMarkedButNotSuffixedWithOnAndOff()
        {
            string source = Read(MenuFile);

            // AddChecked also rewrites the row as "{text}: On" / "{text}: Off"
            // so a TOGGLE announces its state in words. In a group of ten modes
            // that is nine "Off"s on the way to one "On" — the noise that
            // teaches an operator to stop listening — and on a row whose text
            // is "USB\tAlt+U" the suffix lands inside the accelerator column.
            Assert.Contains("_radioGroupItems", source, StringComparison.Ordinal);
            Assert.Contains("if (_radioGroupItems.Contains(id)) continue;", source,
                StringComparison.Ordinal);

            // And the set must be cleared with the rest of the menu state, or a
            // rebuild would carry stale ids that suppress a real toggle's words.
            Assert.Contains("_radioGroupItems.Clear();", source, StringComparison.Ordinal);
        }

        private static string Read(string relative)
        {
            string path = Path.Combine(RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path),
                "Could not find " + relative + " (looked at " + path + "). A test that cannot "
                + "find its subject passes every absence check it makes.");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) return dir.FullName;
                dir = dir.Parent;
            }
            return AppContext.BaseDirectory;
        }
    }
}
