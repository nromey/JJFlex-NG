using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Radios;
using Radios.Speech;

namespace JJFlexWpf.Dialogs;

/// <summary>What Save As was asked to write. The recordings dialog does the conversion, off the UI thread.</summary>
internal sealed record RecordedQsoSaveRequest(string Destination, RecordedQsoExport.Format Format, int Channels,
    int Bitrate, long StartFrame, long? EndFrame, double? Seconds);

/// <summary>
/// The rules Save As follows, without a window (#787, Tape Deck track 4), so
/// each can be tested: which file types exist, what extension each has, which
/// options each offers, and the name and the type staying in step.
/// </summary>
internal static class RecordedQsoSaveAs
{
    internal sealed record Kind(RecordedQsoExport.Format Format, string Extension, string LabelKey,
        bool HasChannels, int[] Bitrates, int DefaultBitrate);

    /// <summary>
    /// Every type, in the order the combo offers them. Bitrates in kilobits per
    /// second; MP3 64 and Opus 32 are Noel's ruled defaults (2026-10-22 08:09).
    /// FLAC and WAV have no bitrate. M4A is HE-AAC mono at 32 kbps only, as
    /// track 3 built it, so it offers neither choice.
    /// </summary>
    internal static readonly IReadOnlyList<Kind> Kinds = new[]
    {
        new Kind(RecordedQsoExport.Format.Flac, ".flac", "audio.qsos.saveas_type_flac", true, Array.Empty<int>(), 0),
        new Kind(RecordedQsoExport.Format.Wav, ".wav", "audio.qsos.saveas_type_wav", true, Array.Empty<int>(), 0),
        new Kind(RecordedQsoExport.Format.Mp3, ".mp3", "audio.qsos.saveas_type_mp3", true, new[] { 32, 48, 64, 96, 128 }, 64),
        new Kind(RecordedQsoExport.Format.Opus, ".opus", "audio.qsos.saveas_type_opus", true, new[] { 24, 32, 48, 64 }, 32),
        new Kind(RecordedQsoExport.Format.M4a, ".m4a", "audio.qsos.saveas_type_m4a", false, Array.Empty<int>(), 32),
    };

    internal static Kind For(RecordedQsoExport.Format format) => Kinds.First(k => k.Format == format);

