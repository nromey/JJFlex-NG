#nullable enable

using System.Collections.Generic;
using System.Linq;
using Radios;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The pure half of the window census (#154): the filter, the order, the
    /// protected classes, and the sentences. Nothing here enumerates a real
    /// window or calls a native function.
    /// </summary>
    // Joined to the RadioConfig statics collection because the sentence
    // tests read the lexicon, and LexiconTests in that collection clears the
    // process-wide store — see LexiconKeyCoverageTests for the day that bit.
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class DesktopWindowCensusTests
    {
        private static DesktopWindowRecord W(
            string title = "Window", string cls = "Some", int pid = 500, string proc = "someapp",
            bool alive = true, bool responding = true, bool ours = false, bool fg = false,
            bool enabled = true, nint owner = 0, string ownerTitle = "", bool ownerEnabled = true,
            bool tool = false, bool cloaked = false, nint hwnd = 1)
            => new(hwnd, title, cls, pid, proc, alive, responding, ours, fg, enabled,
                owner, ownerTitle, ownerEnabled, tool, cloaked);

        // ────────────────────────────────────────────────────────────────
        //  The measured picture, 2026-09-02
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void TheSelectorEnabledOverADisabledShellIsAModal()
        {
            // hwnd 7538446 visible and ENABLED, owned by the main window;
            // the main window hwnd 526084 enabled=False.
            var selector = W(title: "Select Radio", cls: "HwndWrapper[jjflexible;;29c59d40]",
                ours: true, enabled: true, owner: 526084, ownerTitle: "JJ Flexible Radio Access",
                ownerEnabled: false, hwnd: 7538446);
            var shell = W(title: "JJ Flexible Radio Access", ours: true, enabled: false, hwnd: 526084);

            Assert.True(selector.HoldsAModal);
            Assert.False(selector.IsBehindAModal);
            Assert.True(shell.IsBehindAModal);
            Assert.False(shell.HoldsAModal);
        }

        // ────────────────────────────────────────────────────────────────
        //  Filter and order
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void TheForegroundComesFirstThenOursThenTheRest()
        {
            var arranged = DesktopWindowCensus.Arrange(new[]
            {
                W(title: "Mail", hwnd: 1),
                W(title: "Select Radio", ours: true, hwnd: 2),
                W(title: "Thief", fg: true, hwnd: 3),
                W(title: "JJ Flexible Radio Access", ours: true, hwnd: 4),
                W(title: "Browser", hwnd: 5),
            });

            Assert.Equal(new nint[] { 3, 2, 4, 1, 5 }, arranged.Select(w => w.Hwnd).ToArray());
        }

        [Fact]
        public void UntitledAndToolAndCloakedWindowsAreLeftOut_ButTheForegroundNeverIs()
        {
            var arranged = DesktopWindowCensus.Arrange(new[]
            {
                W(title: "", hwnd: 1),
                W(title: "Tool", tool: true, hwnd: 2),
                W(title: "Elsewhere", cloaked: true, hwnd: 3),
                W(title: "", fg: true, hwnd: 4),      // untitled, but it has the keyboard
                W(title: "Real", hwnd: 5),
            });

            Assert.Equal(new nint[] { 4, 5 }, arranged.Select(w => w.Hwnd).ToArray());
        }

        [Fact]
        public void ACloakedForegroundIsStillLeftOut()
        {
            // Cloaked means another virtual desktop or a parked store app;
            // a "foreground" there is not on this screen.
            var arranged = DesktopWindowCensus.Arrange(new[] { W(fg: true, cloaked: true) });
            Assert.Empty(arranged);
        }

        [Fact]
        public void TheDesktopIsRecognisedByClass()
        {
            Assert.True(W(cls: "Progman").IsDesktop);
            Assert.True(W(cls: "WorkerW").IsDesktop);
            Assert.False(W(cls: "HwndWrapper[jjflexible;;x]").IsDesktop);
        }

        // ────────────────────────────────────────────────────────────────
        //  What the watchdog must never take from
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void TheProtectedClassesAreTheSecurityPrompts()
        {
            // Same five as tools/uia-probe/Native.cs, for the same reason.
            foreach (var cls in new[]
                     {
                         "Shell_SystemDialogProxy", "#32770", "Credential Dialog Xaml Host",
                         "ConsentUI", "$$$Secure UI$$$",
                     })
            {
                Assert.True(DesktopWindowCensus.IsProtectedForegroundClass(cls), cls);
            }
        }

        [Fact]
        public void OrdinaryClassesAreNotProtected()
        {
            Assert.False(DesktopWindowCensus.IsProtectedForegroundClass("HwndWrapper[jjflexible;;x]"));
            Assert.False(DesktopWindowCensus.IsProtectedForegroundClass("CabinetWClass"));
            Assert.False(DesktopWindowCensus.IsProtectedForegroundClass("Chrome_WidgetWin_1"));
            Assert.False(DesktopWindowCensus.IsProtectedForegroundClass(""));
            Assert.False(DesktopWindowCensus.IsProtectedForegroundClass(null));
        }

        // ────────────────────────────────────────────────────────────────
        //  The sentences
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void TheSelectorRowSaysItHasTheKeyboardAndIsBlockingTheShell()
        {
            var selector = W(title: "Select Radio", ours: true, fg: true, enabled: true,
                owner: 526084, ownerTitle: "JJ Flexible Radio Access", ownerEnabled: false);

            string row = DesktopWindowCensusSpeech.Row(selector, 1);

            Assert.StartsWith("1. Select Radio.", row);
            Assert.Contains("JJ Flexible Radio Access, this program", row);
            Assert.Contains("Has the keyboard", row);
            Assert.Contains("blocking JJ Flexible Radio Access", row);
            Assert.EndsWith(".", row);
        }

        [Fact]
        public void TheShellRowSaysItIsWaitingBehindADialog()
        {
            string row = DesktopWindowCensusSpeech.Row(
                W(title: "JJ Flexible Radio Access", ours: true, enabled: false), 2);

            Assert.Contains("Waiting behind a dialog", row);
            Assert.DoesNotContain("Has the keyboard", row);
        }

        [Fact]
        public void AWindowWhoseProgramHasExitedGetsTheOrphanCalloutFirst()
        {
            // The orphan-process family (#14, #21) showing itself — named
            // when it happens rather than left as a puzzle, and named ahead
            // of anything else about the window.
            string status = DesktopWindowCensusSpeech.StatusPhrase(
                W(title: "Ghost", alive: false, fg: true));

            Assert.StartsWith("Its program has exited", status);
            Assert.Contains("Has the keyboard", status);
        }

        [Fact]
        public void ANotRespondingProgramIsSaidSo_ButNotWhenItIsGone()
        {
            Assert.Contains("not responding",
                DesktopWindowCensusSpeech.StatusPhrase(W(responding: false)));
            Assert.DoesNotContain("not responding",
                DesktopWindowCensusSpeech.StatusPhrase(W(alive: false, responding: false)));
        }

        [Fact]
        public void AnOrdinaryWindowHasNoStatusAndAShorterRow()
        {
            string row = DesktopWindowCensusSpeech.Row(W(title: "Mail", proc: "mailapp"), 3);

            Assert.Equal("3. Mail. mailapp.", row);
        }

        [Fact]
        public void ProgramsAreNamedInPlainWordsWhereKnown_AndByExeOtherwise()
        {
            Assert.Equal("Windows File Explorer",
                DesktopWindowCensusSpeech.ProgramPhrase(W(proc: "explorer")));
            Assert.Equal("NVDA, your screen reader",
                DesktopWindowCensusSpeech.ProgramPhrase(W(proc: "nvda")));
            Assert.Equal("the Windows desktop",
                DesktopWindowCensusSpeech.ProgramPhrase(W(proc: "explorer", cls: "Progman")));
            Assert.Equal("obscuretool",
                DesktopWindowCensusSpeech.ProgramPhrase(W(proc: "obscuretool")));
            Assert.Equal("a program that could not be identified",
                DesktopWindowCensusSpeech.ProgramPhrase(W(proc: "", alive: false)));
        }

        [Fact]
        public void AnUntitledWindowIsSaidToBeUntitled()
        {
            string row = DesktopWindowCensusSpeech.Row(W(title: "", fg: true), 1);
            Assert.StartsWith("1. Untitled window.", row);
        }

        [Fact]
        public void TheTitleCarriesTheCountAndCountsOne()
        {
            Assert.Equal("What is on my screen, 5 windows", DesktopWindowCensusSpeech.Title(5));
            Assert.Equal("What is on my screen, 1 window", DesktopWindowCensusSpeech.Title(1));
        }

        [Fact]
        public void TheReclaimAnnouncementNamesTheThiefWhenItCan()
        {
            Assert.Equal("Windows File Explorer had taken the keyboard. You are back in Select Radio.",
                DesktopWindowCensusSpeech.ReclaimAnnouncement(W(proc: "explorer"), "Select Radio"));
            Assert.Equal("obscuretool had taken the keyboard. You are back in Select Radio.",
                DesktopWindowCensusSpeech.ReclaimAnnouncement(W(proc: "obscuretool"), "Select Radio"));
            Assert.Equal("Another program had taken the keyboard. You are back in Select Radio.",
                DesktopWindowCensusSpeech.ReclaimAnnouncement(W(proc: ""), "Select Radio"));
        }

        [Fact]
        public void TheTheftRowNamesWhoWhereAndWhen()
        {
            var theft = new ForegroundTheft(
                new System.DateTime(2026, 9, 2, 14, 5, 0), W(proc: "explorer"), "Select Radio");

            string row = DesktopWindowCensusSpeech.LastTheftRow(theft);

            Assert.Contains("Windows File Explorer", row);
            Assert.Contains("Select Radio", row);
            Assert.Contains(theft.When.ToString("t", System.Globalization.CultureInfo.CurrentCulture), row);
        }

        [Fact]
        public void TheSnapshotFindsItsForeground()
        {
            var snapshot = new DesktopWindowSnapshot(System.DateTime.Now,
                DesktopWindowCensus.Arrange(new[] { W(hwnd: 1), W(hwnd: 2, fg: true) }));

            Assert.Equal((nint)2, snapshot.Foreground!.Hwnd);
            Assert.Equal((nint)2, snapshot.Windows[0].Hwnd);
        }
    }
}
