using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace Radios;

// ── Transmit noise setups (Sprint 49 Track H, item F) ──
//
// THE RULED SHAPE (design part seven, "Storage, the ruled shape"; Noel
// 2026-10-31 08:47, and the 2026-11-07 07:52 ruling on a missing setup): a
// transmit noise setup is a NAMED record held PER RADIO, keyed by the radio's
// serial, beside the per-radio config. It carries both reducer slots (on,
// engine, each engine's strength and settings) and the gate's settings. A
// microphone setup points at one by name, per radio, through
// RadioTxSetup.NoiseSetupName; several microphones may point at one setup, so
// changing it changes it for all of them.
//
// Why per radio and not inside the microphone: the engines a computer runs
// for one radio's transmit chain are chosen against that radio's processing
// (its speech processor, its filters), and a setup tuned for the 8600 is not
// the 6300's. The microphone still decides WHICH setup, so a headset and a
// desk mic on one radio keep their own.
//
// Sharing between radios is deliberate and explicit (S10, 2026-11-18 08:28):
// CopyToRadio, and nothing automatic.

/// <summary>One reducer slot of a transmit noise setup: the receive slot's stored shape, for transmit.</summary>
public sealed class TxNoiseSlotSetup
{
    public bool Enabled { get; set; }

    /// <summary>The chosen engine's catalog id; empty means none chosen (the slot's own default applies).</summary>
    public string EngineId { get; set; } = "";

    /// <summary>Every engine's stored strength and settings, as the slot exports them. Direction is Transmit.</summary>
    public List<DenoiseEngineSettings> Engines { get; set; } = new();

    public TxNoiseSlotSetup Clone() => new()
    {
        Enabled = Enabled,
        EngineId = EngineId,
        Engines = Engines.Select(CloneSettings).ToList(),
    };

    internal static DenoiseEngineSettings CloneSettings(DenoiseEngineSettings s) => new()
    {
        EngineId = s.EngineId,
        Direction = s.Direction,
        Strength = s.Strength,
        Values = s.Values.Select(v => new DenoiseNamedValue { Name = v.Name, Number = v.Number, Choice = v.Choice }).ToList(),
    };
}

/// <summary>
/// The transmit noise gate's settings, as a setup carries them. The same
/// fields and defaults <see cref="TxConditioningSettings"/> has always carried
/// for the gate, so the migration moves them across one for one.
/// </summary>
public sealed class TxNoiseGateSetup
{
    public bool Enabled { get; set; }
    public bool AutoThreshold { get; set; } = true;
    public float ThresholdMarginDb { get; set; } = 8f;
    /// <summary>Only meaningful when AutoThreshold is false.</summary>
    public float ThresholdDb { get; set; } = JJPortaudio.TxNoiseGate.DefaultThresholdDb;
    public float AttackMs { get; set; } = 3f;
    public float HoldMs { get; set; } = 150f;
    public float ReleaseMs { get; set; } = 200f;
    public float RangeDb { get; set; } = 25f;

    public TxNoiseGateSetup Clone() => (TxNoiseGateSetup)MemberwiseClone();
}

/// <summary>
/// One named transmit noise setup: both reducer slots, spectral first then
/// neural, and the gate. Held per radio by <see cref="TxNoiseSetupStore"/>.
/// </summary>
public sealed class TxNoiseSetup
{
    public const int CurrentVersion = 1;

    /// <summary>Schema version of this record, for forward migration.</summary>
    public int Version { get; set; } = CurrentVersion;

    public string Name { get; set; } = "";

    public TxNoiseSlotSetup Spectral { get; set; } = new();

    public TxNoiseSlotSetup Neural { get; set; } = new();

    public TxNoiseGateSetup Gate { get; set; } = new();

