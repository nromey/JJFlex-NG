using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace JJBrailleTrial;

/// <summary>
/// The Home-like control, built the way Home is today: a read-only text box
/// whose automation peer is deliberately silent. Copied from the pattern of
/// SilentTextBox in JJFlexWpf/Controls/FrequencyDisplay.xaml.cs, not referenced:
/// a FrameworkElementAutomationPeer with control type Custom, so no Value or
/// Text pattern and no text or caret events reach a screen reader. The reader
/// side gets the line only from the carrier beside it.
/// </summary>
public sealed class SilentHomeBox : TextBox
{
    protected override AutomationPeer OnCreateAutomationPeer() => new SilentHomePeer(this);

    private sealed class SilentHomePeer : FrameworkElementAutomationPeer
    {
        public SilentHomePeer(FrameworkElement owner) : base(owner) { }

        protected override string GetClassNameCore() => "FrequencyDisplay";

        protected override AutomationControlType GetAutomationControlTypeCore()
            => AutomationControlType.Custom;
    }
}

/// <summary>
/// The carrier element of design section 4.2: an invisible element beside the
/// Home control whose automation Name is the view string, with automation id
/// JJFlexBrailleView, marked neither control nor content so no reader lands on
/// it or reads it (the pattern of JJFlexWpf/Controls/DecorativeText.cs).
/// It draws nothing and has no size.
/// </summary>
public sealed class BrailleCarrier : FrameworkElement
{
    public const string CarrierAutomationId = "JJFlexBrailleView";

    public BrailleCarrier()
    {
        Width = 0;
        Height = 0;
        Focusable = false;
        AutomationProperties.SetAutomationId(this, CarrierAutomationId);
    }

    public string ViewText { get; private set; } = "";

    /// <summary>
    /// Change the Name and raise UI Automation's property-changed event for it.
    /// Returns whether any UI Automation client was listening for property
    /// changes at all, which the trace records: it is evidence about whether a
    /// reader could have heard the event.
    /// </summary>
    public bool SetViewText(string text)
    {
        string old = ViewText;
        ViewText = text;
        bool listening = AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged);
        if (listening)
        {
            var peer = UIElementAutomationPeer.FromElement(this) ?? UIElementAutomationPeer.CreatePeerForElement(this);
            peer?.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, old, text);
        }
        return listening;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CarrierPeer(this);

    private sealed class CarrierPeer : FrameworkElementAutomationPeer
    {
        private readonly BrailleCarrier _owner;

        public CarrierPeer(BrailleCarrier owner) : base(owner) => _owner = owner;

        protected override string GetNameCore() => _owner.ViewText;

        protected override string GetAutomationIdCore() => CarrierAutomationId;

        protected override string GetClassNameCore() => "JJFlexBrailleView";

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;
    }
}
