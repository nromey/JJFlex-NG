using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace Radios;

// ── Microphone profiles (Track F, 2026-08-16) ──
//
// MICROPHONE-FIRST, on purpose. Flex's own profile model is per-radio state —
// "what settings do I use on this radio" — but the operator's actual question
// is "what does this microphone need", and that answer travels: one headset
// used across a Flex, a Kenwood and a borrowed rig is a case a per-radio model
// cannot express at all. So the profile is named for the microphone, carries
// the capture (PC-side) half once, and holds a per-radio binding for the
// radio-side half.
//
// The split follows capture-then-sculpt:
//
//   Stage one, PC capture — which device, the Windows input level, boost, the
//   gate. The radio cannot store any of this (a USB device identifier means
//   nothing to it), so it is OURS, in MicCaptureSettings.
//
//   Stage two, the radio's TX chain — mic gain, EQ, compander, processor,
//   bias. On a Flex the radio ALREADY stores these in its own mic profiles,
//   shared with every other client, so our half is a REFERENCE to one by name
//   (RadioProfileReference) — never a copy that would drift and fight other
//   clients over the same state. A Kenwood or Icom has no named-profile
//   concept, so for those the binding carries the actual values
//   (RadioTxValues). That discrimination is the shape of RadioTxSetup.
//
// The foreign-radio rule falls out for free: a radio with no binding gets
// NOTHING applied to it — the capture half is still yours, their TX chain is
// still theirs. And a referenced profile that does not exist on this radio is
// reported plainly, never guessed at and never created behind the operator's
// back (creation is offered in UI, explicitly, only).

