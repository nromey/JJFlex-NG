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
///               document item. He deletes the file afterwards. Refused inside
///               JJFlex-private or the repository, which are backed up or public.
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
            RefuseBackedUpOrPublic(path);
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

    internal static void RefuseBackedUpOrPublic(string fullPath)
    {
        string[] refused =
        [
            Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\JJFlex-private"),
            @"C:\dev\jjf-private",
            @"C:\dev\JJFlex-NG",
        ];
        foreach (var r in refused)
            if (fullPath.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                throw new UnlockException($"Not writing a secret under {r}: that folder is swept into backups or is public. Choose a folder outside it, such as your Desktop if it is not synced, or use the clipboard.");
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new UnlockException("Not writing a secret to a network path. It must not leave this machine.");
        var dir = Path.GetDirectoryName(fullPath) ?? "";
        if (Repository.IsInsideGitWorkTree(dir))
            throw new UnlockException($"Not writing a secret inside a git working tree ({dir}).");
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
