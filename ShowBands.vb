Imports System.Windows.Forms
Imports HamBands
Imports JJTrace

Public Class ShowBands
    Private Sub setupBoxes()
        Static setup As Boolean = False
        If Not setup Then
            setup = True
            For Each b As Bands.BandItem In Bands.TheBands
                BandBox.Items.Add(b.Name)
            Next
            For Each str As String In [Enum].GetNames(GetType(Bands.Licenses))
                If str = "none" Then
                    LicenseBox.Items.Add("All")
                Else
                    LicenseBox.Items.Add(str)
                End If
            Next
            For Each str As String In [Enum].GetNames(GetType(Bands.Modes))
                If str = "none" Then
                    ModeBox.Items.Add("All")
                Else
                    ModeBox.Items.Add(str)
                End If
            Next
        End If
        ' default to current situation
        LicenseBox.SelectedIndex = CurrentOp.License
        If (RigControl IsNot Nothing) AndAlso Power Then
            Dim band As Bands.BandItem = Bands.Query(RigControl.RXFrequency)
            If band IsNot Nothing Then
                BandBox.SelectedIndex = band.ID
            End If
            Select Case RigControl.Mode.ToString
                Case "cw", "cwr", "fsk", "fskr"
                    ModeBox.SelectedIndex = Bands.Modes.CW
                Case Else
                    ModeBox.SelectedIndex = Bands.Modes.PhoneCW
            End Select
        End If
    End Sub

    Private Sub ShowBands_Load(sender As System.Object, e As System.EventArgs) Handles MyBase.Load
        DialogResult = System.Windows.Forms.DialogResult.None
        setupBoxes()
    End Sub

    Private Sub ShowButton_Click(sender As System.Object, e As System.EventArgs) Handles ShowButton.Click
        Dim rslt As Bands.BandItem = Nothing
        DialogResult = DialogResult.None
        If boxSelected(BandBox) Then
            If boxSelected(LicenseBox) Then
                If boxSelected(ModeBox) Then
                    rslt = Bands.Query(DirectCast(BandBox.SelectedIndex, Bands.BandNames), DirectCast(LicenseBox.SelectedIndex, Bands.Licenses), DirectCast(ModeBox.SelectedIndex, Bands.Modes))
                Else
                    rslt = Bands.Query(DirectCast(BandBox.SelectedIndex, Bands.BandNames), DirectCast(LicenseBox.SelectedIndex, Bands.Licenses))
                End If
            Else
                ' license not specified
                If boxSelected(ModeBox) Then
                    rslt = Bands.Query(DirectCast(BandBox.SelectedIndex, Bands.BandNames), DirectCast(ModeBox.SelectedIndex, Bands.Modes))
                Else
                    ' neither license nor mode specified
                    rslt = Bands.Query(DirectCast(BandBox.SelectedIndex, Bands.BandNames))
                End If
            End If
        Else
            MessageBox.Show("select a band", "required", MessageBoxButtons.OK)
            BandBox.Focus()
        End If
        If rslt IsNot Nothing Then
            showResult(rslt)
            BandBox.SelectedIndex = -1
            LicenseBox.SelectedIndex = -1
            ModeBox.SelectedIndex = -1
            ResultBox.SelectionStart = 0
            ResultBox.Focus()
        End If
    End Sub

    Private Function boxSelected(lb As ListBox) As Boolean
        If lb.Equals(BandBox) Then
            Return (lb.SelectedIndex >= 0)
        Else
            ' not a band, treat the first item as a non-selection.
            Return (lb.SelectedIndex > 0)
        End If
    End Function

    Private Sub showResult(rslt As Bands.BandItem)
        ResultBox.Text = Convert.ToString(rslt.Name) & vbTab & showFreq(rslt.Low) & vbTab & showFreq(rslt.High) & vbCr & vbLf
        If rslt.Divisions IsNot Nothing Then
            For Each d As Bands.BandDivision In rslt.Divisions
                If d.License IsNot Nothing Then
                    For Each l As Bands.Licenses In d.License
                        ResultBox.Text += l.ToString() & " "
                    Next
                    ResultBox.Text += "- "
                End If
                ResultBox.Text += showFreq(d.Low) & " " & showFreq(d.High)
                If d.Mode IsNot Nothing Then
                    ResultBox.Text += " - "
                    For Each m As Bands.Modes In d.Mode
                        ResultBox.Text += m.ToString() & " "
                    Next
                End If
                ResultBox.Text += vbCr & vbLf
            Next
        End If
    End Sub

    Private Function showFreq(frequency As ULong) As String
        Return (frequency \ 1000).ToString()
    End Function

    Private Sub CnclButton_Click(sender As System.Object, e As System.EventArgs) Handles CnclButton.Click
        DialogResult = System.Windows.Forms.DialogResult.Cancel
    End Sub
End Class