namespace JJFlex.RecordIndex.Crypto;

/// <summary>
/// Hands a newly made recovery key to Noel to put in 1Password. The key is never
/// printed, never written to a log, and never written anywhere that a backup
/// script sweeps up. Two ways, his choice:
///
///   clipboard   The key goes on the clipboard, kept out of clipboard history
///               and cloud sync. He pastes it into a new 1Password item, comes
///               back, presses Enter, and the clipboard is cleared.
///
///   file:PATH   The key is written to a file he names, for a 1Password
///               document item. He deletes the file afterwards. Refused in any
///               folder that syncs, is backed up, or is public (see LocalOnly).
///
/// Either way, before the index trusts the new key, he proves the copy in
/// 1Password is good by pasting it back (see <see cref="Verify"/>).
/// </summary>
public static class RecoveryHandoff
{
    public static void Hand(string recoveryText, string target, IPrompter prompt)
    {
        if (target.Equals("clipboard", StringComparison.OrdinalIgnoreCase))
        {
            SecureClipboard.SetSecret(recoveryText);
            prompt.Say("The new recovery key is on the clipboard. It is kept out of clipboard history and cloud sync.");
            prompt.Say("Switch to 1Password, make a new item named JJ Flexible record index recovery key, paste it into a password or concealed field, and save the item.");
            if (!prompt.WaitForEnter("Then come back here and press Enter to clear the clipboard. "))
            {
                SecureClipboard.ClearIfEquals(recoveryText);
                throw new UnlockException("No one is at the console to confirm the key was saved, so the clipboard was cleared and nothing was enrolled.");
            }
            SecureClipboard.ClearIfEquals(recoveryText);
            prompt.Say("Clipboard cleared.");
            return;
        }
        if (target.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var path = Path.GetFullPath(target[5..]);
            LocalOnly.Refuse(path, "a secret");
            if (File.Exists(path)) throw new UnlockException($"{path} already exists. Name a new file.");
            File.WriteAllText(path, recoveryText + Environment.NewLine);
            prompt.Say($"The new recovery key is in {path}. Add that file to 1Password as a document, or open it and paste its one line into a new item, then delete the file.");
            return;
        }
        throw new UnlockException("Say where the recovery key should go: --recovery-to clipboard, or --recovery-to file:PATH.");
    }

    /// <summary>
    /// The operator pastes the key back from 1Password, proving the stored copy
    /// is complete before anything depends on it.
    /// </summary>
    public static bool Verify(byte[] expectedSpki, UnlockOptions options, IPrompter prompt)
    {
        prompt.Say("Now prove the copy in 1Password is good: copy it from 1Password and paste it here.");
        string text;
        try { text = (options.ReadRecoveryText ?? (src => RecoveryInput.Read(src, prompt)))(options.RecoverySource); }
        catch (UnlockException e) { prompt.Say(e.Message); return false; }
        try
        {
            using var k = RecoveryKey.FromText(text);
            if (k.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(expectedSpki)) { prompt.Say("That matches. The recovery key is good."); return true; }
            prompt.Say("That is a recovery key, but not the new one.");
            return false;
        }
        catch (FormatException e) { prompt.Say(e.Message); return false; }
    }
}

/// <summary>
/// Refuses to write secrets or private record content anywhere it would leave
/// this machine or become public: synced folders (OneDrive, which on this machine
/// holds the Desktop and Documents; Dropbox), backed-up trees (JJFlex-private,
/// C:\dev, the Claude state, JJ Flexible's settings), network paths, and any git
/// working tree.
/// </summary>
public static class LocalOnly
{
    public static void Refuse(string fullPath, string what)
    {
        // On this machine the Desktop and Documents are redirected into OneDrive,
        // so "put it on the Desktop" would upload the secret. Every synced or
        // backed-up root is refused, not just the obvious ones.
        var refused = new List<string>
        {
            Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\JJFlex-private"),
            @"C:\dev",
            Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\.claude"),
            Environment.ExpandEnvironmentVariables(@"%APPDATA%\JJFlexRadio"),
        };
        foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } od) refused.Add(od);
        refused.Add(Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\OneDrive"));
        if (DropboxRoot() is string dropbox) refused.Add(dropbox);

        foreach (var r in refused)
            if (fullPath.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new UnlockException($"Not writing {what} under {r}: that folder is synced, backed up, or public. Use the clipboard instead, or a folder nothing syncs, such as {Path.GetTempPath().TrimEnd('\\')}.");
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new UnlockException($"Not writing {what} to a network path. It must not leave this machine.");
        var dir = Path.GetDirectoryName(fullPath) ?? "";
        if (Repository.IsInsideGitWorkTree(dir))
            throw new UnlockException($"Not writing {what} inside a git working tree ({dir}).");
    }

    /// <summary>The Dropbox folder, from Dropbox's own info.json (it differs between machines).</summary>
    private static string? DropboxRoot()
    {
        try
        {
            var info = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Dropbox\info.json");
            if (!File.Exists(info)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(info));
            foreach (var account in doc.RootElement.EnumerateObject())
                if (account.Value.TryGetProperty("path", out var p) && p.GetString() is { Length: > 0 } path) return path;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        return null;
    }
}

internal static class Repository
{
    public static bool IsInsideGitWorkTree(string dir)
    {
        for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git"))) return true;
        return false;
    }
}
