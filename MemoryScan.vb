Imports JJTrace
Imports Radios

Public Class MemoryScan
    Private Const mustHaveGroups As String = "At least one group must be selected."
    Private Const noMemories As String = "There are no memories to scan."
    Private memories As List(Of String)
    Private memoryID As Integer

    Private Sub MemoryScan_Load(sender As System.Object, e As System.EventArgs) Handles MyBase.Load
        wasActive = False
        If Not Controls.Contains(MemoryGroupControl) Then
            MemoryGroupControl.Location = New Point(0, 20)
            MemoryGroupControl.TabIndex = 0
            MemoryGroupControl.ManageUserGroups = False
            Controls.Add(MemoryGroupControl)
        End If

        ' Ensure the memory groups are current.
        If Not MemoryGroupControl.Setup Then
            DialogResult = System.Windows.Forms.DialogResult.Abort
            Return
        End If
        DialogResult = System.Windows.Forms.DialogResult.None
    End Sub

    Private beepLevel As Integer
    Private Sub StartButton_Click(sender As System.Object, e As System.EventArgs) Handles StartButton.Click
        If MemoryGroupControl.GroupsCheckBox.CheckedIndices.Count = 0 Then
            MsgBox(mustHaveGroups)
            DialogResult = System.Windows.Forms.DialogResult.None
            MemoryGroupControl.GroupsCheckBox.Focus()
            Return
        End If
        If Not (IsNumeric(SpeedBox.Text) AndAlso (SpeedBox.Text > "0") AndAlso (SpeedBox.Text <= "600")) Then
            MsgBox(scan.SpeedError)
            DialogResult = System.Windows.Forms.DialogResult.None
            SpeedBox.Focus()
            Return
        End If

        ' Get a list of memories
        memories = New List(Of String)
        For Each Name As String In MemoryGroupControl.GroupsCheckBox.CheckedItems
            memories.AddRange(MemoryGroupControl.groupFile(Name).Members)
        Next
        If memories.Count = 0 Then
            MsgBox(noMemories)
            DialogResult = System.Windows.Forms.DialogResult.None
            MemoryGroupControl.GroupsCheckBox.Focus()
            Return
        End If

        Tracing.TraceLine("Start memory scan", TraceLevel.Info)
        scanstate = scans.memory
#If 0 Then
        ' Disable beep level, speech and automode if on
        beepLevel = RigControl.RigBeepLevel
        RigControl.RigBeepLevel = RigControl.RigBeepOff
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

        ' Start the scan
        memoryID = 0
        RigControl.SelectMemoryByName(memories(memoryID))
        scanTimer.Interval = SpeedBox.Text * 100
        scanTimer.Start()

        ' Leave this form.
        DialogResult = System.Windows.Forms.DialogResult.OK
    End Sub

    Friend Sub MemoryScanPause()
#If 0 Then
        RigControl.RigBeepLevel = beepLevel
#End If
    End Sub

    Friend Sub MemoryScanResume()
#If 0 Then
        RigControl.RigBeepLevel = RigControl.RigBeepOff
#End If
    End Sub

    Friend Sub MemoryScanStop()
#If 0 Then
        RigControl.RigBeepLevel = beepLevel
#End If
    End Sub

    Friend Sub ScanTimer_Tick(ByVal sender As System.Object, ByVal e As System.EventArgs)
        memoryID = (memoryID + 1) Mod memories.Count
        RigControl.SelectMemoryByName(memories(memoryID))
    End Sub

    Private Sub ManageButton_Click(sender As System.Object, e As System.EventArgs) Handles ManageButton.Click
        ManageGroups.ShowDialog()
        MemoryGroupControl.Setup()
        DialogResult = System.Windows.Forms.DialogResult.None
    End Sub

    Private Sub CnclButton_Click(sender As System.Object, e As System.EventArgs) Handles CnclButton.Click
        DialogResult = System.Windows.Forms.DialogResult.Cancel
    End Sub

    Private wasActive As Boolean
    Private Sub MemoryScan_Activated(sender As System.Object, e As System.EventArgs) Handles MyBase.Activated
        If Not wasActive Then
            wasActive = True
            MemoryGroupControl.GroupsBox.Focus()
        End If
    End Sub
End Class