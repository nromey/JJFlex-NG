using System.Security.Cryptography;

namespace JJFlex.RecordIndex.Crypto;

/// <summary>How the operator (or a test) proves the right to open the index.</summary>
public sealed class UnlockOptions
{
    /// <summary>"auto" (YubiKey if one is enrolled and plugged in, else recovery), "yubikey", or "recovery".</summary>
    public string Method { get; set; } = "auto";

    /// <summary>Where the recovery key comes from: "prompt", "clipboard", or "file:&lt;path&gt;".</summary>
    public string RecoverySource { get; set; } = "prompt";

    /// <summary>Opens a PIV device; the real one by default, the software stand-in under test.</summary>
    public Func<int?, IPivDevice>? OpenPiv { get; set; }

    /// <summary>Reads the recovery key text; replaced under test.</summary>
    public Func<string, string>? ReadRecoveryText { get; set; }
}

public static class Unlocker
{
    /// <summary>
    /// Unwraps the database key with the first recipient the operator can use.
    /// Returns the key and the recipient that opened it. The caller zeroes the key.
    /// </summary>
    public static (byte[] Key, Recipient By) Unlock(Keyring ring, UnlockOptions options, IPrompter prompt)
    {
        var id = ring.DatabaseIdBytes;
        var method = options.Method.ToLowerInvariant();
        var yubis = ring.Recipients.Where(r => r.Kind == Recipient.KindYubiKey).ToList();
        var recoveries = ring.Recipients.Where(r => r.Kind == Recipient.KindRecovery).ToList();

        if (method is "auto" or "yubikey" && yubis.Count > 0)
        {
            var failures = new List<string>();
            foreach (var r in yubis)
            {
                try
                {
                    using var dev = (options.OpenPiv ?? DefaultOpenPiv(prompt))(r.YubiKeySerial);
                    var slot = PivSlots.Parse(r.PivSlot ?? "9d");
                    var key = Envelope.Unwrap(r.Wrap.ToWrapped(), r.PublicKeyBytes, id, eph => dev.KeyAgree(slot, eph));
                    return (Checked(ring, key), r);
                }
                catch (UnlockException e) { failures.Add(e.Message); }
                catch (CryptographicException) { failures.Add($"The key in {r.Describe()} did not open the wrap; the slot may have been regenerated since it was enrolled."); }
            }
            if (method == "yubikey")
                throw new UnlockException(string.Join(" ", failures));
            if (recoveries.Count > 0) prompt.Say(string.Join(" ", failures) + " Falling back to the recovery key.");
        }
        else if (method == "yubikey")
        {
            throw new UnlockException("No YubiKey is enrolled for this index.");
        }

        if (recoveries.Count == 0)
            throw new UnlockException("No recovery key is enrolled for this index, and no YubiKey opened it.");

        var text = (options.ReadRecoveryText ?? (src => RecoveryInput.Read(src, prompt)))(options.RecoverySource);
        using var recovery = RecoveryKey.FromText(text);
        var spki = recovery.ExportSubjectPublicKeyInfo();
        var match = recoveries.FirstOrDefault(r => r.PublicKeyBytes.AsSpan().SequenceEqual(spki))
            ?? throw new UnlockException("That recovery key is a valid one, but it is not enrolled in this index. It may belong to a different index or have been removed.");
        var dbKey = Envelope.Unwrap(match.Wrap.ToWrapped(), match.PublicKeyBytes, id, Envelope.SoftwareAgreement(recovery));
        return (Checked(ring, dbKey), match);
    }

    private static byte[] Checked(Keyring ring, byte[] key)
    {
        if (ring.Verifies(key)) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new UnlockException("The key unwrapped but does not match this database's key check. The keyring may have been mixed up with another index's.");
    }

    public static Func<int?, IPivDevice> DefaultOpenPiv(IPrompter prompt) => serial => YubiKeyPivDevice.Open(prompt, serial);
}
