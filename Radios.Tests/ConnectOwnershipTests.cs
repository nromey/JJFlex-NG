using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Radios;
using Radios.StationConnect;
using Xunit;

namespace Radios.Tests;

/// <summary>
/// Executes the production selector funnel, timer, account-resume path and Ask
/// bodies without loading JJFlexWpf or creating a window. Only the UI and
/// discovery endpoints are substitutes; config and suppression use the real
/// stores under the test suite's temporary settings root. This verifies ordering
/// and persistence, not focus, speech, or what firmware does after connection.
/// </summary>
[Collection(RadioConfigStaticsCollection.Name)]
public sealed class ConnectOwnershipTests : IDisposable
{
    private readonly RadioConfigStaticsScope _scope = new(nameof(ConnectOwnershipTests));
    private const string _serial = "0638-0000-8600-0001";
    private const string OtherSerial = "0638-0000-8600-0002";
    private static readonly Lazy<Type> SelectorType = new(CompileSelector);

    public void Dispose()
    {
        AdvisorySuppression.Unsuppress(AdvisoryKeys.RadioOwnership(_serial).Value);
        AdvisorySuppression.Unsuppress(AdvisoryKeys.RadioOwnership(OtherSerial).Value);
        _scope.Dispose();
    }

    private dynamic Selector(RadioOwnership? answer = null, bool silence = false)
    {
        dynamic selector = Activator.CreateInstance(SelectorType.Value, _serial)!;
        selector.SetAnswer(answer, silence);
        return selector;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FirstManualOrAutomaticConnectHasBothAnswersBeforeCompletion(bool automatic)
    {
        Assert.Null(ProfileStewardship.PreAnswerForKnownRadio(
            RadioOwnership.Mine, false, ProfileGuestIntent.NotAnswered));
        dynamic s = Selector(RadioOwnership.Mine);
        if (automatic) s.AutoConnect(); else s.Connect();
        Assert.Equal(1, (int)s.Asks);
        Assert.Equal(RadioOwnership.Mine, (RadioOwnership)s.OwnershipAtConnect);
        Assert.Equal(ProfileGuestIntent.LoadMineAndPutBack, (ProfileGuestIntent)s.IntentAtConnect);
        Assert.Equal(1, (int)s.Completions);
        Assert.True((bool)s.TimerStoppedAtAsk);
        AssertPersisted(RadioOwnership.Mine, ProfileGuestIntent.LoadMineAndPutBack);
    }

    [Fact]
    public void SomeoneElsesRecordsOwnershipOnlyAndDoesNotWriteLeaveAlone()
    {
        dynamic s = Selector(RadioOwnership.SomeoneElses);
        s.Connect();
        AssertPersisted(RadioOwnership.SomeoneElses, ProfileGuestIntent.NotAnswered);
        Assert.Equal(ProfileGuestIntent.NotAnswered, (ProfileGuestIntent)s.IntentAtConnect);
        Assert.Equal(RadioOwnership.SomeoneElses, (RadioOwnership)s.OwnershipAtConnect);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EscapeOrSilencingWritesNoOwnershipOrIntentAndNeverLeaveAlone(bool silence)
    {
        dynamic s = Selector(null, silence);
        s.Connect();
        Assert.Equal(1, (int)s.Asks);
        Assert.Equal(1, (int)s.Completions);
        Assert.Equal(RadioOwnership.Unset, RadioConfig.OwnershipOf(_serial));
        Assert.Equal(ProfileGuestIntent.NotAnswered, RadioConfig.ProfileIntentOf(_serial));
        Assert.False(File.Exists(ConfigFile()));
        Assert.Equal(silence, AdvisorySuppression.IsSuppressed(AdvisoryKeys.RadioOwnership(_serial)));

        dynamic next = Selector();
        next.Connect();
        Assert.Equal(silence ? 0 : 1, (int)next.Asks);
        Assert.Equal(1, (int)next.Completions);
    }

    [Theory]
    [InlineData(RadioOwnership.Mine, ProfileGuestIntent.LoadMineAndPutBack)]
    [InlineData(RadioOwnership.SomeoneElses, ProfileGuestIntent.NotAnswered)]
    public void SuppressingAlongsideAnAnswerPreservesThatAnswer(
        RadioOwnership answer, ProfileGuestIntent intent)
    {
        dynamic s = Selector(answer, true);
        s.Connect();
        AssertPersisted(answer, intent);
        Assert.True(AdvisorySuppression.IsSuppressed(AdvisoryKeys.RadioOwnership(_serial)));
    }

    [Fact]
    public void ExistingSuppressionIsCheckedBeforeDialogConstructionAndCanBeRestored()
    {
        var key = AdvisoryKeys.RadioOwnership(_serial);
        AdvisorySuppression.Suppress(key);
        dynamic s = Selector(RadioOwnership.Mine);
        s.AutoConnect();
        Assert.Equal(0, (int)s.Constructions);
        Assert.Equal(0, (int)s.Asks);
        Assert.Equal(ProfileGuestIntent.NotAnswered, (ProfileGuestIntent)s.IntentAtConnect);
        Assert.False(File.Exists(ConfigFile()));
        Assert.True(AdvisorySuppression.Unsuppress(key.Value));
        dynamic next = Selector(RadioOwnership.Mine);
        next.AutoConnect();
        Assert.Equal(1, (int)next.Asks);
        AssertPersisted(RadioOwnership.Mine, ProfileGuestIntent.LoadMineAndPutBack);
    }

    [Fact]
    public void SameSerialIsAskedOncePerSelectorButAnotherSerialHasItsOwnQuestion()
    {
        dynamic s = Selector();
        s.Connect();
        s.Connect();
        Assert.Equal(1, (int)s.Asks);
        s.SetSerial(OtherSerial);
        s.Connect();
        Assert.Equal(2, (int)s.Asks);
        Assert.Equal(3, (int)s.Completions);
    }

    [Fact]
    public void NestedConnectCannotCompleteWhileTheQuestionIsOpen()
    {
        dynamic s = Selector(RadioOwnership.Mine);
        s.DuringQuestion = (Action)(() =>
        {
            s.Connect();
            s.AutoConnect();
            Assert.Equal(0, (int)s.Completions);
            Assert.Equal(RadioOwnership.Unset, RadioConfig.OwnershipOf(_serial));
        });
        s.Connect();
        Assert.Equal(1, (int)s.Asks);
        Assert.Equal(1, (int)s.Completions);
        AssertPersisted(RadioOwnership.Mine, ProfileGuestIntent.LoadMineAndPutBack);
    }

    [Fact]
    public void ForeignAccountSwitchResumesTheActualFunnelWithoutAskingAgainAfterEscape()
    {
        dynamic s = Selector();
        s.MakeForeign();
        s.Connect();
        Assert.Equal(1, (int)s.Switches);
        Assert.Equal(1, (int)s.Asks);
        Assert.Equal(1, (int)s.AsksAtSwitch);
        Assert.Equal(0, (int)s.Completions);
        s.FinishDiscovery();
        Assert.Equal(1, (int)s.Asks);
        Assert.Equal(1, (int)s.Completions);
        Assert.False(File.Exists(ConfigFile()));
    }

    [Theory]
    [InlineData(ConnectPathKind.Local)]
    [InlineData(ConnectPathKind.SmartLink)]
    public void ForcedConnectAlsoAsksBeforeCompleting(ConnectPathKind path)
    {
        dynamic s = Selector(RadioOwnership.Mine);
        s.ForceConnect(path);
        Assert.Equal(1, (int)s.Asks);
        Assert.Equal(ProfileGuestIntent.LoadMineAndPutBack, (ProfileGuestIntent)s.IntentAtConnect);
    }

    [Theory]
    [InlineData(ProfileGuestIntent.LeaveAlone)]
    [InlineData(ProfileGuestIntent.UseMyTransmitAudio)]
    public void AnExistingProfileAnswerIsNotOverwritten(ProfileGuestIntent intent)
    {
        RadioConfig.RecordProfileIntent(_serial, intent);
        dynamic s = Selector(RadioOwnership.Mine);
        s.Connect();
        Assert.Equal(0, (int)s.Asks);
        AssertPersisted(RadioOwnership.Unset, intent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingLoadIntentWithUnsetOwnershipAsksAndRecordsOwnership(bool automatic)
    {
        RadioConfig.RecordProfileIntent(_serial, ProfileGuestIntent.LoadMineAndPutBack);
        dynamic s = Selector(RadioOwnership.Mine);
        if (automatic) s.AutoConnect(); else s.Connect();
        Assert.Equal(1, (int)s.Asks);
        Assert.Equal(RadioOwnership.Mine, (RadioOwnership)s.OwnershipAtConnect);
        Assert.Equal(ProfileGuestIntent.LoadMineAndPutBack, (ProfileGuestIntent)s.IntentAtConnect);
        AssertPersisted(RadioOwnership.Mine, ProfileGuestIntent.LoadMineAndPutBack);
    }

    [Theory]
    [InlineData(RadioOwnership.Mine)]
    [InlineData(RadioOwnership.SomeoneElses)]
    public void DeclaredOwnershipWithLoadIntentIsNotAskedAgain(RadioOwnership ownership)
    {
        var config = RadioConfig.LoadForRadio(_serial);
        config.Ownership = ownership;
        config.ProfileIntent = ProfileGuestIntent.LoadMineAndPutBack;
        Assert.True(config.SaveForRadio(_serial));
        dynamic s = Selector(RadioOwnership.Mine);
        s.Connect();
        Assert.Equal(0, (int)s.Asks);
        AssertPersisted(ownership, ProfileGuestIntent.LoadMineAndPutBack);
    }

    /// <summary>
    /// The regression #638 names: answering "someone else's" persists
    /// <c>SomeoneElses</c> + <c>NotAnswered</c>, and a predicate that reads the
    /// intent without the ownership asked again on every later connect. This is
    /// the state the dialog itself creates by being used, which is why the
    /// defect reached a build — the sibling above covers a declared LOAD intent,
    /// and nothing covered a declared answer with no intent.
    /// </summary>
    [Theory]
    [InlineData(RadioOwnership.Mine)]
    [InlineData(RadioOwnership.SomeoneElses)]
    public void DeclaredOwnershipWithNoIntentIsNotAskedAgain(RadioOwnership ownership)
    {
        var config = RadioConfig.LoadForRadio(_serial);
        config.Ownership = ownership;
        config.ProfileIntent = ProfileGuestIntent.NotAnswered;
        Assert.True(config.SaveForRadio(_serial));
        dynamic s = Selector(RadioOwnership.Mine);
        s.Connect();
        Assert.Equal(0, (int)s.Asks);
        AssertPersisted(ownership, ProfileGuestIntent.NotAnswered);
    }

    [Theory]
    [InlineData(ProfileGuestIntent.NotAnswered)]
    [InlineData(ProfileGuestIntent.LoadMineAndPutBack)]
    public void HoldDefersTheQuestionUntilItIsLifted(ProfileGuestIntent intent)
    {
        var config = RadioConfig.LoadForRadio(_serial);
        config.ProfileIntent = intent;
        config.ChangeNothingOnThisRadio = true;
        Assert.True(config.SaveForRadio(_serial));
        dynamic s = Selector(RadioOwnership.Mine);
        s.Connect();
        Assert.Equal(0, (int)s.Asks);
        AssertPersisted(RadioOwnership.Unset, intent);
        config.ChangeNothingOnThisRadio = false;
        Assert.True(config.SaveForRadio(_serial));
        s.Connect();
        Assert.Equal(1, (int)s.Asks);
        AssertPersisted(RadioOwnership.Mine, ProfileGuestIntent.LoadMineAndPutBack);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(RadioOwnership.SomeoneElses)]
    public void ExistingLoadIntentIsPreservedWhenOwnershipIsNotClaimed(RadioOwnership? answer)
    {
        RadioConfig.RecordProfileIntent(_serial, ProfileGuestIntent.LoadMineAndPutBack);
        dynamic s = Selector(answer);
        s.Connect();
        Assert.Equal(1, (int)s.Asks);
        AssertPersisted(answer ?? RadioOwnership.Unset, ProfileGuestIntent.LoadMineAndPutBack);
    }

    [Fact]
    public void EveryOwnershipIntentAndHoldCombinationAgreesWithTheRefusalLadder()
    {
        foreach (var ownership in Enum.GetValues<RadioOwnership>())
        foreach (var intent in Enum.GetValues<ProfileGuestIntent>())
        foreach (bool hold in new[] { false, true })
        {
            var facts = new StationPolicyFacts {
                Connected = true, Ownership = ownership, Intent = intent,
                HoldArmed = hold, WantedGlobal = "Test global"
            };
            string refusal = StationCoordinator.AutomaticStewardshipRefusal(facts);
            // An ownership answer can only help while ownership is UNSET.
            // A declared answer — Mine OR SomeoneElses — is settled, and
            // re-asking is the defect #638 names; the RadioOwnership enum says
            // so itself: Unset is "the only value that lets the question be
            // raised, so a radio that has been answered — either way — is never
            // asked about again."
            //
            // This clause tested the refusal STRING alone until 2026-10-01, so
            // it expected the question to help for SomeoneElses + NotAnswered —
            // which is the state answering "someone else's" persists (see
            // SomeoneElsesRecordsOwnershipOnlyAndDoesNotWriteLeaveAlone). The
            // test therefore pinned the re-ask as desired behaviour.
            //
            // Within Unset it helps when the ladder's refusal is one that
            // declaring ownership clears: the unanswered profile question, or
            // #590's owner gate. LeaveAlone and UseMyTransmitAudio are refused
            // at gates three and four regardless of ownership, so an answer
            // there unblocks nothing and would invite re-claiming a radio the
            // operator correctly declined.
            bool answerCanHelp = ownership == RadioOwnership.Unset
                && (refusal == "the profile question for this radio is not answered"
                    || refusal == "this connection is not the declared owner's; only the owner's connection restores a station (ruled 2026-09-21, #590)");
            Assert.Equal(answerCanHelp,
                StationCoordinator.OwnershipQuestionWouldHelp(ownership, intent, hold));
            if (answerCanHelp)
            {
                facts.Ownership = RadioOwnership.Mine;
                facts.Intent = ProfileGuestIntent.LoadMineAndPutBack;
                Assert.Null(StationCoordinator.AutomaticStewardshipRefusal(facts));
            }
        }
    }

    [Fact]
    public void ConnectDoesNotSuggestOwnershipEvenWhenWorkshopWould()
    {
        var config = RadioConfig.LoadForRadio(_serial);
        config.LastSeenUtc = DateTime.UtcNow;
        config.LastSeenRemote = false;
        Assert.Equal(RadioOwnership.Mine, config.SuggestOwnership(null));
        Assert.True(config.SaveForRadio(_serial));
        dynamic s = Selector();
        s.Connect();
        Assert.Equal(RadioOwnership.Unset, (RadioOwnership)s.Suggestion);
        s.AskFromWorkshop();
        Assert.Equal(RadioOwnership.Mine, (RadioOwnership)s.Suggestion);
    }

    [Fact]
    public void AnUnreadableFileAfterTheQuestionOpensDoesNotLoseTheLoadedSettings()
    {
        var config = RadioConfig.LoadForRadio(_serial);
        config.FixedHolePunchPort = 5638;
        Assert.True(config.SaveForRadio(_serial));
        dynamic s = Selector(RadioOwnership.Mine);
        s.DuringQuestion = (Action)(() => File.WriteAllText(ConfigFile(), "incomplete XML"));
        s.Connect();
        AssertPersisted(RadioOwnership.Mine, ProfileGuestIntent.LoadMineAndPutBack);
        Assert.Equal(5638, RadioConfig.LoadForRadio(_serial).FixedHolePunchPort);
    }

    [Fact]
    public void FailedSaveDoesNotConnectOrSplitTheAnswerAndCanBeRetried()
    {
        var config = RadioConfig.LoadForRadio(_serial);
        config.FixedHolePunchPort = 5638;
        Assert.True(config.SaveForRadio(_serial));
        dynamic s = Selector(RadioOwnership.Mine);
        FileStream locked = null;
        try
        {
            s.DuringQuestion = (Action)(() => locked = File.Open(ConfigFile(),
                FileMode.Open, FileAccess.Read, FileShare.None));
            s.Connect();
            Assert.Equal(0, (int)s.Completions);
        }
        finally { locked?.Dispose(); }
        AssertPersisted(RadioOwnership.Unset, ProfileGuestIntent.NotAnswered);
        Assert.Equal(5638, RadioConfig.LoadForRadio(_serial).FixedHolePunchPort);
        s.DuringQuestion = null;
        s.Connect();
        Assert.Equal(2, (int)s.Asks);
        Assert.Equal(1, (int)s.Completions);
        AssertPersisted(RadioOwnership.Mine, ProfileGuestIntent.LoadMineAndPutBack);
    }

    [Fact]
    public void SuppressionIsPerRadioVersionedAndListedByTitleAfterReload()
    {
        var key = AdvisoryKeys.RadioOwnership(_serial);
        var other = AdvisoryKeys.RadioOwnership(OtherSerial);
        Assert.EndsWith("-v1", key.Value);
        Assert.Equal(key.Label, AdvisoryKeys.Describe(key.Value));
        Assert.Contains(_serial, key.Label);
        Assert.DoesNotContain("radio-ownership|", key.Label);
        var path = _scope.PathTo("suppression.json");
        new AdvisorySuppressionStore(path).Suppress(key);
        var reopened = new AdvisorySuppressionStore(path);
        Assert.False(reopened.IsSuppressed(other));
        var item = Assert.Single(reopened.Snapshot());
        Assert.Equal(key.Label, item.Label);
        Assert.Contains(key.Label, item.Sentence());
        Assert.DoesNotContain(key.Value, item.Sentence());
        Assert.True(reopened.Unsuppress(key.Value));
        Assert.Empty(new AdvisorySuppressionStore(path).Snapshot());
    }

    private string ConfigFile() => _scope.PathTo("radios", _serial, "config.xml");

    private void AssertPersisted(RadioOwnership ownership, ProfileGuestIntent intent)
    {
        Assert.Equal(ownership, RadioConfig.OwnershipOf(_serial));
        Assert.Equal(intent, RadioConfig.ProfileIntentOf(_serial));
        // Read the real XML independently of the in-memory config cache.
        var doc = System.Xml.Linq.XDocument.Load(ConfigFile());
        Assert.Equal(ownership.ToString(), doc.Root!.Element("Ownership")!.Value);
        Assert.Equal(intent.ToString(), doc.Root!.Element("ProfileIntent")!.Value);
    }

    private static Type CompileSelector()
    {
        string root = RepoRoot();
        var selector = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root,
            "JJFlexWpf", "Dialogs", "RigSelectorDialog.xaml.cs"))).GetRoot();
        var dialog = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root,
            "JJFlexWpf", "Dialogs", "RadioOwnershipDialog.cs"))).GetRoot();
        string[] methods = { "DoConnect", "AutoConnectTimer_Tick", "ResumePendingConnect", "TryStartRemoteLookFor" };
        string[] fields = { "_ownershipAsked", "_ownershipQuestionOpen", "_accountSwitchTried" };
        string members = string.Join("\n", selector.DescendantNodes()
            .OfType<MethodDeclarationSyntax>().Where(m => methods.Contains(m.Identifier.Text))
            .Select(m => m.ToFullString()));
        members += string.Join("\n", selector.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Where(f => f.Declaration.Variables.Any(v => fields.Contains(v.Identifier.Text)))
            .Select(f => f.ToFullString()));
        string[] questionMethods = { "Ask", "AskForConnect", "ShowQuestion" };
        string ask = string.Join("\n", dialog.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => questionMethods.Contains(m.Identifier.Text)).Select(m => m.ToFullString()));
        string source = Harness.Replace("/* SELECTOR METHODS */", members)
            .Replace("/* ASK METHOD */", ask);
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Append(typeof(RadioConfig).Assembly.Location)
            .Distinct().Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("ConnectOwnershipHeadless",
            new[] { CSharpSyntaxTree.ParseText(source) }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return Assembly.Load(image.ToArray()).GetType("Headless.Selector")!;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JJFlexRadio.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found");
    }

    // These are terminal UI/discovery collaborators only. DoConnect, Ask,
    // the timer and the discovery-resume code above come from production.
    private const string Harness = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using global::Radios;
        namespace Headless {
        using Radios = Headless;
        public class RadioListItem {
            public string Serial = "";
            public bool ForeignAccount, LanAvailable = true, WanAvailable = true, WanUnconfirmed;
            // Availability is three facts and the rest is read from them,
            // exactly as the production row does it (#619, Track L12). Keep
            // these computed: a settable IsLive let the stub disagree with
            // the shipped rule without anything noticing.
            public global::Radios.PickerRowPaths Paths => new(LanAvailable, WanAvailable, WanUnconfirmed);
            public bool IsLive => Paths.IsLive;
            public bool HasPathToTry => Paths.HasPathToTry;
            public string BoundAccount = "", LastSeenViaAccount = "";
            public List<ConnectPathKind> EffectiveChain = new() { ConnectPathKind.Local, ConnectPathKind.SmartLink };
        }
        public class Timer { public bool Stopped; public void Stop() => Stopped = true; }
        public class Callbacks {
            public string AutoConnectSerial = "";
            public Action<string, bool> ScreenReaderSpeak;
            public Action<string> SetSessionAccount;
        }
        public class Account { public string Email = "borrowed@example.invalid"; }
        public class Accounts { public Account GetAccountByEmail(string email) => new Account(); }
        public static class FlexBase { public static Accounts SharedAccountManager = new(); }
        // A FORWARDER, not a stub. The harness aliases Radios to Headless so
        // that the spliced picker source finds terminal collaborators here, and
        // that alias also catches the sighting rules, which are real production
        // logic. Forwarding means the test reads the SHIPPED answer; stubbing
        // would have it read this file's opinion instead.
        public static class PickerSighting {
            public static bool AutoConnectMayChoose(global::Radios.PickerRowPaths row) =>
                global::Radios.PickerSighting.AutoConnectMayChoose(row);
        }
        namespace StationConnect {
            public static class StationCoordinator {
                public static bool OwnershipQuestionWouldHelp(RadioOwnership ownership,
                    ProfileGuestIntent intent, bool holdArmed) =>
                    global::Radios.StationConnect.StationCoordinator.OwnershipQuestionWouldHelp(
                        ownership, intent, holdArmed);
            }
        }
        public class Selector {
            private readonly object _radiosLock = new();
            private readonly List<RadioListItem> _radiosList = new();
            private readonly Callbacks _callbacks = new();
            private readonly Timer _autoConnectTimer = new();
            private string _pendingConnectSerial;
            private ConnectPathKind? _pendingConnectForced;
            private bool _remoteDiscoveryInFlight, _remoteListLive;
            private bool IsLoaded => true;
            public int Completions, Switches, AsksAtSwitch;
            public int Asks => RadioOwnershipDialog.Asks;
            public int Constructions => RadioOwnershipDialog.Constructions;
            public RadioOwnership Suggestion => RadioOwnershipDialog.Suggestion;
            public void AskFromWorkshop() => RadioOwnershipDialog.Ask(_radiosList[0].Serial,
                "Workshop test radio", "Test deliberate write");
            public bool TimerStoppedAtAsk;
            public Action DuringQuestion;
            public RadioOwnership OwnershipAtConnect;
            public ProfileGuestIntent IntentAtConnect;
            public Selector(string serial) {
                _radiosList.Add(new RadioListItem { Serial = serial });
                _callbacks.AutoConnectSerial = serial;
                RadioOwnershipDialog.Asks = RadioOwnershipDialog.Constructions = 0;
                RadioOwnershipDialog.OnShow = () => {
                    TimerStoppedAtAsk = _autoConnectTimer.Stopped;
                    DuringQuestion?.Invoke();
                };
            }
            public void SetAnswer(RadioOwnership? answer, bool silence) {
                RadioOwnershipDialog.Answer = answer; RadioOwnershipDialog.Silence = silence;
            }
            public void SetSerial(string serial) { _radiosList[0].Serial = serial; }
            public void Connect() => DoConnect(_radiosList[0]);
            public void AutoConnect() => AutoConnectTimer_Tick(null, EventArgs.Empty);
            public void ForceConnect(ConnectPathKind path) => DoConnect(_radiosList[0], path);
            public void MakeForeign() {
                var r = _radiosList[0]; r.ForeignAccount = true;
                r.LanAvailable = r.WanAvailable = false; r.BoundAccount = "borrowed@example.invalid";
            }
            public void FinishDiscovery() {
                var r = _radiosList[0]; r.WanAvailable = true;
                _remoteDiscoveryInFlight = false; _remoteListLive = true;
                ResumePendingConnect(true);
            }
            private string RowName(RadioListItem radio) => "Test radio " + radio.Serial;
            private void UpdateAccountAffordances() { }
            private object CurrentAccountState() => new object();
            private string CurrentAccountEmail() => "operator@example.invalid";
            private void SwitchToAccount(object state) { Switches++; AsksAtSwitch = Asks; }
            private void StartRemoteFlow() => _remoteDiscoveryInFlight = true;
            private void CompleteConnect(RadioListItem radio, ConnectPathKind path,
                bool forced, List<ConnectPathKind> fallbacks) {
                OwnershipAtConnect = RadioConfig.OwnershipOf(radio.Serial);
                IntentAtConnect = RadioConfig.ProfileIntentOf(radio.Serial);
                Completions++;
            }
            /* SELECTOR METHODS */
        }
        public class CheckBox { public bool? IsChecked; }
        public class RadioOwnershipDialog {
            public static RadioOwnership? Answer;
            public static bool Silence;
            public static int Asks, Constructions;
            public static Action OnShow;
            private RadioOwnership? _answer;
            private CheckBox _dontShowAgain = new();
            public static RadioOwnership Suggestion;
            private RadioOwnershipDialog(string label, string reason, RadioOwnership suggestion) {
                Constructions++; Suggestion = suggestion;
            }
            private void ShowModalDialog() {
                Asks++; OnShow?.Invoke(); _answer = Answer; _dontShowAgain.IsChecked = Silence;
            }
            /* ASK METHOD */
        }
        }
        """;
}
