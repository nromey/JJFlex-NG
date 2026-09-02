using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace JJFlexWpf;

/// <summary>
/// The H surface: "what can I press in the layer I am standing in" — spoken
/// when the layer is short, a navigable list when it is long, and the count
/// first either way. Sprint 44 Track K (#158, #519).
/// </summary>
/// <remarks>
/// <para>
/// <b>Noel, 2026-09-02:</b> <i>"H has always spoken commands. Never really liked
/// how the current help that speaks keys has no way to read at your leisure.
/// All of that needs to be a list."</i> #158 measured what H said until now:
/// 1,576 characters, 255 words, thirty semicolon-separated items — 51 to 85
/// seconds of continuous speech. One utterance, no way back, and missing the
/// one you wanted means starting again.
/// </para>
/// <para>
/// <b>The principle that decides which surface you get: speech is for an
/// ANSWER, a navigable surface is for a SEARCH.</b> Six volume targets are one
/// sentence, and a dialog would be friction for no gain; thirty rows are not a
/// sentence. <see cref="SpokenLimit"/> is the line, in one place, and the count
/// is said first either way so the operator knows immediately which they got
/// and never waits through a recitation wondering whether it will end.
/// </para>
/// <para>
/// <b>This class owns the surface; the keys that reach it belong to the
/// dispatcher.</b> The top-level H calls <see cref="Present"/> with
/// <see cref="LeaderContext"/>; a layer's own H calls it with that layer's
/// inventory Context. Nothing here binds a key. The rows come from
/// <see cref="KeyTree.LayerRows(string)"/>, so a layer that appears in the
/// explorer appears here with the same keys and the same words.
/// </para>
/// </remarks>
public static class KeyLayerHelp
{
    public const string LeaderContext = KeyTree.LeaderContext;

    /// <summary>
    /// The most rows that are still a sentence. #158's own examples set the
    /// range — six targets spoken, thirty listed — and this sits where a
    /// spoken answer stops being one an operator can hold in their head.
    /// Adjust here and nowhere else; the bench decides, not the source.
    /// </summary>
    public const int SpokenLimit = 8;

    public static bool IsShort(int rowCount) => rowCount <= SpokenLimit;

    /// <summary>
    /// Speak the layer's keys if there are few, open the list if there are
    /// many, and say the count first either way. The caller plays whatever
    /// cue its layer uses; this only speaks or shows.
    /// </summary>
    public static void Present(string layerContext)
    {
        var rows = Rows(layerContext);
        string layer = LayerName(layerContext);

        if (rows.Count == 0)
        {
            // Never silent: a key that says nothing reads as a key that does
            // nothing. And never a window whose whole content is "nothing".
            Radios.ScreenReaderOutput.Speak(
                Radios.Lexicon.Get("leader.help.empty", ("layer", layer)),
                Radios.VerbosityLevel.Terse, true);
            return;
        }

        if (IsShort(rows.Count))
        {
            Radios.ScreenReaderOutput.Speak(SpokenList(layerContext),
                Radios.VerbosityLevel.Terse, true);
            return;
        }

        var dialog = new Dialogs.ShowHelpDialog
        {
            // The count rides in the title, which is the first thing a screen
            // reader says about a new window — count first, by construction.
            Title = ListTitle(layerContext, rows.Count),
            HelpItems = rows.ToList(),
            SecondaryActionLabel = Radios.Lexicon.Get("leader.help.explore_button"),
            SecondaryAction = () => Dialogs.KeyExplorerDialog.Open(layerContext),
        };
        dialog.ShowModalDialog();
    }

    /// <summary>
    /// The layer's rows as the list shows them: the key as spoken inside this
    /// layer, and the inventory description, in inventory order.
    /// </summary>
    public static IReadOnlyList<(string key, string description)> Rows(string layerContext)
    {
        var opener = KeyTree.OpenerOf(layerContext);
        return KeyTree.LayerRows(layerContext)
            .Select(r => (KeyTree.KeyWithin(r, opener), r.Description))
            .ToList();
    }

    /// <summary>The short form: "{count} keys in {layer}: key, what it does; ..."</summary>
    public static string SpokenList(string layerContext)
    {
        var rows = Rows(layerContext);
        var sb = new StringBuilder();
        bool first = true;
        foreach (var (key, description) in rows)
        {
            if (!first) sb.Append("; ");
            sb.Append(Radios.Lexicon.Get("leader.help.row", ("key", key), ("description", description)));
            first = false;
        }
        return Radios.Lexicon.Get("leader.help.spoken",
            ("count", rows.Count), ("layer", LayerName(layerContext)), ("rows", sb.ToString()));
    }

    public static string ListTitle(string layerContext, int count)
        => Radios.Lexicon.Get("leader.help.list_title",
            ("layer", LayerTitle(layerContext)), ("count", count));

    /// <summary>
    /// The layer as named mid-sentence: "the JJ key layer", or the inventory's
    /// own label for a sub-layer ("Volume mode").
    /// </summary>
    public static string LayerName(string layerContext)
    {
        if (layerContext == LeaderContext)
            return Radios.Lexicon.Get("leader.help.layer_name");
        return LayerTitle(layerContext);
    }

    /// <summary>The layer as a title: "JJ key layer", "Volume mode".</summary>
    public static string LayerTitle(string layerContext)
    {
        if (layerContext == LeaderContext)
            return Radios.Lexicon.Get("leader.help.layer_title");
        var row = KeyTree.LayerRows(layerContext).FirstOrDefault(r => r.ContextLabel.Length > 0);
        return row?.ContextLabel ?? layerContext;
    }
}
