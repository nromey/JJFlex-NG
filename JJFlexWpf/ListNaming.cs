using System.Windows.Controls;

namespace JJFlexWpf;

/// <summary>
/// A list whose accessible name carries a count ("Tracking notches, 3 on this
/// radio", "Crash reports waiting for an answer, 2 on this computer") has to
/// keep that count true as rows come and go, without the screen reader
/// speaking every rename. One rule for every such list (junk box repair one,
/// the tracking notch list and the crash report list).
/// </summary>
/// <remarks>
/// A screen reader speaks a rename of the object it is focused ON. When focus
/// is on a row, the list is that row's container and a rename is not spoken,
/// so the list is renamed at once. The one state held back is the list
/// element itself holding keyboard focus while it has rows: the name is then
/// owed, and paid when the list's own focus moves. An EMPTY list holding
/// focus is renamed at once, because "none" is the news, said once. Whether
/// a given reader really stays quiet for a row's container is a bench
/// question for the harness (UI Automation name plus an NVDA IO-level
/// transcript), not something these tests can show.
/// </remarks>
internal static class ListNaming
{
    /// <summary>Whether the list may be renamed now.</summary>
    internal static bool RenameNow(bool listItselfFocused, bool namingOnFocus, int rows)
        => !listItselfFocused || namingOnFocus || rows == 0;

    /// <summary>
    /// Put focus on a row, so the reader lands on it and the list itself does
    /// not keep focus with a name owed; the list when there is no row.
    /// </summary>
    internal static void FocusRow(ListBox list, int index)
    {
        if (index >= 0 && index < list.Items.Count)
        {
            list.UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem row && row.Focus())
                return;
        }
        list.Focus();
    }
}
