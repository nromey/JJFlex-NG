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
}
