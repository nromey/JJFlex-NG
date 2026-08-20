using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using JJTrace;

namespace Radios.ChainChecks
{
    /// <summary>
    /// Finds the rules. One built into the app, one the operator or a future
    /// update can drop beside their settings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The built-in copy ships as an embedded resource, so it can never go
    /// missing from an install and never needs an installer entry. An override
    /// file under the app's settings folder replaces it ENTIRELY rather than
    /// merging: merging two rulesets would mean an operator's edit could be
    /// silently overruled by a built-in rule they cannot see, and a diagnostic
    /// nobody can predict is not a diagnostic.
    /// </para>
    /// <para>
    /// That override path is also the delivery route for rules pushed out after
    /// release. A fault a tester finds next month becomes a check by shipping
    /// text into that folder — no build, no installer, no version bump.
    /// </para>
    /// <para>
    /// Loading never throws. A ruleset that could not be read comes back empty
    /// with its <see cref="DiagnosticRuleSet.Problems"/> explaining why, and the
    /// report then says it could run no checks — which is the honest outcome and
    /// the one the three-state rule demands.
    /// </para>
    /// </remarks>
    public static class RuleSetLoader
    {
        /// <summary>The transmit chain ruleset's file name, in both places.</summary>
        public const string TxChainFileName = "tx-chain-rules.txt";

        /// <summary>The embedded copy's resource name.</summary>
        private const string TxChainResource = "Radios.ChainChecks.tx-chain-rules.txt";

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, DiagnosticRuleSet> Cache =
            new Dictionary<string, DiagnosticRuleSet>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The transmit chain ruleset. Cached after the first read — the rules
        /// do not change while the app runs, and re-parsing on every check would
        /// make a button press do file work.
        /// </summary>
        public static DiagnosticRuleSet TxChain()
        {
            return Load(TxChainFileName, TxChainResource);
        }

        /// <summary>
        /// Forget the cached rulesets, so the next check re-reads them. For the
        /// operator who has just edited an override file, and for tests.
        /// </summary>
        public static void Forget()
        {
            lock (CacheLock) Cache.Clear();
        }

        /// <summary>
        /// Where an override file for this ruleset would go, whether or not one
        /// is there. Worth showing an operator who wants to write one.
        /// </summary>
        public static string OverridePath(string fileName)
        {
            string dir = RadioConfig.ResolvedBaseDirectory;
            return string.IsNullOrEmpty(dir) ? "" : Path.Combine(dir, fileName);
        }

        private static DiagnosticRuleSet Load(string fileName, string resourceName)
        {
            lock (CacheLock)
            {
                if (Cache.TryGetValue(fileName, out DiagnosticRuleSet cached)) return cached;
            }

            DiagnosticRuleSet set = ReadOverride(fileName) ?? ReadEmbedded(resourceName)
                ?? Missing(fileName, resourceName);

            lock (CacheLock) Cache[fileName] = set;

            if (set.Problems.Count != 0)
            {
                Tracing.TraceLine("RuleSetLoader: " + set.Describe(), TraceLevel.Warning);
                foreach (string p in set.Problems)
                    Tracing.TraceLine("RuleSetLoader: " + p, TraceLevel.Warning);
            }
            else
            {
                Tracing.TraceLine("RuleSetLoader: " + set.Describe(), TraceLevel.Info);
            }

            return set;
        }

        private static DiagnosticRuleSet ReadOverride(string fileName)
        {
            try
            {
                string path = OverridePath(fileName);
                if (path.Length == 0 || !File.Exists(path)) return null;
                string text = File.ReadAllText(path);
                return DiagnosticRuleSet.Parse(text, "your own rule file at " + path);
            }
            catch (Exception ex)
            {
                // An unreadable override must NOT silently fall through to the
                // built-in rules: the operator would then be running checks
                // they did not write and cannot see. Say so instead.
                var set = new DiagnosticRuleSet { Origin = "your own rule file" };
                set.Problems.Add("Your own rule file could not be read: " + ex.Message
                    + ". Move it out of the way to go back to the built-in rules.");
                return set;
            }
        }

        private static DiagnosticRuleSet ReadEmbedded(string resourceName)
        {
            try
            {
                Assembly asm = typeof(RuleSetLoader).Assembly;
                using Stream stream = asm.GetManifestResourceStream(resourceName);
                if (stream == null) return null;
                using var reader = new StreamReader(stream);
                return DiagnosticRuleSet.Parse(reader.ReadToEnd(), "this build");
            }
            catch (Exception ex)
            {
                var set = new DiagnosticRuleSet { Origin = "this build" };
                set.Problems.Add("The built-in rules could not be read: " + ex.Message);
                return set;
            }
        }

        private static DiagnosticRuleSet Missing(string fileName, string resourceName)
        {
            var set = new DiagnosticRuleSet { Origin = "nowhere" };
            set.Problems.Add("No rules were found. The built-in copy (" + resourceName
                + ") is missing from this build, and there is no file at "
                + (OverridePath(fileName) is { Length: > 0 } p ? p : "the settings folder") + ".");
            return set;
        }
    }
}
