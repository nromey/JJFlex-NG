using System.Security.Cryptography;
using JJFlex.RecordIndex.Crypto;
using JJFlex.RecordIndex.Search;
using Xunit;

namespace JJFlex.RecordIndex.Tests;

public class KeyTests
{
    [Fact]
    public void A_wrap_opens_only_with_its_own_key_and_context()
    {
        var dbKey = Envelope.NewKey();
        using var r = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var spki = r.ExportSubjectPublicKeyInfo();
        var id = RandomNumberGenerator.GetBytes(16);
        var w = Envelope.Wrap(dbKey, spki, id);

        Assert.Equal(dbKey, Envelope.Unwrap(w, spki, id, Envelope.SoftwareAgreement(r)));
        Assert.ThrowsAny<CryptographicException>(() => Envelope.Unwrap(w, spki, id, Envelope.SoftwareAgreement(other)));
        Assert.ThrowsAny<CryptographicException>(() => Envelope.Unwrap(w, spki, RandomNumberGenerator.GetBytes(16), Envelope.SoftwareAgreement(r)));
        var tampered = w with { Ciphertext = (byte[])w.Ciphertext.Clone() };
        tampered.Ciphertext[0] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => Envelope.Unwrap(tampered, spki, id, Envelope.SoftwareAgreement(r)));
    }

    [Fact]
    public void The_recovery_key_text_round_trips_and_catches_a_bad_paste()
    {
        using var k = RecoveryKey.Generate();
        var text = RecoveryKey.ToText(k);
        Assert.StartsWith("JJFRI1-", text);
        Assert.Equal(7 + 9 * 9 - 1, text.Length);

        using var back = RecoveryKey.FromText("  " + text.ToUpperInvariant().Replace("JJFRI1", "jjfri1") + "\r\n");
        Assert.Equal(k.ExportSubjectPublicKeyInfo(), back.ExportSubjectPublicKeyInfo());

        var e1 = Assert.Throws<FormatException>(() => RecoveryKey.FromText(text[..^4]));
        Assert.Contains("72", e1.Message);
        var flipped = text[..10] + (text[10] == 'a' ? 'b' : 'a') + text[11..];
        Assert.Contains("checksum", Assert.Throws<FormatException>(() => RecoveryKey.FromText(flipped)).Message);
        Assert.Contains("JJFRI1", Assert.Throws<FormatException>(() => RecoveryKey.FromText("hunter2")).Message);
    }

    [Fact]
    public void Either_key_opens_the_index()
    {
        using var s = new Sandbox();
        s.Zip(@"memory\memory-20260101-010101.zip", ("a.md", "envelope alpha"));
        s.Init(withYubiKey: true);
        var ring = s.Home.LoadKeyring();
        Assert.Equal(2, ring.Recipients.Count);
        Assert.Contains(ring.Recipients, r => r.Kind == Recipient.KindYubiKey && r.YubiKeySerial == 1234567 && r.PivSlot == "9d");

        using (var ix = s.Home.Open(s.Unlock("yubikey"), s.Prompt)) { Assert.Equal(Recipient.KindYubiKey, ix.UnlockedBy.Kind); ix.CatchUp(s.Prompt); }
        using (var ix = s.Home.Open(s.Unlock("recovery"), s.Prompt, readOnly: true))
        {
            Assert.Equal(Recipient.KindRecovery, ix.UnlockedBy.Kind);
            Assert.Single(Searcher.Search(ix.Db, new SearchQuery { Text = "alpha" }).Hits);
        }
    }

    [Fact]
    public void The_YubiKey_path_asks_for_the_PIN_and_refuses_a_wrong_one()
    {
        using var s = new Sandbox();
        s.Init(withYubiKey: true);
        s.Yubi.Prompt = s.Prompt;
        s.Prompt.Secrets.Enqueue("000000");
        Assert.Throws<UnlockException>(() => s.Home.Open(s.Unlock("yubikey"), s.Prompt).Dispose());
        s.Prompt.Secrets.Enqueue("123456");
        using var ix = s.Home.Open(s.Unlock("yubikey"), s.Prompt);
        Assert.Equal(Recipient.KindYubiKey, ix.UnlockedBy.Kind);
    }

    [Fact]
    public void A_valid_recovery_key_that_is_not_enrolled_is_refused_by_name()
    {
        using var s = new Sandbox();
        s.Init(withYubiKey: false);
        using var stranger = RecoveryKey.Generate();
        var e = Assert.Throws<UnlockException>(() => s.Home.Open(s.Unlock("recovery", RecoveryKey.ToText(stranger)), s.Prompt).Dispose());
        Assert.Contains("not enrolled", e.Message);
    }

    [Fact]
    public void Adding_a_key_rewraps_without_touching_the_database()
    {
        using var s = new Sandbox();
        s.Zip(@"memory\memory-20260101-010101.zip", ("a.md", "rewrap bravo"));
        s.Init(withYubiKey: false);
        using (var ix = s.Home.Open(s.Unlock(), s.Prompt)) ix.CatchUp(s.Prompt);

        var before = SHA256.HashData(File.ReadAllBytes(s.Home.DatabasePath));
        using var second = RecoveryKey.Generate();
        using (var ix = s.Home.Open(s.Unlock("recovery"), s.Prompt))
            ix.AddRecipient(ix.NewRecoveryRecipient("Second recovery", second.ExportSubjectPublicKeyInfo()));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(s.Home.DatabasePath)));

        using var ix2 = s.Home.Open(s.Unlock("recovery", RecoveryKey.ToText(second)), s.Prompt, readOnly: true);
        Assert.Single(Searcher.Search(ix2.Db, new SearchQuery { Text = "bravo" }).Hits);
    }

    [Fact]
    public void Adding_a_YubiKey_later_rewraps_and_it_opens()
    {
        using var s = new Sandbox();
        s.Init(withYubiKey: false);
        var spki = s.Yubi.GenerateP256(0x82, PivTouch.Always);
        using (var ix = s.Home.Open(s.Unlock("recovery"), s.Prompt))
            ix.AddRecipient(ix.NewYubiKeyRecipient(spki, s.Yubi.Serial, 0x82));
        using var ix2 = s.Home.Open(s.Unlock("yubikey"), s.Prompt);
        Assert.Equal("82", ix2.UnlockedBy.PivSlot);
    }

    [Fact]
    public void Removing_a_key_stops_it_and_the_guards_hold()
    {
        using var s = new Sandbox();
        s.Init(withYubiKey: true);
        var recoveryId = s.Home.LoadKeyring().Recipients.Single(r => r.Kind == Recipient.KindRecovery).Id;
        var yubiId = s.Home.LoadKeyring().Recipients.Single(r => r.Kind == Recipient.KindYubiKey).Id;

        using (var ix = s.Home.Open(s.Unlock("recovery"), s.Prompt))
            Assert.Contains("unlocked with", Assert.Throws<UnlockException>(() => ix.RemoveRecipient(recoveryId)).Message);

        using (var ix = s.Home.Open(s.Unlock("yubikey"), s.Prompt))
        {
            ix.RemoveRecipient(recoveryId);
            Assert.Contains("only key", Assert.Throws<UnlockException>(() => ix.RemoveRecipient(yubiId)).Message);
        }
        Assert.Throws<UnlockException>(() => s.Home.Open(s.Unlock("recovery"), s.Prompt).Dispose());
        using var still = s.Home.Open(s.Unlock("yubikey"), s.Prompt);
        Assert.Single(still.Ring.Recipients);
    }

    [Fact]
    public void Rotating_replaces_the_database_key_and_every_enrolled_key_still_opens()
    {
        using var s = new Sandbox();
        s.Zip(@"memory\memory-20260101-010101.zip", ("a.md", "rotate charlie"));
        s.Init(withYubiKey: true);
        using (var ix = s.Home.Open(s.Unlock(), s.Prompt)) ix.CatchUp(s.Prompt);
        var (oldKey, _) = Unlocker.Unlock(s.Home.LoadKeyring(), s.Unlock("recovery"), s.Prompt);

        using (var ix = s.Home.Open(s.Unlock("yubikey"), s.Prompt)) ix.RotateDatabaseKey(s.Prompt);

        Assert.False(File.Exists(s.Home.RotatingKeyringPath));
        Assert.Throws<CryptographicException>(() => JJFlex.RecordIndex.Store.IndexDatabase.Open(s.Home.DatabasePath, oldKey).Dispose());
        foreach (var method in new[] { "yubikey", "recovery" })
        {
            using var ix = s.Home.Open(s.Unlock(method), s.Prompt, readOnly: true);
            Assert.Single(Searcher.Search(ix.Db, new SearchQuery { Text = "charlie" }).Hits);
        }
    }

    [Fact]
    public void Init_enrolls_nothing_when_the_recovery_key_is_not_confirmed()
    {
        using var s = new Sandbox();
        using var wrong = RecoveryKey.Generate();
        var e = Assert.Throws<UnlockException>(() => s.Home.Init(new IndexHome.InitOptions
        {
            RecordRoot = s.Record,
            CaptureRecovery = _ => { },
        }, s.Unlock(recovery: RecoveryKey.ToText(wrong)), s.Prompt));
        Assert.Contains("not confirmed", e.Message);
        Assert.False(s.Home.Exists);
    }

    [Fact]
    public void A_secret_file_is_refused_inside_a_repository_or_on_the_network()
    {
        var p = new TestPrompter();
        Assert.Throws<UnlockException>(() => RecoveryHandoff.Hand("x", @"file:" + Path.Combine(AppContext.BaseDirectory, "leak.txt"), p));
        Assert.Throws<UnlockException>(() => RecoveryHandoff.Hand("x", @"file:\\nas.example\share\leak.txt", p));
        Assert.Throws<UnlockException>(() => RecoveryHandoff.Hand("x", @"file:" + Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\JJFlex-private\leak.txt"), p));
        // On this machine the Desktop is inside OneDrive: "the Desktop" would upload it.
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive") ?? Environment.ExpandEnvironmentVariables(@"%USERPROFILE%\OneDrive");
        var e = Assert.Throws<UnlockException>(() => RecoveryHandoff.Hand("x", "file:" + Path.Combine(oneDrive, "Desktop", "leak-test-never-written.txt"), p));
        Assert.Contains("synced", e.Message);
        Assert.False(File.Exists(Path.Combine(oneDrive, "Desktop", "leak-test-never-written.txt")));
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "leak.txt")));
    }
}