    /// <summary>The type a name's extension names, or null when it names none of ours.</summary>
    internal static Kind? KindOfName(string name)
    {
        string ext = Path.GetExtension(name ?? "");
        return Kinds.FirstOrDefault(k => string.Equals(k.Extension, ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The name with its extension made <paramref name="kind"/>'s: one of
    /// ours is replaced, anything else is kept and ours added after it
    /// (Noel, 2026-10-24 15:58: the name and the format can never disagree).
    /// </summary>
    internal static string WithExtension(string name, Kind kind)
    {
        name = (name ?? "").TrimEnd();
        if (KindOfName(name) != null) name = name.Substring(0, name.Length - Path.GetExtension(name).Length);
        return name + kind.Extension;
    }

    /// <summary>
    /// A first suggestion that is never the source and never an existing
    /// file: the recording's own name, "-trim" for a part of it, and "-2",
    /// "-3" as needed. In the recording's own year and month folder, every
    /// time (Noel, 2026-10-24 16:00).
    /// </summary>
    internal static string SuggestName(string sourcePath, bool whole, Kind kind, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        string folder = Path.GetDirectoryName(sourcePath) ?? "";
        string stem = Path.GetFileNameWithoutExtension(sourcePath) + (whole ? "" : "-trim");
        string name = stem + kind.Extension;
        int n = 2;
        while (exists(Path.Combine(folder, name)) || SameFile(Path.Combine(folder, name), sourcePath))
            name = stem + "-" + n++ + kind.Extension;
        return name;
    }

    /// <summary>
    /// The name to show after the file type changes. While the name is still
    /// the one suggested, suggest again for the new type; once the operator
    /// has edited it, keep their name and change only its extension.
    /// </summary>
    /// <remarks>
    /// Retest 2026-10-25: Save As to MP3 offered "QSO-...-172707-2.mp3". The
    /// first suggestion is made for FLAC, the first type, and a FLAC of the
    /// same name IS the source, so it took "-2"; changing the type then only
    /// swapped the extension and kept a "-2" that no longer meant anything.
    /// </remarks>
    internal static string NameForType(string current, string? suggested, string sourcePath, bool whole,
        Kind kind, Func<string, bool>? exists = null) =>
        suggested != null && string.Equals(current, suggested, StringComparison.Ordinal)
            ? SuggestName(sourcePath, whole, kind, exists)
            : WithExtension(current, kind);

    internal static bool SameFile(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Save As for one recorded QSO (#787, Tape Deck track 4): file name, folder
/// and Browse, file type, the range being saved, then the type's options
/// (channels, bitrate), then Save and Cancel. Escape cancels and changes
/// nothing. The conversion itself runs after this closes, so it never traps
/// the keyboard here.
/// </summary>
/// <remarks>
/// MP3 and M4A depend on Windows. When Windows cannot make one, it stays in
/// the list but cannot be chosen, the reason is on the File type box's
/// Ctrl+F1 note, and typing its extension says the reason aloud. Which path
/// the MP3 sentence should name is an open question for Noel
/// (<c>ASK-tape-deck-t4-mp3.md</c>); it is one lexicon key.
/// </remarks>
internal sealed class RecordedQsoSaveAsDialog : JJFlexDialog
{
    private readonly RecordedQso _source;
    private readonly long _startFrame;
    private readonly long? _endFrame;
    private readonly double? _seconds;
    private readonly TextBox _name = new();
    private readonly TextBox _folder = new();
    private readonly ComboBox _type = new();
    private readonly TextBox _range = new();
    private readonly ComboBox _channels = new();
    private readonly ComboBox _bitrate = new();
    private readonly Label _channelsLabel, _bitrateLabel;
    private readonly TextBlock _note = new();
    private readonly Dictionary<RecordedQsoExport.Format, string> _unavailable = new();
    private bool _syncing;
    private RecordedQsoSaveAs.Kind _kind = RecordedQsoSaveAs.Kinds[0];
    private readonly bool _whole;
    private string? _suggested;     // the name as last suggested, until the operator edits it

    /// <summary>The answer, when Save was pressed and everything checked out.</summary>
    internal RecordedQsoSaveRequest? Request { get; private set; }

    internal RecordedQsoSaveAsDialog(RecordedQso source, bool whole, long startFrame, long? endFrame,
        string rangeSentence, double? seconds)
    {
        _source = source;
        _startFrame = startFrame;
        _endFrame = endFrame;
        _seconds = seconds;
        Title = Lexicon.Get("audio.qsos.saveas_title");
        Width = 560;
        SizeToContent = SizeToContent.Height;

        if (!RecordedQsoExport.Mp3Available())
            _unavailable[RecordedQsoExport.Format.Mp3] = Lexicon.Get("audio.qsos.saveas_mp3_unavailable");
        if (!RecordedQsoExport.HeAacM4aAvailable())
            _unavailable[RecordedQsoExport.Format.M4a] = Lexicon.Get("audio.qsos.saveas_m4a_unavailable");

        var root = new StackPanel { Margin = new Thickness(12) };
        Field(root, "audio.qsos.saveas_name", _name);
        _whole = whole;
        _name.Text = _suggested = RecordedQsoSaveAs.SuggestName(source.Path, whole, _kind);
        _name.TextChanged += (_, _) => OnNameChanged();

        var folderRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var browse = new Button { Content = Lexicon.Get("audio.qsos.saveas_browse"), Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(8, 0, 0, 0) };
        AutomationProperties.SetName(browse, Lexicon.Get("audio.qsos.saveas_browse"));
        browse.Click += (_, _) => Browse();
        DockPanel.SetDock(browse, Dock.Right);
        var folderLabel = new Label { Content = Lexicon.Get("audio.qsos.saveas_folder"), Target = _folder, Padding = new Thickness(0, 4, 8, 0) };
        DockPanel.SetDock(folderLabel, Dock.Left);
        AutomationProperties.SetName(_folder, Lexicon.Get("audio.qsos.saveas_folder"));
        _folder.Text = Path.GetDirectoryName(source.Path) ?? "";
        folderRow.Children.Add(folderLabel);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(_folder);
        root.Children.Add(folderRow);

        foreach (var kind in RecordedQsoSaveAs.Kinds)
        {
            string label = Lexicon.Get(kind.LabelKey);
            var item = new ComboBoxItem { Content = label, Tag = kind, IsEnabled = !_unavailable.ContainsKey(kind.Format) };
            AutomationProperties.SetName(item, label);
            _type.Items.Add(item);
        }
        Field(root, "audio.qsos.saveas_type", _type);
        _type.SelectedIndex = 0;
        JJFlexHelp.SetText(_type, Lexicon.Get("audio.qsos.saveas_type_help"));
        _type.SelectionChanged += (_, _) => OnTypeChanged();

        _range.IsReadOnly = true;
        _range.IsReadOnlyCaretVisible = true;
        _range.TextWrapping = TextWrapping.Wrap;
        _range.Text = whole
            ? Lexicon.Get("audio.qsos.saveas_range_whole", ("length", RecordedQsoWords.Length(seconds)))
            : rangeSentence;
        Field(root, "audio.qsos.saveas_range", _range);

        _channelsLabel = Field(root, "audio.qsos.saveas_channels", _channels);
        foreach (var (count, key) in new[] { (1, "audio.qsos.saveas_mono"), (2, "audio.qsos.saveas_stereo") })
        {
            var item = new ComboBoxItem { Content = Lexicon.Get(key), Tag = count };
            AutomationProperties.SetName(item, Lexicon.Get(key));
            _channels.Items.Add(item);
        }
        _channels.SelectedIndex = source.Channels == 2 ? 1 : 0;

        _bitrateLabel = Field(root, "audio.qsos.saveas_bitrate", _bitrate);

        _note.TextWrapping = TextWrapping.Wrap;
        _note.Margin = new Thickness(0, 0, 0, 8);
        if (_unavailable.Count > 0)
        {
            _note.Text = string.Join(" ", _unavailable.Values);
            JJFlexHelp.SetNoteFor(_note, _type);
        }
        root.Children.Add(_note);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = Lexicon.Get("audio.qsos.saveas_save"), MinWidth = 80, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        AutomationProperties.SetName(save, Lexicon.Get("audio.qsos.saveas_save").Replace("_", ""));
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = Lexicon.Get("audio.qsos.saveas_cancel"), MinWidth = 80, Height = 28 };
        AutomationProperties.SetName(cancel, Lexicon.Get("audio.qsos.saveas_cancel"));
        cancel.Click += (_, _) => CloseWithResult(false);
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;

        ShowOptionsFor(_kind);
        Loaded += (_, _) => { _name.Focus(); _name.SelectAll(); };
    }

    private Label Field(Panel parent, string labelKey, Control control)
    {
        var label = new Label { Content = Lexicon.Get(labelKey), Target = control, Padding = new Thickness(0, 0, 0, 2) };
        AutomationProperties.SetName(control, Lexicon.Get(labelKey));
        control.Margin = new Thickness(0, 0, 0, 8);
        parent.Children.Add(label);
        parent.Children.Add(control);
        return label;
    }

    protected override void FocusFirstControl() => _name.Focus();

    private void OnTypeChanged()
    {
        if (_syncing || _type.SelectedItem is not ComboBoxItem { Tag: RecordedQsoSaveAs.Kind kind }) return;
        _kind = kind;
        _syncing = true;
        try
        {
            int caret = _name.CaretIndex;
            string next = RecordedQsoSaveAs.NameForType(_name.Text, _suggested, _source.Path, _whole, kind);
            if (string.Equals(_name.Text, _suggested, StringComparison.Ordinal)) _suggested = next;
            _name.Text = next;
            _name.CaretIndex = Math.Min(caret, _name.Text.Length);
        }
        finally { _syncing = false; }
        ShowOptionsFor(kind);
    }

    private void OnNameChanged()
    {
        if (_syncing) return;
        var named = RecordedQsoSaveAs.KindOfName(_name.Text);
        if (named == null || named.Format == _kind.Format) return;
        if (_unavailable.TryGetValue(named.Format, out string? why))
        {
            Speak(why, refusal: true);
            return;
        }
        _syncing = true;
        try
        {
            _kind = named;
            _type.SelectedIndex = RecordedQsoSaveAs.Kinds.ToList().IndexOf(named);
        }
        finally { _syncing = false; }
        ShowOptionsFor(named);
    }

    private void ShowOptionsFor(RecordedQsoSaveAs.Kind kind)
    {
        var channels = kind.HasChannels ? Visibility.Visible : Visibility.Collapsed;
        _channels.Visibility = channels;
        _channelsLabel.Visibility = channels;
        _bitrate.Items.Clear();
        foreach (int kbps in kind.Bitrates)
        {
            string label = Lexicon.Get("audio.qsos.saveas_kbps", ("kbps", kbps));
            var item = new ComboBoxItem { Content = label, Tag = kbps };
            AutomationProperties.SetName(item, label);
            _bitrate.Items.Add(item);
        }
        _bitrate.SelectedIndex = Array.IndexOf(kind.Bitrates, kind.DefaultBitrate);
        var bitrate = kind.Bitrates.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _bitrate.Visibility = bitrate;
        _bitrateLabel.Visibility = bitrate;
    }

    private void Browse()
    {
        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            InitialDirectory = Directory.Exists(_folder.Text) ? _folder.Text : RecordedQsoCatalog.FolderPath,
        };
        if (picker.ShowDialog(this) == true) _folder.Text = picker.FolderName;
        _folder.Focus();
    }

    private void Save()
    {
        string name = _name.Text.Trim();
        if (name.Length == 0) { Speak(Lexicon.Get("audio.qsos.saveas_no_name"), true); _name.Focus(); return; }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        { Speak(Lexicon.Get("audio.qsos.saveas_bad_name"), true); _name.Focus(); return; }
        var named = RecordedQsoSaveAs.KindOfName(name);
        if (named != null && _unavailable.TryGetValue(named.Format, out string? why))
        { Speak(why, true); _name.Focus(); return; }
        if (_unavailable.ContainsKey(_kind.Format))
        { Speak(_unavailable[_kind.Format], true); _type.Focus(); return; }
        name = RecordedQsoSaveAs.WithExtension(name, _kind);
        string folder = _folder.Text.Trim();
        if (!Directory.Exists(folder)) { Speak(Lexicon.Get("audio.qsos.saveas_no_folder"), true); _folder.Focus(); return; }
        string destination = Path.Combine(folder, name);
        if (RecordedQsoSaveAs.SameFile(destination, _source.Path))
        { Speak(Lexicon.Get("audio.qsos.saveas_same_file"), true); _name.Focus(); return; }
        if (File.Exists(destination))
        {
            var replace = new ConfirmActionDialog(Lexicon.Get("audio.qsos.saveas_replace_title"),
                Lexicon.Get("audio.qsos.saveas_replace", ("file", name), ("folder", folder)),
                question: Lexicon.Get("audio.qsos.saveas_replace_question")) { Owner = this };
            if (replace.ShowModalDialog() != true) { _name.Focus(); return; }
        }
        int channels = _kind.HasChannels && _channels.SelectedItem is ComboBoxItem { Tag: int c } ? c : 1;
        int bitrate = _bitrate.SelectedItem is ComboBoxItem { Tag: int kbps } ? kbps * 1000 : _kind.DefaultBitrate * 1000;
        Request = new RecordedQsoSaveRequest(destination, _kind.Format, channels, bitrate, _startFrame, _endFrame, _seconds);
        CloseWithResult(true);
    }

    // DRAFT PROSE (#787): awaiting Noel.
    private static void Speak(string text, bool refusal = false)
    {
        if (refusal) EarconPlayer.LeaderInvalidTone();
        ScreenReaderOutput.Speak(text, SpeechIntent.Interrupt, refusal ? VerbosityLevel.Critical : VerbosityLevel.Terse,
            subject: SpeechSubject.RecordedQsos);
    }
}
