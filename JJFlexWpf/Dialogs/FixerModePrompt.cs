using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Radios.Fixer;

namespace JJFlexWpf.Dialogs;

/// <summary>
/// The Fixer's mode hand-off (#411): the four transmit-audio modes, offered
/// from inside a run, the way the frequency hand-off offers the frequency.
/// </summary>
/// <remarks>
/// <para>
/// <b>RULED BY NOEL 2026-08-30:</b> <i>"I think it needs a mode control, not
/// all 12 modes but the basics."</i> The list is
/// <see cref="TransmitStageSet.TransmitAudioModes"/> — LSB, USB, DIGU, DIGL,
/// exactly four, because those are the modes with a real transmit-audio path,
/// which is the thing the tool tests. This dialog does not own the list, the
/// words for each mode, or any radio truth: the list and descriptions live in
/// <see cref="TransmitStageSet"/> where a test pins them, and what the radio
/// reports is read by the caller and handed in.
/// </para>
/// <para>
/// <b>When the radio is in a mode outside the list — CW, AM, FM — that mode is
/// NOT added as a fifth entry.</b> The header says what the radio is on, and
/// the operator changes it or keeps it. A "current mode" entry for a mode with
/// no transmit-audio path would offer a button whose only effect is to do
/// nothing, and pad the ruled list to five.
/// </para>
/// <para>
/// Follows the FixerExitPrompt pattern: the situation is a read-only text box
/// focus lands in on open — the base dialog speaks the title, the reader then
/// reads where the radio stands at their own pace, and Tab reaches the
/// choices. Each mode button carries what its acronym means as help text,
/// announced on focus, so the labels stay the words operators actually say.
/// Keeping the mode is the default and the Escape action: a stray Enter or a
/// reflexive Escape must cost nothing.
/// </para>
/// </remarks>
public sealed class FixerModePrompt : JJFlexDialog
{
    private string _chosen = "";

    private FixerModePrompt(string reportedMode)
    {
        Title = "Change the transmit mode — JJ Flexible";
        Width = 480;
        SizeToContent = SizeToContent.Height;

        var root = new StackPanel { Margin = new Thickness(12) };

        var text = new TextBox
        {
            Text = ScreenReaderText.NormalizeLineBreaks(Situation(reportedMode)),
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 160,
        };
        AutomationProperties.SetName(text, Radios.Lexicon.Get("connect.dialog.message_label"));
        root.Children.Add(text);

        var buttons = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };

        foreach (string mode in TransmitStageSet.TransmitAudioModes)
        {
            // NO PER-MODE DESCRIPTION. Four buttons that each explained a
            // sideband convention told the operator nothing they did not
            // already know, four times over - and repeating one sentence per
            // button is the tell that it was not worth saying once.
            string help = "";
            // The button for the mode the radio is already in says so in its
            // help text — a blind operator arrowing through the choices can
            // tell where they stand without going back to the header.
            if (string.Equals(mode, (reportedMode ?? "").Trim(),
                              StringComparison.OrdinalIgnoreCase))
                help = "This is the mode the radio is in now. " + help;

            var button = new Button
            {
                Content = mode,
                MinWidth = 220,
                Height = 30,
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            AutomationProperties.SetName(button, mode);
            JJFlexHelp.SetText(button, help);
            button.Click += (_, _) =>
            {
                _chosen = mode;
                CloseWithResult(true);
            };
            buttons.Children.Add(button);
        }

        var keep = new Button
        {
            Content = "Keep the mode as it is",
            MinWidth = 220,
            Height = 30,
            Margin = new Thickness(0, 0, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsDefault = true,
            IsCancel = true,
        };
        AutomationProperties.SetName(keep, "Keep the mode as it is");
        JJFlexHelp.SetText(keep, "Nothing changes. You go back to the tests where "
                               + "you left off.");
        // IsDefault registers the literal \r character as an access key, and
        // NVDA reads it back as "carriage return". Explicit values preempt
        // the phantom one — the AdvisoryDialog precedent.
        AutomationProperties.SetAcceleratorKey(keep, "Enter");
        buttons.Children.Add(keep);

        root.Children.Add(buttons);
        Content = root;
    }

    /// <summary>
    /// Where the radio stands, said before any choice is offered. States what
    /// the radio NOW REPORTS — the caller reads it live — never what anything
    /// asked for.
    /// </summary>
    private static string Situation(string reportedMode)
    {
        string mode = (reportedMode ?? "").Trim();

        if (mode.Length == 0)
            return "The radio is not reporting a transmit mode right now. You can "
                 + "still ask it to change to one of these — whether it does will "
                 + "be confirmed from the radio's own report.";

        // ONE SENTENCE, AND IT IS ABOUT OUR TEST, NOT ABOUT SIDEBANDS.
        // Ruled by Noel 2026-08-30. The earlier version described each mode -
        // band plans, which convention sits above 10 MHz - which is a licensed
        // operator's own domain read back at them. "A ham radio operator should
        // know this stuff. Switch the mode, the radio operates as normal.
        // Change it so that you test the way you normally would use the radio."
        if (TransmitStageSet.IsTransmitAudioMode(mode))
            return "The radio is set to " + mode.ToUpperInvariant()
                 + ". Test in the mode you normally use.";

        // THE ONLY THING HERE A HAM CANNOT KNOW is why their current mode is not
        // on a list of four, so that is the whole sentence. The earlier version
        // spent a paragraph saying it was not offered without ever saying why.
        return "The radio is set to " + mode.ToUpperInvariant() + ". "
             + mode.ToUpperInvariant() + " has no transmit audio to test, so "
             + "choose one of these four.";
    }

    /// <summary>
    /// Ask, modally. Returns the chosen mode — one of
    /// <see cref="TransmitStageSet.TransmitAudioModes"/> — or empty when the
    /// operator kept what they had, which the caller treats as a cancel.
    /// </summary>
    public static string Ask(Window? owner, string reportedMode)
    {
        var prompt = new FixerModePrompt(reportedMode);
        if (owner != null) prompt.Owner = owner;
        prompt.ShowModalDialog();
        return prompt._chosen;
    }
}
