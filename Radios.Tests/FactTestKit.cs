#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Radios;
using Radios.Facts;

namespace Radios.Tests
{
    /// <summary>
    /// A catalogue the tests control: the same typed lookup the production
    /// renderer reads, holding SYNTHETIC entries only.
    /// </summary>
    /// <remarks>
    /// <b>Synthetic, and named so.</b> No production fact producer exists on
    /// this base, so every owner, contract and message in these tests is a
    /// stand-in for one. They prove the store honours an owner-issued
    /// transition; they cannot prove any real owner's thresholds.
    /// </remarks>
    internal sealed class SyntheticCatalog : IFactCatalog
    {
        private readonly Dictionary<string, LexiconEntry> _entries = new(StringComparer.Ordinal);

        public long Generation { get; set; } = 1;

        public LexiconMessage Lookup(string key)
        {
            _entries.TryGetValue(key, out LexiconEntry? entry);
            return new LexiconMessage(key, entry, Array.Empty<(string, object?)>(), Generation);
        }

        public SyntheticCatalog Message(string key, LexiconEntry text, ShelfLife life, string? historyKey,
                                        ReceiptPolicy receipt, string validity = "syn.active")
        {
            _entries[key] = text.WithDelivery(DeliveryClassification.Message,
                new DeliveryDescriptor(life, validity, historyKey, receipt));
            return this;
        }

        public SyntheticCatalog Text(string key, LexiconEntry text)
        {
            _entries[key] = text.WithDelivery(DeliveryClassification.TextOnly, null);
            return this;
        }

        public SyntheticCatalog Unclassified(string key, string text)
        {
            _entries[key] = LexiconEntry.Plain(text);
            return this;
        }

        public static SyntheticCatalog Standard() => new SyntheticCatalog()
            .Message(FactKit.HotKey,
                LexiconEntry.Ladder("PA hot.", "PA at {temperature} degrees.",
                                    "PA at {temperature} degrees for {duration} minutes."),
                ShelfLife.Persistent, FactKit.HotKey + ".history", ReceiptPolicy.Warning)
            .Text(FactKit.HotKey + ".history", LexiconEntry.Plain("Earlier the PA was at {temperature} degrees."))
            .Message(FactKit.CutKey, LexiconEntry.Plain("Transmit was cut."), ShelfLife.Perishable,
                FactKit.CutKey + ".history", ReceiptPolicy.Warning)
            .Text(FactKit.CutKey + ".history", LexiconEntry.Plain("A moment ago, transmit was cut."))
            .Message(FactKit.BriefKey, LexiconEntry.Plain("PC audio on."), ShelfLife.Forgettable, null,
                ReceiptPolicy.None, ValidityContracts.RequestScoped)
            .Unclassified(FactKit.MysteryKey, "Something happened.")
            .Text(FactKit.LabelKey, LexiconEntry.Plain("A label"))
            .Message(FactKit.StopRequestedKey, LexiconEntry.Plain("Stop requested."), ShelfLife.Perishable,
                FactKit.StopRequestedKey + ".history", ReceiptPolicy.None)
            .Text(FactKit.StopRequestedKey + ".history", LexiconEntry.Plain("A stop was requested."))
            .Message(FactKit.StopConfirmedKey, LexiconEntry.Plain("Stop confirmed."), ShelfLife.Perishable,
                FactKit.StopConfirmedKey + ".history", ReceiptPolicy.Warning)
            .Text(FactKit.StopConfirmedKey + ".history", LexiconEntry.Plain("A stop was confirmed."));
    }

    /// <summary>A transport adapter that records every native request it is asked to make.</summary>
    internal sealed class RecordingTransport
    {
        public RecordingTransport(FactAuthorityRegistry registry, string name, TransportCapability capability)
        {
            Binding = registry.RegisterTransport(name, capability);
        }

        public TransportBinding Binding { get; }
        public List<string> Sent { get; } = new();
        public int NativeCalls => Sent.Count;

        /// <summary>The lowest native boundary: counts, and returns a ticket.</summary>
        public string? Submit(PresentationPlan plan, AttemptHandle attempt)
        {
            Sent.Add(plan.Text);
            return "ticket-" + attempt.Id.Ordinal;
        }
    }

