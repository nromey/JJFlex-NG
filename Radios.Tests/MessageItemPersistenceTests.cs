using System.IO;
using System.Windows.Forms;
using System.Xml.Serialization;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// The operator file still loads, and round-trips, with the recording
    /// name added to a message slot (Sprint 48 Track A, #151).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is tested, and its limit, stated plainly.</b> The persisted
    /// type is <c>CWMessages.MessageItem</c>, VB, in the application
    /// executable, which this test project cannot reference. What CAN be
    /// tested is the mechanism it relies on: <see cref="XmlSerializer"/> over
    /// public fields named <c>key</c>, <c>message</c>, <c>Label</c> and now
    /// <c>Audio</c>. The twin below has exactly those fields, in that order,
    /// with those types, so the XML it produces is the XML
    /// <c>personal_v1.CWText</c> produces. <c>MessageLibraryHomeTests</c>
    /// pins the VB side to that shape by reading its source.
    /// </para>
    /// <para>
    /// <b>The property that matters is the old file.</b> Every operator file
    /// in the field has no <c>Audio</c> element. It must load with the field
    /// null — not throw, not drop the slot — and the ten-slot round trip must
    /// carry mixed payloads unchanged.
    /// </para>
    /// </remarks>
    public sealed class MessageItemPersistenceTests
    {
        /// <summary>Field-for-field twin of CWMessages.MessageItem.</summary>
        public class MessageItem
        {
            public Keys key;
            public string? message;
            public string? Label;
            public string? Audio;
        }

        private static readonly XmlSerializer Serializer = new(typeof(MessageItem[]));

        private static MessageItem[] RoundTrip(MessageItem[] items)
        {
            using var w = new StringWriter();
            Serializer.Serialize(w, items);
            using var r = new StringReader(w.ToString());
            return (MessageItem[])Serializer.Deserialize(r)!;
        }

        [Fact]
        public void TenSlotsWithMixedPayloadsRoundTrip()
        {
            var items = new MessageItem[10];
            for (int i = 0; i < 10; i++)
            {
                Keys digit = i == 9 ? Keys.D0 : Keys.D1 + i;
                items[i] = new MessageItem
                {
                    key = digit | Keys.Control,
                    Label = "slot " + (i + 1),
                    message = i % 3 == 1 ? null : "CQ TEST DE K5NER " + i,   // audio-only every third
                    Audio = i % 3 == 2 ? null : "take-2026-10-05-10150" + i,  // text-only every third
                };
            }

            var back = RoundTrip(items);

            Assert.Equal(10, back.Length);
            for (int i = 0; i < 10; i++)
            {
                Assert.Equal(items[i].key, back[i].key);
                Assert.Equal(items[i].Label, back[i].Label);
                Assert.Equal(items[i].message, back[i].message);
                Assert.Equal(items[i].Audio, back[i].Audio);
            }
        }

        /// <summary>
        /// The XML an operator file carried before this track: three elements
        /// per slot and no Audio. It must load with Audio null.
        /// </summary>
        [Fact]
        public void AnOperatorFileWrittenBeforeTheRecordingFieldExistedStillLoads()
        {
            const string old = @"<?xml version=""1.0"" encoding=""utf-16""?>
<ArrayOfMessageItem xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"">
  <MessageItem>
    <key>Control D1</key>
    <message>CQ CQ DE K5NER</message>
    <Label>CQ</Label>
  </MessageItem>
  <MessageItem>
    <key>Control D2</key>
    <message>5NN TN</message>
    <Label>Exchange</Label>
  </MessageItem>
</ArrayOfMessageItem>";

            using var r = new StringReader(old);
            var back = (MessageItem[])Serializer.Deserialize(r)!;

            Assert.Equal(2, back.Length);
            Assert.Equal(Keys.D1 | Keys.Control, back[0].key);
            Assert.Equal("CQ CQ DE K5NER", back[0].message);
            Assert.Equal("CQ", back[0].Label);
            Assert.Null(back[0].Audio);
            Assert.Null(back[1].Audio);
        }

        /// <summary>
        /// Positive control for the test above: the serializer really does
        /// write the element names the hand-written XML uses, so a rename of
        /// a field would fail here rather than silently loading nothing.
        /// </summary>
        [Fact]
        public void TheSerializerWritesTheElementNamesTheOldFileUses()
        {
            using var w = new StringWriter();
            Serializer.Serialize(w, new[] { new MessageItem { key = Keys.D1 | Keys.Control, message = "x", Label = "y", Audio = "z" } });
            string xml = w.ToString();
            // XmlSerializer writes a [Flags] value's names in declaration
            // order ("D1 Control"); the hand-written old file above uses the
            // order a person writes ("Control D1"). Both parse, as the test
            // above proves. What is pinned here is the element NAME and that
            // both names of the chord are in it.
            var key = System.Text.RegularExpressions.Regex.Match(xml, "<key>([^<]*)</key>");
            Assert.True(key.Success, "no <key> element was written");
            Assert.Contains("D1", key.Groups[1].Value);
            Assert.Contains("Control", key.Groups[1].Value);
            Assert.Contains("<message>x</message>", xml);
            Assert.Contains("<Label>y</Label>", xml);
            Assert.Contains("<Audio>z</Audio>", xml);
        }
    }
}
