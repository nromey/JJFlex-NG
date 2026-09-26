#nullable enable
using System;
using System.Collections.Generic;

namespace Radios
{
    /// <summary>
    /// How long the information in a message stays worth saying — the axis
    /// Noel ruled in #617, and the only axis this field carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Shelf life is not importance.</b> It is not verbosity, not priority,
    /// and not whether the operator asked. A forgettable message can be
    /// urgent at the moment it happens; a persistent one can be dull. The
    /// question this answers is only: if the sentence did not land, is saying
    /// it later still true?
    /// </para>
    /// <para>
    /// <b>There are exactly three values and there is no fourth.</b> A key
    /// with no classification is not "defaulted" to one of these — see
    /// <see cref="DeliveryClassification.Unclassified"/>, which is an error
    /// state. #617 named a wrong default applied to hundreds of strings at
    /// once as the expensive mistake here, so there is no default to be
    /// wrong.
    /// </para>
    /// <para>
    /// <b>Nothing here counts seconds.</b> No member of this type, and no
    /// field beside it, expresses an age. The condition a message names in
    /// <see cref="DeliveryDescriptor.Validity"/> decides whether saying it is
    /// still justified; a clock only schedules work. A TTL field in a
    /// delivery descriptor is forbidden by the ruling, not merely absent.
    /// </para>
    /// </remarks>
    public enum ShelfLife
    {
        /// <summary>
        /// True only in its moment. <i>"Reflected power is high, stopping
        /// transmit."</i> Replaying it after its premise ends is worse than
        /// silence, because the operator would act on a state that no longer
        /// holds — so the SENTENCE is withdrawn. The FACT does not perish: it
        /// becomes history, in honestly past-tense words under
        /// <see cref="DeliveryDescriptor.HistoryKey"/>.
        /// </summary>
        Perishable = 0,

        /// <summary>
        /// Still true in a minute, so saying it again is correct rather than
        /// stale. <i>"Your PA is at 70 degrees."</i> It keeps trying while its
        /// condition holds — the condition makes it true, never the clock, and
        /// no number of failed attempts retires it.
        /// </summary>
        Persistent = 1,

        /// <summary>
        /// One eligible presentation and no retained debt. <i>"PC audio
        /// on."</i> If it missed, it is dropped: it creates no entry in the
        /// undelivered list and no continuing obligation.
        /// </summary>
        Forgettable = 2,
    }

    /// <summary>
    /// What a lexicon entry says about itself as a unit of DELIVERY —
    /// distinct from what it says as text.
    /// </summary>
    /// <remarks>
    /// The three states are genuinely three. The difference between
    /// <see cref="Unclassified"/> and <see cref="TextOnly"/> is the whole
    /// point: one means nobody has said yet, the other means somebody looked
    /// and said this is not a message.
    /// </remarks>
    public enum DeliveryClassification
    {
        /// <summary>
        /// No <c>delivery</c> field at all. <b>An error state, not a fourth
        /// shelf life.</b> Every legacy entry is here until the classification
        /// pass reaches it, which is why the migration manifest exists; after
        /// migration, a key in this state fails the acceptance gate.
        /// <para>
        /// At run time it is handled rather than thrown away: an already
        /// admitted fact keeps its slot and stays reachable, and only the
        /// AUTOMATIC presentation is blocked. Losing a safety event because
        /// its metadata was missing would be the worse failure.
        /// </para>
        /// </summary>
        Unclassified = 0,

        /// <summary>
        /// <c>"delivery": null</c> — an affirmative statement that this string
        /// is interface text, a label or a fragment, and never a standalone
        /// thing the application says on its own account. Passing one of these
        /// to the standalone-message path is a classification error, not
        /// implicit forgettable speech.
        /// </summary>
        TextOnly = 1,

        /// <summary>
        /// <c>"delivery": { ... }</c> — a real standalone message, carrying
        /// <see cref="LexiconEntry.Delivery"/>.
        /// </summary>
        Message = 2,
    }

    /// <summary>
    /// Which established tone answers for an occurrence, when the sentence
    /// itself may not land. The earcon is the receipt (#617).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This set is deliberately closed, and deliberately small.</b> Astra's
    /// design says to reuse the established warning and capture tone
    /// identities to begin with, and that <i>a new vocabulary needs Noel's
    /// accessibility decision</i>. So nothing here invents a sound: these name
    /// tones that already exist and already mean something to the operator.
    /// <b>Adding a member is an accessibility decision, not a code change.</b>
    /// </para>
    /// <para>
    /// A policy names WHICH receipt, never WHETHER one was heard. The store
    /// records requested, playback reported, unavailable and suppressed
    /// separately, because a tone request is not a person hearing a tone.
    /// </para>
    /// <para>
    /// <b>The seam:</b> binding a policy to an actual sound belongs to the
    /// layer that owns the sounds (<c>EarconPlayer</c> and its catalog, in
    /// JJFlexWpf). Radios names the policy; it does not play anything.
    /// </para>
    /// </remarks>
    public enum ReceiptPolicy
    {
        /// <summary>
        /// Explicitly no receipt. An affirmative choice — a UI fragment or an
        /// operator-requested history reading must not manufacture an alarm.
        /// </summary>
        None = 0,

