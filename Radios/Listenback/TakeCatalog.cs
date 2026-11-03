using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Radios.Listenback;

/// <summary>
/// One folder under the takes root, as the catalog lists it: its kind, read
/// from the header alone, and when it was made. Reading a header never reads
/// the take's audio or IQ, so a long list stays cheap; the full integrity
/// check (<see cref="TakeReader.Inspect"/>) is asked for one take at a time.
/// </summary>
public sealed record TakeCatalogEntry(string Directory, DateTime MadeUtc, string Kind,
    IReadOnlyList<KeyValuePair<string, string>> Header, string HeaderProblem)
{
    public string Name => Path.GetFileName(Directory);
    public string Get(string key) => Header.LastOrDefault(x => x.Key == key).Value;
    /// <summary>False for a folder whose header could not be read at all.</summary>
    public bool HeaderReadable => HeaderProblem.Length == 0;
}

/// <summary>
/// The ONE inventory of takes, replacing "any folder with a heard.wav"
/// (Unified takes, design section 1). Every caller states the kind it wants;
/// nothing picks up a take of another kind by accident.
/// </summary>
/// <remarks>
/// <para>
/// <b>Folders, not files.</b> Every directory under the root is a take except
/// the dot-named ones, which hold the test registry (<see cref="TestsFolder"/>)
/// and anything else that is bookkeeping. A folder whose header cannot be read
/// is still listed, as <see cref="TakeKind.Damaged"/>, so a damaged take is an
/// identifiable row and not an absence.
/// </para>
/// <para>
/// <b>Never writes.</b> Nothing here creates, renames, moves or touches a take
/// folder or its <c>take.txt</c>.
/// </para>
/// </remarks>
public static class TakeCatalog
{
    /// <summary>The folder under the takes root that holds the test registry.</summary>
    public const string TestsFolder = ".tests";

    /// <summary>Whether a directory under the root is a take folder rather than bookkeeping.</summary>
    public static bool IsTakeFolder(string directory)
    {
        string name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return name.Length > 0 && name[0] != '.';
    }

    /// <summary>Read one take folder's catalog entry. Never throws for a damaged folder.</summary>
    public static TakeCatalogEntry Read(string directory)
    {
        DateTime made;
        try { made = TakeSession.MadeUtc(directory); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        { made = DateTime.MinValue; }
        try
        {
            var header = TakeHeader.Read(Path.Combine(directory, "take.txt"));
            return new(directory, made, TakeKind.Of(header), header, "");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
        {
            return new(directory, made, TakeKind.Damaged, Array.Empty<KeyValuePair<string, string>>(),
                ex is InvalidDataException ? ex.Message : ex.GetType().Name);
        }
    }

    /// <summary>
    /// Every take under <paramref name="root"/>, newest first by when it was
    /// made (<see cref="TakeSession.MadeUtc"/>), the folder name breaking ties
    /// so the order is stable.
    /// </summary>
    public static IReadOnlyList<TakeCatalogEntry> List(string root)
    {
        if (!Directory.Exists(root)) return Array.Empty<TakeCatalogEntry>();
        return Directory.EnumerateDirectories(root).Where(IsTakeFolder).Select(Read)
            .OrderByDescending(e => e.MadeUtc).ThenByDescending(e => e.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The newest take of one of <paramref name="kinds"/> that has a
    /// <c>heard.wav</c>, other than <paramref name="except"/>, or null. The
    /// order is the one the IQ callers always used (take.txt's last write);
    /// the kind is read only for candidates in that order, so the header of
    /// every take is not read on each call.
    /// </summary>
    public static string? Newest(string root, IReadOnlyCollection<string> kinds, string? except = null)
    {
        if (!Directory.Exists(root)) return null;
        foreach (string directory in Directory.EnumerateDirectories(root)
                     .Where(d => IsTakeFolder(d) && d != except && File.Exists(Path.Combine(d, "heard.wav")) &&
                                 File.Exists(Path.Combine(d, "take.txt")))
                     .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "take.txt"))))
            if (kinds.Contains(Read(directory).Kind)) return directory;
        return null;
    }
}
