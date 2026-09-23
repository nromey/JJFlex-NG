#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using JJTrace;

namespace Radios.Alarms
{
    /// <summary>What the journal can say about itself, for the Diagnostics readout.</summary>
    public sealed record AlarmJournalStats(
        long ObservationsRecorded,
        long EventsRecorded,
        long RecordsWritten,
        long BytesWritten,
        long Dropped,
        int QueueHighWaterMark,
        double RetainedRingSeconds,
        int RingRecords,
        string CurrentSegmentPath,
        bool Healthy,
        string HealthDetail);

    /// <summary>
    /// The disclosed, local, per-observation journal an armed alarm starts
    /// (design section 6): every callback of every recorded meter, every
    /// alarm event and every delivery, appended continuously on a worker,
    /// with a 120-second ring in memory so a detailed capture that starts
    /// later can prepend the preceding ninety seconds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Independent of the Record-meter-stream toggle and of verbosity.</b>
    /// It must survive forgetting to start a capture: a ring dumped only when
    /// an alarm fires loses the evidence if the connection dies first, or if
    /// no line was crossed. So it appends to disk while armed, batched at
    /// 250 ms and immediately on an event, and it flushes the partial batch
    /// on disconnect, disarm and disposal. That bounds ordinary buffering;
    /// it says nothing about OS-cache loss in a host power failure, and
    /// there is no guarantee of a final callback before the radio goes dark.
    /// </para>
    /// <para>
    /// <b>Bounded, with named gaps.</b> The observation queue holds at most
    /// 20,000 records or 16 MiB, whichever first. On overflow the records are
    /// dropped, counted, and the next record written is a gap carrying the
    /// dropped count and the sequence range where it is known. Alarm
    /// evaluation never waits on this queue; the meter thread only enqueues.
    /// </para>
    /// <para>
    /// <b>One record per meter, once.</b> Sixty-four alarms on one meter do
    /// not write it sixty-four times: the service records each observation
    /// once and the events refer to it by sequence.
    /// </para>
    /// <para>
    /// <b>A capture links the segment; it does not start a second logger.</b>
    /// When a detailed capture starts, the journal writes the ring's contents
    /// to a pre-roll file beside the running segment, notes the link in the
    /// trace with the segment id and the pre-roll actually retained, and
    /// carries on. Retention and export through the diagnostic archive is
    /// the follow-up named in the track report; today the folder keeps the
    /// newest segments per radio and never deletes one a capture linked.
    /// </para>
    /// <para>
    /// Records are JSON lines, one per line, with engineering units typed and
    /// culture-independent. The sensor timestamp is present and null, with
    /// its basis stated, because the callback does not supply one.
    /// </para>
    /// </remarks>
    public sealed class AlarmJournal : IAlarmObservationRecorder, IDisposable
    {
        public const int FlushIntervalMs = 250;
        public const int HeartbeatIntervalMs = 1000;
        public const int MaxQueueRecords = 20_000;
        public const long MaxQueueBytes = 16L * 1024 * 1024;
        public const int RingSeconds = 120;
        public const long RingBytesCap = 32L * 1024 * 1024;
        public const long SegmentRotateBytes = 64L * 1024 * 1024;
        public const int SegmentsKeptPerRadio = 20;

        private enum ItemKind { Observation, Line }

        private readonly struct Item
        {
            public Item(MeterObservation observation) { Kind = ItemKind.Observation; Observation = observation; Line = ""; }
            public Item(string line) { Kind = ItemKind.Line; Observation = default; Line = line; }
            public readonly ItemKind Kind;
            public readonly MeterObservation Observation;
            public readonly string Line;
            public int EstimatedBytes => Kind == ItemKind.Observation ? 450 : Line.Length + 1;
        }

        private readonly string _root;
        private readonly IAlarmClock _clock;
        private readonly object _gate = new object();
        private readonly Queue<Item> _queue = new Queue<Item>();
        private long _queueBytes;
        private int _highWater;
        private long _dropped;
        private long _droppedRun;
        private long _droppedFirstSeq = -1, _droppedLastSeq = -1;

        private readonly List<(long Ms, string Line, int Bytes)> _ring = new List<(long, string, int)>();
        private long _ringBytes;

        private readonly Thread _worker;
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private volatile bool _stopping;
        private volatile bool _flushNow;

        private string _serial = "unknown-radio";
        private int _generation;
        private string _segmentId = "";
        private string _segmentPath = "";
        private string _predecessorId = "";
        private long _segmentBytes;
        private readonly HashSet<string> _linkedSegments = new HashSet<string>(StringComparer.Ordinal);

