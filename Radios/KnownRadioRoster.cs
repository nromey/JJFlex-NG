using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using JJTrace;
using Radios.DiscoveryChain;

namespace Radios
{
    /// <summary>
    /// One radio this installation has seen before, assembled for display.
    /// Presence is NOT part of this record — whether the radio is reachable
    /// right now is discovery's business, and the selector overlays it.
    /// </summary>
    public sealed class KnownRadioEntry
    {
        public string Serial { get; set; } = "";
        public string Nickname { get; set; } = "";

        /// <summary>The operator's chosen name for this radio — a choice,
        /// never overwritten by sightings. Empty when no choice was made;
        /// display falls through to <see cref="Nickname"/>.</summary>
        public string UserNickname { get; set; } = "";

        /// <summary>The name to show: the choice when made, otherwise the
        /// observation.</summary>
        public string DisplayName =>
            !string.IsNullOrWhiteSpace(UserNickname) ? UserNickname : Nickname;

        public string Model { get; set; } = "";
        public bool IsFavorite { get; set; }

        /// <summary>The operator's ordered connection-path chain for this
        /// radio. Empty means no preference recorded — derive from live
        /// availability, local first.</summary>
        public List<ConnectPathKind> PathChain { get; set; } = new();

        /// <summary>UTC of the last sighting, or <see cref="DateTime.MinValue"/>
        /// when this install has a profile for the radio but never recorded a
        /// sighting (profiles written before the roster shipped).</summary>
        public DateTime LastSeenUtc { get; set; }

        /// <summary>True when the last sighting came over SmartLink.</summary>
        public bool LastSeenRemote { get; set; }

        /// <summary>SmartLink account that last listed this radio, empty for a
        /// LAN-only sighting.</summary>
        public string LastSeenViaAccount { get; set; } = "";

        /// <summary>The operator's chosen account for this radio — sticky,
        /// never auto-overwritten by a sighting. Empty means automatic.</summary>
        public string PreferredAccount { get; set; } = "";

        /// <summary>Which account reaches this radio: the choice if made,
        /// otherwise the observation. Callers fall through to the
        /// preferred-account-for-new-connections when this is empty too.</summary>
        public string ResolvedAccount =>
            !string.IsNullOrWhiteSpace(PreferredAccount) ? PreferredAccount : LastSeenViaAccount;

        /// <summary>True when this radio appears in the cached radio list for
        /// the account the selector is currently working with. Such a row is a
        /// FAST PAINT — it is honest about what the account last returned and
        /// must never be connected to without a live fetch in flight.</summary>
        public bool InAccountCache { get; set; }

        /// <summary>When the cached account list this row came from was
        /// fetched. <see cref="DateTime.MinValue"/> when the row did not come
        /// from an account cache.</summary>
        public DateTime AccountListFetchedUtc { get; set; }

        /// <summary>
        /// The operator took this radio off the list but kept its settings
        /// (task #98). Carried through the merge rather than filtered at the
        /// source so that a radio which turns out to be listed by an account
        /// RIGHT NOW comes back with its chosen name, its favourite flag and
        /// its path chain intact — dropping it early would have brought it
        /// back anonymous, which reads as the settings having been destroyed
        /// by the safe scope. <see cref="Load"/> filters at the end.
        /// </summary>
        public bool HiddenFromList { get; set; }
    }

