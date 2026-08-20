using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace JJFlex.RigSurface
{
    /// <summary>
    /// One object the radio has told us about: a slice, the transmit section,
    /// a connected client, and so on.
    /// </summary>
    public sealed class RigObject
    {
        internal RigObject(RigTarget target, int index)
        {
            Target = target;
            Index = index;
        }

        public RigTarget Target { get; }

        public int Index { get; }

        /// <summary>
        /// The client handle that owns this object, as the radio reports it in
        /// the object's own <c>client_handle</c> field. Null where the object
        /// is station-wide and has no owner, or where the radio has not said.
        /// <para>
        /// This is the field that decides whether an object is ours to touch.
        /// It is NOT the same as the handle prefix on the status line, which is
        /// the routing handle and is frequently 0.
        /// </para>
        /// </summary>
        public string? OwnerHandle { get; internal set; }

        /// <summary>Every key the radio has reported for this object, merged.</summary>
        public Dictionary<string, string> Fields { get; } =
            new(StringComparer.Ordinal);

        /// <summary>When the radio last said anything about this object.</summary>
        public DateTimeOffset LastUpdated { get; internal set; }

        /// <summary>True once the radio has told us the object went away.</summary>
        public bool Removed { get; internal set; }

        internal RigObject Clone()
        {
            var copy = new RigObject(Target, Index)
            {
                OwnerHandle = OwnerHandle,
                LastUpdated = LastUpdated,
                Removed = Removed,
            };
            foreach (KeyValuePair<string, string> kv in Fields)
            {
                copy.Fields[kv.Key] = kv.Value;
            }
            return copy;
        }

        public string Describe()
        {
            string who = OwnerHandle is null ? "" : $" [owner {OwnerHandle}]";
            string name = Index == RigField.NoIndex
                ? Target.ToString().ToLowerInvariant()
                : string.Create(CultureInfo.InvariantCulture, $"{Target.ToString().ToLowerInvariant()} {Index}");
            return name + who;
        }
    }

    /// <summary>
    /// The radio's own state, as the radio has reported it.
    /// <para>
    /// EVERY value in here arrived on the wire from the radio. Nothing in this
    /// class is ever populated from a value we sent. That is the whole point of
    /// the type and it is the difference between a test that proves something
    /// and a test that reads back its own assumption: FlexLib maintains a local
    /// cache and will dedup a set against it, so asking FlexLib what the mode
    /// is can return the value we asked for even when the radio refused the
    /// command.
    /// </para>
    /// <para>
    /// Consequently the only mutator is internal and is called from exactly one
    /// place, <see cref="RigWire"/>'s reader thread. Do not add another.
    /// </para>
    /// </summary>
    public sealed class RadioState
    {
        private readonly object _gate = new();
        private readonly Dictionary<(RigTarget Target, int Index), RigObject> _objects = new();
        private long _version;
        private DateTimeOffset _lastStatusAt;

        /// <summary>Increments on every status line folded in. Cheap change detector.</summary>
        public long Version
        {
            get { lock (_gate) { return _version; } }
        }

        /// <summary>
        /// When the radio last sent us anything at all. A stale value here means
        /// the connection has gone quiet, which makes every read UNKNOWN rather
        /// than merely old — see <see cref="Guards"/>.
        /// </summary>
        public DateTimeOffset LastStatusAt
        {
            get { lock (_gate) { return _lastStatusAt; } }
        }

        /// <summary>Reads one field as the radio last reported it, or null.</summary>
        public string? Get(RigField field)
        {
            lock (_gate)
            {
                return _objects.TryGetValue((field.Target, field.Index), out RigObject? obj)
                       && !obj.Removed
                       && obj.Fields.TryGetValue(field.Key, out string? value)
                    ? value
                    : null;
            }
        }

        public string? Get(RigTarget target, string key) => Get(new RigField(target, RigField.NoIndex, key));

        public bool TryGetDouble(RigField field, out double value)
        {
            value = 0;
            string? raw = Get(field);
            return raw is not null
                && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public bool TryGetInt(RigField field, out int value)
        {
            value = 0;
            string? raw = Get(field);
            return raw is not null
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Snapshot of one object, or null if the radio has never mentioned it.</summary>
        public RigObject? GetObject(RigTarget target, int index = RigField.NoIndex)
        {
            lock (_gate)
            {
                return _objects.TryGetValue((target, index), out RigObject? obj) && !obj.Removed
                    ? obj.Clone()
                    : null;
            }
        }

        /// <summary>Every live object of a kind, index-ordered.</summary>
        public IReadOnlyList<RigObject> GetObjects(RigTarget target)
        {
            lock (_gate)
            {
                return _objects
                    .Where(kv => kv.Key.Target == target && !kv.Value.Removed)
                    .OrderBy(kv => kv.Key.Index)
                    .Select(kv => kv.Value.Clone())
                    .ToList();
            }
        }

        /// <summary>Every live object, whatever the kind.</summary>
        public IReadOnlyList<RigObject> AllObjects()
        {
            lock (_gate)
            {
                return _objects.Values
                    .Where(o => !o.Removed)
                    .OrderBy(o => o.Target)
                    .ThenBy(o => o.Index)
                    .Select(o => o.Clone())
                    .ToList();
            }
        }

        /// <summary>
        /// Flattens the whole live model into field/value pairs. This is what a
        /// snapshot is made of.
        /// </summary>
        public IReadOnlyDictionary<RigField, string> Flatten()
        {
            var result = new Dictionary<RigField, string>();
            lock (_gate)
            {
                foreach (KeyValuePair<(RigTarget Target, int Index), RigObject> kv in _objects)
                {
                    if (kv.Value.Removed) continue;
                    foreach (KeyValuePair<string, string> f in kv.Value.Fields)
                    {
                        result[new RigField(kv.Key.Target, kv.Key.Index, f.Key)] = f.Value;
                    }
                }
            }
            return result;
        }

        // ---------------------------------------------------------------- //
        // The single mutation path. Reader thread only.
        // ---------------------------------------------------------------- //

        internal void Fold(ParsedStatus status)
        {
            lock (_gate)
            {
                _version++;
                _lastStatusAt = DateTimeOffset.UtcNow;

                var key = (status.Target, status.Index);
                if (!_objects.TryGetValue(key, out RigObject? obj))
                {
                    obj = new RigObject(status.Target, status.Index);
                    _objects[key] = obj;
                }

                obj.LastUpdated = _lastStatusAt;

                if (status.Removed)
                {
                    obj.Removed = true;
                    return;
                }

                obj.Removed = false;

                foreach (KeyValuePair<string, string> kv in status.Fields)
                {
                    obj.Fields[kv.Key] = kv.Value;
                    if (string.Equals(kv.Key, "client_handle", StringComparison.Ordinal))
                    {
                        obj.OwnerHandle = kv.Value;
                    }
                }

                // A client object's own identity IS its handle, which arrives as
                // the object's index token rather than as a client_handle field.
                if (status.Target == RigTarget.Client && status.Handle is not null)
                {
                    obj.OwnerHandle = status.Handle;
                }
            }
        }

        internal void MarkAllStale()
        {
            lock (_gate)
            {
                _objects.Clear();
                _version++;
            }
        }
    }
}
