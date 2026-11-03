using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Radios.Fixer;

namespace Radios.Listenback;

/// <summary>
/// A test: a series of takes an operator can come back to (Unified takes,
/// design section 6; Noel's rulings Q7, D3 and D5). Stored as
/// <c>.tests/&lt;id&gt;/test.json</c> under the takes root.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two identities, on purpose</b> (Noel, 2026-11-02 17:31: "The hex ID would
/// stay, it's the other id that could be changed"). <see cref="Id"/> is a
/// hidden GUID that never changes and that every take points to.
/// <see cref="Name"/> is what the operator reads and says: at first the
/// Fixer's phone-readable ID (<see cref="FixerRunId"/>, no second alphabet),
/// and after a rename whatever the operator typed. Every name the test has
/// had stays in <see cref="Aliases"/>, so "W6R-23T" still finds the test
/// after it became "Heil test".
/// </para>
/// <para>
/// <b>Take numbers are never reused.</b> <see cref="NextTakeNumber"/> only
/// rises; a failed or deleted take keeps its reservation as a tombstone.
/// </para>
/// </remarks>
public sealed class TakeTest
{
    public const string FormatName = "jjflex-take-test-1";
    public const string PolicyAsk = "ask";
    public const string PolicyAddToThisTest = "add-to-this-test";

    public string Format { get; set; } = FormatName;
    public string Id { get; set; } = "";
    public string ReadableId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public int NextTakeNumber { get; set; } = 1;
    public List<TakeReservation> Reservations { get; set; } = new();
    /// <summary>
    /// <see cref="PolicyAsk"/>, or <see cref="PolicyAddToThisTest"/> once the
    /// operator answered "add to this one": the question is then never asked
    /// again for this test, including after it is resumed (Q7).
    /// </summary>
    public string ChangePolicy { get; set; } = PolicyAsk;
}

/// <summary>One take number held by a test, whatever became of the take.</summary>
public sealed class TakeReservation
{
    public const string Reserved = "reserved";
    public const string Made = "made";
    public const string Failed = "failed";
    public const string Deleted = "deleted";

    public string Folder { get; set; } = "";
    public int Number { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string State { get; set; } = Reserved;
}

/// <summary>
/// A take's own sidecar, <c>take.meta.json</c>: the test it belongs to and
/// its number there, and the operator's note. Written beside take.txt, never
/// into it, because take.txt ends at <c>closed</c>.
/// </summary>
public sealed class TakeMetadata
{
    public const string FileName = "take.meta.json";
    public const string FormatName = "jjflex-take-metadata-1";

