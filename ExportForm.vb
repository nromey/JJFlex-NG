Imports System.IO
Imports adif
Imports JJLogLib
Imports JJTrace

Public Class ExportForm
    Dim expFile As StreamWriter
    Const overwritePrefix As String = "Overwrite file "
    Const overwriteSuffix As String = "?"
    Private session As LogSession

    Private Sub ExportForm_Load(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles MyBase.Load
        DialogResult = System.Windows.Forms.DialogResult.None
        If ContactLog.Name = vbNullString Then
            MsgBox(mustHaveLog)
            Tracing.TraceLine("import_load:no log file", TraceLevel.Error)
            DialogResult = System.Windows.Forms.DialogResult.Abort
            Return
        End If
        session = New LogSession(ContactLog)
        If Not session.Start Then
            DialogResult = System.Windows.Forms.DialogResult.Abort
            session = Nothing
            GoTo checkExit
        End If
        ExportingLabel.Enabled = False
        ExportingLabel.Visible = False
        FromName.Text = CurrentOp.LogFile
        ' Get the output file.
        With OpenFileDialog1
            .AddExtension = True
            .DefaultExt = "ADI"
            .Filter = "ADIF file (*.ADI)|*.ADI|Text file (*.TXT)|*.TXT"
            .CheckFileExists = False
            .CheckPathExists = True
            .FileName = CurrentOp.LogFile
            Dim fn As String = .SafeFileName()
            ' Get the name w/o the extention.
            For i As Integer = fn.Length - 1 To 1 Step -1
                If fn(i) = "."c Then
                    fn = fn.Substring(0, i)
                    Exit For
                End If
            Next
            .FileName = fn & "." & .DefaultExt
            Dim di As DirectoryInfo
            di = Directory.GetParent(CurrentOp.LogFile)
            .InitialDirectory = di.FullName
            .Title = "Output Filename"
            .ValidateNames = True
            DialogResult = .ShowDialog
            If DialogResult = DialogResult.OK Then
                If File.Exists(.FileName) Then
                    If MessageBox.Show(overwritePrefix & .SafeFileName & overwriteSuffix, overwritePrefix, MessageBoxButtons.YesNo) = DialogResult.No Then
                        DialogResult = DialogResult.Abort
                        GoTo checkExit
                    End If
                End If
                ' Either the file doesn't exist, or user will overwrite.
                Try
                    expFile = New StreamWriter(.FileName)
                Catch ex As IOException
                    Tracing.ErrMessageTrace(ex)
                    DialogResult = DialogResult.Abort
                    GoTo checkExit
                End Try
                ToName.Text = .FileName
                DialogResult = DialogResult.None
            End If
        End With
checkExit:
        If DialogResult <> System.Windows.Forms.DialogResult.None Then
            Return
        End If
    End Sub

    Private Sub OkButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles OkButton.Click
        ExportingLabel.Enabled = True
        ExportingLabel.Visible = True
        ExportingLabel.Focus()
        Dim ADIFstr As String
        ' There's always a header.
        ADIFstr = headerToADIF()
        If ADIFstr IsNot Nothing Then
            Try
                expFile.Write(ADIFstr)
            Catch ex As IOException
                Tracing.ErrMessageTrace(ex)
                DialogResult = System.Windows.Forms.DialogResult.Abort
                Return
            End Try
        End If
        While Not session.EOF AndAlso session.NextRecord
            ADIFstr = LogrecToADIF()
            If ADIFstr IsNot Nothing Then
                Try
                    expFile.Write(ADIFstr)
                Catch ex As IOException
                    Tracing.ErrMessageTrace(ex)
                    Exit While
                End Try
            End If
        End While
        Try
            expFile.Close()
            expFile.Dispose()
        Catch ex As IOException
            Tracing.ErrMessageTrace(ex)
        End Try
        DialogResult = DialogResult.OK
    End Sub

    Private Sub CnclButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles CnclButton.Click
        Try
            expFile.Close()
            expFile.Dispose()
        Catch ex As Exception
            ' Ignore exception.
        End Try
        DialogResult = DialogResult.Cancel
    End Sub

    Const headerHeader As String = "Log for call sign "
    Const headerProgram As String = "  program "
    Const headerProgramVersion As String = " version "
    Const headerLogVersion As String = "  Log version "
    Const headerOpName As String = "  Operator name "
    Const headerOpQTH As String = "  Operator QTH "
    Const headerLogDuptype As String = "  Log dup type "
    Const headerFileName As String = "  Log file name "
    Const headerFormName As String = "  Log form name "
    Const headerEndtag As String = "<EOH>"
    Private Function headerToADIF() As String
        Dim outStr As String = ""
        outStr &= headerHeader & CurrentOp.callSign & vbCrLf
        outStr &= headerProgram & myAssemblyName.Name & _
            headerProgramVersion & myAssemblyName.Version.ToString & vbCrLf
        Dim str As String = ""
        If session.FormData.getScreenText(AdifTags.iADIF_RecordVersion, str) Then
            outStr &= headerLogVersion & str & vbCrLf
        End If
        outStr &= headerOpName & CurrentOp.fullName & vbCrLf
        outStr &= headerOpQTH & CurrentOp.qth & vbCrLf
        outStr &= headerLogDuptype & _
            CType(session.GetHeaderFieldText(AdifTags.HDR_DupCheck),  _
                LogDupChecking.DupTypes).ToString & vbCrLf
        outStr &= headerFileName & ContactLog.Name & vbCrLf
        outStr &= headerFormName & _
            session.GetHeaderFieldText(AdifTags.HDR_FormNAME) & vbCrLf
        ' Output the header ADIF-tagged data.
        outStr &= "  "
        ' First output reserved tags.
        For Each item As LogClass.HeaderReservedTagsType In LogClass.HeaderReservedTags
            outStr &= BuildADIFTag(item.Tag, item.value)
        Next
        For Each fld As LogFieldElement In session.HeaderDictionary.Values
            outStr &= BuildADIFTag(fld.ADIFTag, fld.Data)
        Next
        outStr &= headerEndtag & vbCrLf
        Return outStr
    End Function

    Private Function LogrecToADIF() As String
        ' Produce an ADIF string from this LogRecord.
        ' It must have at least a Date, Time and a Call.
        ' Return Nothing on error.
        Dim outStr As String = vbNullString
        If (session.GetFieldText(AdifTags.ADIF_DateOn) = vbNullString) Or _
           (session.GetFieldText(AdifTags.ADIF_TimeOn) = vbNullString) Or _
           (session.GetFieldText(AdifTags.ADIF_Call) = vbNullString) Then
            Return outStr
        End If
        ' Output each logged field, except for internal fields.
        ' Note that session.FieldDictionary contains only logged fields.
        For Each fld As LogFieldElement In session.FieldDictionary.Values
            If fld.ADIFTag(0) <> "$"c Then
                ' Not internal field
                ' Preprocessing
                Select Case fld.ADIFTag
                    Case AdifTags.ADIF_RXFreq, AdifTags.ADIF_TXFreq
                        ' Frequencies' formats are a little different.
                        fld.Data = formatADIFFreq(fld.Data)
                    Case AdifTags.ADIF_DateOn, AdifTags.ADIF_DateOff
                        Try
                            ' Change to yyyymmdd
                            Dim str As String = fld.Data.Substring(6, 4) & _
                                fld.Data.Substring(0, 2) & fld.Data.Substring(3, 2)
                            fld.Data = str
                        Catch ex As Exception
                            ' don't reformat
                        End Try
                    Case AdifTags.ADIF_Mode
                        ' Do any JJFlexRadio-related data formatting.
                        Dim arg As String = fld.Data.ToUpper
                        Select Case arg
                            Case "LSB", "USB"
                                fld.Data = "SSB"
                            Case "CWR"
                                fld.Data = "CW"
                            Case "FSK", "FSKR"
                                fld.Data = "RTTY"
                        End Select
                End Select
                outStr &= BuildADIFTag(fld.ADIFTag, fld.Data)
            End If
        Next
        ' Return nothing if nothing built.
        If outStr <> vbNullString Then
            ' Add the end-of-record tag.
            outStr &= "<" & AdifTags.ADIF_RecordEnd & ">" & vbCrLf
        End If
        Return outStr
    End Function

    Private Function BuildADIFTag(ByVal ADIFStr As String, ByVal Val As String) As String
        ' Build the ADIF tag, <ADIFstr:length[:type]>val
        Dim outStr As String = Nothing
        If Val <> "" Then
            outStr = "<" & ADIFStr
            Dim outTypeStr As String = ""
            Dim typefld As AdifTags.ADIFTypeField = Nothing
            ' Add any type or reformat.
            If AdifTags.ADIFTypeDictionary.TryGetValue(ADIFStr, typefld) Then
                If (typefld.type = AdifTags.ADIFTypeInternal) Then
                    ' reformat the data.
                    Val = typefld.InternalToADIF(Val)
                Else
                    ' Add the type to the tag
                    outTypeStr = ":" & typefld.type
                End If
            End If
            outStr &= ":" & Val.Length & outTypeStr & ">" & Val & " "
        End If
        Return outStr
    End Function

    Private Function formatADIFFreq(ByVal freq As String) As String
        ' Get the frequency in MHZ.
        Dim freqStr As String = FormatFreqForRadio(freq)
        If freqStr IsNot Nothing Then
            freqStr = freqStr.TrimStart({"0"c})
            While freqStr.Length < KHZSIZE
                freqStr = freqStr.Insert(0, "0")
            End While
            freqStr = freqStr.Insert(freqStr.Length - KHZSIZE, ".")
            If (freqStr(0) = ".") Then
                freqStr.Insert(0, "0") ' add leading 0 if needed
            End If
        End If
        Return freqStr
    End Function

    Private Sub ExportForm_FormClosing(sender As System.Object, e As System.Windows.Forms.FormClosingEventArgs) Handles MyBase.FormClosing
        If session IsNot Nothing Then
            session.EndSession()
            session = Nothing
        End If
    End Sub
End Class