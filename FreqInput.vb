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
            ' Play confirm tone based on typing sound mode
            Try
                Dim configDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) & "\JJFlexRadio\Radios"
                Dim config = JJFlexWpf.AudioOutputConfig.Load(configDir)
                If config.TypingSound = JJFlexWpf.TypingSoundMode.Mechanical Then
                    JJFlexWpf.EarconPlayer.TypewriterBellTone()
                Else
                    JJFlexWpf.EarconPlayer.DingTone()
                End If
            Catch
                JJFlexWpf.EarconPlayer.DingTone()
            End Try
            DialogResult = DialogResult.OK
            Buffer = str
        End If
    End Sub

    Private Sub Button2_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles CnclButton.Click
        DialogResult = DialogResult.Cancel
    End Sub

    Private Sub FreqBox_KeyPress(ByVal sender As Object, ByVal e As System.Windows.Forms.KeyPressEventArgs) Handles FreqBox.KeyPress
        ' Play typing sound for digits based on current audio config setting
        If Char.IsDigit(e.KeyChar) OrElse e.KeyChar = "."c Then
            Try
                Dim configDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) & "\JJFlexRadio\Radios"
                Dim config = JJFlexWpf.AudioOutputConfig.Load(configDir)
                JJFlexWpf.EarconPlayer.PlayTypingSound(e.KeyChar, config.TypingSound)
            Catch
            End Try
        End If
    End Sub
End Class