    public string Format { get; set; } = FormatName;
    public string TestId { get; set; } = "";
    public int TakeNumber { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>What the next take is about to be made with, for the "start a new test?" question.</summary>
/// <param name="Serial">The radio's serial.</param>
/// <param name="MicProfile">The radio's mic profile.</param>
/// <param name="MicInput">The radio's mic input (a stand-in for the microphone until microphones are named, D5).</param>
/// <param name="PcMicrophoneDevice">The computer's microphone device (the same stand-in).</param>
public sealed record TakeAdmissionFacts(string Serial, string MicProfile, string MicInput, string PcMicrophoneDevice);

/// <summary>The answer to "may the next take join the active test?".</summary>
/// <param name="Ask">True when the operator must be asked "start a new test or add to this one" first.</param>
/// <param name="Test">The test the take joins when <see cref="Ask"/> is false; null means a new test is made at reservation.</param>
/// <param name="Changed">What changed since the test's last take, in <see cref="TakeSetup.Registered"/>'s keys plus <c>serial</c>.</param>
public sealed record TakeAdmission(bool Ask, TakeTest Test, IReadOnlyList<string> Changed);

public static partial class TakeSession
{
    // ── The store: tests and sidecars under the takes root ──

    private static readonly object StoreGate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string TestsDirectory(string root) => Path.Combine(root, TakeCatalog.TestsFolder);
    private static string TestPath(string root, string id) => Path.Combine(TestsDirectory(root), id, "test.json");

    /// <summary>
    /// Hold the store for one transaction: one lock in this process, and an
    /// exclusive lock file for any other instance of the app sharing the root.
    /// </summary>
    private static T Transaction<T>(string root, Func<T> body)
    {
        lock (StoreGate)
        {
            Directory.CreateDirectory(TestsDirectory(root));
            string lockPath = Path.Combine(TestsDirectory(root), ".lock");
            FileStream? held = null;
            for (int attempt = 0; held == null; attempt++)
            {
                try { held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (attempt < 100) { Thread.Sleep(20); }
            }
            using (held) return body();
        }
    }

    /// <summary>Write JSON atomically: a flushed temporary file, then one replace. A failed write leaves the old file.</summary>
    private static void WriteAtomically<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, Json);
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static TakeTest? ReadTestFile(string path)
    {
        if (!File.Exists(path)) return null;
        var test = JsonSerializer.Deserialize<TakeTest>(File.ReadAllText(path));
        if (test == null || test.Format != TakeTest.FormatName || !Guid.TryParse(test.Id, out _))
            throw new InvalidDataException("test-record");
        return test;
    }

    /// <summary>Every test under the root. An unreadable record throws rather than reading as absent.</summary>
    public static IReadOnlyList<TakeTest> Tests(string root)
    {
        string directory = TestsDirectory(root);
        if (!Directory.Exists(directory)) return Array.Empty<TakeTest>();
        return Directory.EnumerateDirectories(directory)
            .Select(d => ReadTestFile(Path.Combine(d, "test.json"))).Where(t => t != null).Select(t => t!)
            .OrderBy(t => t.CreatedUtc).ToList();
    }

    /// <summary>One test by its permanent ID, or null.</summary>
    public static TakeTest? Test(string root, string id) =>
        Guid.TryParse(id, out _) ? ReadTestFile(TestPath(root, id)) : null;

    /// <summary>How a name is compared: Unicode-normalized, trimmed, case-insensitive. The spelling shown is kept as typed.</summary>
    public static string NameKey(string name) =>
        (name ?? "").Normalize(NormalizationForm.FormC).Trim().ToUpperInvariant();

    /// <summary>The test whose current name or any earlier name is <paramref name="name"/>, or null.</summary>
    public static TakeTest? FindTest(string root, string name)
    {
        string key = NameKey(name);
        if (key.Length == 0) return null;
        return Tests(root).FirstOrDefault(t => NameKey(t.Name) == key || t.Aliases.Any(a => NameKey(a) == key));
    }

    /// <summary>Make a new test with a fresh phone-readable ID that no test has ever used.</summary>
    public static TakeTest CreateTest(string root, Random? random = null) => Transaction(root, () =>
    {
        var taken = new HashSet<string>(Tests(root).SelectMany(t => t.Aliases.Append(t.Name)).Select(NameKey));
        string readable;
        do readable = random == null ? FixerRunId.New() : FixerRunId.New(random);
        while (taken.Contains(NameKey(readable)));
        var now = DateTime.UtcNow;
        var test = new TakeTest
        {
            Id = Guid.NewGuid().ToString("D"), ReadableId = readable, Name = readable,
            Aliases = new List<string>(), CreatedUtc = now, UpdatedUtc = now,
        };
        WriteAtomically(TestPath(root, test.Id), test);
        return test;
    });

    /// <summary>Why a rename was refused, or "" when it was accepted.</summary>
    public const string RenameEmpty = "empty";
    public const string RenameTooLong = "too-long";
    public const string RenameTaken = "taken";
    public const string RenameUnknown = "unknown-test";
    public const int NameMaximum = 80;

    /// <summary>
    /// Rename a test: the readable name changes, every earlier name becomes an
    /// alias that still finds it, and nothing on disk but test.json moves. A
    /// name or alias of another test is refused, as is an empty, control-only
    /// or over-long one. Returns "" or the refusal's reason.
    /// </summary>
    public static string Rename(string root, string id, string newName) => Transaction(root, () =>
    {
        string cleaned = new string((newName ?? "").Normalize(NormalizationForm.FormC)
            .Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0) return RenameEmpty;
        if (cleaned.Length > NameMaximum) return RenameTooLong;
        var test = Test(root, id);
        if (test == null) return RenameUnknown;
        string key = NameKey(cleaned);
        if (Tests(root).Any(t => t.Id != test.Id && (NameKey(t.Name) == key || t.Aliases.Any(a => NameKey(a) == key))))
            return RenameTaken;
        if (NameKey(test.Name) == key) { test.Name = cleaned; }
        else
        {
            if (!test.Aliases.Any(a => NameKey(a) == NameKey(test.Name))) test.Aliases.Add(test.Name);
            test.Aliases.RemoveAll(a => NameKey(a) == key);
            test.Name = cleaned;
        }
        test.UpdatedUtc = DateTime.UtcNow;
        WriteAtomically(TestPath(root, test.Id), test);
        return "";
    });

    /// <summary>
    /// Reserve the next take number in a test for a take folder, durably,
    /// BEFORE anything is captured: the number first in test.json, then the
    /// take's sidecar. A failed capture keeps its number; numbers are never
    /// reused or renumbered.
    /// </summary>
    public static int Reserve(string root, string testId, string takeFolder) => Transaction(root, () =>
    {
        var test = Test(root, testId) ?? throw new InvalidDataException("unknown-test");
        string folder = Path.GetFileName(takeFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (test.Reservations.Any(r => r.Folder == folder)) throw new InvalidOperationException("already-reserved");
        int number = test.NextTakeNumber++;
        test.Reservations.Add(new TakeReservation { Folder = folder, Number = number, CreatedUtc = DateTime.UtcNow });
        test.UpdatedUtc = DateTime.UtcNow;
        WriteAtomically(TestPath(root, test.Id), test);
        WriteAtomically(Path.Combine(takeFolder, TakeMetadata.FileName),
            new TakeMetadata { TestId = test.Id, TakeNumber = number });
        return number;
    });

    /// <summary>Record what became of a reserved take: made, failed or deleted. The number stays held.</summary>
    public static void MarkReservation(string root, string testId, string takeFolder, string state) => Transaction(root, () =>
    {
        var test = Test(root, testId) ?? throw new InvalidDataException("unknown-test");
        string folder = Path.GetFileName(takeFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var reservation = test.Reservations.FirstOrDefault(r => r.Folder == folder)
            ?? throw new InvalidOperationException("not-reserved");
        reservation.State = state;
        test.UpdatedUtc = DateTime.UtcNow;
        WriteAtomically(TestPath(root, test.Id), test);
        return 0;
    });

    /// <summary>A take's sidecar, or null for a take that has none (every take made before tests existed).</summary>
    public static TakeMetadata? Metadata(string takeFolder)
    {
        string path = Path.Combine(takeFolder, TakeMetadata.FileName);
        if (!File.Exists(path)) return null;
        var meta = JsonSerializer.Deserialize<TakeMetadata>(File.ReadAllText(path));
        return meta == null || meta.Format != TakeMetadata.FormatName ? throw new InvalidDataException("take-metadata") : meta;
    }

    /// <summary>
    /// Set the operator's note on a take. A take with no sidecar gets one
    /// holding only the note: no test is invented for an old take.
    /// </summary>
    public static void SetNote(string root, string takeFolder, string note) => Transaction(root, () =>
    {
        var meta = Metadata(takeFolder) ?? new TakeMetadata();
        meta.Note = new string((note ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        WriteAtomically(Path.Combine(takeFolder, TakeMetadata.FileName), meta);
        return 0;
    });

    /// <summary>
    /// The facts of a test's most recent MADE take, from its own header, or
    /// null when the test has none yet.
    /// </summary>
    public static TakeAdmissionFacts? LastTakeFacts(string root, TakeTest test)
    {
        foreach (var reservation in test.Reservations.Where(r => r.State == TakeReservation.Made).OrderByDescending(r => r.Number))
        {
            string header = Path.Combine(root, reservation.Folder, "take.txt");
            if (!File.Exists(header)) continue;
            try
            {
                var fields = TakeHeader.Read(header);
                string Get(string key) => fields.LastOrDefault(f => f.Key == key).Value ?? "";
                return new(Get("serial"), Get("mic-profile"), Get("mic-input"), Get("pc-microphone-device"));
            }
            catch (InvalidDataException) { }
        }
        return null;
    }

    // ── This run's context: which test the next take joins ──

    /// <summary>The test series context of this run of the app.</summary>
    public static TakeSeriesContext Series { get; } = new();
}

/// <summary>
/// Which test the next take joins, for this run of the app (design section 6,
/// as RULED by Noel 2026-11-02).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A new run starts with no active test; the first take makes one. A
/// test is persisted only when a take joins it, so a run that makes no take
/// leaves no empty test behind.</item>
/// <item>D3: reconnecting to the SAME radio (same serial) in this run keeps
/// the active test, so a SmartLink dropout does not split a series. A
/// connection to a different radio starts a new test at its first take, with
/// no question (there is no predecessor to ask about).</item>
/// <item>Q7 and D5: before the next take, if the mic profile, the mic input,
/// the computer's microphone device or the radio differs from the test's last
/// take, the operator is asked "start a new test or add to this one", unless
/// they already answered "add to this one" for this test.</item>
/// <item>Play takes' "Add a take to this test" resumes any test explicitly,
/// overriding the automatic choice.</item>
/// </list>
/// </remarks>
public sealed class TakeSeriesContext
{
    private readonly object _gate = new();
    private string? _activeTestId;
    private string? _serial;

    /// <summary>The active test's permanent ID, or null when the next take starts a new one.</summary>
    public string? ActiveTestId { get { lock (_gate) return _activeTestId; } }

    /// <summary>A radio connected. The same radio as before keeps the test (D3); another starts afresh.</summary>
    public void Connected(string serial)
    {
        lock (_gate)
        {
            if (_serial != null && !string.Equals(_serial, serial, StringComparison.Ordinal)) _activeTestId = null;
            _serial = serial;
        }
    }

    /// <summary>Resume a test explicitly ("Add a take to this test"); the next take joins it.</summary>
    public void Resume(string testId) { lock (_gate) _activeTestId = testId; }

    /// <summary>Forget everything, as a new run of the app would.</summary>
    public void Reset() { lock (_gate) { _activeTestId = null; _serial = null; } }

    /// <summary>
    /// Whether the next take may join the active test now, or the operator
    /// must be asked first. Asked BEFORE the countdown, never while keyed;
    /// asking changes nothing.
    /// </summary>
    public TakeAdmission Admit(string root, TakeAdmissionFacts next)
    {
        string? id = ActiveTestId;
        var test = id == null ? null : TakeSession.Test(root, id);
        if (test == null) return new(false, null!, Array.Empty<string>());
        var last = TakeSession.LastTakeFacts(root, test);
        if (last == null) return new(false, test, Array.Empty<string>());
        var changed = new List<string>();
        // The microphone stand-ins first, then the profile, then the radio.
        if (last.MicInput != next.MicInput) changed.Add("mic-input");
        if (last.PcMicrophoneDevice != next.PcMicrophoneDevice) changed.Add("pc-microphone-device");
        if (last.MicProfile != next.MicProfile) changed.Add("mic-profile");
        if (last.Serial != next.Serial) changed.Add("serial");
        bool ask = changed.Count > 0 && test.ChangePolicy != TakeTest.PolicyAddToThisTest;
        return new(ask, test, changed);
    }

    /// <summary>
    /// The operator's answer to the question: a new test (made now, and
    /// active), or "add to this one", which silences the question for the
    /// rest of that test, including after it is resumed.
    /// </summary>
    public TakeTest Answer(string root, bool startNewTest)
    {
        if (startNewTest)
        {
            var created = TakeSession.CreateTest(root);
            lock (_gate) _activeTestId = created.Id;
            return created;
        }
        string id = ActiveTestId ?? throw new InvalidOperationException("no-active-test");
        TakeSession.SetChangePolicy(root, id, TakeTest.PolicyAddToThisTest);
        return TakeSession.Test(root, id) ?? throw new InvalidDataException("unknown-test");
    }

    /// <summary>
    /// Reserve the next take's number: in the active test, or in a new test
    /// made now when there is none. Call after <see cref="Admit"/> said no
    /// question is needed, or after <see cref="Answer"/>.
    /// </summary>
    public (TakeTest Test, int Number) Reserve(string root, string takeFolder)
    {
        string? id = ActiveTestId;
        var test = id == null ? null : TakeSession.Test(root, id);
        if (test == null)
        {
            test = TakeSession.CreateTest(root);
            lock (_gate) _activeTestId = test.Id;
        }
        int number = TakeSession.Reserve(root, test.Id, takeFolder);
        return (TakeSession.Test(root, test.Id)!, number);
    }
}

public static partial class TakeSession
{
    /// <summary>Set a test's change policy (<see cref="TakeTest.PolicyAsk"/> or <see cref="TakeTest.PolicyAddToThisTest"/>).</summary>
    public static void SetChangePolicy(string root, string testId, string policy) => Transaction(root, () =>
    {
        var test = Test(root, testId) ?? throw new InvalidDataException("unknown-test");
        test.ChangePolicy = policy;
        test.UpdatedUtc = DateTime.UtcNow;
        WriteAtomically(TestPath(root, test.Id), test);
        return 0;
    });

    /// <summary>
    /// "W6R-23T, take 3", or "Heil test, take 3" after a rename; null for a
    /// take that belongs to no test.
    /// </summary>
    public static (TakeTest Test, int Number)? Membership(string root, string takeFolder)
    {
        TakeMetadata? meta;
        try { meta = Metadata(takeFolder); }
        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is JsonException) { return null; }
        if (meta == null || meta.TestId.Length == 0) return null;
        TakeTest? test;
        try { test = Test(root, meta.TestId); }
        catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is JsonException) { return null; }
        return test == null ? null : (test, meta.TakeNumber);
    }
}
