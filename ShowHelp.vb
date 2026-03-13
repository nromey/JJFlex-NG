Imports Radios

Public Enum ShowHelpTypes
    standard
    alphabetic
    byGroup
End Enum

Friend Class ShowHelp
    Private helpType As ShowHelpTypes

    Friend Sub New(t As ShowHelpTypes)
        ' This call is required by the designer.
        InitializeComponent()
        helpType = t
    End Sub

    Class alphabeticComparer
        Implements IComparer
        Public Function Compare(x As Object, y As Object) As Integer Implements IComparer.Compare
            Dim i1 As KeyTableEntry = CType(x, KeyTableEntry)
            Dim i2 As KeyTableEntry = CType(y, KeyTableEntry)
            Return i1.HelpText.CompareTo(i2.HelpText)
        End Function
    End Class

    Class groupComparer
        Implements IComparer
        Public Function Compare(x As Object, y As Object) As Integer Implements IComparer.Compare
            Dim i1 As KeyTableEntry = CType(x, KeyTableEntry)
            Dim i2 As KeyTableEntry = CType(y, KeyTableEntry)
            Dim rv As Integer = i1.Group.CompareTo(i2.Group)
            If rv = 0 Then
                rv = i1.HelpText.CompareTo(i2.HelpText)
            End If
            Return rv
        End Function
    End Class

    Private Sub ShowHelp_Load(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles MyBase.Load
        DialogResult = DialogResult.None
        Dim theKeys As KeyTableEntry() = Commands.CurrentKeys
        Dim displayPrefix As String = ""
        TextBox1.Clear()
        Select Case helpType
            Case ShowHelpTypes.alphabetic
                Array.Sort(theKeys, New alphabeticComparer)
            Case ShowHelpTypes.byGroup
                Array.Sort(theKeys, New groupComparer)
                displayPrefix = "  "
        End Select
        Dim displayGroup As String = vbNullString
        For i As Integer = 0 To theKeys.Length - 1
            If theKeys(i).KeyDef.Key <> Keys.None Then
                If (helpType = ShowHelpTypes.byGroup) And (theKeys(i).Group.ToString <> displayGroup) Then
                    displayGroup = theKeys(i).Group.ToString
                    TextBox1.Text &= displayGroup
                    TextBox1.Text &= vbCrLf
                End If
                TextBox1.Text &= displayPrefix & KeyString(theKeys(i).KeyDef.Key)
                TextBox1.Text &= " - "
                TextBox1.Text &= theKeys(i).HelpText
                TextBox1.Text &= vbCrLf
            End If
        Next
        TextBox1.SelectionStart = 0
        TextBox1.ScrollToCaret()
    End Sub

    Private Sub CancelButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles CnclButton.Click
        DialogResult = DialogResult.Cancel
    End Sub

    Private Sub ShowHelp_Activated(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles MyBase.Activated
        TextBox1.Focus()
    End Sub
End Class