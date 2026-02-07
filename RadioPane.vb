Imports System.Drawing
Imports System.Windows.Forms
Imports JJTrace
Imports Radios

''' <summary>
''' Minimal radio status pane for Logging Mode.
''' Shows frequency, mode, band, and tune step from the connected radio.
''' Tab through focusable status items; arrow keys tune; Left/Right change step size.
''' </summary>
''' <remarks>
''' Future: context-aware hotkeys sprint will add band switching on F-keys, etc.
''' For now this is a "peek at the radio" — exit Logging Mode for full control.
''' Tuning uses the same step sizes as FlexLib's TuneStepList:
''' 1, 10, 50, 100, 500, 1000, 2000, 3000 Hz.
''' </remarks>
Friend Class RadioPane
    Inherits UserControl

    ' Focusable read-only status items — Tab cycles through these.
    Private FreqBox As TextBox      ' Read-only TextBox so screen reader can Tab to it
    Private ModeBox As TextBox
    Private BandBox As TextBox
    Private TuneStepBox As TextBox  ' Shows current step size; Left/Right to change

    Private StatusLabel As Label    ' Hint text at the bottom

    ' Local tune step list (mirrors FlexLib Slice.TuneStepList).
    ' Values in Hz.
    Private Shared ReadOnly TuneStepList() As Integer = {1, 10, 50, 100, 500, 1000, 2000, 3000}
    Private currentStepIndex As Integer = 1  ' Default index 1 = 10 Hz

    Friend Sub New()
        MyBase.New()
        Me.Name = "RadioPane"
        Me.AccessibleName = "Radio pane"
        Me.AccessibleRole = AccessibleRole.Pane
        BuildControls()
    End Sub

    ''' <summary>
    ''' Refresh the display from the current radio state.
    ''' Call this when entering Logging Mode or after tuning.
    ''' </summary>
    Friend Sub UpdateFromRadio()
        If RigControl Is Nothing OrElse Not Power Then
            FreqBox.Text = "No radio"
            FreqBox.AccessibleName = "Frequency: no radio connected"
            ModeBox.Text = "---"
            ModeBox.AccessibleName = "Mode: none"
            BandBox.Text = "---"
            BandBox.AccessibleName = "Band: none"
            TuneStepBox.Text = "---"
            TuneStepBox.AccessibleName = "Tune step: none"
            StatusLabel.Text = "Connect a radio first"
            Return
        End If

        ' Frequency (RXFrequency is ulong, in Hz * 1e6 scale — FormatFreq handles it)
        Dim rxFreq = RigControl.RXFrequency
        Dim freqText = FormatFreq(rxFreq)
        FreqBox.Text = freqText & " MHz"
        FreqBox.AccessibleName = "Frequency " & freqText & " megahertz"

        ' Mode
        Dim modeText = ""
        If RigControl.Mode IsNot Nothing Then
            modeText = RigControl.Mode.ToString().ToUpper()
        End If
        ModeBox.Text = If(modeText <> "", modeText, "---")
        ModeBox.AccessibleName = "Mode " & If(modeText <> "", modeText, "none")

        ' Band
        Dim bandItem = HamBands.Bands.Query(RigControl.TXFrequency)
        Dim bandText = If(bandItem IsNot Nothing, bandItem.Name, "---")
        BandBox.Text = bandText
        BandBox.AccessibleName = "Band " & If(bandItem IsNot Nothing, bandItem.Name, "none")

        ' Tune step display
        Dim stepText = FormatStepSize(TuneStepList(currentStepIndex))
        TuneStepBox.Text = "Step: " & stepText
        TuneStepBox.AccessibleName = "Tune step " & stepText

        StatusLabel.Text = "Up/Down tune, Left/Right step size"
    End Sub

    ''' <summary>
    ''' Handle arrow keys for tuning, step-size changes, and Ctrl+F for manual frequency.
    ''' Up/Down = tune by 1 step. Shift+Up/Down = tune by 10 steps.
    ''' Left/Right = cycle through TuneStepList (change step size).
    ''' Ctrl+F = enter frequency manually via FreqInput dialog.
    ''' </summary>
    Protected Overrides Function ProcessCmdKey(ByRef msg As Message, keyData As Keys) As Boolean
        If RigControl Is Nothing OrElse Not Power Then
            Return MyBase.ProcessCmdKey(msg, keyData)
        End If

        Dim handled = True
        Select Case keyData
            Case Keys.Up
                TuneBySteps(1)
            Case Keys.Down
                TuneBySteps(-1)
            Case Keys.Up Or Keys.Shift
                TuneBySteps(10)
            Case Keys.Down Or Keys.Shift
                TuneBySteps(-10)
            Case Keys.Right
                ChangeTuneStep(1)
            Case Keys.Left
                ChangeTuneStep(-1)
            Case Keys.F Or Keys.Control
                EnterManualFrequency()
            Case Else
                handled = False
        End Select

        If handled Then
            UpdateFromRadio()
            Return True
        End If
        Return MyBase.ProcessCmdKey(msg, keyData)
    End Function

    ''' <summary>
    ''' Tune the radio by the given number of steps.
    ''' Uses RigControl.RXFrequency (ulong) which is freq in Hz (via LibFreqtoLong).
    ''' </summary>
    Private Sub TuneBySteps(steps As Integer)
        Try
            Dim stepHz = TuneStepList(currentStepIndex)
            Dim currentFreq = RigControl.RXFrequency  ' ulong, Hz
            Dim delta = CLng(steps) * CLng(stepHz)
            Dim newFreq = CLng(currentFreq) + delta
            If newFreq > 0 Then
                RigControl.RXFrequency = CULng(newFreq)
            End If
        Catch ex As Exception
            Tracing.TraceLine("RadioPane.TuneBySteps: " & ex.Message, Diagnostics.TraceLevel.Error)
        End Try
    End Sub

    ''' <summary>
    ''' Show the FreqInput dialog to enter a frequency manually.
    ''' Same dialog used by Ctrl+F in Classic/Modern modes.
    ''' </summary>
    Private Sub EnterManualFrequency()
        Try
            If FreqInput.ShowDialog() = DialogResult.OK Then
                ' FreqInput.Buffer is a string in Hz. WriteFreq sends it to the radio.
                RigControl.Frequency = CLng(FreqInput.Buffer)
                ScreenReaderOutput.Speak("Frequency set", True)
            End If
        Catch ex As Exception
            Tracing.TraceLine("RadioPane.EnterManualFrequency: " & ex.Message, Diagnostics.TraceLevel.Error)
        End Try
    End Sub

    ''' <summary>
    ''' Cycle through the TuneStepList. direction = +1 (larger) or -1 (smaller).
    ''' Announces the new step size via screen reader.
    ''' </summary>
    Private Sub ChangeTuneStep(direction As Integer)
        Dim newIdx = currentStepIndex + direction
        If newIdx < 0 Then newIdx = 0
        If newIdx >= TuneStepList.Length Then newIdx = TuneStepList.Length - 1
        currentStepIndex = newIdx

        Dim stepText = FormatStepSize(TuneStepList(currentStepIndex))
        TuneStepBox.Text = "Step: " & stepText
        TuneStepBox.AccessibleName = "Tune step " & stepText
        ScreenReaderOutput.Speak("Tune step " & stepText, True)
    End Sub

    ''' <summary>
    ''' Format a step size in Hz for display. E.g. 10 → "10 Hz", 1000 → "1 kHz".
    ''' </summary>
    Private Shared Function FormatStepSize(hz As Integer) As String
        If hz >= 1000 Then
            Return (hz / 1000).ToString() & " kHz"
        Else
            Return hz.ToString() & " Hz"
        End If
    End Function

