using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Radios;
using WinFormsKeys = System.Windows.Forms.Keys;

namespace JJFlexWpf.Dialogs
{
    /// <summary>
    /// A window around <see cref="SystemWideKeysPanel"/> (#307). Since Sprint
    /// 48 Track E the configuration lives in Settings, under System-wide keys,
    /// and nothing in the menus opens this window: the Tools item of that name
    /// deep-links to the Settings tab exactly as "Diagnostic Log" does, and the
    /// Keys dialog routes there from a system-wide row. Noel, 2026-10-05:
    /// <i>"I'd have the ptt toggle stuff in a settings tab somewhere, no need
    /// for two nested dialogs do you?"</i>
    /// </summary>
    /// <remarks>
    /// Kept rather than deleted so the one-dialog-at-a-time accessibility
    /// sweep, which realises every <see cref="JJFlexDialog"/> subclass through
    /// its parameterless constructor, still has a window for the panel; and so
    /// a caller that wants the panel modally on its own — none today — has one.
    /// Deleting it is the orchestrator's call, not a track's (#602).
    /// </remarks>
    public sealed class SystemWideKeysDialog : JJFlexDialog
    {
        /// <summary>
        /// This program's own bindings of a chord, for the panel's conflict
        /// report. Forwarded to <see cref="SystemWideKeysPanel.InAppConflictsSource"/>,
        /// which every host of the panel reads; kept here because the main
        /// window wires it under this name.
        /// </summary>
        public static Func<WinFormsKeys, IReadOnlyList<string>>? InAppConflictsSource
        {
            get => SystemWideKeysPanel.InAppConflictsSource;
            set => SystemWideKeysPanel.InAppConflictsSource = value;
        }

        private readonly SystemWideKeysPanel _panel;

        /// <summary>Open on the saved configuration. The dialog sweep's constructor.</summary>
        public SystemWideKeysDialog()
            : this(SystemWideKeysConfig.Load(RadioConfig.AppDataRoot), RadioConfig.AppDataRoot) { }

        public SystemWideKeysDialog(SystemWideKeysConfig config, string configDirectory)
        {
            Title = Lexicon.Get("settings.systemwide_keys.title");
            Width = 620;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;

            _panel = new SystemWideKeysPanel(config, configDirectory);

            var root = new StackPanel { Margin = new Thickness(16) };
            root.Children.Add(_panel);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = Lexicon.Get("settings.systemwide_keys.ok"), MinWidth = 96, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            ok.Click += (_, _) =>
            {
                string? outcome = _panel.Commit();
                if (outcome != null)
                    ScreenReaderOutput.Speak(outcome, Radios.Speech.SpeechIntent.Interrupt, VerbosityLevel.Critical,
                        subject: Radios.Speech.SpeechSubject.SystemWideKeyPicker);
                CloseWithResult(true);
            };
            buttons.Children.Add(ok);
            var cancel = new Button { Content = Lexicon.Get("settings.systemwide_keys.cancel"), MinWidth = 96, Height = 28, IsCancel = true };
            cancel.Click += (_, _) => CloseWithResult(false);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            Content = root;
        }

        /// <summary>Open modally over the given owner.</summary>
        public static void Show(Window? owner)
        {
            var dialog = new SystemWideKeysDialog();
            if (owner != null) dialog.Owner = owner;
            dialog.ShowModalDialog();
        }

        /// <summary>
        /// A capture in progress owns every key, Escape included — the base
        /// class would otherwise read Escape as "close the dialog".
        /// </summary>
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (_panel.HandleCaptureKey(e)) return;
            base.OnPreviewKeyDown(e);
        }
    }
}