    /// <summary>
    /// The known-radios roster: every radio this install has ever connected to
    /// or listed, regardless of whether anything can see it right now.
    ///
    /// <para>Two sources, merged by serial. The serial-keyed per-radio store
    /// (<see cref="RadioConfig"/>, <c>radios\{serial}\config.xml</c>) is
    /// authoritative for the display metadata a user owns — favorite flag,
    /// last-seen stamp, which account saw it. <c>radioConnectionCacheV1.xml</c>
    /// fills in model and per-account list membership, and covers radios that
    /// were connected before the roster fields existed.</para>
    ///
    /// <para>Everything here is local-only and read on demand; nothing is cached
    /// in memory across selector sessions, because the selector is the only
    /// consumer and it opens rarely. Every method swallows IO failure and
    /// traces — a corrupt profile must never keep the picker from opening.</para>
    /// </summary>
    public static class KnownRadioRoster
    {
        /// <summary>
        /// Directory holding <c>radioConnectionCacheV1.xml</c>. Assigned at
        /// startup next to <see cref="RadioConfig.BaseDirectory"/>; when unset
        /// it falls back to the same derivation FlexBase uses for
        /// <c>OpenParms.ConfigDirectory</c> so the roster still finds the cache
        /// on an install where the explicit assignment has not run yet.
        /// </summary>
        public static string? CacheDirectory { get; set; }

        private static string ResolveCacheDirectory()
        {
            if (!string.IsNullOrEmpty(CacheDirectory)) return CacheDirectory!;
            var baseDir = RadioConfig.BaseDirectory;
            return string.IsNullOrEmpty(baseDir) ? "" : Path.Combine(baseDir, "Radios");
        }

        private static RadioConnectionCache? OpenCache()
        {
            var dir = ResolveCacheDirectory();
            if (string.IsNullOrEmpty(dir)) return null;
            try
            {
                return new RadioConnectionCache(dir);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"KnownRadioRoster.OpenCache: {ex.GetType().Name} {ex.Message}",
                    System.Diagnostics.TraceLevel.Warning);
                return null;
            }
        }

