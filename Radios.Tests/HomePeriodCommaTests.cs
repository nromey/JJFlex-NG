using System;
using System.Linq;
using Xunit;

namespace Radios.Tests
{
    /// <summary>
    /// Noel's ruling, 2026-10-25 19:31: period creates a slice and comma
    /// releases one, from EVERY field on the Home slice row, Frequency
    /// included, decided by context. Once a digit has been typed, number entry
    /// owns the keys and period is the decimal point. A period with no digit
    /// before it is never a decimal: "you should always type a zero first."
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read from source, with the same Roslyn scan <see cref="FieldKeyMapTests"/>
    /// uses to reconcile each field's handler with the inventory, because the
    /// handlers take a WPF <c>KeyEventArgs</c>, which needs an input manager on
    /// an STA thread, and this suite builds no window. The scan sees which keys
    /// each field's handler claims and whether a private mode field gates the
    /// claim; the source checks below hold the ORDER on the Frequency field,
    /// which is what makes "context" mean anything. Neither replaces pressing
    /// period on S Meter, typing 14.2 on Frequency, and pressing a bare period
    /// on Frequency, at the bench.
    /// </para>
    /// </remarks>
    public sealed class HomePeriodCommaTests
    {
        private const string Handlers = "JJFlexWpf/FreqOutHandlers.cs";

