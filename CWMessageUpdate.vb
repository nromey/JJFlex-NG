Imports JJTrace

''' <summary>
''' SUPERSEDED by JJFlexWpf.Dialogs.CWMessageUpdateDialog (#329), and dead long
''' before that.
''' </summary>
''' <remarks>
''' This form lost its last caller during the WPF migration and nothing has
''' constructed it since, which is what made CW message management unreachable:
''' it was the only door to CWMessages.Add, .Update and .Remove, and the Tools
''' menu item that should have opened it said "not yet implemented" instead. The
''' list dialog that replaced it is now wired through CWMessages.Manage.
'''
''' Kept for the same reason as CWMessageAdd - see that file's header for why
''' the record matters more than the deletion.
''' </remarks>
Public Class CWMessageUpdate
    Private Const noMessages As String = "There are no CW Messages"
    Private wasActive As Boolean

    Private Sub setAcceptAction()
        ' Set the default action for the Enter key.
        If KeysList.SelectedIndex = -1 Then
            ' nothing selected
            Me.AcceptButton = AddButton
        Else
            Me.AcceptButton = UpdateButton
        End If
    End Sub

    Private Sub setupList()
        Dim oldID As Integer = KeysList.SelectedIndex
        KeysList.Items.Clear()
        For i As Integer = 0 To CWText.Length - 1
            KeysList.Items.Add(CWText(i).label)
        Next
        ' Restore the index if possible.
        If oldID < KeysList.Items.Count Then
            KeysList.SelectedIndex = oldID
            setAcceptAction()
        Else
            KeysList.SelectedIndex = 0
        End If
    End Sub

    Private Sub CWMessageUpdate_Load(sender As System.Object, e As System.EventArgs) Handles MyBase.Load
        If CWText.Length = 0 Then
            CWText.Add()
        End If
        DialogResult = System.Windows.Forms.DialogResult.None
        wasActive = False
        setupList()
    End Sub

    Private Sub CWMessageUpdate_Activated(sender As System.Object, e As System.EventArgs) Handles MyBase.Activated
        If Not wasActive Then
            wasActive = True
            KeysList.Focus()
        End If
    End Sub

    Private Sub UpdateButton_Click(sender As System.Object, e As System.EventArgs) Handles UpdateButton.Click
        Dim id As Integer = KeysList.SelectedIndex
        If id < 0 Then
            Return
        End If
        ' CWMessageAdd handles an add or update.
        CWText.Update(id)
        setupList()
        DialogResult = System.Windows.Forms.DialogResult.None
        KeysList.Focus()
    End Sub

    Private Sub DeleteButton_Click(sender As System.Object, e As System.EventArgs) Handles DeleteButton.Click
        Dim id As Integer = KeysList.SelectedIndex
        If id < 0 Then
            Return
        End If
        KeysList.SelectedIndex -= 1
        CWText.Remove(id)
        setupList()
        If CWText.Length > 0 Then
            DialogResult = System.Windows.Forms.DialogResult.None
            KeysList.Focus()
        Else
            DialogResult = System.Windows.Forms.DialogResult.Cancel
        End If
    End Sub

    Private Sub CnclButton_Click(sender As System.Object, e As System.EventArgs) Handles CnclButton.Click
        DialogResult = System.Windows.Forms.DialogResult.Cancel
    End Sub

    Private Sub AddButton_Click(sender As System.Object, e As System.EventArgs) Handles AddButton.Click
        CWText.Add()
        setupList()
        KeysList.Focus()
    End Sub

    Private Sub KeysList_SelectedIndexChanged(sender As System.Object, e As System.EventArgs) Handles KeysList.SelectedIndexChanged
        setAcceptAction()
    End Sub
End Class