        private long _observations, _events, _written, _bytesWritten;
        private long _lastSeq;
        private long _lastReceiptMs = -1;
        private bool _healthy = true;
        private string _healthDetail = "";
        private long _lastHeartbeatMs;
        private bool _armed;

        public AlarmJournal(string root, IAlarmClock clock, bool startWorker = true)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _worker = new Thread(Run) { IsBackground = true, Name = "AlarmJournal" };
            if (startWorker) _worker.Start();
        }

        /// <summary>Under the settings root, or null when it cannot be resolved.</summary>
        public static AlarmJournal? Default()
        {
            string root = RadioConfig.AppDataRoot;
            return string.IsNullOrEmpty(root) ? null : new AlarmJournal(Path.Combine(root, "alarm-journal"), new SystemAlarmClock());
        }

        public string Root => _root;

        // ── IAlarmObservationRecorder: the meter thread side ──

        public void Record(in MeterObservation observation)
        {
            if (observation.Provenance != ObservationProvenance.Measured) return;   // previews and replays never reach the journal as data
            Enqueue(new Item(observation), observation.Sequence);
        }

        public void RecordEvent(AlarmEvent alarmEvent)
        {
            Enqueue(new Item(EventLine(alarmEvent)), -1);
            _flushNow = true;
            _signal.Set();
        }

        /// <summary>What delivery did, from <see cref="AlarmDelivery.Reported"/>.</summary>
        public void RecordDelivery(AlarmDeliveryReport report)
        {
            Enqueue(new Item(Serialize(new
            {
                t = "delivery",
                kind = report.Event.Kind.ToString(),
                alarmId = report.Event.Definition.Id,
                episode = report.Event.EpisodeId,
                preview = report.IsPreview,
                sound = report.SoundRequested,
                speech = report.SpeechRequested,
                toneLeadMs = report.ToneLeadMs,
                sentence = report.Sentence,
                utc = _clock.UtcNow,
                mono = _clock.NowMs,
                speechOutcome = "handed to the reader; completion, if the reader reports it, is in the trace",
            })), -1);
            _flushNow = true;
            _signal.Set();
        }

        public void RecordedSetChanged(int connectionGeneration, IReadOnlyList<MeterDescriptor> recorded)
        {
            _armed = recorded.Count > 0;
            var meters = new List<object>(recorded.Count);
            foreach (MeterDescriptor d in recorded)
                meters.Add(new { index = d.Index, name = d.Name, description = d.Description, source = d.Source, sourceIndex = d.SourceIndex, units = d.Units.ToString(), low = d.Low, high = d.High });
            Enqueue(new Item(Serialize(new { t = "recorded_set", gen = connectionGeneration, utc = _clock.UtcNow, mono = _clock.NowMs, meters })), -1);
            _flushNow = true;
            _signal.Set();
        }

        public void ConnectionStarted(int connectionGeneration, string radioSerial)
        {
            lock (_gate)
            {
                _serial = AlarmDefinitionStore.SafeSerial(radioSerial);
                _generation = connectionGeneration;
            }
            Enqueue(new Item(Serialize(new { t = "connection", state = "started", gen = connectionGeneration, utc = _clock.UtcNow, mono = _clock.NowMs })), -1);
            _flushNow = true;
            _signal.Set();
        }

        public void ConnectionEnded(int connectionGeneration, string why)
        {
            Enqueue(new Item(Serialize(new { t = "connection", state = "ended", gen = connectionGeneration, why, utc = _clock.UtcNow, mono = _clock.NowMs,
                lastSeq = Interlocked.Read(ref _lastSeq), lastReceiptMs = Interlocked.Read(ref _lastReceiptMs) })), -1);
            _flushNow = true;
            _signal.Set();
        }

        // ── the capture link ──

