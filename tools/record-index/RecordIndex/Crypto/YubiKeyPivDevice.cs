using System.Text;
using Yubico.YubiKey;
using Yubico.YubiKey.Cryptography;
using Yubico.YubiKey.Piv;

namespace JJFlex.RecordIndex.Crypto;

/// <summary>
/// The real YubiKey, through Yubico's own .NET SDK (Yubico.YubiKey, Apache 2.0).
/// PIV is used rather than FIDO2 or OpenPGP because PIV key agreement is exactly
/// "ECDH with a key that cannot leave the device", it needs no extra software
/// installed, and slot 9D exists for this purpose.
///
/// NOT EXERCISED BY ANY AUTOMATED TEST, by rule: nothing automated touches the
/// operator's YubiKey. Everything above this class is tested against the software
/// stand-in, which implements the same <see cref="IPivDevice"/>. This class is a
/// thin shim and its first real run is Noel's enrollment.
/// </summary>
public sealed class YubiKeyPivDevice : IPivDevice
{
    private readonly IYubiKeyDevice _device;
    private readonly PivSession _session;
    private readonly IPrompter _prompt;

    private YubiKeyPivDevice(IYubiKeyDevice device, IPrompter prompt)
    {
        _device = device;
        _prompt = prompt;
        _session = new PivSession(device) { KeyCollector = Collect };
    }

    /// <summary>Finds exactly one YubiKey, or the one with the given serial.</summary>
    public static YubiKeyPivDevice Open(IPrompter prompt, int? serial = null)
    {
        var all = YubiKeyDevice.FindAll().ToList();
        if (serial is int s) all = all.Where(d => d.SerialNumber == s).ToList();
        if (all.Count == 0)
            throw new UnlockException(serial is int n
                ? $"The YubiKey with serial number {n} is not plugged in."
                : "No YubiKey is plugged in.");
        if (all.Count > 1)
            throw new UnlockException("More than one YubiKey is plugged in. Unplug all but one, or name the serial number.");
        return new YubiKeyPivDevice(all[0], prompt);
    }

    public int? Serial => _device.SerialNumber;

    public string Firmware => _device.FirmwareVersion.ToString();

    public byte[]? GetPublicKey(byte slot)
    {
        if (_device.FirmwareVersion >= new FirmwareVersion(5, 3, 0))
        {
            var meta = _session.GetMetadata(slot);
            if (meta.KeyStatus == PivKeyStatus.Unknown || meta.Algorithm == PivAlgorithm.None) return null;
            if (meta.PublicKeyParameters is ECPublicKey ec && ec.KeyType == KeyType.ECP256) return ec.ExportSubjectPublicKeyInfo();
            throw new UnlockException($"Slot {PivSlots.Name(slot)} holds a key that is not P-256 ({meta.Algorithm}). Choose another slot.");
        }
        // Older firmware cannot report a slot's key; a certificate is the only clue.
        try
        {
            var cert = _session.GetCertificate(slot);
            using var ecdh = cert.GetECDiffieHellmanPublicKey();
            return ecdh?.ExportSubjectPublicKeyInfo();
        }
        catch (InvalidOperationException) { return null; }
    }

    public byte[] GenerateP256(byte slot, PivTouch touch)
    {
        var policy = touch switch
        {
            PivTouch.Never => PivTouchPolicy.Never,
            PivTouch.Cached => PivTouchPolicy.Cached,
            _ => PivTouchPolicy.Always,
        };
        var pub = _session.GenerateKeyPair(slot, KeyType.ECP256, PivPinPolicy.Once, policy);
        return pub is ECPublicKey ec
            ? ec.ExportSubjectPublicKeyInfo()
            : throw new UnlockException("The YubiKey returned an unexpected key type.");
    }

    public byte[] KeyAgree(byte slot, byte[] peerSpki)
    {
        var peer = ECPublicKey.CreateFromSubjectPublicKeyInfo(peerSpki);
        return _session.KeyAgree(slot, peer);
    }

    private bool Collect(KeyEntryData data)
    {
        switch (data.Request)
        {
            case KeyEntryRequest.Release:
                return true;
            case KeyEntryRequest.TouchRequest:
                _prompt.Say("Touch your YubiKey now.");
                return true;
            case KeyEntryRequest.VerifyPivPin:
            {
                if (data.IsRetry)
                    _prompt.Say(data.RetriesRemaining is int r
                        ? $"That PIN was not accepted. {r} tries remain before the PIV PIN locks."
                        : "That PIN was not accepted.");
                var pin = _prompt.Secret("YubiKey PIV PIN");
                if (pin.Length == 0) return false;
                data.SubmitValue(Encoding.UTF8.GetBytes(pin));
                return true;
            }
            case KeyEntryRequest.AuthenticatePivManagementKey:
            {
                if (data.IsRetry) _prompt.Say("That management key was not accepted.");
                var hex = _prompt.Secret("YubiKey PIV management key, in hexadecimal. Press Enter alone to use the factory default");
                byte[] key = hex.Length == 0
                    ? Convert.FromHexString("010203040506070801020304050607080102030405060708")
                    : Convert.FromHexString(hex.Replace(" ", ""));
                data.SubmitValue(key);
                return true;
            }
            default:
                return false;
        }
    }

    public void Dispose() => _session.Dispose();
}
