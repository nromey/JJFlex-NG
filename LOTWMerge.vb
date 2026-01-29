Imports System.Diagnostics
Imports System.IO
Imports System.Threading
Imports adif
Imports JJLogLib
Imports JJTrace
Imports MsgLib

Public Class LOTWMerge
    Private Const LOTWFileDeficient As String = "The LOTW file doesn't contain the correct information."
    Private Const logFileDeficient As String = "The JJRadio log file doesn't contain the correct information."
    Private Const merging As String = "Merging ..."
    Private Const mergComplete As String = "Merge complete, {0} records in, {1} records updated."
    Private Const mergeErrors As String = "Errors found during the merge."
    Private Const mergeLog As String = "MergeLog.txt"
    Private mergeLogStream As StreamWriter = Nothing
    Private _mergeLogname As String
    Private Property mergeLogname As String
        Get
            Return _mergeLogname
        End Get
        Set(value As String)
            Dim id As Integer = CurrentOp.LogFile.LastIndexOf("\"c)
            _mergeLogname = CurrentOp.LogFile.Substring(0, id) & "\" & value
        End Set
    End Property

    Private Property LOTWFilename As String
        Get
            Return LOTWFileBox.Text
        End Get
        Set(value As String)
            LOTWFileBox.Text = value
        End Set
    End Property

    Private inSession As AdifSession = Nothing
    Private outSession As LogSession = Nothing

    Private requiredTags = New List(Of String) From _
    { _
        AdifTags.ADIF_Call, _
        AdifTags.ADIF_DateOn, _
        AdifTags.ADIF_TimeOn, _
        AdifTags.ADIF_QSL_RCVD _
    }
    Private Function hasRequiredTags(rec As Dictionary(Of String, LogFieldElement)) As Boolean
        Dim rv As Boolean = True
        For Each Tag As String In requiredTags
            If Not rec.Keys.Contains(Tag) Then
                rv = False
                Exit For
            End If
        Next
        Return rv
    End Function

    Private Sub LOTWMerge_Load(sender As System.Object, e As System.EventArgs) Handles MyBase.Load
        Tracing.TraceLine("LOTWMerge load", TraceLevel.Info)
        With LOTWFileDialog
            .InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            DialogResult = .ShowDialog()
            If DialogResult <> System.Windows.Forms.DialogResult.OK Then
                Return
            End If
            LOTWFilename = .FileName
        End With

        ' Check for proper format and required tags.
        DialogResult = System.Windows.Forms.DialogResult.None
        Dim goodLOTW As Boolean = True
        Dim LOTWRecPresent As Boolean = False
        Try
            inSession = New AdifSession(LOTWFilename)
            Dim rec As Dictionary(Of String, LogFieldElement) = inSession.Read(False)
            Do While (rec.Count <> 0)
                If inSession.IsRecord(rec) Then
                    LOTWRecPresent = True
                    If Not hasRequiredTags(rec) Then
                        goodLOTW = False
                        Exit Do
                    End If
                End If
                rec = inSession.Read(False)
            Loop
        Catch ex As Exception
            Tracing.TraceLine("LOTWMerge checking ADIF file:" & ex.Message, TraceLevel.Error)
            goodLOTW = False
        Finally
            inSession.Close()
            inSession = Nothing
        End Try
        ' the format must be good and must have one record.
        If Not goodLOTW Or Not LOTWRecPresent Then
            MsgBox(LOTWFileDeficient)
            DialogResult = System.Windows.Forms.DialogResult.Abort
        Else
            ' restart the session.
            inSession = New AdifSession(LOTWFilename)
        End If

        Dim goodLog As Boolean = True
        If File.Exists(CurrentOp.LogFile) Then
            outSession = New LogSession(ContactLog)
            ' Start the session.
            If outSession.Start(CurrentOp, Nothing) Then
                Form1.StatusBox.Write("LogFile",
                        LogCharacteristics.TrimmedFilename(ContactLog.Name, 20))
                goodLog = hasRequiredTags(outSession.FieldDictionary)
            Else
                goodLog = False
            End If
        Else
            ' File doesn't exist.
            goodLog = False
        End If
        If Not goodLog Then
            MsgBox(logFileDeficient)
            DialogResult = System.Windows.Forms.DialogResult.Abort
        Else
            LogBox.Text = CurrentOp.LogFile
        End If
    End Sub

    Private mergeThread As Thread = Nothing
    Private cancelling As Boolean = False
    Private Delegate Sub formEndDel(rv As DialogResult)
    Private Event formEndEvent As formEndDel

    Private Sub OKButton_Click(sender As System.Object, e As System.EventArgs) Handles OKButton.Click
        Tracing.TraceLine("LOTWMerge okButton", TraceLevel.Info)

        ' Delete any existing merge log.
        mergeLogname = mergeLog ' sets the path in the logfile directory
        Try
            If File.Exists(mergeLogname) Then
                File.Delete(mergeLogname)
            End If
        Catch ex As Exception
            MsgBox(ex.Message)
            Return
        End Try

        AddHandler formEndEvent, AddressOf formEndHandler
        OKButton.Enabled = False

        mergeThread = New Thread(AddressOf mergeProc)
        mergeThread.Name = "mergeThread"
        ProgressBox.Focus()
        mergeThread.Start()
        Thread.Sleep(0)
    End Sub

    Private inCount As Integer = 0
    Private outCount As Integer = 0
    Private Sub mergeProc()
        Try
            Tracing.TraceLine("mergeProc", TraceLevel.Info)
            MsgLib.TextOut.DisplayText(ProgressBox, merging, False)

            Do While Not cancelling
                Dim flds As Dictionary(Of String, LogFieldElement) = inSession.Read(False)
                If flds.Count = 0 Then
                    Exit Do
                End If
                If inSession.IsRecord(flds) Then
                    inCount += 1
                    Dim callsign As LogFieldElement = Nothing
                    Dim dat As LogFieldElement = Nothing
                    Dim tim As LogFieldElement = Nothing
                    If Not flds.TryGetValue(AdifTags.ADIF_Call, callsign) Or _
                       Not flds.TryGetValue(AdifTags.ADIF_DateOn, dat) Or _
                       Not flds.TryGetValue(AdifTags.ADIF_TimeOn, tim) Then
                        writeMergeLog("LOTWMerge record " & inCount & " with missing fields:" & callsign.Data & " " & dat.Data & " " & tim.Data)
                        Continue Do
                    End If
                    Dim args = New List(Of LogFieldElement)
                    args.Add(callsign)
                    ' Must reformat the date.
                    If dat.Data.Length = 8 Then
                        dat.Data = dat.Data.Substring(4, 2) & "/" & dat.Data.Substring(6, 2) & "/" & dat.Data.Substring(0, 4)
                    End If
                    args.Add(dat)
                    ' We have only a 4-char time.
                    If tim.Data.Length > 4 Then
                        tim.Data = tim.Data.Substring(0, 4)
                    End If
                    args.Add(tim)
                    outSession.SetSearchArg(args)
                    ' look for match from beginning of the log.
                    Dim match As LogSession.MatchClass = outSession.Match(True)
                    If match IsNot Nothing Then
                        Dim rcvd As LogFieldElement = flds(AdifTags.ADIF_QSL_RCVD)
                        outSession.SetFieldText(AdifTags.ADIF_QSL_RCVD, rcvd.Data)
                        Tracing.TraceLine("LOTWMerge record with fields:" & callsign.Data & " " & dat.Data & " " & tim.Data & ", QSL_RCVD:" & rcvd.Data, TraceLevel.Info)
                        outSession.Update()
                        outCount += 1
                    Else
                        writeMergeLog("LOTWMerge record not found in log:" & callsign.Data & " " & dat.Data & " " & tim.Data)
                    End If
                End If
                If (inCount Mod 50) = 0 Then
                    MsgLib.TextOut.DisplayText(ProgressBox, ".", False)
                    Console.Beep()
                End If
            Loop

            MsgLib.TextOut.DisplayText(ProgressBox, String.Format(mergComplete, inCount, outCount), False, True)
        Catch ex As Exception
            Tracing.ErrMessageTrace(ex)
        End Try

        ' Now we can close the form.
        If cancelling Then
            RaiseEvent formEndEvent(System.Windows.Forms.DialogResult.Cancel)
        Else
            RaiseEvent formEndEvent(System.Windows.Forms.DialogResult.OK)
        End If
    End Sub

    Private Sub writeMergeLog(txt As String)
        If mergeLogStream Is Nothing Then
            mergeLogStream = New StreamWriter(mergeLogname)
        End If
        mergeLogStream.WriteLine(txt)
        Tracing.TraceLine(txt, TraceLevel.Error)
    End Sub

    Private Sub CnclButton_Click(sender As System.Object, e As System.EventArgs) Handles CnclButton.Click
        Tracing.TraceLine("LOTWMerge cancel:", TraceLevel.Info)
        If (mergeThread IsNot Nothing) AndAlso mergeThread.IsAlive Then
            ' wait for thread to close and show status.
            cancelling = True
            DialogResult = System.Windows.Forms.DialogResult.None
        End If
    End Sub

    Private Sub LOTWMerge_FormClosing(sender As System.Object, e As System.Windows.Forms.FormClosingEventArgs) Handles MyBase.FormClosing
        Tracing.TraceLine("LOTWMerge closing", TraceLevel.Info)
        If inSession IsNot Nothing Then
            inSession.Close()
        End If
        If outSession IsNot Nothing Then
            outSession.EndSession()
        End If
        If mergeLogStream IsNot Nothing Then
            mergeLogStream.Close()
        End If
    End Sub

    Private Sub formEndHandler(rv As DialogResult)
        Tracing.TraceLine("formEndHandler:" & rv, TraceLevel.Info)
        DialogResult = System.Windows.Forms.DialogResult.None
        CnclButton.Text = "Done"
        ProgressBox.Focus()
    End Sub
End Class