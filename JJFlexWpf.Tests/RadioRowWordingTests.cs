using System.Linq;
using JJFlexWpf.Dialogs;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// What a row in the radio picker SAYS. Pure text off a plain object — no
/// window, no dispatcher, nothing that touches WPF, in the same spirit as
/// <see cref="FixerFixDecisionTests"/>.
/// </summary>
/// <remarks>
/// <para>
/// This exists because row wording is the project's dominant defect class in
/// miniature: a sentence that was true when written and quietly stopped being
/// true. The row text has already been wrong in three separate ways — asserting
/// "offline" during the settle window before 2026-08-17, repeating "last seen"
/// twice in one sentence, and passing the literal sentinel "Unknown" through as
/// a radio's name. None of those broke a build.
/// </para>
/// <para>
/// So the assertions below are mostly about PROPERTIES rather than examples: no
/// row claims to know the radio is off, an unsettled row commits to nothing, and
/// a row we are ourselves in the middle of re-checking says so.
/// </para>
/// </remarks>
public sealed class RadioRowWordingTests
{
    private static RadioListItem Row(string name = "6300inshack", string model = "FLEX-6300") =>
        new()
        {
            Serial = "1315-4176-6300-7236",
            Name = name,
            ModelName = model,
            DiscoverySettled = true,
        };

    // ------------------------------------------------------------------
    // The honest-wording rule (#254, #259)
    // ------------------------------------------------------------------