    /// <summary>
    /// A radio-incapable set of synthetic owners, contracts and sessions over
    /// one store. Nothing here can command a radio.
    /// </summary>
    internal sealed class FactKit
    {
        public const string HotKey = "syn.hot";
        public const string CutKey = "syn.cut";
        public const string BriefKey = "syn.brief";
        public const string MysteryKey = "syn.mystery";
        public const string LabelKey = "syn.label";
        public const string StopRequestedKey = "syn.stop.requested";
        public const string StopConfirmedKey = "syn.stop.confirmed";

        public static readonly DateTime T0 = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

        public static readonly ConditionContract Temperature = new ConditionContract(
            "syn.temperature", 1, new[] { "condition.hot" }, new[] { HotKey }, DeliveryPriority.OperatorAlarm,
            new[] { new EvidenceField("temperature", FactValueKind.Decimal), new EvidenceField("duration", FactValueKind.Integer, required: false) },
            mayReportWorsening: true, mayResolve: true);

        public static readonly ConditionContract StopRequest = new ConditionContract(
            "syn.stop-request", 1, new[] { "stop.requested" }, new[] { StopRequestedKey }, DeliveryPriority.TransmitSafety);

        public static readonly ConditionContract StopConfirm = new ConditionContract(
            "syn.stop-confirm", 1, new[] { "stop.confirmed" }, new[] { StopConfirmedKey }, DeliveryPriority.TransmitSafety,
            new[] { new EvidenceField("confirmed", FactValueKind.Flag) }, mayResolve: true);

        public static readonly ConditionContract General = new ConditionContract(
            "syn.general", 1, new[] { "note" }, new[] { CutKey, BriefKey, MysteryKey, LabelKey }, DeliveryPriority.Ordinary);

        public FactKit(SyntheticCatalog? catalog = null)
        {
            Catalog = catalog ?? SyntheticCatalog.Standard();
            Registry = FactAuthorityRegistry.Create(Catalog);
            Store = Registry.Store;
            Hot = Registry.DeclareOwner("synthetic temperature owner", Temperature);
            OtherHot = Registry.DeclareOwner("synthetic second temperature owner", Temperature);
            Witness = Registry.DeclareOwner("synthetic stop-request witness", StopRequest);
            Confirmer = Registry.DeclareOwner("synthetic stop-confirmation owner", StopConfirm);
            Notes = Registry.DeclareOwner("synthetic general owner", General);
            Presentation = Registry.RegisterPresentation("test presentation builder");
        }

        public SyntheticCatalog Catalog { get; }
        public FactAuthorityRegistry Registry { get; }
        public FactStore Store { get; }
        public FactOwner Hot { get; }
        public FactOwner OtherHot { get; }
        public FactOwner Witness { get; }
        public FactOwner Confirmer { get; }
        public FactOwner Notes { get; }
        public FactPresentation Presentation { get; }

        public FactSession Session(string? radio = "SERIAL-1") => Registry.OpenSession(radio, "test");

        public SlotPublisher Slot(FactScope scope, FactOwner owner, ConditionContract contract, string condition = "pa")
        {
            RegistrationResult result = Registry.Register(owner, scope, contract, new ConditionKey(condition));
            if (!result.Granted) throw new InvalidOperationException("registration failed: " + result);
            return result.Publisher!;
        }

        public SlotPublisher HotSlot(FactScope scope, string condition = "pa") => Slot(scope, Hot, Temperature, condition);
        public SlotPublisher NotesSlot(FactScope scope, string condition = "notes") => Slot(scope, Notes, General, condition);

        public static FactObservation Temp(decimal t, long? minutes = 3) =>
            minutes == null
                ? FactObservation.Of(("temperature", FactValue.Of(t)))
                : FactObservation.Of(("temperature", FactValue.Of(t)), ("duration", FactValue.Of(minutes.Value)));

        public static CapturedFactEvent Capture(SlotPublisher publisher, FactObservation? observation = null, string? source = null)
        {
            CaptureResult result = publisher.Capture(observation ?? FactObservation.Empty, T0, source);
            if (!result.Captured) throw new InvalidOperationException("capture refused: " + result.Outcome + " " + result.Explanation);
            return result.Event!;
        }

        /// <summary>Open a hot-PA occurrence with its temperature and duration as owed material.</summary>
        public static PublicationResult OpenHot(SlotPublisher publisher, decimal temperature = 70m, long minutes = 3,
                                                OpenOptions? options = null)
        {
            CapturedFactEvent ev = Capture(publisher, Temp(temperature, minutes));
            return publisher.Open(ev, "condition.hot", HotKey,
                new[] { new MaterialDeclaration("temperature", FactValue.Of(temperature)),
                        new MaterialDeclaration("duration", FactValue.Of(minutes)) },
                options);
        }

