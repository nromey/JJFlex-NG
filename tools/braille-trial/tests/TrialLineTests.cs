using JJBrailleTrial.Model;
using Xunit;

namespace JJBrailleTrial.Tests;

public class ViewStringTests
{
    [Fact]
    public void The_first_view_string_is_the_header_then_one_line_per_field()
    {
        var line = new TrialLine();
        string expected =
            "JJFV1\t1\t1\thome\t1\t0\t0\n" +
            "1\tV\t14.225.000\n" +
            "2\tC\tUSB\n" +
            "3\tR\tS7\n" +
            "4\tT\tVOX off\n" +
            "5\tT\tTuning modern";
        Assert.Equal(expected, line.BuildViewString());
    }

    [Fact]
    public void The_header_has_seven_tab_separated_parts_and_every_field_line_three()
    {
        var lines = new TrialLine().BuildViewString().Split('\n');
        Assert.Equal(7, lines[0].Split('\t').Length);
        Assert.Equal(6, lines.Length);
        for (int i = 1; i < lines.Length; i++)
        {
            Assert.Equal(3, lines[i].Split('\t').Length);
        }
    }

    [Fact]
    public void The_revision_stays_when_nothing_changed()
    {
        var line = new TrialLine();
        line.BuildViewString();
        line.BuildViewString();
        Assert.Equal(1, line.Revision);
    }

    [Fact]
    public void The_revision_goes_up_when_a_field_text_changes()
    {
        var line = new TrialLine();
        line.BuildViewString();
        line.ApplyRoute(ScreenLineTests.Accept(line, TrialLine.VoxOrdinal, null));
        string view = line.BuildViewString();
        Assert.Equal(2, line.Revision);
        Assert.Contains("4\tT\tVOX on", view, System.StringComparison.Ordinal);
    }

    [Fact]
    public void The_revision_goes_up_when_only_the_caret_moves()
    {
        var line = new TrialLine();
        line.BuildViewString();
        Assert.True(line.SetCaretFromIndex(TrialLine.FieldStart(TrialLine.ModeOrdinal)));
        string header = line.BuildViewString().Split('\n')[0];
        Assert.Equal(2, line.Revision);
        Assert.Equal("JJFV1\t2\t1\thome\t2\t0\t0", header);
    }

    [Fact]
    public void Switching_view_changes_the_header_and_not_the_layout()
    {
        var line = new TrialLine();
        line.BuildViewString();
        Assert.Equal(BrailleView.Field, line.SwitchView());
        string header = line.BuildViewString().Split('\n')[0];
        Assert.Equal("JJFV1\t2\t1\tfield\t1\t0\t0", header);
        Assert.Equal(1, line.Layout);
    }

    [Fact]
    public void A_tuning_walk_step_changes_the_frequency_and_the_meter_lines()
    {
        var line = new TrialLine();
        string before = line.BuildViewString();
        line.WalkStep();
        string after = line.BuildViewString();
        Assert.NotEqual(before, after);
        Assert.Contains("1\tV\t14.225.100", after, System.StringComparison.Ordinal);
        Assert.Contains("3\tR\tS6", after, System.StringComparison.Ordinal);
    }
}

public class ScreenLineTests
{
    [Fact]
    public void Fields_sit_in_fixed_slots_one_space_apart()
    {
        var line = new TrialLine();
        Assert.Equal("14.225.000 USB S7 VOX off Tuning modern ", line.DisplayText);
        Assert.Equal(0, TrialLine.FieldStart(TrialLine.FrequencyOrdinal));
        Assert.Equal(11, TrialLine.FieldStart(TrialLine.ModeOrdinal));
        Assert.Equal(15, TrialLine.FieldStart(TrialLine.MeterOrdinal));
        Assert.Equal(18, TrialLine.FieldStart(TrialLine.VoxOrdinal));
        Assert.Equal(26, TrialLine.FieldStart(TrialLine.TuningOrdinal));
    }

    [Fact]
    public void A_toggle_changing_width_does_not_move_the_fields_after_it()
    {
        var line = new TrialLine();
        int before = line.DisplayText.Length;
        line.ApplyRoute(Accept(line, TrialLine.VoxOrdinal, null));
        Assert.Equal("14.225.000 USB S7 VOX on  Tuning modern ", line.DisplayText);
        Assert.Equal(before, line.DisplayText.Length);
    }

    [Theory]
    [InlineData(0, TrialLine.FrequencyOrdinal, 0)]
    [InlineData(7, TrialLine.FrequencyOrdinal, 7)]
    [InlineData(10, TrialLine.FrequencyOrdinal, 9)]   // the gap belongs to the field on its left
    [InlineData(11, TrialLine.ModeOrdinal, 0)]
    [InlineData(24, TrialLine.VoxOrdinal, 6)]
    [InlineData(39, TrialLine.TuningOrdinal, 12)]     // padding sits on the last character
    public void A_screen_index_maps_to_its_field_and_character(int index, int ordinal, int ch)
    {
        Assert.Equal((ordinal, ch), new TrialLine().PositionToField(index));
    }

