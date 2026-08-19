using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// The ownership question, asked at the first moment an action needs the
/// answer (Sprint 31 Track S, task #94, ratified 2026-08-19).
///
/// <para><b>Why this is not a ConfirmActionDialog.</b> That dialog answers
/// yes-or-no, and Escape there is indistinguishable from No. Here they are
/// three different outcomes and the difference is load-bearing: "yes, mine"
/// and "no, someone else's" are both DURABLE answers that stop the question
/// being asked again, while Escape must record nothing at all. An operator who
/// backs out of a question they did not expect has not told us their radio
/// belongs to somebody else, and storing that would silently switch off a
/// feature they never declined.</para>
///
/// <para><b>What it does not do.</b> It does not check, verify, or enforce
/// anything. Ownership is a declaration of intent — the app protects an honest
/// operator from an accident, and does not defend against a dishonest one. The
/// body text says so plainly rather than implying a check happens somewhere.
/// </para>
///
/// <para>Screen-reader shape follows AdvisoryDialog: the body is a read-only
/// TextBox so it can be arrowed through and re-read at the operator's own
/// pace, focus lands there on open, and no button is IsDefault — a question
/// about writing to someone else's equipment should not be answerable by
/// muscle-memory Enter.</para>
/// </summary>
public sealed class RadioOwnershipDialog : JJFlexDialog
{
    private RadioOwnership? _answer;

    private RadioOwnershipDialog(string radioLabel, string reason, RadioOwnership suggestion)
    {
        Title = "Is this radio yours?";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 600;

        var root = new StackPanel { Margin = new Thickness(12) };

        string suggestionLine = suggestion == RadioOwnership.Mine
            ? "\n\nJJ Flex would guess this one is yours, going on how you have "
              + "reached it before. That is only a guess — it has no way to know, "
              + "and it will not decide for you."
            : "";

        var body = new TextBox
        {
            Text = ScreenReaderText.NormalizeLineBreaks(
                reason
                + "\n\nSome settings live on the radio itself rather than on this "
                + "computer, and every program connected to that radio shares them. "
                + "Changing one on a radio you are borrowing changes it for its "
                + "owner too, with nothing on their end to say why."
                + "\n\nSo before JJ Flex creates anything new on " + radioLabel
                + ", it would like to know whose radio it is."
                + suggestionLine
                + "\n\nThis is your word for it, nothing more — JJ Flex does not "
                + "check and cannot. It is here so you do not change a friend's "
                + "radio by accident, not to stop you doing anything on purpose."
                + "\n\nYou can change the answer later in Settings, on the Radios "
                + "tab. Pressing Escape answers nothing and leaves the question "
                + "for next time."),
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 380,
        };
        AutomationProperties.SetName(body, "Why JJ Flex is asking whose radio this is");
        root.Children.Add(body);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        var mine = MakeAnswer("It is _my radio", "Yes, this radio is mine",
            RadioOwnership.Mine);
        var theirs = MakeAnswer("It is s_omeone else's", "No, this radio belongs to someone else",
            RadioOwnership.SomeoneElses);

        var later = new Button
        {
            Content = "_Not now",
            MinWidth = 100,
            Height = 28,
            IsCancel = true,
        };
        AutomationProperties.SetName(later, "Not now, do not answer");
        AutomationProperties.SetAccessKey(later, "Alt+N");
        later.Click += (_, _) =>
        {
            _answer = null;
            try { DialogResult = false; } catch (InvalidOperationException) { }
            Close();
        };

        buttons.Children.Add(mine);
        buttons.Children.Add(theirs);
        buttons.Children.Add(later);
        root.Children.Add(buttons);

        System.Windows.Input.KeyboardNavigation.SetTabIndex(body, 1);
        System.Windows.Input.KeyboardNavigation.SetTabIndex(buttons, 2);

        Content = root;
    }

    private Button MakeAnswer(string label, string automationName, RadioOwnership value)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 130,
            Height = 28,
            Margin = new Thickness(0, 0, 8, 0),
            // Deliberately not IsDefault. See the class remarks.
            IsDefault = false,
        };
        AutomationProperties.SetName(button, automationName);
        button.Click += (_, _) =>
        {
            _answer = value;
            try { DialogResult = true; } catch (InvalidOperationException) { }
            Close();
        };
        return button;
    }

    /// <summary>
    /// Ask whose radio this is and record the answer against
    /// <paramref name="radioId"/>. Returns the answer, or null when the
    /// operator declined to answer — in which case NOTHING is stored and the
    /// caller must treat the radio as not-theirs for this action only.
    /// </summary>
    /// <param name="radioId">Serial (or backend id) the answer is keyed to.</param>
    /// <param name="radioLabel">How to name the radio in the question.</param>
    /// <param name="reason">The action that made the question necessary, in
    /// the operator's own terms. Leading with what they just tried to do is
    /// what keeps this from reading as an interrogation out of nowhere.</param>
    /// <param name="operatorAccount">The operator's own SmartLink account, if
    /// any, used only to pre-suggest an answer in the text.</param>
    public static RadioOwnership? Ask(string radioId, string radioLabel, string reason,
        string? operatorAccount = null)
    {
        if (string.IsNullOrEmpty(radioId)) return null;

        var cfg = RadioConfig.LoadForRadio(radioId);
        var dialog = new RadioOwnershipDialog(
            string.IsNullOrWhiteSpace(radioLabel) ? "this radio" : radioLabel,
            reason,
            cfg.SuggestOwnership(operatorAccount));
        dialog.ShowModalDialog();

        var answer = dialog._answer;
        if (answer == null) return null;

        // The save may fail (a locked file, a busy scanner). The operator's
        // answer still stands for this session — refusing an intent because
        // the disk was busy hands the disk's problem to the operator, and
        // RadioConfig.SaveForRadio already reports the failure once, centrally.
        RadioConfig.RecordOwnership(radioId, answer.Value);
        return answer;
    }
}
