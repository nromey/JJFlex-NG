#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JJTrace;

namespace Radios.Alarms
{
    /// <summary>The whole of one radio's alarm configuration, as saved.</summary>
    public sealed class AlarmDefinitionFile
    {
        /// <summary>Bumped when the shape changes. A reader that meets a newer number says so rather than guessing.</summary>
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string RadioSerial { get; set; } = "";
        public DateTime SavedUtc { get; set; }

        /// <summary>Every definition, enabled or not.</summary>
        public List<AlarmDefinition> Definitions { get; set; } = new List<AlarmDefinition>();

        /// <summary>
        /// Meters the operator wants in the capture WITHOUT an alarm on them
        /// (#566's first ruling). The recorded set is the union of these and
        /// the alarms' selectors; there is no hardcoded list anywhere.
        /// </summary>
        public List<MeterSelector> RecordOnlyMeters { get; set; } = new List<MeterSelector>();
    }

    /// <summary>What loading found: a file, no file, or a file that could not be read.</summary>
    public enum AlarmStoreState
    {
        /// <summary>No file yet. An empty, healthy set of zero alarms.</summary>
        Empty,

        Loaded,

        /// <summary>The file exists and could not be read. NOT an empty set of supposedly healthy alarms —
        /// the configuration is unavailable and the UI must say so.</summary>
        Unavailable,
    }

    public sealed record AlarmStoreLoad(AlarmStoreState State, AlarmDefinitionFile File, string Problem)
    {
        public bool IsUsable => State != AlarmStoreState.Unavailable;
    }

    /// <summary>
    /// Alarm definitions on disk: one JSON file per radio serial under the
    /// settings root, written atomically, versioned, and never guessed at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Lives at <c>radios\{serial}\alarms.json</c> beneath
    /// <see cref="RadioConfig.AppDataRoot"/> — the same per-serial directory
    /// <see cref="RadioConfig"/> uses, so a throwaway settings tree
    /// (<c>JJFLEX_CONFIG_DIR</c>) relocates this store with everything else.
    /// Never <c>Environment.GetFolderPath</c> plus "JJFlexRadio": that is the
    /// nineteen-places defect CLAUDE.md records.
    /// </para>
    /// <para>
    /// Atomic through a temp file and <c>File.Move</c> with overwrite, as
    /// <see cref="Radios.Fixer.Evidence.EvidenceFileStore{TRecord}"/> does; a
    /// crash mid-write leaves the previous file intact.
    /// </para>
    /// <para>
    /// <b>Unreadable is unavailable, not empty.</b> A corrupt file yields
    /// <see cref="AlarmStoreState.Unavailable"/> with the problem named, and
    /// the file is left in place for the operator to look at. Saving over it
    /// is a deliberate act the dialog asks about, not a side effect of
    /// loading.
    /// </para>
    /// </remarks>
    public sealed class AlarmDefinitionStore
    {
        public const string FileName = "alarms.json";

        private readonly string _root;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        /// <param name="root">The settings root. Tests pass a scratch directory.</param>
        public AlarmDefinitionStore(string root)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
        }

        /// <summary>The store under the app's settings root, or null when that root cannot be resolved.</summary>
        public static AlarmDefinitionStore? Default()
        {
            string root = RadioConfig.AppDataRoot;
            return string.IsNullOrEmpty(root) ? null : new AlarmDefinitionStore(root);
        }

        /// <summary>Where a radio's alarms live. The serial is sanitised the way a path must be.</summary>
        public string PathFor(string radioSerial) =>
            Path.Combine(_root, "radios", SafeSerial(radioSerial), FileName);

        /// <summary>Flex serials are digits and dashes; anything else is replaced so a serial can never escape the folder.</summary>
        public static string SafeSerial(string serial)
        {
            if (string.IsNullOrWhiteSpace(serial)) return "unknown-radio";
            var sb = new StringBuilder(serial.Length);
            foreach (char c in serial)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        public AlarmStoreLoad Load(string radioSerial)
        {
            string path = PathFor(radioSerial);
            if (!File.Exists(path))
                return new AlarmStoreLoad(AlarmStoreState.Empty,
                    new AlarmDefinitionFile { RadioSerial = radioSerial }, "");

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                AlarmDefinitionFile? file = JsonSerializer.Deserialize<AlarmDefinitionFile>(json, JsonOptions);
                if (file == null)
                    return Unavailable(radioSerial, path, "the file is empty");
                if (file.SchemaVersion > AlarmDefinitionFile.CurrentSchemaVersion)
                    return Unavailable(radioSerial, path,
                        "written by a newer version (schema " + file.SchemaVersion + ", this build reads "
                        + AlarmDefinitionFile.CurrentSchemaVersion + ")");
                file.Definitions ??= new List<AlarmDefinition>();
                file.RecordOnlyMeters ??= new List<MeterSelector>();
                file.RadioSerial = radioSerial;   // the directory is authoritative
                return new AlarmStoreLoad(AlarmStoreState.Loaded, file, "");
            }
            catch (JsonException ex)
            {
                return Unavailable(radioSerial, path, "not valid JSON: " + ex.Message);
            }
            catch (IOException ex)
            {
                return Unavailable(radioSerial, path, "could not be read: " + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Unavailable(radioSerial, path, "could not be read: " + ex.Message);
            }
        }

        private static AlarmStoreLoad Unavailable(string serial, string path, string problem)
        {
            Tracing.TraceLine("AlarmDefinitionStore: " + path + " is unavailable — " + problem, TraceLevel.Error);
            return new AlarmStoreLoad(AlarmStoreState.Unavailable,
                new AlarmDefinitionFile { RadioSerial = serial }, problem);
        }

        /// <summary>Write the file atomically. False, traced, on failure; the previous file survives.</summary>
        public bool Save(AlarmDefinitionFile file)
        {
            if (file == null) return false;
            string path = PathFor(file.RadioSerial);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                file.SchemaVersion = AlarmDefinitionFile.CurrentSchemaVersion;
                file.SavedUtc = DateTime.UtcNow;
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonOptions), Encoding.UTF8);
                File.Move(temp, path, overwrite: true);
                Tracing.TraceLine("AlarmDefinitionStore: saved " + file.Definitions.Count + " definition(s) and "
                    + file.RecordOnlyMeters.Count + " record-only meter(s) for " + file.RadioSerial, TraceLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("AlarmDefinitionStore: could not save " + path + " — " + ex.Message, TraceLevel.Error);
                return false;
            }
        }
    }
}
