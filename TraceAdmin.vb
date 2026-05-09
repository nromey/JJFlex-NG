Imports JJTrace

Public Class TraceAdmin
    Const defaultName As String = "JJRadioTrace.txt"
    Const TraceLevelDefault As TraceLevel = TraceLevel.Info
    Const mustHaveFile As String = "You must specify a file name."
    Const mustSelect As String = "You must select a trace level."

    Private Enum toggleStates
        off
        onn
    End Enum
    Private toggleText As String() = _
        {"Start", "Stop"}
    Private _toggleState As toggleStates = toggleStates.off
    Private Function toggleState() As toggleStates
        Dim rv As toggleStates = _toggleState
        If rv = toggleStates.off Then
            _toggleState = toggleStates.onn
        Else
            _toggleState = toggleStates.off
        End If
        setButton()
        Return rv
    End Function

    Private Sub setButton()
        ToggleButton.Text = toggleText(_toggleState)
    End Sub

    Private Sub TraceAdmin_Load(sender As System.Object, e As System.EventArgs) Handles MyBase.Load
        If FileNameBox.Text = vbNullString Then
            FileNameBox.Text = My.Computer.FileSystem.SpecialDirectories.MyDocuments & "\" & defaultName
        End If
        setButton()
        ' Set default trace level if not set.
        If LevelListBox.SelectedIndex = -1 Then
            LevelListBox.SelectedIndex = CType(TraceLevelDefault, Integer)
        End If
    End Sub

    Private Function setLevel() As Boolean
        Dim rv As Boolean
        Dim id As Integer = LevelListBox.SelectedIndex
        If id = -1 Then
            MsgBox(mustSelect)
            rv = False
        Else
            Tracing.TraceLine("Changing trace level to " & CType(id, TraceLevel).ToString)
            Tracing.TheSwitch.Level = id
            rv = True
        End If
        Return rv
    End Function

    Private Sub ToggleButton_Click(sender As System.Object, e As System.EventArgs) Handles ToggleButton.Click
        DialogResult = System.Windows.Forms.DialogResult.None
        toggleState()
        If _toggleState = toggleStates.onn Then
            ' check values
            If FileNameBox.Text = vbNullString Then
                MsgBox(mustHaveFile)
                Return
            End If
            If Not setLevel() Then
                MsgBox(setLevel)
                Return
            End If
            ' Archive the prior session (boot or daily) before starting a user trace.
            ' Idempotent: if no session active, this is a no-op.
            ArchiveCurrentTraceSession(TraceSessionOutcome.CleanExit, "User initiated new trace file via TraceAdmin")
            Tracing.TraceFile = FileNameBox.Text
            Tracing.On = True
            BeginNewTraceSession()
            LastUserTraceFile = FileNameBox.Text
            Tracing.TraceLine("User-initiated trace, " & myAssembly.Location & " " & myVersion.ToString() & " " & Date.Now & " level=" & Tracing.TheSwitch.Level.ToString)
            Tracing.TraceLine("tracing to " & Tracing.TraceFile)
        Else
            Tracing.TraceLine("Tracing off")
            ' Archive the user trace before closing it.
            ArchiveCurrentTraceSession(TraceSessionOutcome.CleanExit, "User toggled tracing off via TraceAdmin")
        End If
        ' Exit the form
        DialogResult = System.Windows.Forms.DialogResult.OK
    End Sub

    Private Sub CnclButton_Click(sender As System.Object, e As System.EventArgs) Handles CnclButton.Click
        DialogResult = System.Windows.Forms.DialogResult.Cancel
    End Sub

    Private Sub BrowseButton_Click(sender As System.Object, e As System.EventArgs) Handles BrowseButton.Click
        ' Note the actual TraceFile is set when the trace is started.
        With OpenFileDialog
            .FileName = FileNameBox.Text
            If .ShowDialog = System.Windows.Forms.DialogResult.OK Then
                FileNameBox.Text = .FileName
            End If
        End With
    End Sub
End Class