using System.Diagnostics;
using JJPortaudio;
using JJTrace;
// NAudio 3.0 folded the separate NAudio.Wasapi.CoreAudioApi namespace (which
// held AudioVolumeLevel and the device-topology Part types) back into
// NAudio.CoreAudioApi. One using now covers both.
using NAudio.CoreAudioApi;

namespace JJFlexWpf;

/// <summary>
/// The Windows input level for one microphone — the same slider Windows Sound
/// settings shows — plus, where the driver exposes one, the Microphone Boost
/// hiding behind it. This is the stage-one gain control: Windows records the
/// device at this level before any radio ever sees a sample, so a capture
/// clipped here stays clipped no matter what the radio's TX chain does to it
/// afterwards.
/// </summary>
/// <remarks>
/// <para>
/// Mic Level Track, 2026-08-13. Radio audio devices are enumerated by
/// PortAudio, but PortAudio has no notion of a device's volume — that lives in
/// Windows Core Audio, reached here through NAudio (already referenced by this
/// project, and unused for this until now). So this class has one hard job:
/// deciding which Core Audio capture endpoint IS the PortAudio device the
/// operator selected.
/// </para>
/// <para>
/// <b>The matching rules are the picker's own identity rules, on purpose.</b>
/// The picker groups PortAudio endpoints by normalised name with one extra
/// allowance for MME's 31-character truncation (see
/// <c>Devices.BuildPickerList</c> and the <c>MmeNameLimit</c> comment in
/// Devices.cs). WASAPI and DirectSound report the endpoint's full Windows
/// friendly name — the same string <see cref="MMDevice.FriendlyName"/> holds —
/// so a selected row is matched by exact normalised name first, using every
/// name its device group goes by, and by unique 31-character prefix only when
/// the group offers nothing but a truncated MME name. "Unique" is load-bearing
/// both times: a name that matches two endpoints is a question, not an answer.
/// </para>
/// <para>
/// <b>When no confident match exists, <see cref="TryFind"/> returns null and
/// says why.</b> The caller disables the control and shows the reason. This is
/// not politeness: silently moving some other microphone's level is far worse
/// than offering nothing, because an operator who cannot see the screen has no
/// way to notice the wrong device moved. Every failure path here trades
/// capability for legibility, deliberately.
/// </para>
/// <para>
/// Microphone Boost is found by walking the endpoint's device topology toward
/// the jack and taking the first part named like a boost that carries a volume
/// control. That name is the driver's own string, which on non-English Windows
/// may be localised — in which case the boost simply is not found and the
/// control stays absent, which is safe. The classic mmsys.cpl Levels tab is
/// doing the same walk; the modern Settings app does not show boost at all,
/// which is exactly why a boost left at +30 dB is the classic cause of a
/// pinned 0 dBFS reading that nothing visible explains.
/// </para>
/// </remarks>
internal sealed class WindowsMicLevel : IDisposable
{
    /// <summary>
    /// PortAudio's fixed type id for MME (paMME). The one host API whose
    /// device names arrive truncated — see the MmeNameLimit comment in
    /// Devices.cs.
    /// </summary>
    private const int PaMmeTypeId = 2;

    /// <summary>
    /// Windows truncates MME device names to 31 characters (MAXPNAMELEN is 32
    /// including the terminator). Mirrors <c>Devices.MmeNameLimit</c>, which is
    /// private to a file another track owns; the normalised form can be one
    /// shorter when a trailing space was cut, so the prefix rule accepts 30.
    /// </summary>
    private const int MmeNameLimit = 31;

    private readonly MMDevice _device;
    private readonly AudioEndpointVolume _volume;
    private readonly AudioVolumeLevel? _boost;

    /// <summary>
    /// Windows' own name for the endpoint being controlled. Always shown to
    /// the operator next to the control — naming the device we are actually
    /// adjusting is the honesty guarantee the matching rules exist to earn.
    /// </summary>
    public string FriendlyName { get; }

    /// <summary>
    /// True when the selected row was one of the host-API default aliases
    /// ("Microsoft Sound Mapper", "Primary Sound Capture Driver"), which do
    /// not name a device — they follow the Windows default. The control then
    /// moves the current default capture device, and the note says which
    /// device that is right now.
    /// </summary>
    public bool FollowsWindowsDefault { get; }

