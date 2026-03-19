Friend Class FreqInput
    Friend Buffer As String = ""

    Private Sub FreqInput_Load(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles MyBase.Load
        FreqBox.Text = ""
    End Sub

    Private Sub Button1_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles OKButton.Click
        DialogResult = Nothing
        Dim raw = FreqBox.Text.Trim()

        ' Pass through special terms without frequency validation
        If raw.Equals("cqtest", StringComparison.OrdinalIgnoreCase) Then
            DialogResult = DialogResult.OK
            Buffer = raw
            Return
        End If

        ' Pass through calibration references
        If JJFlexWpf.CalibrationEngine.VerifyCalibration(raw) IsNot Nothing Then
            DialogResult = DialogResult.OK
            Buffer = raw
            Return
        End If

        Dim str As String
        str = FormatFreqForRadio(raw)
        If str Is Nothing Then
            MsgBox("Frequency" & BadFreqMSG)
        Else
            DialogResult = DialogResult.OK
            Buffer = str
        End If
    End Sub

    Private Sub Button2_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles CnclButton.Click
        DialogResult = DialogResult.Cancel
    End Sub
End Class