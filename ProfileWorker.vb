Imports Radios

Public Class ProfileWorker
    Private Const mustSpecifyName As String = "You must specify a name."
    Private Const mustBeUnique As String = "The name must be unique within this type."
    Private Const mustSelectType As String = "You must select a type."

    ''' <summary>
    ''' The profile
    ''' </summary>
    ''' <remarks>
    ''' On an update, this is a copy of the original.
    ''' </remarks>
    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Friend Property TheProfile As Profile_t

    Private updt As Boolean

    Private ReadOnly Property profiles As List(Of Profile_t)
        Get
            Dim rv As List(Of Profile_t)
            If RigControl Is Nothing Then
                rv = Nothing
            Else
                rv = CurrentOp.Profiles
            End If
            Return rv
        End Get
    End Property

    Friend Sub New(Prof As Profile_t)
        ' This call is required by the designer.
        InitializeComponent()

        ' Add any initialization after the InitializeComponent() call.
        If Prof Is Nothing Then
            TheProfile = New Profile_t()
        Else
            TheProfile = New Profile_t(Prof)
            updt = True
        End If
    End Sub

    Private Sub ProfileWorker_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        DialogResult = DialogResult.None
        If profiles Is Nothing Then
            DialogResult = DialogResult.Cancel
            Return
        End If

        ' populate
        TypeBox.Items.AddRange([Enum].GetNames(GetType(ProfileTypes)))
        If updt Then
            NameBox.Text = TheProfile.Name
            TypeBox.Text = TheProfile.ProfileType.ToString
            DefaultBox.Checked = TheProfile.Default
        End If
    End Sub

    Private Sub OKButton_Click(sender As Object, e As EventArgs) Handles OKButton.Click
        NameBox.Text = NameBox.Text.Trim
        If NameBox.Text = "" Then
            MsgBox(mustSpecifyName)
            NameBox.Focus()
            Return
        End If
        If TypeBox.SelectedIndex = -1 Then
            MsgBox(mustSelectType)
            TypeBox.Focus()
            Return
        End If
        ' Name must be unique within type on an add.
        If Not updt Then
            For Each p As Profile_t In RigControl.GetProfilesByType(TypeBox.SelectedIndex)
                If p.Name = NameBox.Text Then
                    MsgBox(mustBeUnique)
                    NameBox.Focus()
                    Return
                End If
            Next
        End If

        ' data looks ok.
        TheProfile.Name = NameBox.Text
        TheProfile.ProfileType = TypeBox.SelectedIndex
        TheProfile.Default = DefaultBox.Checked

        DialogResult = DialogResult.OK
    End Sub

    Private Sub CnclButton_Click(sender As Object, e As EventArgs) Handles CnclButton.Click
        DialogResult = DialogResult.Cancel
    End Sub
End Class