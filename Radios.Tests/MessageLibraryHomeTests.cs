using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Where the message library lives, and where it does not (Sprint 48
    /// Track A, #151).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>RecordingStore is the home and the radio's DVK is not.</b>
    /// <c>RecordingStore</c>'s own remarks say why: DVK slots are STATION
    /// state, owned by whoever owns the rig and visible to every MultiFlex
    /// client; a recording is OPERATOR state and has to work on a radio the
    /// operator cannot write to. That is also Noel's 2026-10-02 isolation
    /// invariant, and it is the same conclusion Jim reached for CW messages
    /// when he saved them with the operator. FlexLib's <c>DVK</c> class is
    /// complete and may become a second backend later; nothing authored here
    /// may write a recording into it today.
    /// </para>
    /// <para>
    /// <b>Source rules, with their positive controls.</b> "No authored file
    /// references the DVK" is a claim that the scan would have SEEN one, so
    /// the first test also proves the vendor file it is about exists and
    /// carries the verbs in question. The VB shape rule pins what
    /// <c>MessageItemPersistenceTests</c> relies on: the persisted VB type has
    /// exactly the fields the C# twin serialises.
    /// </para>
    /// </remarks>
    // In the collection because one test below reloads the lexicon, which
    // resolves the process-wide settings root (#232).
    [Collection(RadioConfigStaticsCollection.Name)]
    public sealed class MessageLibraryHomeTests
    {
        private static readonly string[] DvkVerbs =
        {
            "UploadRecording", "DVKCommandType", "DVKRecording", "DownloadWAVFile", "new DVK(", ".DVK.",
        };

        [Fact]
        public void NothingAuthoredWritesARecordingIntoTheRadiosDvk()
        {
            // Positive control: the vendor class is there and has the verbs,
            // so a scan that found nothing in our code found nothing because
            // there is nothing, not because the words changed.
            string vendor = Path.Combine(IntegrationPassTree.Root, "FlexLib_API", "FlexLib", "DVK.cs");
            Assert.True(File.Exists(vendor), "FlexLib's DVK.cs is gone; the control for this rule is missing.");
            string vendorText = File.ReadAllText(vendor);
            Assert.Contains("UploadRecording", vendorText);
            Assert.Contains("DVKCommandType", vendorText);

            var hits = new List<string>();
            int scanned = 0;
            foreach (string file in IntegrationPassTree.AuthoredSource)
            {
                if (IntegrationPassTree.IsTest(file)) continue;
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".cs" && ext != ".vb") continue;
                scanned++;
                string text = File.ReadAllText(file);
                foreach (string verb in DvkVerbs)
                {
                    if (text.IndexOf(verb, StringComparison.Ordinal) >= 0)
                        hits.Add(IntegrationPassTree.Relative(file) + " names " + verb);
                }
            }
            Assert.True(scanned > 200, "Only " + scanned + " authored source files scanned; the tree was not found.");
            Assert.True(hits.Count == 0,
                "Authored code reaches for the radio's DVK. RecordingStore is the home for recordings and "
                + "the message library points at it; the DVK is a possible second backend, not where this "
                + "lives (RecordingStore remarks; #151). " + string.Join("; ", hits));
        }

        /// <summary>
        /// The VB operator data has the field the message key reads, in the
        /// order the persistence twin serialises it, and the operator file
        /// still carries the array.
        /// </summary>
        [Fact]
        public void JimsMessageItemCarriesTheRecordingNameAndIsStillOperatorData()
        {
            string cw = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "CWMessages.vb"));
            int key = cw.IndexOf("Public key As Keys", StringComparison.Ordinal);
            int message = cw.IndexOf("Public message As String", StringComparison.Ordinal);
            int label = cw.IndexOf("Public Label As String", StringComparison.Ordinal);
            int audio = cw.IndexOf("Public Audio As String", StringComparison.Ordinal);
            Assert.True(key > 0 && message > key && label > message && audio > label,
                "CWMessages.MessageItem must carry key, message, Label, Audio as public fields in that "
                + "order: XmlSerializer writes fields in declaration order and MessageItemPersistenceTests "
                + "mirrors that shape.");

            string personal = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "PersonalData.vb"));
            Assert.Contains("Public CWText As MessageItem()", personal);
            Assert.Contains("Friend Sub UpdateCWText(op As personal_v1, cwt As MessageItem())", personal);
        }

        /// <summary>
        /// The C# item the key tables read has the same two payloads, so the
        /// VB-to-C# bridge in globals.vb can carry both.
        /// </summary>
        [Fact]
        public void TheBridgeCarriesTheRecordingNameAcross()
        {
            string globals = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "globals.vb"));
            Assert.Contains("New CWMessageItem(CWText(i).key, CWText(i).message, CWText(i).Label, CWText(i).Audio)", globals);
        }

        /// <summary>
        /// The empty-library sentence must be true for both payload kinds. A
        /// voice-only operator who presses Ctrl+1 with nothing configured
        /// must not be told about CW.
        /// </summary>
        [Fact]
        public void TheNoMessagesSentenceIsTrueForVoiceAndCw()
        {
            Lexicon.Forget();
            Lexicon.Load(Lexicon.Partitions);
            string line = Lexicon.Get("settings.cw.no_messages_configured");
            Assert.False(string.IsNullOrWhiteSpace(line));
            Assert.DoesNotContain("CW", line, StringComparison.Ordinal);
            Assert.Contains("Messages", line, StringComparison.Ordinal);   // names the door
        }

        /// <summary>
        /// Every sentence the message key can say is keyed to one speech
        /// subject, so a refusal replaces a stale "Sending" rather than
        /// queueing behind it. An unkeyed emitter in this family is the
        /// settled-SWR defect (#503) in a new coat.
        /// </summary>
        [Fact]
        public void EveryMessageKeySentenceDeclaresTheMessageKeySubject()
        {
            string sender = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "VoiceMessageSender.cs"));
            int speaks = CountOf(sender, "ScreenReaderOutput.Speak(");
            int keyed = CountOf(sender, "subject: SpeechSubject.MessageKey");
            Assert.True(speaks > 0, "VoiceMessageSender no longer speaks; this rule is scanning nothing.");
            Assert.Equal(speaks, keyed);

            string kc = File.ReadAllText(Path.Combine(IntegrationPassTree.Root, "JJFlexWpf", "KeyCommands.cs"));
            int at = kc.IndexOf("protected void SendCWMessage()", StringComparison.Ordinal);
            Assert.True(at > 0, "SendCWMessage has been renamed; re-anchor this rule.");
            int end = kc.IndexOf("Leader Key System", at, StringComparison.Ordinal);
            string body = kc.Substring(at, end - at);
            Assert.Equal(CountOf(body, "ScreenReaderOutput.Speak("), CountOf(body, "SpeechSubject.MessageKey"));
        }

        private static int CountOf(string text, string needle)
        {
            int n = 0, at = 0;
            while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
            return n;
        }
    }
}
