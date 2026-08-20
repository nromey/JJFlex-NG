using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace JJFlexWpf;

/// <summary>
/// The TabControl behind a category list (Sprint 32 Track G, task #134) —
/// same control, different automation peer, and the peer is the whole point.
///
/// <para><b>Why this class exists.</b> The CategoryTabControl style templates
/// the header strip away: the ControlTemplate is a bare
/// <c>PART_SelectedContentHost</c> ContentPresenter, with no TabPanel and no
/// ItemsPresenter, because the category ListBox is the selector now. Visually
/// and for the keyboard that is exactly right. For UI Automation it was fatal:
/// a plain TabControl's peer is an items-based
/// <see cref="TabControlAutomationPeer"/>, which enumerates children through
/// the control's items host — and this template has none. On a live tree walk
/// (2026-08-20, out-of-process, the same client path a screen reader uses) the
/// peer did not return an empty list, it <b>threw</b>: E_UNEXPECTED out of
/// GetFirstChild, ElementNotAvailable out of sibling navigation. The walk died
/// at the category list, every control inside every category was absent from
/// the tree, and no focus event ever surfaced — which a blind operator
/// experienced as real tab stops with total silence on every one, in the Audio
/// Workshop and in Settings both.</para>
///
/// <para><b>The fix.</b> Expose what the template actually is: a pane whose
/// visual children are the selected category's content. The base
/// <see cref="FrameworkElementAutomationPeer"/> builds children by walking the
/// visual tree, which for this template is precisely the selected content —
/// there is nothing items-based left to expose. Selection is the ListBox's
/// job, in UIA as at the keyboard; giving this peer a selection pattern too
/// would recreate in the automation tree the duplicate-selector leak the
/// category list was built to remove.</para>
///
/// <para>Everything else about TabControl — TabItem authoring, SelectedItem,
/// SelectionChanged, the CategoryNavigator sync — is inherited unchanged.
/// The CategoryTabControl style targets this class rather than TabControl so
/// the headerless template can never again ride on the peer that throws.</para>
/// </summary>
public class CategoryTabHost : TabControl
{
    protected override AutomationPeer OnCreateAutomationPeer()
        => new CategoryTabHostAutomationPeer(this);

    private sealed class CategoryTabHostAutomationPeer : FrameworkElementAutomationPeer
    {
        public CategoryTabHostAutomationPeer(CategoryTabHost owner) : base(owner) { }

        protected override AutomationControlType GetAutomationControlTypeCore()
            => AutomationControlType.Pane;

        protected override string GetClassNameCore() => nameof(CategoryTabHost);
    }
}
