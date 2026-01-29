Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.Diagnostics
Imports JJTrace
Imports Radios

Public Class ManageGroups
    Private Const mustHaveGroup As String = "You must select a group."
    Private GroupsControl As MemoryGroup

    Private Sub ManageGroups_Load(sender As System.Object, e As System.EventArgs) Handles MyBase.Load
        Tracing.TraceLine("ManageGroups_Load", TraceLevel.Info)
        wasActive = False
        GroupsControl = New MemoryGroup
        GroupsControl.Location = New Point(0, 20)
        GroupsControl.TabIndex = 0
        Controls.Add(GroupsControl)

        ' Setup for user groups.
        GroupsControl.ManageUserGroups = True
        If Not GroupsControl.Setup Then
            DialogResult = System.Windows.Forms.DialogResult.Abort
            Return
        End If

        DialogResult = System.Windows.Forms.DialogResult.None
    End Sub

    Private Sub ManageGroups_FormClosing(sender As System.Object, e As System.Windows.Forms.FormClosingEventArgs) Handles MyBase.FormClosing
        GroupsControl.Dispose()
    End Sub

    Private Sub AddButton_Click(sender As System.Object, e As System.EventArgs) Handles AddButton.Click
        Tracing.TraceLine("AddButton_Click", TraceLevel.Info)
        Dim theForm = New ManageGroupsEdit
        theForm.Group = Nothing
        theForm.groupsControl = GroupsControl
        ' Add and save the group.
        If theForm.ShowDialog = System.Windows.Forms.DialogResult.OK Then
            GroupsControl.AddUserGroup(theForm.Group)
        End If
        theForm.Dispose()
        GroupsControl.Setup()
        DialogResult = System.Windows.Forms.DialogResult.None
        GroupsControl.GroupsBox.Focus()
    End Sub

    Private Sub UpdateButton_Click(sender As System.Object, e As System.EventArgs) Handles UpdateButton.Click
        Tracing.TraceLine("UpdateButton_Click", TraceLevel.Info)
        DialogResult = System.Windows.Forms.DialogResult.None
        If GroupsControl.SelectedGroup Is Nothing Then
            MsgBox(mustHaveGroup)
            GroupsControl.Focus()
            Return
        End If
        Dim theForm = New ManageGroupsEdit
        theForm.Group = GroupsControl.SelectedGroup
        theForm.groupsControl = GroupsControl
        ' Add and save the group.
        If theForm.ShowDialog = System.Windows.Forms.DialogResult.OK Then
            GroupsControl.UpdateUserGroup(GroupsControl.SelectedGroup, theForm.Group)
        End If
        theForm.Dispose()
        GroupsControl.Setup()
        DialogResult = System.Windows.Forms.DialogResult.None
        GroupsControl.GroupsBox.Focus()
    End Sub

    Private Sub DeleteButton_Click(sender As System.Object, e As System.EventArgs) Handles DeleteButton.Click
        Tracing.TraceLine("DeleteButton_Click", TraceLevel.Info)
        DialogResult = System.Windows.Forms.DialogResult.None
        If GroupsControl.SelectedGroup Is Nothing Then
            MsgBox(mustHaveGroup)
            GroupsControl.Focus()
            Return
        End If
        GroupsControl.DeleteUserGroup(GroupsControl.SelectedGroup)
        GroupsControl.Setup()
        DialogResult = System.Windows.Forms.DialogResult.None
        GroupsControl.GroupsBox.Focus()
    End Sub

    Private Sub DoneButton_Click(sender As System.Object, e As System.EventArgs) Handles DoneButton.Click
        DialogResult = System.Windows.Forms.DialogResult.OK
    End Sub

    Private wasActive As Boolean
    Private Sub ManageGroups_Activated(sender As System.Object, e As System.EventArgs) Handles MyBase.Activated
        If Not wasActive Then
            wasActive = True
            GroupsControl.GroupsListBox.Focus()
        End If
    End Sub
End Class