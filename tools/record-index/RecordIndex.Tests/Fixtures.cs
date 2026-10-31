using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using JJFlex.RecordIndex;
using JJFlex.RecordIndex.Crypto;

namespace JJFlex.RecordIndex.Tests;

/// <summary>
/// A software stand-in for a YubiKey's PIV applet: same interface, keys held in
/// memory, and a PIN check so the tests can see the PIN being asked for. Nothing
/// automated may touch the operator's real YubiKey.
/// </summary>
public sealed class SoftwarePivDevice(int serial, string pin = "123456") : IPivDevice
{
    private readonly Dictionary<byte, ECDiffieHellman> _slots = new();
    public int AgreeCount { get; private set; }
    public IPrompter? Prompt { get; set; }
    public int? Serial => serial;

    public byte[]? GetPublicKey(byte slot) => _slots.TryGetValue(slot, out var k) ? k.ExportSubjectPublicKeyInfo() : null;

    public byte[] GenerateP256(byte slot, PivTouch touch)
    {
        _slots[slot] = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return _slots[slot].ExportSubjectPublicKeyInfo();
    }

    public byte[] KeyAgree(byte slot, byte[] peerSpki)
    {
        if (!_slots.TryGetValue(slot, out var k)) throw new UnlockException("Slot empty.");
        if (Prompt is not null && Prompt.Secret("YubiKey PIV PIN") != pin) throw new UnlockException("Wrong PIN.");
        AgreeCount++;
        using var peer = Envelope.ImportPublic(peerSpki);
        return k.DeriveRawSecretAgreement(peer.PublicKey);
    }

    public void Dispose() { }
}

public sealed class TestPrompter : IPrompter
{
    public List<string> Said { get; } = new();
    public Queue<string> Secrets { get; } = new();
    public void Say(string line) => Said.Add(line);
    public string Secret(string label) => Secrets.Count > 0 ? Secrets.Dequeue() : "";
    public bool WaitForEnter(string label) => true;
}

/// <summary>A disposable synthetic record plus an index home, both under %TEMP%.</summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "record-index-tests", Guid.NewGuid().ToString("N"));
    public string Record => Path.Combine(Root, "historical");
    public string HomeFolder => Path.Combine(Root, "index");
    public IndexHome Home { get; }
    public TestPrompter Prompt { get; } = new();
    public SoftwarePivDevice Yubi { get; } = new(serial: 1234567);
    public string? RecoveryText { get; private set; }

    public Sandbox()
    {
        Directory.CreateDirectory(Record);
        Home = new IndexHome(HomeFolder);
    }

    /// <summary>Unlock options that use the stand-in YubiKey and the captured recovery key, never the console.</summary>
    public UnlockOptions Unlock(string method = "auto", string? recovery = null) => new()
    {
        Method = method,
        OpenPiv = _ => Yubi,
        ReadRecoveryText = _ => recovery ?? RecoveryText ?? throw new InvalidOperationException("no recovery key"),
    };

    public void Init(bool withYubiKey = true)
    {
        Home.Init(new IndexHome.InitOptions
        {
            RecordRoot = Record,
            WithYubiKey = withYubiKey,
            CaptureRecovery = t => RecoveryText = t,
        }, Unlock(), Prompt);
    }

    public string Zip(string relative, params (string Path, string Text)[] members) => ZipBytes(relative, members.Select(m => (m.Path, Encoding.UTF8.GetBytes(m.Text))).ToArray());

    public string ZipBytes(string relative, params (string Path, byte[] Data)[] members)
    {
        var full = Path.Combine(Record, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (File.Exists(full)) File.Delete(full);
        using (var z = ZipFile.Open(full, ZipArchiveMode.Create))
            foreach (var (p, data) in members)
            {
                var e = z.CreateEntry(p);
                using var s = e.Open();
                s.Write(data);
            }
        return full;
    }

    public string Loose(string relative, string text)
    {
        var full = Path.Combine(Record, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    public static string Transcript(params (string Role, string Text, string Ts)[] lines) =>
        string.Join("\n", lines.Select((l, i) =>
            System.Text.Json.JsonSerializer.Serialize(new
            {
                type = l.Role,
                uuid = Guid.NewGuid().ToString(),
                timestamp = l.Ts,
                message = new { role = l.Role, content = new object[] { new { type = "text", text = l.Text } } },
            }))) + "\n";

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
