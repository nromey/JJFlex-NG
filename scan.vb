Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.IO
Imports System.Math
Imports System.Threading
Imports System.Xml.Serialization
Imports JJTrace
Imports Radios

Friend Class scan
    Friend Const SpeedError As String = "The speed must be 1 through 600 tenths of a second."

    ' Preset is set when a scan is from memory.
    ' It must be set before doing the ShowDialog().
    ' ThisPreset is only valid if we know we're using a preset.
    Friend Preset As SavedScanData.ScanData = Nothing
    Dim thisPreset As SavedScanData.ScanData
    Dim CurFreq As Single
    Dim wasActive As Boolean = False

    Friend Function doPreset(ByVal sd As SavedScanData.ScanData, ByVal showForm As Boolean) As DialogResult
        ' showForm should always be false unless called from outside.
        ' The dialogResult will be None if called from inside.
        Dim rslt As DialogResult = DialogResult.None
        If showForm Then
            Preset = sd
            rslt = ShowDialog()
        Else
            ' internal call
            SuspendLayout()
            SelectFieldText(StartFreq, FormatFreq(sd.StartFrequency))
            SelectFieldText(EndFreq, FormatFreq(sd.EndFrequency))
            ' Get increment in KHZ, not HZ.
            Dim str As String
            str = sd.Increment.Substring(0, sd.Increment.Length - 3)
            str &= "." & sd.Increment.Substring(sd.Increment.Length - 3, 1)
            SelectFieldText(Increment, str)
            SelectFieldText(Speed, CStr(sd.speed))
            ' These buttons are only active for a preset.
            RemoveButton.Enabled = True
            ReplaceButton.Enabled = True
            ResumeLayout()
            thisPreset = sd
        End If
        Return rslt
    End Function

    Friend Sub loadScanData()
        Dim cfgFile As Stream
        Try
            cfgFile = File.Open(SavedScanData.pathName, FileMode.Open)
        Catch ex As Exception
            If Err.Number <> 53 Then
                ' No error if not found.
                Tracing.ErrMessageTrace(ex)
            End If
            ' Create an empty SavedScans.
            SavedScans = New SavedScanData
            Return
        End Try
        Try
            Dim xs As New XmlSerializer(GetType(SavedScanData))
            SavedScans = xs.Deserialize(cfgFile)
        Catch ex As Exception
            Tracing.ErrMessageTrace(ex)
        Finally
            cfgFile.Close()
        End Try
    End Sub

    Friend Sub writeScanData()
        Dim cfgFile As Stream
        Try
            cfgFile = File.Open(SavedScanData.pathName, FileMode.Create)
        Catch ex As Exception
            Tracing.ErrMessageTrace(ex)
            Return
        End Try
        Try
            Dim xs As New XmlSerializer(GetType(SavedScanData))
            xs.Serialize(cfgFile, SavedScans)
        Catch ex As Exception
            Tracing.ErrMessageTrace(ex)
        Finally
            cfgFile.Close()
        End Try
    End Sub

    Private Sub scan_Load(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles MyBase.Load
        DialogResult = DialogResult.None
        wasActive = False ' indicate new form
        RemoveButton.Enabled = False
        ReplaceButton.Enabled = False
        ' Load any configured scans if not loaded.
        If SavedScans Is Nothing Then
            loadScanData()
        End If
        If Preset IsNot Nothing Then
            Dim sd As SavedScanData.ScanData = Preset
            Preset = Nothing
            doPreset(sd, False)
        End If
    End Sub

    Private Function getIncrement(ByVal incIn As String) As String
        ' The increment must be in KHZ 0.01 - 999.99.
        ' It can be in 10 HZ increments.
        Dim incr As Integer
        Dim incOut As String = Nothing
        Try
            incr = CInt(1000 * CSng(incIn))
            Dim r As Integer
            DivRem(incr, 10, r)
            If r OrElse (incr < 10) OrElse (incr > 999990) Then
                incOut = ""
            Else
                incOut = CStr(incr)
            End If
        Catch ex As Exception
            incOut = ""
        End Try
        Return incOut
    End Function
    Private Function checkScan(ByRef low As String, ByRef high As String, ByRef incstr As String) As Boolean
        Dim errFLD As TextBox = Nothing
        low = FormatFreqForRadio(StartFreq.Text)
        high = FormatFreqForRadio(EndFreq.Text)
        incstr = getIncrement(Increment.Text)
        If low Is Nothing Then
            MsgBox("Starting frequency" & BadFreqMSG)
            errFLD = StartFreq
        ElseIf high Is Nothing Then
            MsgBox("Ending frequency" & BadFreqMSG)
            errFLD = EndFreq
        ElseIf high <= low Then
            MsgBox("The ending frequency must exceed the starting frequency.")
            errFLD = StartFreq
        ElseIf incstr = "" Then
            MsgBox("The increment must be in KHZ, 0.00 through 999.99")
            errFLD = Increment
        ElseIf Not (IsNumeric(Speed.Text) AndAlso (Speed.Text > "0") AndAlso (Speed.Text <= "600")) Then
            MsgBox(SpeedError)
            errFLD = Speed
        End If
        If errFLD IsNot Nothing Then
            errFLD.Focus()
            Return False
        End If
        Return True
    End Function
    Private Sub StartButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles StartButton.Click
        Dim low As String = Nothing
        Dim high As String = Nothing
        Dim incstr As String = Nothing
        If Not checkScan(low, high, incstr) Then
            DialogResult = DialogResult.None
            Return
        End If
        ' The data is good.
        StartLinearScan(low, high, incstr, Speed.Text)
        DialogResult = DialogResult.OK
    End Sub

    Private Sub CnclButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles CnclButton.Click
        DialogResult = DialogResult.Cancel
    End Sub

    Private Sub ClearButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles ClearButton.Click
        StartFreq.Text = ""
        EndFreq.Text = ""
        Increment.Text = ""
        Speed.Text = ""
        StartFreq.Focus()
    End Sub

    Private Sub StartFreq_Leave(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles StartFreq.Leave
        SelectFieldText(StartFreq, Nothing)
    End Sub
    Private Sub EndFreq_Leave(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles EndFreq.Leave
        SelectFieldText(EndFreq, Nothing)
    End Sub
    Private Sub Increment_Leave(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles Increment.Leave
        SelectFieldText(Increment, Nothing)
    End Sub
    Private Sub Speed_Leave(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles Speed.Leave
        SelectFieldText(Speed, Nothing)
    End Sub

    Private Sub UseSavedButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles UseSavedButton.Click
        DialogResult = DialogResult.None
        If SelectScan.ShowDialog <> DialogResult.OK Then
            Return
        End If
        Dim sd As SavedScanData.ScanData = SavedScans.Item(SelectScan.ItemIndex)
        Select Case sd.Type
            Case SavedScanData.ScanTypes.linear
                doPreset(sd, False)
#If 0 Then
            Case SavedScanData.ScanTypes.memory
                MemoryScan.doPreset(sd, True)
                ' Leave upon return.
                DialogResult = DialogResult.OK
#End If
        End Select
    End Sub

    Private Sub SaveButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles SaveButton.Click
        DialogResult = DialogResult.None
        Dim low As String = Nothing
        Dim high As String = Nothing
        Dim incstr As String = Nothing
        If Not checkScan(low, high, incstr) Then
            Return
        End If
        Dim name As String
        name = InputBox("Scan name: ", "Name this scan")
        If name = "" Then
            MsgBox("You must provide a name.")
            Return
        End If
        ' The data is good.
        Dim lsd As New SavedScanData.ScanData(name, low, high, incstr, _
                                              Speed.Text)
        If SavedScans.Add(lsd) Then
            writeScanData()
        Else
            MsgBox(name & DupEntryMsg)
        End If
    End Sub

    Private Sub scan_Activated(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles MyBase.Activated
        If Not wasActive Then
            ' new form
            wasActive = True
            StartFreq.Focus()
        End If
    End Sub

    Private Sub ReplaceButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles ReplaceButton.Click
        If Not SavedScans.Remove(thisPreset.name) Then
            ShowInternalError(MSReplace)
        Else
            Dim low As String = FormatFreqForRadio(StartFreq.Text)
            Dim high As String = FormatFreqForRadio(EndFreq.Text)
            Dim incstr As String = getIncrement(Increment.Text)
            Dim lsd As New SavedScanData.ScanData(thisPreset.name, low, _
                                                  high, incstr, Speed.Text)
            If SavedScans.Add(lsd) Then
                writeScanData()
            Else
                ShowInternalError(ScanReplaceAdd)
            End If
        End If
    End Sub

    Private Sub RemoveButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles RemoveButton.Click
        If SavedScans.Remove(thisPreset.name) Then
            writeScanData()
            DialogResult = DialogResult.OK
        Else
            ShowInternalError(ScanRemove)
        End If
    End Sub

    ' region - scan control
#Region "scan control"
    Dim startScanFreq, curScanFreq, endScanFreq, scanIncrement As UInt64

    Friend Sub StartLinearScan(ByVal low As String, ByVal high As String, ByVal increment As String, ByVal speed As String)
        Tracing.TraceLine("StartLinearScan:" & low & " " & high & " " & increment & " " & speed, TraceLevel.Info)
        ' LinearScan
        StopScan()
        scanTimer.Interval = TimeSpan.FromMilliseconds(CInt(speed) * 100)
        curScanFreq = low
        startScanFreq = curScanFreq
        scanIncrement = increment
        endScanFreq = high
        scanstate = scans.linear
        ' Disable speech if on
#If 0 Then
        speechStatus = RigControl.RigSpeech
        If speechStatus Then
            RigControl.RigSpeech = False
        End If
        autoModeStatus = RigControl.AutoMode
        If autoModeStatus Then
            RigControl.AutoMode = False
            modeStatus = RigControl.Mode
        End If
#End If
        StatusBox.Write("Scan", Running)
        ' StartScan
        RigControl.Frequency = curScanFreq
        scanTimer.Start()
    End Sub

    Friend Sub ScanTimer_Tick(ByVal sender As System.Object, ByVal e As System.EventArgs)
        curScanFreq += scanIncrement
        If curScanFreq > endScanFreq Then
            curScanFreq = startScanFreq
        End If
        RigControl.Frequency = curScanFreq
    End Sub

    ' This works for both linear and memory scans.
    Friend Sub PauseScan()
        Tracing.TraceLine("PauseScan", TraceLevel.Info)
        ' If scanning, pause the scan.
        If (scanstate = scans.none) OrElse (Not ScanInProcess) Then
            Return
        End If
        ' Pause
        StatusBox.Write("Scan", Paused)
        ' Restore the speech
#If 0 Then
        If speechStatus Then
            RigControl.RigSpeech = True
            ' This should speak the frequency.
            RigControl.Frequency = RigControl.Frequency
        End If
#End If
        ' Note we leave automode disabled.
        ' Call any memory-scan-specifics
        If scanstate = scans.memory Then
            'MemoryScan.MemoryScanPause()
        End If
        scanTimer.Stop()
    End Sub

    ' This works for both linear and memory scans.
    Friend Sub resumeScan()
        Tracing.TraceLine("ResumeScan", TraceLevel.Info)
        ' If scanning, resume the scan.
        If (scanstate = scans.none) OrElse ScanInProcess Then
            Return
        End If
        ' Resume
        StatusBox.Write("Scan", Running)
        ' speech off if was on.
#If 0 Then
        If speechStatus Then
            RigControl.RigSpeech = False
        End If
#End If
        ' Call any memory-scan-specifics
        If scanstate = scans.memory Then
            'MemoryScan.MemoryScanResume()
        End If
        scanTimer.Start()
    End Sub

    ' This works for both linear and memory scans.
    Friend Sub StopScan()
        Tracing.TraceLine("stopScan", TraceLevel.Info)
        If scanstate <> scans.none Then
            StatusBox.Write("Scan", OffWord)
            scanTimer.Stop()
#If 0 Then
            ' Restore auto mode
            If autoModeStatus Then
                RigControl.AutoMode = True
            End If
            ' Restore the speech
            If speechStatus Then
                RigControl.RigSpeech = True
                ' This should speak the frequency.
                RigControl.Frequency = RigControl.Frequency
            End If
            If autoModeStatus Then
                ' Restore the mode; auto mode might have changed it.
                Thread.Sleep(250) ' wait a bit
                RigControl.Mode = modeStatus
            End If
#End If
            ' Call any memory-scan-specifics
            If scanstate = scans.memory Then
                'MemoryScan.MemoryScanStop()
            End If
            scanstate = scans.none
        End If
    End Sub
#End Region
End Class