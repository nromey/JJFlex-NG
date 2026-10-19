using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Radios.Listenback;

public sealed record SavedRadioSetting(string Name, string Value, string RestoreCommand,
    string StatusPrefix = "", string StatusKey = "", string ResourceKind = "", uint ResourceId = 0, string RequestedValue = null);
public sealed record RecoveryResource(string Kind, uint Id, uint Owner, uint PanId, double Frequency,
    string Mode, string Antenna);
public sealed record SavedListenerRequest(uint SliceId, uint PanId, double Frequency, string Mode, string ReceiveAntenna);
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
    public SavedListenerRequest ListenerRequest { get; set; }
    public int CreationEvidenceVersion { get; set; }
    public string GuiClientId { get; set; }
    public int? PriorActiveSlice { get; set; }
    public List<RecoveryResource> Resources { get; set; } = new();
    public string UnreadableHash { get; set; }
    public string ReadProblem { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string ContentHash { get; set; }
}

/// <summary>
/// Persist intent before changing the radio. A snapshot survives every failure;
/// only confirmed restoration removes it. Reconnection is a discovery operation,
/// never implicit permission to replay commands.
/// </summary>
public sealed partial class RadioRecoveryStore
{
    private readonly string _root;
    public RadioRecoveryStore(string root) => _root = root;
    public string PathFor(string serial) => Path.Combine(_root,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serial))) + ".json");
    public RadioRecoverySnapshot Find(string serial)
    {
        string path = PathFor(serial);
        if (!File.Exists(path)) return null;
        byte[] bytes = File.ReadAllBytes(path);
        try
        {
            var saved = JsonSerializer.Deserialize<RadioRecoverySnapshot>(bytes)
                ?? throw new InvalidDataException("recovery-empty");
            if (saved.Format != 1 || saved.Serial != serial || saved.Settings == null || saved.ResourceNotes == null ||
                saved.Resources == null || string.IsNullOrEmpty(saved.TakeDirectory) || saved.UnreadableHash != null ||
                saved.Settings.Any(s => s == null || string.IsNullOrEmpty(s.Name) || s.RestoreCommand == null || s.Value == null || s.ResourceKind == null || s.StatusPrefix == null || s.StatusKey == null) ||
                saved.Resources.Any(r => r == null) || saved.ResourceNotes.Any(n => n == null))
                throw new InvalidDataException("recovery-identity");
            saved.ContentHash = Convert.ToHexString(SHA256.HashData(bytes));
            return saved;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return new() { Serial = serial, UnreadableHash = Convert.ToHexString(SHA256.HashData(bytes)), ReadProblem = ex.GetType().Name };
        }
    }
    // This is an explicit acknowledgement after manual inspection, never a
    // successful restoration. Preserve the exact evidence, including wrong-radio files.
    public bool ArchiveUnreadable(RadioRecoverySnapshot offered, bool operatorApproved)
    {
        if (!operatorApproved || offered.UnreadableHash == null) return false;
        var current = Find(offered.Serial);
        if (current?.UnreadableHash != offered.UnreadableHash) return false;
        string path = PathFor(offered.Serial);
        File.Move(path, path + ".unreadable-" + Guid.NewGuid().ToString("N"));
        return true;
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
        if (snapshot.UnreadableHash != null) return false;
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