        /// <summary>The established warning tone identity.</summary>
        Warning = 1,

        /// <summary>The established capture tone identity.</summary>
        Capture = 2,
    }

    /// <summary>
    /// The delivery contract a classified message carries: how long it is
    /// worth saying, what makes it true, where its past tense lives, and which
    /// tone answers for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Per key, never per call site.</b> Two call sites may not give one
    /// key two answers — that is exactly what putting the classification
    /// beside the string in the JSON buys (#617). Where one key genuinely
    /// serves two incompatible meanings, the KEY is split and the text
    /// fragments are reused; the descriptor is not compromised.
    /// </para>
    /// <para>
    /// <b>Describing is not authorising.</b> A caller cannot obtain
    /// transmit-safety priority by choosing an urgent-sounding key, and shelf
    /// life grants no authority over any fact. Priority and producer
    /// capability come from the registered producer, never from here.
    /// </para>
    /// <para>
    /// <b>There is no age field and there must never be one.</b> See
    /// <see cref="ShelfLife"/>.
    /// </para>
    /// </remarks>
    public sealed class DeliveryDescriptor : IEquatable<DeliveryDescriptor>
    {
        public DeliveryDescriptor(
            ShelfLife shelfLife, string validity, string? historyKey, ReceiptPolicy receipt)
        {
            ShelfLife = shelfLife;
            Validity = validity ?? string.Empty;
            HistoryKey = string.IsNullOrEmpty(historyKey) ? null : historyKey;
            Receipt = receipt;
        }

        /// <summary>How long the information stays worth saying.</summary>
        public ShelfLife ShelfLife { get; }

        /// <summary>
        /// The registered typed condition contract that makes this message
        /// true, by name.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Required on every classified message, <b>including a forgettable
        /// one</b>: no replay policy makes an obsolete first presentation
        /// truthful, so even a message that will never be retried must be
        /// checked against its premise before it is said the first time.
        /// </para>
        /// <para>
        /// A genuinely invariant or request-scoped answer names
        /// <see cref="ValidityContracts.RequestScoped"/> explicitly rather
        /// than leaving this blank — "there is nothing to check" is a claim
        /// somebody should have to write down.
        /// </para>
        /// <para>
        /// <b>The JSON names the contract; it never evaluates it.</b> The
        /// executable evaluation stays with the domain owner that registered
        /// it, which is what stops a wording file from deciding whether a
        /// radio is transmitting.
        /// </para>
        /// </remarks>
        public string Validity { get; }

        /// <summary>
        /// The key of the distinct past-tense rendering used once this
        /// message's premise has ended, or null when it declares none.
        /// </summary>
        /// <remarks>
        /// Required for retained shelf lives, because an undelivered
        /// perishable event and a persistent condition that resolved unheard
        /// both have to be readable afterwards without replaying a present-
        /// tense assurance under a history heading. The key it names must
        /// itself be <see cref="DeliveryClassification.TextOnly"/> — history
        /// is read through the explicit fact-read path, never through an
        /// automatic current-message route.
        /// </remarks>
        public string? HistoryKey { get; }

        /// <summary>Which established tone answers for the occurrence.</summary>
        public ReceiptPolicy Receipt { get; }

        /// <summary>
        /// True when this message's information is retained after a failed
        /// attempt — perishable events keep their history, persistent
        /// conditions keep their obligation, forgettable ones keep nothing.
        /// </summary>
        public bool IsRetained => ShelfLife != ShelfLife.Forgettable;

        public bool Equals(DeliveryDescriptor? other)
        {
            if (other is null) return false;
            return ShelfLife == other.ShelfLife
                && string.Equals(Validity, other.Validity, StringComparison.Ordinal)
                && string.Equals(HistoryKey, other.HistoryKey, StringComparison.Ordinal)
                && Receipt == other.Receipt;
        }

        public override bool Equals(object? obj) => Equals(obj as DeliveryDescriptor);

        public override int GetHashCode() =>
            HashCode.Combine(ShelfLife, Validity, HistoryKey ?? string.Empty, Receipt);