    /// <summary>A deep copy, so a caller holding a setup can never change the store's copy behind its back.</summary>
    public TxNoiseSetup Clone() => new()
    {
        Version = Version,
        Name = Name,
        Spectral = (Spectral ?? new()).Clone(),
        Neural = (Neural ?? new()).Clone(),
        Gate = (Gate ?? new()).Clone(),
    };

    /// <summary>The same setup under another name.</summary>
    public TxNoiseSetup CloneAs(string name)
    {
        var copy = Clone();
        copy.Name = name;
        return copy;
    }

    /// <summary>
    /// True when the two setups would configure the chain identically, names
    /// aside. Compared through the serialized form, so a field added later is
    /// compared without anyone remembering to add it here.
    /// </summary>
    public bool SameContentAs(TxNoiseSetup other)
    {
        if (other == null) return false;
        return Serialize(CloneAs("")) == Serialize(other.CloneAs(""));
    }

    private static readonly XmlSerializer SetupSerializer = new(typeof(TxNoiseSetup));

    private static string Serialize(TxNoiseSetup s)
    {
        using var w = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        SetupSerializer.Serialize(w, s);
        return w.ToString();
    }
}

/// <summary>The file one radio's setups live in.</summary>
[XmlRoot("TxNoiseSetups")]
public sealed class TxNoiseSetupFile
{
    public const int CurrentSchemaVersion = 1;

    [XmlAttribute("schemaVersion")]
    public int SchemaVersion { get; set; }

    /// <summary>The radio these belong to, informational: the folder is the key.</summary>
    public string RadioId { get; set; } = "";

    public List<TxNoiseSetup> Setups { get; set; } = new();
}

/// <summary>How a store operation ended.</summary>
public enum TxNoiseSetupOutcome
{
    /// <summary>It happened and is on disk.</summary>
    Done,
    /// <summary>No setup of that name on that radio.</summary>
    NotFound,
    /// <summary>The target name is already used on that radio (rename, or a copy without replace).</summary>
    NameTaken,
    /// <summary>The name is empty or only spaces.</summary>
    BadName,
    /// <summary>The radio id is not a radio serial, or there is no settings root.</summary>
    BadRadio,
    /// <summary>The radio's setup file exists but could not be read; it was moved aside and nothing was changed.</summary>
    Unreadable,
    /// <summary>The change could not be written; nothing on disk changed.</summary>
    NotSaved,
}

/// <summary>The outcome of a store operation, and the name it ended up acting on.</summary>
public sealed record TxNoiseSetupResult(TxNoiseSetupOutcome Outcome, string Name = "", string? SidelinedPath = null)
{
    public bool Succeeded => Outcome == TxNoiseSetupOutcome.Done;
}

/// <summary>
/// One radio's transmit noise setups, in
/// <c>{settings root}\radios\{serial}\txNoiseSetups.xml</c>, beside that
/// radio's <c>config.xml</c>. Every operation reads the file, changes it and
/// writes it back under one lock, so two windows cannot interleave a rename
/// and a save. Names compare without regard to case, as the microphone
/// setups' names do. Every setup handed out is a copy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolved from <see cref="RadioConfig.AppDataRoot"/></b> (CLAUDE.md,
/// JJFLEX_CONFIG_DIR): a throwaway settings tree takes this store with it.
/// </para>
/// <para>
/// <b>An unreadable file is the operator's, so it is moved aside, never
/// overwritten</b>, the contract the microphone and preset stores keep: the
/// operation that found it changes nothing and reports
/// <see cref="TxNoiseSetupOutcome.Unreadable"/> with where the file went.
/// </para>
/// </remarks>
public sealed class TxNoiseSetupStore
{
    public const string FileName = "txNoiseSetups.xml";

    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly XmlSerializer FileSerializer = new(typeof(TxNoiseSetupFile));
    private readonly string _root;

