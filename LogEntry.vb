Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.IO
Imports System.Threading
Imports adif
Imports JJLogLib
Imports JJRadio.LogDupChecking
Imports JJTrace

''' <summary>
''' log entry form and data
''' </summary>
Friend Class LogEntry
    Const mustClose As String = "You must first close the Log Entry form."
    Const writeLogEntryMsg As String = "Write the log entry for "
    Const writeLogEntryTitleMsg As String = "Write Log Entry"
    Private session As LogSession = Nothing
    Private sessionLock = New Object
    Dim wasActive As Boolean ' set when form activated.
    Dim inDialog As Boolean = False ' Indicates the form was loaded.
    Dim oldCall As String
    Dim oldFields As Dictionary(Of String, LogFieldElement)

    Class myKeyCommands
        Inherits KeyCommands
        ' Main program keys handled here.
        Dim myKeys As Dictionary(Of KeyCommands.CommandValues, KeyCommands.CommandValues)
        ' The rest of the keys we'll handle are specified with the log forms.
        Dim parent As LogEntry
        Public Sub New(ByVal p As LogEntry)
            ' Don't default the command table.
            MyBase.New(False)
            ' Include macros

            myKeys = New Dictionary(Of KeyCommands.CommandValues, KeyCommands.CommandValues)
            myKeys.Add(KeyCommands.CommandValues.NewLogEntry, Nothing)
            myKeys.Add(KeyCommands.CommandValues.LogFinalize, Nothing)
            myKeys.Add(KeyCommands.CommandValues.SearchLog, Nothing)

            parent = p
            ' Set the keys for logging commands in my KeyTable.
            Dim dummy As KeyCommands.CommandValues = Nothing
            For Each entries In Commands.KeyDictionary.Values
                For Each item As KeyCommands.keyTbl In entries
                    If (item.KeyType = KeyCommands.KeyTypes.log) Or _
                       item.UseWhenLogging Or _
                       myKeys.TryGetValue(item.key.id, dummy) Then
                        Dim myItem = New KeyCommands.keyTbl(item)
                        ' Note if UseWhenLogging is set, use the original handler.
                        If Not myItem.UseWhenLogging Then
                            myItem.rtn = AddressOf setFieldID
                        ElseIf myItem.KeyType = KeyTypes.CWText Then
                            ' Use the current version of the routine which uses CommandID.
                            myItem.rtn = AddressOf sendCWMessage
                        End If
                        AddToKeyDictionary(myItem)
                    End If
                Next
            Next
        End Sub

        Private Sub setFieldID()
            ' This is called by DoCommand, see KeyCommands.
            parent.FieldID = CommandIDToADIF(CommandId)
        End Sub
    End Class
    Dim myCommands As myKeyCommands

    Dim logPosition As Long
    Dim rec As String

    Private Function formExists() As Boolean
        Return (session IsNot Nothing) AndAlso (session.FormData IsNot Nothing)
    End Function

    Private Property ignoreTextChange As Boolean
        Get
            If formExists() Then
                Return session.FormData.IgnoreTextChange
            Else
                Return False
            End If
        End Get
        Set(value As Boolean)
            If formExists() Then
                session.FormData.IgnoreTextChange = value
            End If
        End Set
    End Property

    ' Used to go to a specific field.
    Friend FieldID As String = vbNullString

    ' onFile is set if working with a log entry already on file.
    Private Property onFile As Boolean
        Get
            If formExists() Then
                Return session.FormData.onFile
            Else
                Return False
            End If
        End Get
        Set(value As Boolean)
            If formExists() Then
                session.FormData.onFile = value
            End If
        End Set
    End Property

    ''' <summary>
    ''' Set if a write is needed.
    ''' </summary>
    ''' <value>True if needed.  Not set if getting a search argument.</value>
    ''' <returns>True if needed.</returns>
    Friend Property NeedsWrite As Boolean
        Get
            If Not formExists() Then
                Return False
            Else
                Return session.FormData.NeedsWrite
            End If
        End Get
        Set(value As Boolean)
            If (Not searching) And formExists() Then
                session.FormData.NeedsWrite = value
            End If
        End Set
    End Property
    Private originalNeedsWrite As Boolean

    ''' <summary>
    ''' Get a field's text from the form.
    ''' </summary>
    ''' <param name="adif">tag of the field</param>
    ''' <param name="txt">string field to return</param>
    ''' <returns>True if the field is present.</returns>
    ''' <remarks>Use session.GetFieldText() to get a field from the session.</remarks>
    Private Function getFieldText(adif As String, ByRef txt As String) As Boolean
        If formExists() Then
            Return session.FormData.getScreenText(adif, txt)
        Else
            Return False
        End If
    End Function
    ''' <summary>
    ''' Get the field's text
    ''' </summary>
    ''' <param name="adif">tag</param>
    ''' <returns>The string or VBNullString</returns>
    Friend Function getFieldTextValue(adif As String) As String
        Dim rv As String = vbNullString
        If formExists() Then
            session.FormData.getScreenText(adif, rv)
        End If
        Return rv
    End Function
    ''' <summary>
    ''' Set the field's form text.
    ''' </summary>
    ''' <param name="adif">tag of the field</param>
    ''' <param name="txt">text to set</param>
    ''' <remarks>Use session.SetFieldText() to set a field in the session.</remarks>
    Private Sub setFieldText(adif As String, txt As String)
        If formExists() Then
            session.FormData.setScreenText(adif, txt)
        End If
    End Sub

    Private Sub SetDateTime()
        Dim dt As Date = Date.UtcNow
        setFieldText(AdifTags.ADIF_DateOn, dt.ToString("MM/dd/yyyy"))
        setFieldText(AdifTags.ADIF_TimeOn, dt.ToString("HHmm"))
    End Sub
    Friend Sub SetLogDateTime()
        ' Called due to a key press.
        SetDateTime()
    End Sub
    Private Sub setDateTimeIfNotSet()
        Dim dummy As String = vbNullString
        If getFieldText(AdifTags.ADIF_DateOn, dummy) AndAlso (dummy = vbNullString) Then
            SetDateTime()
        End If
    End Sub

    Private Sub SetDateTimeOut()
        Dim dt As Date = Date.UtcNow
        Dim dummy As String = vbNullString
        If getFieldText(AdifTags.ADIF_DateOff, dummy) AndAlso (dummy = vbNullString) Then
            setFieldText(AdifTags.ADIF_DateOff, dt.ToString("MM/dd/yyyy"))
        End If
        If getFieldText(AdifTags.ADIF_TimeOff, dummy) AndAlso (dummy = vbNullString) Then
            setFieldText(AdifTags.ADIF_TimeOff, dt.ToString("HHmm"))
        End If
    End Sub

    Private Sub setupOriginalContent()
        If formExists() Then
            session.FormData.SetupOriginalContent()
        End If
    End Sub

    Private Sub clear()
        If formExists() Then
            ' Clear the form fields.
            session.FormData.ClearAllContent()
            ' Special case for the serial number.
            setFieldText(AdifTags.ADIF_SentSerial, CStr(session.serial))
        End If
        setupOriginalContent() ' clears the original content
        oldCall = "" ' used for dup checking.
        gotoField("CALL") ' Focus on the call.
    End Sub

    ''' <summary>
    ''' Start a log session
    ''' </summary>
    ''' <returns>True on success</returns>
    Friend Function StartLogSession() As Boolean
        Tracing.TraceLine("StartLogSession", TraceLevel.Info)
        Dim rv As Boolean = False
        SyncLock sessionLock
            rv = (session IsNot Nothing)
            ' If no session.
            If Not rv Then
                session = New LogSession(ContactLog)
                ' Start the session.
                Dim clean As New LogClass.cleanupClass("LogEntry", AddressOf cleanup)
                rv = session.Start(CurrentOp, clean)
                If Not rv Then
                    session = Nothing
                    Tracing.TraceLine("StartLogSession:session didn't start", TraceLevel.Error)
                End If
            End If
        End SyncLock
        Return rv
    End Function

    ''' <summary>
    ''' Start a log file session.
    ''' Seek to the position if not -1.
    ''' </summary>
    ''' <param name="pos">position or -1</param>
    ''' <returns>True on success.</returns>
    Private Function startLogForm(pos As Long) As Boolean
        ' The active log is positioned at the position, or at the end if -1.
        Dim rv As Boolean
        Form1.StatusBox.Write("LogFile",
            LogCharacteristics.TrimmedFilename(ContactLog.Name, 20))
        setupForm()
        If pos = -1 Then
            ' Seek to the end of the file.
            rv = session.SeekToLast()
        Else
            ' Position specified.
            rv = session.SeekToPosition(pos)
            If rv Then
                rv = session.NextRecord
            End If
        End If
        clear() ' Clear screen fields.
        If Not rv Then
            Tracing.TraceLine("StartLogForm:failed", TraceLevel.Error)
        End If
        Return rv
    End Function

    ''' <summary>
    ''' Handle unsolicited cleanup.
    ''' </summary>
    ''' <returns>True if the form isn't active.</returns>
    Private Function cleanup() As Boolean
        If inDialog Then
            MsgBox(mustClose)
            Return False
        End If
        Close()
        Return True
    End Function

    Private Property searching As Boolean
        Get
            If formExists() Then
                Return session.FormData.Searching
            Else
                Return False
            End If
        End Get
        Set(value As Boolean)
            If formExists() Then
                session.FormData.Searching = value
            End If
        End Set
    End Property
    ''' <summary>
    ''' Get log search argument.
    ''' </summary>
    ''' <param name="s">the session whose fields are setup.</param>
    ''' <returns>true on success</returns>
    Public Function GetSearchArg(s As LogSession) As Boolean
        session = s
        searching = True
        Me.ShowDialog()
        searching = False
        Return (s.HasData)
    End Function

    Private fPos As Long = -1
    ''' <summary>
    ''' provide the file position of the item to show.
    ''' </summary>
    ''' <value>position as long</value>
    ''' <returns>position</returns>
    Public Property FilePosition As Long
        Get
            Return fPos
        End Get
        Set(value As Long)
            fPos = value
        End Set
    End Property

    Private theForm As Control = Nothing
    Private Sub setupForm()
        theForm = session.FormData.TheForm
        ' See if doing hamqth lookup.
        session.FormData.LookingUp = ((session.GetHeaderFieldText(AdifTags.HDR_CallLookup) = Logs.LookupChoices.Yes) And _
                                      Not FindDialog)
        theForm.Location = PlaceHolder.Location
        ' Compute the form size.
        Dim width As Integer = System.Math.Max(theForm.Size.Width, _
                                               PlaceHolder.Location.X + _
                                               PlaceHolder.Size.Width + 50)
        Dim height As Integer = theForm.Location.Y + _
            theForm.Size.Height
        Me.Size = New Size(width, height + 100)
        ' Locate the bottom buttons.
        OkButton.Location = New Point(OkButton.Location.X, height + 10)
        CnclButton.Location = New Point(CnclButton.Location.X, height + 10)
        theForm.TabIndex = PlaceHolder.TabIndex
        Controls.Add(theForm)

        ' Add interrupt handlers.
        For Each fld As LogField In session.FormData.Fields.Values
            ' Handle keyDown for displayed fields.
            If fld.IsDisplayed Then
                AddHandler fld.control.KeyDown, AddressOf handleKeyDown
            End If
            If fld.ADIFTag = AdifTags.ADIF_Call Then
                AddHandler fld.control.Leave, AddressOf CallSign_Leave
            End If

            ' Allow DXCC to be entered.
            If searching Then
                If fld.ADIFTag = AdifTags.ADIF_DXCC Then
                    CType(fld.control, TextBox).ReadOnly = False
                End If
            End If
        Next
    End Sub

    Private Sub LogEntry_Load(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles MyBase.Load
        inDialog = True ' Won't allow unsolicited cleanup.
        DialogResult = DialogResult.None
        wasActive = False ' Indicate new form.
        ' Save the needsWrite state in case the form is cancelled.
        originalNeedsWrite = NeedsWrite

        If searching Then
            setupForm()
            Me.Text = "Get Search Arguments"
            FieldID = AdifTags.ADIF_Call
            ShowEntry()
        Else
            Me.Text = "Log Entry Form"
            If session Is Nothing Then
                ' Open the log file.  Just quit if this fails.
                ' This also sets up the log form.
                If Not StartLogSession() OrElse Not startLogForm(FilePosition) Then
                    Tracing.TraceLine("LogEntry:Couldn't open the log", TraceLevel.Error)
                    DialogResult = DialogResult.Abort
                    Return
                End If
                If FilePosition <> -1 Then
                    ' Show an existing entry.
                    ShowEntry()
                End If
            End If
        End If
        ignoreTextChange = False
        myCommands = New myKeyCommands(Me)
    End Sub

    Friend Shadows Sub Close()
        'Logs.FormClosing()
        If session IsNot Nothing Then
            session.EndSession()
            session = Nothing
            If theForm IsNot Nothing Then
                Controls.Remove(theForm)
                theForm.Dispose()
            End If
        End If
    End Sub

    ''' <summary>
    ''' ask user to write the log if necessary
    ''' </summary>
    ''' <returns>false if cancelled, true otherwise</returns>
    ''' <remarks>
    ''' We'll ask if there is new or updated data, and the entry has a callsign.
    ''' If not cancelled, returns true even if the file wasn't written
    ''' </remarks>
    Friend Function optionalWrite() As Boolean
        Dim rv As Boolean = True
        Dim callText As String = vbNullString
        If NeedsWrite AndAlso (getFieldText(AdifTags.ADIF_Call, callText) AndAlso (callText <> vbNullString)) Then
            Select Case MessageBox.Show(writeLogEntryMsg & callText & "?", writeLogEntryTitleMsg, MessageBoxButtons.YesNo)
                Case DialogResult.Yes
                    iWrite()
                Case DialogResult.Cancel
                    rv = False
            End Select
        End If
        NeedsWrite = False ' Won't ask again.
        Return rv
    End Function

    Private Sub setModeIfNotSet()
        If (RigControl Is Nothing) OrElse (Not Power) Then
            Return
        End If
        Dim txt As String = vbNullString
        If getFieldText(AdifTags.ADIF_Mode, txt) AndAlso (txt = vbNullString) _
           And (RigControl.Mode IsNot Nothing) Then
            txt = RigControl.Mode.ToString.ToUpper
            Select Case txt
                Case "LSB", "USB"
                    txt = "SSB"
                Case "CWR"
                    txt = "CW"
                Case "FSK", "FSKR"
                    txt = "RTTY"
            End Select
            setFieldText(AdifTags.ADIF_Mode, txt)
        End If
    End Sub

    Private Sub setFreqsIfNotSet()
        If (RigControl Is Nothing) OrElse (Not Power) Then
            Return
        End If
        Dim dummy As String = vbNullString
        If getFieldText(AdifTags.ADIF_RXFreq, dummy) AndAlso (dummy = vbNullString) Then
            ' set from radio.
            setFieldText(AdifTags.ADIF_RXFreq, FormatFreq(RigControl.RXFrequency))
        End If
        If getFieldText(AdifTags.ADIF_TXFreq, dummy) AndAlso (dummy = vbNullString) Then
            setFieldText(AdifTags.ADIF_TXFreq, FormatFreq(RigControl.TXFrequency))
        End If
        If getFieldText(AdifTags.ADIF_Band, dummy) AndAlso (dummy = vbNullString) Then
            ' Set the band here so dup checking works.
            Dim item As HamBands.Bands.BandItem
            item = HamBands.Bands.Query(RigControl.TXFrequency)
            If item IsNot Nothing Then
                setFieldText(AdifTags.ADIF_Band, item.Name)
            End If
        End If
    End Sub

    Private Sub newEntry()
        If session Is Nothing Then
            Return
        End If
        clear()
        onFile = False
        ' Get to the end of the file in case this is cancelled.
        If Not session.EOF Then
            session.SeekToLast()
            session.NextRecord()
        End If
        setNewEntryItems()
        NeedsWrite = False
    End Sub

    Private Sub setNewEntryItems()
        Dim wasIgnored As Boolean = ignoreTextChange
        ignoreTextChange = True
        setDateTimeIfNotSet()
        setModeIfNotSet()
        setFreqsIfNotSet()
        If Not onFile Then
            ' Set the serial number.
            setFieldText(AdifTags.ADIF_SentSerial, CStr(session.serial))
        End If
        ignoreTextChange = wasIgnored
    End Sub

    Private Sub OKButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles OkButton.Click
        Dim b As Button = sender
        ' Perform any field exits.
        OkButton.Focus()
        ' If was a search, set the searchString.
        If searching Then
            ' Put screen fields into log format.
            entryFieldsToSession()
        End If
        DialogResult = DialogResult.OK
    End Sub

    Private Sub CnclButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles CnclButton.Click
        ' Cancel button.
        If Not searching Then
            ' Reset fields changed this time to original values.
            ignoreTextChange = True
            session.FormData.RestoreOriginalContent()
            ignoreTextChange = False
        End If
        NeedsWrite = originalNeedsWrite
        DialogResult = DialogResult.Cancel
    End Sub

    ''' <summary>
    ''' Write a log record.
    ''' </summary>
    ''' <returns>true on success</returns>
    ''' <remarks>Called from outside.</remarks>
    Friend Function Write() As Boolean
        Return iWrite()
    End Function
    Private Function iWrite() As Boolean
        If session Is Nothing Then
            Return False
        End If
        ' Write the log record.
        Dim fret As Boolean
        ' If onFile is set, this is an update.
        If Not onFile Then
            ' Set datetime, rx and tx freqs if not set.
            Dim dummy As String = vbNullString
            If getFieldText(AdifTags.ADIF_DateOn, dummy) AndAlso (dummy = vbNullString) Then
                SetDateTime()
            End If
            SetDateTimeOut()
            setFreqsIfNotSet()
        End If
        ' Put screen fields into log format.
        entryFieldsToSession()
        If session.FormData.WriteEntry IsNot Nothing Then
            ' Call out to the log.
            If onFile Then
                session.FormData.WriteEntry(session.FieldDictionary, oldFields)
            Else
                session.FormData.WriteEntry(session.FieldDictionary, Nothing)
            End If
        End If
        If isDupChecking Then
            ' Update dup checking
            Dim key = New LogDupChecking.keyElement(session, DupType)
            Dups.AddToDictionary(key)
        End If
        ' Update or add.
        If onFile Then
            fret = session.Update()
            ' We're positioned at the just-updated record.
            If fret Then
                ShowEntry()
                If FilePosition <> -1 Then
                    FilePosition = session.JustReadPosition ' Update the position.
                End If
            End If
        Else
            ' Adding a new entry.
            fret = session.Append
            session.serial += 1
            ' We're positioned at the just-appended record.
            clear()
        End If
        NeedsWrite = False
        Return fret
    End Function

    ''' <summary>
    ''' Put entry fields into the session.
    ''' </summary>
    ''' <remarks>
    ''' The data in the session is ordered according to the order
    ''' in which fields are specified on the form.
    ''' Note that this is not the order in which they are displayed.
    ''' </remarks>
    Private Sub entryFieldsToSession()
        ' For each logged field
        For Each fld As LogFieldElement In session.FieldDictionary.Values
            Dim screenFld As LogField
            ' We want to show an error if the field doesn't exist.
            screenFld = session.FormData.getScreenElement(fld.ADIFTag, True)
            fld.Data = screenFld.Item
        Next
    End Sub

    Private Sub handleKeyDown(ByVal Sender As System.Object, ByVal e As System.Windows.Forms.KeyEventArgs)
        If e.Handled Then
            Return
        End If
        If Not (e.Alt Or e.Shift) Then
            Select Case e.KeyCode
                Case Keys.PageUp
                    ' Go to a previous record.
                    optionalWrite() ' See if need to write.
                    If e.Control Then
                        session.SeekToFirst()
                        If (Not session.EOF) AndAlso session.NextRecord Then
                            ShowEntry()
                        End If
                    Else
                        If session.PreviousRecord Then
                            ShowEntry()
                        End If
                    End If
                    e.SuppressKeyPress = True
                Case Keys.PageDown
                    ' Go to a subsequent record.
                    optionalWrite() ' See if need to write.
                    If e.Control Then
                        session.SeekToLast()
                    End If
                    If (Not session.EOF) AndAlso session.NextRecord Then
                        ShowEntry()
                    End If
                    e.SuppressKeyPress = True
            End Select
        End If
        If Not e.Handled Then
            e.SuppressKeyPress = myCommands.DoCommand(e.KeyData)
            GotoCommand()
        End If
    End Sub

    Private Sub LogEntry_Activated(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles MyBase.Activated
        If Not wasActive Then
            ' This form was just brought up.
            wasActive = True
            setupOriginalContent()
            ' If a specific field was requested, give it focus, or perform the action.
            GotoCommand()
        End If
    End Sub

    ''' <summary> go to the field, or do the action, indicated by fieldID, and invalidate fieldID </summary>
    Friend Sub GotoCommand()
        Select Case FieldID
            Case KeyCommands.iADIF_Logwrite
                iWrite()
            Case KeyCommands.iADIF_LogNewEntry
                newEntry()
            Case KeyCommands.iADIF_Logsearch
                searchForThisCall()
            Case Else
                gotoField(FieldID)
        End Select
        FieldID = vbNullString
    End Sub

    Private Function gotoField(adif As String) As Boolean
        If (session Is Nothing) Or (adif = vbNullString) Then
            Return False
        End If
        Dim rv As Boolean
        Dim fld As LogField = session.FormData.getScreenElement(adif)
        If fld IsNot Nothing Then
            fld.control.Focus()
            rv = True
        Else
            rv = False
        End If
        Return rv
    End Function

    ''' <summary>
    ''' Show an existing entry.
    ''' </summary>
    ''' <remarks>
    ''' This sets the onFile flag.
    ''' </remarks>
    Private Sub ShowEntry()
        Dim wasIgnored As Boolean = ignoreTextChange
        ignoreTextChange = True
        clear()
        Dim dummy As String = ""
        onFile = True
        ' Setup oldFields if not search arg.
        If Not searching Then
            oldFields = New Dictionary(Of String, LogFieldElement)
        End If
        ' for each logged field.
        For Each fld As LogFieldElement In session.FieldDictionary.Values
            setFieldText(fld.ADIFTag, fld.Data)
            If Not searching Then
                oldFields.Add(fld.ADIFTag, New LogFieldElement(fld))
            End If
        Next
        oldCall = session.GetFieldText(AdifTags.ADIF_Call)
        setDup(False)
        ignoreTextChange = wasIgnored
        setupOriginalContent()
    End Sub

    Private isDup As Boolean
    ''' <summary>
    ''' Set the dup count on screen.
    ''' If a dup, set issDup and alarm.
    ''' </summary>
    Private Sub setDup(callChange As Boolean)
        ' quit if not dup checking or this is a search.
        If (Not isDupChecking) Or searching Then
            Return
        End If
        Dim key = New LogDupChecking.keyElement(session, DupType)
        Dim ct As Integer = Dups.DupTest(key)
        If (Not onFile) Or callChange Then
            ct += 1
        End If
        setFieldText(AdifTags.iADIF_DupCount, CStr(ct))
        isDup = (ct > 1)
        If isDup Then
            Console.Beep(880, 400)
        End If
    End Sub

    Private Sub CallSign_Leave(ByVal sender As System.Object, ByVal e As System.EventArgs)
        Dim callSign As String = getFieldTextValue(AdifTags.ADIF_Call)
        ' Do nothing if entering search args or no call.
        If (session Is Nothing) Or searching Or (callSign = vbNullString) Then
            Return
        End If
        ' Prime for a new entry.
        setNewEntryItems()
        If isDupChecking Then
            ' Check for a dup, prime the session.
            session.SetFieldText(AdifTags.ADIF_Mode, getFieldTextValue(AdifTags.ADIF_Mode))
            session.SetFieldText(AdifTags.ADIF_Band, getFieldTextValue(AdifTags.ADIF_Band))
            session.SetFieldText(AdifTags.ADIF_SentSerial, getFieldTextValue(AdifTags.ADIF_SentSerial))
            session.SetFieldText(AdifTags.ADIF_Call, callSign)
            setDup(oldCall <> callSign)
        End If
    End Sub

    ''' <summary>
    ''' Return true if this field has the focus
    ''' </summary>
    ''' <param name="tag">field's ADIF tag</param>
    Private Function hasFocus(tag As String) As Boolean
        Dim fld As LogField = Nothing
        Dim rv As Boolean = False
        If session.FormData.Fields.TryGetValue(tag, fld) Then
            rv = fld.control.Focused
        End If
        Return rv
    End Function

    Private Sub searchForThisCall()
        Dim callsign As String = getFieldTextValue(AdifTags.ADIF_Call)
        If (Not searching) And (callsign <> vbNullString) Then
            ' Run the search under another thread.
            Dim thrd As New Thread(AddressOf findThread)
            thrd.Start(callsign)
            Thread.Sleep(0)
        End If
    End Sub
    Private Sub findThread(callsign As String)
        ' FindLogEntry.SearchCall = session.GetFieldText(AdifTags.ADIF_Call)
        FindLogEntry.SearchCall = callsign
        FindLogEntry.ShowDialog()
    End Sub

    Private Sub LogEntry_FormClosed(sender As System.Object, e As System.Windows.Forms.FormClosedEventArgs) Handles MyBase.FormClosed
        inDialog = False
    End Sub

    Friend Sub EndSession()
        If session IsNot Nothing Then
            session.EndSession()
            session = Nothing
        End If
    End Sub

    Private Sub LogEntry_FormClosing(sender As System.Object, e As System.Windows.Forms.FormClosingEventArgs) Handles MyBase.FormClosing
        ' Invalidate fieldID.
        FieldID = vbNullString
    End Sub
End Class