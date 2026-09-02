using System;
using System.Collections.Generic;
using System.Linq;
using WinFormsKeys = System.Windows.Forms.Keys;

namespace JJFlexWpf;

/// <summary>
/// Which of the JJ key's tiers a chord sits in — decided by the modifier on
/// the key that follows Ctrl+J. Labels are neutral on purpose: #515 gives each
/// tier one MEANING (a plain letter opens a layer, Ctrl toggles, and so on),
/// but the tree describes the map as it IS, and the map is migrating. Naming a
/// tier by a meaning half its members do not yet have would be the tree
/// asserting something the operator can disprove by pressing the key.
/// </summary>
public enum KeyTier
{
    Plain,
    Shift,
    Ctrl,
    Alt,
    /// <summary>Help and the way out: the rows the inventory groups as "help".</summary>
    Help,
    Other,
}

public enum KeyTreeNodeKind
{
    Root,
    Tier,
    /// <summary>A Ctrl+J follow-on chord. Has children when it opens a layer.</summary>
    Chord,
    /// <summary>A key that only means something inside a layer.</summary>
    LayerKey,
}

/// <summary>One node of the JJ key tree — see <see cref="KeyTree"/>.</summary>
public sealed class KeyTreeNode
{
    public KeyTreeNodeKind Kind { get; init; }

    /// <summary>
    /// The key as spoken, with the part the parent already said removed:
    /// "V" rather than "Ctrl+J, V"; "H" rather than "Ctrl+J, V, H". Empty on
    /// the root and tier nodes.
    /// </summary>
    public string Key { get; init; } = "";

    /// <summary>What the tree shows and what a screen reader speaks.</summary>
    public string Text { get; init; } = "";

    /// <summary>
    /// The inventory Context this node stands for. On a chord that opens a
    /// layer it is that layer's Context, so the explorer can be opened AT a
    /// layer; on a layer key it is the key's own Context. Empty elsewhere.
    /// </summary>
    public string LayerContext { get; init; } = "";

    public KeyTier Tier { get; init; } = KeyTier.Other;

    public KeyInventory.FixedKeyEntry? Entry { get; init; }

    public List<KeyTreeNode> Children { get; } = new();

    public bool HasChildren => Children.Count > 0;
}

/// <summary>
/// The JJ key structure, DERIVED from <see cref="KeyInventory"/> every time it
/// is asked for: a tier, then a chord, then the keys that only mean something
/// after that chord. Sprint 44 Track K (#158, #519).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a tree and not a list, in Noel's reasoning: the layer count grows.</b>
/// A flat surface renders one layer's rows and has no way to say that H means
/// headphone only after V. It stops representing the structure at exactly the
/// moment the structure becomes worth learning — silently, which is this
/// project's dominant failure shape.
/// </para>
/// <para>
/// <b>Nothing here is a snapshot of today's inventory.</b> Two other tracks are
/// reshaping it as this is written — ten chords are being added and two whole
/// layers with them — so this class hardcodes no layer name, no chord, and no
/// count. It reads the table and follows two links to find a layer's keys:
/// </para>
/// <list type="bullet">
/// <item><description>The declared one: a chord's
/// <see cref="KeyInventory.FixedKeyEntry.OpensLayer"/> names the Context whose
/// rows live under it. Pan mode needs this — its rows are written "Left /
/// Right" and no prefix can link them.</description></item>
/// <item><description>The structural one: rows written with the chord in front,
/// "Ctrl+J, V, H", belong to "Ctrl+J, V" by their own prefix. Volume mode's
/// rows already do this and need nothing declared.</description></item>
/// </list>
/// <para>
/// A new layer that follows either convention appears in the explorer with no
/// change here. One that follows neither is NOT found, and the test for that
/// negative case exists so the absence is a known shape rather than a silence.
/// </para>
/// <para>
/// Text is composed through the lexicon, and every key goes through
/// <see cref="KeyInventory.FixedKeyEntry.SpokenKey"/> so a bare "?" never
/// reaches a screen reader (#303).
/// </para>
/// </remarks>
public static class KeyTree
{
    /// <summary>The inventory Context of the top-level JJ key rows.</summary>
    public const string LeaderContext = "Leader";