    /// <param name="root">The settings root. The app passes <see cref="RadioConfig.AppDataRoot"/>; tests pass a scratch folder.</param>
    public TxNoiseSetupStore(string root)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
    }

    /// <summary>The store under the app's settings root, or null when that root cannot be resolved.</summary>
    public static TxNoiseSetupStore? Default()
    {
        string root = RadioConfig.AppDataRoot;
        return string.IsNullOrEmpty(root) ? null : new TxNoiseSetupStore(root);
    }

    /// <summary>Where a radio's setups live, or null when the id is not a radio serial.</summary>
    public string? PathFor(string radioId) =>
        RadioConfig.IsWellFormedRadioId(radioId)
            ? Path.Combine(_root, "radios", RadioConfig.SanitizeRadioId(radioId), FileName)
            : null;

    /// <summary>Every setup saved for this radio, copies, in the order they were first saved. Empty for an unknown radio or an unreadable file (which a read leaves in place).</summary>
    public IReadOnlyList<TxNoiseSetup> List(string radioId)
    {
        var path = PathFor(radioId);
        if (path == null) return Array.Empty<TxNoiseSetup>();
        lock (LockFor(path))
        {
            var file = Read(path, sidelineUnreadable: false, out _);
            return file?.Setups.Select(s => s.Clone()).ToList() ?? (IReadOnlyList<TxNoiseSetup>)Array.Empty<TxNoiseSetup>();
        }
    }

    /// <summary>The names of this radio's setups, in saved order.</summary>
    public IReadOnlyList<string> Names(string radioId) => List(radioId).Select(s => s.Name).ToList();

    /// <summary>A copy of the named setup, or null when this radio has none of that name.</summary>
    public TxNoiseSetup? Find(string radioId, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return List(radioId).FirstOrDefault(s => NameEquals(s.Name, name));
    }

    /// <summary>
    /// Save a setup under its own <see cref="TxNoiseSetup.Name"/>: replaces one
    /// of that name (keeping its place in the list), or adds it at the end. The
    /// stored copy is taken now; later changes to <paramref name="setup"/> do
    /// not reach the store.
    /// </summary>
    public TxNoiseSetupResult Save(string radioId, TxNoiseSetup setup)
    {
        if (setup == null) throw new ArgumentNullException(nameof(setup));
        string name = (setup.Name ?? "").Trim();
        if (name.Length == 0) return new(TxNoiseSetupOutcome.BadName);
        return Change(radioId, file =>
        {
            var copy = setup.CloneAs(name);
            int at = file.Setups.FindIndex(s => NameEquals(s.Name, name));
            if (at >= 0) file.Setups[at] = copy; else file.Setups.Add(copy);
            return new(TxNoiseSetupOutcome.Done, name);
        });
    }

    /// <summary>
    /// Rename a setup on this radio. Refuses a name already used by ANOTHER
    /// setup (a change of case alone is allowed). Microphone setups that point
    /// at the old name are NOT changed here: they live in the operator's
    /// microphone store, so the caller follows a rename with
    /// <see cref="MicrophoneProfileStore.RepointNoiseSetup"/> and saves that
    /// store, or they will read the setup as missing.
    /// </summary>
    public TxNoiseSetupResult Rename(string radioId, string from, string to)
    {
        string target = (to ?? "").Trim();
        if (target.Length == 0) return new(TxNoiseSetupOutcome.BadName);
        return Change(radioId, file =>
        {
            int at = file.Setups.FindIndex(s => NameEquals(s.Name, from));
            if (at < 0) return new(TxNoiseSetupOutcome.NotFound, from ?? "");
            int clash = file.Setups.FindIndex(s => NameEquals(s.Name, target));
            if (clash >= 0 && clash != at) return new(TxNoiseSetupOutcome.NameTaken, target);
            file.Setups[at] = file.Setups[at].CloneAs(target);
            return new(TxNoiseSetupOutcome.Done, target);
        });
    }

    /// <summary>Delete a setup from this radio. Microphones pointing at it then read it as missing, which applies nothing and is said once.</summary>
    public TxNoiseSetupResult Delete(string radioId, string name) =>
        Change(radioId, file =>
        {
            int at = file.Setups.FindIndex(s => NameEquals(s.Name, name));
            if (at < 0) return new(TxNoiseSetupOutcome.NotFound, name ?? "");
            file.Setups.RemoveAt(at);
            return new(TxNoiseSetupOutcome.Done, name ?? "");
        });

    /// <summary>
    /// "Copy this setup to another radio" (S10, ruled 2026-11-18 08:28): a
    /// copy under the same name in the other radio's store. Nothing automatic
    /// happens on either radio, and no microphone is re-pointed. Refuses with
    /// <see cref="TxNoiseSetupOutcome.NameTaken"/> when the other radio already
    /// has a setup of that name, unless <paramref name="replace"/>.
    /// </summary>
    public TxNoiseSetupResult CopyToRadio(string fromRadioId, string name, string toRadioId, bool replace = false)
    {
        if (PathFor(fromRadioId) == null || PathFor(toRadioId) == null) return new(TxNoiseSetupOutcome.BadRadio);
        var source = Find(fromRadioId, name);
        if (source == null) return new(TxNoiseSetupOutcome.NotFound, name ?? "");
        return Change(toRadioId, file =>
        {
            int at = file.Setups.FindIndex(s => NameEquals(s.Name, source.Name));
            if (at >= 0 && !replace) return new(TxNoiseSetupOutcome.NameTaken, source.Name);
            if (at >= 0) file.Setups[at] = source.Clone(); else file.Setups.Add(source.Clone());
            return new(TxNoiseSetupOutcome.Done, source.Name);
        });
    }

    /// <summary>The names compare as the microphone store's do: trimmed, without regard to case.</summary>
    public static bool NameEquals(string? a, string? b) =>
        string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private TxNoiseSetupResult Change(string radioId, Func<TxNoiseSetupFile, TxNoiseSetupResult> change)
    {
        var path = PathFor(radioId);
        if (path == null) return new(TxNoiseSetupOutcome.BadRadio);
        lock (LockFor(path))
        {
            var file = Read(path, sidelineUnreadable: true, out var sidelined);
            if (file == null) return new(TxNoiseSetupOutcome.Unreadable, "", sidelined);
            var result = change(file);
            if (!result.Succeeded) return result;
            file.RadioId = radioId;
            file.SchemaVersion = TxNoiseSetupFile.CurrentSchemaVersion;
            return Write(path, file) ? result : result with { Outcome = TxNoiseSetupOutcome.NotSaved };
        }
    }

    private static object LockFor(string path) => Locks.GetOrAdd(Path.GetFullPath(path), _ => new object());

    /// <summary>
    /// The file, a fresh one when there is none, or null when it exists and
    /// cannot be read. Only a CHANGE moves an unreadable file aside (it is
    /// about to write a new one there); a read leaves it where it is, so
    /// listing can never be what disturbs the operator's file.
    /// </summary>
    private static TxNoiseSetupFile? Read(string path, bool sidelineUnreadable, out string? sidelined)
    {
        sidelined = null;
        if (!File.Exists(path)) return new TxNoiseSetupFile();
        try
        {
            using var fs = File.OpenRead(path);
            if (FileSerializer.Deserialize(fs) is TxNoiseSetupFile file)
            {
                file.Setups ??= new();
                file.Setups.RemoveAll(s => s == null);
                foreach (var s in file.Setups)
                {
                    s.Name ??= "";
                    s.Spectral ??= new();
                    s.Neural ??= new();
                    s.Gate ??= new();
                }
                return file;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"TxNoiseSetupStore: {path} could not be read: {ex.Message}");
        }
        if (sidelineUnreadable) sidelined = Sideline(path);
        return null;
    }

    private static string Sideline(string path)
    {
        string aside = path + $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
        try { File.Move(path, aside); return aside; }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"TxNoiseSetupStore: could not move {path} aside: {ex.Message}");
            return path;
        }
    }

    /// <summary>Write through a temporary file and a replace, so a failed write leaves the previous file whole.</summary>
    private static bool Write(string path, TxNoiseSetupFile file)
    {
        string temp = path + ".saving";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var fs = File.Create(temp)) FileSerializer.Serialize(fs, file);
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"TxNoiseSetupStore: {path} was not saved: {ex.Message}");
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            return false;
        }
    }
}