    /// <summary>True when the driver exposes a Microphone Boost with a volume control.</summary>
    public bool HasBoost => _boost != null;

    /// <summary>Boost range, dB. Meaningful only when <see cref="HasBoost"/>.</summary>
    public float BoostMinDb { get; }
    public float BoostMaxDb { get; }
    /// <summary>Boost step, dB — drivers offer coarse steps like 10 dB. 0 means unreported.</summary>
    public float BoostStepDb { get; }

    /// <summary>
    /// Raised whenever the endpoint's level or mute changes — including our
    /// own writes echoing back. Fires on a COM worker thread; marshal to the
    /// UI thread before touching controls.
    /// </summary>
    public event Action? VolumeChanged;

    private WindowsMicLevel(MMDevice device, bool followsDefault)
    {
        _device = device;
        _volume = device.AudioEndpointVolume;
        FriendlyName = SafeFriendlyName(device);
        FollowsWindowsDefault = followsDefault;

        float min = 0f, max = 0f, step = 0f;
        _boost = FindBoost(device, ref min, ref max, ref step);
        BoostMinDb = min;
        BoostMaxDb = max;
        BoostStepDb = step;

        _volume.OnVolumeNotification += HandleNotification;

        Tracing.TraceLine("WindowsMicLevel: controlling \"" + FriendlyName + "\""
            + (followsDefault ? " (via Windows default)" : "")
            + (HasBoost ? $", boost {BoostMinDb:F0}..{BoostMaxDb:F0} dB step {BoostStepDb:F0}" : ", no boost part"),
            TraceLevel.Info);
    }

