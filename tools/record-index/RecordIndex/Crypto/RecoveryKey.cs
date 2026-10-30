using System.Security.Cryptography;
using System.Text;

namespace JJFlex.RecordIndex.Crypto;

/// <summary>
/// The recovery key pair: a software P-256 key whose private half lives only in
/// Noel's password manager. Its text form is one line of hexadecimal in groups
/// of eight, so it pastes cleanly into a 1Password field and back into a hidden
/// prompt, and, if it ever has to be read aloud or typed from paper, it is
/// case-insensitive and spells one character at a time:
///
///   JJFRI1-xxxxxxxx-xxxxxxxx-xxxxxxxx-xxxxxxxx-xxxxxxxx-xxxxxxxx-xxxxxxxx-xxxxxxxx-cccccccc
///
/// Eight groups carry the 32-byte private scalar; the last group is a checksum
/// (the first four bytes of SHA-256 of the scalar), which catches a truncated or
/// mangled paste with a clear message instead of an unexplained decryption error.
/// </summary>
public static class RecoveryKey
{
    private const string Prefix = "JJFRI1";

    public static ECDiffieHellman Generate() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>The single-line secret. Never print it, never log it.</summary>
    public static string ToText(ECDiffieHellman key)
    {
        var p = key.ExportParameters(includePrivateParameters: true);
        byte[] d = p.D ?? throw new CryptographicException("No private half.");
        try
        {
            var hex = Convert.ToHexStringLower(d) + Convert.ToHexStringLower(SHA256.HashData(d).AsSpan(0, 4));
            var sb = new StringBuilder(Prefix);
            for (int i = 0; i < hex.Length; i += 8) sb.Append('-').Append(hex, i, 8);
            return sb.ToString();
        }
        finally { CryptographicOperations.ZeroMemory(d); }
    }

    /// <summary>Parses the text form back into a usable private key.</summary>
    /// <exception cref="FormatException">With a message fit to read aloud.</exception>
    public static ECDiffieHellman FromText(string text)
    {
        var compact = new string((text ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        if (!compact.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("That is not a record index recovery key. It should begin with JJFRI1.");
        var hex = compact[Prefix.Length..];
        if (hex.Length != 72)
            throw new FormatException($"The recovery key should have 72 hexadecimal characters after JJFRI1, and this one has {hex.Length}. Check the whole line was copied.");
        byte[] all;
        try { all = Convert.FromHexString(hex); }
        catch (FormatException) { throw new FormatException("The recovery key has characters that are not hexadecimal. Only the digits 0 to 9 and the letters a to f belong in it."); }
        byte[] d = all[..32];
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(all.AsSpan(32), SHA256.HashData(d).AsSpan(0, 4)))
                throw new FormatException("The recovery key's checksum does not match. Part of it was probably lost or changed in copying.");
            var key = ECDiffieHellman.Create();
            key.ImportParameters(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d });
            return key;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(d);
            CryptographicOperations.ZeroMemory(all);
        }
    }
}
