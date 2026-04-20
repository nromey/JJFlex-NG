Imports Escapes
Imports JJTrace

''' <summary>
''' for sending a command to the radio manually
''' </summary>
Friend Class CMDLine
    Friend Buffer As String

    Private Sub Button1_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles OKButton.Click
        Buffer = Escapes.EscapeHelper.Encode(TextBox1.Text)
        Tracing.TraceLine("cmdline:" & Escapes.EscapeHelper.Decode(Buffer), TraceLevel.Info)
        DialogResult = DialogResult.OK
    End Sub

    Private Sub Button2_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles CnclButton.Click
        DialogResult = DialogResult.Cancel
    End Sub

    Private Sub CMDLine_Load(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles MyBase.Load
        TextBox1.Text = ""
    End Sub
End Class