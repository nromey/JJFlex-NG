using JJFlexWpf.Dialogs;
using JJPortaudio;
using PortAudioSharp;
using Radios.Fixer;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// The fix actions' pure decisions — the parts of FixerFixActions that decide
/// rather than do. No sound card, no radio, no file, and above all NO WINDOW:
/// nothing in this class touches WPF.
/// </summary>
/// <remarks>
/// The properties under test, not the examples: a fix never moves a device
/// that is already where it belongs; a twin the engine cannot open is never
/// offered; an already-set state is reported as already set, never as a
/// change; a change that cannot be read back is reported as a failure, never
/// as silence; and what a setting became is named in the record.
/// </remarks>
public sealed class FixerFixDecisionTests
{
    // ---------------- helpers ----------------

    private static Devices.DeviceInfo Row(
        int api,
        string name = "Mic | Line | Instrument 1 (Test EVO8)",
        int channels = 2,
        Devices.DeviceTypes type = Devices.DeviceTypes.input)
    {
        var d = new Devices.DeviceInfo
        {
            Info = new PortAudio.PaDeviceInfo
            {
                name = name,
                maxInputChannels = type == Devices.DeviceTypes.input ? channels : 0,
                maxOutputChannels = type == Devices.DeviceTypes.output ? channels : 0,
            },
            Type = type,
            HostApiTypeId = api,
            HostApiName = Devices.NameOfHostApi(api),
        };
        d.GroupOwner = d;
        d.Alternates = new List<Devices.DeviceInfo>();
        return d;
    }

    /// <summary>Tie rows into one enumeration group the way BuildGroups does:
    /// first row is the owner, the rest are its alternates.</summary>
    private static void Group(params Devices.DeviceInfo[] rows)
    {
        Devices.DeviceInfo owner = rows[0];
        var alternates = new List<Devices.DeviceInfo>();
        foreach (Devices.DeviceInfo r in rows)
        {
            r.GroupOwner = owner;
            if (!ReferenceEquals(r, owner)) alternates.Add(r);
        }
        owner.Alternates = alternates;
    }

    // ---------------- OnApi: the twin decision ----------------

    [Fact]
    public void OnApi_NothingResolvedMeansNoTwin()
    {
        Assert.Null(FixerFixDecisions.OnApi(null, Devices.WasapiTypeId));
    }

    [Fact]
    public void OnApi_DeviceAlreadyOnTargetIsReturnedItself_NeverMoved()
    {
        // Even with a second target-API row in the group, the device that is
        // already where it belongs stays exactly the row it is.
        Devices.DeviceInfo wasapi = Row(Devices.WasapiTypeId);
        Devices.DeviceInfo mme = Row(Devices.MmeTypeId);
        Group(wasapi, mme);

        Assert.Same(wasapi, FixerFixDecisions.OnApi(wasapi, Devices.WasapiTypeId));
    }

    [Fact]
    public void OnApi_TwinFoundWhenTheGroupOwnerIsOnTheTargetApi()
    {
        Devices.DeviceInfo wasapi = Row(Devices.WasapiTypeId);
        Devices.DeviceInfo mme = Row(Devices.MmeTypeId);
        Group(wasapi, mme);

        Assert.Same(wasapi, FixerFixDecisions.OnApi(mme, Devices.WasapiTypeId));
    }

    [Fact]
    public void OnApi_TwinFoundAmongTheAlternates()
    {
        Devices.DeviceInfo mme = Row(Devices.MmeTypeId);
        Devices.DeviceInfo directSound = Row(Devices.DirectSoundTypeId);
        Devices.DeviceInfo wasapi = Row(Devices.WasapiTypeId);
        Group(mme, directSound, wasapi);

        Assert.Same(wasapi, FixerFixDecisions.OnApi(mme, Devices.WasapiTypeId));
    }

    [Fact]
    public void OnApi_NoEndpointOnTheTargetApiMeansNoTwin()
    {
        Devices.DeviceInfo mme = Row(Devices.MmeTypeId);
        Devices.DeviceInfo directSound = Row(Devices.DirectSoundTypeId);
        Group(mme, directSound);

        Assert.Null(FixerFixDecisions.OnApi(mme, Devices.WasapiTypeId));
    }