    private const string LeaderPrefix = "Ctrl+J, ";

    /// <summary>Layers nest; anything deeper than this is a cycle, not a design.</summary>
    private const int MaxDepth = 4;

    public static KeyTreeNode Build() => Build(KeyInventory.All());

    public static KeyTreeNode Build(IEnumerable<KeyInventory.FixedKeyEntry> inventory)
    {
        var all = inventory.ToList();
        var leader = all.Where(e => e.Context == LeaderContext).ToList();

        var tiers = new Dictionary<KeyTier, List<KeyTreeNode>>();
        foreach (var row in leader)
        {
            var node = ChordNode(row, parent: null, all, depth: 1);
            var tier = TierOf(row);
            if (!tiers.TryGetValue(tier, out var list))
                tiers[tier] = list = new List<KeyTreeNode>();
            list.Add(node);
        }

        int keyCount = leader.Count;
        int layerCount = CountLayers(tiers.Values.SelectMany(t => t));

        var root = new KeyTreeNode
        {
            Kind = KeyTreeNodeKind.Root,
            LayerContext = LeaderContext,
            Text = Radios.Lexicon.Get("leader.explorer.root", ("count", keyCount)),
        };

        foreach (var tier in TierOrder)
        {
            if (!tiers.TryGetValue(tier, out var chords) || chords.Count == 0) continue;

            var tierNode = new KeyTreeNode
            {
                Kind = KeyTreeNodeKind.Tier,
                Tier = tier,
                Text = Radios.Lexicon.Get("leader.explorer.tier",
                    ("tier", TierLabel(tier)), ("count", chords.Count)),
            };
            // A lookup surface: alphabetical by the key itself, so "what does
            // V do" is root, tier, V. The inventory's own order groups by
            // subject, which is right for a list read top to bottom and wrong
            // for a tree walked by letter.
            foreach (var chord in chords.OrderBy(c => SortCode(c.Entry!)))
                tierNode.Children.Add(chord);
            root.Children.Add(tierNode);
        }

        return root;
    }

    /// <summary>
    /// The rows that belong to one layer, in inventory order: for the top
    /// level, every JJ key row; for any other Context, the rows its opening
    /// chord links to. A Context nothing opens still answers, by Context
    /// equality, so a layer can list itself before its door is registered.
    /// </summary>
    public static IReadOnlyList<KeyInventory.FixedKeyEntry> LayerRows(string context)
        => LayerRows(context, KeyInventory.All());

    public static IReadOnlyList<KeyInventory.FixedKeyEntry> LayerRows(
        string context, IEnumerable<KeyInventory.FixedKeyEntry> inventory)
    {
        var all = inventory.ToList();
        if (context == LeaderContext)
            return all.Where(e => e.Context == LeaderContext).ToList();

        var opener = all.FirstOrDefault(e => e.OpensLayer == context);
        if (opener != null)
            return ChildrenOf(opener, all);

        return all.Where(e => e.Context == context).ToList();
    }

    /// <summary>The chord that opens a layer, or null for the top level and for a layer with no registered door.</summary>
    public static KeyInventory.FixedKeyEntry? OpenerOf(string context)
        => OpenerOf(context, KeyInventory.All());