        /// <summary>
        /// The full roster, newest sighting first within each group. Pass the
        /// SmartLink account the selector is currently working with (may be
        /// empty) so rows that account last listed are marked
        /// <see cref="KnownRadioEntry.InAccountCache"/>.
        /// </summary>
        public static List<KnownRadioEntry> Load(string accountEmail = "")
        {
            var byserial = new Dictionary<string, KnownRadioEntry>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var cfg in RadioConfig.LoadAllKnown())
                {
                    if (string.IsNullOrWhiteSpace(cfg.RadioId)) continue;
                    byserial[cfg.RadioId] = new KnownRadioEntry
                    {
                        Serial = cfg.RadioId,
                        Nickname = cfg.Nickname ?? "",
                        UserNickname = cfg.UserNickname ?? "",
                        Model = cfg.Model ?? "",
                        IsFavorite = cfg.IsFavorite,
                        PathChain = cfg.PathChain ?? new List<ConnectPathKind>(),
                        LastSeenUtc = cfg.LastSeenUtc,
                        LastSeenRemote = cfg.LastSeenRemote,
                        LastSeenViaAccount = cfg.LastSeenViaAccount ?? "",
                        PreferredAccount = cfg.PreferredAccount ?? "",
                        HiddenFromList = cfg.HiddenFromList,
                    };
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"KnownRadioRoster.Load: profile enumeration failed: {ex.Message}",
                    System.Diagnostics.TraceLevel.Warning);
            }

            var cache = OpenCache();
            if (cache != null)
            {
                try
                {
                    foreach (var e in cache.GetAllEntries())
                    {
                        if (string.IsNullOrWhiteSpace(e.Serial)) continue;
                        if (!byserial.TryGetValue(e.Serial, out var entry))
                        {
                            entry = new KnownRadioEntry { Serial = e.Serial };
                            byserial[e.Serial] = entry;
                        }
                        if (string.IsNullOrWhiteSpace(entry.Nickname)) entry.Nickname = e.Nickname ?? "";
                        if (string.IsNullOrWhiteSpace(entry.Model)) entry.Model = e.Model ?? "";
                        // The cache stamps only connects; the profile stamps every
                        // sighting. Whichever is newer is the truthful answer.
                        var cacheSeen = e.WanLastSeenUtc > e.LanLastSeenUtc ? e.WanLastSeenUtc : e.LanLastSeenUtc;
                        if (cacheSeen > entry.LastSeenUtc)
                        {
                            entry.LastSeenUtc = cacheSeen;
                            entry.LastSeenRemote = e.WanLastSeenUtc >= e.LanLastSeenUtc && e.WanLastSeenUtc != default;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"KnownRadioRoster.Load: cache merge failed: {ex.Message}",
                        System.Diagnostics.TraceLevel.Warning);
                }

                // Attribution comes from EVERY cached account list, not only
                // the account the selector is working with. A radio that only
                // some OTHER account can list is exactly the row the operator
                // needs told about, and skipping the other lists left it
                // anonymous (the 2026-08-09 inverted-label bug). InAccountCache
                // stays scoped to the matching account — it means "this account
                // can see it now" and drives live/offline logic downstream.
                try
                {
                    // Ascending fetch order so the LATEST list to mention a
                    // radio wins attribution when several do (a club rig on
                    // two accounts). The per-radio profile's value, written at
                    // sighting time, outranks the cache either way.
                    var cacheAttribution = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var acct in cache.GetAllAccountRadioLists()
                                 .Where(a => a?.Radios != null && !string.IsNullOrWhiteSpace(a.AccountEmail))
                                 .OrderBy(a => a.FetchedUtc))
                    {
                        bool isCurrent = !string.IsNullOrWhiteSpace(accountEmail)
                            && string.Equals(acct.AccountEmail, accountEmail, StringComparison.OrdinalIgnoreCase);
                        foreach (var r in acct.Radios)
                        {
                            if (string.IsNullOrWhiteSpace(r.Serial)) continue;
                            if (!byserial.TryGetValue(r.Serial, out var entry))
                            {
                                entry = new KnownRadioEntry { Serial = r.Serial };
                                byserial[r.Serial] = entry;
                            }
                            if (string.IsNullOrWhiteSpace(entry.Nickname)) entry.Nickname = r.Nickname ?? "";
                            if (string.IsNullOrWhiteSpace(entry.Model)) entry.Model = r.Model ?? "";
                            if (isCurrent)
                            {
                                entry.InAccountCache = true;
                                entry.AccountListFetchedUtc = acct.FetchedUtc;
                            }
                            // An account list that names this radio outranks a
                            // hide. Removal cleared it from every cached list,
                            // so its presence here means a pass since then put
                            // it back — the account really does list it, and
                            // hiding a radio the operator can reach is the one
                            // outcome removal must never produce (task #98).
                            entry.HiddenFromList = false;
                            cacheAttribution[r.Serial] = acct.AccountEmail;
                            // Appearing in an account's fetched list IS a remote
                            // sighting, whichever account fetched it.
                            if (acct.FetchedUtc > entry.LastSeenUtc)
                            {
                                entry.LastSeenUtc = acct.FetchedUtc;
                                entry.LastSeenRemote = true;
                            }
                        }
                    }

                    foreach (var kv in cacheAttribution)
                    {
                        if (byserial.TryGetValue(kv.Key, out var entry)
                            && string.IsNullOrWhiteSpace(entry.LastSeenViaAccount))
                        {
                            entry.LastSeenViaAccount = kv.Value;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Tracing.TraceLine($"KnownRadioRoster.Load: account list merge failed: {ex.Message}",
                        System.Diagnostics.TraceLevel.Warning);
                }
            }

            return byserial.Values
                .Where(e => !e.HiddenFromList)
                .OrderByDescending(e => e.IsFavorite)
                .ThenByDescending(e => e.LastSeenUtc)
                .ToList();
        }

        /// <summary>
        /// Take a radio off the roster (task #98). Two scopes, and the
        /// difference between them is the whole point of the confirmation the
        /// caller must have shown first.
        ///
        /// <para><paramref name="deleteSettings"/> FALSE — the safe scope. The
        /// per-radio profile survives untouched; the radio is simply flagged
        /// off the list, and every cached record of it is dropped so the row
        /// does not repaint from the source nobody thought to look at. A live
        /// sighting, or an account list that names it, brings it straight back
        /// WITH its settings. For an online radio this is therefore close to a
        /// no-op, deliberately; where it earns its keep is the junk entry that
        /// will never answer again.</para>
        ///
        /// <para><paramref name="deleteSettings"/> TRUE — the destructive
        /// scope. The whole <c>radios\{serial}\</c> directory goes: the profile
        /// AND the connection history ring. The radio can come back; the
        /// configuration cannot, and the caller must have named what is lost
        /// before getting here. Deleting the DIRECTORY rather than the row is
        /// what makes it stick — leave the directory and every setting
        /// resurrects the moment the radio is re-discovered.</para>
        ///
        /// <para>Returns false when any part of the removal was refused, and a
        /// false must NOT be reported as success: the row would be back at the
        /// next launch.</para>
        /// </summary>
        public static bool Remove(string serial, bool deleteSettings)
        {
            if (string.IsNullOrWhiteSpace(serial)) return false;

            bool ok = true;

            // The cache first, under both scopes. The roster merges profile and
            // cache by serial, so a removal that touches only one of them is a
            // removal the operator watches undo itself.
            try
            {
                var cache = OpenCache();
                if (cache != null && !cache.Forget(serial)) ok = false;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"KnownRadioRoster.Remove({serial}): cache: {ex.Message}",
                    System.Diagnostics.TraceLevel.Warning);
                ok = false;
            }

            try
            {
                var baseDir = RadioConfig.ResolvedBaseDirectory;
                var dir = string.IsNullOrEmpty(baseDir)
                    ? null
                    : Path.Combine(baseDir!, "radios", RadioConfig.SanitizeRadioId(serial));

                if (deleteSettings)
                {
                    if (dir != null && Directory.Exists(dir))
                    {
                        Directory.Delete(dir, recursive: true);
                        Tracing.TraceLine(
                            $"KnownRadioRoster.Remove({serial}): profile directory deleted",
                            System.Diagnostics.TraceLevel.Info);
                    }
                }
                else if (dir != null && Directory.Exists(dir))
                {
                    var cfg = RadioConfig.LoadForRadio(serial);
                    cfg.HiddenFromList = true;
                    if (!cfg.SaveForRadio(serial)) ok = false;
                }
                // No directory and not deleting: there were no settings to
                // keep, so clearing the cache above was the entire removal.
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"KnownRadioRoster.Remove({serial}): profile: {ex.Message}",
                    System.Diagnostics.TraceLevel.Warning);
                ok = false;
            }

            return ok;
        }

        /// <summary>
        /// Record that a radio was seen right now. Called once per radio per
        /// selector session — a LAN radio re-announces about once a second and
        /// writing config.xml at that rate would be filesystem abuse for a
        /// timestamp nobody reads until the next launch.
        /// </summary>
        public static void RecordSighting(string serial, string nickname, string model,
            bool isRemote, string accountEmail)
        {
            if (string.IsNullOrWhiteSpace(serial)) return;
            try
            {
                var cfg = RadioConfig.LoadForRadio(serial);
                if (!string.IsNullOrWhiteSpace(nickname)) cfg.Nickname = nickname;
                if (!string.IsNullOrWhiteSpace(model)) cfg.Model = model;
                // A radio that answers is real, so a sighting outranks a hide
                // (task #98). Keeping a reachable radio off the list would lock
                // the operator out of their own rig with no explanation
                // anywhere, and the removal UI is careful never to promise an
                // online radio will stay gone.
                cfg.HiddenFromList = false;
                cfg.LastSeenUtc = DateTime.UtcNow;
                cfg.LastSeenRemote = isRemote;
                // A LAN sighting says nothing about which account can see the
                // radio remotely, so it must not erase an answer we already have.
                if (isRemote && !string.IsNullOrWhiteSpace(accountEmail))
                    cfg.LastSeenViaAccount = accountEmail;
                cfg.SaveForRadio(serial);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"KnownRadioRoster.RecordSighting({serial}): {ex.Message}",
                    System.Diagnostics.TraceLevel.Warning);
            }
        }

        /// <summary>Read the favorite flag for one radio.</summary>
        public static bool IsFavorite(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return false;
            try { return RadioConfig.LoadForRadio(serial).IsFavorite; }
            catch { return false; }
        }

        /// <summary>
        /// Set (or clear) the favorite flag. Returns true when the value was
        /// persisted — a false return means the caller must NOT announce
        /// success, because the store declined (unset base directory, read-only
        /// profile) and the next launch would silently disagree.
        /// </summary>
        public static bool SetFavorite(string serial, bool favorite)
        {
            if (string.IsNullOrWhiteSpace(serial)) return false;
            try
            {
                var cfg = RadioConfig.LoadForRadio(serial);
                cfg.IsFavorite = favorite;
                return cfg.SaveForRadio(serial);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"KnownRadioRoster.SetFavorite({serial}): {ex.Message}",
                    System.Diagnostics.TraceLevel.Warning);
                return false;
            }
        }

        /// <summary>
        /// Set (or clear, with an empty string) the operator's preferred
        /// account for one radio. Returns true when the value was persisted —
        /// a false return means the caller must NOT announce success, because
        /// the store declined and the next launch would silently disagree.
        /// The ONLY writers are the deliberate-action surfaces (row context
        /// menu, account manager associations view); sightings write
        /// LastSeenViaAccount and must never touch this field.
        /// </summary>
        public static bool SetPreferredAccount(string serial, string accountEmail)
        {
            if (string.IsNullOrWhiteSpace(serial)) return false;
            try
            {
                var cfg = RadioConfig.LoadForRadio(serial);
                cfg.PreferredAccount = accountEmail ?? "";
                return cfg.SaveForRadio(serial);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"KnownRadioRoster.SetPreferredAccount({serial}): {ex.Message}",
                    System.Diagnostics.TraceLevel.Warning);
                return false;
            }
        }

        /// <summary>
        /// Set (or clear, with an empty/null list) the operator's ordered
        /// connection-path chain for one radio. Returns true when the value
        /// was persisted — a false return means the caller must NOT announce
        /// success, because the store declined and the next launch would
        /// silently disagree. Like <see cref="SetPreferredAccount"/>, the
        /// only writers are deliberate-action surfaces (the selector's path
        /// control and context menu); nothing automatic may touch this.
        /// </summary>
        public static bool SetPathChain(string serial, List<ConnectPathKind> chain)
        {
            if (string.IsNullOrWhiteSpace(serial)) return false;
            try
            {
                var cfg = RadioConfig.LoadForRadio(serial);
                cfg.PathChain = chain ?? new List<ConnectPathKind>();
                return cfg.SaveForRadio(serial);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine($"KnownRadioRoster.SetPathChain({serial}): {ex.Message}",
                    System.Diagnostics.TraceLevel.Warning);
                return false;
            }
        }

        /// <summary>
        /// "3 days ago", "just now", "last seen unknown" — spoken age, never a
        /// bare timestamp. Screen-reader users get no benefit from parsing an
        /// ISO date out of a list row.
        /// </summary>
        public static string DescribeAge(DateTime utc)
        {
            if (utc == default || utc == DateTime.MinValue) return "last seen unknown";
            var age = DateTime.UtcNow - utc;
            if (age < TimeSpan.Zero) return "last seen just now";
            if (age.TotalMinutes < 2) return "last seen just now";
            if (age.TotalMinutes < 60) return $"last seen {(int)age.TotalMinutes} minutes ago";
            if (age.TotalHours < 24)
            {
                int h = (int)age.TotalHours;
                return $"last seen {h} hour{(h == 1 ? "" : "s")} ago";
            }
            int d = (int)age.TotalDays;
            if (d < 30) return $"last seen {d} day{(d == 1 ? "" : "s")} ago";
            int m = d / 30;
            return $"last seen {m} month{(m == 1 ? "" : "s")} ago";
        }
    }
}
