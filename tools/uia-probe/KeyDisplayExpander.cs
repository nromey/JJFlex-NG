namespace JJFlex.UiaProbe;

/// <summary>How a concrete chord was derived from a KeyDisplay string.</summary>
internal enum Derivation
{
    /// <summary>The KeyDisplay was already one exact chord.</summary>
    Exact,
    /// <summary>One of several alternatives listed for the same behaviour.</summary>
    Alternative,
    /// <summary>One member of a written range, "0-7" or "Ctrl+1 through Ctrl+7".</summary>
    Range,
    /// <summary>A double-tap, "[[" or "]]".</summary>
    DoubleTap,
    /// <summary>A REPRESENTATIVE of an open family the prose does not enumerate
    /// ("Digits", "Plus then digits"). Pressing it proves the family is wired;
    /// it does not prove every member.</summary>
    Sampled,
}

internal sealed record ExpandedChord(Chord Chord, Derivation Derivation);

internal sealed record Expansion(
    string KeyDisplay,
    IReadOnlyList<ExpandedChord> Chords,
    string? Residue);

/// <summary>
/// Turns <c>KeyInventory.FixedKeyEntry.KeyDisplay</c> into concrete pressable
/// chords.
///
/// <para><b>This class exists because KeyDisplay is prose, not data.</b> The
/// inventory writes "Space, Up, Down, or Q", "0-7 or A-H", "Ctrl+J, Shift+A
/// through Shift+H" and "Plus then digits" — all perfectly clear to an
/// operator and none of them a keystroke a machine can send. Five surfaces
/// consume that field and all five only ever display it, so nothing has ever
/// forced it to be machine-readable. A harness that presses every key has to
/// bridge that gap somewhere, and doing it here keeps the inventory in the
/// voice it was written in.</para>
///
/// <para>Whatever it cannot bridge comes back as <see cref="Expansion.Residue"/>
/// rather than being quietly dropped. An unexpandable row is a row nobody
/// tested, and the report has to say so out loud.</para>
/// </summary>
internal static class KeyDisplayExpander
{
    private const string LeaderPrefix = "Ctrl+J, ";

    public static Expansion Expand(string keyDisplay)
    {
        string text = (keyDisplay ?? "").Trim();
        if (text.Length == 0) return new Expansion(keyDisplay ?? "", Array.Empty<ExpandedChord>(), "empty");

        // ── Leader form. "Ctrl+J, V, H" is a three-keystroke SEQUENCE, so the
        //    commas here mean "then", not "or" — the opposite of what they mean
        //    in "Space, Up, or Down". Getting this backwards would test the
        //    leader layer by pressing V on its own.
        if (text.StartsWith(LeaderPrefix, StringComparison.Ordinal))
        {
            string tail = text[LeaderPrefix.Length..];
            var tokens = tail.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var perToken = new List<List<ExpandedChord>>();
            foreach (string token in tokens)
            {
                var alts = ExpandSingleStep(token);
                if (alts.Count == 0)
                    return new Expansion(keyDisplay!, Array.Empty<ExpandedChord>(),
                        $"leader tail '{token}' is not a chord this expander understands");
                perToken.Add(alts);
            }

            var results = new List<ExpandedChord>();
            foreach (var combo in CartesianProduct(perToken))
            {
                var steps = new List<Step> { LeaderStep() };
                foreach (var ec in combo) steps.AddRange(ec.Chord.Steps);
                Derivation d = combo.Count == 1 && perToken[0].Count == 1
                    ? Derivation.Exact
                    : combo.Select(c => c.Derivation).FirstOrDefault(x => x != Derivation.Exact, Derivation.Exact);
                results.Add(new ExpandedChord(
                    new Chord { Steps = steps, Display = string.Join(", ", steps.Select(s => s.Display)) }, d));
            }
            return new Expansion(keyDisplay!, results, null);
        }

        var single = ExpandSingleStep(text);
        return single.Count > 0
            ? new Expansion(keyDisplay!, single, null)
            : new Expansion(keyDisplay!, Array.Empty<ExpandedChord>(), Describe(text));
    }

    private static Step LeaderStep() =>
        Chord.TryParse("Ctrl+J", out var c, out _) ? c.Steps[0] : throw new InvalidOperationException("Ctrl+J failed to parse");

    // ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Expand one alternation group: everything a single KeyDisplay token can
    /// mean, other than a leader sequence.
    /// </summary>
    private static List<ExpandedChord> ExpandSingleStep(string text)
    {
        var results = new List<ExpandedChord>();
        var pieces = SplitAlternatives(text);
        bool multiple = pieces.Count > 1;

        foreach (string piece in pieces)
        {
            string p = piece.Trim();
            if (p.Length == 0) continue;

            // "Shift+A through Shift+H", "Ctrl+1 through Ctrl+7"
            if (TryThroughRange(p, out var through))
            {
                results.AddRange(through.Select(c => new ExpandedChord(c, Derivation.Range)));
                continue;
            }
            // "0-7", "A-H", "5-9"
            if (TryDashRange(p, out var dashed))
            {
                results.AddRange(dashed.Select(c => new ExpandedChord(c, Derivation.Range)));
                continue;
            }
            // "[[" / "]]" — press the same key twice.
            if (p.Length == 2 && p[0] == p[1] && Chord.TryParse(p[0].ToString(), out var one, out _))
            {
                var steps = new List<Step> { one.Steps[0], one.Steps[0] };
                results.Add(new ExpandedChord(
                    new Chord { Steps = steps, Display = $"{one.Display}, {one.Display}" }, Derivation.DoubleTap));
                continue;
            }
            // Open families the prose names but does not list.
            if (TryFamily(p, out var family, out Derivation famDeriv))
            {
                results.Add(new ExpandedChord(family, famDeriv));
                continue;
            }
            if (Chord.TryParse(p, out var chord, out _))
            {
                results.Add(new ExpandedChord(chord, multiple ? Derivation.Alternative : Derivation.Exact));
                continue;
            }
            return new List<ExpandedChord>();   // one unreadable piece invalidates the group
        }
        return results;
    }