    /// <summary>
    /// The regression guard that matters more than any single example.
    ///
    /// <para>"Offline" asserts something about the RADIO. All this app ever
    /// knows is whether it has heard from one. Don's rig was almost certainly
    /// powered up and on the air while a row here called it offline; we had
    /// simply stopped asking. Same class as the Fixer reporting "not run" when
    /// it means "not measured".</para>
    /// </summary>
    [Fact]
    public void NoRowStringClaimsToKnowTheRadioIsOff()
    {
        // Load on demand, then PROVE the sweep found something. A store that
        // failed to load returns an empty key set, and this test would then
        // report a clean bill of health having examined nothing — which is the
        // exact shape of failure it exists to catch.
        Lexicon.Get("connect.row.checking");
        var rowKeys = Lexicon.Keys
            .Where(k => k.StartsWith("connect.row.", System.StringComparison.Ordinal))
            .ToList();
        Assert.True(rowKeys.Count > 10,
            "Only " + rowKeys.Count + " row keys were loaded, so this check verified nothing.");

        // Every row-scoped key, straight out of the store, so a future author
        // who reinstates the word is caught here rather than by an operator.
        var offenders = rowKeys
            .Where(k => Lexicon.Get(k).Contains("offline", System.StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Row text must describe OUR last contact, never assert the radio's state. "
            + "Offending keys: " + string.Join(", ", offenders));
    }

    [Fact]
    public void ARosterRowWithAKnownAgeSaysWhenWeLastHeardFromIt()
    {
        var r = Row();
        r.LastSeenText = "last seen 3 days ago";

        Assert.Contains("last heard from 3 days ago", r.WhereText);
        Assert.DoesNotContain("offline", r.WhereText, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARosterRowWithNoAgeSaysOnlyThatWeHaveNotHeardFromIt()
    {
        var r = Row();
        r.LastSeenText = "";

        Assert.Contains("not heard from", r.WhereText);
        Assert.DoesNotContain("offline", r.WhereText, System.StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // States that are not an absence
    // ------------------------------------------------------------------

    [Fact]
    public void ARowThatHasNotAnsweredYetCommitsToNothing()
    {
        var r = Row();
        r.DiscoverySettled = false;

        Assert.Equal(Lexicon.Get("connect.row.checking"), r.WhereText);
    }

    /// <summary>
    /// A refresh WE started is not the radio going quiet. Tearing down a
    /// SmartLink session drops every WAN radio by design; reporting that as an
    /// absence is reporting our own silence as news about the operator's
    /// equipment (2026-08-25 capture).
    /// </summary>
    [Fact]
    public void ARowWeAreRecheckingSaysSoRatherThanReportingAnAbsence()
    {
        var r = Row();
        r.LastSeenText = "last seen just now";
        r.RefreshInFlight = true;

        Assert.Equal(Lexicon.Get("connect.row.rechecking"), r.WhereText);
    }

    /// <summary>
    /// The third "we have not asked yet" (#340), and the one that cost an
    /// evening. Don's 6300 is visible only to Don's SmartLink account. With no
    /// session held for that account, nothing has listened for it — so a row
    /// saying "not heard from" is passing a verdict on equipment it never
    /// dialled. Noel heard exactly that on 2026-08-28, seconds before picking
    /// the same row and connecting to the radio successfully.
    /// </summary>
    [Fact]
    public void ARowWhoseAccountIsNotSignedInSaysThatRatherThanReportingAnAbsence()
    {
        var r = Row();
        r.LastSeenText = "last seen 2 hours ago";
        r.LastSeenViaAccount = "dbreda@example.com";
        r.ForeignAccount = true;
        r.BoundAccountHasLiveSession = false;

        Assert.DoesNotContain("not heard from", r.WhereText, System.StringComparison.OrdinalIgnoreCase);
        // And it names the account, because that is both the reason and the
        // thing Enter is about to switch to.
        Assert.Contains("dbreda@example.com", r.WhereText);
    }

    /// <summary>
    /// The positive control for the test above. A state that means "we have not
    /// asked" must stop applying the moment we HAVE asked, or it becomes a
    /// permanent excuse and the roster can never report a real absence again.
    /// </summary>
    [Fact]
    public void OnceTheAccountIsSignedInTheSameRowReportsWhatWeHeard()
    {
        var r = Row();
        r.LastSeenText = "last seen 2 hours ago";
        r.LastSeenViaAccount = "dbreda@example.com";
        r.ForeignAccount = true;
        r.BoundAccountHasLiveSession = true;

        Assert.Contains("not heard from", r.WhereText, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The state between those two (#382). Opening the picker now dials the
    /// accounts the roster depends on, which takes a second or two — long
    /// enough for a screen reader to read the whole row. Without a word for
    /// that window the row renders "not signed in" on its way to a verdict:
    /// true of the instant, and a flicker through the exact sentence Noel read
    /// on 2026-08-29 and called wrong.
    /// </summary>
    [Fact]
    public void ARowWhoseAccountIsBeingDialledSaysThatRatherThanNotSignedIn()
    {
        var r = Row();
        r.LastSeenText = "last seen 2 hours ago";
        r.LastSeenViaAccount = "dbreda@example.com";
        r.ForeignAccount = true;
        r.BoundAccountHasLiveSession = false;
        r.BoundAccountSessionComingUp = true;

        Assert.Equal(
            Lexicon.Get("connect.row.account_signing_in", ("account", "dbreda@example.com")),
            r.WhereText);
        Assert.DoesNotContain("not signed in", r.WhereText, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not heard from", r.WhereText, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The positive control, and the bullet #382 was most at risk of papering
    /// over. An account whose sign-in was CLEARED is never dialled — the
    /// presence layer skips it rather than churning against it — so the coming-up
    /// flag is never set for it and the row keeps saying "not signed in". That
    /// is the one row here describing a real state the operator can act on, and
    /// it also tells them what Enter is about to do.
    /// </summary>
    [Fact]
    public void AClearedAccountIsNotDialledSoItsRowStillSaysNotSignedIn()
    {
        var r = Row();
        r.LastSeenText = "last seen 2 hours ago";
        r.LastSeenViaAccount = "dbreda@example.com";
        r.ForeignAccount = true;
        r.BoundAccountHasLiveSession = false;
        r.BoundAccountSessionComingUp = false;

        Assert.Contains("not signed in", r.WhereText, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The other positive control: the coming-up state must not outlive the
    /// session it describes. Once the account is live the row reports what the
    /// account actually said, or a permanently-connecting row would be a
    /// spinner that never stops — worse than the sentence it replaced, because
    /// nothing about it suggests Enter has anything to do.
    /// </summary>
    [Fact]
    public void OnceTheSessionIsUpTheComingUpWordingStopsApplying()
    {
        var r = Row();
        r.LastSeenText = "last seen 2 hours ago";
        r.LastSeenViaAccount = "dbreda@example.com";
        r.ForeignAccount = true;
        r.BoundAccountSessionComingUp = true;
        r.BoundAccountHasLiveSession = true;

        Assert.DoesNotContain("signing in", r.WhereText, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not heard from", r.WhereText, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And it stays as narrow as the state it sits in front of. A LAN row with
    /// no account behind it WAS asked, so nothing about a SmartLink session
    /// coming up somewhere else may speak for it.
    /// </summary>
    [Fact]
    public void ALanRowIsUntouchedByASessionComingUpElsewhere()
    {
        var r = Row();
        r.LastSeenText = "last seen 2 hours ago";
        r.BoundAccountHasLiveSession = false;
        r.BoundAccountSessionComingUp = true;

        Assert.Contains("last heard from 2 hours ago", r.WhereText);
        Assert.DoesNotContain("signing in", r.WhereText, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And it stays narrow. A radio last seen on the LAN with no account behind
    /// it WAS asked — discovery settled and it did not answer — so an unsigned
    /// SmartLink account is no excuse for it.
    /// </summary>
    [Fact]
    public void ALanRowKeepsItsAbsenceWhenNoAccountIsInvolved()
    {
        var r = Row();
        r.LastSeenText = "last seen 2 hours ago";
        r.BoundAccountHasLiveSession = false;

        Assert.Contains("last heard from 2 hours ago", r.WhereText);
    }

    [Fact]
    public void ALiveRowSaysWhereItIs()
    {
        var r = Row();
        r.LanAvailable = true;
        Assert.Equal(Lexicon.Get("connect.row.local"), r.WhereText);

        var w = Row();
        w.WanAvailable = true;
        Assert.Equal(Lexicon.Get("connect.row.remote"), w.WhereText);
    }

    // ------------------------------------------------------------------
    // Telling two identical radios apart
    // ------------------------------------------------------------------

    /// <summary>
    /// Noel read a DIFFERENT operator's FLEX-6300 as a stale copy of Don's on
    /// 2026-08-25, because both rows render identically. The row has always
    /// known its owning account and simply never said so.
    /// </summary>
    [Fact]
    public void TwoRowsSharingAModelNameTheirOwningAccount()
    {
        var r = Row();
        r.LanAvailable = true;
        r.LastSeenViaAccount = "dbreda@example.com";
        r.ModelIsAmbiguous = true;

        Assert.Contains("dbreda@example.com", r.DisplayText);
    }

    /// <summary>
    /// And only then. Naming the account on every row would spend a second of
    /// speech per arrow key answering a question nobody asks on a one-radio
    /// list — the friction tax this app exists to refuse.
    /// </summary>
    [Fact]
    public void AnUnambiguousRowDoesNotSpendSpeechOnTheAccount()
    {
        var r = Row();
        r.LanAvailable = true;
        r.LastSeenViaAccount = "dbreda@example.com";
        r.ModelIsAmbiguous = false;

        Assert.DoesNotContain("dbreda@example.com", r.DisplayText);
    }

    /// <summary>
    /// The operator's chosen account outranks the observation, here as
    /// everywhere else — BoundAccount is the one rule for that.
    /// </summary>
    [Fact]
    public void ThePreferredAccountIsTheOneNamed()
    {
        var r = Row();
        r.LanAvailable = true;
        r.LastSeenViaAccount = "observed@example.com";
        r.PreferredAccount = "chosen@example.com";
        r.ModelIsAmbiguous = true;

        Assert.Contains("chosen@example.com", r.DisplayText);
        Assert.DoesNotContain("observed@example.com", r.DisplayText);
    }

    // ------------------------------------------------------------------
    // The live sentence (#391, #394 — Noel's spec, 2026-08-30)
    // ------------------------------------------------------------------

    /// <summary>
    /// Every live row states its client count, zero included. Silence used to
    /// be the zero case, and silence is indistinguishable from a feature that
    /// is not working — Noel, at his tester's radio, deciding whether to
    /// transmit: "I'm not seeing that Don's connected or no one's connected."
    /// </summary>
    [Fact]
    public void ALiveRowAlwaysStatesItsClientCountZeroIncluded()
    {
        var r = Row();
        r.LanAvailable = true;
        // A delivered list is what licenses the zero — see the test below for
        // the row whose source never spoke.
        r.OccupancyKnown = true;

        Assert.EndsWith("online with 0 connected clients", r.DisplayText);
    }

    /// <summary>
    /// And the zero is LICENSED, never defaulted. The 2026-08-30 field trace
    /// caught the state this guards: presence pushes dropped with no intake,
    /// the row live off the WAN bank's availability answer, its stations list
    /// still the constructor's empty default — while Don sat on the radio. A
    /// row in that state must admit it does not know, because "online with 0
    /// connected clients" would be a confident false claim in the exact
    /// sentence read before keying somebody else's transmitter.
    /// </summary>
    [Fact]
    public void ALiveRowWhoseSourceNeverSpokeSaysUnknownNotZero()
    {
        var r = Row();
        r.WanAvailable = true;

        Assert.EndsWith("online, client count unknown", r.DisplayText);
        Assert.DoesNotContain("0 connected", r.DisplayText);
    }

    /// <summary>
    /// And a row that cannot hear the radio claims no count at all — a zero
    /// it cannot know would be a claim, not a report. The clause's absence
    /// now means exactly one thing: not live.
    /// </summary>
    [Fact]
    public void ARowThatCannotHearTheRadioClaimsNoCount()
    {
        var r = Row();
        r.LastSeenText = "last seen 3 days ago";

        Assert.DoesNotContain("connected client", r.DisplayText,
            System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Dual-homed names BOTH paths and no choice. Which leg gets tried
    /// belongs to the path combo and the connect announcement, not to a
    /// clause read on every arrow keypress.
    /// </summary>
    [Fact]
    public void ADualHomedRowNamesBothPathsAndNoChoice()
    {
        var r = Row();
        r.LanAvailable = true;
        r.WanAvailable = true;

        Assert.Equal(Lexicon.Get("connect.row.dual"), r.WhereText);
        Assert.DoesNotContain("using", r.WhereText, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AForeignWanRowNamesTheAccountItsConnectWouldBrokerThrough()
    {
        var r = Row();
        r.WanAvailable = true;
        r.BrokerAccount = "dbreda@example.com";

        Assert.Equal(
            Lexicon.Get("connect.row.remote_via", ("account", "dbreda@example.com")),
            r.WhereText);
    }

    /// <summary>
    /// A radio arriving on the operator's own account names no account — the
    /// refresh pass stamps BrokerAccount empty for it (#401's ruling: name
    /// the account only when it is NOT the one in play).
    /// </summary>
    [Fact]
    public void AWanRowOnTheAccountInPlayNamesNoAccount()
    {
        var r = Row();
        r.WanAvailable = true;

        Assert.Equal(Lexicon.Get("connect.row.remote"), r.WhereText);
        Assert.DoesNotContain("@", r.WhereText);
    }

    /// <summary>
    /// One account, said once. When the where-clause already names the
    /// account ("on SmartLink via dbreda@..."), the model-disambiguation
    /// suffix stands down rather than naming it a second time in the same
    /// sentence.
    /// </summary>
    [Fact]
    public void AnAmbiguousRowWhoseWhereClauseNamesTheAccountSaysItOnce()
    {
        var r = Row();
        r.WanAvailable = true;
        r.LastSeenViaAccount = "dbreda@example.com";
        r.BrokerAccount = "dbreda@example.com";
        r.ModelIsAmbiguous = true;

        var text = r.DisplayText;
        int first = text.IndexOf("dbreda@example.com", System.StringComparison.OrdinalIgnoreCase);
        int last = text.LastIndexOf("dbreda@example.com", System.StringComparison.OrdinalIgnoreCase);
        Assert.True(first >= 0, "The account vanished entirely: " + text);
        Assert.Equal(first, last);
    }
}
