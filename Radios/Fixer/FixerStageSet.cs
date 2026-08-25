using System;
using System.Collections.Generic;
using System.Threading;

namespace Radios.Fixer
{
    /// <summary>
    /// One Fixer domain — a set of stages — as data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The runner, the report and the page take one of these and never ask
    /// which domain it is.</b> That is the whole contract: the transmit set is
    /// the first tenant, a connection set is coming, and it must arrive as
    /// another one of these plus host bindings — never as a change to
    /// <see cref="FixerRun"/>, <see cref="FixerReport"/> or
    /// <see cref="FixerPage"/>. If a second domain needs those touched, the
    /// framework is wrong, not the domain.
    /// </para>
    /// <para>
    /// Anything a stage DOES — measuring, and above all transmitting — is an
    /// injected delegate the host supplies when it builds the set. Nothing in
    /// Radios.Fixer keys a transmitter or opens an audio device; the engine
    /// invokes what it was handed and records what came back. A stage whose
    /// delegate was never supplied is recorded as unable to run, honestly,
    /// rather than attempted some other way.
    /// </para>
    /// </remarks>
    public sealed class FixerStageSet
    {
        /// <summary>Stable identifier, e.g. "transmit". Goes into help links
        /// and traces; never shown as prose.</summary>
        public string Id { get; }

        /// <summary>The domain's name as an operator reads it, e.g. "Transmit".</summary>
        public string Name { get; }

        /// <summary>Shown at the top of the page. This is where the set
        /// encourages starting at the beginning — encourages, never locks.</summary>
        public string Intro { get; }

        /// <summary>The stages, in the order they are listed and encouraged.
        /// The order they were actually run in is the run's to record.</summary>
        public IReadOnlyList<FixerStage> Stages { get; }

        /// <summary>
        /// Questions the operator must answer per RUN, each sent to the host
        /// as its own message — never bundled into a stage request, because a
        /// safety fact re-asserted per request is a fact the caller controls
        /// (see FixerTransmitGate). The transmit set's load declaration is the
        /// founding example. A new run asks again from scratch: the page never
        /// pre-fills these, because the station may have been re-cabled since.
        /// </summary>
        public IReadOnlyList<FixerRunDeclaration> RunDeclarations { get; }

        /// <summary>
        /// The fixes this set's findings can offer, by action id. Every entry
        /// is host-supplied; a finding whose action id has no entry here gets
        /// an honest "could not be performed" record instead of a silent no-op.
        /// </summary>
        public IReadOnlyDictionary<string, FixerFixAction> FixActions { get; }

        public FixerStageSet(string id, string name, string intro,
                             IReadOnlyList<FixerStage> stages,
                             IReadOnlyDictionary<string, FixerFixAction> fixActions,
                             IReadOnlyList<FixerRunDeclaration> runDeclarations = null)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("a stage set needs an id", nameof(id));
            if (stages == null || stages.Count == 0) throw new ArgumentException("a stage set needs stages", nameof(stages));

