Imports System.IO
Imports System.Windows.Forms

Public Class Welcome

    Private Sub Welcome_Load(sender As System.Object, e As System.EventArgs) Handles MyBase.Load
        Try
            Dim theFile As New StreamReader("Welcome.txt")
            WelcomeBox.Text = theFile.ReadToEnd()
            theFile.Close()
            theFile.Dispose()
        Catch ex As Exception
            MsgBox(ex.Message)
            End
        End Try
        DialogResult = System.Windows.Forms.DialogResult.None
        WelcomeBox.SelectionStart = 0
    End Sub

    Private Sub DocButton_Click(sender As System.Object, e As System.EventArgs) Handles DocButton.Click
        System.Diagnostics.Process.Start(DocName)
        System.Threading.Thread.Sleep(0)
        DialogResult = System.Windows.Forms.DialogResult.None
    End Sub

    Private Sub ConfigButton_Click(sender As System.Object, e As System.EventArgs) Handles ConfigButton.Click
        ' continue
        DialogResult = System.Windows.Forms.DialogResult.OK
    End Sub

    Private Sub ImportButton_Click(sender As Object, e As EventArgs) Handles ImportButton.Click
        If ImportSetup.ImportSetup() Then
            DialogResult = DialogResult.OK
        End If
    End Sub

    Private Sub QuitButton_Click(sender As System.Object, e As System.EventArgs) Handles QuitButton.Click
        DialogResult = System.Windows.Forms.DialogResult.Abort
    End Sub
End Class