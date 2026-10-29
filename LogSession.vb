Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.Text.RegularExpressions
Imports adif
Imports HamBands
Imports JJLogLib
Imports JJTrace

Public Class LogSession
    Const badHeader As String = "Invalid log header."
    Const BadArg As String = "Bad search argument: "
    Const noFields As String = "No search arguments given."
    Const fieldCountMismatch As String = "Log record field count mismatch."
    Const updateFailed As String = "Update failure; record may have already been updated."
    Private id As Integer ' session's id.
    Friend theLog As LogClass ' associated log file.
    Friend FormData As Logs.LogElement ' Log form instance
    Friend ShowStats As Logs.ShowStatsDel
    Friend ReadOnly Property NeedFrequencyFix As Boolean
        Get
            Return theLog.InitialVersion <= LogClass.HeaderVersion2
        End Get
    End Property
    Private usingForm As Boolean = True ' Always using a form for now.
    Private headerPosition As Long
    Private firstRecordPosition As Long
    Private currentPosition As Long
    Private justRead As Long
    ''' <summary>
    ''' position of the record we just read.
    ''' </summary>
    Friend ReadOnly Property JustReadPosition As Long
        Get
            Return justRead
        End Get
    End Property

    Friend HeaderDictionary As Dictionary(Of String, LogFieldElement)
    Friend FieldDictionary As Dictionary(Of String, LogFieldElement)

    ''' <summary>
    ''' Number of log record fields.
    ''' </summary>
    ''' <returns>integer</returns>
    Public ReadOnly Property Length As Integer
        Get
            Return FieldDictionary.Values.Count
        End Get
    End Property

    Friend Function getField(adif As String, required As Boolean, _
                                   dict As Dictionary(Of String, LogFieldElement)) As LogFieldElement
        Dim rv As LogFieldElement = Nothing
        If Not dict.TryGetValue(adif, rv) Then
            rv = Nothing
            If required Then
                MsgBox(InternalError & SessionADIFNotFound)
            End If
        End If
        Return rv
    End Function

    Friend Function GetFieldText(adif As String) As String
        Return GetFieldText(adif, True)
    End Function
    Friend Function GetFieldText(adif As String, required As Boolean) As String
        Return GetFieldText(adif, required, FieldDictionary)
    End Function
    ''' <summary>
    ''' (Overloaded) Get a field from the session
    ''' </summary>
    ''' <param name="adif">the ADIF tag</param>
    ''' <param name="required">true or false</param>
    ''' <param name="dict">dictionary to use</param>
    ''' <returns>the string value</returns>
    Friend Function GetFieldText(adif As String, required As Boolean, _
                             dict As Dictionary(Of String, LogFieldElement)) As String
        Dim fld As LogFieldElement = getField(adif, required, dict)
        Dim rv As String
        If fld Is Nothing Then
            rv = ""
        Else
            rv = fld.Data
        End If
        Return rv
    End Function

    ''' <summary>
    ''' Get a field from the header.
    ''' </summary>
    ''' <param name="adif">ADIF tag</param>
    ''' <returns>string data</returns>
    Friend Function GetHeaderFieldText(adif As String) As String
        Return GetFieldText(adif, True, HeaderDictionary)
    End Function

    ''' <summary>
    ''' (overloaded) Set the specified session field.
    ''' </summary>
    ''' <param name="adif">the ADIF tag</param>
    ''' <param name="val">the string value</param>
    ''' <param name="required">true or false</param>
    ''' <param name="dict">field dictionary</param>
    Friend Sub SetFieldText(adif As String, val As String, _
                        required As Boolean, _
                        dict As Dictionary(Of String, LogFieldElement))
        Dim fld As LogFieldElement = getField(adif, required, dict)
        If fld IsNot Nothing Then
            fld.Data = val
            ' Special case for setting the band.
            If (val <> "") And (adif = "FREQ") Then
                Dim item As Bands.BandItem
                Dim bandFld As LogFieldElement = getField(AdifTags.ADIF_Band, False, dict)
                If bandFld IsNot Nothing Then
                    item = Bands.Query(FreqInt64(val))
                    bandFld.Data = item.Name
                End If
            End If
        End If
    End Sub
    Friend Sub SetFieldText(adif As String, val As String)
        SetFieldText(adif, val, True)
    End Sub
    Friend Sub SetFieldText(adif As String, val As String, _
                        required As Boolean)
        SetFieldText(adif, val, required, FieldDictionary)
    End Sub

    Friend Sub setHeaderFieldText(adif As String, val As String)
        SetFieldText(adif, val, True, HeaderDictionary)
    End Sub

    ''' <summary>
    ''' Current serial number for a new record.
    ''' </summary>
    Public serial As Integer

    Const charsToEscape As String = LogClass.escChar & LogClass.FLDSEP
    ''' <summary>
    ''' Add escape characters if needed.
    ''' </summary>
    ''' <param name="str">input string</param>
    ''' <returns>output string</returns>
    Private Shared Function prepareText(ByVal str As String) As String
        ' Careful of a true null string.
        If str = vbNullString Then
            Return vbNullString
        End If
        Dim i As Integer = 0
        While i < str.Length
            If charsToEscape.IndexOf(str(i)) > -1 Then
                str = str.Insert(i, LogClass.escChar)
                i += 1
            End If
            i += 1
        End While
        Return str
    End Function

    ''' <summary>
    ''' Set the log record fields to null.
    ''' </summary>
    Public Sub Clear()
        Tracing.TraceLine("session clear", TraceLevel.Info)
        For Each fld As LogFieldElement In FieldDictionary.Values
            fld.Data = ""
        Next
    End Sub

    ''' <summary>
    ''' Split a log file record into fields.
    ''' Unescape escaped characters.
    ''' </summary>
    ''' <param name="Str">Input string</param>
    ''' <returns>Output string array</returns>
    Private Shared Function Split(ByVal Str As String) As List(Of String)
        ' Split the string with colon delimiters and escaped colons.
        Dim flds = New List(Of String)
        Dim str2 As String = vbNullString
        Dim lastPos As Integer = Str.Length - 1
        For i As Integer = 0 To lastPos
            If Str(i) = LogClass.escChar Then
                ' escape, use the following character.
                Try
                    str2 &= Str(i + 1)
                    i += 1
                Catch ex As Exception
                    ShowInternalError(MySplitERR)
                End Try
            ElseIf Str(i) = LogClass.FLDSEP Then
                ' field separator
                flds.Add(str2)
                str2 = vbNullString
            Else
                ' regular character
                str2 &= Str(i)
            End If
        Next
        ' Add the last field.
        flds.Add(str2)
        Tracing.TraceLine("log split:" & Str & " returning " & flds.Count.ToString & " fields", TraceLevel.Verbose)
        Return flds
    End Function

    ''' <summary>
    ''' Extract fields from a record.
    ''' </summary>
    ''' <param name="Str">Input string</param>
    Private Sub extractFields(ByVal Str As String)
        Tracing.TraceLine("extractFields:" & Str, TraceLevel.Verbose)
        ' Perform any needed data conversion.
        If FormData.RecordConverter IsNot Nothing Then
            Str = FormData.RecordConverter(Str)
        End If
        Dim flds As List(Of String) = Split(Str)
        Dim nFields As Integer = Math.Min(flds.Count, FieldDictionary.Count)
        For i As Integer = 0 To FieldDictionary.Count - 1
            If (i >= nFields) OrElse (flds(i) = vbNullString) Then
                FieldDictionary.Values(i).Data = ""
            Else
                FieldDictionary.Values(i).Data = flds(i)
            End If
        Next
    End Sub

    Private Sub extractHeaderFields(str As String)
        Tracing.TraceLine("extractHeaderFields:" & str, TraceLevel.Info)
        Dim flds As List(Of String) = Split(str)
        Dim hdrVersion As String = flds(0)
        If theLog.InitialVersion = vbNullString Then
            theLog.InitialVersion = hdrVersion
        End If
        ' Always update to the current version.
        flds(0) = LogClass.CurrentHeaderVersion
        For i As Integer = 0 To Math.Min(LogClass.HeaderADIFTags.Length, flds.Count) - 1
            Dim item As LogFieldElement = Nothing
            If HeaderDictionary.TryGetValue(LogClass.HeaderADIFTags(i), item) Then
                item.Data = flds(i)
            Else
                MsgBox(badHeader)
                err = True
                Return
            End If
        Next
        If (flds.Count = HeaderDictionary.Count) AndAlso _
           (hdrVersion = LogClass.CurrentHeaderVersion) Then
            ' no conversion needed
        ElseIf ((flds.Count = LogClass.HeaderVersion2NFields) AndAlso _
                (hdrVersion = LogClass.HeaderVersion2)) Or
               ((flds.Count = LogClass.HeaderVersion3NFields) AndAlso _
                (hdrVersion = LogClass.HeaderVersion3)) Then
            ' conversion needed from version 2 or 3.
            setHeaderFieldText(AdifTags.HDR_CallLookup, CType(Logs.DefaultLookupChoice, Integer))
        ElseIf (flds.Count = LogClass.HeaderVersion1NFields) AndAlso _
               (hdrVersion = LogClass.HeaderVersion1) Then
            setHeaderFieldText(AdifTags.HDR_FormNAME, Logs.DefaultLogname)
        Else
            ShowInternalError(LogHeaderVersionError)
            err = True
            Return
        End If
    End Sub

    Private Function fields2Header() As String
        Dim str As String = vbNullString
        For i As Integer = 0 To LogClass.HeaderADIFTags.Length - 1
            If i > 0 Then
                str &= LogClass.FLDSEP
            End If
            ' PrepareText() may add escape sequences.
            str &= prepareText(HeaderDictionary(LogClass.HeaderADIFTags(i)).Data)
        Next
        Tracing.TraceLine("fields2Header output:" & str, TraceLevel.Verbose)
        Return str
    End Function

    ''' <summary>
    ''' Update the log header.
    ''' </summary>
    ''' <returns>true on success</returns>
    Public Function UpdateLogHeader() As Boolean
        Return UpdateLogHeader(False)
    End Function
    Private Function UpdateLogHeader(newLog As Boolean) As Boolean
        Tracing.TraceLine("UpdateLogHeader:" & newLog.ToString, TraceLevel.Info)
        err = False
        Dim str As String = fields2Header()
        theLog.Lock()
        Try
            If newLog Then
                theLog.Append(str)
            Else
                theLog.SeekToFirst()
                theLog.Update(str)
            End If
        Catch ex As Exception
            theLog.Unlock()
            err = True
            Tracing.ErrMessageTrace(ex)
        End Try
        If Not err Then
            Try
                currentPosition = theLog.SeekToNext
            Catch ex As Exception
                ' Ignore it, probably a new log file.
            End Try
            ef = theLog.EOF
            theLog.Unlock()
        End If
        Return Not err
    End Function
    ''' <summary>
    ''' Every field of a new log's header, one per LogClass.HeaderADIFTags entry.
    ''' </summary>
    ''' <remarks>
    ''' #814 repair, found by the Codex check. Until round one a new log was
    ''' always made through Log Characteristics, whose OK handler wrote every
    ''' field itself, so the missing HDR_CallLookup default never showed. The
    ''' no-log-file choice creates a log directly, and an empty lookup field
    ''' made the full log form throw (setupForm compares it as a number). The
    ''' values are what Log Characteristics gives a new log: the default form,
    ''' no dup checking, serial 1, and no call lookup.
    ''' </remarks>
    Private Sub setHeaderDefaults()
        setHeaderFieldText(AdifTags.HDR_LogHeaderVersion, LogClass.CurrentHeaderVersion)
        setHeaderFieldText(AdifTags.HDR_FormNAME, Logs.DefaultLogname)
        setHeaderFieldText(AdifTags.HDR_DupCheck, CStr(CInt(LogDupChecking.DupTypes.none)))
        setHeaderFieldText(AdifTags.HDR_StartingSerial, "1")
        setHeaderFieldText(AdifTags.HDR_CallLookup, CStr(CInt(Logs.DefaultLookupChoice)))
    End Sub

    Private err As Boolean
    ''' <summary>
    ''' Indicates I/O Error.
    ''' </summary>
    ''' <returns>true if error occurred.</returns>
    Public ReadOnly Property IOError As Boolean
        Get
            Return err
        End Get
    End Property
    Private ef As Boolean
    ''' <summary>
    ''' Indicate end-of-file
    ''' </summary>
    ''' <returns>True if end</returns>
    Public ReadOnly Property EOF As Boolean
        Get
            Return ef
        End Get
    End Property

    ''' <summary>
    ''' (overloaded) Create a log session.
    ''' </summary>
    ''' <param name="log">A LogClass object.</param>
    Public Sub New(ByVal log As LogClass)
        theLog = log
        setup()
    End Sub
    ''' <summary>
    ''' (overloaded) create a log session
    ''' </summary>
    ''' <param name="logName"></param>
    Public Sub New(logName As String)
        theLog = New LogClass(logName)
        setup()
    End Sub
    Private Sub setup()
        err = False
        ef = False
    End Sub

    ''' <summary>
    ''' Overloaded - Start the log session, opens the file on the first session.
    ''' This session may not use a form.
    ''' </summary>
    ''' <returns>true on success.</returns>
    ''' <remarks>The file is opened in LogClass.</remarks>
    Public Function Start() As Boolean
        Return Start(Nothing, Nothing)
    End Function
    ''' <summary>
    ''' Overloaded - Start the log session, opens the file on the first session.
    ''' </summary>
    ''' <param name="operater">set if log characteristics can be undefined</param>
    ''' <param name="cleanup">see LogClass.CleanupClass
    ''' The function must return true if complete, or false to reject the cleanup.
    ''' </param>
    ''' <returns>true on success.</returns>
    ''' <remarks>The file is opened in LogClass.</remarks>
    Public Function Start(operater As PersonalData.personal_v1, _
                          ByVal cleanup As LogClass.cleanupClass) _
            As Boolean
        Tracing.TraceLine("session start:", TraceLevel.Info)
        theLog.Lock()
        id = theLog.Open(operater, cleanup)
        err = (id = 0)
        If Not err Then
            ' Build the header dictionary.
            HeaderDictionary = New Dictionary(Of String, LogFieldElement)
            For Each Str As String In LogClass.HeaderADIFTags
                Dim fld = New LogFieldElement(Str)
                HeaderDictionary.Add(Str, fld)
            Next
            ' The first record is the header.
            If (id = 1) And theLog.EOF Then
                Tracing.TraceLine("  new log", TraceLevel.Info)
                ' new log file, build the header.
                setHeaderDefaults()
                UpdateLogHeader(True)
            Else
                Tracing.TraceLine("  existing log", TraceLevel.Info)
                currentPosition = theLog.SeekToFirst
                getNextRecord(AddressOf extractHeaderFields)
                If theLog.InitialVersion <> LogClass.CurrentHeaderVersion Then
                    ' Update to current version.
                    UpdateLogHeader()
                End If
            End If
            If Not err Then
                serial = CInt(GetHeaderFieldText(AdifTags.HDR_StartingSerial))
                ' Get the position of the header and first log record.
                headerPosition = theLog.SeekToFirst
                Try
                    firstRecordPosition = theLog.SeekToNext
                    ef = False ' not EOF if this worked.
                Catch ex As Exception
                    ' we're at EOF
                    firstRecordPosition = -1
                    ef = True
                End Try
            End If
            If Not err Then
                ' Header is complete.
                FormData = Logs.GetLog(GetHeaderFieldText(AdifTags.HDR_FormNAME), BaseConfigDir)
                ShowStats = Logs.ShowStats

                err = (FormData Is Nothing)
                If Not err Then
                    ' Setup the field dictionary mapping ADIF tag to logged fields.
                    FieldDictionary = New Dictionary(Of String, LogFieldElement)
                    For Each fld As LogField In FormData.Fields.Values
                        If fld.IsLogged Then
                            FieldDictionary.Add(fld.ADIFTag, _
                                                New LogFieldElement(fld.ADIFTag))
                        End If
                    Next
                    ' Get the last logged serial number if not at EOF.
                    ' Note that if at EOF, it was gotten from the header.
                    If Not ef Then
                        Try
                            theLog.SeekToLast()
                            currentPosition = theLog.Position
                            If NextRecord() AndAlso _
                               IsNumeric(GetFieldText(AdifTags.ADIF_SentSerial)) Then
                                serial = CInt(GetFieldText(AdifTags.ADIF_SentSerial)) + 1
                                ef = False ' won't stay at EOF.
                            End If
                        Catch ex As Exception
                            Tracing.ErrMessageTrace(ex)
                            err = True
                        End Try
                    End If
                    currentPosition = firstRecordPosition
                    Tracing.TraceLine("  header at:" & headerPosition.ToString & " " & _
                        "firstRecord:" & firstRecordPosition.ToString & " " & _
                        "serial:" & serial.ToString, TraceLevel.Info)
                End If
            End If
            justRead = -1
        End If
        theLog.Unlock()
        If err Then
            EndSession()
        Else
            Clear()
        End If
        Return Not err
    End Function

    ''' <summary>
    ''' End the current log session.
    ''' </summary>
    Public Sub EndSession()
        Tracing.TraceLine("endSession:" & id.ToString, TraceLevel.Info)
        If (FormData IsNot Nothing) And usingForm Then
            FormData.Close()
        End If
        theLog.Close(id)
    End Sub

    ''' <summary>
    ''' Seek to the first record.
    ''' </summary>
    ''' <returns>true on success</returns>
    Public Function SeekToFirst() As Boolean
        Tracing.TraceLine("SeekToFirst", TraceLevel.Info)
        err = False
        theLog.Lock()
        If firstRecordPosition = -1 Then
            ' there's no first record!
            ef = True
            Return False
        End If
        currentPosition = theLog.SeekToPosition(firstRecordPosition)
        ef = False ' we know we're not at EOF.
        theLog.Unlock()
        justRead = -1
        Return True
    End Function

    ''' <summary>
    ''' Seek to the last record.
    ''' </summary>
    ''' <returns>true on success</returns>
    Public Function SeekToLast() As Boolean
        Tracing.TraceLine("SeekToLast", TraceLevel.Info)
        err = False
        theLog.Lock()
        currentPosition = theLog.SeekToLast
        If currentPosition = headerPosition Then
            ef = True
        Else
            ef = theLog.EOF
        End If
        theLog.Unlock()
        justRead = -1
        Return True
    End Function

    ''' <summary>
    ''' Seek to the specified position.
    ''' </summary>
    ''' <param name="pos">position as long</param>
    ''' <returns>true on success.</returns>
    Public Function SeekToPosition(ByVal pos As Long) As Boolean
        Tracing.TraceLine("SeekToPosition:" & pos.ToString, TraceLevel.Info)
        Dim cPos As Long
        err = False
        theLog.Lock()
        Try
            cPos = theLog.SeekToPosition(pos)
        Catch ex As Exception
            theLog.Unlock()
            Tracing.ErrMessageTrace(ex)
            err = True
        End Try
        If Not err Then
            ef = theLog.EOF
            theLog.Unlock()
            currentPosition = cPos
        End If
        justRead = -1
        Return Not err
    End Function

    ''' <summary>
    ''' Get the current file position.
    ''' </summary>
    ''' <returns>file position</returns>
    Public Function Position() As Long
        Return theLog.Position
    End Function

    ''' <summary>
    ''' Read the current record and position to the next one.
    ''' </summary>
    ''' <returns>true on success</returns>
    Public Function NextRecord() As Boolean
        Return getNextRecord(AddressOf extractFields)
    End Function
    Private Delegate Sub setFields(str As String)
    ' Set the fields using the given subroutine.
    Private Function getNextRecord(setFunc As setFields) As Boolean
        Tracing.TraceLine("GetNextRecord:" & currentPosition.ToString & " " & ef.ToString, TraceLevel.Verbose)
        ' We're positioned after the just-read record.
        err = False
        Dim str As String = ""
        If Not ef Then
            theLog.Lock()
            Try
                theLog.SeekToPosition(currentPosition)
                str = theLog.Read
                justRead = currentPosition
                currentPosition = theLog.Position ' Might be bad if at EOF.
            Catch ex As Exception
                theLog.Unlock()
                Tracing.ErrMessageTrace(ex)
                err = True
            End Try
            ef = theLog.EOF
        Else
            ' At EOF.  The log wasn't locked.
            err = True
        End If
        If Not err Then
            theLog.Unlock()
            ' set the desired fields.  This can set err.
            setFunc(str)
        End If
        Return Not err
    End Function

    ''' <summary>
    ''' Read the previous record.
    ''' Positioned immediately following.
    ''' </summary>
    ''' <returns>true on success</returns>
    Public Function PreviousRecord() As Boolean
        ' We're positioned after the just-read record when finished.
        Tracing.TraceLine("PreviousRecord:" & currentPosition.ToString & " " & ef.ToString, TraceLevel.Info)
        Dim rv As Boolean = True
        Dim cPos As Long
        theLog.Lock()
        Try
            If ef Then
                cPos = theLog.SeekToLast()
                If cPos = justRead Then
                    cPos = theLog.SeekToPrevious()
                End If
                ' logically at EOF if this is the header.
                ef = (cPos = headerPosition)
            Else
                If justRead = -1 Then
                    ' We'll get the record at currentPosition.
                    cPos = currentPosition
                Else
                    theLog.SeekToPosition(justRead)
                    cPos = theLog.SeekToPrevious()
                End If
            End If
            currentPosition = cPos
        Catch ex As Exception
            rv = False
        End Try
        theLog.Unlock()
        ' We'll error out if ef is set, but careful of the header.
        If currentPosition = headerPosition Then
            currentPosition = firstRecordPosition
            If currentPosition <> -1 Then
                theLog.SeekToPosition(currentPosition)
                ' logically we're not at EOF.
                ef = False
            Else
                ' no first log record
                ef = True
                rv = False
            End If
        End If
        If rv Then
            rv = NextRecord()
        Else
            ' Get back to where we were.
            Try
                theLog.SeekToPosition(currentPosition)
            Catch ex As Exception
                ' Ignore.
            End Try
        End If
        err = Not rv
        Return rv
    End Function

    ''' <summary>
    ''' Return a file record from the fields.
    ''' </summary>
    ''' <returns>A record, string with colon-separated fields.</returns>
    Private Function fields2Record() As String
        Dim str As String = ""
        For i As Integer = 0 To Length - 1
            If i > 0 Then
                str &= LogClass.FLDSEP
            End If
            ' PrepareText() may add escape sequences.
            str &= prepareText(FieldDictionary.Values(i).Data)
        Next
        Tracing.TraceLine("fields2Record returning:" & str, TraceLevel.Verbose)
        Return str
    End Function

    ''' <summary>
    ''' Add the accumulated fields to the file.
    ''' The file is positioned at EOF.
    ''' </summary>
    ''' <returns>true on success</returns>
    Public Function Append() As Boolean
        Tracing.TraceLine("session append", TraceLevel.Info)
        err = False
        ef = False
        theLog.Lock()
        ' Only plug in a serial number if there isn't one.
        ' Should only happen on an import of an old file.
        ' Note the serial number isn't required.
        If GetFieldText(AdifTags.ADIF_SentSerial, False) = "" Then
            SetFieldText(AdifTags.ADIF_SentSerial, CStr(serial), False)
            serial += 1
        End If
        Try
            theLog.Append(fields2Record())
            ' The append positions us to the just-appended record, but we're logically at EOF..
            If firstRecordPosition = -1 Then
                ' hasn't been set yet.
                firstRecordPosition = theLog.Position
            End If
            justRead = -1
            ef = True
            currentPosition = -1
        Catch ex As Exception
            err = True
            Tracing.ErrMessageTrace(ex)
        Finally
            theLog.Unlock()
        End Try
        Return Not err
    End Function

    ''' <summary>
    ''' Update the current record from the fields.
    ''' The file is positioned at the next record.
    ''' </summary>
    ''' <returns>true on success</returns>
    Public Function Update() As Boolean
        Tracing.TraceLine("session update", TraceLevel.Info)
        err = False
        theLog.Lock()
        Try
            theLog.SeekToPosition(justRead)
        Catch ex As Exception
            theLog.Unlock()
            err = True
            Tracing.TraceLine(updateFailed, TraceLevel.Error)
            MsgBox(updateFailed)
        End Try
        If Not err Then
            Try
                theLog.Update(fields2Record())
                If justRead = firstRecordPosition Then
                    ' we need to reset this.
                    firstRecordPosition = theLog.Position
                End If
            Catch ex As Exception
                theLog.Unlock()
                Tracing.ErrMessageTrace(ex)
                err = True
            End Try
        End If
        If Not err Then
            ' Since the fields are setup, this record was logically justRead.
            justRead = theLog.Position
            Try
                currentPosition = theLog.SeekToNext
                ef = theLog.EOF
            Catch ex As Exception
                ' at EOF.
                ef = True
            End Try
            theLog.Unlock()
        End If
        Return Not err
    End Function

    ''' <summary>
    ''' True if the current record has data.
    ''' </summary>
    ''' <value></value>
    ''' <returns></returns>
    ''' <remarks></remarks>
    Friend ReadOnly Property HasData As Boolean
        Get
            For Each fld As LogFieldElement In FieldDictionary.Values
                If fld.Data <> vbNullString Then
                    Return True
                End If
            Next
            Return False
        End Get
    End Property

    ' Match data.
    Class matchData
        Friend field As LogFieldElement
        Friend arg As Regex
    End Class
    Private matchFields As List(Of matchData)

    ''' <summary>
    ''' Match data returned
    ''' </summary>
    Friend Class MatchClass
        Public Fields As Dictionary(Of String, LogFieldElement)
        Public Pos As Long
        Public Sub New(dict As Dictionary(Of String, LogFieldElement), _
                       p As Integer)
            Fields = New Dictionary(Of String, LogFieldElement)
            For Each fld As LogFieldElement In dict.Values
                ' Careful! We need a new LogFieldElement.
                Dim newFld = New LogFieldElement(fld.ADIFTag, fld.Data)
                Fields.Add(fld.ADIFTag, newFld)
                Pos = p
            Next
        End Sub
    End Class

    ''' <summary>
    ''' (overloaded) Record the log search argument.
    ''' </summary>
    Public Function SetSearchArg() As Boolean
        Dim args = New List(Of LogFieldElement)
        ' Find non-empty, non-reserved fields.
        For Each fld As LogFieldElement In FieldDictionary.Values
            If (fld.ADIFTag(0) <> "$") And (fld.Data <> "") Then
                args.Add(fld)
            End If
        Next
        Dim rv = (args.Count > 0)
        If rv Then
            rv = SetSearchArg(args)
        End If
        Return rv
    End Function

    ''' <summary>
    ''' (overloaded) Record the log search argument.
    ''' </summary>
    ''' <param name="args">arguments, list of LogFieldElement</param>
    Public Function SetSearchArg(args As List(Of LogFieldElement)) As Boolean
        Dim regexOpt As Integer = (RegexOptions.IgnoreCase Or RegexOptions.Compiled)
        Dim rv As Boolean = True
        matchFields = New List(Of matchData)
        For Each fld As LogFieldElement In args
            Dim argData = New matchData
            argData.field = fld
            Tracing.TraceLine("match field:" & fld.ADIFTag & " " & fld.Data, TraceLevel.Info)
            Try
                argData.arg = New Regex(fld.Data, regexOpt)
            Catch ex As Exception
                MsgBox(BadArg & fld.Data)
                rv = False
            End Try
            If rv Then
                matchFields.Add(argData)
            End If
        Next
        If matchFields.Count = 0 Then
            MsgBox(noFields)
            rv = False
        Else
            Tracing.TraceLine("SetSearchArg fields:" & matchFields.Count.ToString, TraceLevel.Info)
        End If
        Return rv
    End Function

    ''' <summary>
    ''' Find a matching log entry.
    ''' Read until one is found or EOF is reached, or an error occurs.
    ''' </summary>
    ''' <param name="startOver">True to start from beginning of the file</param>
    ''' <returns>an object of type MatchClass, or nothing if not found, EOF or error.</returns>
    Friend Function Match(startOver As Boolean) As MatchClass
        Dim rv As MatchClass = Nothing
        If startOver Then
            SeekToFirst()
        End If
        ' While not EOF and no match and next record successfully read
        While (Not EOF) AndAlso (rv Is Nothing) AndAlso NextRecord()
            If MatchThisRecord() Then
                rv = New MatchClass(FieldDictionary, justRead)
            End If
        End While
        Return rv
    End Function

    Friend Function Match() As MatchClass
        Return Match(False)
    End Function

    Private Function MatchThisRecord() As Boolean
        ' For each field to match
        For Each fld As matchData In matchFields
            ' Quit if one doesn't match
            If Not fld.arg.IsMatch(FieldDictionary(fld.field.ADIFTag).Data) Then
                Return False
            End If
        Next
        Return True
    End Function
End Class