        public override string ToString() =>
            ShelfLife.ToString().ToLowerInvariant()
            + ", validity " + Validity
            + (HistoryKey == null ? "" : ", history " + HistoryKey)
            + ", receipt " + Receipt.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// The condition-contract names the JSON is allowed to cite, and the one
    /// rule about adding to them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only <see cref="RequestScoped"/> is defined here, because it is the one
    /// contract that needs no domain to evaluate it. <b>Every other contract
    /// name belongs to the domain owner that can actually answer it</b> — the
    /// transmit-context owner, the alarm service, the capture owner — and is
    /// registered by that owner rather than listed in this file. A central
    /// list of contract names would be a second place for a domain's truth to
    /// live, and it would drift.
    /// </para>
    /// <para>
    /// Until those registrations exist, <see cref="Radios.Facts.ValidityRegistry"/>
    /// holds what has been registered at run time and the schema check reports
    /// a cited name it cannot find, rather than guessing.
    /// </para>
    /// </remarks>
    public static class ValidityContracts
    {
        /// <summary>
        /// The message answers a question that was asked, or states something
        /// that cannot stop being true — so its premise is its own asking.
        /// <para>
        /// Named explicitly rather than left blank so that "nothing to check
        /// here" is a written claim somebody can disagree with, instead of an
        /// absence that reads the same as an oversight.
        /// </para>
        /// </summary>
        public const string RequestScoped = "request-scoped";

        /// <summary>Is this a name this class itself defines?</summary>
        public static bool IsBuiltIn(string? name) =>
            string.Equals(name, RequestScoped, StringComparison.Ordinal);
    }

    /// <summary>
    /// One typed lookup result: the key, what it says, and the delivery
    /// contract it carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this type exists at all.</b> A string that has already come back
    /// from <see cref="Lexicon.Get(string)"/> has lost its key and its
    /// metadata, permanently. There is no recovering identity by matching text
    /// against the catalog, no second dictionary from sentence to descriptor,
    /// and no version of "change the JSON and speech behaviour changes" that
    /// works through the plain API. Producers that publish facts must carry
    /// THIS through the call chain instead.
    /// </para>
    /// <para>
    /// <b>Arguments travel with it, unrendered.</b> The plan a delivery
    /// coordinator eventually submits refers to a descriptor and its typed
    /// arguments, not to an immortal string, so the sentence can be rendered
    /// at the last moment against the catalog generation it was planned
    /// against.
    /// </para>
    /// </remarks>
    public sealed class LexiconMessage
    {
        internal LexiconMessage(
            string key,
            LexiconEntry? entry,
            IReadOnlyList<(string Name, object? Value)> arguments,
            long catalogGeneration)
        {
            Key = key;
            Entry = entry;
            Arguments = arguments;
            CatalogGeneration = catalogGeneration;
        }

        /// <summary>The key, which is the identity. Never lost.</summary>
        public string Key { get; }

        /// <summary>The entry behind the key, or null when the key is not loaded.</summary>
        public LexiconEntry? Entry { get; }

        /// <summary>Typed arguments, unrendered, for a placeholder fill at render time.</summary>
        public IReadOnlyList<(string Name, object? Value)> Arguments { get; }

        /// <summary>
        /// Which catalog this was looked up against. A wording reload advances
        /// it, so a plan prepared under an older generation can be re-rendered
        /// or invalidated rather than mixing two catalogs in one sentence.
        /// </summary>
        public long CatalogGeneration { get; }

        /// <summary>
        /// How this key is classified. An absent key reads as
        /// <see cref="DeliveryClassification.Unclassified"/> — the same
        /// handled-but-not-presented state as a key whose metadata is
        /// missing, because to the caller they are the same problem.
        /// </summary>
        public DeliveryClassification Classification =>
            Entry?.Classification ?? DeliveryClassification.Unclassified;

        /// <summary>The delivery contract, or null when there is none to have.</summary>
        public DeliveryDescriptor? Delivery => Entry?.Delivery;

        /// <summary>
        /// True only when this key is a classified standalone message.
        /// <b>Both other states are refusals</b>: unclassified blocks
        /// automatic presentation while keeping the fact, and text-only means
        /// somebody affirmatively said this string is not a message.
        /// </summary>
        public bool IsPresentableMessage => Classification == DeliveryClassification.Message;

        /// <summary>
        /// The rendered sentence at a tier, with arguments filled. Identical
        /// to what <see cref="Lexicon.Get(string, VerbosityLevel)"/> would
        /// return for the same key and tier — the text API and the typed API
        /// never disagree about words.
        /// </summary>
        public string Text(VerbosityLevel level)
        {
            string text = Entry?.Resolve(level) ?? Key;
            var args = Arguments;
            if (args.Count == 0) return text;

            var pairs = new (string Name, object? Value)[args.Count];
            for (int i = 0; i < args.Count; i++) pairs[i] = args[i];
            return Lexicon.Fill(text, pairs);
        }

        /// <summary>The rendered sentence at the chatty tier.</summary>
        public string Text() => Text(VerbosityLevel.Chatty);

        public override string ToString() =>
            Key + " [" + Classification.ToString().ToLowerInvariant()
            + (Delivery == null ? "" : ": " + Delivery) + "]";
    }
}
