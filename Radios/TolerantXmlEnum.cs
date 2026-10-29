#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using JJTrace;

namespace Radios
{
    /// <summary>
    /// The text an enum is saved as in XML, and the way back, by the same
    /// rule <see cref="XmlSerializer"/> uses: a member's name, or its
    /// <see cref="XmlEnumAttribute"/> name when it has one.
    /// </summary>
    public static class XmlEnumText
    {
        private static class Map<TEnum> where TEnum : struct, Enum
        {
            internal static readonly Dictionary<string, TEnum> FromText = Build();
            internal static readonly Dictionary<TEnum, string> ToText =
                FromText.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

            private static Dictionary<string, TEnum> Build()
            {
                var map = new Dictionary<string, TEnum>(StringComparer.Ordinal);
                foreach (FieldInfo field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    var xmlEnum = field.GetCustomAttribute<XmlEnumAttribute>();
                    string text = xmlEnum?.Name ?? field.Name;
                    map[text] = (TEnum)field.GetValue(null)!;
                }
                return map;
            }
        }

        /// <summary>True, with the value, when this build knows the text.</summary>
        public static bool TryParse<TEnum>(string? text, out TEnum value) where TEnum : struct, Enum
        {
            if (text != null && Map<TEnum>.FromText.TryGetValue(text.Trim(), out value)) return true;
            value = default;
            return false;
        }

        /// <summary>The text this value is saved as.</summary>
        public static string ToText<TEnum>(TEnum value) where TEnum : struct, Enum =>
            Map<TEnum>.ToText.TryGetValue(value, out string? text) ? text : value.ToString();
    }

    /// <summary>
    /// One enum setting in a saved file, read tolerantly (#847): a value this
    /// build does not know leaves THIS field at its default instead of failing
    /// the whole file, and is kept, so saving the file gives it back unchanged
    /// to the build that wrote it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Found 2026-10-29: a newer build saved
    /// <c>&lt;AudioCheckListenMethod&gt;Loopback&lt;/AudioCheckListenMethod&gt;</c>,
    /// an older build's <see cref="XmlSerializer"/> threw on the unknown name,
    /// and <see cref="RadioConfig.Load"/> fell back to defaults for every
    /// per-radio fact, the ownership answer among them, so the operator was
    /// asked "Is this radio yours?" again. The write side failed safe only
    /// because it refused to save over a file it could not read.
    /// </para>
    /// <para>
    /// <b>The unknown value survives a save.</b> A tolerant reader that then
    /// saved the default back would destroy the newer build's answer, so the
    /// raw text is held beside the value and written back while this build
    /// has not changed the field. Once something here sets the field, the new
    /// value is what gets saved: that is a decision, made in this build.
    /// </para>
    /// </remarks>
    public sealed class TolerantXmlEnum<TEnum> where TEnum : struct, Enum
    {
        private readonly string _field;
        private TEnum _value;
        private string? _unknownText;

        public TolerantXmlEnum(string field, TEnum initial)
        {
            _field = field;
            _value = initial;
        }

        /// <summary>
        /// The value this build works with. Setting it is a decision made in
        /// this build, so it replaces any unknown text kept from the file,
        /// even when the value set equals the default the unknown one read as.
        /// </summary>
        public TEnum Value
        {
            get => _value;
            set
            {
                _value = value;
                _unknownText = null;
            }
        }

        /// <summary>
        /// The saved text this build did not know, while this build has not
        /// set the field since reading it; otherwise null.
        /// </summary>
        public string? UnknownText => _unknownText;

        /// <summary>
        /// What is saved and read: the value's XML name, or the unknown text
        /// kept from the file.
        /// </summary>
        public string Text
        {
            get => UnknownText ?? XmlEnumText.ToText(_value);
            set
            {
                if (XmlEnumText.TryParse(value, out TEnum known))
                {
                    _value = known;
                    _unknownText = null;
                    return;
                }
                _unknownText = value ?? "";
                Tracing.TraceLine(
                    $"Saved setting {_field}: '{_unknownText}' is not a value this build knows; " +
                    $"using {_value} for it and keeping '{_unknownText}' for the build that wrote it",
                    System.Diagnostics.TraceLevel.Warning);
            }
        }
    }

    /// <summary>
    /// A saved list of enum values, read tolerantly in the same way: entries
    /// this build does not know are skipped for its own use and kept, so the
    /// list is saved back as it was read while this build has not changed it.
    /// </summary>
    public sealed class TolerantXmlEnumList<TEnum> where TEnum : struct, Enum
    {
        private readonly string _field;
        private string[]? _rawRead;
        private List<TEnum>? _knownAtRead;

        public TolerantXmlEnumList(string field) { _field = field; }

        /// <summary>
        /// The list this build works with. Callers may replace it or change it
        /// in place; replacing it is a decision made in this build and drops
        /// the entries kept from the file, and so does any change in place.
        /// </summary>
        public List<TEnum> Values
        {
            get => _values;
            set
            {
                _values = value ?? new List<TEnum>();
                _rawRead = null;
                _knownAtRead = null;
            }
        }
        private List<TEnum> _values = new();

        /// <summary>What is saved and read.</summary>
        public string[] Texts
        {
            get
            {
                if (_rawRead != null && _knownAtRead != null && Values.SequenceEqual(_knownAtRead))
                    return (string[])_rawRead.Clone();
                return Values.Select(v => XmlEnumText.ToText(v)).ToArray();
            }
            set
            {
                var known = new List<TEnum>();
                bool anyUnknown = false;
                foreach (string text in value ?? Array.Empty<string>())
                {
                    if (XmlEnumText.TryParse(text, out TEnum v)) known.Add(v);
                    else
                    {
                        anyUnknown = true;
                        Tracing.TraceLine(
                            $"Saved setting {_field}: '{text}' is not a value this build knows; " +
                            "skipping it here and keeping it for the build that wrote it",
                            System.Diagnostics.TraceLevel.Warning);
                    }
                }
                _values = known;
                _rawRead = anyUnknown ? (string[])(value ?? Array.Empty<string>()).Clone() : null;
                _knownAtRead = anyUnknown ? new List<TEnum>(known) : null;
            }
        }
    }
}
