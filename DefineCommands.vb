Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports JJTrace
Imports Radios

Public Class DefineCommands
    Const dupTitle As String = "Duplicate key Definitions"
    Const dupMessage As String = "Continue with duplicate key definitions?"
    Dim wasActive As Boolean ' true if active when activated
    Dim theKeys As KeyCommands.keyTbl()
    Dim commandChanges, messageChanges As Boolean ' true if changes were made
    Dim currentScope As KeyCommands.KeyScope = KeyCommands.KeyScope.Global

    ''' <summary>
    ''' Get the scope for the currently selected tab.
    ''' </summary>
    Private Function SelectedScope() As KeyCommands.KeyScope
        ' Sprint 8 Phase 8.6: 5-scope system (Global, Radio, Classic, Modern, Logging).
        ' Current tab control has 3 tabs. Sprint 9 will add Classic + Modern tabs
        ' when DefineCommands is converted to WPF. Until then, Classic/Modern scoped
        ' keys (if any) are not visible in this editor.
        Select Case ScopeTabControl.SelectedIndex
            Case 0 : Return KeyCommands.KeyScope.Global
            Case 1 : Return KeyCommands.KeyScope.Radio
            Case 2 : Return KeyCommands.KeyScope.Logging
            Case Else : Return KeyCommands.KeyScope.Global
        End Select
    End Function

    ''' <summary>
    ''' Populate the ListView with commands matching the selected scope tab.
    ''' </summary>
    Private Sub PopulateListView()
        currentScope = SelectedScope()
        CommandsListView.BeginUpdate()
        CommandsListView.Items.Clear()
        For i As Integer = 0 To theKeys.Length - 1
            Dim item = theKeys(i)
            If item.Scope <> currentScope Then Continue For
            Dim lvi = New ListViewItem(KeyString(item.key.key))
            lvi.SubItems.Add(item.helpText)
            lvi.SubItems.Add(item.Group.ToString())
            lvi.Tag = i ' index into theKeys
            CommandsListView.Items.Add(lvi)
        Next
        CommandsListView.EndUpdate()
        ValueBox.Text = ""
        ValueBox.Enabled = False
        ConflictLabel.Text = ""
        Dim count = CommandsListView.Items.Count
        Dim tabName = currentScope.ToString()
        ScreenReaderOutput.Speak(tabName & " hotkeys tab, " & count.ToString() & " commands", True)
    End Sub

    ''' <summary>
    ''' Scope-aware conflict detection.
    ''' Same scope = CONFLICT. Global + anything = CONFLICT.
    ''' Radio + Logging = OK (never simultaneous).
    ''' </summary>
    ''' <param name="k">The key to check</param>
    ''' <param name="scope">The scope of the command being assigned</param>
    ''' <param name="excludeIndex">Index in theKeys to exclude (the entry being edited)</param>
    Private Function CheckConflict(k As Keys, scope As KeyCommands.KeyScope, excludeIndex As Integer) As String
        If k = Keys.None Then Return Nothing
        For i As Integer = 0 To theKeys.Length - 1
            If i = excludeIndex Then Continue For
            Dim other = theKeys(i)
            If other.key.key <> k Then Continue For
            ' Check if scopes conflict.
            Dim conflicts As Boolean = False
            If scope = KeyCommands.KeyScope.Global OrElse other.Scope = KeyCommands.KeyScope.Global Then
                conflicts = True
            ElseIf scope = other.Scope Then
                conflicts = True
            End If
            ' Radio + Logging = no conflict (different scopes, never simultaneous).
            If conflicts Then
                Return KeyString(k) & " conflicts with " & other.helpText & " in " & other.Scope.ToString() & " scope"
            End If
        Next
        Return Nothing
    End Function

    ''' <summary>
    ''' Check for duplicate keys within the same scope on the current tab.
    ''' Returns True if duplicates found.
    ''' </summary>
    Private Function DupCheckCurrentTab() As Boolean
        Dim hasDups As Boolean = False
        Dim dupDict = New Dictionary(Of Keys, Integer)
        For Each lvi As ListViewItem In CommandsListView.Items
            Dim idx = CInt(lvi.Tag)
            Dim k = theKeys(idx).key.key
            If k = Keys.None Then Continue For
            If dupDict.ContainsKey(k) Then
                hasDups = True
            Else
                dupDict.Add(k, idx)
            End If
        Next
        Return hasDups
    End Function

    ''' <summary>
    ''' Check for cross-scope conflicts across all commands.
    ''' </summary>
    Private Function HasAnyConflicts() As Boolean
        For i As Integer = 0 To theKeys.Length - 1
            Dim k = theKeys(i).key.key
            If k = Keys.None Then Continue For
            For j As Integer = i + 1 To theKeys.Length - 1
                If theKeys(j).key.key <> k Then Continue For
                Dim s1 = theKeys(i).Scope
                Dim s2 = theKeys(j).Scope
                If s1 = s2 Then Return True
                If s1 = KeyCommands.KeyScope.Global OrElse s2 = KeyCommands.KeyScope.Global Then Return True
            Next
        Next
        Return False
    End Function

    Private Sub DefineKeys_Load(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles MyBase.Load
        DialogResult = DialogResult.None
        theKeys = Commands.CurrentKeys()
        wasActive = False
        commandChanges = False
        messageChanges = False
        PopulateListView()
    End Sub

    Private Sub ScopeTabControl_SelectedIndexChanged(sender As Object, e As EventArgs) Handles ScopeTabControl.SelectedIndexChanged
        If theKeys IsNot Nothing Then
            PopulateListView()
        End If
    End Sub

    Private Sub CommandsListView_SelectedIndexChanged(sender As Object, e As EventArgs) Handles CommandsListView.SelectedIndexChanged
        If CommandsListView.SelectedItems.Count = 0 Then
            ValueBox.Enabled = False
            ValueBox.Text = ""
            PressKeyLabel.Text = ""
            Return
        End If
        Dim idx = CInt(CommandsListView.SelectedItems(0).Tag)
        ValueBox.Text = KeyString(theKeys(idx).key.key)
        ValueBox.Enabled = True
        PressKeyLabel.Text = "Press desired key to change"
    End Sub

    Private Sub ValueBox_KeyDown(ByVal sender As System.Object, ByVal e As System.Windows.Forms.KeyEventArgs) Handles ValueBox.KeyDown
        If (e.KeyCode = Keys.Tab) Or (e.KeyCode = Keys.Menu) Or
           (e.KeyCode = Keys.ShiftKey) Or (e.KeyCode = Keys.ControlKey) Then
            Return
        End If
        If CommandsListView.SelectedItems.Count = 0 Then Return
        Dim lvi = CommandsListView.SelectedItems(0)
        Dim idx = CInt(lvi.Tag)
        e.SuppressKeyPress = True
        Dim k As Keys = (e.KeyCode Or e.Modifiers)
        If k = theKeys(idx).key.key Then Return

        Tracing.TraceLine("defineKeys ValueBox_KeyDown:from " & theKeys(idx).key.key.ToString & " to " & k.ToString, TraceLevel.Info)
        If (theKeys(idx).KeyType = KeyCommands.KeyTypes.Command) Or
           (theKeys(idx).KeyType = KeyCommands.KeyTypes.log) Then
            commandChanges = True
        Else
            messageChanges = True
        End If
        If k = Keys.Delete Then
            k = Keys.None
        End If

        ' Auto-clear any conflicting binding before assigning.
        ' This prevents duplicate keys from ever existing — the standard approach
        ' used by VS Code, IntelliJ, etc.
        If k <> Keys.None Then
            Dim conflict = CheckConflict(k, theKeys(idx).Scope, idx)
            If conflict IsNot Nothing Then
                ' Find and clear the conflicting entry.
                For ci As Integer = 0 To theKeys.Length - 1
                    If ci = idx Then Continue For
                    If theKeys(ci).key.key <> k Then Continue For
                    ' Check if scopes actually conflict.
                    Dim s1 = theKeys(idx).Scope
                    Dim s2 = theKeys(ci).Scope
                    Dim isConflict = (s1 = s2) OrElse
                                     (s1 = KeyCommands.KeyScope.Global) OrElse
                                     (s2 = KeyCommands.KeyScope.Global)
                    If isConflict Then
                        Dim oldCmd = theKeys(ci).helpText
                        theKeys(ci).key.key = Keys.None
                        ' Update the ListView if the cleared entry is visible.
                        For Each otherLvi As ListViewItem In CommandsListView.Items
                            If CInt(otherLvi.Tag) = ci Then
                                otherLvi.SubItems(0).Text = KeyString(Keys.None)
                                Exit For
                            End If
                        Next
                        ConflictLabel.Text = "Cleared " & KeyString(k) & " from " & oldCmd
                        ScreenReaderOutput.Speak("Cleared " & KeyString(k) & " from " & oldCmd, True)
                        commandChanges = True
                    End If
                Next
            End If
        End If

        theKeys(idx).key.key = k
        Dim str As String = KeyString(k)
        lvi.SubItems(0).Text = str
        ValueBox.Text = str

        ' Announce the assignment (conflict message was already spoken above if applicable).
        If ConflictLabel.Text = "" Then
            ScreenReaderOutput.Speak(str & " assigned to " & theKeys(idx).helpText, True)
        End If

        CommandsListView.Focus()
    End Sub

    Private Sub ValueBox_Enter(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles ValueBox.Enter
        PressKeyLabel.Text = "Press desired key to change"
    End Sub

    Private Sub ValueBox_Leave(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles ValueBox.Leave
        PressKeyLabel.Text = ""
    End Sub

    Private Sub DefineKeys_Activated(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles MyBase.Activated
        If Not wasActive Then
            wasActive = True
            CommandsListView.Focus()
        End If
    End Sub

    Private Sub OKButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles OKButton.Click
        If commandChanges Or messageChanges Then
            If HasAnyConflicts() Then
                ' Block saving — conflicts should never reach this point due to auto-clear,
                ' but if they do, refuse to save and tell the user.
                MessageBox.Show("There are conflicting key assignments. Please resolve them before saving.",
                                dupTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning)
                ScreenReaderOutput.Speak("Cannot save. Conflicting key assignments exist. Please resolve them first.", True)
                DialogResult = DialogResult.None
                Return
            Else
                DialogResult = DialogResult.OK
            End If
            If DialogResult = DialogResult.OK Then
                ' Changes were made, so set values.
                Dim cmdDefs = New Collection(Of KeyCommands.KeyDefType)
                Dim CWDefs = New Collection(Of KeyCommands.KeyDefType)
                For Each item As KeyCommands.keyTbl In theKeys
                    If (item.KeyType = KeyCommands.KeyTypes.Command) Or (item.KeyType = KeyCommands.KeyTypes.log) Then
                        cmdDefs.Add(item.key)
                    Else
                        CWDefs.Add(item.key)
                    End If
                Next
                If commandChanges Then
                    Commands.SetValues(cmdDefs.ToArray, KeyCommands.KeyTypes.allKeys, True)
                    If Not messageChanges Then
                        Commands.UpdateCWText(CWDefs.ToArray)
                    End If
                End If
                If messageChanges Then
                    Commands.UpdateCWText(CWDefs.ToArray)
                    CWText.UpdateOperator()
                End If
            End If
        Else
            DialogResult = DialogResult.OK
        End If
    End Sub

    Private Sub CnclButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles CnclButton.Click
        DialogResult = DialogResult.Cancel
    End Sub

    Private Sub ResetButton_Click(sender As Object, e As EventArgs) Handles ResetButton.Click
        If CommandsListView.SelectedItems.Count = 0 Then Return
        Dim idx = CInt(CommandsListView.SelectedItems(0).Tag)
        Dim cmdId = theKeys(idx).key.id
        ' Find the default key for this command.
        Dim defaultCmd = Commands.GetDefaultKey(cmdId)
        If defaultCmd IsNot Nothing Then
            theKeys(idx).key.key = defaultCmd.key
            CommandsListView.SelectedItems(0).SubItems(0).Text = KeyString(defaultCmd.key)
            ValueBox.Text = KeyString(defaultCmd.key)
            commandChanges = True
            ScreenReaderOutput.Speak("Reset to " & KeyString(defaultCmd.key), True)
        End If
    End Sub

    Private Sub ResetAllButton_Click(sender As Object, e As EventArgs) Handles ResetAllButton.Click
        Dim scope = SelectedScope()
        For Each lvi As ListViewItem In CommandsListView.Items
            Dim idx = CInt(lvi.Tag)
            Dim cmdId = theKeys(idx).key.id
            Dim defaultCmd = Commands.GetDefaultKey(cmdId)
            If defaultCmd IsNot Nothing Then
                theKeys(idx).key.key = defaultCmd.key
                lvi.SubItems(0).Text = KeyString(defaultCmd.key)
            End If
        Next
        commandChanges = True
        ScreenReaderOutput.Speak("All " & scope.ToString() & " keys reset to defaults", True)
    End Sub
End Class
