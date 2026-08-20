using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace JJFlexWpf
{
    /// <summary>
    /// Reading voice packs — a file of <see cref="MeterVoice"/> definitions
    /// someone else authored, brought into this installation's user voices.
    ///
    /// Import only, and that is a decision rather than an unfinished feature.
    /// Noel, Sprint 32: "We'd probably want to add it in code, I'm not sure how
    /// we'd 'author' a tone in the actual interface, that might be too complex
    /// for a radio application." Authoring a timbre means a screen full of
    /// partial amplitudes, envelope times and modulation rates, and every one
    /// of them has to be reachable and readable by a screen reader before it is
    /// worth putting on screen at all. A voice pack file skips that whole
    /// surface: someone who wants to build voices does it where building things
    /// is easy, and everyone else gets the result with one button.
    ///
    /// The file format is deliberately the one the config already writes —
    /// XmlSerializer over a list of MeterVoice — so a pack can be lifted
    /// straight out of an audioConfig.xml, and a voice that loads here is a
    /// voice that will load there.
    /// </summary>
    public static class MeterVoicePack
    {
        /// <summary>What an import did, in words a dialog can read out.</summary>
        public sealed class ImportResult
        {
            /// <summary>Names actually stored, after any collision suffixing.</summary>
            public List<string> Imported { get; } = new();

            /// <summary>Entries skipped, each with the reason.</summary>
            public List<string> Skipped { get; } = new();

            /// <summary>Set when the file could not be read at all.</summary>
            public string? Error { get; set; }

            /// <summary>True when at least one voice landed.</summary>
            public bool AnyImported => Imported.Count > 0;

            /// <summary>
            /// One spoken sentence. Says the count first, because that is the
            /// answer to the question the operator actually asked, and names
            /// the voices after it.
            /// </summary>
            public string Summary()
            {
                if (Error != null) return $"Import failed. {Error}";
                if (Imported.Count == 0 && Skipped.Count == 0)
                    return "That file held no voices.";

                string head = Imported.Count switch
                {
                    0 => "No voices imported",
                    1 => "1 voice imported",
                    _ => $"{Imported.Count} voices imported",
                };
                if (Imported.Count > 0)
                    head += ": " + string.Join(", ", Imported);
                if (Skipped.Count > 0)
                    head += $". {Skipped.Count} skipped: " + string.Join("; ", Skipped);
                return head + ".";
            }
        }

        /// <summary>
        /// Read a voice pack and add its voices to the user-voice set. Built-in
        /// names are never shadowed — <see cref="MeterVoiceLibrary.SaveUserVoice"/>
        /// suffixes a colliding name instead, so importing a pack that happens
        /// to contain a "Bell" cannot quietly change what "Bell" means for every
        /// meter that already references it.
        /// </summary>
        public static ImportResult Import(string path)
        {
            var result = new ImportResult();
            try
            {
                List<MeterVoice>? voices;
                var serializer = new XmlSerializer(typeof(List<MeterVoice>));
                var settings = new XmlReaderSettings
                {
                    // No DTD, no external resolver. A voice pack is a file from
                    // somebody else's machine.
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                };
                using (var stream = File.OpenRead(path))
                using (var reader = XmlReader.Create(stream, settings))
                {
                    voices = serializer.Deserialize(reader) as List<MeterVoice>;
                }

                if (voices == null)
                {
                    result.Error = "That file is not a voice pack.";
                    return result;
                }

                foreach (var voice in voices)
                {
                    if (voice == null) continue;
                    if (string.IsNullOrWhiteSpace(voice.Name))
                    {
                        result.Skipped.Add("one voice with no name");
                        continue;
                    }
                    if (voice.Partials == null || voice.Partials.Length == 0)
                    {
                        result.Skipped.Add($"{voice.Name}, no partials");
                        continue;
                    }

                    string requested = voice.Name.Trim();
                    string stored = MeterVoiceLibrary.SaveUserVoice(voice);
                    result.Imported.Add(
                        string.Equals(stored, requested, StringComparison.Ordinal)
                            ? stored
                            : $"{stored} (the name {requested} is a built-in)");
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }
            return result;
        }
    }
}