        private static string Body(string source, string signature)
        {
            int at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, signature + " was not found");
            int open = source.IndexOf('{', at);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }
            throw new InvalidOperationException(signature + " has no end");
        }

        [Fact]
        public void A_period_or_comma_on_any_slice_row_field_is_a_slice_key_S_Meter_included()
        {
            var result = FieldKeyMapScan.ScanRepository();
            Assert.Contains(result.Fields, f => f.FieldKey == "SMeter");
            Assert.True(result.Fields.Count >= 15, "only " + result.Fields.Count + " Home fields scanned");

            foreach (var field in result.Fields)
            {
                // Ungated: claimed in the resting state, not only inside a mode.
                Assert.True(field.Claims.Any(c => c.Binding.Display == "." && !c.StateGated),
                    field.Where + " does not take a bare period");
                Assert.True(field.Claims.Any(c => c.Binding.Display == "," && !c.StateGated),
                    field.Where + " does not take a bare comma");
            }

            // And the claim is the slice create and release, in the one place
            // the universal Home keys live.
            string universal = Body(FieldKeyMapScan.ReadSource(Handlers),
                "private bool TryHandleUniversalHomeKey(KeyEventArgs e, bool numberEntryActive = false)");
            int period = universal.IndexOf("if (ch == '.' && !numberEntryActive)", StringComparison.Ordinal);
            int comma = universal.IndexOf("if (ch == ',' && !numberEntryActive)", StringComparison.Ordinal);
            Assert.True(period >= 0, "the universal period no longer yields to number entry");
            Assert.True(comma >= 0, "the universal comma no longer yields to number entry");
            Assert.True(universal.IndexOf("CreateSliceAndAnnounce();", period, StringComparison.Ordinal) < comma,
                "the universal period does not create a slice");
            Assert.Contains("ReleaseActiveSliceAndAnnounce();", universal.Substring(comma));
        }

        [Theory]
        [InlineData("public void AdjustFreq(FrequencyDisplay.DisplayField field, KeyEventArgs e)")]
        [InlineData("public void AdjustFreqModern(FrequencyDisplay.DisplayField field, KeyEventArgs e)")]
        public void On_Frequency_a_period_after_a_digit_is_the_decimal_point(string handler)
        {
            // "14.2": the digits start number entry, so quick-type must see the
            // period first and keep it; only then may the universal keys run,
            // and they are told number entry holds.
            string src = FieldKeyMapScan.ReadSource(Handlers);
            string body = Body(src, handler);
            int quickType = body.IndexOf("if (HandleQuickTypeKey(key, ch, e)) return;", StringComparison.Ordinal);
            int universal = body.IndexOf("TryHandleUniversalHomeKey(e, numberEntryActive: _inQuickType);", StringComparison.Ordinal);
            Assert.True(quickType >= 0, "the Frequency field no longer offers keys to number entry");
            Assert.True(universal > quickType,
                "the slice keys run before number entry, so a typed 14.2 would create a slice at the point");

            string quick = Body(src, "private bool HandleQuickTypeKey(Key key, char ch, KeyEventArgs e)");
            Assert.Contains("if (ch == '.' && _inQuickType)", quick);
            Assert.Contains("QuickTypeDot();", quick);
            Assert.Contains("if (ch >= '0' && ch <= '9')", quick);
        }

        [Fact]
        public void On_Frequency_a_period_with_no_digit_before_it_is_never_a_decimal()
        {
            string src = FieldKeyMapScan.ReadSource(Handlers);

            // Number entry begins at a digit and nowhere else: QuickTypeDigit is
            // the only writer of "true", and QuickTypeDot refuses to start it.
            int setters = src.Split("_inQuickType = true").Length - 1;
            Assert.Equal(1, setters);
            Assert.Contains("_inQuickType = true", Body(src, "private void QuickTypeDigit(char digit)"));
            Assert.Contains("if (!_inQuickType) return;", Body(src, "private void QuickTypeDot()"));

            // So with no digit typed the Frequency field's universal fall-through
            // is told number entry is idle, and the scan sees the Frequency
            // contexts take a bare period as a slice key (the resting state),
            // beside quick-type's period, which the mode field gates.
            var freq = FieldKeyMapScan.ScanRepository().Fields.Where(f => f.Context.StartsWith("Freq", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, freq.Count);
            foreach (var f in freq)
            {
                Assert.Contains(f.Claims, c => c.Binding.Display == "." && !c.StateGated && c.Method == "TryHandleUniversalHomeKey");
                Assert.Contains(f.Claims, c => c.Binding.Display == "." && c.StateGated && c.Gate.Contains("_inQuickType"));
            }
        }

        [Fact]
        public void On_Frequency_a_comma_after_a_digit_is_a_thousands_separator()
        {
            // Noel, 2026-10-25 20:35: "if you're typing frequencies, it could
            // be a thousands separator." Number entry takes the comma, gated by
            // its own state, ahead of the slice keys; the parse is held in
            // JJFlexWpf.Tests (QuickTypeCommitTests).
            string src = FieldKeyMapScan.ReadSource(Handlers);
            string quick = Body(src, "private bool HandleQuickTypeKey(Key key, char ch, KeyEventArgs e)");
            int comma = quick.IndexOf("if (ch == ',' && _inQuickType)", StringComparison.Ordinal);
            Assert.True(comma >= 0, "number entry no longer takes the comma");
            Assert.Contains("QuickTypeSeparator();", quick.Substring(comma));

            var freq = FieldKeyMapScan.ScanRepository().Fields.Where(f => f.Context.StartsWith("Freq", StringComparison.Ordinal)).ToList();
            Assert.Equal(2, freq.Count);
            foreach (var f in freq)
                Assert.Contains(f.Claims, c => c.Binding.Display == "," && c.StateGated && c.Gate.Contains("_inQuickType"));
        }

        [Fact]
        public void The_Slice_field_keeps_no_copy_of_its_own()
        {
            // One home for the pair. A second copy on the Slice field would
            // drift from the universal one, and did not know about number entry.
            string slice = Body(FieldKeyMapScan.ReadSource(Handlers),
                "public void AdjustSlice(FrequencyDisplay.DisplayField field, KeyEventArgs e)");
            Assert.DoesNotContain("case '.':", slice);
            Assert.DoesNotContain("case ',':", slice);
            Assert.DoesNotContain("Rig.NewSlice()", slice);
            Assert.Contains("TryHandleUniversalHomeKey(e);", slice);
        }
    }
}
