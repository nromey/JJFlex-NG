Imports System.Diagnostics
Imports JJTrace

Public Class ReverseBeacon
    Private webBaseAddress As String = "http://www.reversebeacon.net/dxsd1/dxsd1.php?f=0&t=dx&c="

    Private Sub ReverseBeacon_Load(sender As System.Object, e As System.EventArgs) Handles MyBase.Load
        DialogResult = System.Windows.Forms.DialogResult.None
        CallBox.Text = CurrentOp.callSign
        CallBox.SelectionLength = CallBox.Text.Length
    End Sub

    Private Sub OkButton_Click(sender As System.Object, e As System.EventArgs) Handles OkButton.Click
        Dim addr As String = webBaseAddress & CallBox.Text
        Tracing.TraceLine("beacon:" & addr, TraceLevel.Info)
        Process.Start(addr)
        DialogResult = System.Windows.Forms.DialogResult.OK
    End Sub

    Private Sub CnclButton_Click(sender As System.Object, e As System.EventArgs) Handles CnclButton.Click
        DialogResult = System.Windows.Forms.DialogResult.Cancel
    End Sub
End Class