/// <summary>What moving a microphone's old cleanup settings into a named setup did.</summary>
public enum TxNoiseMigrationOutcome
{
    /// <summary>The microphone carries no cleanup settings; nothing to move.</summary>
    NothingToMove,
    /// <summary>The microphone already points at a setup on this radio; nothing moved again.</summary>
    AlreadyPointed,
    /// <summary>A setup was created from the microphone's settings and the microphone now points at it. Say so, once.</summary>
    Created,
    /// <summary>This radio already held a setup of that name with exactly these settings; the microphone now points at it.</summary>
    Reused,
    /// <summary>The radio id is not a serial; nothing was done.</summary>
    BadRadio,
    /// <summary>The setup could not be written (or the radio's setup file was unreadable); the microphone was left exactly as it was.</summary>
    NotSaved,
}

public sealed record TxNoiseMigrationResult(TxNoiseMigrationOutcome Outcome, string SetupName = "")
{
    /// <summary>True when the microphone profile was changed, so its store must be saved.</summary>
    public bool ProfileChanged => Outcome is TxNoiseMigrationOutcome.Created or TxNoiseMigrationOutcome.Reused;
}

/// <summary>
/// Moves a microphone setup's old per-microphone cleanup settings
/// (<see cref="MicCaptureSettings.Conditioning"/>) into a named transmit noise
/// setup for one radio (design part seven: "the app creates a setup named
/// after the microphone setup for the current radio, points at it, and says
/// so once").
/// </summary>
/// <remarks>
/// <para><b>It never loses a value.</b> Every field of
/// <see cref="TxConditioningSettings"/> lands in the setup (a test walks the
/// type by reflection, so a field added later fails it until it is carried),
/// and the microphone's <c>Conditioning</c> is LEFT IN PLACE, so an older build
/// reading the same file still finds it. The pointer, not the absence of
/// <c>Conditioning</c>, is what says a microphone has moved, so it moves once
/// per radio.</para>
/// <para><b>A name already used</b> by a different setup on that radio is not
/// overwritten: the new setup takes the first free "name 2", "name 3".</para>
/// </remarks>
public static class TxNoiseSetupMigration
{
    /// <summary>The engine the old settings always meant: transmit RNNoise was the only engine the chain ran.</summary>
    public const string LegacyEngineId = "rnnoise";

