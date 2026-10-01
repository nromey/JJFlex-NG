#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Radios.Facts
{
    /// <summary>The four shapes an observed value may take.</summary>
    public enum FactValueKind
    {
        Integer = 0,
        Decimal = 1,
        Text = 2,
        Flag = 3,
    }

    /// <summary>
    /// One typed value an owner observed or declared.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Typed, so a contract can check it.</b> A condition contract declares
    /// the evidence it accepts by name AND kind, which is what stops one
    /// producer's temperature arriving as another producer's free text, and what
    /// lets a journal round-trip the value without guessing what "70" meant.
    /// </para>
    /// <para>
    /// Nothing in the fact layer compares these for meaning. A worsening is the
    /// OWNER's decision; the store only records which values it was told.
    /// </para>
    /// </remarks>
    public readonly struct FactValue : IEquatable<FactValue>
    {
        private readonly long _integer;
        private readonly decimal _decimal;
        private readonly string? _text;
        private readonly bool _flag;

        private FactValue(FactValueKind kind, long integer, decimal dec, string? text, bool flag)
        {
            Kind = kind;
            _integer = integer;
            _decimal = dec;
            _text = text;
            _flag = flag;
        }

        public FactValueKind Kind { get; }

        public static FactValue Of(long value) => new FactValue(FactValueKind.Integer, value, 0m, null, false);
        public static FactValue Of(decimal value) => new FactValue(FactValueKind.Decimal, 0, value, null, false);
        public static FactValue Of(string value) =>
            new FactValue(FactValueKind.Text, 0, 0m, value ?? throw new ArgumentNullException(nameof(value)), false);
        public static FactValue Of(bool value) => new FactValue(FactValueKind.Flag, 0, 0m, null, value);

        public long AsInteger => Kind == FactValueKind.Integer ? _integer : throw Wrong(FactValueKind.Integer);
        public decimal AsDecimal => Kind == FactValueKind.Decimal ? _decimal : throw Wrong(FactValueKind.Decimal);
        public string AsText => Kind == FactValueKind.Text ? _text! : throw Wrong(FactValueKind.Text);
        public bool AsFlag => Kind == FactValueKind.Flag ? _flag : throw Wrong(FactValueKind.Flag);

        private InvalidOperationException Wrong(FactValueKind asked) =>
            new InvalidOperationException("this value is " + Kind + ", not " + asked);

        /// <summary>The invariant rendering: what a placeholder is filled with, and what the journal stores.</summary>
        public string Invariant => Kind switch
        {
            FactValueKind.Integer => _integer.ToString(CultureInfo.InvariantCulture),
            FactValueKind.Decimal => _decimal.ToString(CultureInfo.InvariantCulture),
            FactValueKind.Flag => _flag ? "true" : "false",
            _ => _text ?? string.Empty,
        };

        /// <summary>Parse the journal's rendering back, or null when it does not parse as that kind.</summary>
        public static FactValue? Parse(FactValueKind kind, string? text)
        {
            if (text == null) return null;
            switch (kind)
            {
                case FactValueKind.Integer:
                    return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long i)
                        ? Of(i) : (FactValue?)null;
                case FactValueKind.Decimal:
                    return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d)
                        ? Of(d) : (FactValue?)null;
                case FactValueKind.Flag:
                    return text == "true" ? Of(true) : text == "false" ? Of(false) : (FactValue?)null;
                case FactValueKind.Text:
                    return Of(text);
                default:
                    return null;
            }
        }

        /// <summary>The value as a placeholder argument.</summary>
        internal object? AsArgument => Kind switch
        {
            FactValueKind.Integer => _integer,
            FactValueKind.Decimal => _decimal,
            FactValueKind.Flag => _flag,
            _ => _text,
        };

        public bool Equals(FactValue other) =>
            Kind == other.Kind && string.Equals(Invariant, other.Invariant, StringComparison.Ordinal);

        public override bool Equals(object? obj) => obj is FactValue other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Kind, Invariant);
        public static bool operator ==(FactValue a, FactValue b) => a.Equals(b);
        public static bool operator !=(FactValue a, FactValue b) => !a.Equals(b);

        public override string ToString() => Kind + ":" + Invariant;
    }

    /// <summary>A named, typed field a contract accepts as evidence.</summary>
    public sealed class EvidenceField : IEquatable<EvidenceField>
    {
        public EvidenceField(string name, FactValueKind kind, bool required = true)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));
            Name = name;
            Kind = kind;
            Required = required;
        }

        public string Name { get; }
        public FactValueKind Kind { get; }
        public bool Required { get; }

        public bool Equals(EvidenceField? other) =>
            other != null && Name == other.Name && Kind == other.Kind && Required == other.Required;

        public override bool Equals(object? obj) => Equals(obj as EvidenceField);
        public override int GetHashCode() => HashCode.Combine(Name, Kind, Required);
        public override string ToString() => Name + " (" + Kind + (Required ? "" : ", optional") + ")";
    }

    /// <summary>
    /// An immutable set of named, typed values: one observation, or one domain
    /// baseline.
    /// </summary>
    public sealed class FactObservation : IEquatable<FactObservation>
    {
        private readonly SortedDictionary<string, FactValue> _values;

        public static FactObservation Empty { get; } = new FactObservation(new SortedDictionary<string, FactValue>(StringComparer.Ordinal));

        private FactObservation(SortedDictionary<string, FactValue> values) => _values = values;

        /// <summary>This observation with one more value. The original is untouched.</summary>
        public FactObservation With(string name, FactValue value)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(name));
            var copy = new SortedDictionary<string, FactValue>(_values, StringComparer.Ordinal) { [name] = value };
            return new FactObservation(copy);
        }

        public static FactObservation Of(params (string Name, FactValue Value)[] values)
        {
            var map = new SortedDictionary<string, FactValue>(StringComparer.Ordinal);
            foreach (var (name, value) in values ?? Array.Empty<(string, FactValue)>())
            {
                if (string.IsNullOrWhiteSpace(name)) throw new ArgumentNullException(nameof(values));
                if (map.ContainsKey(name)) throw new ArgumentException("duplicate value '" + name + "'", nameof(values));
                map[name] = value;
            }
            return new FactObservation(map);
        }

        public IReadOnlyDictionary<string, FactValue> Values => _values;
        public int Count => _values.Count;
        public bool IsEmpty => _values.Count == 0;

        public bool TryGet(string name, out FactValue value) => _values.TryGetValue(name, out value);

        /// <summary>
        /// A stable fingerprint of the contents, for "same event, same payload"
        /// comparisons and for the baseline a worsening names. Never an identity.
        /// </summary>
        public string Fingerprint
        {
            get
            {
                var sb = new StringBuilder();
                foreach (var pair in _values)
                {
                    sb.Append(pair.Key).Append('=').Append((int)pair.Value.Kind).Append(':')
                      .Append(pair.Value.Invariant.Length).Append(':').Append(pair.Value.Invariant).Append(';');
                }
                return FactHash.Of(sb.ToString());
            }
        }

        public bool Equals(FactObservation? other)
        {
            if (other is null || other._values.Count != _values.Count) return false;
            foreach (var pair in _values)
                if (!other._values.TryGetValue(pair.Key, out FactValue v) || v != pair.Value) return false;
            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as FactObservation);
        public override int GetHashCode() => Fingerprint.GetHashCode(StringComparison.Ordinal);

        public override string ToString()
        {
            var parts = new List<string>(_values.Count);
            foreach (var pair in _values) parts.Add(pair.Key + "=" + pair.Value.Invariant);
            return "{" + string.Join(", ", parts) + "}";
        }
    }

    /// <summary>A short content hash for fingerprints. Detects mismatch; proves nothing about meaning.</summary>
    internal static class FactHash
    {
        public static string Of(string text)
        {
            byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty));
            return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
        }
    }
}
