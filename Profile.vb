Imports System.Diagnostics
Imports JJTrace
Imports Radios

Public Class Profile
    Private ReadOnly Property theRadio As FlexBase
        Get
            Return RigControl
        End Get
    End Property

    Private Class profileDisplay
        Private val As Profile_t
        Private parent As Profile
        Public Sub New(v As Profile_t, p As Profile)
            val = v
            parent = p
        End Sub
        Public ReadOnly Property Display As String
            Get
                Dim str As String = val.Name
                If parent.theRadio.GetProfileByName(val.Name, val.ProfileType, CurrentOp.Profiles) IsNot Nothing Then
                    str &= "+"
                Else
                    str &= "-"
                End If
                If parent.onRig(val) Then
                    str &= "+ "
                Else
                    str &= "- "
                End If
                str &= val.ProfileType.ToString & " "
                If val.Default Then
                    str &= "(default) "
                End If
                If Profile_t.Current(parent.theRadio, val) Then
                    str &= "(current)"
                End If
                Return str
            End Get
        End Property
        Public ReadOnly Property Value As Profile_t
            Get
                Return val
            End Get
        End Property
    End Class
    Private displayList As List(Of profileDisplay)

    Private worker As ProfileWorker

    ' sort by type, then name
    Private Function sortByName(p1 As profileDisplay, p2 As profileDisplay)
        Dim n As Integer = CType(p1.Value.ProfileType, Integer) - CType(p2.Value.ProfileType, Integer)
        If n = 0 Then
            n = String.Compare(p1.Value.Name, p2.Value.Name)
        End If
        Return n
    End Function

    Private Sub Profile_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        DialogResult = DialogResult.None
        If RigControl Is Nothing Then
            MessageBox.Show(NoRig, ErrorHdr, MessageBoxButtons.OK)
            DialogResult = DialogResult.Cancel
            Return
        End If

        showList()
    End Sub

    Private Sub showList()
        Dim id = ProfilesListbox.SelectedIndex
        displayList = New List(Of profileDisplay)
        For Each p As Profile_t In CurrentOp.Profiles
            displayList.Add(New profileDisplay(p, Me))
        Next
        ' get profiles on the rig, not in the operator's list.
        For Each p As Profile_t In RigControl.GetRigProfiles(CurrentOp.Profiles)
            displayList.Add(New profileDisplay(p, Me))
        Next
        displayList.Sort(AddressOf sortByName)
        ProfilesListbox.DataSource = Nothing
        ProfilesListbox.DisplayMember = "Display"
        ProfilesListbox.ValueMember = "Value"
        ProfilesListbox.DataSource = displayList
        ' Go to, or near, the last selected item.
        If (id <> -1) And (id < displayList.Count) Then
            ProfilesListbox.SelectedIndex = id
        End If
        ProfilesListbox.Focus()
    End Sub

    Private Function onRig(p As Profile_t)
        Return (RigControl.GetProfileByName(p.Name, p.ProfileType, RigControl.GetRigProfiles(Nothing)) IsNot Nothing)
    End Function

    Private Sub unsetDefault(typ As ProfileTypes)
        For Each p As Profile_t In RigControl.GetProfilesByType(typ, RigControl.GetDefaultProfiles())
            p.Default = False
        Next
    End Sub

    Private Sub AddButton_Click(sender As Object, e As EventArgs) Handles AddButton.Click
        worker = New ProfileWorker(Nothing)
        If worker.ShowDialog = DialogResult.OK Then
            ' Only one default allowed per type.
            If worker.TheProfile.Default Then
                unsetDefault(worker.TheProfile.ProfileType)
            End If
            CurrentOp.Profiles.Add(worker.TheProfile)
        End If
        worker.Dispose()
        Operators.Write(CurrentOp)
        showList()
    End Sub

    Private Sub UpdateButton_Click(sender As Object, e As EventArgs) Handles UpdateButton.Click
        Dim val = ProfilesListbox.SelectedValue
        If val IsNot Nothing Then
            worker = New ProfileWorker(val)
            If worker.ShowDialog = DialogResult.OK Then
                ' replace
                CurrentOp.Profiles.Remove(val)
                ' Only one default allowed per type.
                If worker.TheProfile.Default Then
                    unsetDefault(worker.TheProfile.ProfileType)
                End If
                CurrentOp.Profiles.Add(worker.TheProfile)
            End If
            worker.Dispose()
            Operators.Write(CurrentOp)
            showList()
        Else
            MsgBox(Radios.Lexicon.Get("settings.profile.must_select"))
            ProfilesListbox.Focus()
        End If
    End Sub

    Private Sub DeleteButton_Click(sender As Object, e As EventArgs) Handles DeleteButton.Click
        Dim val As Profile_t = CType(ProfilesListbox.SelectedValue, Profile_t)
        If val IsNot Nothing Then
            If Not Profile_t.Current(RigControl, val) Then
                RigControl.DeleteProfile(val)
                Operators.Write(CurrentOp)
                showList()
            Else
                MessageBox.Show(Radios.Lexicon.Get("settings.profile.cannot_delete_current"), ErrorHdr, MessageBoxButtons.OK)
                ProfilesListbox.Focus()
            End If
        Else
            MessageBox.Show(Radios.Lexicon.Get("settings.profile.must_select"), ErrorHdr, MessageBoxButtons.OK)
            ProfilesListbox.Focus()
        End If
    End Sub

    Private Sub SelectButton_Click(sender As Object, e As EventArgs) Handles SelectButton.Click
        Dim val = CType(ProfilesListbox.SelectedValue, Profile_t)
        If (val IsNot Nothing) Then
            ' Ensure if global, profile is on the rig.
            If (val.ProfileType <> ProfileTypes.global) OrElse onRig(val) Then
                RigControl.SelectProfile(val)
                ' no write here.  Exit dialogue.
                DialogResult = DialogResult.OK
            Else
                MessageBox.Show(Radios.Lexicon.Get("settings.profile.must_select_global_on_radio"), ErrorHdr, MessageBoxButtons.OK)
                ProfilesListbox.Focus()
            End If
        Else
            MessageBox.Show(Radios.Lexicon.Get("settings.profile.must_select"), ErrorHdr, MessageBoxButtons.OK)
            ProfilesListbox.Focus()
        End If
    End Sub

    Private Sub CnclButton_Click(sender As Object, e As EventArgs) Handles CnclButton.Click
        DialogResult = DialogResult.Cancel
    End Sub

    Private Sub Profile_FormClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
        ' nothing to do now
    End Sub

    Private Sub ProfilesListbox_SelectedIndexChanged(sender As Object, e As EventArgs) Handles ProfilesListbox.SelectedIndexChanged
        SaveButton.Enabled = False
        SaveButton.Visible = False
        ' if a global profile is selected.
        If ProfilesListbox.SelectedIndex <> -1 AndAlso
           CType(ProfilesListbox.SelectedValue, Profile_t).ProfileType = ProfileTypes.global Then
            SaveButton.Enabled = True
            SaveButton.Visible = True
        End If
    End Sub

    Private Sub SaveButton_Click(sender As Object, e As EventArgs) Handles SaveButton.Click
        Dim val = ProfilesListbox.SelectedValue
        If val IsNot Nothing Then
            RigControl.SaveProfile(val)
            RigControl.SelectProfile(val)
            ' no write here.
            showList()
        Else
            MsgBox(Radios.Lexicon.Get("settings.profile.must_select"))
            ProfilesListbox.Focus()
        End If
    End Sub
End Class