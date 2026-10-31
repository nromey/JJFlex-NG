using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JJFlex.RecordIndex.Crypto;

/// <summary>
/// The keyring file that sits beside the database. Everything in it is safe to
/// be read by anyone who can read the database file: public keys, and the
/// database key wrapped once per recipient. It never holds a secret in the clear.
/// </summary>
public sealed class Keyring
{
    public const string FormatName = "jjflex-record-index-keyring";

    [JsonPropertyName("format")] public string Format { get; set; } = FormatName;
    [JsonPropertyName("version")] public int Version { get; set; } = 1;

    /// <summary>Random 16 bytes naming this database; bound into every wrap.</summary>
    [JsonPropertyName("databaseId")] public string DatabaseId { get; set; } = "";

    /// <summary>Proves an unwrapped key is the right one (HMAC, truncated). Reveals nothing about the key.</summary>
    [JsonPropertyName("keyCheck")] public string KeyCheck { get; set; } = "";

    [JsonPropertyName("cipher")] public string Cipher { get; set; } = "SQLCipher 4 page encryption (via SQLite3 Multiple Ciphers), raw 256-bit key";

    /// <summary>
    /// Public half of the staging key. The seal's hook encrypts a newly staged
    /// archive to this key without unlocking anything; the private half lives
    /// INSIDE the encrypted database, so only an unlocked index can read a stage.
    /// </summary>
    [JsonPropertyName("stagingPublicKey")] public string StagingPublicKey { get; set; } = "";

    [JsonPropertyName("recipients")] public List<Recipient> Recipients { get; set; } = new();

    [JsonIgnore] public byte[] DatabaseIdBytes => Convert.FromBase64String(DatabaseId);

    public static Keyring Load(string path)
    {
        if (!File.Exists(path))
            throw new UnlockException($"There is no keyring at {path}. Run record-index init first, or name the right folder with --home.");
        var k = JsonSerializer.Deserialize<Keyring>(File.ReadAllText(path))
            ?? throw new UnlockException("The keyring file is empty.");
        if (k.Format != FormatName || k.Version != 1)
            throw new UnlockException("The keyring file is not one this version of record-index understands.");
        return k;
    }

    /// <summary>
    /// Writes atomically: a crash mid-write can never leave a keyring that
    /// unlocks nothing. No backup copy is kept on purpose: an old keyring would
    /// still hold a wrap for a recipient that was removed.
    /// </summary>
    public void Save(string path)
    {
        if (Recipients.Count == 0) throw new InvalidOperationException("Refusing to save a keyring with no recipients: nothing could ever unlock it.");
        var tmp = path + ".new";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }

    public bool Verifies(ReadOnlySpan<byte> key) =>
        CryptographicOperations.FixedTimeEquals(Envelope.KeyCheck(key, DatabaseIdBytes), Convert.FromBase64String(KeyCheck));
}

public sealed class Recipient
{
    public const string KindRecovery = "recovery";
    public const string KindYubiKey = "yubikey-piv";

    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("publicKey")] public string PublicKey { get; set; } = "";
    [JsonPropertyName("added")] public string Added { get; set; } = "";
    [JsonPropertyName("yubikeySerial")] public int? YubiKeySerial { get; set; }
    [JsonPropertyName("pivSlot")] public string? PivSlot { get; set; }
    [JsonPropertyName("wrap")] public WrapJson Wrap { get; set; } = new();

    [JsonIgnore] public byte[] PublicKeyBytes => Convert.FromBase64String(PublicKey);

    public static Recipient Create(string kind, string label, byte[] spki, ReadOnlySpan<byte> dbKey, byte[] databaseId)
    {
        var r = new Recipient
        {
            Id = Envelope.Fingerprint(spki),
            Kind = kind,
            Label = label,
            PublicKey = Convert.ToBase64String(spki),
            Added = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };
        r.Rewrap(dbKey, databaseId);
        return r;
    }

    public void Rewrap(ReadOnlySpan<byte> dbKey, byte[] databaseId) =>
        Wrap = WrapJson.From(Envelope.Wrap(dbKey, PublicKeyBytes, databaseId));

    public string Describe() => Kind switch
    {
        KindYubiKey => $"{Label}: YubiKey serial {YubiKeySerial?.ToString() ?? "unknown"}, PIV slot {PivSlot}, id {Id}",
        _ => $"{Label}: recovery key, id {Id}",
    };
}

public sealed class WrapJson
{
    [JsonPropertyName("ephemeralPublicKey")] public string EphemeralPublicKey { get; set; } = "";
    [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("ciphertext")] public string Ciphertext { get; set; } = "";
    [JsonPropertyName("tag")] public string Tag { get; set; } = "";

    public static WrapJson From(WrappedKey w) => new()
    {
        EphemeralPublicKey = Convert.ToBase64String(w.EphemeralSpki),
        Nonce = Convert.ToBase64String(w.Nonce),
        Ciphertext = Convert.ToBase64String(w.Ciphertext),
        Tag = Convert.ToBase64String(w.Tag),
    };

    public WrappedKey ToWrapped() => new(
        Convert.FromBase64String(EphemeralPublicKey), Convert.FromBase64String(Nonce),
        Convert.FromBase64String(Ciphertext), Convert.FromBase64String(Tag));
}

/// <summary>A failure to unlock, worded to be read aloud to the operator.</summary>
public sealed class UnlockException(string message) : Exception(message);
