Imports System.Reflection
Imports Flex.Smoothlake.FlexLib
Imports JJTrace

Public Class AboutProgram
    ' DLL's whose versions are shown.
    Private Shared dllList As String() = {
        "flexlib",
        "jjloglib",
        "radios",
        "radioboxes"
        }

    Private Sub AboutProgram_Load(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles MyBase.Load
        If My.Application.Info.Title <> "" Then
            TextBox1.Text = My.Application.Info.Title
        Else
            TextBox1.Text = System.IO.Path.GetFileNameWithoutExtension(My.Application.Info.AssemblyName)
        End If
        TextBox1.Text &= " " & My.Application.Info.ProductName
        TextBox1.Text &= " " & String.Format("Version {0}", My.Application.Info.Version.ToString)
        TextBox1.Text &= vbCrLf & "Copyright " & My.Application.Info.Copyright
        TextBox1.Text &= vbCrLf & My.Application.Info.CompanyName
        TextBox1.Text &= vbCrLf & "Contributors: J.J. Shaffer, K5NER"
        TextBox1.Text &= vbCrLf & vbCrLf
        TextBox1.Text &= "dll versions:"
        Dim a As Assembly = Assembly.GetExecutingAssembly
        For Each an As AssemblyName In a.GetReferencedAssemblies
            Try
                If dllList.Contains(an.Name.ToLower) Then
                    TextBox1.Text &= vbCrLf & an.Name & ":" & an.Version.ToString
                End If
            Catch
            End Try
        Next
    End Sub

    Private Sub OKButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles OKButton.Click
        DialogResult = DialogResult.OK
    End Sub
End Class
