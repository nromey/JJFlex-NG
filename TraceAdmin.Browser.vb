Imports System.Globalization
Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports JJTrace
Imports Radios

''' <summary>
''' Saved Diagnostic Logs — reads the LZMA manifest, presents archived sessions
''' as a sortable filterable ListView, and wires up the action buttons (View Trace,
''' Copy Path, Export Selected, Delete Selected, Prune Now). All speech-channel
''' announcements honor the no-silent-keystrokes rule (every action speaks).
''' Per design at docs/planning/active/trace-archive-navigation-design.md
''' (Sprint 29 Track H) and docs/planning/active/diagnostic-log-surface.md §3.
'''
''' Built in Sprint 29 and unreachable until Sprint 30: nothing in the app ever
''' instantiated the form that hosted it, so its whole test checklist stayed
''' unticked because no tester could get to it.
''' </summary>
Partial Class TraceAdmin

    ' Cached manifest entries (full set; filter copies into _shown).
    Private _allEntries As List(Of TraceSessionEntry) = New List(Of TraceSessionEntry)()
    Private _shownEntries As List(Of TraceSessionEntry) = New List(Of TraceSessionEntry)()

    ' Has the browser tab been initialized at least once? Lazy-load on first activation.
    Private _browserInitialized As Boolean = False

    ' ListView sort state.
    Private _sortColumn As Integer = 0
    Private _sortDescending As Boolean = True ' default: newest first

    ' Suppress filter-pipeline runs while we're programmatically updating UI state.
    Private _suspendFilter As Boolean = False

    ' --------------------------------------------------------------------
    ' Initialization. This used to hang off a tab activation; with the Tracing
    ' tab gone the browser IS the window, so TraceAdmin_Load calls it.
    ' --------------------------------------------------------------------

    Friend Sub InitializeArchiveBrowser()
        Try
            ' Columns
            ArchiveListView.Columns.Add(Radios.Lexicon.Get("logging.browser.col_date"), 150, HorizontalAlignment.Left)
            ArchiveListView.Columns.Add(Radios.Lexicon.Get("logging.browser.col_duration"), 80, HorizontalAlignment.Left)
            ArchiveListView.Columns.Add(Radios.Lexicon.Get("logging.browser.col_outcome"), 180, HorizontalAlignment.Left)
            ArchiveListView.Columns.Add(Radios.Lexicon.Get("logging.browser.col_target"), 220, HorizontalAlignment.Left)
            ArchiveListView.Columns.Add(Radios.Lexicon.Get("logging.browser.col_size"), 80, HorizontalAlignment.Right)

            ' Outcome dropdown — first item is "Any", followed by every known outcome.
            FilterOutcomeCombo.Items.Clear()
            FilterOutcomeCombo.Items.Add(Radios.Lexicon.Get("logging.browser.filter_any"))
            For Each kvp In TraceOutcomeLabels.AllOutcomes()
                FilterOutcomeCombo.Items.Add(kvp.Value)
            Next
            FilterOutcomeCombo.SelectedIndex = 0

            ' Default the date pickers but leave them unchecked (means "no bound").
            FilterFromDate.Value = Date.Today.AddDays(-30)
            FilterFromDate.Checked = False
            FilterToDate.Value = Date.Today
            FilterToDate.Checked = False

            ' Load the manifest into _allEntries.
            ReloadManifestCache()
            ApplyFilterAndRefreshList(announce:=False)
        Catch ex As Exception
            Tracing.ErrMessageTrace(ex)
        End Try
    End Sub

    ' --------------------------------------------------------------------
    ' Manifest cache — load once on tab activation, refresh after destructive
    ' actions or Prune Now.
    ' --------------------------------------------------------------------

    Private Sub ReloadManifestCache()
        Try
            Dim manifestPath As String = Path.Combine(TraceArchiveDir, SessionArchive.ManifestFileName)
            Dim manifest As TraceManifest = TraceManifest.Load(manifestPath)
            _allEntries = If(manifest.Entries, New List(Of TraceSessionEntry)())
        Catch ex As Exception
            Tracing.ErrMessageTrace(ex)
            _allEntries = New List(Of TraceSessionEntry)()
        End Try
    End Sub

    ' --------------------------------------------------------------------
    ' Filter pipeline — runs in-memory LINQ over the cached manifest.
    ' --------------------------------------------------------------------

    Private Function CurrentOutcomeFilter() As String
        If FilterOutcomeCombo.SelectedIndex <= 0 Then Return Nothing ' "Any"
        Dim selected As String = TryCast(FilterOutcomeCombo.SelectedItem, String)
        If String.IsNullOrEmpty(selected) Then Return Nothing
        For Each kvp In TraceOutcomeLabels.AllOutcomes()
            If String.Equals(kvp.Value, selected, StringComparison.Ordinal) Then Return kvp.Key
        Next
        Return Nothing
    End Function

    Private Function FilterEntries() As List(Of TraceSessionEntry)
        Dim outcomeKey As String = CurrentOutcomeFilter()
        Dim search As String = If(FilterSearchBox.Text, String.Empty).Trim()
        Dim hasFromBound As Boolean = FilterFromDate.Checked
        Dim hasToBound As Boolean = FilterToDate.Checked
        Dim fromUtc As DateTime = If(hasFromBound, FilterFromDate.Value.Date.ToUniversalTime(), DateTime.MinValue)
        Dim toUtc As DateTime = If(hasToBound, FilterToDate.Value.Date.AddDays(1).ToUniversalTime(), DateTime.MaxValue)

        Dim q = _allEntries.AsEnumerable()
        If hasFromBound Then q = q.Where(Function(en) en.BootTime >= fromUtc)
        If hasToBound Then q = q.Where(Function(en) en.BootTime < toUtc)
        If Not String.IsNullOrEmpty(outcomeKey) Then q = q.Where(Function(en) String.Equals(en.Outcome, outcomeKey, StringComparison.Ordinal))
        If search.Length > 0 Then
            Dim s As String = search
            q = q.Where(Function(en) EntryMatchesSearch(en, s))
        End If
        Return SortEntries(q.ToList())
    End Function

    Private Shared Function EntryMatchesSearch(entry As TraceSessionEntry, search As String) As Boolean
        If entry Is Nothing Then Return False
        If Not String.IsNullOrEmpty(entry.OutcomeDetail) AndAlso
           entry.OutcomeDetail.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 Then Return True
        If entry.ConnectionTarget IsNot Nothing Then
            If MatchField(entry.ConnectionTarget.Serial, search) Then Return True
            If MatchField(entry.ConnectionTarget.Nickname, search) Then Return True
            If MatchField(entry.ConnectionTarget.SmartlinkAccount, search) Then Return True
            If MatchField(entry.ConnectionTarget.Ip, search) Then Return True
        End If
        Return False
    End Function

    Private Shared Function MatchField(field As String, search As String) As Boolean
        Return Not String.IsNullOrEmpty(field) AndAlso
               field.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
    End Function

    Private Function SortEntries(entries As List(Of TraceSessionEntry)) As List(Of TraceSessionEntry)
        Dim cmp As Comparison(Of TraceSessionEntry)
        Select Case _sortColumn
            Case 0 ' Date
                cmp = Function(a, b) a.BootTime.CompareTo(b.BootTime)
            Case 1 ' Duration
                cmp = Function(a, b) (If(a.DurationMs, 0L)).CompareTo(If(b.DurationMs, 0L))
            Case 2 ' Outcome (display name)
                cmp = Function(a, b) String.Compare(TraceOutcomeLabels.Display(a.Outcome), TraceOutcomeLabels.Display(b.Outcome), StringComparison.OrdinalIgnoreCase)
            Case 3 ' Target
                cmp = Function(a, b) String.Compare(FormatTarget(a.ConnectionTarget), FormatTarget(b.ConnectionTarget), StringComparison.OrdinalIgnoreCase)
            Case 4 ' Size
                cmp = Function(a, b) (If(a.TraceSizeCompressedBytes, 0L)).CompareTo(If(b.TraceSizeCompressedBytes, 0L))
            Case Else
                cmp = Function(a, b) a.BootTime.CompareTo(b.BootTime)
        End Select

        entries.Sort(cmp)
        If _sortDescending Then entries.Reverse()
        Return entries
    End Function

    Private Sub ApplyFilterAndRefreshList(announce As Boolean)
        _shownEntries = FilterEntries()
        RefreshListView()
        UpdateFilterStatusLabel(announce)
        UpdateFooterLabel()
        UpdateSelectionDetail() ' selection may have changed if list shrank
    End Sub

    ' --------------------------------------------------------------------
    ' ListView population.
    ' --------------------------------------------------------------------

    Private Sub RefreshListView()
        ArchiveListView.BeginUpdate()
        Try
            ArchiveListView.Items.Clear()
            For Each entry In _shownEntries
                Dim item As New ListViewItem(HumanSessionPhrase(entry))
                item.Tag = entry
                item.SubItems.Add(FormatDuration(entry.DurationMs))
                item.SubItems.Add(TraceOutcomeLabels.Display(entry.Outcome))
                item.SubItems.Add(FormatTarget(entry.ConnectionTarget))
                item.SubItems.Add(FormatSize(entry.TraceSizeCompressedBytes))
                ArchiveListView.Items.Add(item)
            Next
        Finally
            ArchiveListView.EndUpdate()
        End Try
    End Sub

    Private Shared Function FormatBootTime(utc As DateTime) As String
        Return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
    End Function

    ''' <summary>
    ''' How a session reads out loud: "Tonight at 8:47 PM", "Yesterday at 3:12
    ''' PM", "14 Aug at 9:00 AM" — with detailed captures named as such, because
    ''' the one an operator is hunting for is nearly always the capture they
    ''' just took.
    '''
    ''' This is the phrasing the feedback dialog's session picker will need, so
    ''' it is written once and shared rather than reinvented there. It is the
    ''' FIRST column deliberately: a screen reader reads a row's first cell as
    ''' the row's identity, and "2026-08-18 20:47" is not how anyone remembers
    ''' when something went wrong.
    ''' </summary>
    Friend Shared Function HumanSessionPhrase(entry As TraceSessionEntry) As String
        Dim localTime As DateTime = entry.BootTime.ToLocalTime()
        Dim clock As String = localTime.ToString("h:mm tt", CultureInfo.InvariantCulture)
        Dim today As Date = Date.Now.Date
        Dim dayPart As String
        If localTime.Date = today Then
            dayPart = Radios.Lexicon.Get(If(localTime.Hour >= 17, "logging.browser.day_tonight", "logging.browser.day_today"))
        ElseIf localTime.Date = today.AddDays(-1) Then
            dayPart = Radios.Lexicon.Get("logging.browser.day_yesterday")
        Else
            dayPart = localTime.ToString("d MMM", CultureInfo.InvariantCulture)
        End If

        Dim prefix As String = ""
        If Not String.IsNullOrEmpty(entry.OutcomeDetail) AndAlso
           entry.OutcomeDetail.StartsWith(CaptureOutcomeDetailPrefix, StringComparison.Ordinal) Then
            prefix = Radios.Lexicon.Get("logging.browser.capture_prefix")
        End If
        Return Radios.Lexicon.Get("logging.browser.session_phrase",
                                  ("prefix", prefix), ("dayPart", dayPart), ("clock", clock))
    End Function

    Private Shared Function FormatDuration(durationMs As Long?) As String
        If Not durationMs.HasValue Then Return Radios.Lexicon.Get("logging.browser.empty_dash")
        Dim secs As Long = durationMs.Value \ 1000
        Dim hours As Long = secs \ 3600
        Dim mins As Long = (secs Mod 3600) \ 60
        Dim s As Long = secs Mod 60
        If hours > 0 Then
            Return String.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}:{2:D2}", hours, mins, s)
        End If
        Return String.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}", mins, s)
    End Function

    Private Shared Function FormatTarget(target As TraceConnectionTarget) As String
        If target Is Nothing Then Return Radios.Lexicon.Get("logging.browser.empty_dash")
        Dim parts As New List(Of String)()
        If Not String.IsNullOrEmpty(target.Nickname) Then parts.Add(target.Nickname)
        If Not String.IsNullOrEmpty(target.Serial) Then parts.Add(target.Serial)
        If parts.Count = 0 AndAlso Not String.IsNullOrEmpty(target.SmartlinkAccount) Then parts.Add(target.SmartlinkAccount)
        If parts.Count = 0 AndAlso Not String.IsNullOrEmpty(target.Ip) Then parts.Add(target.Ip)
        Return If(parts.Count = 0,
                  Radios.Lexicon.Get("logging.browser.empty_dash"),
                  String.Join(Radios.Lexicon.Get("logging.browser.target_join"), parts))
    End Function

    Private Shared Function FormatSize(bytes As Long?) As String
        If Not bytes.HasValue Then Return Radios.Lexicon.Get("logging.browser.empty_dash")
        Dim b As Long = bytes.Value
        If b < 1024 Then Return Radios.Lexicon.Get("logging.browser.size_bytes", ("value", b))
        If b < 1024L * 1024 Then Return Radios.Lexicon.Get("logging.browser.size_kb", ("value", b \ 1024))
        Return Radios.Lexicon.Get("logging.browser.size_mb", ("value", (b / (1024.0 * 1024.0)).ToString("F1")))
    End Function

    Private Shared Function FormatSizeSpoken(bytes As Long?) As String
        If Not bytes.HasValue Then Return Radios.Lexicon.Get("logging.browser.size_unknown")
        Dim b As Long = bytes.Value
        If b < 1024 Then Return Radios.Lexicon.Get("logging.browser.size_spoken_bytes", ("value", b))
        If b < 1024L * 1024 Then Return Radios.Lexicon.Get("logging.browser.size_spoken_kb", ("value", b \ 1024))
        Return Radios.Lexicon.Get("logging.browser.size_spoken_mb", ("value", (b / (1024.0 * 1024.0)).ToString("F1")))
    End Function

    ' --------------------------------------------------------------------
    ' Filter status label — also acts as the polite live region by speaking
    ' "N total, M shown" at Terse verbosity when filters change.
    ' --------------------------------------------------------------------

    Private Sub UpdateFilterStatusLabel(announce As Boolean)
        Dim total As Integer = _allEntries.Count
        Dim shown As Integer = _shownEntries.Count
        Dim visualText As String = Radios.Lexicon.Get("logging.browser.filter_status", ("total", total), ("shown", shown))
        FilterStatusLabel.Text = visualText
        FilterStatusLabel.AccessibleName = visualText

        If announce Then
            Try
                ScreenReaderOutput.Speak(Radios.Lexicon.Get("logging.browser.filter_status_spoken", ("shown", shown), ("total", total)), VerbosityLevel.Terse)
            Catch
            End Try
        End If
    End Sub

    Private Sub UpdateFooterLabel()
        Dim total As Long = 0
        Dim count As Integer = _allEntries.Count
        For Each en In _allEntries
            If en.TraceSizeCompressedBytes.HasValue Then total += en.TraceSizeCompressedBytes.Value
        Next
        FooterLabel.Text = Radios.Lexicon.Get(
            If(count = 1, "logging.browser.footer_one", "logging.browser.footer_many"),
            ("size", FormatSize(total)), ("count", count))
        FooterLabel.AccessibleName = FooterLabel.Text
    End Sub

    ' --------------------------------------------------------------------
    ' Filter event wiring (with debounce on the search box).
    ' --------------------------------------------------------------------

    Private Sub FilterFromDate_ValueChanged(sender As Object, e As EventArgs) Handles FilterFromDate.ValueChanged
        If _suspendFilter Then Return
        ApplyFilterAndRefreshList(announce:=True)
    End Sub

    Private Sub FilterToDate_ValueChanged(sender As Object, e As EventArgs) Handles FilterToDate.ValueChanged
        If _suspendFilter Then Return
        ApplyFilterAndRefreshList(announce:=True)
    End Sub

    Private Sub FilterOutcomeCombo_SelectedIndexChanged(sender As Object, e As EventArgs) Handles FilterOutcomeCombo.SelectedIndexChanged
        If _suspendFilter Then Return
        ApplyFilterAndRefreshList(announce:=True)
    End Sub

    Private Sub FilterSearchBox_TextChanged(sender As Object, e As EventArgs) Handles FilterSearchBox.TextChanged
        If _suspendFilter Then Return
        ' Debounce: stop and restart the timer so the filter applies after the
        ' user pauses typing (300 ms). Avoids hammering the LINQ pipeline on
        ' every keystroke and avoids speech spam.
        SearchDebounceTimer.Stop()
        SearchDebounceTimer.Start()
    End Sub

    Private Sub SearchDebounceTimer_Tick(sender As Object, e As EventArgs) Handles SearchDebounceTimer.Tick
        SearchDebounceTimer.Stop()
        ApplyFilterAndRefreshList(announce:=True)
    End Sub

    ' --------------------------------------------------------------------
    ' ListView column-click sorting + selection change → detail panel.
    ' --------------------------------------------------------------------

    Private Sub ArchiveListView_ColumnClick(sender As Object, e As ColumnClickEventArgs) Handles ArchiveListView.ColumnClick
        If e.Column = _sortColumn Then
            _sortDescending = Not _sortDescending
        Else
            _sortColumn = e.Column
            ' Sensible defaults: dates and sizes default desc, others asc.
            _sortDescending = (e.Column = 0 OrElse e.Column = 4)
        End If
        ApplyFilterAndRefreshList(announce:=False)
        Try
            Dim direction As String = Radios.Lexicon.Get(
                If(_sortDescending, "logging.browser.sort_descending", "logging.browser.sort_ascending"))
            ScreenReaderOutput.Speak(Radios.Lexicon.Get("logging.browser.sorted_by",
                                                        ("column", ArchiveListView.Columns(_sortColumn).Text), ("direction", direction)),
                                     VerbosityLevel.Terse)
        Catch
        End Try
    End Sub

    Private Sub ArchiveListView_SelectedIndexChanged(sender As Object, e As EventArgs) Handles ArchiveListView.SelectedIndexChanged
        UpdateSelectionDetail()
    End Sub

    Private Sub UpdateSelectionDetail()
        Dim selected As List(Of TraceSessionEntry) = SelectedEntries()
        If selected.Count = 0 Then
            SelectionDetailBox.Text = Radios.Lexicon.Get("logging.browser.no_selection")
            Return
        End If
        If selected.Count > 1 Then
            Dim totalSize As Long = 0
            For Each en In selected
                If en.TraceSizeCompressedBytes.HasValue Then totalSize += en.TraceSizeCompressedBytes.Value
            Next
            SelectionDetailBox.Text = Radios.Lexicon.Get("logging.browser.multi_selected",
                                                         ("count", selected.Count), ("size", FormatSize(totalSize)))
            Try
                ScreenReaderOutput.Speak(Radios.Lexicon.Get("logging.browser.multi_selected_spoken", ("count", selected.Count)), VerbosityLevel.Terse)
            Catch
            End Try
            Return
        End If

        Dim entry As TraceSessionEntry = selected(0)
        Dim sb As New System.Text.StringBuilder()
        sb.AppendLine(Radios.Lexicon.Get("logging.browser.detail_outcome", ("outcome", TraceOutcomeLabels.Display(entry.Outcome))))
        If Not String.IsNullOrEmpty(entry.OutcomeDetail) Then
            sb.AppendLine(Radios.Lexicon.Get("logging.browser.detail_reason", ("reason", entry.OutcomeDetail)))
        End If
        If entry.ConnectionTarget IsNot Nothing Then
            sb.AppendLine(Radios.Lexicon.Get("logging.browser.detail_target", ("target", FormatTarget(entry.ConnectionTarget))))
        End If
        If entry.KeyEvents IsNot Nothing AndAlso entry.KeyEvents.Count > 0 Then
            sb.AppendLine(Radios.Lexicon.Get("logging.browser.detail_key_events",
                                             ("count", entry.KeyEvents.Count), ("events", String.Join(", ", entry.KeyEvents))))
        End If
        If Not String.IsNullOrEmpty(entry.AppVersion) Then
            sb.AppendLine(Radios.Lexicon.Get("logging.browser.detail_app_version", ("version", entry.AppVersion)))
        End If
        If Not String.IsNullOrEmpty(entry.VerbosityLevel) Then
            sb.AppendLine(Radios.Lexicon.Get("logging.browser.detail_verbosity", ("verbosity", entry.VerbosityLevel)))
        End If
        sb.Append(Radios.Lexicon.Get("logging.browser.detail_file", ("path", ResolveFullPath(entry))))
        SelectionDetailBox.Text = sb.ToString()

        AnnounceSelection(entry)
    End Sub

    Private Sub AnnounceSelection(entry As TraceSessionEntry)
        Try
            Dim verbosity As VerbosityLevel = ScreenReaderOutput.CurrentVerbosity
            Dim outcome As String = TraceOutcomeLabels.Display(entry.Outcome)
            Dim duration As String = FormatDuration(entry.DurationMs)
            Dim target As String = FormatTarget(entry.ConnectionTarget)
            Dim msg As String

            Select Case verbosity
                Case VerbosityLevel.Critical
                    ' Critical-only verbosity = don't push selection chatter.
                    Return
                Case VerbosityLevel.Terse
                    msg = Radios.Lexicon.Get("logging.browser.announce_terse",
                                             ("outcome", outcome), ("target", target), ("duration", duration))
                Case Else ' Chatty
                    Dim eventsCount As Integer = If(entry.KeyEvents Is Nothing, 0, entry.KeyEvents.Count)
                    msg = Radios.Lexicon.Get(
                        If(eventsCount = 1, "logging.browser.announce_chatty_one", "logging.browser.announce_chatty_many"),
                        ("outcome", outcome), ("target", target), ("duration", duration), ("eventsCount", eventsCount))
            End Select
            ScreenReaderOutput.Speak(msg, VerbosityLevel.Terse)
        Catch
        End Try
    End Sub

    Private Function SelectedEntries() As List(Of TraceSessionEntry)
        Dim list As New List(Of TraceSessionEntry)()
        For Each item As ListViewItem In ArchiveListView.SelectedItems
            Dim entry As TraceSessionEntry = TryCast(item.Tag, TraceSessionEntry)
            If entry IsNot Nothing Then list.Add(entry)
        Next
        Return list
    End Function

    Private Function ResolveFullPath(entry As TraceSessionEntry) As String
        If entry Is Nothing OrElse String.IsNullOrEmpty(entry.Filename) Then Return String.Empty
        Return Path.Combine(TraceArchiveDir, entry.Filename.Replace("/"c, Path.DirectorySeparatorChar))
    End Function

    ' --------------------------------------------------------------------
    ' Action buttons.
    ' --------------------------------------------------------------------

    Private Sub ViewTraceButton_Click(sender As Object, e As EventArgs) Handles ViewTraceButton.Click
        ViewSelectedTrace()
    End Sub

    Private Sub ViewSelectedTrace()
        Dim selected As List(Of TraceSessionEntry) = SelectedEntries()
        If selected.Count = 0 Then
            Try : ScreenReaderOutput.Speak(Radios.Lexicon.Get("logging.browser.no_trace_selected"), VerbosityLevel.Critical) : Catch : End Try
            Return
        End If
        If selected.Count > 1 Then
            Try : ScreenReaderOutput.Speak(Radios.Lexicon.Get("logging.browser.select_single"), VerbosityLevel.Critical) : Catch : End Try
            Return
        End If

        Dim entry As TraceSessionEntry = selected(0)
        Dim archivePath As String = ResolveFullPath(entry)
        If String.IsNullOrEmpty(archivePath) OrElse Not File.Exists(archivePath) Then
            Try : ScreenReaderOutput.Speak(Radios.Lexicon.Get("logging.browser.file_missing"), VerbosityLevel.Critical) : Catch : End Try
            Return
        End If

        Try
            Dim tempDir As String = Path.Combine(Path.GetTempPath(), "JJFlexRadioTraceView", entry.SessionId)
            Dim extracted As String = SessionArchive.ExtractTraceText(archivePath, tempDir)
            If String.IsNullOrEmpty(extracted) OrElse Not File.Exists(extracted) Then
                ScreenReaderOutput.Speak(Radios.Lexicon.Get("logging.browser.extract_failed"), VerbosityLevel.Critical)
                Return
            End If
            Dim psi As New ProcessStartInfo(extracted) With {.UseShellExecute = True}
            Process.Start(psi)
            ScreenReaderOutput.Speak(Radios.Lexicon.Get("logging.browser.opened"), VerbosityLevel.Critical)
        Catch ex As Exception
            Tracing.ErrMessageTrace(ex)
            Try : ScreenReaderOutput.Speak(Radios.Lexicon.Get("logging.browser.open_failed"), VerbosityLevel.Critical) : Catch : End Try
        End Try
    End Sub

    Private Sub CopyPathButton_Click(sender As Object, e As EventArgs) Handles CopyPathButton.Click
        CopySelectedPath()
    End Sub

    Private Sub CopySelectedPath()
        Dim selected As List(Of TraceSessionEntry) = SelectedEntries()
        If selected.Count = 0 Then
            Try : ScreenReaderOutput.Speak(Radios.Lexicon.Get("logging.browser.no_trace_selected"), VerbosityLevel.Critical) : Catch : End Try
            Return
        End If
        Try
            Dim text As String
            If selected.Count = 1 Then
                text = ResolveFullPath(selected(0))
            Else
                text = String.Join(Environment.NewLine, selected.Select(Function(en) ResolveFullPath(en)))
            End If
            Clipboard.SetText(text)
            Dim msg As String = If(selected.Count = 1,
                Radios.Lexicon.Get("logging.browser.path_copied_one"),
                Radios.Lexicon.Get("logging.browser.path_copied_many", ("count", selected.Count)))
            ScreenReaderOutput.Speak(msg, VerbosityLevel.Critical)
        Catch ex As Exception
            Tracing.ErrMessageTrace(ex)
            Try : ScreenReaderOutput.Speak(Radios.Lexicon.Get("logging.browser.copy_failed"), VerbosityLevel.Critical) : Catch : End Try
        End Try
    End Sub

    Private Sub ExportSelectedButton_Click(sender As Object, e As EventArgs) Handles ExportSelectedButton.Click
        ExportSelectedTraces()
    End Sub

    Private Sub ExportSelectedTraces()
        Dim selected As List(Of TraceSessionEntry) = SelectedEntries()
        If selected.Count = 0 Then
            Try : ScreenReaderOutput.Speak(Radios.Lexicon.Get("logging.browser.none_to_export"), VerbosityLevel.Critical) : Catch : End Try
            Return
        End If

        Using dlg As New SaveFileDialog()
            dlg.Filter = "Zip archive (*.zip)|*.zip"
            dlg.DefaultExt = "zip"
            dlg.FileName = $"traces-export-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
            dlg.Title = Radios.Lexicon.Get("logging.browser.export_title")
            If dlg.ShowDialog(Me) <> DialogResult.OK Then Return

            Try
                If File.Exists(dlg.FileName) Then File.Delete(dlg.FileName)
                Using outZip As ZipArchive = ZipFile.Open(dlg.FileName, ZipArchiveMode.Create)
                    For Each en In selected
                        Dim full As String = ResolveFullPath(en)
                        If String.IsNullOrEmpty(full) OrElse Not File.Exists(full) Then Continue For
                        Dim entryName As String = Path.GetFileName(full)
                        outZip.CreateEntryFromFile(full, entryName)
                    Next
                End Using
                ScreenReaderOutput.Speak(Radios.Lexicon.Get(
                    If(selected.Count = 1, "logging.browser.exported_one", "logging.browser.exported_many"),
                    ("count", selected.Count)), VerbosityLevel.Critical)
            Catch ex As Exception
                Tracing.ErrMessageTrace(ex)
                Try : ScreenReaderOutput.Speak(Radios.Lexicon.Get("logging.browser.export_failed"), VerbosityLevel.Critical) : Catch : End Try
            End Try
        End Using
    End Sub

    Private Sub DeleteSelectedButton_Click(sender As Object, e As EventArgs) Handles DeleteSelectedButton.Click
        DeleteSelectedTraces()
    End Sub

    Private Sub DeleteSelectedTraces()
        Dim selected As List(Of TraceSessionEntry) = SelectedEntries()
        If selected.Count = 0 Then
            Try : ScreenReaderOutput.Speak(Radios.Lexicon.Get("logging.browser.none_to_delete"), VerbosityLevel.Critical) : Catch : End Try
            Return
        End If

        Dim prompt As String = If(selected.Count = 1,
            Radios.Lexicon.Get("logging.browser.delete_prompt_one"),
            Radios.Lexicon.Get("logging.browser.delete_prompt_many", ("count", selected.Count)))
        Dim result As DialogResult = MessageBox.Show(Me, prompt, Radios.Lexicon.Get("logging.browser.delete_title"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2)
        If result <> DialogResult.OK Then
            Try : ScreenReaderOutput.Speak(Radios.Lexicon.Get("logging.browser.delete_cancelled"), VerbosityLevel.Terse) : Catch : End Try
            Return
        End If

        Dim filenames As List(Of String) = selected _
            .Where(Function(en) Not String.IsNullOrEmpty(en.Filename)) _
            .Select(Function(en) en.Filename) _
            .ToList()
        Dim deleted As Integer = SessionArchive.DeleteEntries(TraceArchiveDir, filenames)

        ReloadManifestCache()
        ApplyFilterAndRefreshList(announce:=False)
        Try
            ScreenReaderOutput.Speak(Radios.Lexicon.Get(
                If(deleted = 1, "logging.browser.deleted_one", "logging.browser.deleted_many"),
                ("count", deleted)), VerbosityLevel.Critical)
        Catch
        End Try
    End Sub

    ' --------------------------------------------------------------------
    ' Footer "Prune Now" button.
    ' --------------------------------------------------------------------

    Private Sub PruneNowButton_Click(sender As Object, e As EventArgs) Handles PruneNowButton.Click
        Dim retentionDays As Integer = CInt(PruneRetentionUpDown.Value)
        Dim prompt As String = $"Remove all archived traces older than {retentionDays} days?"
        Dim result As DialogResult = MessageBox.Show(Me, prompt, "Prune Trace Archive", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2)
        If result <> DialogResult.OK Then
            Try : ScreenReaderOutput.Speak("Prune cancelled", VerbosityLevel.Terse) : Catch : End Try
            Return
        End If
        ' PerformTraceArchivePrune speaks the result on its own (Critical level).
        PerformTraceArchivePrune(retentionDays)
        ReloadManifestCache()
        ApplyFilterAndRefreshList(announce:=False)
    End Sub

    ' --------------------------------------------------------------------
    ' Keyboard shortcuts on the ListView.
    ' --------------------------------------------------------------------

    Private Sub ArchiveListView_KeyDown(sender As Object, e As KeyEventArgs) Handles ArchiveListView.KeyDown
        ' Enter → View, Ctrl+C → Copy Path, Delete → Delete, Ctrl+A → select all.
        If e.KeyCode = Keys.Enter Then
            ViewSelectedTrace()
            e.Handled = True
            e.SuppressKeyPress = True
        ElseIf e.Control AndAlso e.KeyCode = Keys.C Then
            CopySelectedPath()
            e.Handled = True
            e.SuppressKeyPress = True
        ElseIf e.KeyCode = Keys.Delete Then
            DeleteSelectedTraces()
            e.Handled = True
            e.SuppressKeyPress = True
        ElseIf e.Control AndAlso e.KeyCode = Keys.A Then
            For Each item As ListViewItem In ArchiveListView.Items
                item.Selected = True
            Next
            e.Handled = True
            e.SuppressKeyPress = True
        End If
    End Sub
End Class
