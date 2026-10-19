using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Radios.Listenback;

public sealed record SavedRadioSetting(string Name, string Value, string RestoreCommand,
    string StatusPrefix = "", string StatusKey = "", string ResourceKind = "", uint ResourceId = 0);
public sealed class RadioRecoverySnapshot
{
    public int Format { get; set; } = 1;
    public string Serial { get; set; }
    public string TakeDirectory { get; set; }
    public uint ClientHandle { get; set; }
    public Guid ConnectionId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<SavedRadioSetting> Settings { get; set; } = new();
    public List<string> ResourceNotes { get; set; } = new();
    public bool ResourceCreationPending { get; set; }
}

/// <summary>
/// Persist intent before changing the radio. A snapshot survives every failure;
/// only confirmed restoration removes it. Reconnection is a discovery operation,
/// never implicit permission to replay commands.
/// </summary>
public sealed class RadioRecoveryStore
{
    private readonly string _root;
    public RadioRecoveryStore(string root) => _root = root;
    public string PathFor(string serial) => Path.Combine(_root,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serial))) + ".json");
    public RadioRecoverySnapshot Find(string serial)
    {
        string path = PathFor(serial);
        if (!File.Exists(path)) return null;
        var saved = JsonSerializer.Deserialize<RadioRecoverySnapshot>(File.ReadAllText(path))
            ?? throw new InvalidDataException("recovery-empty");
        if (saved.Format != 1 || saved.Serial != serial) throw new InvalidDataException("recovery-identity");
        return saved;
    }
    public void Save(RadioRecoverySnapshot snapshot)
    {
        Directory.CreateDirectory(_root);
        string path = PathFor(snapshot.Serial), pending = path + ".pending";
        using (var file = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, snapshot, new JsonSerializerOptions { WriteIndented = true });
            file.Flush(true);
        }
        File.Move(pending, path, true);
    }
    public async Task<bool> RestoreAsync(RadioRecoverySnapshot snapshot,
        Func<SavedRadioSetting, Task<bool>> restoreAndConfirm, bool operatorApproved)
    {
        if (!operatorApproved) return false;
        // Snapshot creation is serialized by the owning radio session. Never
        // clear a newer take's recovery record from an old asynchronous cleanup.
        var current = Find(snapshot.Serial);
        if (current == null) return true;
        if (current.TakeDirectory != snapshot.TakeDirectory) return false;
        bool confirmed = !snapshot.ResourceCreationPending;
        for (int i = snapshot.Settings.Count - 1; i >= 0; --i)
        {
            try { confirmed &= await restoreAndConfirm(snapshot.Settings[i]).ConfigureAwait(false); }
            catch { confirmed = false; }
        }
        if (confirmed && Find(snapshot.Serial)?.TakeDirectory == snapshot.TakeDirectory) File.Delete(PathFor(snapshot.Serial));
        else confirmed = false;
        return confirmed;
    }
}