    /// <summary>
    /// Break "Space, Up, Down, or Q" and "Up / Down or U / D" into pieces.
    /// Space is only treated as a separator when every space-separated piece
    /// is itself a chord, so "Page Up" survives and "[ ]" splits.
    /// </summary>
    private static List<string> SplitAlternatives(string text)
    {
        var work = new List<string> { text };
        foreach (string sep in new[] { " or ", ",", " / ", "/" })
        {
            var next = new List<string>();
            foreach (string s in work)
                next.AddRange(s.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            work = next;
        }

        var final = new List<string>();
        foreach (string s in work)
        {
            string t = s.Trim();
            if (t.Contains(' ', StringComparison.Ordinal)
                && !t.Contains(" through ", StringComparison.Ordinal))
            {
                string[] bits = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (bits.Length > 1 && bits.All(b => Chord.TryParse(b, out _, out _)))
                {
                    final.AddRange(bits);
                    continue;
                }
            }
            final.Add(t);
        }
        return final;
    }

    private static bool TryThroughRange(string text, out List<Chord> chords)
    {
        chords = new List<Chord>();
        int at = text.IndexOf(" through ", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return false;

        string lo = text[..at].Trim();
        string hi = text[(at + " through ".Length)..].Trim();
        if (!Chord.TryParse(lo, out var loChord, out _) || !Chord.TryParse(hi, out var hiChord, out _)) return false;
        if (loChord.Steps.Count != 1 || hiChord.Steps.Count != 1) return false;

        Step a = loChord.Steps[0], b = hiChord.Steps[0];
        if (a.Mods != b.Mods || a.Vk > b.Vk) return false;
        if (!IsRangeable(a.Vk) || !IsRangeable(b.Vk)) return false;

        for (ushort vk = a.Vk; vk <= b.Vk; vk++)
            chords.Add(FromVk(a.Mods, vk));
        return chords.Count > 0;
    }

    private static bool TryDashRange(string text, out List<Chord> chords)
    {
        chords = new List<Chord>();
        if (text.Length != 3 || text[1] != '-') return false;
        if (!Chord.TryParse(text[0].ToString(), out var loChord, out _)) return false;
        if (!Chord.TryParse(text[2].ToString(), out var hiChord, out _)) return false;

        Step a = loChord.Steps[0], b = hiChord.Steps[0];
        if (a.Mods != b.Mods || a.Vk > b.Vk) return false;
        if (!IsRangeable(a.Vk) || !IsRangeable(b.Vk)) return false;

        for (ushort vk = a.Vk; vk <= b.Vk; vk++)
            chords.Add(FromVk(a.Mods, vk));
        return chords.Count > 0;
    }

    private static bool IsRangeable(ushort vk) => vk is (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A);

    private static Chord FromVk(Mods mods, ushort vk)
    {
        string name = ((char)vk).ToString();
        string display = string.Join("", new[]
        {
            (mods & Mods.Ctrl) != 0 ? "Ctrl+" : "",
            (mods & Mods.Alt) != 0 ? "Alt+" : "",
            (mods & Mods.Shift) != 0 ? "Shift+" : "",
            name,
        });
        var step = new Step(mods, vk, display);
        return new Chord { Steps = new[] { step }, Display = display };
    }

    /// <summary>
    /// Families the inventory names in words. Each returns ONE representative
    /// keystroke, flagged Sampled so the report never claims more than it
    /// proved.
    /// </summary>
    private static bool TryFamily(string text, out Chord chord, out Derivation derivation)
    {
        chord = null!;
        derivation = Derivation.Sampled;
        switch (text.ToLowerInvariant())
        {
            case "digits":
                return Chord.TryParse("1", out chord, out _);
            case "plus then digits":
                if (!Chord.TryParse("Plus", out var plus, out _)) return false;
                if (!Chord.TryParse("1", out var d1, out _)) return false;
                var steps = new List<Step> { plus.Steps[0], d1.Steps[0] };
                chord = new Chord { Steps = steps, Display = "Plus, 1" };
                return true;
            default:
                return false;
        }
    }

    private static string Describe(string text) =>
        $"'{text}' is prose, not a chord — no expansion rule matched";

    private static IEnumerable<List<T>> CartesianProduct<T>(List<List<T>> lists)
    {
        IEnumerable<List<T>> seed = new[] { new List<T>() };
        foreach (var list in lists)
        {
            seed = seed.SelectMany(prefix => list.Select(item =>
            {
                var next = new List<T>(prefix) { item };
                return next;
            }));
        }
        return seed;
    }
}
