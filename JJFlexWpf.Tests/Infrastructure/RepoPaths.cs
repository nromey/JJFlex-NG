using System.IO;
namespace JJFlexWpf.Tests.Infrastructure;

/// <summary>
/// Finds the worktree root from wherever the test assembly happens to be, so
/// the source-level checks and the generated inventory do not depend on the
/// build output layout.
/// </summary>
public static class RepoPaths
{
    private static readonly Lazy<string?> LazyRoot = new(Locate, isThreadSafe: true);

    public static string? Root => LazyRoot.Value;

    public static string RequireRoot()
        => Root ?? throw new InvalidOperationException(
            "Could not locate the worktree root (no JJFlexRadio.sln above " + AppContext.BaseDirectory + ").");

    private static string? Locate()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JJFlexRadio.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }
}