        /// <summary>
        /// A detailed capture started: write the ring to a pre-roll file beside
        /// the segment, record the link, and say in the trace where it is.
        /// Returns the pre-roll path, or empty when nothing could be written.
        /// </summary>
        public string CaptureStarted(string tracePath)
        {
            List<(long Ms, string Line, int Bytes)> ring;
            double retained;
            lock (_gate)
            {
                ring = new List<(long, string, int)>(_ring);
                retained = ring.Count == 0 ? 0 : (ring[ring.Count - 1].Ms - ring[0].Ms) / 1000.0;
            }
            Flush(force: true);

            string preRollPath = "";
            try
            {
                EnsureSegment();
                preRollPath = Path.Combine(Path.GetDirectoryName(_segmentPath) ?? _root,
                    "capture-" + _clock.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-preroll.jsonl");
                using var w = new StreamWriter(preRollPath, false, new UTF8Encoding(false));
                w.WriteLine(Serialize(new { t = "preroll", segment = _segmentId, tracePath, retainedSeconds = retained, records = ring.Count,
                    ringSecondsRequested = RingSeconds, utc = _clock.UtcNow, mono = _clock.NowMs }));
                foreach (var r in ring) w.WriteLine(r.Line);
                lock (_gate) _linkedSegments.Add(_segmentId);
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("AlarmJournal: pre-roll could not be written — " + ex.Message, TraceLevel.Warning);
                preRollPath = "";
            }

            Enqueue(new Item(Serialize(new { t = "capture", state = "started", segment = _segmentId, segmentPath = _segmentPath, tracePath, preRollPath,
                preRollSeconds = retained, preRollRecords = ring.Count, utc = _clock.UtcNow, mono = _clock.NowMs })), -1);
            _flushNow = true;
            _signal.Set();

            Tracing.TraceLine("AlarmJournal: capture linked to journal segment " + _segmentId + " at " + _segmentPath
                + "; pre-roll " + ring.Count + " record(s) over " + retained.ToString("0.0", CultureInfo.InvariantCulture)
                + " s" + (retained < 90 ? " (less than ninety seconds exists)" : "") + " at " + preRollPath, TraceLevel.Info);
            return preRollPath;
        }

        public void CaptureStopped()
        {
            Enqueue(new Item(Serialize(new { t = "capture", state = "stopped", segment = _segmentId, utc = _clock.UtcNow, mono = _clock.NowMs })), -1);
            Flush(force: true);
        }

        // ── stats ──

        public AlarmJournalStats Stats()
        {
            lock (_gate)
            {
                double retained = _ring.Count == 0 ? 0 : (_ring[_ring.Count - 1].Ms - _ring[0].Ms) / 1000.0;
                return new AlarmJournalStats(_observations, _events, _written, _bytesWritten, Interlocked.Read(ref _dropped),
                    _highWater, retained, _ring.Count, _segmentPath, _healthy, _healthDetail);
            }
        }

        /// <summary>The ring as it stands: every retained line, oldest first, and the seconds it spans.</summary>
        public IReadOnlyList<string> PreRoll(out double retainedSeconds)
        {
            lock (_gate)
            {
                retainedSeconds = _ring.Count == 0 ? 0 : (_ring[_ring.Count - 1].Ms - _ring[0].Ms) / 1000.0;
                var lines = new List<string>(_ring.Count);
                foreach (var r in _ring) lines.Add(r.Line);
                return lines;
            }
        }

        // ── the queue ──

        private void Enqueue(Item item, long seq)
        {
            lock (_gate)
            {
                if (_stopping) return;
                int bytes = item.EstimatedBytes;
                if (_queue.Count >= MaxQueueRecords || _queueBytes + bytes > MaxQueueBytes)
                {
                    long n = Interlocked.Increment(ref _dropped);
                    _droppedRun++;
                    if (seq >= 0)
                    {
                        if (_droppedFirstSeq < 0) _droppedFirstSeq = seq;
                        _droppedLastSeq = seq;
                    }
                    if (_droppedRun == 1)
                        Tracing.TraceLine("AlarmJournal: queue full (" + _queue.Count + " records, " + _queueBytes
                            + " bytes); dropping until the writer catches up — a gap record will name the range", TraceLevel.Error);
                    return;
                }
                if (_droppedRun > 0)
                {
                    // The first record that fits after a run of drops is preceded by the gap.
                    string gap = Serialize(new
                    {
                        t = "gap",
                        dropped = _droppedRun,
                        firstSeq = _droppedFirstSeq >= 0 ? _droppedFirstSeq : (long?)null,
                        lastSeq = _droppedLastSeq >= 0 ? _droppedLastSeq : (long?)null,
                        exact = _droppedFirstSeq >= 0,
                        utc = _clock.UtcNow, mono = _clock.NowMs,
                    });
                    _queue.Enqueue(new Item(gap));
                    _queueBytes += gap.Length + 1;
                    _droppedRun = 0;
                    _droppedFirstSeq = _droppedLastSeq = -1;
                }
                _queue.Enqueue(item);
                _queueBytes += bytes;
                if (_queue.Count > _highWater) _highWater = _queue.Count;
            }
        }

        // ── the worker ──

        private void Run()
        {
            while (!_stopping)
            {
                _signal.WaitOne(FlushIntervalMs);
                try
                {
                    Flush(force: _flushNow);
                    _flushNow = false;
                    Heartbeat();
                }
                catch (Exception ex)
                {
                    Degrade("flush failed: " + ex.Message);
                }
            }
            try { Flush(force: true); } catch { }
        }

        /// <summary>Drain the queue to the segment. Public so a harness without a worker can drive it.</summary>
        public void Flush(bool force)
        {
            List<Item> batch;
            lock (_gate)
            {
                if (_queue.Count == 0) return;
                batch = new List<Item>(_queue);
                _queue.Clear();
                _queueBytes = 0;
            }

            var lines = new List<string>(batch.Count);
            foreach (Item item in batch)
            {
                string line = item.Kind == ItemKind.Observation ? ObservationLine(item.Observation) : item.Line;
                lines.Add(line);
                if (item.Kind == ItemKind.Observation)
                {
                    Interlocked.Increment(ref _observations);
                    Interlocked.Exchange(ref _lastSeq, item.Observation.Sequence);
                    Interlocked.Exchange(ref _lastReceiptMs, item.Observation.ReceiptMonotonicMs);
                    Ring(item.Observation.ReceiptMonotonicMs, line);
                }
                else
                {
                    Interlocked.Increment(ref _events);
                }
            }

            try
            {
                EnsureSegment();
                Append(lines);
                if (!_healthy) { _healthy = true; _healthDetail = ""; Tracing.TraceLine("AlarmJournal: writing again", TraceLevel.Info); }
                if (_segmentBytes >= SegmentRotateBytes) Rotate();
            }
            catch (Exception ex)
            {
                Degrade("could not write " + lines.Count + " record(s): " + ex.Message);
            }
        }

        /// <summary>
        /// Open, append, close. Nothing holds the segment open between batches,
        /// so the problem-report bundle, an export, or a person with a text
        /// editor can read the live journal while the app is running — a
        /// journal nobody can open until the app exits is evidence that
        /// arrives too late. A few opens a second is nothing next to the
        /// trace's own writer.
        /// </summary>
        private void Append(IReadOnlyList<string> lines)
        {
            using var stream = new FileStream(_segmentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var w = new StreamWriter(stream, new UTF8Encoding(false));
            foreach (string line in lines)
            {
                w.WriteLine(line);
                _segmentBytes += line.Length + 2;
                _bytesWritten += line.Length + 2;
                _written++;
            }
            w.Flush();
        }

        private void Ring(long ms, string line)
        {
            lock (_gate)
            {
                int bytes = line.Length + 1;
                _ring.Add((ms, line, bytes));
                _ringBytes += bytes;
                long cutoff = ms - RingSeconds * 1000L;
                while (_ring.Count > 0 && (_ring[0].Ms < cutoff || _ringBytes > RingBytesCap))
                {
                    _ringBytes -= _ring[0].Bytes;
                    _ring.RemoveAt(0);
                }
            }
        }

        private void Heartbeat()
        {
            if (!_armed) return;
            long now = _clock.NowMs;
            if (now - _lastHeartbeatMs < HeartbeatIntervalMs) return;
            _lastHeartbeatMs = now;
            long lastReceipt = Interlocked.Read(ref _lastReceiptMs);
            string line = Serialize(new
            {
                t = "heartbeat",
                utc = _clock.UtcNow,
                mono = now,
                lastSeq = Interlocked.Read(ref _lastSeq),
                // The meter's own receipt time, carried separately: a heartbeat's
                // advancing clock never refreshes it.
                lastReceiptMs = lastReceipt < 0 ? (long?)null : lastReceipt,
                receiptAgeSeconds = lastReceipt < 0 ? (double?)null : (now - lastReceipt) / 1000.0,
                queueHighWater = _highWater,
                dropped = Interlocked.Read(ref _dropped),
                healthy = _healthy,
            });
            try
            {
                EnsureSegment();
                Append(new[] { line });
            }
            catch (Exception ex) { Degrade("heartbeat: " + ex.Message); }
        }

        private void Degrade(string detail)
        {
            if (_healthy) Tracing.TraceLine("AlarmJournal: DEGRADED — " + detail, TraceLevel.Error);
            _healthy = false;
            _healthDetail = detail;
        }

        // ── segments ──

        private void EnsureSegment()
        {
            if (_segmentPath.Length != 0) return;
            string dir = Path.Combine(_root, _serial);
            Directory.CreateDirectory(dir);
            string id = _clock.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string path = Path.Combine(dir, "journal-" + id + ".jsonl");
            _segmentPath = path;
            _segmentBytes = 0;
            string predecessor = _segmentId;
            _segmentId = id;
            string header = Serialize(new
            {
                t = "segment",
                id,
                predecessor,
                gen = _generation,
                radio = _serial,
                utc = _clock.UtcNow,
                mono = _clock.NowMs,
                build = BuildVersion(),
                sensorTimestampBasis = MeterObservation.SensorTimestampBasis,
            });
            Append(new[] { header });
            Prune(dir);
        }

        private void Rotate()
        {
            string successorNote = Serialize(new { t = "segment_end", id = _segmentId, utc = _clock.UtcNow, mono = _clock.NowMs, bytes = _segmentBytes });
            try { Append(new[] { successorNote }); } catch { }
            _predecessorId = _segmentId;
            _segmentPath = "";
            EnsureSegment();
        }

        /// <summary>Keep the newest segments; never delete one a capture linked.</summary>
        private void Prune(string dir)
        {
            try
            {
                string[] files = Directory.GetFiles(dir, "journal-*.jsonl");
                if (files.Length <= SegmentsKeptPerRadio) return;
                Array.Sort(files, StringComparer.Ordinal);
                int remove = files.Length - SegmentsKeptPerRadio;
                foreach (string f in files)
                {
                    if (remove == 0) break;
                    string id = Path.GetFileNameWithoutExtension(f).Substring("journal-".Length);
                    bool linked;
                    lock (_gate) linked = _linkedSegments.Contains(id);
                    if (linked || string.Equals(f, _segmentPath, StringComparison.OrdinalIgnoreCase)) continue;
                    File.Delete(f);
                    remove--;
                }
            }
            catch (Exception ex)
            {
                Tracing.TraceLine("AlarmJournal: prune failed — " + ex.Message, TraceLevel.Warning);
            }
        }

        // ── records ──

        private string ObservationLine(in MeterObservation o) => Serialize(new
        {
            t = "obs",
            seq = o.Sequence,
            gen = o.ConnectionGeneration,
            meterIndex = o.Meter.Index,
            name = o.Meter.Name,
            source = o.Meter.Source,
            sourceIndex = o.Meter.SourceIndex,
            units = o.Meter.Units.ToString(),
            value = o.Value,
            valid = o.Validity.ToString(),
            outOfRange = o.OutOfRange,
            provenance = o.Provenance.ToString(),
            utc = o.ReceiptUtc,
            mono = o.ReceiptMonotonicMs,
            sensorUtc = (DateTime?)null,
            sensorBasis = MeterObservation.SensorTimestampBasis,
        });

        private string EventLine(AlarmEvent e) => Serialize(new
        {
            t = "event",
            kind = e.Kind.ToString(),
            alarmId = e.Definition.Id,
            revision = e.Definition.Revision,
            alarm = e.Definition.Name,
            condition = e.Definition.Condition.ToString(),
            direction = e.Definition.Direction.ToString(),
            episode = e.EpisodeId,
            obsSeq = e.Observation?.Sequence,
            obsProvenance = e.Observation?.Provenance.ToString(),
            value = float.IsNaN(e.Value) ? (float?)null : e.Value,
            threshold = e.Threshold,
            hysteresis = e.Definition.Hysteresis,
            persistenceSamples = e.PersistenceSamples,
            freshnessAllowanceSeconds = e.Definition.FreshnessAllowanceSeconds,
            ageSeconds = double.IsNaN(e.AgeSeconds) ? (double?)null : e.AgeSeconds,
            change = float.IsNaN(e.Change) ? (float?)null : e.Change,
            baseline = float.IsNaN(e.Baseline) ? (float?)null : e.Baseline,
            intervalSeconds = double.IsNaN(e.IntervalSeconds) ? (double?)null : e.IntervalSeconds,
            reminderReason = e.ReminderReason.ToString(),
            transmitting = e.Transmitting,
            wasActive = e.WasActive,
            snoozeSeconds = e.SnoozeSeconds,
            detail = e.Detail,
            receiptMono = e.Observation?.ReceiptMonotonicMs,
            evalMono = e.AtMs,
            utc = _clock.UtcNow,
        });

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = false };

        private static string Serialize(object o) => JsonSerializer.Serialize(o, JsonOptions);

        private static string BuildVersion()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetEntryAssembly() ?? typeof(AlarmJournal).Assembly;
                var info = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm);
                return info?.InformationalVersion ?? asm.GetName().Version?.ToString() ?? "unknown";
            }
            catch { return "unknown"; }
        }

        public void Dispose()
        {
            if (_stopping) return;
            _stopping = true;
            _signal.Set();
            if (_worker.IsAlive) { try { _worker.Join(2000); } catch { } }
            else { try { Flush(force: true); } catch { } }
            _signal.Dispose();
        }
    }
}
