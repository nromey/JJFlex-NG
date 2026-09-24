#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Radios.Facts
{
    /// <summary>
    /// The journal's on-disk shape, both directions. Everything read is
    /// validated before it is believed.
    /// </summary>
    internal static class FactJournalFormat
    {
        // ────────────────────────────────────────────────────────────────
        //  Render
        // ────────────────────────────────────────────────────────────────

        public static string Render(StoreImage image, long generation)
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("schema", FactJournal.SchemaVersion);
                w.WriteString("writer", image.Writer.ToString("N"));
                w.WriteNumber("generation", generation);
                w.WriteNumber("mutation", image.Mutation);

                w.WriteStartObject("compacted");
                w.WriteNumber("covered", image.CompactedCovered);
                w.WriteNumber("reviewedOnly", image.CompactedReviewedOnly);
                w.WriteNumber("forgettableUnpresented", image.CompactedForgettable);
                w.WriteBoolean("lowerBound", image.CompactedLowerBound);
                w.WriteEndObject();

                w.WriteStartArray("facts");
                foreach (FactRecord r in image.Facts) WriteFact(w, r);
                w.WriteEndArray();

                w.WriteStartArray("overlays");
                foreach (OverlayImage o in image.Overlays)
                {
                    w.WriteStartObject();
                    WriteEpisode(w, "episode", o.Episode);
                    w.WriteString("content", o.ContentFingerprint);
                    WriteIds(w, "reviewed", o.Reviewed);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("continuity");
                foreach (ContinuityRecord c in image.Continuity)
                {
                    w.WriteStartObject();
                    w.WriteString("radio", c.Radio);
                    w.WriteString("condition", c.Condition);
                    w.WriteString("contract", c.Contract);
                    w.WriteNumber("definitionRevision", c.DefinitionRevision);
                    w.WriteString("pause", c.Pause.ToString());
                    WriteValues(w, "baseline", c.Baseline);
                    if (c.LastEpisode is EpisodeId last) WriteEpisode(w, "lastEpisode", last);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteStartArray("issues");
                foreach (IssueRecord i in image.Issues)
                {
                    w.WriteStartObject();
                    w.WriteString("kind", i.Kind.ToString());
                    w.WriteString("sourceKey", i.SourceKey);
                    w.WriteString("source", i.Source);
                    w.WriteString("reason", i.Reason);
                    w.WriteNumber("revision", i.Revision);
                    w.WriteNumber("reviewedRevision", i.ReviewedRevision);
                    w.WriteString("state", i.State.ToString());
                    w.WriteNumber("count", i.Count);
                    w.WriteString("extent", i.Extent.ToString());
                    WriteStrings(w, "exemplars", i.Exemplars);
                    w.WriteBoolean("exemplarsOverflowed", i.ExemplarsOverflowed);
                    WriteStrings(w, "seen", i.Seen.OrderBy(s => s, StringComparer.Ordinal));
                    w.WriteBoolean("seenOverflowed", i.SeenOverflowed);
                    w.WriteEndObject();
                }
                w.WriteEndArray();

                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static void WriteFact(Utf8JsonWriter w, FactRecord r)
        {
            w.WriteStartObject();
            WriteEpisode(w, "episode", r.Id);
            w.WriteNumber("revision", r.Revision);
            w.WriteString("owner", r.OwnerName);
            w.WriteStartObject("contract");
            w.WriteString("name", r.ContractName);
            w.WriteNumber("revision", r.ContractRevision);
            w.WriteEndObject();
            w.WriteStartObject("condition");
            w.WriteString("name", r.Condition.Condition);
            w.WriteNumber("definitionRevision", r.Condition.DefinitionRevision);
            w.WriteEndObject();
            w.WriteString("claim", r.Claim);
            w.WriteStartObject("scope");
            w.WriteNumber("id", r.ScopeId);
            w.WriteString("kind", r.ScopeKind.ToString());
            w.WriteString("radio", r.RadioIdentity);
            w.WriteEndObject();
            w.WriteString("occurrence", r.OccurrenceLabel);
            w.WriteString("priority", r.Priority.ToString());

            w.WriteStartObject("message");
            w.WriteString("key", r.MessageKey);
            w.WriteString("classification", r.Classification.ToString());
            w.WriteNumber("catalogGeneration", r.CatalogGeneration);
            if (r.Delivery == null) w.WriteNull("delivery");
            else
            {
                w.WriteStartObject("delivery");
                w.WriteString("shelfLife", r.Delivery.ShelfLife.ToString());
                w.WriteString("validity", r.Delivery.Validity);
                w.WriteString("historyKey", r.Delivery.HistoryKey);
                w.WriteString("receipt", r.Delivery.Receipt.ToString());
                w.WriteEndObject();
            }
            w.WriteEndObject();

            w.WriteStartObject("validity");
            w.WriteString("state", r.Validity.State.ToString());
            w.WriteString("ended", r.Validity.Ended?.ToString());
            w.WriteString("unknown", r.Validity.Unknown?.ToString());
            if (r.Validity.SupersededByRevision is long s) w.WriteNumber("supersededBy", s); else w.WriteNull("supersededBy");
            w.WriteString("note", r.Validity.Note);
            w.WriteString("asOfUtc", Time(r.Validity.AsOfUtc));
            w.WriteEndObject();

            w.WriteString("observedUtc", Time(r.ObservedUtc));
            w.WriteNumber("lastEventSequence", r.LastEventSequence);
            w.WriteNumber("observationRevision", r.ObservationRevision);
            w.WriteNumber("materialRevision", r.MaterialRevision);
            WriteValues(w, "current", r.CurrentValues);
            WriteValues(w, "baseline", r.Baseline);

            w.WriteStartArray("events");
            foreach (EventRecord e in r.Events)
            {
                w.WriteStartObject();
                w.WriteNumber("ordinal", e.EventOrdinal);
                w.WriteNumber("sequence", e.Sequence);
                w.WriteString("source", e.SourceEventId);
                w.WriteString("observedUtc", Time(e.ObservedUtc));
                w.WriteString("effect", e.Effect);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("eventsDropped", r.HistoryDropped);

            w.WriteStartArray("materials");
            foreach (MaterialUnit m in r.Materials)
            {
                w.WriteStartObject();
                w.WriteNumber("id", m.Id);
                w.WriteString("name", m.Name);
                WriteValue(w, "value", m.Value);
                w.WriteString("kind", m.Kind.ToString());
                if (m.Supersedes is long sup) w.WriteNumber("supersedes", sup); else w.WriteNull("supersedes");
                if (m.RelatesTo is long rel) w.WriteNumber("relatesTo", rel); else w.WriteNull("relatesTo");
                w.WriteNumber("introducedAt", m.IntroducedAtRevision);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("nextMaterial", r.NextMaterialId);

            w.WriteStartObject("coverage");
            WriteIds(w, "presented", r.CoveredLedger.OrderBy(x => x));
            WriteIds(w, "reviewed", r.ReviewedLocal.OrderBy(x => x));
            w.WriteEndObject();
            w.WriteString("pause", r.RestoredPause.ToString());
            w.WriteBoolean("legacyUnverified", r.LegacyUnverifiedOwed);

            w.WriteStartArray("attempts");
            foreach (AttemptRecord a in r.Attempts)
            {
                w.WriteStartObject();
                w.WriteStartObject("attempt");
                w.WriteString("writer", a.Id.Writer.ToString("N"));
                w.WriteNumber("ordinal", a.Id.Ordinal);
                w.WriteEndObject();
                w.WriteNumber("plan", a.PlanId);
                w.WriteString("kind", a.Kind.ToString());
                w.WriteBoolean("historical", a.HistoricalPlan);
                w.WriteString("rendering", a.Rendering);
                w.WriteStartArray("material");
                foreach (var pair in a.PlanMaterial.OrderBy(p => p.Key))
                {
                    w.WriteStartObject();
                    w.WriteNumber("id", pair.Key);
                    w.WriteString("name", pair.Value);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                if (a.GrantId is long g) w.WriteNumber("grant", g); else w.WriteNull("grant");
                w.WriteString("binding", a.BindingName);
                w.WriteString("ticket", a.Ticket);
                w.WriteBoolean("consumed", a.Consumed);
                w.WriteBoolean("authorized", a.Authorized);
                w.WriteString("notStarted", a.NotStarted?.ToString());
                w.WriteBoolean("interrupted", a.Interrupted);
                w.WriteBoolean("cancelRequested", a.CancellationRequested);
                w.WriteString("cancelOperation", a.CancelOperation);
                w.WriteStartArray("evidence");
                foreach (TransportEvidence e in a.Evidence.Values)
                {
                    w.WriteStartObject();
                    w.WriteNumber("seq", e.AdapterSequence);
                    w.WriteString("kind", e.Kind.ToString());
                    w.WriteString("ticket", e.Ticket);
                    WriteStrings(w, "segments", e.Segments);
                    w.WriteString("cause", e.Cause?.ToString());
                    w.WriteString("operation", e.Operation);
                    w.WriteString("reason", e.Reason);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("compactedAttempts", r.CompactedAttempts);
            w.WriteBoolean("compactedLowerBound", r.CompactedAttemptsLowerBound);

            w.WriteStartObject("receipt");
            w.WriteString("policy", r.Receipt.Policy.ToString());
            w.WriteString("state", r.Receipt.State.ToString());
            w.WriteBoolean("consumed", r.Receipt.Consumed);
            w.WriteNumber("id", r.Receipt.ReceiptId);
            w.WriteEndObject();

            w.WriteString("detail", r.Detail);
            w.WriteBoolean("detailTruncated", r.DetailTruncated);
            w.WriteEndObject();
        }

        private static void WriteEpisode(Utf8JsonWriter w, string name, EpisodeId id)
        {
            w.WriteStartObject(name);
            w.WriteString("writer", id.Writer.ToString("N"));
            w.WriteNumber("ordinal", id.Ordinal);
            w.WriteString("origin", id.Origin.ToString());
            w.WriteEndObject();
        }

        private static void WriteValue(Utf8JsonWriter w, string name, FactValue v)
        {
            w.WriteStartObject(name);
            w.WriteString("kind", v.Kind.ToString());
            w.WriteString("v", v.Invariant);
            w.WriteEndObject();
        }

        private static void WriteValues(Utf8JsonWriter w, string name, FactObservation values)
        {
            w.WriteStartObject(name);
            foreach (var pair in values.Values) WriteValue(w, pair.Key, pair.Value);
            w.WriteEndObject();
        }

        private static void WriteIds(Utf8JsonWriter w, string name, IEnumerable<long> ids)
        {
            w.WriteStartArray(name);
            foreach (long id in ids) w.WriteNumberValue(id);
            w.WriteEndArray();
        }

        private static void WriteStrings(Utf8JsonWriter w, string name, IEnumerable<string> values)
        {
            w.WriteStartArray(name);
            foreach (string v in values) w.WriteStringValue(v);
            w.WriteEndArray();
        }

        private static string Time(DateTime t) => t.ToString("o", CultureInfo.InvariantCulture);

        // ────────────────────────────────────────────────────────────────
        //  Parse
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Read a source. Throws for a file that cannot be believed as a
        /// whole; records that fail validation individually are left out and
        /// listed in <paramref name="rejected"/>, never half-built into a
        /// convincing fact.
        /// </summary>
        public static LoadedSource Parse(string json, string sourceName, out List<string> rejected)
        {
            rejected = new List<string>();
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("not an object");
            if (!root.TryGetProperty("schema", out JsonElement schema) || schema.ValueKind != JsonValueKind.Number)
                throw new InvalidDataException("no schema version");

            int version = schema.GetInt32();
            if (version == 1) return ParseLegacy(root, json, sourceName, rejected);
            if (version != FactJournal.SchemaVersion)
                throw new UnsupportedSchemaException("schema " + version + " is not one this version reads");

            var source = new LoadedSource
            {
                Name = sourceName,
                HeaderWriter = RequireGuid(root, "writer"),
                Generation = RequireLong(root, "generation", min: 1),
            };

            if (root.TryGetProperty("compacted", out JsonElement compacted) && compacted.ValueKind == JsonValueKind.Object)
            {
                source.CompactedCovered = OptionalLong(compacted, "covered");
                source.CompactedReviewedOnly = OptionalLong(compacted, "reviewedOnly");
                source.CompactedForgettable = OptionalLong(compacted, "forgettableUnpresented");
            }

            var seen = new HashSet<EpisodeId>();
            foreach (JsonElement element in RequireArray(root, "facts").EnumerateArray())
            {
                try
                {
                    FactRecord record = ReadFact(element, source);
                    if (!seen.Add(record.Id)) throw new InvalidDataException("the same episode appears twice in one file");
                    source.Facts.Add(record);
                }
                catch (Exception ex) when (ex is InvalidDataException or FormatException or KeyNotFoundException
                                            or InvalidOperationException or ArgumentException)
                {
                    rejected.Add(sourceName + ": " + ex.Message);
                }
            }

            if (root.TryGetProperty("overlays", out JsonElement overlays) && overlays.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement o in overlays.EnumerateArray())
                {
                    try
                    {
                        source.Overlays.Add(new OverlayImage
                        {
                            Episode = ReadEpisode(Require(o, "episode")),
                            ContentFingerprint = RequireString(o, "content"),
                            Reviewed = ReadIds(o, "reviewed"),
                        });
                    }
                    catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException)
                    {
                        rejected.Add(sourceName + " overlay: " + ex.Message);
                    }
                }
            }

            if (root.TryGetProperty("continuity", out JsonElement continuity) && continuity.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement c in continuity.EnumerateArray())
                {
                    try
                    {
                        source.Continuity.Add(new ContinuityRecord
                        {
                            Radio = RequireString(c, "radio"),
                            Condition = RequireString(c, "condition"),
                            Contract = RequireString(c, "contract"),
                            DefinitionRevision = (int)RequireLong(c, "definitionRevision", min: 0),
                            Pause = RequireEnum<PauseCause>(c, "pause"),
                            Baseline = ReadValues(Require(c, "baseline")),
                            LastEpisode = c.TryGetProperty("lastEpisode", out JsonElement le) ? ReadEpisode(le) : null,
                        });
                    }
                    catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException)
                    {
                        rejected.Add(sourceName + " continuity: " + ex.Message);
                    }
                }
            }

            if (root.TryGetProperty("issues", out JsonElement issues) && issues.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement i in issues.EnumerateArray())
                {
                    try
                    {
                        var issue = new IssueRecord
                        {
                            Kind = RequireEnum<IssueKind>(i, "kind"),
                            SourceKey = RequireString(i, "sourceKey"),
                            Source = OptionalString(i, "source") ?? string.Empty,
                            Reason = OptionalString(i, "reason") ?? string.Empty,
                            Revision = RequireLong(i, "revision", min: 0),
                            ReviewedRevision = RequireLong(i, "reviewedRevision", min: 0),
                            State = RequireEnum<IssueState>(i, "state"),
                            Count = RequireLong(i, "count", min: 0),
                            Extent = RequireEnum<ExtentCertainty>(i, "extent"),
                            ExemplarsOverflowed = OptionalBool(i, "exemplarsOverflowed"),
                            SeenOverflowed = OptionalBool(i, "seenOverflowed"),
                        };
                        foreach (string e in ReadStrings(i, "exemplars").Take(FactStoreCapacity.MaxIssueExemplars)) issue.Exemplars.Add(e);
                        foreach (string s in ReadStrings(i, "seen").Take(FactStoreCapacity.MaxIssueDedupeKeys)) issue.Seen.Add(s);
                        if (issue.ReviewedRevision > issue.Revision) throw new InvalidDataException("reviewed beyond its revision");
                        source.Issues.Add(issue);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException)
                    {
                        rejected.Add(sourceName + " issue: " + ex.Message);
                    }
                }
            }

            return source;
        }

        private static FactRecord ReadFact(JsonElement e, LoadedSource source)
        {
            EpisodeId id = ReadEpisode(Require(e, "episode"));
            JsonElement contract = Require(e, "contract");
            JsonElement condition = Require(e, "condition");
            JsonElement scope = Require(e, "scope");
            JsonElement message = Require(e, "message");
            JsonElement validity = Require(e, "validity");

            var record = new FactRecord
            {
                Id = id,
                Revision = RequireLong(e, "revision", min: 1),
                OwnerName = RequireString(e, "owner"),
                ContractName = RequireString(contract, "name"),
                ContractRevision = (int)RequireLong(contract, "revision", min: int.MinValue),
                Condition = new ConditionKey(RequireString(condition, "name"),
                                             (int)RequireLong(condition, "definitionRevision", min: int.MinValue)),
                Claim = RequireString(e, "claim"),
                ScopeId = RequireLong(scope, "id", min: 0),
                ScopeKind = RequireEnum<FactScopeKind>(scope, "kind"),
                RadioIdentity = OptionalString(scope, "radio"),
                OccurrenceLabel = OptionalString(e, "occurrence"),
                Priority = RequireEnum<DeliveryPriority>(e, "priority"),
                MessageKey = RequireString(message, "key"),
                Classification = RequireEnum<DeliveryClassification>(message, "classification"),
                CatalogGeneration = RequireLong(message, "catalogGeneration", min: long.MinValue),
                ObservedUtc = RequireTime(e, "observedUtc"),
                LastEventSequence = RequireLong(e, "lastEventSequence", min: 0),
                ObservationRevision = RequireLong(e, "observationRevision", min: 0),
                MaterialRevision = RequireLong(e, "materialRevision", min: 0),
                CurrentValues = ReadValues(Require(e, "current")),
                Baseline = ReadValues(Require(e, "baseline")),
                HistoryDropped = RequireLong(e, "eventsDropped", min: 0),
                NextMaterialId = RequireLong(e, "nextMaterial", min: 1),
                RestoredPause = RequireEnum<PauseCause>(e, "pause"),
                LegacyUnverifiedOwed = OptionalBool(e, "legacyUnverified"),
                CompactedAttempts = RequireLong(e, "compactedAttempts", min: 0),
                CompactedAttemptsLowerBound = OptionalBool(e, "compactedLowerBound"),
                Detail = OptionalString(e, "detail") ?? string.Empty,
                DetailTruncated = OptionalBool(e, "detailTruncated"),
                Restored = true,
            };

            if (Encoding.UTF8.GetByteCount(record.Detail) > FactStoreCapacity.MaxDetailBytes)
                throw new InvalidDataException("detail longer than any writer could have saved");

            JsonElement delivery = Require(message, "delivery");
            if (delivery.ValueKind == JsonValueKind.Object)
            {
                record.Delivery = new DeliveryDescriptor(
                    RequireEnum<ShelfLife>(delivery, "shelfLife"),
                    RequireString(delivery, "validity"),
                    OptionalString(delivery, "historyKey"),
                    RequireEnum<ReceiptPolicy>(delivery, "receipt"));
            }
            if ((record.Classification == DeliveryClassification.Message) != (record.Delivery != null))
                throw new InvalidDataException("classification and descriptor disagree");

            record.Validity = ReadValidity(validity);

            foreach (JsonElement ev in RequireArray(e, "events").EnumerateArray().Take(FactStoreCapacity.MaxEventHistoryPerFact))
            {
                record.Events.Add(new EventRecord
                {
                    EventOrdinal = RequireLong(ev, "ordinal", min: 1),
                    Sequence = RequireLong(ev, "sequence", min: 0),
                    SourceEventId = OptionalString(ev, "source"),
                    ObservedUtc = RequireTime(ev, "observedUtc"),
                    Effect = OptionalString(ev, "effect") ?? string.Empty,
                });
            }

            var materialIds = new HashSet<long>();
            foreach (JsonElement m in RequireArray(e, "materials").EnumerateArray())
            {
                long mid = RequireLong(m, "id", min: 1);
                if (!materialIds.Add(mid)) throw new InvalidDataException("duplicate material identity");
                if (mid >= record.NextMaterialId) throw new InvalidDataException("material identity beyond its allocator");
                long? sup = OptionalNullableLong(m, "supersedes");
                long? rel = OptionalNullableLong(m, "relatesTo");
                record.Materials.Add(new MaterialUnit(mid, RequireString(m, "name"), ReadValue(Require(m, "value")),
                    RequireEnum<MaterialKind>(m, "kind"), sup, rel, RequireLong(m, "introducedAt", min: 0)));
            }
            if (record.Materials.Count == 0) throw new InvalidDataException("a fact with no information");
            if (record.Materials.Count > FactStoreCapacity.MaxMaterialUnitsPerFact) throw new InvalidDataException("too many units");
            foreach (MaterialUnit m in record.Materials)
            {
                if (m.Supersedes is long s && !materialIds.Contains(s)) throw new InvalidDataException("supersedes a unit that is not here");
                if (m.RelatesTo is long r && !materialIds.Contains(r)) throw new InvalidDataException("relates to a unit that is not here");
            }

            JsonElement coverage = Require(e, "coverage");
            foreach (long c in ReadIds(coverage, "presented"))
            {
                if (!materialIds.Contains(c)) throw new InvalidDataException("coverage names a unit that is not here");
                record.CoveredLedger.Add(c);
            }
            foreach (long c in ReadIds(coverage, "reviewed"))
            {
                if (!materialIds.Contains(c)) throw new InvalidDataException("review names a unit that is not here");
                record.ReviewedImported.Add(c);   // the origin writer's own review, imported as history
            }

            int attempts = 0;
            foreach (JsonElement a in RequireArray(e, "attempts").EnumerateArray())
            {
                if (++attempts > FactStoreCapacity.MaxAttemptEvidence + FactStoreCapacity.MaxAttemptTombstones)
                    throw new InvalidDataException("more attempts than any writer retains");
                record.Attempts.Add(ReadAttempt(a, record, materialIds));
            }

            JsonElement receipt = Require(e, "receipt");
            record.Receipt = new ReceiptRecord
            {
                Policy = RequireEnum<ReceiptPolicy>(receipt, "policy"),
                State = RequireEnum<ReceiptState>(receipt, "state"),
                Consumed = OptionalBool(receipt, "consumed"),
                ReceiptId = RequireLong(receipt, "id", min: 0),
                FromPreviousProcess = true,
            };
            // A claim or a boundary crossed and never reported is, after a
            // restart, an unknown outcome — never a replay permit.
            if (record.Receipt.State == ReceiptState.Claimed)
            {
                record.Receipt.State = ReceiptState.OutcomeUnknown;
                record.Receipt.Consumed = true;
            }

            record.Origin = new HistoricalOrigin
            {
                Source = source.Name,
                Generation = source.Generation,
                HeaderWriter = source.HeaderWriter,
                RecordFingerprint = FactHash.Of(e.GetRawText()),
            };
            return record;
        }

        private static AttemptRecord ReadAttempt(JsonElement a, FactRecord owner, HashSet<long> materialIds)
        {
            JsonElement id = Require(a, "attempt");
            var attempt = new AttemptRecord
            {
                Id = new AttemptId(RequireGuid(id, "writer"), RequireLong(id, "ordinal", min: 1)),
                Fact = owner,
                PlanId = RequireLong(a, "plan", min: 0),
                Kind = RequireEnum<PlanRequestKind>(a, "kind"),
                HistoricalPlan = OptionalBool(a, "historical"),
                Rendering = OptionalString(a, "rendering") ?? string.Empty,
                GrantId = OptionalNullableLong(a, "grant"),
                BindingName = OptionalString(a, "binding"),
                Ticket = OptionalString(a, "ticket"),
                Consumed = OptionalBool(a, "consumed"),
                Authorized = OptionalBool(a, "authorized"),
                NotStarted = OptionalString(a, "notStarted") is string ns ? ParseEnum<NotStartedReason>(ns) : null,
                Interrupted = OptionalBool(a, "interrupted"),
                CancellationRequested = OptionalBool(a, "cancelRequested"),
                CancelOperation = OptionalString(a, "cancelOperation"),
                FromPreviousProcess = true,
            };
            foreach (JsonElement m in RequireArray(a, "material").EnumerateArray())
            {
                long mid = RequireLong(m, "id", min: 1);
                if (!materialIds.Contains(mid)) throw new InvalidDataException("an attempt names a unit that is not here");
                attempt.PlanMaterial[mid] = RequireString(m, "name");
            }
            foreach (JsonElement ev in RequireArray(a, "evidence").EnumerateArray())
            {
                long seq = RequireLong(ev, "seq", min: 0);
                EvidenceKind kind = RequireEnum<EvidenceKind>(ev, "kind");
                string? ticket = OptionalString(ev, "ticket");
                TransportEvidence evidence = kind switch
                {
                    EvidenceKind.RequestIssued => TransportEvidence.RequestIssued(seq, ticket),
                    EvidenceKind.RequestThrew => TransportEvidence.RequestThrew(seq, OptionalString(ev, "reason") ?? "", ticket),
                    EvidenceKind.BackendAccepted => TransportEvidence.BackendAccepted(seq, ticket),
                    EvidenceKind.BackendRefused => TransportEvidence.BackendRefused(seq, OptionalString(ev, "reason"), ticket),
                    EvidenceKind.Progress => TransportEvidence.Progress(seq, ReadStrings(ev, "segments").ToArray(), ticket),
                    EvidenceKind.Completed => TransportEvidence.Completed(seq, ticket),
                    EvidenceKind.CancellationRequested => TransportEvidence.CancellationRequested(seq,
                        OptionalString(ev, "operation") ?? "", OptionalString(ev, "reason") ?? "", ticket),
                    EvidenceKind.Cancelled => TransportEvidence.Cancelled(seq,
                        ParseEnum<CancelCause>(OptionalString(ev, "cause") ?? "Unknown"), OptionalString(ev, "operation"), ticket),
                    _ => TransportEvidence.CompletionUnobservable(seq, ticket),
                };
                if (attempt.Evidence.ContainsKey(seq)) throw new InvalidDataException("duplicate adapter sequence");
                attempt.Evidence.Add(seq, evidence);
            }
            return attempt;
        }

        private static ValiditySnapshot ReadValidity(JsonElement v)
        {
            ValidityState state = RequireEnum<ValidityState>(v, "state");
            DateTime asOf = RequireTime(v, "asOfUtc");
            string? note = OptionalString(v, "note");
            return state switch
            {
                ValidityState.Current => ValiditySnapshot.Establish(asOf, note),
                ValidityState.Ended => ValiditySnapshot.End(ParseEnum<EndedKind>(RequireString(v, "ended")), asOf, note),
                ValidityState.Unknown => ValiditySnapshot.NotKnown(ParseEnum<UnknownReason>(RequireString(v, "unknown")), asOf, note),
                _ => ValiditySnapshot.Superseded(OptionalNullableLong(v, "supersededBy") ?? throw new InvalidDataException("superseded by nothing"), asOf, note),
            };
        }

        private static EpisodeId ReadEpisode(JsonElement e) =>
            new EpisodeId(RequireGuid(e, "writer"), RequireLong(e, "ordinal", min: 1), RequireEnum<EpisodeOrigin>(e, "origin"));

        private static FactValue ReadValue(JsonElement e)
        {
            FactValueKind kind = RequireEnum<FactValueKind>(e, "kind");
            return FactValue.Parse(kind, RequireString(e, "v")) ?? throw new InvalidDataException("a value does not parse as " + kind);
        }

        private static FactObservation ReadValues(JsonElement e)
        {
            if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException("values are not an object");
            FactObservation values = FactObservation.Empty;
            foreach (JsonProperty p in e.EnumerateObject()) values = values.With(p.Name, ReadValue(p.Value));
            return values;
        }

        // ────────────────────────────────────────────────────────────────
        //  Schema 1 — legacy evidence
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Read a schema-1 file as the writer's historical record. Legacy
        /// evidence is never proof the new contract existed: its aggregate
        /// delivery claim stays unverified, its missing pause causes and attempt
        /// proof stay unknown, and no attempt rows are manufactured.
        /// </summary>
        private static LoadedSource ParseLegacy(JsonElement root, string json, string sourceName, List<string> rejected)
        {
            string contentHash = FactHash.Of(json);
            long process = root.TryGetProperty("processIncarnation", out JsonElement p) && p.ValueKind == JsonValueKind.Number
                ? p.GetInt64() : 0;

            var source = new LoadedSource
            {
                Name = sourceName,
                Legacy = true,
                Generation = 1,
                HeaderWriter = NamespacedGuid("jjflex-facts-legacy-source:" + contentHash),
            };

            if (!root.TryGetProperty("facts", out JsonElement facts) || facts.ValueKind != JsonValueKind.Array) return source;

            int index = 0;
            foreach (JsonElement e in facts.EnumerateArray())
            {
                index++;
                try
                {
                    string slot = RequireString(e, "slot");
                    string occurrence = RequireString(e, "occurrence");
                    string? episode = OptionalString(e, "episode");
                    long connection = OptionalLong(e, "connection");

                    // Preserved origin fields, validated against each other.
                    // Only when they agree is the identity stable across
                    // copies; otherwise it is namespaced by the file's own
                    // content, so two different files can never be taken for
                    // one episode, while an exact copy still deduplicates.
                    bool established = process != 0 && episode != null
                        && episode == process.ToString(CultureInfo.InvariantCulture) + ":"
                                      + connection.ToString(CultureInfo.InvariantCulture) + ":" + slot + ":" + occurrence;
                    EpisodeId id = established
                        ? new EpisodeId(NamespacedGuid("jjflex-facts-legacy-writer:" + process), StableOrdinal(episode!), EpisodeOrigin.Legacy)
                        : new EpisodeId(NamespacedGuid("jjflex-facts-legacy-source:" + contentHash),
                                        StableOrdinal(slot + "\u001f" + occurrence + "\u001f" + index), EpisodeOrigin.LegacySourceSpecific);

                    var classification = OptionalString(e, "classification") is string c
                        ? ParseEnum<DeliveryClassification>(c) : DeliveryClassification.Unclassified;
                    DeliveryDescriptor? delivery = null;
                    if (OptionalString(e, "shelfLife") is string life)
                    {
                        delivery = new DeliveryDescriptor(ParseEnum<ShelfLife>(life), OptionalString(e, "validityContract") ?? "",
                            OptionalString(e, "historyKey"),
                            OptionalString(e, "receiptPolicy") is string rp ? ParseEnum<ReceiptPolicy>(rp) : ReceiptPolicy.None);
                    }
                    if (classification == DeliveryClassification.Message && delivery == null)
                        classification = DeliveryClassification.Unclassified;

                    DateTime observed = OptionalString(e, "observedUtc") is string t && DateTime.TryParse(t, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out DateTime when) ? when : DateTime.MinValue;

                    long materialRevision = Math.Max(1, OptionalLong(e, "materialRevision"));
                    long reviewedRevision = OptionalLong(e, "reviewedMaterialRevision");
                    bool undelivered = !e.TryGetProperty("undelivered", out JsonElement u) || u.ValueKind != JsonValueKind.False;

                    var record = new FactRecord
                    {
                        Id = id,
                        Revision = Math.Max(1, OptionalLong(e, "revision")),
                        OwnerName = "legacy",
                        ContractName = OptionalString(e, "validityContract") ?? "legacy",
                        Condition = new ConditionKey(slot),
                        Claim = "legacy",
                        ScopeId = connection,
                        ScopeKind = FactScopeKind.RadioSession,
                        RadioIdentity = OptionalString(e, "radio"),
                        OccurrenceLabel = occurrence,
                        MessageKey = OptionalString(e, "key") ?? string.Empty,
                        Classification = classification,
                        Delivery = delivery,
                        Validity = LegacyValidity(OptionalString(e, "validity"), observed),
                        ObservedUtc = observed,
                        MaterialRevision = materialRevision,
                        NextMaterialId = 2,
                        RestoredPause = OptionalBool(e, "paused") ? PauseCause.LegacyUnknownCause : PauseCause.NoLivePermission,
                        CompactedAttempts = OptionalLong(e, "attempts"),
                        Restored = true,
                    };
                    record.ApplyDetail(OptionalString(e, "detail"));
                    if (OptionalBool(e, "detailTruncated")) record.DetailTruncated = true;
                    record.Materials.Add(new MaterialUnit(1, MaterialUnit.CoreName, FactValue.Of(record.MessageKey),
                                                          MaterialKind.Core, null, null, 0));
                    if (reviewedRevision >= materialRevision && reviewedRevision > 0) record.ReviewedImported.Add(1);
                    record.LegacyUnverifiedOwed = undelivered;
                    record.Receipt = new ReceiptRecord
                    {
                        Policy = delivery?.Receipt ?? ReceiptPolicy.None,
                        State = OptionalString(e, "receipt") is string rs ? ParseEnum<ReceiptState>(rs) : ReceiptState.NotRequested,
                        FromPreviousProcess = true,
                    };
                    record.Receipt.Consumed = record.Receipt.State != ReceiptState.NotRequested;
                    record.Origin = new HistoricalOrigin
                    {
                        Source = sourceName,
                        Generation = 1,
                        HeaderWriter = id.Writer,
                        Legacy = true,
                        RecordFingerprint = FactHash.Of(e.GetRawText()),
                    };
                    source.Facts.Add(record);
                }
                catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException
                                            or InvalidOperationException or KeyNotFoundException)
                {
                    rejected.Add(sourceName + " legacy record " + index + ": " + ex.Message);
                }
            }
            return source;
        }

        private static ValiditySnapshot LegacyValidity(string? state, DateTime asOf) => state switch
        {
            "Ended" => ValiditySnapshot.End(EndedKind.EndedObservationContext, asOf, "legacy record: ended, kind not recorded"),
            "Superseded" => ValiditySnapshot.NotKnown(UnknownReason.DataUnavailable, asOf, "legacy record: superseded, by what not recorded"),
            _ => ValiditySnapshot.NotKnown(UnknownReason.DataUnavailable, asOf,
                     "legacy record saved as " + (state ?? "unknown") + "; whether it is still true is not known"),
        };

        internal static Guid NamespacedGuid(string text)
        {
            byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
            byte[] bytes = new byte[16];
            Array.Copy(hash, bytes, 16);
            return new Guid(bytes);
        }

        internal static long StableOrdinal(string text)
        {
            byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
            long value = BitConverter.ToInt64(hash, 0) & long.MaxValue;
            return value == 0 ? 1 : value;
        }

        // ────────────────────────────────────────────────────────────────
        //  Field readers — every one validates
        // ────────────────────────────────────────────────────────────────

        private static JsonElement Require(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v)
                ? v
                : throw new InvalidDataException("missing '" + name + "'");

        private static JsonElement RequireArray(JsonElement e, string name)
        {
            JsonElement v = Require(e, name);
            return v.ValueKind == JsonValueKind.Array ? v : throw new InvalidDataException("'" + name + "' is not a list");
        }

        private static string RequireString(JsonElement e, string name)
        {
            JsonElement v = Require(e, name);
            return v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new InvalidDataException("'" + name + "' is not text");
        }

        private static string? OptionalString(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static long RequireLong(JsonElement e, string name, long min)
        {
            JsonElement v = Require(e, name);
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out long n)) throw new InvalidDataException("'" + name + "' is not a whole number");
            if (n < min) throw new InvalidDataException("'" + name + "' is out of range");
            return n;
        }

        private static long OptionalLong(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : 0;

        private static long? OptionalNullableLong(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n) ? n : null;

        private static bool OptionalBool(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;

        private static Guid RequireGuid(JsonElement e, string name) =>
            Guid.TryParseExact(RequireString(e, name), "N", out Guid g) && g != Guid.Empty
                ? g
                : throw new InvalidDataException("'" + name + "' is not a writer identity");

        private static DateTime RequireTime(JsonElement e, string name) =>
            DateTime.TryParse(RequireString(e, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime t)
                ? t
                : throw new InvalidDataException("'" + name + "' is not a time");

        private static T RequireEnum<T>(JsonElement e, string name) where T : struct, Enum => ParseEnum<T>(RequireString(e, name));

        private static T ParseEnum<T>(string text) where T : struct, Enum =>
            Enum.TryParse(text, ignoreCase: false, out T value) && Enum.IsDefined(value)
                ? value
                : throw new InvalidDataException("'" + text + "' is not a " + typeof(T).Name);

        private static long[] ReadIds(JsonElement e, string name)
        {
            var ids = new List<long>();
            foreach (JsonElement v in RequireArray(e, name).EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out long n) || n < 1)
                    throw new InvalidDataException("'" + name + "' holds something that is not an identity");
                ids.Add(n);
            }
            return ids.ToArray();
        }

        private static IEnumerable<string> ReadStrings(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out JsonElement v) || v.ValueKind != JsonValueKind.Array) yield break;
            foreach (JsonElement s in v.EnumerateArray())
                if (s.ValueKind == JsonValueKind.String) yield return s.GetString()!;
        }
    }
}
