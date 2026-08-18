using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace JJFlexWpf.Controls
{
    /// <summary>
    /// A TextBlock that is visible on screen and INVISIBLE to a screen reader.
    ///
    /// **For headings and labels whose control already introduces itself.** A
    /// bold "Network identity" above a card that already carries
    /// AutomationProperties.Name="Network identity" is not extra information —
    /// it is the same words twice. The sighted operator needs the heading to
    /// know what the control below is; the screen reader operator does not,
    /// because the control says so itself when focus lands on it.
    ///
    /// Static text is read as part of a dialog's BODY when the dialog opens,
    /// which is why these duplicates are heard at the worst possible moment:
    /// during startup, before the operator has done anything. Deleting the
    /// heading would fix speech and break the visual layout. Removing it from
    /// the UIA control view fixes speech and changes nothing on screen.
    ///
    /// Use for decoration ONLY. If a piece of text carries information that no
    /// control repeats, it belongs in the tree and should stay a plain
    /// TextBlock — silence is not an improvement when the words were the only
    /// place the information lived.
    ///
    /// Related: <see cref="ValueFieldControl"/> solves the mirror-image problem
    /// with GetChildrenCore() => null, hiding a child TextBlock so the control's
    /// own name is read instead of both.
    /// </summary>
    public class DecorativeText : TextBlock
    {
        protected override AutomationPeer OnCreateAutomationPeer()
            => new DecorativePeer(this);

        private sealed class DecorativePeer : TextBlockAutomationPeer
        {
            public DecorativePeer(TextBlock owner) : base(owner) { }

            // Out of the control view — the view screen readers walk when
            // reading a dialog body and when moving by control.
            protected override bool IsControlElementCore() => false;

            // Out of the content view too. Leaving it in the content view would
            // keep it readable by review cursor, which sounds harmless but is
            // exactly the duplication being removed.
            protected override bool IsContentElementCore() => false;
        }
    }
}
