using System;
using System.IO;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #760: "Save Station Setup to Radio" sits on the Radio menu beside
    /// Profiles on This Radio, and the Command Finder says so. The native menu
    /// needs a shell window to build, so the placement is read from the source;
    /// the harness walks the menu.
    /// </summary>
    public sealed class SaveStationSetupMenuPlaceTests
    {
        private static string Read(string relative)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "JJFlexRadio.sln"))) d = d.Parent;
            Assert.True(d != null, "could not find the repository root");
            return File.ReadAllText(Path.Combine(d!.FullName, relative));
        }

        private const string Item = "\"Save Station Setup to Radio\", SaveStationSetupFromMenu";

        [Fact]
        public void There_is_one_item_and_it_is_on_the_Radio_menu()
        {
            string menu = Read("JJFlexWpf/NativeMenuBar.cs");

            Assert.Contains("AddWired(radio, " + Item + ");", menu, StringComparison.Ordinal);
            Assert.DoesNotContain("AddWired(selSub, " + Item, menu, StringComparison.Ordinal);
            int first = menu.IndexOf(Item, StringComparison.Ordinal);
            Assert.Equal(first, menu.LastIndexOf(Item, StringComparison.Ordinal));
        }

        [Fact]
        public void It_comes_straight_after_Profiles_on_This_Radio()
        {
            string menu = Read("JJFlexWpf/NativeMenuBar.cs");
            int submenu = menu.IndexOf("AddSubmenu(radio, \"Profiles on This Radio\")", StringComparison.Ordinal);
            int item = menu.IndexOf("AddWired(radio, " + Item, StringComparison.Ordinal);
            Assert.True(submenu > 0 && item > submenu);

            // Nothing else lands on the Radio menu between them.
            string between = menu.Substring(submenu + 10, item - submenu - 10);
            Assert.DoesNotContain("(radio, ", between, StringComparison.Ordinal);
        }

        [Fact]
        public void The_Command_Finder_gives_the_new_place()
        {
            string inventory = Read("JJFlexWpf/KeyInventory.cs");

            Assert.Contains("MenuText = \"Radio menu, Save Station Setup to Radio\"", inventory, StringComparison.Ordinal);
            Assert.DoesNotContain("Selection submenu, Save Station Setup", inventory, StringComparison.Ordinal);
        }
    }
}
