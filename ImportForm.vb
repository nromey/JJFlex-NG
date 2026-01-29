Imports System.DateTime
Imports System.IO
Imports adif
Imports JJLogLib
Imports JJTrace

Public Class ImportForm
    Dim impFile As FileStream
    Dim rfs As BinaryReader
    Dim session As LogSession
    Dim tempFile As String
    Dim EOF As Boolean
    Dim IOError As Boolean

    Const tagChar As Char = "<"
    Const tagEnd As Char = ">"
    Const tagIntra As Char = ":"c
    Const badHeader As String = "Probable malformed ADIF header."

    Private Function newExtention(ByVal fn As String, ByVal ext As String) As String
        ' Return the file name with the new extention.
        ' ext should not contain the period.
        ' First get the name w/o the extention.
        Dim i As Integer = fn.LastIndexOf("."c)
        If i <> -1 Then
            fn = fn.Substring(0, i)
        End If
        fn &= "." & ext
        Return fn
    End Function

    Private Sub ImportForm_Load(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles MyBase.Load
        Tracing.TraceLine("import_load", TraceLevel.Info)
        ImportingLabel.Enabled = False
        ImportingLabel.Visible = False
        DialogResult = DialogResult.None
        impFile = Nothing
        If ContactLog.Name = vbNullString Then
            MsgBox(mustHaveLog)
            Tracing.TraceLine("import_load:no log file", TraceLevel.Error)
            DialogResult = System.Windows.Forms.DialogResult.Abort
            Return
        End If
        ' Cleanup all ContactLog sessions.
        If Not ContactLog.Cleanup Then
            ' Cleanup not allowed.
            DialogResult = System.Windows.Forms.DialogResult.Abort
            Return
        End If
        ' Get the input file.
        With OpenFileDialog1
            .AddExtension = True
            .DefaultExt = "ADI"
            .Filter = "ADIF file (*.ADI)|*.ADI|Text file (*.TXT)|*.TXT"
            .CheckFileExists = True
            .CheckPathExists = True
            .FileName = newExtention(CurrentOp.LogFile, "ADI")
            .FileName = .SafeFileName
            Dim di As DirectoryInfo
            di = Directory.GetParent(CurrentOp.LogFile)
            .InitialDirectory = di.FullName
            .Title = "Input Filename"
            .ValidateNames = True
            If .ShowDialog = DialogResult.OK Then
                Try
                    impFile = New FileStream(.FileName, FileMode.Open)
                    rfs = New BinaryReader(impFile)
                Catch ex As IOException
                    Tracing.ErrMessageTrace(ex)
                    DialogResult = DialogResult.Abort
                    Return
                End Try
            End If
            FromName.Text = .FileName
            ' The current logfile is the output.  Import to a temporary first though.
            ToName.Text = CurrentOp.LogFile
        End With
    End Sub

    Private Sub OkButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles OkButton.Click
        ImportingLabel.Visible = True
        ImportingLabel.Enabled = True
        ImportingLabel.Focus()
        ' Import to a temp file first.
        ' Note this will originally use the default log, see import().
        tempFile = My.Computer.FileSystem.GetTempFileName
        Tracing.TraceLine("import tempfile:" & tempFile, TraceLevel.Info)
        session = New LogSession(tempFile)
        If Not session.Start Then
            DialogResult = DialogResult.Abort
            Return
        End If

        ' Do the import.
        Dim importOK As Boolean = import()
        closeFiles()

        If importOK Then
            DialogResult = DialogResult.OK
            ' Replace the log with the temp file.
            Try
                Tracing.TraceLine("import replacing:" & CurrentOp.LogFile, TraceLevel.Info)
                File.Delete(CurrentOp.LogFile)
                File.Move(tempFile, CurrentOp.LogFile)
                ' config the log.
                ConfigContactLog()
            Catch ex As Exception
                Tracing.ErrMessageTrace(ex)
                DialogResult = DialogResult.Abort
            End Try
        Else
            Tracing.TraceLine("import failed", TraceLevel.Error)
            DialogResult = DialogResult.Abort
            If File.Exists(tempFile) Then
                Try
                    File.Delete(tempFile)
                Catch ex As Exception
                    ' Ignore.
                End Try
            End If
        End If
    End Sub

    Private Sub CnclButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles CnclButton.Click
        ' Close impFile if needed.  impLog won't be open.
        closeFiles()
        DialogResult = DialogResult.Cancel
    End Sub

    Private Sub closeFiles()
        If impFile IsNot Nothing Then
            Try
                impFile.Close()
                impFile.Dispose()
            Catch ex As Exception
                Tracing.ErrMessageTrace(ex)
            End Try
        End If
        If session IsNot Nothing Then
            session.EndSession()
        End If
    End Sub

    Enum states
        initial
        findTagEnd
        inTag
    End Enum
    Private Function import() As Boolean
        Tracing.TraceLine("import started", TraceLevel.Info)
        ' Returns false on an I/O error.
        ' Create the tag names array.
        Dim state As states = states.initial
        Dim tag As String = ""
        Dim sanity As Long = 0
        EOF = False
        IOError = False
        While Not (EOF Or IOError)
            ' Sanity check.
            If sanity > impFile.Length Then
                ShowInternalError(ImportHangup)
                Return False
            End If
            sanity = sanity + 1 ' note that sanity is not an input character count
            Select Case state
                Case states.initial
                    ' Looking for a tag.
                    If read(1) = tagChar Then
                        ' This might be a tag.
                        state = states.findTagEnd
                        tag = ""
                    End If
                Case states.findTagEnd
                    ' Looking for a tag terminator.
                    Dim c As String = read(1)
                    If c = tagChar Then
                        ' Oops, tag begin char found.
                        tag = ""
                    ElseIf c = tagEnd Then
                        ' Found "<tag>".
                        state = states.inTag
                    Else
                        ' Add this to the tag.
                        tag &= c
                    End If
                Case states.inTag
                    ' Have a potential tag.
                    Dim fld As LogFieldElement = doTag(tag)
                    If fld Is Nothing Then
                        If IOError Then
                            Exit While
                        End If
                        ' We don't handle this tag, or it's bogus.  Start over.
                    Else
                        ' We handle this one.
                        ' Check for record end
                        If fld.ADIFTag = AdifTags.ADIF_HeaderEnd Then
                            If Not session.UpdateLogHeader() Then
                                IOError = True
                                Exit While
                            End If
                            ' Now restart the session with the new header.
                            session.EndSession()
                            session = New LogSession(tempFile)
                            If Not session.Start() Then
                                MsgBox(badHeader)
                                IOError = True
                                Exit While
                            End If
                        ElseIf fld.ADIFTag = AdifTags.ADIF_RecordEnd Then
                            ' See if we need to tack on the record version.
                            If (session.FormData.RecordVersion <> vbNullString) Then
                                session.SetFieldText(AdifTags.iADIF_RecordVersion, session.FormData.RecordVersion)
                            End If
                            If Not session.Append Then
                                IOError = True
                                Exit While
                            End If
                            session.Clear()
                        End If
                    End If
                    state = states.initial
            End Select
        End While
        Return Not IOError
    End Function

    Private Function doTag(ByVal tag As String) As LogFieldElement
        ' tag should have the form ADIFTag:Length[:type].
        ' The ADIFTag can be mixed case.
        ' Only specified ADIF tags are processed along with EOH and EOR.
        ' The file must be positioned immediately following the tagEndChar.
        Dim rv As LogFieldElement = Nothing
        ' Check for an end tag.
        Dim tagText As String = tag.ToUpper
        If (tagText = AdifTags.ADIF_HeaderEnd) Or (tagText = AdifTags.ADIF_RecordEnd) Then
            rv = New LogFieldElement(tagText)
            Return rv
        End If

        Dim tagTextLen As Integer = tag.IndexOf(tagIntra)
        If (tagTextLen = -1) Or (tagTextLen = tag.Length - 1) Then
            ' Covers the case where tag = "".
            Return rv
        End If
        ' Get the length.  We ignore the type for now.
        Dim tagLenLen As Integer = tag.Substring(tagTextLen + 1).IndexOf(tagIntra)
        If tagLenLen = -1 Then
            tagLenLen = tag.Length - tagTextLen - 1
        End If
        Dim tagContentLen As Integer
        Try
            tagContentLen = tag.Substring(tagTextLen + 1, tagLenLen)
        Catch ex As Exception
            ' Not numeric
            Return rv
        End Try
        ' Looks like an ADIF tag.
        ' LogEntry.ADIFTags are uppercase.
        tagText = tag.Substring(0, tagTextLen).ToUpper

        ' Get the associated LogFieldElement from the session.
        ' We treat the LOTW_QSL and EQSL_QSL tags as QSL tags.
        If (tagTextLen > 8) AndAlso _
           ((tagText.Substring(0, 8) = "LOTW_QSL") Or (tagText.Substring(0, 8) = "EQSL_QSL")) Then
            tagText = tagText.Substring(4)
        End If
        rv = getTag(tagText)
        If rv IsNot Nothing Then
            ' A valid tag we handle.
            rv.Data = read(tagContentLen)
            If rv.Data Is Nothing Then
                rv = Nothing
            Else
                Select Case rv.ADIFTag
                    Case AdifTags.ADIF_RXFreq, AdifTags.ADIF_TXFreq
                        ' Show the frequencies as mhz.khz.hz.
                        rv.Data = FormatFreq(FormatFreqForRadio(rv.Data))
                    Case AdifTags.ADIF_DateOn, AdifTags.ADIF_DateOff
                        If rv.Data.Length = 8 Then
                            Dim str As String = rv.Data.Substring(4, 2) & "/" & _
                                  rv.Data.Substring(6, 2) & "/" & rv.Data.Substring(0, 4)
                            rv.Data = str
                        End If
                    Case AdifTags.HDR_LogHeaderVersion
                        ' The log header will be at the current version.
                        rv.Data = LogClass.CurrentHeaderVersion
                    Case Else
                        ' See if special processing required.
                        Dim fldType As AdifTags.ADIFTypeField = Nothing
                        If AdifTags.ADIFTypeDictionary.TryGetValue(tagText, fldType) AndAlso _
                           (fldType.type = AdifTags.ADIFTypeInternal) Then
                            rv.Data = fldType.ADIFToInternal(rv.Data)
                        End If
                End Select
            End If
        End If
        Return rv
    End Function

    Private Function getTag(tag As String) As LogFieldElement
        Dim rv As LogFieldElement
        rv = session.getField(tag, False, session.HeaderDictionary)
        If rv Is Nothing Then
            rv = session.getField(tag, False, session.FieldDictionary)
        End If
        Return rv
    End Function

    Private Function read(ByVal len As Integer) As String
        ' This can set EOF and IOError.
        If (impFile.Position + len) > impFile.Length Then
            EOF = True
            Return Nothing
        End If
        Dim buf(len - 1) As Char
        Try
            buf = rfs.ReadChars(len)
        Catch ex As Exception
            Tracing.ErrMessageTrace(ex)
            IOError = True
            Return Nothing
        End Try
        Return CStr(buf)
    End Function
End Class