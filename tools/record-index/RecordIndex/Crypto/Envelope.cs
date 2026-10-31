using System.Security.Cryptography;
using System.Text;

namespace JJFlex.RecordIndex.Crypto;

/// <summary>
/// Envelope encryption for the database key (#854).
///
/// The database is encrypted under one random 256-bit key. That key is never
/// stored in the clear: for each unlocking key pair ("recipient") it is wrapped
/// with ECDH P-256, HKDF-SHA256 and AES-256-GCM, the ECIES shape:
///
///   1. A fresh ephemeral P-256 key pair is made for every wrap.
///   2. Z = ECDH(ephemeral private, recipient public), the raw x-coordinate.
///      That is exactly what a YubiKey's PIV GENERAL AUTHENTICATE returns for
///      key agreement, so a key that never leaves the YubiKey can unwrap.
///   3. KEK = HKDF-SHA256(Z, salt = SHA-256(ephemeral SPKI || recipient SPKI),
///      info = label || database id).
///   4. The database key is sealed with AES-256-GCM under KEK. The associated
///      data binds the wrap to this database and this recipient, so a wrap cannot
///      be moved to another keyring entry or another database unnoticed.
///
/// Adding or removing a recipient re-wraps this 32-byte key only. The database
/// itself is not touched.
/// </summary>
public static class Envelope
{
    public const int KeyBytes = 32;
    private const string Label = "jjflex-record-index/v1/wrap";

    /// <summary>Creates a fresh random database (or staging) key.</summary>
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    public static WrappedKey Wrap(ReadOnlySpan<byte> key, byte[] recipientSpki, byte[] contextId)
    {
        if (key.Length != KeyBytes) throw new ArgumentException("A database key is 32 bytes.", nameof(key));
        using var recipient = ImportPublic(recipientSpki);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        byte[] ephemeralSpki = ephemeral.ExportSubjectPublicKeyInfo();
        byte[] z = ephemeral.DeriveRawSecretAgreement(recipient.PublicKey);
        try
        {
            byte[] kek = DeriveKek(z, ephemeralSpki, recipientSpki, contextId);
            try
            {
                byte[] nonce = RandomNumberGenerator.GetBytes(12);
                byte[] ciphertext = new byte[key.Length];
                byte[] tag = new byte[16];
                using var gcm = new AesGcm(kek, 16);
                gcm.Encrypt(nonce, key, ciphertext, tag, Aad(recipientSpki, contextId));
                return new WrappedKey(ephemeralSpki, nonce, ciphertext, tag);
            }
            finally { CryptographicOperations.ZeroMemory(kek); }
        }
        finally { CryptographicOperations.ZeroMemory(z); }
    }

    /// <summary>
    /// Unwraps with whatever can perform the key agreement: a software private
    /// key, or a YubiKey slot. <paramref name="agree"/> receives the ephemeral
    /// public key (SPKI) and returns Z, the raw shared x-coordinate.
    /// </summary>
    public static byte[] Unwrap(WrappedKey wrapped, byte[] recipientSpki, byte[] contextId, Func<byte[], byte[]> agree)
    {
        byte[] z = agree(wrapped.EphemeralSpki);
        try
        {
            byte[] kek = DeriveKek(z, wrapped.EphemeralSpki, recipientSpki, contextId);
            try
            {
                byte[] key = new byte[wrapped.Ciphertext.Length];
                using var gcm = new AesGcm(kek, 16);
                gcm.Decrypt(wrapped.Nonce, wrapped.Ciphertext, wrapped.Tag, key, Aad(recipientSpki, contextId));
                return key;
            }
            finally { CryptographicOperations.ZeroMemory(kek); }
        }
        finally { CryptographicOperations.ZeroMemory(z); }
    }

    /// <summary>Key agreement with a software P-256 private key.</summary>
    public static Func<byte[], byte[]> SoftwareAgreement(ECDiffieHellman privateKey) =>
        ephemeralSpki =>
        {
            using var peer = ImportPublic(ephemeralSpki);
            return privateKey.DeriveRawSecretAgreement(peer.PublicKey);
        };

    public static ECDiffieHellman ImportPublic(byte[] spki)
    {
        var k = ECDiffieHellman.Create();
        k.ImportSubjectPublicKeyInfo(spki, out _);
        if (k.KeySize != 256) { k.Dispose(); throw new CryptographicException("Only P-256 keys are accepted."); }
        return k;
    }

    /// <summary>A short, stable identifier for a public key: the first 8 bytes of its SHA-256, in hex.</summary>
    public static string Fingerprint(byte[] spki) => Convert.ToHexStringLower(SHA256.HashData(spki).AsSpan(0, 8));

    /// <summary>
    /// A key check value: proves an unwrapped key is the database's key without
    /// revealing anything about it (HMAC output, truncated).
    /// </summary>
    public static byte[] KeyCheck(ReadOnlySpan<byte> key, byte[] contextId)
    {
        var msg = Concat(Encoding.UTF8.GetBytes("jjflex-record-index/v1/check"), contextId);
        return HMACSHA256.HashData(key, msg).AsSpan(0, 16).ToArray();
    }

    private static byte[] DeriveKek(byte[] z, byte[] ephemeralSpki, byte[] recipientSpki, byte[] contextId)
    {
        byte[] salt = SHA256.HashData(Concat(ephemeralSpki, recipientSpki));
        byte[] info = Concat(Encoding.UTF8.GetBytes(Label), contextId);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, z, KeyBytes, salt, info);
    }

    private static byte[] Aad(byte[] recipientSpki, byte[] contextId) =>
        Concat(Encoding.UTF8.GetBytes(Label), contextId, SHA256.HashData(recipientSpki));

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        int at = 0;
        foreach (var p in parts) { p.CopyTo(all, at); at += p.Length; }
        return all;
    }
}

public sealed record WrappedKey(byte[] EphemeralSpki, byte[] Nonce, byte[] Ciphertext, byte[] Tag);
