using System.Text;

namespace JJFlex.RecordIndex.Crypto;

/// <summary>Talks to the operator. Plain lines only: this is read by a screen reader.</summary>
public interface IPrompter
{
    void Say(string line);

    /// <summary>Reads a secret without echoing it. Returns "" if the operator just pressed Enter.</summary>
    string Secret(string label);

    /// <summary>Waits for Enter. Returns false if there is no one at the console to press it.</summary>
    bool WaitForEnter(string label);
}

public sealed class ConsolePrompter : IPrompter
{
    public void Say(string line) => Console.Error.WriteLine(line);

    public string Secret(string label)
    {
        if (Console.IsInputRedirected)
            throw new UnlockException($"{label} is needed, and there is no one at a console to type it. Run this command yourself in a terminal.");
        Console.Error.Write(label + ": ");
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }
            if (k.Key == ConsoleKey.Escape) { sb.Clear(); break; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.ToString();
    }

    public bool WaitForEnter(string label)
    {
        if (Console.IsInputRedirected) return false;
        Console.Error.Write(label);
        while (Console.ReadKey(intercept: true).Key != ConsoleKey.Enter) { }
        Console.Error.WriteLine();
        return true;
    }
}

/// <summary>Where the recovery key text comes from when unlocking.</summary>
public static class RecoveryInput
{
    public static string Read(string source, IPrompter prompt)
    {
        if (source.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            var path = source[5..];
            if (!File.Exists(path)) throw new UnlockException($"There is no recovery key file at {path}.");
            return File.ReadAllText(path);
        }
        if (source.Equals("clipboard", StringComparison.OrdinalIgnoreCase))
        {
            var text = SecureClipboard.ReadText()
                ?? throw new UnlockException("The clipboard holds no text. Copy the recovery key from 1Password first.");
            // Only clear what we just consumed; anything else on the clipboard is the operator's.
            SecureClipboard.ClearIfEquals(text);
            prompt.Say("Read the recovery key from the clipboard, and cleared the clipboard.");
            return text;
        }
        prompt.Say("Paste or type the record index recovery key from 1Password. Nothing you type is shown. Press Enter when done.");
        return prompt.Secret("Recovery key");
    }
}