    public static KeyInventory.FixedKeyEntry? OpenerOf(
        string context, IEnumerable<KeyInventory.FixedKeyEntry> inventory)
    {
        if (context == LeaderContext) return null;
        var all = inventory.ToList();
        var declared = all.FirstOrDefault(e => e.OpensLayer == context);
        if (declared != null) return declared;
        // The structural link, read backwards: a row of this Context whose
        // display carries a chord prefix names its own door.
        foreach (var row in all.Where(e => e.Context == context))
        {
            foreach (var candidate in all.Where(e => e.Context == LeaderContext))
            {
                if (row.KeyDisplay.StartsWith(candidate.KeyDisplay + ", ", StringComparison.Ordinal))
                    return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// The key as it should be spoken inside its parent: the parent's own
    /// chord removed from the front, then the leader prefix if it is still
    /// there. A row written as a bare key ("Left / Right") comes back as is.
    /// </summary>
    public static string KeyWithin(KeyInventory.FixedKeyEntry row, KeyInventory.FixedKeyEntry? parent)
    {
        string key = row.SpokenKey;
        if (parent != null)
        {
            string parentPrefix = parent.KeyDisplay + ", ";
            if (key.StartsWith(parentPrefix, StringComparison.Ordinal))
                return key.Substring(parentPrefix.Length);
            // The spoken form may differ from the display form ("H or Shift
            // slash" for "H or ?") — the prefix is the same in both.
            string parentSpokenPrefix = parent.SpokenKey + ", ";
            if (key.StartsWith(parentSpokenPrefix, StringComparison.Ordinal))
                return key.Substring(parentSpokenPrefix.Length);
        }
        if (key.StartsWith(LeaderPrefix, StringComparison.Ordinal))
            return key.Substring(LeaderPrefix.Length);
        return key;
    }

    public static KeyTier TierOf(KeyInventory.FixedKeyEntry row)
    {
        if (string.Equals(row.Group, "help", StringComparison.OrdinalIgnoreCase))
            return KeyTier.Help;

        var chords = Radios.LeaderChordParser.ParseDisplay(row.KeyDisplay, row.ExcludedKeys);
        if (chords.Count == 0) return KeyTier.Other;

        var first = chords[0];
        var code = first & WinFormsKeys.KeyCode;
        bool letter = code >= WinFormsKeys.A && code <= WinFormsKeys.Z;
        if (!letter) return KeyTier.Other;

        return (first & WinFormsKeys.Modifiers) switch
        {
            WinFormsKeys.None => KeyTier.Plain,
            WinFormsKeys.Shift => KeyTier.Shift,
            WinFormsKeys.Control => KeyTier.Ctrl,
            WinFormsKeys.Alt => KeyTier.Alt,
            _ => KeyTier.Other,
        };
    }

    public static string TierLabel(KeyTier tier) => tier switch
    {
        KeyTier.Plain => Radios.Lexicon.Get("leader.explorer.tier.plain"),
        KeyTier.Shift => Radios.Lexicon.Get("leader.explorer.tier.shift"),
        KeyTier.Ctrl => Radios.Lexicon.Get("leader.explorer.tier.ctrl"),
        KeyTier.Alt => Radios.Lexicon.Get("leader.explorer.tier.alt"),
        KeyTier.Help => Radios.Lexicon.Get("leader.explorer.tier.help"),
        _ => Radios.Lexicon.Get("leader.explorer.tier.other"),
    };

    /// <summary>Every node, depth first, the root included.</summary>
    public static IEnumerable<KeyTreeNode> Flatten(KeyTreeNode root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var n in Flatten(child))
                yield return n;
    }

    /// <summary>The node that opens or stands for a layer, or null.</summary>
    public static KeyTreeNode? FindLayer(KeyTreeNode root, string context)
    {
        if (string.IsNullOrEmpty(context)) return null;
        if (context == LeaderContext) return root;
        // A door can sit inside a layer as well as at the top (layers nest),
        // so the kind is not the test — having children and naming the
        // Context is.
        return Flatten(root).FirstOrDefault(n =>
            (n.Kind == KeyTreeNodeKind.Chord || n.Kind == KeyTreeNodeKind.LayerKey)
            && n.HasChildren && n.LayerContext == context);
    }

    /// <summary>
    /// The letter a type-to-jump press should match against: the chord's
    /// own key with its modifiers removed ("Shift+N" answers to N), or the
    /// first letter of a tier's label.
    /// </summary>
    public static char JumpLetter(KeyTreeNode node)
    {
        string s = node.Kind == KeyTreeNodeKind.Chord || node.Kind == KeyTreeNodeKind.LayerKey
            ? node.Key
            : node.Text;
        foreach (string mod in new[] { "Ctrl+", "Control+", "Shift+", "Alt+" })
        {
            while (s.StartsWith(mod, StringComparison.OrdinalIgnoreCase))
                s = s.Substring(mod.Length);
        }
        foreach (char c in s)
        {
            if (char.IsLetterOrDigit(c)) return char.ToUpperInvariant(c);
        }
        return '\0';
    }

    // ────────────────────────────────────────────────────────────────

    private static readonly KeyTier[] TierOrder =
    {
        KeyTier.Plain, KeyTier.Shift, KeyTier.Ctrl, KeyTier.Alt, KeyTier.Help, KeyTier.Other,
    };

    private static KeyTreeNode ChordNode(
        KeyInventory.FixedKeyEntry row, KeyInventory.FixedKeyEntry? parent,
        List<KeyInventory.FixedKeyEntry> all, int depth)
    {
        var children = depth < MaxDepth ? ChildrenOf(row, all) : new List<KeyInventory.FixedKeyEntry>();
        string key = KeyWithin(row, parent);

        var node = new KeyTreeNode
        {
            Kind = parent == null ? KeyTreeNodeKind.Chord : KeyTreeNodeKind.LayerKey,
            Key = key,
            Entry = row,
            Tier = parent == null ? TierOf(row) : KeyTier.Other,
            LayerContext = children.Count > 0
                ? (row.OpensLayer.Length > 0 ? row.OpensLayer : children[0].Context)
                : row.Context,
            Text = children.Count > 0
                ? Radios.Lexicon.Get("leader.explorer.layer",
                    ("key", key), ("description", row.Description), ("count", children.Count))
                : Radios.Lexicon.Get("leader.explorer.chord",
                    ("key", key), ("description", row.Description)),
        };

        foreach (var child in children)
            node.Children.Add(ChordNode(child, row, all, depth + 1));

        return node;
    }

    /// <summary>
    /// The rows a chord opens: the declared Context first, then any row that
    /// carries this chord as its prefix. Inventory order, each row once, the
    /// chord itself never its own child.
    /// </summary>
    private static List<KeyInventory.FixedKeyEntry> ChildrenOf(
        KeyInventory.FixedKeyEntry row, List<KeyInventory.FixedKeyEntry> all)
    {
        var picked = new HashSet<KeyInventory.FixedKeyEntry>(ReferenceEqualityComparer.Instance);
        string prefix = row.KeyDisplay + ", ";
        foreach (var e in all)
        {
            if (ReferenceEquals(e, row)) continue;
            bool declared = row.OpensLayer.Length > 0 && e.Context == row.OpensLayer;
            bool structural = e.KeyDisplay.StartsWith(prefix, StringComparison.Ordinal);
            if (declared || structural) picked.Add(e);
        }
        return all.Where(picked.Contains).ToList();
    }

    private static int CountLayers(IEnumerable<KeyTreeNode> chords)
    {
        int n = 0;
        foreach (var chord in chords)
        {
            if (!chord.HasChildren) continue;
            n++;
            n += CountLayers(chord.Children);
        }
        return n;
    }

    private static int SortCode(KeyInventory.FixedKeyEntry row)
    {
        var chords = Radios.LeaderChordParser.ParseDisplay(row.KeyDisplay, row.ExcludedKeys);
        if (chords.Count == 0) return int.MaxValue;
        return (int)(chords[0] & WinFormsKeys.KeyCode);
    }
}