        public static PublicationResult OpenNote(SlotPublisher publisher, string key, string? detail = null,
                                                 IEnumerable<MaterialDeclaration>? materials = null)
        {
            CapturedFactEvent ev = Capture(publisher);
            return publisher.Open(ev, "note", key, materials, new OpenOptions { Detail = detail });
        }

        public static WorseningTransition Worse(FactSnapshot current, string id, decimal temperature) =>
            new WorseningTransition(id, current.Baseline.Fingerprint,
                new[] { new MaterialDeclaration("temperature", FactValue.Of(temperature)) }, "worse by owner rule");

        /// <summary>
        /// The links a continuing owner declares: each declared unit that the
        /// continuity view offers at the SAME name and value is the same
        /// assertion. Anything else is new information.
        /// </summary>
        public static MaterialLink[] Links(ContinuityView? view, params MaterialDeclaration[] declared)
        {
            if (view == null) return Array.Empty<MaterialLink>();
            var links = new List<MaterialLink>();
            foreach (MaterialDeclaration d in declared)
            {
                ContinuityAssertion? same = view.Assertions.FirstOrDefault(a => a.Name == d.Name && a.Value == d.Value);
                if (same != null) links.Add(new MaterialLink(d.Name, same.Reference));
            }
            return links.ToArray();
        }

        /// <summary>
        /// Open a hot-PA occurrence as a CONTINUATION of what the store holds
        /// for this station: the issued predecessor reference, and a link for
        /// every declared unit the predecessor already held at that value.
        /// </summary>
        public static PublicationResult ContinueHot(SlotPublisher publisher, decimal temperature = 70m, long minutes = 3,
                                                    ContinuityClaim claim = ContinuityClaim.Continuation,
                                                    WorseningTransition? worsening = null, decimal? observedTemperature = null)
        {
            ContinuityView? view = publisher.Continuity;
            var declared = new[]
            {
                new MaterialDeclaration("temperature", FactValue.Of(temperature)),
                new MaterialDeclaration("duration", FactValue.Of(minutes)),
            };
            // On a reconnect worsening the carried declaration is the earlier
            // value and the observation is the worse one.
            CapturedFactEvent ev = Capture(publisher, Temp(observedTemperature ?? temperature, minutes));
            return publisher.Open(ev, "condition.hot", HotKey, declared, new OpenOptions
            {
                Continuity = claim,
                Predecessor = view?.Reference,
                Carried = Links(view, declared),
                WorseningOfPrior = worsening,
            });
        }

        /// <summary>Open a hot-PA occurrence as an evidenced NEW ONSET: the owner witnessed the rise.</summary>
        public static PublicationResult OnsetHot(SlotPublisher publisher, decimal temperature = 70m, long minutes = 3) =>
            OpenHot(publisher, temperature, minutes, new OpenOptions
            {
                Continuity = ContinuityClaim.NewOccurrence,
                NewOnsetEvidence = FactObservation.Of(("onset", FactValue.Of("sensor reported a fresh rise"))),
            });

        public PresentationPlan PlanAutomatic(EpisodeId id, VerbosityLevel tier = VerbosityLevel.Chatty)
        {
            PlanPreparation prepared = Presentation.Prepare(id, PlanRequest.Automatic(tier));
            if (!prepared.Prepared) throw new InvalidOperationException("not prepared: " + prepared.Outcome + " " + prepared.Explanation);
            return prepared.Plan!;
        }

        public AttemptHandle Allocate(PresentationPlan plan, TransportBinding binding)
        {
            AttemptAllocation allocation = Presentation.AllocateAttempt(plan, binding);
            if (allocation.Attempt == null) throw new InvalidOperationException("not allocated: " + allocation.Outcome);
            return allocation.Attempt;
        }
    }

    /// <summary>An absolute temporary directory for journal tests, never the real settings folder.</summary>
    internal sealed class TempFactDir : IDisposable
    {
        public TempFactDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "jjflex-facts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string[] Shards() => Directory.GetFiles(Path, "facts-*.json").Where(p => p.EndsWith(".json", StringComparison.Ordinal)).ToArray();

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { /* a temp directory we could not remove is not a test failure */ }
            catch (UnauthorizedAccessException) { /* nor this */ }
        }
    }
}
