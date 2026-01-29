Imports JJTrace

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