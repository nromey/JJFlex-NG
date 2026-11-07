using System;
using System.IO;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// #909: the document views' WebView2 data (About, the HTML document
    /// viewer, the Fixer) follows the settings root when JJFLEX_CONFIG_DIR moves
    /// it, and stays where it always was otherwise.
    /// </summary>
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class LocalDataRootTests
    {
        private const string Roaming = @"C:\Users\op\AppData\Roaming";
        private const string Local = @"C:\Users\op\AppData\Local";

        [Fact]
        public void An_ordinary_run_keeps_the_local_folder_it_always_used()
        {
            string root = LocalDataRoot.Resolve(Path.Combine(Roaming, "JJFlexRadio"), Roaming, Local);
            Assert.Equal(Path.Combine(Local, "JJFlexRadio"), root);
        }

        [Fact]
        public void A_relocated_settings_root_carries_the_local_root_inside_it()
        {
            const string temp = @"D:\scratch\jjflex-run-7";
            string root = LocalDataRoot.Resolve(temp, Roaming, Local);

            Assert.Equal(Path.Combine(temp, LocalDataRoot.RelocatedFolderName), root);
            Assert.DoesNotContain(Local, root, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_relocated_root_that_happens_to_sit_in_roaming_is_still_relocated()
        {
            // Only the ordinary folder's own parent counts as ordinary; a
            // throwaway tree nested deeper under roaming app data does not.
            string temp = Path.Combine(Roaming, "scratch", "JJFlexRadio");
            Assert.Equal(Path.Combine(temp, LocalDataRoot.RelocatedFolderName),
                         LocalDataRoot.Resolve(temp, Roaming, Local));
        }

        [Fact]
        public void This_process_resolves_to_a_folder_under_its_own_roots()
        {
            // The live property, against the live settings root: either the
            // ordinary local folder or a folder inside the relocated tree.
            // Never the sign-in folder.
            string folder = LocalDataRoot.DocumentViewsWebView2;
            Assert.EndsWith("WebView2", folder);
            Assert.NotEqual(Path.Combine(RadioConfig.AppDataRoot, "WebView2"), folder,
                            StringComparer.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("JJFlexWpf/Dialogs/AboutDialog.xaml.cs")]
        [InlineData("JJFlexWpf/Dialogs/HtmlInfoDialog.cs")]
        [InlineData("JJFlexWpf/Dialogs/FixerDialog.cs")]
        public void The_document_views_take_their_folder_from_LocalDataRoot(string relative)
        {
            string src = File.ReadAllText(Path.Combine(FlexBasePartials.RepoRoot(), relative));
            Assert.DoesNotContain("SpecialFolder.LocalApplicationData", src);
            Assert.DoesNotContain("SpecialFolder.ApplicationData", src);
            Assert.Contains("LocalDataRoot.DocumentViewsWebView2", src);
        }
    }
}
