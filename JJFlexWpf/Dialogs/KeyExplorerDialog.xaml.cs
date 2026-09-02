using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// The JJ key tree explorer — JJ key, slash. Walks the whole JJ key
    /// structure as a tree: a tier, then a chord, then the keys that only mean
    /// something after that chord. Sprint 44 Track K (#158, #519).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A tree, ruled 2026-09-02.</b> It was uncertain in August — Noel:
    /// <i>"maybe have a tree???? not sure"</i> — and calling it "the tree
    /// explorer" ratified it. His reason is the layer count: a flat list has no
    /// way to say that H means headphone only after V. The tree is derived by
    /// <see cref="KeyTree"/> from the inventory every time this opens, so a
    /// layer another track adds appears here without a change to this file.
    /// </para>
    /// <para>
    /// <b>Three surfaces, one job each.</b> This one WALKS. The layer list
    /// (<see cref="KeyLayerHelp"/>) lists where you are standing; the Command
    /// Finder searches everything. There is no search box here on purpose —
    /// Ctrl+slash is the search, and it sits next to this key because Noel
    /// reasoned from the one to the other.
    /// </para>
    /// <para>
    /// <b>Selecting does not run the chord.</b> #158 raised it and #519 did not
    /// rule it; executing from a help surface is a new path to every command in
    /// the application, including the ones that transmit, and that is a decision
    /// for the register, not for a track. Enter here opens or closes a branch,
    /// and on a leaf re-reads it.
    /// </para>
    /// <para>
    /// <b>Keys, because a tree in a dialog runs in focus mode.</b> Right opens a
    /// branch, Left closes it or climbs, Enter does either. A letter jumps to
    /// the next visible key that starts with it — the modifier is ignored, so N
    /// finds Shift+N inside the Shift tier. The letter is consumed here whether
    /// or not it matched, so a persistent layer live underneath cannot take it
    /// (see <see cref="KeyHelpSurfaces"/>). Escape closes. F1 opens the JJ key
    /// help page. Ctrl+F1 explains these keys.
    /// </para>
    /// </remarks>
    public partial class KeyExplorerDialog : JJFlexDialog
    {
        /// <summary>
        /// The inventory Context to land on, expanded — a layer's own
        /// Shift+slash opens the explorer at that layer. Null lands on the
        /// first tier.
        /// </summary>
        public string? StartAtContext { get; set; }

        private readonly KeyTreeNode _root;

        public KeyExplorerDialog()
        {
            InitializeComponent();
            ResizeMode = ResizeMode.CanResizeWithGrip;
            KeyHelpSurfaces.Attach(this);

            // Built in the constructor so the counted title is the one the
            // base class announces at Loaded — count first, by construction.
            _root = KeyTree.Build();
            int keys = 0, layers = 0;
            foreach (var n in KeyTree.Flatten(_root))
            {
                if (n.Kind == KeyTreeNodeKind.Chord) keys++;
                if ((n.Kind == KeyTreeNodeKind.Chord || n.Kind == KeyTreeNodeKind.LayerKey) && n.HasChildren) layers++;
            }
            Title = Radios.Lexicon.Get("leader.explorer.title", ("count", keys), ("layers", layers));

            string hint = Radios.Lexicon.Get("leader.explorer.hint");
            HintText.Text = hint;
            JJFlexHelp.SetText(Tree, hint);
            AutomationProperties.SetName(Tree, Radios.Lexicon.Get("leader.explorer.tree_name"));

            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.F1)
                {
                    HelpLauncher.ShowHelp("LeaderKey");
                    e.Handled = true;
                }
            };

            Loaded += OnLoaded;
        }

        /// <summary>Open the explorer, optionally landed on a layer.</summary>
        public static void Open(string? startAtContext = null)
        {
            var dialog = new KeyExplorerDialog { StartAtContext = startAtContext };
            dialog.ShowModalDialog();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (Tree.Items.Count > 0) return; // Loaded can fire again after a hide/show

            var rootItem = ItemFor(_root);
            rootItem.IsExpanded = true;
            foreach (var tier in _root.Children)
            {
                var tierItem = ItemFor(tier);
                tierItem.IsExpanded = true;
                foreach (var chord in tier.Children)
                    tierItem.Items.Add(Subtree(chord));
                rootItem.Items.Add(tierItem);
            }
            Tree.Items.Add(rootItem);

            var landing = LandingItem(rootItem);
            landing.IsSelected = true;
            landing.BringIntoView();
            landing.Focus();
        }

        protected override void FocusFirstControl()
        {
            // The tree, always: the base class's first-focusable walk would
            // otherwise land on the hint or the Close button depending on
            // layout, and the hint is not a place to arrive.
            if (Tree.SelectedItem is TreeViewItem selected) selected.Focus();
            else Tree.Focus();
        }

        private static TreeViewItem Subtree(KeyTreeNode node)
        {
            var item = ItemFor(node);
            item.IsExpanded = false; // layers open on demand
            foreach (var child in node.Children)
                item.Items.Add(Subtree(child));
            return item;
        }

        private static TreeViewItem ItemFor(KeyTreeNode node)
        {
            var item = new TreeViewItem { Header = node.Text, Tag = node };
            AutomationProperties.SetName(item, node.Text);
            return item;
        }

        /// <summary>
        /// Where focus lands: the requested layer's node, its path expanded;
        /// otherwise the first tier — orientation ("Plain letter, 18 keys,
        /// level 2, 1 of 5") rather than the root, whose count the title has
        /// already said.
        /// </summary>
        private TreeViewItem LandingItem(TreeViewItem rootItem)
        {
            if (!string.IsNullOrEmpty(StartAtContext) && StartAtContext != KeyTree.LeaderContext)
            {
                foreach (var item in VisibleAndHidden(rootItem))
                {
                    if (item.Tag is KeyTreeNode n && n.HasChildren && n.LayerContext == StartAtContext)
                    {
                        for (var p = item; p != null; p = p.Parent as TreeViewItem)
                            p.IsExpanded = true;
                        return item;
                    }
                }
            }
            return rootItem.Items.Count > 0 && rootItem.Items[0] is TreeViewItem first ? first : rootItem;
        }

        private static IEnumerable<TreeViewItem> VisibleAndHidden(TreeViewItem item)
        {
            yield return item;
            foreach (var child in item.Items)
            {
                if (child is TreeViewItem tvi)
                    foreach (var d in VisibleAndHidden(tvi))
                        yield return d;
            }
        }

        /// <summary>The items an operator can reach by arrowing: expanded branches only.</summary>
        private IEnumerable<TreeViewItem> VisibleItems()
        {
            foreach (var top in Tree.Items)
            {
                if (top is not TreeViewItem tvi) continue;
                foreach (var v in VisibleUnder(tvi)) yield return v;
            }
        }

        private static IEnumerable<TreeViewItem> VisibleUnder(TreeViewItem item)
        {
            yield return item;
            if (!item.IsExpanded) yield break;
            foreach (var child in item.Items)
            {
                if (child is TreeViewItem tvi)
                    foreach (var v in VisibleUnder(tvi))
                        yield return v;
            }
        }

        private void Tree_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var selected = Tree.SelectedItem as TreeViewItem;
            var raw = e.Key == Key.System ? e.SystemKey : e.Key;

            if (raw == Key.Enter && selected != null)
            {
                if (selected.Items.Count > 0)
                {
                    selected.IsExpanded = !selected.IsExpanded;
                }
                else if (selected.Tag is KeyTreeNode leaf)
                {
                    // Re-read on demand: the whole point of a surface over a
                    // recitation is that you can hear a row again.
                    Radios.ScreenReaderOutput.Speak(leaf.Text,
                        Radios.Speech.SpeechIntent.Interrupt, Radios.VerbosityLevel.Critical);
                }
                e.Handled = true;
                return;
            }

            if (Keyboard.Modifiers != ModifierKeys.None && Keyboard.Modifiers != ModifierKeys.Shift) return;
            char letter = LetterOf(raw);
            if (letter == '\0') return;

            JumpTo(letter, selected);
            e.Handled = true;
        }

        private static char LetterOf(Key key)
        {
            if (key >= Key.A && key <= Key.Z) return (char)('A' + (key - Key.A));
            if (key >= Key.D0 && key <= Key.D9) return (char)('0' + (key - Key.D0));
            if (key >= Key.NumPad0 && key <= Key.NumPad9) return (char)('0' + (key - Key.NumPad0));
            return '\0';
        }

        private void JumpTo(char letter, TreeViewItem? from)
        {
            var visible = new List<TreeViewItem>(VisibleItems());
            if (visible.Count == 0) return;

            int start = from == null ? -1 : visible.IndexOf(from);
            for (int step = 1; step <= visible.Count; step++)
            {
                var candidate = visible[(start + step) % visible.Count];
                if (candidate.Tag is KeyTreeNode node && KeyTree.JumpLetter(node) == letter)
                {
                    candidate.IsSelected = true;
                    candidate.BringIntoView();
                    candidate.Focus();
                    return;
                }
            }

            // Say so rather than sit silent: a letter that does nothing reads
            // as a broken key, and the reason is that the branch is closed.
            EarconPlayer.LeaderInvalidTone();
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("leader.explorer.no_match", ("letter", letter.ToString())),
                Radios.Speech.SpeechIntent.Interrupt, Radios.VerbosityLevel.Critical);
        }
    }
}