#Region "UI Construction"

    Private Sub BuildControls()
        Me.SuspendLayout()
        Me.BackColor = SystemColors.Control

        Dim yPos As Integer = 8

        ' Frequency — read-only TextBox so Tab can reach it and screen reader reads it.
        FreqBox = MakeReadOnlyField("Frequency", 8, yPos, 180, 24,
                                     New Font(Me.Font.FontFamily, 12, FontStyle.Bold))
        FreqBox.Text = "No radio"
        FreqBox.TabIndex = 0
        yPos += 30

        ' Mode
        ModeBox = MakeReadOnlyField("Mode", 8, yPos, 80, 20)
        ModeBox.Text = "---"
        ModeBox.TabIndex = 1

        ' Band — to the right of mode
        BandBox = MakeReadOnlyField("Band", 92, yPos, 80, 20)
        BandBox.Text = "---"
        BandBox.TabIndex = 2
        yPos += 26

        ' Tune step
        TuneStepBox = MakeReadOnlyField("Tune step", 8, yPos, 120, 20)
        TuneStepBox.Text = "Step: 10 Hz"
        TuneStepBox.TabIndex = 3
        yPos += 28

        ' Hint text (not focusable)
        StatusLabel = New Label() With {
            .Text = "Up/Down tune" & vbCrLf & "Left/Right step size" & vbCrLf & "Shift+Up/Down coarse",
            .Location = New Point(8, yPos),
            .Size = New Size(180, 48),
            .ForeColor = SystemColors.GrayText,
            .AutoSize = False
        }
        Me.Controls.Add(StatusLabel)

        Me.ResumeLayout(False)
    End Sub

    ''' <summary>
    ''' Create a read-only TextBox that behaves like a label but is Tab-focusable
    ''' for screen reader navigation.
    ''' </summary>
    Private Function MakeReadOnlyField(accessName As String, x As Integer, y As Integer,
                                        w As Integer, h As Integer,
                                        Optional fnt As Font = Nothing) As TextBox
        Dim tb As New TextBox() With {
            .Location = New Point(x, y),
            .Size = New Size(w, h),
            .ReadOnly = True,
            .BorderStyle = BorderStyle.None,
            .BackColor = SystemColors.Control,
            .TabStop = True,
            .AccessibleName = accessName,
            .AccessibleRole = AccessibleRole.StaticText
        }
        If fnt IsNot Nothing Then tb.Font = fnt
        Me.Controls.Add(tb)
        Return tb
    End Function

#End Region

End Class