    internal static RouteDecision Accept(TrialLine line, int ordinal, int? ch)
        => BridgeProtocol.DecideRoute(new RouteMessage(line.Layout, ordinal, ch), line);
}

public class RouteDecodingTests
{
    [Fact]
    public void A_route_round_trips_through_the_two_numbers()
    {
        var (w, l) = BridgeProtocol.EncodeRoute(1, TrialLine.FrequencyOrdinal, 7);
        Assert.Equal(new RouteMessage(1, 1, 7), BridgeProtocol.DecodeRoute(w, l));
        Assert.Equal(1u * 65536u + 8u, l);
    }

    [Fact]
    public void Character_code_zero_means_not_known()
    {
        var (w, l) = BridgeProtocol.EncodeRoute(1, TrialLine.ModeOrdinal, null);
        Assert.Equal(2u * 65536u, l);
        Assert.Null(BridgeProtocol.DecodeRoute(w, l).CharPos);
    }

    [Fact]
    public void Only_the_low_32_bits_are_read()
    {
        ulong w = 0xFFFF_FFFF_0000_0001UL;
        ulong l = 0xABCD_0000_0003_0001UL;
        Assert.Equal(new RouteMessage(1, 3, 0), BridgeProtocol.DecodeRoute(w, l));
    }

    [Fact]
    public void A_stale_layout_number_is_dropped_and_says_why()
    {
        var line = new TrialLine();
        var d = BridgeProtocol.DecideRoute(new RouteMessage(2, TrialLine.VoxOrdinal, null), line);
        Assert.False(d.Accepted);
        Assert.Equal("stale layout 2, current is 1", d.Reason);
        var outcome = line.ApplyRoute(d);
        Assert.False(outcome.Done);
        Assert.Null(outcome.Spoken);
        Assert.False(line.VoxOn);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(65535)]
    public void An_ordinal_that_names_no_field_is_dropped(int ordinal)
    {
        var d = BridgeProtocol.DecideRoute(new RouteMessage(1, ordinal, 0), new TrialLine());
        Assert.False(d.Accepted);
        Assert.StartsWith("no field with ordinal", d.Reason, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(500)]
    public void A_character_past_the_field_is_treated_as_not_known(int ch)
    {
        var d = BridgeProtocol.DecideRoute(new RouteMessage(1, TrialLine.FrequencyOrdinal, ch), new TrialLine());
        Assert.True(d.Accepted);
        Assert.Null(d.CharPos);
        Assert.Contains("outside frequency (length 10)", d.Reason, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Hello_carries_reader_version_and_display_size()
    {
        var (w, l) = BridgeProtocol.EncodeHello(BridgeProtocol.ReaderNvda, 1, 1, 40);
        Assert.Equal(new HelloMessage(1, 1, 1, 40), BridgeProtocol.DecodeHello(w, l));
    }

    [Fact]
    public void Command_carries_the_command_and_the_reader()
    {
        Assert.Equal(new CommandMessage(1, 2), BridgeProtocol.DecodeCommand(1, 2));
    }
}

public class RouteActionTests
{
    [Fact]
    public void A_route_on_a_frequency_digit_moves_the_caret_there_and_names_the_digit()
    {
        var line = new TrialLine();
        var outcome = line.ApplyRoute(ScreenLineTests.Accept(line, TrialLine.FrequencyOrdinal, 7));
        Assert.Equal("frequency, digit 6, 100 hertz", outcome.Spoken);
        Assert.Equal(TrialLine.FrequencyOrdinal, line.CaretOrdinal);
        Assert.Equal(7, line.CaretChar);
        Assert.Equal(7, line.CaretIndex);
    }

    [Fact]
    public void A_route_on_a_dot_lands_on_the_next_digit()
    {
        var line = new TrialLine();
        var outcome = line.ApplyRoute(ScreenLineTests.Accept(line, TrialLine.FrequencyOrdinal, 2));
        Assert.Equal(3, line.CaretChar);
        Assert.Equal("frequency, digit 3, 100 kilohertz", outcome.Spoken);
    }

    [Fact]
    public void A_route_with_no_character_lands_on_the_first_digit()
    {
        var line = new TrialLine();
        line.SetCaretFromIndex(TrialLine.FieldStart(TrialLine.MeterOrdinal));
        var outcome = line.ApplyRoute(ScreenLineTests.Accept(line, TrialLine.FrequencyOrdinal, null));
        Assert.Equal(0, line.CaretChar);
        Assert.Equal("frequency, digit 1, 10 megahertz", outcome.Spoken);
    }

    [Fact]
    public void A_route_on_the_mode_moves_the_caret_and_says_the_mode()
    {
        var line = new TrialLine();
        var outcome = line.ApplyRoute(ScreenLineTests.Accept(line, TrialLine.ModeOrdinal, 1));
        Assert.Equal("mode, USB", outcome.Spoken);
        Assert.Equal(TrialLine.ModeOrdinal, line.CaretOrdinal);
        Assert.Equal(0, line.CaretChar);
    }

    [Fact]
    public void A_route_on_the_meter_says_the_reading()
    {
        var line = new TrialLine();
        Assert.Equal("S meter, S7", line.ApplyRoute(ScreenLineTests.Accept(line, TrialLine.MeterOrdinal, 0)).Spoken);
    }

    [Fact]
    public void A_route_on_a_toggle_flips_it_and_leaves_the_caret()
    {
        var line = new TrialLine();
        line.ApplyRoute(ScreenLineTests.Accept(line, TrialLine.FrequencyOrdinal, 8));
        var outcome = line.ApplyRoute(ScreenLineTests.Accept(line, TrialLine.VoxOrdinal, 2));
        Assert.Equal("VOX on", outcome.Spoken);
        Assert.True(line.VoxOn);
        Assert.Equal(TrialLine.FrequencyOrdinal, line.CaretOrdinal);
        Assert.Equal(8, line.CaretChar);
        Assert.Equal("VOX off", line.ApplyRoute(ScreenLineTests.Accept(line, TrialLine.VoxOrdinal, 0)).Spoken);
    }
}

public class TuningTests
{
    [Fact]
    public void The_operator_sequence_classic_then_the_100_hertz_digit_then_up_and_down()
    {
        // Noel, 2026-11-21 15:20: route on the tuning toggle switches to classic,
        // route on the 100 Hz digit puts the caret there, Up and Down tune by 100 Hz.
        var line = new TrialLine();
        line.BuildViewString();

        var toggled = line.ApplyRoute(ScreenLineTests.Accept(line, TrialLine.TuningOrdinal, 3));
        Assert.Equal("Tuning classic", toggled.Spoken);
        Assert.Contains("5\tT\tTuning classic", line.BuildViewString(), System.StringComparison.Ordinal);

        line.ApplyRoute(ScreenLineTests.Accept(line, TrialLine.FrequencyOrdinal, 7));
        Assert.Equal(100, line.PlaceValue(line.CaretChar));

        var up = line.Tune(1);
        Assert.True(up.Done);
        Assert.Equal("14.225.100", up.Spoken);
        Assert.Contains("1\tV\t14.225.100", line.BuildViewString(), System.StringComparison.Ordinal);
        Assert.Equal(7, line.CaretChar);

        var down = line.Tune(-1);
        Assert.Equal("14.225.000", down.Spoken);
        line.Tune(-1);
        Assert.Equal(14_224_900, line.FrequencyHz);
        Assert.Equal("14.224.900", TrialLine.FormatFrequency(line.FrequencyHz));
    }

    [Theory]
    [InlineData(0, 10_000_000)]
    [InlineData(1, 1_000_000)]
    [InlineData(3, 100_000)]
    [InlineData(5, 1_000)]
    [InlineData(7, 100)]
    [InlineData(9, 1)]
    [InlineData(2, 0)]
    [InlineData(6, 0)]
    public void Each_character_has_its_place_value(int ch, long place)
    {
        Assert.Equal(place, new TrialLine().PlaceValue(ch));
    }

    [Fact]
    public void Modern_tuning_steps_one_kilohertz_wherever_the_caret_is()
    {
        var line = new TrialLine();
        line.SetCaretFromIndex(TrialLine.FieldStart(TrialLine.VoxOrdinal));
        var up = line.Tune(1);
        Assert.True(up.Done);
        Assert.Equal(14_226_000, line.FrequencyHz);
    }

    [Fact]
    public void Classic_tuning_refuses_when_the_caret_is_not_on_a_frequency_digit()
    {
        var line = new TrialLine();
        line.ApplyRoute(ScreenLineTests.Accept(line, TrialLine.TuningOrdinal, null));
        line.SetCaretFromIndex(TrialLine.FieldStart(TrialLine.ModeOrdinal));
        var outcome = line.Tune(1);
        Assert.False(outcome.Done);
        Assert.Equal(14_225_000, line.FrequencyHz);

        line.SetCaretFromIndex(6); // the second dot
        Assert.False(line.Tune(1).Done);
    }

    [Fact]
    public void Tuning_stops_at_the_limits_and_says_so()
    {
        var line = new TrialLine();
        line.ApplyRoute(ScreenLineTests.Accept(line, TrialLine.TuningOrdinal, null));
        line.ApplyRoute(ScreenLineTests.Accept(line, TrialLine.FrequencyOrdinal, 0));
        Assert.True(line.Tune(1).Done);   // 24.225.000
        Assert.True(line.Tune(1).Done);   // 34.225.000
        Assert.True(line.Tune(1).Done);   // 44.225.000
        Assert.False(line.Tune(1).Done);  // 54.225.000 would pass the top
        Assert.Equal(44_225_000, line.FrequencyHz);
    }

    [Fact]
    public void Space_flips_a_toggle_and_refuses_elsewhere()
    {
        var line = new TrialLine();
        Assert.False(line.PressSpace().Done);
        line.SetCaretFromIndex(TrialLine.FieldStart(TrialLine.TuningOrdinal));
        var outcome = line.PressSpace();
        Assert.True(outcome.Done);
        Assert.True(line.ClassicTuning);
        Assert.StartsWith("space: ", outcome.TraceText, System.StringComparison.Ordinal);
    }
}
