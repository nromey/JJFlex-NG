namespace JJFlex.RecordIndex.Crypto;

/// <summary>
/// What the record index needs from a PIV device: a P-256 key in a slot that can
/// do key agreement and never leaves the device. The real implementation is
/// <see cref="YubiKeyPivDevice"/>; the tests use a software stand-in with this
/// same interface, because nothing automated may touch Noel's YubiKey.
/// </summary>
public interface IPivDevice : IDisposable
{
    /// <summary>The device's serial number, recorded in the keyring so the right key is asked for.</summary>
    int? Serial { get; }

    /// <summary>The slot's public key as SubjectPublicKeyInfo, or null when the slot is empty.</summary>
    byte[]? GetPublicKey(byte slot);

    /// <summary>
    /// Generates a new P-256 key in the slot and returns its public key. The
    /// private key is created on the device and cannot be exported.
    /// </summary>
    byte[] GenerateP256(byte slot, PivTouch touch);

    /// <summary>
    /// ECDH with the slot's private key. Returns the raw shared x-coordinate.
    /// May ask for the PIN and a touch.
    /// </summary>
    byte[] KeyAgree(byte slot, byte[] peerSpki);
}

public enum PivTouch { Never, Always, Cached }

public static class PivSlots
{
    /// <summary>9D, PIV's designated key-management (decryption) slot.</summary>
    public const byte KeyManagement = 0x9D;

    /// <summary>Accepts "9d", "82" ... "95" (the retired key-management slots).</summary>
    public static byte Parse(string text)
    {
        var t = text.Trim().ToLowerInvariant().Replace("0x", "");
        byte b = Convert.ToByte(t, 16);
        if (b == KeyManagement || (b >= 0x82 && b <= 0x95)) return b;
        throw new FormatException("Use slot 9d, or one of the retired key-management slots 82 to 95. Slots 9a, 9c and 9e are for signing and authentication, not for this.");
    }

    public static string Name(byte slot) => slot.ToString("x2");
}
