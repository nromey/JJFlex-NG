using System.ComponentModel;
using JJFlexWpf.Dialogs;
using Radios;
using Xunit;

namespace JJFlexWpf.Tests;

/// <summary>
/// A radio list row tells its bindings when to re-read its text (#1008), so
/// the printed text and the accessible name, both bound to
/// <see cref="RadioListItem.DisplayText"/>, follow the row.
/// </summary>
/// <remarks>
/// <b>Desk safety.</b> A plain data object only, on an MTA thread (asserted
/// first), where WPF refuses to build any element. No dialog, no window.
/// </remarks>
public sealed class RadioListItemTextTests
{
    [Fact]
    public void A_row_raises_its_text_changed_and_the_text_is_the_rows_current_state()
    {
        NoiseReductionPerChannelTests.AssertWindowless();
        Lexicon.Load(Lexicon.Partitions);
        var row = new RadioListItem { Serial = "4925-1213-8600-6245", Name = "Bench", ModelName = "FLEX-8600" };
        var raised = new List<string?>();
        ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        string before = row.DisplayText;
        row.LanAvailable = true;
        row.NotifyDisplayTextChanged();

        Assert.Equal(new[] { nameof(RadioListItem.DisplayText) }, raised);
        Assert.Equal(row.ToString(), row.DisplayText);   // the accessible fallback reads the same text
        Assert.NotEqual(before, row.DisplayText);        // control: the change is one the text shows
    }
}
