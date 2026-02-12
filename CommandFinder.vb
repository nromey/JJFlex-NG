Imports Radios

''' <summary>
''' Command Finder dialog — searchable list of all hotkey commands.
''' Opened via Ctrl+/ (ContextHelp command) or the Tools menu.
''' Can be pre-filtered to show only commands matching the current scope + Global.
''' </summary>
Public Class CommandFinder
    ''' <summary>
    ''' Set before ShowDialog to pre-filter results.
    ''' UIMode.Logging shows Logging+Global; Classic/Modern shows Radio+Global; Nothing shows all.
    ''' </summary>
    Friend PreFilterScope As UIMode = UIMode.Classic

    Private allCommands As KeyCommands.keyTbl()

    Private Sub CommandFinder_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        allCommands = Commands.CurrentKeys()
        SearchBox.Text = ""
        FilterResults("")
        SearchBox.Focus()
    End Sub

    Private Sub SearchBox_TextChanged(sender As Object, e As EventArgs) Handles SearchBox.TextChanged
        FilterResults(SearchBox.Text.Trim())
    End Sub

    ''' <summary>
    ''' Filter and display commands matching the search query and current scope.
    ''' </summary>
    Private Sub FilterResults(query As String)
        ResultsListView.BeginUpdate()
        ResultsListView.Items.Clear()
        Dim count As Integer = 0
        For Each item In allCommands
            ' Scope filter: show commands matching the pre-filter scope.
            If Not ScopeVisible(item.Scope) Then Continue For
            ' Text filter: match against helpText, Description, Keywords, Group.
            If query.Length > 0 AndAlso Not MatchesQuery(item, query) Then Continue For
            Dim lvi = New ListViewItem(item.helpText)
            lvi.SubItems.Add(KeyString(item.key.key))
            lvi.SubItems.Add(item.Scope.ToString())
            lvi.Tag = item
            ResultsListView.Items.Add(lvi)
            count += 1
        Next
        ResultsListView.EndUpdate()
        ResultCountLabel.Text = count.ToString() & " commands"
        ScreenReaderOutput.Speak(count.ToString() & " results", True)
    End Sub

    ''' <summary>
    ''' Check if a command's scope should be visible given the pre-filter.
    ''' </summary>
    Private Function ScopeVisible(scope As KeyCommands.KeyScope) As Boolean
        If scope = KeyCommands.KeyScope.Global Then Return True
        Select Case PreFilterScope
            Case UIMode.Logging
                Return (scope = KeyCommands.KeyScope.Logging)
            Case UIMode.Classic, UIMode.Modern
                Return (scope = KeyCommands.KeyScope.Radio)
            Case Else
                Return True
        End Select
    End Function

    ''' <summary>
    ''' Check if a command matches the search query (case-insensitive).
    ''' </summary>
    Private Function MatchesQuery(item As KeyCommands.keyTbl, query As String) As Boolean
        Dim q = query.ToLowerInvariant()
        If item.helpText IsNot Nothing AndAlso item.helpText.ToLowerInvariant().Contains(q) Then Return True
        If item.Description IsNot Nothing AndAlso item.Description.ToLowerInvariant().Contains(q) Then Return True
        If item.Group.ToString().ToLowerInvariant().Contains(q) Then Return True
        If item.menuText IsNot Nothing AndAlso item.menuText.ToLowerInvariant().Contains(q) Then Return True
        If item.Keywords IsNot Nothing Then
            For Each kw In item.Keywords
                If kw.ToLowerInvariant().Contains(q) Then Return True
            Next
        End If
        Return False
    End Function

    ''' <summary>
    ''' Enter on a result executes the command and closes the dialog.
    ''' </summary>
    Private Sub ResultsListView_KeyDown(sender As Object, e As KeyEventArgs) Handles ResultsListView.KeyDown
        If e.KeyCode = Keys.Enter Then
            ExecuteSelectedCommand()
            e.Handled = True
        End If
    End Sub

    ''' <summary>
    ''' Double-click on a result executes the command and closes the dialog.
    ''' </summary>
    Private Sub ResultsListView_DoubleClick(sender As Object, e As EventArgs) Handles ResultsListView.DoubleClick
        ExecuteSelectedCommand()
    End Sub

    ''' <summary>
    ''' Close the dialog and execute the currently selected command.
    ''' Shared by Enter key and double-click handlers.
    ''' </summary>
    Private Sub ExecuteSelectedCommand()
        If ResultsListView.SelectedItems.Count = 0 Then Return
        Dim item = TryCast(ResultsListView.SelectedItems(0).Tag, KeyCommands.keyTbl)
        If item Is Nothing Then Return
        Me.Close()
        If item.rtn IsNot Nothing Then
            Commands.CommandId = item.key.id
            Try
                item.rtn()
            Catch ex As Exception
                JJTrace.Tracing.TraceLine("CommandFinder:execute:" & ex.Message, TraceLevel.Error)
            End Try
        End If
    End Sub

    Private Sub CommandFinder_KeyDown(sender As Object, e As KeyEventArgs) Handles MyBase.KeyDown
        If e.KeyCode = Keys.Escape Then
            Me.Close()
            e.Handled = True
        End If
    End Sub
End Class