    /// <summary>The setup the old settings describe, under <paramref name="name"/>.</summary>
    public static TxNoiseSetup FromConditioning(string name, TxConditioningSettings c)
    {
        if (c == null) throw new ArgumentNullException(nameof(c));
        return new TxNoiseSetup
        {
            Name = name,
            Spectral = new TxNoiseSlotSetup(),
            Neural = new TxNoiseSlotSetup
            {
                Enabled = c.NrEnabled,
                EngineId = LegacyEngineId,
                Engines = new List<DenoiseEngineSettings>
                {
                    new() { EngineId = LegacyEngineId, Direction = DenoiseDirection.Transmit, Strength = c.NrStrength },
                },
            },
            Gate = new TxNoiseGateSetup
            {
                Enabled = c.GateEnabled,
                AutoThreshold = c.AutoThreshold,
                ThresholdMarginDb = c.ThresholdMarginDb,
                ThresholdDb = c.GateThresholdDb,
                AttackMs = c.GateAttackMs,
                HoldMs = c.GateHoldMs,
                ReleaseMs = c.GateReleaseMs,
                RangeDb = c.GateRangeDb,
            },
        };
    }

    /// <summary>
    /// The old settings shape for a setup: what the neural slot's RNNoise and
    /// the gate say. For the microphone store's dual-write and for older code
    /// that still reads <see cref="TxConditioningSettings"/>; a setup that runs
    /// any other engine reads as NR on with that engine's strength unknown, so
    /// RNNoise's stored strength is used.
    /// </summary>
    public static TxConditioningSettings ToConditioning(TxNoiseSetup s)
    {
        if (s == null) throw new ArgumentNullException(nameof(s));
        var neural = s.Neural ?? new();
        var gate = s.Gate ?? new();
        var rnn = neural.Engines.LastOrDefault(e => e.EngineId == LegacyEngineId);
        return new TxConditioningSettings
        {
            NrEnabled = neural.Enabled,
            NrStrength = rnn != null && float.IsFinite(rnn.Strength) ? rnn.Strength
                : NoiseFloor.DefaultStrength(LegacyEngineId, DenoiseDirection.Transmit),
            GateEnabled = gate.Enabled,
            AutoThreshold = gate.AutoThreshold,
            ThresholdMarginDb = gate.ThresholdMarginDb,
            GateThresholdDb = gate.ThresholdDb,
            GateAttackMs = gate.AttackMs,
            GateHoldMs = gate.HoldMs,
            GateReleaseMs = gate.ReleaseMs,
            GateRangeDb = gate.RangeDb,
        };
    }

