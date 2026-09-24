#nullable enable
using System;
using System.Collections.Generic;

namespace Radios.Facts
{
    /// <summary>
    /// One retained episode: what happened, what makes it true, what has been
    /// tried, and what is still owed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The fact is not the sentence.</b> It survives the wording, the
    /// selected radio, the reader binding and every delivery attempt. The
    /// sentence is rendered from a descriptor and a current snapshot at the
    /// moment an attempt starts; what is stored here is the information, not a
    /// cached string. That is why a persistent temperature condition does not
    /// make its old exact number timeless — the continuing fact is the
    /// established condition, and the number belongs to the observation that
    /// produced it.
    /// </para>
    /// </remarks>
    public sealed class Fact
    {
        private readonly List<DeliveryAttempt> _attempts = new();

        internal Fact(
            FactIdentity identity,
            string messageKey,
            DeliveryDescriptor? delivery,
            DeliveryClassification classification,
            ValiditySnapshot validity,
            FactProvenance provenance,
            long admittedAtQuietGeneration)
        {
            Identity = identity;
            MessageKey = messageKey;
            Delivery = delivery;
            Classification = classification;
            Validity = validity;
            Provenance = provenance;
            AdmittedAtQuietGeneration = admittedAtQuietGeneration;
            Revision = 1;
            MaterialRevision = 1;
        }

        public FactIdentity Identity { get; }

        /// <summary>The lexicon key. The identity of the wording, never the wording itself.</summary>
        public string MessageKey { get; }

        /// <summary>
        /// The delivery contract, or null when the key was unclassified or
        /// text-only at admission. A null here does NOT discard the fact — see
        /// <see cref="Classification"/>.
        /// </summary>
        public DeliveryDescriptor? Delivery { get; private set; }

        /// <summary>
        /// How the key was classified when this fact was admitted.
        /// </summary>
        /// <remarks>
        /// <b>Pinned at admission on purpose.</b> A classification changed by a
        /// later software version must not retroactively reinterpret evidence
        /// already stored: a fact that was persistent when it happened is read
        /// back as persistent, whatever the catalog says now.
        /// </remarks>
        public DeliveryClassification Classification { get; private set; }

        /// <summary>Which catalog generation the classification was read from.</summary>
        public long CatalogGeneration { get; internal set; }

        /// <summary>Is saying this still justified?</summary>
        public ValiditySnapshot Validity { get; private set; }

        /// <summary>Where the observation came from.</summary>
        public FactProvenance Provenance { get; private set; }

        /// <summary>
        /// Strictly increasing. Every accepted update advances it, including a
        /// fresh sample that changes nothing anyone needs to hear.
        /// </summary>
        public long Revision { get; private set; }

        /// <summary>
        /// Advances only when the INFORMATION changed — a correction, a
        /// material outcome, a genuinely new clause.
        /// </summary>
        /// <remarks>
        /// The two revisions are separate because a fresh sample must be able
        /// to update a value without manufacturing an incident, undoing a
        /// silence or replenishing a delivery allowance. Review is scoped to a
        /// material revision, so a cosmetic update cannot make an
        /// already-reviewed fact pending again, and a real correction can.
        /// </remarks>
        public long MaterialRevision { get; private set; }

        /// <summary>
        /// The quiet cohort this fact was admitted under. An event captured
        /// before a barrier inherits its pause even if it arrives after —
        /// <b>arriving late cannot earn fresh permission</b>.
        /// </summary>
        public long AdmittedAtQuietGeneration { get; }

        /// <summary>Which material revision the operator has explicitly reviewed, or zero.</summary>
        public long ReviewedMaterialRevision { get; private set; }

        /// <summary>What is known about the earcon.</summary>
        public ReceiptState Receipt { get; private set; } = ReceiptState.NotRequested;

        /// <summary>The receipt token for this occurrence — issued once, never replayed per retry.</summary>
        public string? ReceiptToken { get; private set; }

        /// <summary>Automatic delivery is paused until an explicit read or resume.</summary>
        public bool AutomaticPaused { get; private set; }

        /// <summary>Why it is paused, for the detail view.</summary>
        public string? PauseReason { get; private set; }

        /// <summary>The attempts this fact retains evidence for, newest last.</summary>
        public IReadOnlyList<DeliveryAttempt> Attempts => _attempts;

        /// <summary>
        /// Attempts whose evidence has been coalesced away by
        /// <see cref="FactStoreCapacity.MaxAttemptEvidence"/>. Never decreases.
        /// </summary>
        public int CompactedAttempts { get; private set; }