/// <summary>
/// One microphone the operator uses: its name, its PC-capture settings, and
/// what each known radio should do about its TX chain.
/// </summary>
public class MicrophoneProfile
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Stage one — the PC capture half. Always ours.</summary>
    public MicCaptureSettings Capture { get; set; } = new();

    /// <summary>
    /// Stage two — one entry per radio this microphone has been set up on.
    /// Distinct element names per shape so the XML reads as what it is.
    /// </summary>
    [XmlArray("RadioSetups")]
    [XmlArrayItem("ProfileReference", typeof(RadioProfileReference))]
    [XmlArrayItem("StoredValues", typeof(RadioTxValues))]
    public List<RadioTxSetup> RadioSetups { get; set; } = new();

    /// <summary>The stage-two entry for a radio, or null when this microphone
    /// has never been set up on it.</summary>
    public RadioTxSetup? FindSetupFor(string radioId)
    {
        if (string.IsNullOrEmpty(radioId)) return null;
        return RadioSetups.FirstOrDefault(s =>
            string.Equals(s.RadioId, radioId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Replace or add the stage-two entry for one radio, leaving every other
    /// radio's binding alone — saving this microphone's setup on the 6300
    /// must not clobber what it means on the 8600. That per-radio surgery is
    /// the microphone-first model doing its job.
    /// </summary>
    public void SetSetupFor(RadioTxSetup setup)
    {
        if (setup == null || string.IsNullOrEmpty(setup.RadioId)) return;
        RadioSetups.RemoveAll(s =>
            string.Equals(s.RadioId, setup.RadioId, StringComparison.OrdinalIgnoreCase));
        RadioSetups.Add(setup);
    }

    /// <summary>
    /// Apply the radio half of this profile to a connected rig. The capture
    /// half is the caller's job (it owns the Windows endpoints); this method
    /// touches only the radio, and only when a binding for THIS radio exists.
    /// The result's message is spoken-ready and always names what happened —
    /// including, plainly, what did not.
    /// </summary>
    public MicProfileApplyResult ApplyRadioHalf(FlexBase? rig, string radioId)
    {
        if (rig == null)
        {
            return new MicProfileApplyResult
            {
                RadioHalfApplied = false,
                Message = "No radio is connected, so only the computer settings were applied.",
            };
        }

        var setup = FindSetupFor(radioId);
        if (setup == null)
        {
            // No binding: not our radio to touch. This is the guest-operator
            // rule working — a profile carried to somebody else's rig applies
            // your capture settings and leaves their TX chain alone.
            return new MicProfileApplyResult
            {
                RadioHalfApplied = false,
                Message = "This profile has no radio settings for this radio, so the radio was left alone.",
            };
        }

        switch (setup)
        {
            case RadioProfileReference reference:
                if (string.IsNullOrWhiteSpace(reference.ProfileName))
                {
                    return new MicProfileApplyResult
                    {
                        RadioHalfApplied = false,
                        Warning = true,
                        Message = "The stored radio reference is empty, so the radio was left alone.",
                    };
                }
                if (rig.SelectMicProfileIfPresent(reference.ProfileName))
                {
                    return new MicProfileApplyResult
                    {
                        RadioHalfApplied = true,
                        Message = $"Radio mic profile {reference.ProfileName} loaded.",
                    };
                }
                // Absent on this radio: say so plainly, no substitute, no
                // silent creation on someone's equipment.
                return new MicProfileApplyResult
                {
                    RadioHalfApplied = false,
                    Warning = true,
                    Message = $"This radio has no mic profile named {reference.ProfileName}, "
                        + "so the radio was left alone. The computer settings were still applied.",
                };

            case RadioTxValues stored:
                {
                    string notes = stored.Values.ApplyTo(rig);
                    return new MicProfileApplyResult
                    {
                        RadioHalfApplied = true,
                        Message = "Stored radio settings applied."
                            + (string.IsNullOrEmpty(notes) ? "" : " " + notes),
                    };
                }

            default:
                return new MicProfileApplyResult
                {
                    RadioHalfApplied = false,
                    Warning = true,
                    Message = "The stored radio settings are in a shape this version does not understand, "
                        + "so the radio was left alone.",
                };
        }
    }
}

/// <summary>
/// Stage one: what this microphone needs from the computer that captures it.
/// A radio cannot hold any of this.
/// </summary>
public class MicCaptureSettings
{
    /// <summary>The Windows capture device this profile was made with.
    /// Identity, not command — applying a profile never switches devices, it
    /// says when the current device is a different one.</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>Windows input level 0–100 for that device. -1 = not recorded;
    /// nothing is touched on apply.</summary>
    public int InputLevelPercent { get; set; } = -1;

    /// <summary>True when <see cref="BoostDb"/> holds a real reading — some
    /// devices expose no boost control at all, and "no boost control" must
    /// not deserialize the same as "boost recorded at zero".</summary>
    public bool BoostRecorded { get; set; }

    /// <summary>Windows microphone boost in dB, valid when
    /// <see cref="BoostRecorded"/>.</summary>
    public float BoostDb { get; set; }

    /// <summary>
    /// Noise gate settings for this microphone. Null until configured. The
    /// gate lives HERE and not in app-wide config on purpose: a gate tuned
    /// for a headset in a quiet room is wrong for a desk mic in a noisy one,
    /// and actively wrong when operating someone else's radio. The gate
    /// engine itself is transmit-conditioning work (Track I); this is its
    /// home, waiting.
    /// </summary>
    public NoiseGateSettings? Gate { get; set; }
}

/// <summary>
/// Noise gate parameters, per microphone. Defaults follow the plan's ratified
/// shape: fast attack so word starts are not clipped, a hold that bridges
/// gaps within a phrase, a bounded range rather than an infinite cut.
/// </summary>
public class NoiseGateSettings
{
    public bool Enabled { get; set; }
    public int ThresholdDb { get; set; } = -45;
    public int AttackMs { get; set; } = 8;      // 5–10 ms
    public int HoldMs { get; set; } = 100;      // 50–150 ms
    public int ReleaseMs { get; set; } = 200;   // 100–300 ms
    public int RangeDb { get; set; } = 25;      // 20–30 dB, never infinite
}

/// <summary>
/// Stage two, discriminated: what one radio should do about this microphone.
/// A REFERENCE on a Flex (the radio already stores its TX chain in its own
/// mic profiles — not ours to duplicate), ACTUAL VALUES everywhere else (a
/// TS-590 or IC-7300 has no named-profile concept and nothing else will hold
/// them).
/// </summary>
public abstract class RadioTxSetup
{
    /// <summary>Stable radio identifier — Flex serial today, whatever a
    /// future backend provides tomorrow (see RadioConfig's id rules).</summary>
    public string RadioId { get; set; } = "";

    /// <summary>Last known model string, informational, so speech can say
    /// "your 6600" instead of a serial.</summary>
    public string RadioModel { get; set; } = "";
}

/// <summary>The Flex shape: the NAME of a mic profile the radio itself owns.
/// Loading it is one command; the contents never live here, so there is
/// nothing to drift and nothing to fight other clients over.</summary>
public sealed class RadioProfileReference : RadioTxSetup
{
    public string ProfileName { get; set; } = "";
}

/// <summary>The everything-else shape: actual TX chain values, reusing the
/// preset model — the one vocabulary this codebase already has for a captured
/// TX chain.</summary>
public sealed class RadioTxValues : RadioTxSetup
{
    public AudioChainPreset Values { get; set; } = new();
}

/// <summary>What applying the radio half did, in a form the caller can speak.</summary>
public sealed class MicProfileApplyResult
{
    public bool RadioHalfApplied { get; set; }

    /// <summary>True when the outcome deserves the operator's attention even
    /// mid-flow — a referenced profile absent on this radio, a stored shape
    /// this version cannot read. "No radio connected" and "no binding for
    /// this radio" are expected states, not warnings.</summary>
    public bool Warning { get; set; }

    public string Message { get; set; } = "";
}

/// <summary>
/// The operator's microphone profiles, one file per operator — microphones
/// travel with the person, not the radio. Per-radio state lives INSIDE each
/// profile as its RadioSetups bindings.
/// File: {operatorName}_micProfiles.xml.
/// </summary>
[XmlRoot("MicrophoneProfiles")]
public class MicrophoneProfileStore
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Written-by schema version; 0 would mean a pre-versioning
    /// file, which for this store cannot exist — it was born versioned.</summary>
    [XmlAttribute("schemaVersion")]
    public int SchemaVersion { get; set; }

    public List<MicrophoneProfile> Profiles { get; set; } = new();

    public MicrophoneProfile? FindByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return Profiles.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public static MicrophoneProfileStore Load(string configDirectory, string operatorName)
        => Load(configDirectory, operatorName, out _);

    /// <summary>
    /// Load the operator's microphone profiles. Same honesty contract the
    /// preset store adopted (#49), from birth here: a missing file is a
    /// fresh start and an empty store is the honest answer (there are no
    /// built-in microphones to invent); a file that exists but cannot be
    /// read is the operator's setup, so it is moved aside — never left where
    /// the next save would overwrite it — and reported through
    /// <paramref name="corruptSidelinedPath"/> for the caller to speak.
    /// </summary>
    public static MicrophoneProfileStore Load(string configDirectory, string operatorName,
        out string? corruptSidelinedPath)
    {
        corruptSidelinedPath = null;
        var filePath = GetFilePath(configDirectory, operatorName);

        if (!File.Exists(filePath))
            return new MicrophoneProfileStore();

        try
        {
            using (var fs = File.OpenRead(filePath))
            {
                var serializer = new XmlSerializer(typeof(MicrophoneProfileStore));
                var store = (MicrophoneProfileStore?)serializer.Deserialize(fs);
                if (store != null)
                    return store;
            }
            corruptSidelinedPath = SidelineCorruptFile(filePath);
            return new MicrophoneProfileStore();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"MicrophoneProfileStore.Load failed: {ex.Message}");
            corruptSidelinedPath = SidelineCorruptFile(filePath);
            return new MicrophoneProfileStore();
        }
    }

    private static string SidelineCorruptFile(string filePath)
    {
        string sidelined = filePath + $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(filePath, sidelined);
            return sidelined;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"MicrophoneProfileStore: could not sideline corrupt file: {ex.Message}");
            return filePath;
        }
    }

    /// <summary>Save the store. Returns whether the file actually landed —
    /// callers announce saves, and a save that did not happen must never be
    /// announced as one.</summary>
    public bool Save(string configDirectory, string operatorName)
    {
        var filePath = GetFilePath(configDirectory, operatorName);
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            SchemaVersion = CurrentSchemaVersion;
            using var fs = File.Create(filePath);
            var serializer = new XmlSerializer(typeof(MicrophoneProfileStore));
            serializer.Serialize(fs, this);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"MicrophoneProfileStore.Save failed: {ex.Message}");
            return false;
        }
    }

    private static string GetFilePath(string configDirectory, string operatorName)
    {
        return Path.Combine(configDirectory, $"{operatorName}_micProfiles.xml");
    }
}
