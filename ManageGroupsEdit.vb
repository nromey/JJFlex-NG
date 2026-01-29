Imports System.Collections
Imports System.Collections.Generic
Imports System.Diagnostics
Imports JJTrace
Imports Radios

Public Class ManageGroupsEdit
    Private Const mustHaveName As String = "The group must have a unique name."
    Private Const mustHaveMembers As String = "The group must have at least one member."
    Private sortedMemories As List(Of String)

    Friend oldGroup As FlexBase.ScanGroup
    Friend Group As FlexBase.ScanGroup
    Private ReadOnly Property updateFlag As Boolean
        Get
            Return Group IsNot Nothing
        End Get
    End Property
    Friend groupsControl As MemoryGroup

    Private Sub ManageGroupsEdit_Load(sender As System.Object, e As System.EventArgs) Handles MyBase.Load
        SuspendLayout()
        ' Get Sorted memories by display name.
        sortedMemories = RigControl.MemoryNames()

        Dim groupID As Integer = 0
        For i As Integer = 0 To sortedMemories.Count - 1
            ' Add to the members list
            MembersBox.Items.Add(sortedMemories(i))
            ' Check items from the group on an update.
            If updateFlag Then
                If (groupID < Group.Members.Count) AndAlso
                   (Group.Members(groupID) = sortedMemories(i)) Then
                    MembersBox.SetItemChecked(i, True)
                    ' Select the first checked item.
                    If groupID = 0 Then
                        MembersBox.SelectedIndex = i
                    End If
                    groupID += 1
                End If
            End If
        Next
        If updateFlag Then
            NameBox.Text = Group.Name
            oldGroup = Group
        End If
        ResumeLayout()

        DialogResult = System.Windows.Forms.DialogResult.None
    End Sub

    Private Sub OkButton_Click(sender As System.Object, e As System.EventArgs) Handles OkButton.Click
        If (NameBox.Text = "") OrElse
            ((Not updateFlag) AndAlso groupsControl.groupFile.Keys.Contains(NameBox.Text)) Then
            MsgBox(mustHaveName)
            NameBox.Focus()
            Return
        End If
        If MembersBox.CheckedItems.Count = 0 Then
            MsgBox(mustHaveMembers)
            MembersBox.Focus()
            Return
        End If

        Dim items = New List(Of String)
        For Each id As Integer In MembersBox.CheckedIndices
            items.Add(sortedMemories(id))
        Next
        Group = New FlexBase.ScanGroup(NameBox.Text, items)
        DialogResult = System.Windows.Forms.DialogResult.OK
    End Sub
End Class