        /// <summary>Detail text, already bounded. May be empty.</summary>
        public string Detail { get; private set; } = string.Empty;

        /// <summary>True when the detail was truncated, said out loud rather than hidden.</summary>
        public bool DetailTruncated { get; private set; }

        /// <summary>Restored from a previous process's shard: history, never live authority.</summary>
        public bool RestoredFromDisk { get; internal set; }

        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// True when this fact's required detail has no confirmed complete
        /// presentation for its current material revision.
        /// </summary>
        /// <remarks>
        /// <b>This is the operator's default list, in one property.</b> Not
        /// attempted, deferred, refused, partial, completion unknown,
        /// observation unknown and deliberately paused all answer true;
        /// a tracked completion of a plan that carried the required detail is
        /// the only thing that answers false.
        /// </remarks>
        public bool HasUndeliveredDetail
        {
            get
            {
                // A forgettable message creates no continuing debt. One
                // eligible presentation, and a miss is a miss.
                if (Delivery != null && Delivery.ShelfLife == ShelfLife.Forgettable) return false;

                for (int i = _attempts.Count - 1; i >= 0; i--)
                {
                    DeliveryAttempt attempt = _attempts[i];
                    if (attempt.MaterialRevision != MaterialRevision) continue;
                    if (attempt.Discharges) return false;
                }
                return true;
            }
        }

        /// <summary>
        /// True when the operator has explicitly reviewed the material
        /// revision now current. Reviewing is an ACTION — opening the window,
        /// moving focus, pressing Ctrl or asking for a replay acknowledges
        /// nothing.
        /// </summary>
        public bool Reviewed => ReviewedMaterialRevision >= MaterialRevision && ReviewedMaterialRevision > 0;

        /// <summary>
        /// True when this belongs on the pending list: something is owed and
        /// nobody has looked at it.
        /// </summary>
        public bool IsPending => HasUndeliveredDetail && !Reviewed;

        /// <summary>
        /// True when this fact is now historical — its premise ended, however
        /// it ended.
        /// </summary>
        /// <remarks>
        /// <b>Disappearance of a condition does not prove delivery.</b> An
        /// unheard persistent condition that resolves becomes historical detail
        /// that is still owed, which is exactly the case that used to vanish.
        /// </remarks>
        public bool IsHistorical =>
            Validity.State == ValidityState.Ended || Validity.State == ValidityState.Superseded;

        // ────────────────────────────────────────────────────────────────
        //  Mutation — only ever from FactStore, only ever with a capability
        // ────────────────────────────────────────────────────────────────

        internal void ApplyValidity(ValiditySnapshot snapshot, FactProvenance provenance, bool material)
        {
            Validity = snapshot;
            Provenance = provenance;
            Revision++;
            if (material) MaterialRevision++;
        }

        internal void ApplyDetail(string? detail)
        {
            string text = detail ?? string.Empty;
            if (System.Text.Encoding.UTF8.GetByteCount(text) <= FactStoreCapacity.MaxDetailBytes)
            {
                Detail = text;
                DetailTruncated = false;
                return;
            }

            // Truncate, and SAY SO. A silently shortened detail reads as the
            // whole of what was known.
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            int cut = FactStoreCapacity.MaxDetailBytes;
            while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;   // never split a character
            Detail = System.Text.Encoding.UTF8.GetString(bytes, 0, cut);
            DetailTruncated = true;
        }

        internal void ApplyClassification(DeliveryClassification classification, DeliveryDescriptor? delivery)
        {
            Classification = classification;
            Delivery = delivery;
        }

        internal void RecordAttempt(DeliveryAttempt attempt)
        {
            _attempts.Add(attempt);
            while (_attempts.Count > FactStoreCapacity.MaxAttemptEvidence)
            {
                _attempts.RemoveAt(0);
                CompactedAttempts++;
            }
        }

        internal void RecordReceipt(ReceiptState state, string? token)
        {
            Receipt = state;
            if (token != null && ReceiptToken == null) ReceiptToken = token;
        }

        internal void Pause(string reason)
        {
            AutomaticPaused = true;
            PauseReason = reason;
        }

        internal void Resume()
        {
            AutomaticPaused = false;
            PauseReason = null;
        }

        internal void MarkReviewed(long materialRevision)
        {
            if (materialRevision > ReviewedMaterialRevision) ReviewedMaterialRevision = materialRevision;
        }

        public override string ToString() =>
            Identity.EpisodeId + " [" + MessageKey + ", " + Validity
            + (IsPending ? ", pending" : string.Empty) + "]";
    }
}
