using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace JJFlexWpf
{
    /// <summary>
    /// "Where are the jacks on my radio?" — per-model panel references, written for
    /// somebody standing at an unfamiliar radio with a plug in their hand.
    ///
    /// Every document is authored from FlexRadio's own hardware reference manuals and
    /// checked against the panel photographs in them, because the text alone gets
    /// positions wrong. That verification is the whole value of these pages: a wrong
    /// tactile direction sends someone feeling around the wrong end of a radio and is
    /// worse than saying nothing. So a model we have not verified returns null here,
    /// and callers hide the button rather than offering a stub.
    ///
    /// The documents live in <c>docs/help/md/</c> and are embedded from there, so the
    /// same file feeds both the CHM help build and this dialog. Editing the markdown
    /// updates both; there is no second copy to keep in sync.
    ///
    /// Deliberately not covered:
    /// - Aurora (AU-510, AU-520). Built on the 8400 and 8600, but its panel has not
    ///   been confirmed against a manual, and "based on" is not verification.
    /// </summary>
    public static class RadioPanelGuide
    {
        /// <summary>A model's panel reference: a title to show, and the markdown body.</summary>
        public sealed record PanelGuide(string Title, string Markdown);

        /// <summary>
        /// Model substring to (resource, title). Matched case-insensitively against
        /// <c>FlexBase.RadioModel</c>, which reads like "FLEX-6300" or "FLEX-8400M".
        /// Matching on the number alone deliberately folds the M variants in with
        /// their base models: an M has a front panel full of knobs, but no jacks on
        /// it, so every plug still goes to the same place. The documents say so.
        /// </summary>
        private static readonly (string Match, string Resource, string Title)[] Guides =
        {
            ("6300", "know-your-radio-6300.md", "Know Your Radio: FLEX-6300"),
            ("6400", "know-your-radio-6400-6600.md", "Know Your Radio: FLEX-6400 and FLEX-6600"),
            ("6500", "know-your-radio-6500.md", "Know Your Radio: FLEX-6500"),
            ("6600", "know-your-radio-6400-6600.md", "Know Your Radio: FLEX-6400 and FLEX-6600"),
            ("6700", "know-your-radio-6700.md", "Know Your Radio: FLEX-6700"),
            ("8400", "know-your-radio-8400-8600.md", "Know Your Radio: FLEX-8400 and FLEX-8600"),
            ("8600", "know-your-radio-8400-8600.md", "Know Your Radio: FLEX-8400 and FLEX-8600"),
        };

        private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);

        /// <summary>
        /// True when a verified panel reference exists for this model. Callers use it
        /// to decide whether to offer the button at all — an unverified radio gets no
        /// button, not a button leading to an apology.
        /// </summary>
        public static bool HasGuide(string? model) => ForModel(model) != null;

        /// <summary>
        /// The panel reference for a model, or null when we have not verified one.
        /// </summary>
        public static PanelGuide? ForModel(string? model)
        {
            if (string.IsNullOrWhiteSpace(model)) return null;

            foreach (var (match, resource, title) in Guides)
            {
                if (model.Contains(match, StringComparison.OrdinalIgnoreCase))
                {
                    var markdown = Load(resource);
                    return markdown == null ? null : new PanelGuide(title, markdown);
                }
            }

            return null;
        }

        private static string? Load(string resourceFile)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(resourceFile, out var cached)) return cached;
            }

            var name = "JJFlexWpf.Help." + resourceFile;
            try
            {
                using var stream = typeof(RadioPanelGuide).Assembly.GetManifestResourceStream(name);
                if (stream == null)
                {
                    System.Diagnostics.Trace.WriteLine($"RadioPanelGuide: missing embedded resource {name}");
                    return null;
                }

                using var reader = new StreamReader(stream);
                var text = reader.ReadToEnd();

                lock (Cache)
                {
                    Cache[resourceFile] = text;
                }
                return text;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"RadioPanelGuide: could not read {name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Show the panel reference for a model in <see cref="Dialogs.HtmlInfoDialog"/>.
        /// Does nothing when no verified document exists, so it is safe to call
        /// without checking <see cref="HasGuide"/> first.
        ///
        /// Pass <paramref name="owner"/> when opening this from a dialog the user is
        /// still answering — they are looking something up mid-decision, and should
        /// land back on that dialog when they close this one.
        /// </summary>
        public static void Show(string? model, System.Windows.Window? owner = null)
        {
            var guide = ForModel(model);
            if (guide == null) return;

            Dialogs.HtmlInfoDialog.Show(guide.Title, guide.Markdown, owner);
        }
    }
}
