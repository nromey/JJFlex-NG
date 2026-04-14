Imports JJRadio.PersonalData

' The rig selection stuff has been removed.
Friend Class Lister
    Const PText As String = "Operator Selection"
    Const RText As String = "Rig Selection"
    Const mustSelectMsg As String = "You must select an item."
    Const removeSelectedTitle As String = "Removing Selected item"
    Const removeSelectedMsg As String = "You may not remove the currently selected item."
    Const removeDefaultTitleMsg As String = "Removing default"
    Const removeDefaultMsg As String = "If you remove the default, the resulting first item will be used.  Continue?"

    Private wasActive As Boolean
    Private ignoreCheck As Boolean ' do nothing when item checked (e.g.) at setup.
    Private Enum itemTypes
        operater
        rig
    End Enum
    Private itemType As itemTypes
    ''' <summary>
    ''' the applicable current id
    ''' </summary>
    Private Property currentID As Integer
        Get
            'If itemType = itemTypes.operater Then
            Return CurrentOpID
            'End If
        End Get
        Set(value As Integer)
            If itemType = itemTypes.operater Then
                CurrentOpID = value
            End If
        End Set
    End Property

    Private listObj As Object ' the data to operate on.
    Private screenItems As ArrayList ' DataSource for ScreenList
    ' TheList is the data supplied by the user.
    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property TheList As Object
        Get
            Return listObj
        End Get
        Set(ByVal value As Object)
            ' Set listObj and setup ScreenList.
            ignoreCheck = True
            ScreenList.SuspendLayout()
            ScreenList.ClearSelected()
            ScreenList.DataSource = Nothing
            listObj = value
            screenItems = New ArrayList
            Dim id As Integer
            If value.GetType.Name = "PersonalData" Then
                itemType = itemTypes.operater
                For Each p As PersonalData.personal_v1 In listObj.Ops
                    screenItems.Add(p)
                Next
                Me.Text = PText
                ScreenList.AccessibleName = PText
            End If
            ScreenList.DataSource = screenItems
            ScreenList.DisplayMember = "Display"
            id = currentID ' current op or rig ID.
            If id <> -1 Then
                ScreenList.SetItemChecked(id, True)
            End If
            ScreenList.ResumeLayout()
            ignoreCheck = False
        End Set
    End Property

    Private Sub Lister_Load(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles MyBase.Load
        DialogResult = DialogResult.None
        wasActive = False
    End Sub

    Private Sub AddButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles AddButton.Click
        Dim id As Integer = listObj.Add()
        refreshScreenList(id)
        ScreenList.Focus()
    End Sub

    Private Sub UpdateButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles UpdateButton.Click
        Dim id As Integer = ScreenList.SelectedIndex
        If id <> -1 Then
            listObj.Update(id)
            refreshScreenList(id)
        Else
            MsgBox(mustSelectMsg)
        End If
        ScreenList.Focus()
    End Sub

    Private Sub DeleteButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles DeleteButton.Click
        Dim id As Integer = ScreenList.SelectedIndex
        If id = currentID Then
            MessageBox.Show(removeSelectedMsg, removeSelectedTitle, MessageBoxButtons.OK)
            ScreenList.Focus()
            Return
        End If
        If id <> -1 Then
            Dim removeIt As Boolean = True
            If id = listObj.DefaultID Then
                If MessageBox.Show(removeDefaultMsg, removeDefaultTitleMsg, MessageBoxButtons.YesNo) <> DialogResult.Yes Then
                    removeIt = False
                End If
            End If
            If removeIt Then
                listObj.RemoveAT(id)
                ' May need to adjust the current id.
                If id <= currentID Then
                    currentID -= 1
                End If
                refreshScreenList(id)
            End If
        Else
            MsgBox(mustSelectMsg)
        End If
        ScreenList.Focus()
    End Sub

    Private Sub refreshScreenList(id As Integer)
        TheList = listObj
        If id >= ScreenList.Items.Count Then
            id = ScreenList.Items.Count - 1
        End If
        ScreenList.SelectedIndex = id
    End Sub

    Private Sub CnclButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles CnclButton.Click
        DialogResult = DialogResult.Cancel
    End Sub

    Private Sub Lister_Activated(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles MyBase.Activated
        If Not wasActive Then
            wasActive = True
            ScreenList.Focus()
        End If
    End Sub

    Private Sub ScreenList_ItemCheck(ByVal sender As System.Object, ByVal e As System.Windows.Forms.ItemCheckEventArgs) Handles ScreenList.ItemCheck
        If ignoreCheck Then
            Return
        End If
        If ScreenList.GetItemChecked(e.Index) = False Then
            ' Item is being checked.
            ' Uncheck any other items.
            Dim wasChecked As Boolean = False
            ignoreCheck = True
            For i As Integer = 0 To ScreenList.Items.Count - 1
                If ScreenList.GetItemChecked(i) = True Then
                    ScreenList.SetItemChecked(i, False)
                    wasChecked = True
                End If
            Next
            ignoreCheck = False
            If listObj.GetType.Name = "PersonalData" Then
                SetCurrentOp(Operators(e.Index), e.Index)
                currentID = CurrentOpID
                If wasChecked Then
                    Operators.reportEvent()
                End If
            End If
        Else
            e.NewValue = CheckState.Checked ' item was checked, leave it checked.
        End If
    End Sub
End Class