    /// <summary>
    /// The Windows input level, 0..100 — the same number Windows Sound
    /// settings shows as a percentage. Reads and writes may throw when the
    /// device has gone away; the caller owns turning that into a disabled
    /// control with a reason.
    /// </summary>
    public float Percent
    {
        get => _volume.MasterVolumeLevelScalar * 100f;
        set => _volume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 100f) / 100f;
    }

    /// <summary>
    /// The endpoint's Windows mute. A mute left on wins over every level
    /// slider, and it is the precise cause of the probe's "every sample is
    /// digital silence" reading.
    /// </summary>
    public bool Muted
    {
        get => _volume.Mute;
        set => _volume.Mute = value;
    }

    /// <summary>
    /// The Microphone Boost, dB. Writes snap to the driver's step so the
    /// value stored is always one the hardware actually has. 0 when the
    /// device has no boost.
    /// </summary>
    public float BoostDb
    {
        get => (_boost != null) ? _boost.GetLevel(0) : 0f;
        set
        {
            if (_boost == null) return;
            float v = Math.Clamp(value, BoostMinDb, BoostMaxDb);
            if (BoostStepDb > 0f)
                v = BoostMinDb + (float)Math.Round((v - BoostMinDb) / BoostStepDb) * BoostStepDb;
            _boost.SetLevelUniform(v);
        }
    }

    // ------------------------------------------------------------- matching

    /// <summary>
    /// Find the Core Audio capture endpoint for a selected picker row, or
    /// return null with a sentence saying why not — written to be shown to the
    /// operator as-is, next to the disabled control.
    /// </summary>
    public static WindowsMicLevel? TryFind(Devices.DeviceInfo? row, out string whyNot)
    {
        whyNot = "";

        if (row == null)
        {
            whyNot = "Choose a microphone above to adjust its Windows input level.";
            return null;
        }

        if (row.IsMissingSaved)
        {
            whyNot = row.Name + " is not connected, so its Windows input level cannot be adjusted.";
            return null;
        }

        CollectCandidateNames(row, out HashSet<string> fullNames, out HashSet<string> mmeNames);
        return MatchEndpoint(row.Name, fullNames, mmeNames, out whyNot);
    }

    /// <summary>
    /// Find the Core Audio capture endpoint for a device known only by its
    /// SAVED name — the audioDevices.xml selection — or return null with a
    /// sentence saying why not. For callers that must not enumerate PortAudio:
    /// the Audio Workshop reads the saved name from disk while a radio
    /// connection may be live, and a Pa_Initialize/Pa_Terminate cycle there
    /// risks disturbing the audio streams that connection depends on. This
    /// path touches Core Audio only, which carries no PortAudio state at all.
    /// </summary>
    /// <param name="deviceName">The saved device name, exactly as
    /// audioDevices.xml holds it.</param>
    /// <param name="savedHostApiTypeId">The saved PaHostApiTypeId, or -1 when
    /// the file predates 2026-08-07 and never recorded one.</param>
    /// <param name="whyNot">The operator-facing reason when no confident
    /// match exists.</param>
    /// <remarks>
    /// A saved name is one name, not a device group, so this has less to work
    /// with than <see cref="TryFind"/>: the group's other spellings are not
    /// available to widen the exact pass. Everything else survives unchanged
    /// because both entry points funnel into the same matcher — the exact
    /// pass, the unique-31-character-prefix pass for MME truncation, the
    /// loopback refusal, the Windows-default-alias path, and "exactly one
    /// endpoint" at every step. The saved host API type id says which pass
    /// the name is entitled to: an MME name may be truncated and gets the
    /// prefix pass; any other API's name is the full Windows friendly name
    /// and gets the exact pass only; an unknown vintage (-1) gets both,
    /// still gated on the name actually sitting at the truncation boundary,
    /// because a short name that failed the exact pass is simply absent, not
    /// truncated.
    /// </remarks>
    public static WindowsMicLevel? TryFindByName(string? deviceName, int savedHostApiTypeId, out string whyNot)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            whyNot = "No microphone has been chosen on this computer yet, so there is "
                + "no Windows input level to adjust.";
            return null;
        }

        var fullNames = new HashSet<string>(StringComparer.Ordinal);
        var mmeNames = new HashSet<string>(StringComparer.Ordinal);
        string norm = Normalize(deviceName);
        if (savedHostApiTypeId == PaMmeTypeId)
        {
            mmeNames.Add(norm);
        }
        else if (savedHostApiTypeId >= 0)
        {
            fullNames.Add(norm);
        }
        else
        {
            // Pre-2026-08-07 file: the host API was never written down, so
            // whether this name may be truncated is unknowable. Offer it to
            // both passes — the prefix pass's own length gate keeps ordinary
            // short names from ever being treated as truncations.
            fullNames.Add(norm);
            mmeNames.Add(norm);
        }

        return MatchEndpoint(deviceName, fullNames, mmeNames, out whyNot);
    }

    /// <summary>
    /// The one matcher both entry points funnel into, so the row-based and
    /// name-based paths cannot drift apart. Runs the alias, exact, and
    /// truncation passes over a single Core Audio snapshot and refuses —
    /// with an operator-facing sentence — everywhere confidence runs out.
    /// </summary>
    private static WindowsMicLevel? MatchEndpoint(
        string selectedName,
        HashSet<string> fullNames,
        HashSet<string> mmeNames,
        out string whyNot)
    {
        whyNot = "";

        // A WASAPI loopback is not a microphone — it is whatever the computer
        // is playing, offered back as a capture device. Its "level" belongs to
        // the playback device, and moving that from a microphone check would
        // be exactly the wrong-device adjustment this class refuses to make.
        if (Normalize(selectedName).EndsWith("[loopback]", StringComparison.Ordinal))
        {
            whyNot = "This device is a loopback of what the computer is playing, not a microphone, "
                + "so there is no input level to adjust here.";
            return null;
        }

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            // The sound-mapper aliases follow the Windows default device by
            // definition, so the default capture endpoint IS the device the
            // operator selected. The note names it, so nothing is silent.
            if (IsWindowsDefaultAlias(selectedName))
            {
                MMDevice def = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                return new WindowsMicLevel(def, followsDefault: true);
            }

            // One enumeration, held with normalised names, so the exact pass
            // and the truncation pass look at the same snapshot.
            var endpoints = new List<(MMDevice Dev, string Norm)>();
            foreach (MMDevice ep in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                endpoints.Add((ep, Normalize(SafeFriendlyName(ep))));

            // Exact pass: any name the device's group goes by. WASAPI and
            // DirectSound rows carry the full Windows friendly name, so for
            // any ordinarily-named device this is the pass that hits.
            var hits = new List<MMDevice>();
            foreach (var (dev, norm) in endpoints)
            {
                if (fullNames.Contains(norm) || mmeNames.Contains(norm))
                    AddDistinct(hits, dev);
            }

            // Truncation pass, only when exact found nothing and the group has
            // an MME-length name to work with: the same unique-prefix rule the
            // picker uses to fold a truncated MME row into its full-named
            // twin. A prefix matching several endpoints is genuinely
            // ambiguous, and a guess here moves someone's hardware.
            if (hits.Count == 0)
            {
                foreach (string mmeName in mmeNames)
                {
                    if (mmeName.Length < MmeNameLimit - 1) continue;
                    foreach (var (dev, norm) in endpoints)
                    {
                        if (norm.Length > mmeName.Length
                            && norm.StartsWith(mmeName, StringComparison.Ordinal))
                        {
                            AddDistinct(hits, dev);
                        }
                    }
                }
            }

            if (hits.Count == 1)
            {
                MMDevice matched = hits[0];
                DisposeAllExcept(endpoints, matched);
                return new WindowsMicLevel(matched, followsDefault: false);
            }

            DisposeAllExcept(endpoints, null);

            if (hits.Count == 0)
            {
                Tracing.TraceLine("WindowsMicLevel: no Core Audio endpoint matches \""
                    + selectedName + "\"", TraceLevel.Info);
                whyNot = "JJ Flex could not confidently match " + selectedName
                    + " to a Windows sound device, so the level control is turned off "
                    + "rather than risk moving the wrong microphone's level. "
                    + "Adjust it in Windows Sound settings instead.";
            }
            else
            {
                Tracing.TraceLine("WindowsMicLevel: " + hits.Count
                    + " Core Audio endpoints answer to \"" + selectedName
                    + "\"; refusing to guess", TraceLevel.Info);
                whyNot = "More than one Windows sound device answers to the name " + selectedName
                    + ", so JJ Flex cannot be sure which level to move and the control is turned off. "
                    + "Adjust it in Windows Sound settings instead.";
            }
            return null;
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("WindowsMicLevel: match attempt failed — " + ex.Message, TraceLevel.Error);
            whyNot = "Windows would not let JJ Flex read its sound devices, so the level control "
                + "is unavailable. Adjust the level in Windows Sound settings instead.";
            return null;
        }
    }

    /// <summary>
    /// Every name the selected row's physical device goes by, split into full
    /// names and possibly-truncated MME names. The group is the picker's own
    /// grouping — the same physical-device identity the operator chose by.
    /// </summary>
    private static void CollectCandidateNames(
        Devices.DeviceInfo row,
        out HashSet<string> fullNames,
        out HashSet<string> mmeNames)
    {
        fullNames = new HashSet<string>(StringComparer.Ordinal);
        mmeNames = new HashSet<string>(StringComparer.Ordinal);

        var members = new List<Devices.DeviceInfo>();
        Devices.DeviceInfo owner = row.GroupOwner ?? row;
        members.Add(owner);
        if (owner.Alternates != null) members.AddRange(owner.Alternates);
        if (!members.Contains(row)) members.Add(row);

        foreach (Devices.DeviceInfo m in members)
        {
            string norm = Normalize(m.Name);
            if (norm.Length == 0) continue;
            if (m.HostApiTypeId == PaMmeTypeId) mmeNames.Add(norm);
            else fullNames.Add(norm);
        }
    }

    /// <summary>
    /// The fixed names PortAudio's MME and DirectSound backends use for their
    /// follow-the-default aliases. Same list <c>Devices.ClassifyConnection</c>
    /// recognises when it labels these rows in the picker.
    /// </summary>
    private static bool IsWindowsDefaultAlias(string name)
    {
        string n = Normalize(name);
        return n == "microsoft sound mapper - input"
            || n == "primary sound capture driver";
    }

    /// <summary>
    /// Case- and whitespace-insensitive form of a device name. Mirrors
    /// <c>Devices.NormalizeName</c> (private to a file another track owns) so
    /// this class matches by the identity rules the picker already grouped by,
    /// rather than inventing a second scheme.
    /// </summary>
    private static string Normalize(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var sb = new System.Text.StringBuilder(name.Length);
        bool lastWasSpace = false;
        foreach (char c in name.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(char.ToLowerInvariant(c));
                lastWasSpace = false;
            }
        }
        return sb.ToString();
    }

    private static void AddDistinct(List<MMDevice> hits, MMDevice candidate)
    {
        foreach (MMDevice h in hits)
        {
            if (string.Equals(h.ID, candidate.ID, StringComparison.Ordinal)) return;
        }
        hits.Add(candidate);
    }

    private static void DisposeAllExcept(List<(MMDevice Dev, string Norm)> endpoints, MMDevice? keep)
    {
        foreach (var (dev, _) in endpoints)
        {
            if (keep != null && ReferenceEquals(dev, keep)) continue;
            try { dev.Dispose(); } catch { /* teardown must not throw */ }
        }
    }

    private static string SafeFriendlyName(MMDevice device)
    {
        // The friendly name comes from the endpoint's property store, which
        // odd virtual drivers have been known to leave unreadable. A device we
        // can control but not name is still worth offering — under a name that
        // is honest about not being one.
        try
        {
            string name = device.FriendlyName;
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        catch { }
        return "this microphone";
    }

    // ---------------------------------------------------------------- boost

    /// <summary>
    /// Walk the endpoint's topology toward the jack and return the first
    /// boost-named part that carries a volume control. Null — silently, with a
    /// trace — when there is none, when the walk fails, or when the driver
    /// names its parts in a language this cannot recognise. Absence is safe:
    /// the control simply is not offered.
    /// </summary>
    private static AudioVolumeLevel? FindBoost(MMDevice device,
        ref float minDb, ref float maxDb, ref float stepDb)
    {
        try
        {
            DeviceTopology topology = device.DeviceTopology;
            if (topology == null || topology.ConnectorCount == 0) return null;

            Connector endpointConnector = topology.GetConnector(0);
            if (endpointConnector == null || !endpointConnector.IsConnected) return null;

            // The endpoint's connector links to a connector on the audio
            // adapter. For a capture endpoint the signal flows OUT of that
            // adapter connector into the endpoint, so the driver's controls —
            // boost among them — sit on its INCOMING side, between the
            // physical jack and here.
            Part? start = endpointConnector.ConnectedTo?.Part;
            if (start == null) return null;

            return WalkForBoost(start, 0, ref minDb, ref maxDb, ref stepDb);
        }
        catch (Exception ex)
        {
            Tracing.TraceLine("WindowsMicLevel: boost walk failed — " + ex.Message, TraceLevel.Info);
            return null;
        }
    }

    private static AudioVolumeLevel? WalkForBoost(Part part, int depth,
        ref float minDb, ref float maxDb, ref float stepDb)
    {
        // Capture topologies are short linear chains (jack, boost, volume,
        // mute, endpoint). The bound is cycle insurance, not an expectation.
        if (depth > 8) return null;

        PartsList incoming;
        try { incoming = part.PartsIncoming; }
        catch { return null; }   // the chain ends here — E_NOTFOUND surfaces as a throw
        if (incoming == null) return null;

        for (uint i = 0; i < incoming.Count; i++)
        {
            Part p;
            try { p = incoming[i]; }
            catch { continue; }

            string name;
            try { name = p.Name ?? ""; }
            catch { name = ""; }

            if (name.IndexOf("boost", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                try
                {
                    AudioVolumeLevel? level = p.AudioVolumeLevel;
                    if (level != null)
                    {
                        level.GetLevelRange(0, out float mn, out float mx, out float st);
                        minDb = mn;
                        maxDb = mx;
                        stepDb = st;
                        return level;
                    }
                }
                catch
                {
                    // A boost-named part without a readable volume control is
                    // not a boost we can offer. Keep walking.
                }
            }

            AudioVolumeLevel? found = WalkForBoost(p, depth + 1, ref minDb, ref maxDb, ref stepDb);
            if (found != null) return found;
        }
        return null;
    }

    // ------------------------------------------------------------- lifetime

    private void HandleNotification(AudioVolumeNotificationData data) => VolumeChanged?.Invoke();

    public void Dispose()
    {
        try { _volume.OnVolumeNotification -= HandleNotification; } catch { }
        try { _volume.Dispose(); } catch { }
        try { _device.Dispose(); } catch { }
    }
}