    /// <summary>
    /// Move <paramref name="profile"/>'s cleanup settings into a setup on
    /// <paramref name="radioId"/> and point the microphone at it. Changes the
    /// profile in memory only; when <see cref="TxNoiseMigrationResult.ProfileChanged"/>
    /// the caller saves the microphone store. The setup is written first, so a
    /// failed write leaves the microphone pointing at nothing new.
    /// </summary>
    public static TxNoiseMigrationResult Migrate(MicrophoneProfile profile, string radioId, string radioModel,
        TxNoiseSetupStore store)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (store == null) throw new ArgumentNullException(nameof(store));
        if (store.PathFor(radioId) == null) return new(TxNoiseMigrationOutcome.BadRadio);
        var conditioning = profile.Capture?.Conditioning;
        if (conditioning == null) return new(TxNoiseMigrationOutcome.NothingToMove);
        var binding = profile.FindSetupFor(radioId);
        if (!string.IsNullOrWhiteSpace(binding?.NoiseSetupName))
            return new(TxNoiseMigrationOutcome.AlreadyPointed, binding!.NoiseSetupName);

        string baseName = string.IsNullOrWhiteSpace(profile.Name) ? "Microphone" : profile.Name.Trim();
        var wanted = FromConditioning(baseName, conditioning);
        var existing = store.List(radioId);
        string name = baseName;
        var outcome = TxNoiseMigrationOutcome.Created;
        for (int n = 2; ; n++)
        {
            var same = existing.FirstOrDefault(s => TxNoiseSetupStore.NameEquals(s.Name, name));
            if (same == null) break;
            if (same.SameContentAs(wanted)) { outcome = TxNoiseMigrationOutcome.Reused; name = same.Name; break; }
            name = baseName + " " + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (outcome == TxNoiseMigrationOutcome.Created)
        {
            var saved = store.Save(radioId, wanted.CloneAs(name));
            if (!saved.Succeeded) return new(TxNoiseMigrationOutcome.NotSaved);
        }

        if (binding == null)
        {
            // A microphone with no radio-side binding for this radio still
            // needs somewhere to hold the pointer. An empty reference means
            // "no radio mic profile": ApplyRadioHalf reads it as no binding
            // and leaves the radio alone, as it always did.
            binding = new RadioProfileReference { RadioId = radioId, RadioModel = radioModel ?? "" };
            profile.RadioSetups.Add(binding);
        }
        binding.NoiseSetupName = name;
        return new(outcome, name);
    }
}