    [Fact]
    public void OnApi_AnUnopenableTwinIsNeverOffered()
    {
        // A target-API endpoint the engine cannot open (no channels in its
        // direction) must not be the answer — that would trade a misreporting
        // microphone for a dead one.
        Devices.DeviceInfo mme = Row(Devices.MmeTypeId);
        Devices.DeviceInfo deadWasapi = Row(Devices.WasapiTypeId, channels: 0);
        Group(mme, deadWasapi);

        Assert.Null(FixerFixDecisions.OnApi(mme, Devices.WasapiTypeId));
    }

    [Fact]
    public void OnApi_AMissingSavedStandInIsNeverOffered()
    {
        Devices.DeviceInfo mme = Row(Devices.MmeTypeId);
        Devices.DeviceInfo ghost = Row(Devices.WasapiTypeId);
        ghost.IsMissingSaved = true;
        Group(mme, ghost);

        Assert.Null(FixerFixDecisions.OnApi(mme, Devices.WasapiTypeId));
    }

    // ---------------- AlreadyOnApi: the press-twice decision ----------------

    [Fact]
    public void AlreadyOnApi_TrueOnlyWhenSelectorAndEveryResolvedDeviceAgree()
    {
        int target = Devices.WasapiTypeId;

        // Selector and both devices on target: already there.
        Assert.True(FixerFixDecisions.AlreadyOnApi(target, target, target, target));

        // A device that does not exist cannot argue against.
        Assert.True(FixerFixDecisions.AlreadyOnApi(target, null, null, target));

        // The selector elsewhere is not "already", whatever the devices say.
        Assert.False(FixerFixDecisions.AlreadyOnApi(Devices.MmeTypeId, target, target, target));

        // Either device elsewhere is not "already".
        Assert.False(FixerFixDecisions.AlreadyOnApi(target, Devices.MmeTypeId, target, target));
        Assert.False(FixerFixDecisions.AlreadyOnApi(target, target, Devices.MmeTypeId, target));
    }

    // ---------------- PcAudioOutcome ----------------

    [Fact]
    public void PcAudio_AlreadyOnSucceedsWithoutClaimingAChange()
    {
        FixerFixOutcome already = FixerFixDecisions.PcAudioOutcome(wasOn: true, nowOn: true);
        FixerFixOutcome turnedOn = FixerFixDecisions.PcAudioOutcome(wasOn: false, nowOn: true);

        Assert.True(already.Succeeded);
        Assert.True(turnedOn.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(already.WhatItBecame));
        Assert.False(string.IsNullOrWhiteSpace(turnedOn.WhatItBecame));

        // The record must distinguish "it was already so" from "it changed" —
        // an operator deciding whether to undo needs to know which happened.
        Assert.NotEqual(already.WhatItBecame, turnedOn.WhatItBecame);
    }

    [Fact]
    public void PcAudio_AReadBackThatStayedOffIsAFailureNeverSilence()
    {
        FixerFixOutcome outcome = FixerFixDecisions.PcAudioOutcome(wasOn: false, nowOn: false);

        Assert.False(outcome.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(outcome.WhatItBecame));
    }

    // ---------------- MicProfileOutcome ----------------

    [Fact]
    public void MicProfile_AConfirmedSelectionNamesTheProfileInTheRecord()
    {
        FixerFixOutcome outcome = FixerFixDecisions.MicProfileOutcome("Default", "Default");

        Assert.True(outcome.Succeeded);
        Assert.Contains("Default", outcome.WhatItBecame, StringComparison.Ordinal);
    }

    [Fact]
    public void MicProfile_AnUnconfirmedSelectionIsAFailureThatSaysWhy()
    {
        FixerFixOutcome outcome = FixerFixDecisions.MicProfileOutcome("Default", "");

        Assert.False(outcome.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(outcome.WhatItBecame));
    }

    [Fact]
    public void MicProfile_ADifferentProfileIsReportedAsWhatItActuallyIs()
    {
        FixerFixOutcome outcome = FixerFixDecisions.MicProfileOutcome("Default", "SSB Rag Chew");

        Assert.True(outcome.Succeeded);
        // The record names what the setting became, not what was intended.
        Assert.Contains("SSB Rag Chew", outcome.WhatItBecame, StringComparison.Ordinal);
    }
}