            Id = id;
            Name = name ?? "";
            Intro = intro ?? "";
            Stages = stages;
            FixActions = fixActions ?? new Dictionary<string, FixerFixAction>();
            RunDeclarations = runDeclarations ?? Array.Empty<FixerRunDeclaration>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FixerStage s in stages)
                if (!seen.Add(s.Id))
                    throw new ArgumentException("duplicate stage id: " + s.Id, nameof(stages));
        }

        /// <summary>The stage with this id, or null.</summary>
        public FixerStage Find(string stageId)
        {
            foreach (FixerStage s in Stages)
                if (string.Equals(s.Id, stageId, StringComparison.OrdinalIgnoreCase))
                    return s;
            return null;
        }
    }

    /// <summary>What a stage's executor hands back to the engine.</summary>
    public sealed class FixerOutcome
    {
        /// <summary>The answer to the stage's question, in a person's voice.
        /// "Yes — Audient EVO8, on WASAPI", not "Microphone: connected".</summary>
        public string Answer { get; set; } = "";

        /// <summary>What was detected, each classified by who can fix it.</summary>
        public IReadOnlyList<FixerFinding> Findings { get; set; } = Array.Empty<FixerFinding>();

        /// <summary>Observations and measurements, plain text, observations
        /// before interpretation (#217). Goes into the report verbatim.</summary>
        public string Evidence { get; set; } = "";

        /// <summary>Stage-set-typed data a later stage may read as a baseline.
        /// The engine stores it and hands it back; it never interprets it.</summary>
        public object Payload { get; set; }
    }

    /// <summary>One stage of a set: its copy, its skip choices, and the
    /// injected function that actually does the work.</summary>
    public sealed class FixerStage
    {
        /// <summary>Stable identifier, e.g. "microphone-check".</summary>
        public string Id { get; set; } = "";

        /// <summary>The stage number the operator sees. Numbers can start at
        /// zero and must be unique within the set; a gap in a report reads as
        /// an omission, which is why skips are recorded rather than blanked.</summary>
        public int Number { get; set; }

        /// <summary>Short name, e.g. "Microphone check".</summary>
        public string Title { get; set; } = "";

        /// <summary>The question this stage answers, asked like a person.
        /// In browse mode the questions become the navigation.</summary>
        public string Question { get; set; } = "";

        /// <summary>The long explanation, shown behind a disclosure so it
        /// costs no tab stops and no forced reading.</summary>
        public string Explanation { get; set; } = "";

        /// <summary>
        /// True when running this stage keys the transmitter. The page says so
        /// next to the run control, and the engine records — it never keys
        /// anything itself; the transmit call lives inside the host-supplied
        /// <see cref="Execute"/> delegate, behind the host's own guards.
        /// </summary>
        public bool Transmits { get; set; }

        /// <summary>Help topic for the inline help link, e.g. "fixer/transmit/microphone-check".</summary>
        public string HelpTopic { get; set; } = "";

        /// <summary>Host-owned actions this stage's panel offers, if any.</summary>
        public IReadOnlyList<FixerHostAction> HostActions { get; set; } = Array.Empty<FixerHostAction>();

        /// <summary>The reasons an operator can give for skipping this stage.
        /// Distinct reasons stay distinct because they do different things to
        /// the conclusion — see <see cref="FixerSkipChoice.Effect"/>.</summary>
        public IReadOnlyList<FixerSkipChoice> SkipChoices { get; set; } = Array.Empty<FixerSkipChoice>();

        /// <summary>
        /// The injected function that runs the stage. Host-supplied. Null means
        /// the host wired nothing, and the engine records that the stage could
        /// not run — it never improvises a measurement, and for a transmitting
        /// stage this null is precisely what keeps the engine on its side of
        /// the transmit boundary.
        /// </summary>
        public Func<FixerStageContext, FixerOutcome> Execute { get; set; }

        /// <summary>The skip choice with this id, or null.</summary>
        public FixerSkipChoice FindSkip(string choiceId)
        {
            foreach (FixerSkipChoice c in SkipChoices)
                if (string.Equals(c.Id, choiceId, StringComparison.OrdinalIgnoreCase))
                    return c;
            return null;
        }
    }

    /// <summary>What a skip reason does to the conclusion. The two microphone
    /// skip reasons are the founding example: "I can't speak into my radio"
    /// NARROWS the fault domain (a PC microphone may still exist, so a
    /// comparison is still possible) while "I have no microphone" LEAVES IT
    /// OPEN (the comparison is impossible). Collapsing them makes two very
    /// different reports read the same.</summary>
    public enum FixerSkipEffect
    {
        /// <summary>The operator chose not to run it. Says nothing beyond
        /// "the answer is weaker for it".</summary>
        OperatorChoice = 0,

        /// <summary>The reason itself rules something in or out.</summary>
        NarrowsFaultDomain,

        /// <summary>The reason makes the stage's comparison impossible, so its
        /// question stays open.</summary>
        LeavesQuestionOpen,
    }

    /// <summary>One reason a stage can be skipped, and what giving that reason
    /// does to the conclusion.</summary>
    public sealed class FixerSkipChoice
    {
        public string Id { get; }

        /// <summary>The reason in the operator's own words, e.g.
        /// "I can't speak directly into my radio."</summary>
        public string Label { get; }

        public FixerSkipEffect Effect { get; }

        /// <summary>What this reason does to the conclusion, spelled out for
        /// the report. Never empty: "not run, and why" is evidence.</summary>
        public string EffectText { get; }

        public FixerSkipChoice(string id, string label, FixerSkipEffect effect, string effectText)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("a skip choice needs an id", nameof(id));
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("a skip choice needs the operator's reason", nameof(label));
            if (string.IsNullOrWhiteSpace(effectText)) throw new ArgumentException("a skip choice must say what it does to the conclusion", nameof(effectText));

            Id = id;
            Label = label;
            Effect = effect;
            EffectText = effectText;
        }
    }

    /// <summary>What a stage's executor gets to see while it runs.</summary>
    public sealed class FixerStageContext
    {
        private readonly Func<string, FixerStageResult> _prior;

        public string RunId { get; }
        public FixerStage Stage { get; }
        public CancellationToken Cancel { get; }

        public FixerStageContext(string runId, FixerStage stage, CancellationToken cancel,
                                 Func<string, FixerStageResult> priorResult)
        {
            RunId = runId ?? "";
            Stage = stage;
            Cancel = cancel;
            _prior = priorResult;
        }

        /// <summary>
        /// An earlier stage's recorded result, or null. This is how a stage
        /// reads its baseline — the spoken transmit stage reads the microphone
        /// check's result through here, because a stage-4 failure means
        /// something quite different depending on whether the microphone
        /// measured well minutes earlier.
        /// </summary>
        public FixerStageResult ResultFor(string stageId)
            => _prior == null ? null : _prior(stageId);
    }

    /// <summary>
    /// A question the operator answers once per run, whose answer is a HOST
    /// event — the page sends it as its own message and the host records it
    /// (the transmit gate's load declaration is the founding case). It is data
    /// here so the page can render it for any domain; what the answer means
    /// belongs entirely to the host.
    /// </summary>
    public sealed class FixerRunDeclaration
    {
        public string Id { get; }

        /// <summary>Asked like a person: "What is the antenna socket connected
        /// to right now?"</summary>
        public string Question { get; }

        /// <summary>Why the run wants to know, one sentence, shown with the
        /// question.</summary>
        public string WhyItMatters { get; }

        public IReadOnlyList<FixerDeclarationChoice> Choices { get; }

        /// <summary>The wire kind the page sends the answer under — see
        /// <see cref="FixerPageMessage"/>. The transmit set's load declaration
        /// travels as "declare-load"; a future set names its own kind here so
        /// the page never has to learn it.</summary>
        public string MessageKind { get; }

        public FixerRunDeclaration(string id, string question, string whyItMatters,
                                   IReadOnlyList<FixerDeclarationChoice> choices,
                                   string messageKind = "declare-load")
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("a declaration needs an id", nameof(id));
            if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("a declaration needs its question", nameof(question));
            if (choices == null || choices.Count == 0) throw new ArgumentException("a declaration needs choices", nameof(choices));
            if (string.IsNullOrWhiteSpace(messageKind)) throw new ArgumentException("a declaration needs its wire kind", nameof(messageKind));

            Id = id;
            Question = question;
            WhyItMatters = whyItMatters ?? "";
            Choices = choices;
            MessageKind = messageKind;
        }
    }

    /// <summary>
    /// A host-owned action a stage's panel offers — a button that sends one
    /// bare wire kind and nothing else. The founding case is stage 0 offering
    /// the full device picker: the page must not BE a picker (that is
    /// AudioDevicesDialog's job), but it must be able to hand the operator to
    /// the one the host owns without sending them hunting.
    /// </summary>
    public sealed class FixerHostAction
    {
        /// <summary>The wire kind, e.g. "open-device-picker". Must be one the
        /// host's message parser knows, or it will be refused and traced.</summary>
        public string MessageKind { get; }

        public string Label { get; }

        public FixerHostAction(string messageKind, string label)
        {
            if (string.IsNullOrWhiteSpace(messageKind)) throw new ArgumentException("a host action needs its wire kind", nameof(messageKind));
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("a host action needs its words", nameof(label));
            MessageKind = messageKind;
            Label = label;
        }
    }

    /// <summary>One answer a run declaration offers.</summary>
    public sealed class FixerDeclarationChoice
    {
        public string Id { get; }
        public string Label { get; }

        public FixerDeclarationChoice(string id, string label)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("a choice needs an id", nameof(id));
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("a choice needs its words", nameof(label));
            Id = id;
            Label = label;
        }
    }

    /// <summary>A host-supplied fix. Performs the change and reports what the
    /// setting became — the record the report needs, because a fix that is not
    /// recorded is a configuration that changed under the operator silently.</summary>
    public delegate FixerFixOutcome FixerFixAction();

    /// <summary>What a fix action did.</summary>
    public readonly struct FixerFixOutcome
    {
        public bool Succeeded { get; }

        /// <summary>What the setting became, in words — "WASAPI (Audient EVO8)".
        /// On failure, why it could not be done.</summary>
        public string WhatItBecame { get; }

        private FixerFixOutcome(bool ok, string became)
        { Succeeded = ok; WhatItBecame = became ?? ""; }

        public static FixerFixOutcome Done(string whatItBecame)
            => new FixerFixOutcome(true, whatItBecame);

        public static FixerFixOutcome Failed(string why)
            => new FixerFixOutcome(false, why);
    }
}
