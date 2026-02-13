Imports System.Drawing
Imports System.Windows.Forms
Imports adif
Imports JJLogLib
Imports JJTrace
Imports JJCountriesDB
Imports System.Media
Imports Radios

''' <summary>
''' Quick-entry logging panel for Logging Mode.
''' Embedded in Form1 via SplitContainer. Thin wrapper over LogSession —
''' manages its own TextBoxes and talks to LogSession via SetFieldText/GetFieldText.
'''
''' Layout: Entry fields at top (fixed height), Recent QSOs DataGridView below (fills).
''' The grid uses Windows UI Automation — JAWS/NVDA arrow through rows,
''' read column header + cell value automatically.
'''
''' Previous Contact lookup: when the operator tabs out of the Call Sign field,
''' we check the in-memory call index (built during log scan) and show the last
''' QSO with that station plus total contact count. Auto-fills Name if empty.
''' </summary>
Friend Class LogPanel
    Inherits UserControl

    ' --- Entry fields ---
    Private WithEvents CallSignBox As TextBox
    Private RSTSentBox As TextBox
    Private RSTRcvdBox As TextBox
    Private NameBox As TextBox
    Private QTHBox As TextBox
    Private StateBox As TextBox
    Private GridBox As TextBox
    Private CommentsBox As TextBox

    ' --- Read-only display labels ---
    Private FreqLabel As Label
    Private ModeLabel As Label
    Private BandLabel As Label
    Private DateTimeLabel As Label
    Private SerialLabel As Label
    Private DupLabel As Label

    ' --- Previous contact display (tabbable read-only TextBox) ---
    Private PreviousContactBox As TextBox

    ' --- Recent QSOs grid ---
    Private WithEvents RecentGrid As DataGridView

    ''' <summary>
    ''' Maximum recent QSOs to show in the grid, from operator settings.
    ''' Falls back to 20 if no operator is loaded.
    ''' </summary>
    Private ReadOnly Property MaxRecentQSOs As Integer
        Get
            If CurrentOp IsNot Nothing AndAlso CurrentOp.RecentQsoCount >= 5 Then
                Return CurrentOp.RecentQsoCount
            End If
            Return PersonalData.personal_v1.RecentQsoCountDefault
        End Get
    End Property

    ' --- Layout ---
    Private EntryPanel As Panel        ' Fixed-height panel for entry fields + info labels
    Private GridPanel As Panel         ' Fill panel for the DataGridView

    ' --- State ---
    Private session As LogSession
    Private currentCall As String = ""
    Private isDup As Boolean = False

    ' --- Callbook lookup (QRZ or HamQTH) ---
    Private qrzLookup As QrzLookup.QrzCallbookLookup = Nothing
    Private hamqthLookup As HamQTHLookup.CallbookLookup = Nothing
    Private hamqthFallback As HamQTHLookup.CallbookLookup = Nothing  ' HamQTH fallback when QRZ is primary
    Private lastLookedUpCall As String = ""
    Private operatorCountry As String = ""
    Private qrzConsecutiveFailures As Integer = 0
    Private qrzFallbackNotified As Boolean = False
    Private pendingCallSign As String = ""  ' Call sign for fallback retry

    ' --- QRZ Logbook upload ---
    Private qrzLogbook As Global.QrzLookup.QrzLogbookClient = Nothing
    Private stationCallSign As String = ""

    ' --- Call sign index: built during log scan, used for instant previous-contact lookup ---
    Private callIndex As New Dictionary(Of String, PreviousContactInfo)(StringComparer.OrdinalIgnoreCase)

    ''' <summary>
    ''' Tracks aggregate info about previous contacts with a specific call sign.
    ''' Updated during the initial log scan and after each WriteEntry.
    ''' </summary>
    Private Class PreviousContactInfo
        Public Count As Integer         ' Total number of QSOs with this call
        Public LastDate As String       ' Date of most recent QSO (MM/dd/yyyy)
        Public LastBand As String       ' Band of most recent QSO
        Public LastMode As String       ' Mode of most recent QSO
        Public LastName As String       ' Name from most recent QSO
        Public LastQTH As String        ' QTH from most recent QSO
    End Class

    ' --- Preserved state for mode-switch round-trip ---
    Private preservedFields As Dictionary(Of String, String) = Nothing

    Friend Sub New()
        MyBase.New()
        Me.Name = "LogPanel"
        Me.AccessibleName = "Log entry pane"
        Me.AccessibleRole = AccessibleRole.Pane
        BuildControls()
    End Sub

    ''' <summary>
    ''' Bind this panel to an active log session. Call after ConfigContactLog().
    ''' </summary>
    Friend Sub Initialize(s As LogSession)
        session = s
        LoadRecentQSOs()
        If preservedFields IsNot Nothing Then
            RestoreState()
        Else
            NewEntry()
        End If
    End Sub

    ''' <summary>
    ''' Clear all fields and prepare for a new QSO entry.
    ''' Auto-fills radio fields and serial number.
    ''' </summary>
    Friend Sub NewEntry()
        ClearFields()
        ' Clear session date/time so next AutoFillFromRadio gets fresh timestamp
        If session IsNot Nothing Then
            session.SetFieldText(AdifTags.ADIF_DateOn, "")
            session.SetFieldText(AdifTags.ADIF_TimeOn, "")
        End If
        AutoFillFromRadio()
        If session IsNot Nothing Then
            SerialLabel.Text = "Serial: " & CStr(session.serial)
        End If
        DupLabel.Text = "Dup: 0"
        isDup = False
        currentCall = ""
        lastLookedUpCall = ""
        ClearPreviousContact()
        CallSignBox.Focus()
    End Sub

    ''' <summary>
    ''' Write the current entry to the log file.
    ''' Returns True on success.
    ''' </summary>
    Friend Function WriteEntry() As Boolean
        If session Is Nothing Then Return False
        Dim callText = CallSignBox.Text.Trim().ToUpper()
        If String.IsNullOrEmpty(callText) Then
            ScreenReaderOutput.Speak("No call sign entered", True)
            Return False
        End If

        ' Validate required fields before saving.
        Dim missing = GetMissingFields()
        If missing.Count > 0 Then
            Dim fieldList = String.Join(", ", missing)
            ScreenReaderOutput.Speak("Missing " & fieldList, True)
            Tracing.TraceLine("LogPanel.WriteEntry: missing fields: " & fieldList,
                              Diagnostics.TraceLevel.Info)
            Return False
        End If

        ' Copy fields to session.
        FieldsToSession()

        ' Set date/time if not already set.
        Dim dt = Date.UtcNow
        If String.IsNullOrEmpty(session.GetFieldText(AdifTags.ADIF_DateOn)) Then
            session.SetFieldText(AdifTags.ADIF_DateOn, dt.ToString("MM/dd/yyyy"))
            session.SetFieldText(AdifTags.ADIF_TimeOn, dt.ToString("HHmm"))
        End If
        ' Set end date/time.
        If String.IsNullOrEmpty(session.GetFieldText(AdifTags.ADIF_DateOff)) Then
            session.SetFieldText(AdifTags.ADIF_DateOff, dt.ToString("MM/dd/yyyy"))
        End If
        If String.IsNullOrEmpty(session.GetFieldText(AdifTags.ADIF_TimeOff)) Then
            session.SetFieldText(AdifTags.ADIF_TimeOff, dt.ToString("HHmm"))
        End If

        ' Snapshot values for grid row BEFORE Append (which clears serial handling).
        Dim gridTime = session.GetFieldText(AdifTags.ADIF_TimeOn)
        Dim gridCall = session.GetFieldText(AdifTags.ADIF_Call)
        Dim gridMode = session.GetFieldText(AdifTags.ADIF_Mode)
        Dim gridFreq = session.GetFieldText(AdifTags.ADIF_RXFreq)
        Dim gridRSTSent = session.GetFieldText(AdifTags.ADIF_HisRST)
        Dim gridRSTRcvd = session.GetFieldText(AdifTags.ADIF_MyRST)
        Dim gridName = session.GetFieldText(AdifTags.ADIF_Name)
        Dim gridDate = session.GetFieldText(AdifTags.ADIF_DateOn)
        Dim gridBand = session.GetFieldText(AdifTags.ADIF_Band)
        Dim gridQTH = session.GetFieldText(AdifTags.ADIF_QTH, False)

        ' Call the log's WriteEntry delegate (updates stats).
        If session.FormData IsNot Nothing AndAlso session.FormData.WriteEntry IsNot Nothing Then
            session.FormData.WriteEntry(session.FieldDictionary, Nothing)
        End If

        ' Update dup dictionary.
        If isDupChecking Then
            Dim key = New LogDupChecking.keyElement(session, DupType)
            Dups.AddToDictionary(key)
        End If

        ' Append to log file.
        Dim ok = session.Append()
        If ok Then
            session.serial += 1

            ' Add the just-saved QSO to the grid.
            AddQSOToGrid(gridTime, gridCall, gridMode, gridFreq,
                         gridRSTSent, gridRSTRcvd, gridName)

            ' Update the call index with this new QSO.
            UpdateCallIndex(gridCall, gridDate, gridBand, gridMode, gridName, gridQTH)

            ' Upload to QRZ Logbook if enabled (fire-and-forget).
            ' Must snapshot fields BEFORE NewEntry() clears them.
            If qrzLogbook IsNot Nothing Then
                Try
                    Dim fieldsCopy As New Dictionary(Of String, adif.LogFieldElement)
                    For Each kvp In session.FieldDictionary
                        fieldsCopy(kvp.Key) = New adif.LogFieldElement(kvp.Value)
                    Next
                    Dim adifRecord = Global.QrzLookup.QrzLogbookClient.FieldsToAdifRecord(fieldsCopy, stationCallSign)
                    If adifRecord IsNot Nothing Then
                        qrzLogbook.UploadQSO(adifRecord, callText)
                    End If
                Catch ex As Exception
                    Tracing.TraceLine("LogPanel: QRZ upload prep failed: " & ex.Message,
                                      Diagnostics.TraceLevel.Warning)
                End Try
            End If

            ScreenReaderOutput.Speak("Saved " & callText, True)
            Tracing.TraceLine("LogPanel.WriteEntry: saved " & callText, Diagnostics.TraceLevel.Info)
            NewEntry()
        Else
            ScreenReaderOutput.Speak("Write failed", True)
            Tracing.TraceLine("LogPanel.WriteEntry: append failed", Diagnostics.TraceLevel.Error)
        End If
        Return ok
    End Function

    ''' <summary>
    ''' Save current field values so they survive a mode switch.
    ''' Returns Nothing if no call sign is entered (nothing worth preserving).
    ''' </summary>
    Friend Function SaveState() As Dictionary(Of String, String)
        If String.IsNullOrEmpty(CallSignBox.Text.Trim()) Then
            preservedFields = Nothing
            Return Nothing
        End If
        Dim state As New Dictionary(Of String, String) From {
            {AdifTags.ADIF_Call, CallSignBox.Text},
            {AdifTags.ADIF_HisRST, RSTSentBox.Text},
            {AdifTags.ADIF_MyRST, RSTRcvdBox.Text},
            {AdifTags.ADIF_Name, NameBox.Text},
            {AdifTags.ADIF_QTH, QTHBox.Text},
            {AdifTags.ADIF_State, StateBox.Text},
            {AdifTags.ADIF_Grid, GridBox.Text},
            {AdifTags.ADIF_Comment, CommentsBox.Text}
        }
        preservedFields = state
        Return state
    End Function

    ''' <summary>
    ''' Restore previously saved field values after returning to Logging Mode.
    ''' </summary>
    Private Sub RestoreState()
        If preservedFields Is Nothing Then Return
        ClearFields()
        For Each kvp In preservedFields
            SetFieldByTag(kvp.Key, kvp.Value)
        Next
        AutoFillFromRadio()  ' Refresh radio display
        If session IsNot Nothing Then
            SerialLabel.Text = "Serial: " & CStr(session.serial)
        End If
        preservedFields = Nothing
        CallSignBox.Focus()
    End Sub

    ''' <summary>
    ''' Focus the call sign field.
    ''' </summary>
    Friend Sub FocusCallSign()
        CallSignBox.Focus()
    End Sub

    ''' <summary>
    ''' Focus a specific entry field by name. Used by Form1 to handle
    ''' Logging Mode field-jump hotkeys (Alt+C, Alt+R, etc.).
    ''' </summary>
    Friend Sub FocusField(fieldName As String)
        Select Case fieldName.ToUpper()
            Case "CALL" : CallSignBox.Focus()
            Case "RSTSENT" : RSTSentBox.Focus()
            Case "RSTRCVD" : RSTRcvdBox.Focus()
            Case "NAME" : NameBox.Focus()
            Case "QTH" : QTHBox.Focus()
            Case "STATE" : StateBox.Focus()
            Case "GRID" : GridBox.Focus()
            Case "COMMENTS" : CommentsBox.Focus()
            Case "PREVIOUS" : PreviousContactBox.Focus()
            Case "RECENTGRID" : RecentGrid.Focus()
            Case Else : CallSignBox.Focus()
        End Select
    End Sub

#Region "Callbook Lookup"

    ''' <summary>
    ''' Initialize callbook lookup based on operator settings.
    ''' Called from Form1.EnterLoggingMode() after Initialize().
    ''' </summary>
    Friend Sub InitializeCallbook(source As String, username As String, password As String,
                                   Optional operatorCallSign As String = "")
        FinishCallbook()  ' Clean up any previous instance.
        lastLookedUpCall = ""
        qrzConsecutiveFailures = 0
        qrzFallbackNotified = False
        pendingCallSign = ""

        ' Look up operator's country for DX comparison in callbook announcements.
        operatorCountry = ""
        If Not String.IsNullOrEmpty(operatorCallSign) Then
            Try
                Dim cdb As New CountriesDB()
                Dim opRec = cdb.LookupByCall(operatorCallSign)
                If opRec IsNot Nothing Then
                    operatorCountry = If(opRec.Country, "")
                End If
            Catch
            End Try
        End If

        ' Resolve effective source and credentials, falling back to built-in HamQTH
        ' account when the operator hasn't configured (or has incomplete) credentials.
        ' This mirrors the pattern used by StationLookup.
        Dim effectiveSource As String = "HamQTH"
        Dim effectiveUser As String = HamqthLookupID
        Dim effectivePass As String = HamqthLookupPassword

        If Not String.IsNullOrEmpty(source) AndAlso source <> "None" AndAlso
           Not String.IsNullOrEmpty(username) AndAlso Not String.IsNullOrEmpty(password) Then
            effectiveSource = source
            effectiveUser = username
            effectivePass = password
        End If

        Select Case effectiveSource
            Case "QRZ"
                qrzLookup = New QrzLookup.QrzCallbookLookup(effectiveUser, effectivePass)
                AddHandler qrzLookup.CallsignSearchEvent, AddressOf QrzResultHandler
                Tracing.TraceLine("LogPanel: QRZ callbook lookup initialized", Diagnostics.TraceLevel.Info)

                ' Also prepare a HamQTH fallback client for when QRZ fails.
                hamqthFallback = New HamQTHLookup.CallbookLookup(HamqthLookupID, HamqthLookupPassword)
                AddHandler hamqthFallback.CallsignSearchEvent, AddressOf HamQTHResultHandler
                Tracing.TraceLine("LogPanel: HamQTH fallback initialized for QRZ failure recovery", Diagnostics.TraceLevel.Info)

            Case Else  ' "HamQTH" or fallback
                hamqthLookup = New HamQTHLookup.CallbookLookup(effectiveUser, effectivePass)
                AddHandler hamqthLookup.CallsignSearchEvent, AddressOf HamQTHResultHandler
                If effectiveUser = HamqthLookupID Then
                    Tracing.TraceLine("LogPanel: HamQTH callbook lookup initialized (built-in account)", Diagnostics.TraceLevel.Info)
                Else
                    Tracing.TraceLine("LogPanel: HamQTH callbook lookup initialized", Diagnostics.TraceLevel.Info)
                End If
        End Select
    End Sub

    ''' <summary>
    ''' Clean up callbook lookup resources. Called from Form1.ExitLoggingMode().
    ''' </summary>
    Friend Sub FinishCallbook()
        If qrzLookup IsNot Nothing Then
            RemoveHandler qrzLookup.CallsignSearchEvent, AddressOf QrzResultHandler
            qrzLookup.Finished()
            qrzLookup = Nothing
        End If
        If hamqthLookup IsNot Nothing Then
            RemoveHandler hamqthLookup.CallsignSearchEvent, AddressOf HamQTHResultHandler
            hamqthLookup.Finished()
            hamqthLookup = Nothing
        End If
        If hamqthFallback IsNot Nothing Then
            RemoveHandler hamqthFallback.CallsignSearchEvent, AddressOf HamQTHResultHandler
            hamqthFallback.Finished()
            hamqthFallback = Nothing
        End If
    End Sub

    ''' <summary>
    ''' Trigger a callbook lookup for the given call sign.
    ''' Called from CallSignBox_Leave after local lookups.
    ''' </summary>
    Private Sub TriggerCallbookLookup(callSign As String)
        If String.IsNullOrEmpty(callSign) Then Return
        If callSign = lastLookedUpCall Then Return
        lastLookedUpCall = callSign
        pendingCallSign = callSign

        If qrzLookup IsNot Nothing AndAlso qrzLookup.CanLookup Then
            qrzLookup.LookupCall(callSign)
        ElseIf hamqthLookup IsNot Nothing AndAlso hamqthLookup.CanLookup Then
            hamqthLookup.LookupCall(callSign)
        ElseIf hamqthFallback IsNot Nothing AndAlso hamqthFallback.CanLookup Then
            ' QRZ primary is unavailable; use HamQTH fallback directly.
            hamqthFallback.LookupCall(callSign)
        End If
    End Sub

    ''' <summary>
    ''' Handle QRZ lookup result — convert to CallbookResult and apply.
    ''' Called on a background thread; marshals to UI thread.
    ''' Distinguishes three cases:
    '''   1. Valid result with callsign data → apply and reset failure counter.
    '''   2. Valid session but callsign not found → no failure (QRZ is working fine).
    '''   3. Null result or session error → real failure, count toward fallback threshold.
    ''' </summary>
    Private Sub QrzResultHandler(result As QrzLookup.QrzCallbookLookup.QrzDatabase)
        ' Case 1: QRZ returned callsign data — reset failure counter and apply.
        If result IsNot Nothing AndAlso result.Callsign IsNot Nothing Then
            qrzConsecutiveFailures = 0
            pendingCallSign = ""
            Dim cbr As New CallbookResult() With {
                .Source = "QRZ",
                .Name = If(result.Callsign.FirstName, ""),
                .QTH = If(result.Callsign.City, ""),
                .State = If(result.Callsign.State, ""),
                .Grid = If(result.Callsign.Grid, ""),
                .Country = If(result.Callsign.Country, "")
            }
            ApplyCallbookResult(cbr)
            Return
        End If

        ' Case 2: Valid session but callsign not in QRZ database — not a failure.
        ' QRZ is working fine, the call just isn't listed. Try HamQTH as supplemental
        ' lookup (not as a failure fallback), but don't count toward failure threshold.
        If result IsNot Nothing AndAlso result.Session IsNot Nothing AndAlso
           String.IsNullOrEmpty(result.Session.Error) Then
            Tracing.TraceLine($"LogPanel: QRZ callsign not found (session OK), trying HamQTH supplement", Diagnostics.TraceLevel.Info)
            If hamqthFallback IsNot Nothing AndAlso hamqthFallback.CanLookup AndAlso
               Not String.IsNullOrEmpty(pendingCallSign) Then
                hamqthFallback.LookupCall(pendingCallSign)
            End If
            Return
        End If

        ' Case 3: Real failure — null result, session error, or auth problem.
        ' Count toward fallback threshold and try HamQTH.
        qrzConsecutiveFailures += 1
        Tracing.TraceLine($"LogPanel: QRZ lookup failed (consecutive failures: {qrzConsecutiveFailures})", Diagnostics.TraceLevel.Warning)

        If qrzConsecutiveFailures >= 3 AndAlso Not qrzFallbackNotified Then
            qrzFallbackNotified = True
            Radios.ScreenReaderOutput.Speak("QRZ lookups are failing. Using HamQTH as fallback. Check your QRZ subscription status.", False)
        End If

        If hamqthFallback IsNot Nothing AndAlso hamqthFallback.CanLookup AndAlso
           Not String.IsNullOrEmpty(pendingCallSign) Then
            Tracing.TraceLine($"LogPanel: Falling back to HamQTH for {pendingCallSign}", Diagnostics.TraceLevel.Info)
            hamqthFallback.LookupCall(pendingCallSign)
        End If
    End Sub

    ''' <summary>
    ''' Handle HamQTH lookup result — convert to CallbookResult and apply.
    ''' Called on a background thread; marshals to UI thread.
    ''' </summary>
    Private Sub HamQTHResultHandler(result As HamQTHLookup.CallbookLookup.HamQTH)
        If result Is Nothing OrElse result.search Is Nothing Then Return
        Dim cbr As New CallbookResult() With {
            .Source = "HamQTH",
            .Name = If(result.search.nick, ""),
            .QTH = If(result.search.qth, If(result.search.adr_city, "")),
            .State = If(result.search.State, ""),
            .Grid = If(result.search.grid, ""),
            .Country = If(result.search.country, "")
        }
        ApplyCallbookResult(cbr)
    End Sub

    ''' <summary>
    ''' Apply a callbook result to empty fields. Marshals to UI thread if needed.
    ''' Only fills fields that are currently empty (local data + user input takes priority).
    ''' </summary>
    Private Sub ApplyCallbookResult(result As CallbookResult)
        If Me.InvokeRequired Then
            Me.BeginInvoke(Sub() ApplyCallbookResult(result))
            Return
        End If

        ' Fill only empty fields (local data + user input takes priority).
        FillIfEmpty(NameBox, result.Name)
        FillIfEmpty(QTHBox, result.QTH)
        FillIfEmpty(StateBox, result.State)
        FillIfEmpty(GridBox, result.Grid)

        ' Speak actual values: name, QTH, state (if available), country if DX.
        Dim parts As New List(Of String)
        If Not String.IsNullOrEmpty(result.Name) Then parts.Add(result.Name)
        If Not String.IsNullOrEmpty(result.QTH) Then parts.Add(result.QTH)
        If Not String.IsNullOrEmpty(result.State) Then parts.Add(result.State)

        ' Include country only when it differs from operator's country (DX station).
        If Not String.IsNullOrEmpty(result.Country) AndAlso
           Not result.Country.Equals(operatorCountry, StringComparison.OrdinalIgnoreCase) Then
            parts.Add(result.Country)
        End If

        ' Speak the callbook result without interrupting current field announcement.
        ' This way if the operator has tabbed to the next field, they hear both
        ' the field name and then the callbook data queued right after.
        If parts.Count > 0 Then
            ScreenReaderOutput.Speak(String.Join(", ", parts), False)
        End If
    End Sub

    ''' <summary>
    ''' Fill a TextBox only if it's currently empty and the value is non-empty.
    ''' Returns True if the field was filled.
    ''' </summary>
    Private Shared Function FillIfEmpty(tb As TextBox, value As String) As Boolean
        If String.IsNullOrEmpty(value) Then Return False
        If Not String.IsNullOrEmpty(tb.Text.Trim()) Then Return False
        tb.Text = value
        Return True
    End Function

#End Region

#Region "QRZ Logbook"

    ''' <summary>
    ''' Initialize QRZ Logbook upload client based on operator settings.
    ''' Called from Form1.EnterLoggingMode() after InitializeCallbook().
    ''' </summary>
    Friend Sub InitializeQrzLogbook(enabled As Boolean, apiKey As String,
                                     opCallSign As String, version As String)
        FinishQrzLogbook()  ' Clean up any previous instance.
        stationCallSign = If(opCallSign, "")

        If Not enabled Then Return
        If String.IsNullOrEmpty(apiKey) Then Return

        qrzLogbook = New Global.QrzLookup.QrzLogbookClient(apiKey, version)
        AddHandler qrzLogbook.UploadResultEvent, AddressOf QrzLogbookResultHandler
        Tracing.TraceLine("LogPanel: QRZ Logbook upload initialized", Diagnostics.TraceLevel.Info)
    End Sub

    ''' <summary>
    ''' Clean up QRZ Logbook upload resources. Called from Form1.ExitLoggingMode().
    ''' </summary>
    Friend Sub FinishQrzLogbook()
        If qrzLogbook IsNot Nothing Then
            RemoveHandler qrzLogbook.UploadResultEvent, AddressOf QrzLogbookResultHandler
            qrzLogbook.Finished()
            qrzLogbook = Nothing
        End If
    End Sub

    ''' <summary>
    ''' Handle QRZ Logbook upload result. Called on a background thread;
    ''' marshals to UI thread for screen reader announcement.
    ''' </summary>
    Private Sub QrzLogbookResultHandler(success As Boolean, callSign As String, errorMessage As String)
        If Me.InvokeRequired Then
            Me.BeginInvoke(Sub() QrzLogbookResultHandler(success, callSign, errorMessage))
            Return
        End If

        If success Then
            ScreenReaderOutput.Speak("Logged to QRZ", False)
        Else
            Dim msg = "QRZ upload failed"
            If Not String.IsNullOrEmpty(errorMessage) Then
                msg &= ": " & errorMessage
            End If
            ScreenReaderOutput.Speak(msg, False)
        End If
    End Sub

#End Region

#Region "Previous Contact Lookup"

    ''' <summary>
    ''' Update the call index with a newly logged QSO.
    ''' Called after successful WriteEntry.
    ''' </summary>
    Private Sub UpdateCallIndex(callSign As String, dateOn As String, band As String,
                                 mode As String, name As String, qth As String)
        If String.IsNullOrEmpty(callSign) Then Return
        Dim upperCall = callSign.Trim().ToUpper()

        Dim info As PreviousContactInfo = Nothing
        If callIndex.TryGetValue(upperCall, info) Then
            info.Count += 1
            info.LastDate = dateOn
            info.LastBand = band
            info.LastMode = mode
            If Not String.IsNullOrEmpty(name) Then info.LastName = name
            If Not String.IsNullOrEmpty(qth) Then info.LastQTH = qth
        Else
            info = New PreviousContactInfo() With {
                .Count = 1,
                .LastDate = dateOn,
                .LastBand = band,
                .LastMode = mode,
                .LastName = name,
                .LastQTH = qth
            }
            callIndex(upperCall) = info
        End If
    End Sub

    ''' <summary>
    ''' Look up a call sign in the index and display/announce previous contact info.
    ''' Auto-fills Name if the Name field is currently empty.
    ''' </summary>
    Private Sub ShowPreviousContact(callSign As String)
        If String.IsNullOrEmpty(callSign) Then
            ClearPreviousContact()
            Return
        End If

        Dim info As PreviousContactInfo = Nothing
        If Not callIndex.TryGetValue(callSign.Trim().ToUpper(), info) Then
            ClearPreviousContact()
            Return
        End If

        ' Build display text: "3 QSOs. Last: 01/15/2026, 40m CW, Bob"
        Dim parts As New List(Of String)
        parts.Add(info.Count & If(info.Count = 1, " QSO", " QSOs"))

        Dim lastParts As New List(Of String)
        If Not String.IsNullOrEmpty(info.LastDate) Then lastParts.Add(info.LastDate)
        If Not String.IsNullOrEmpty(info.LastBand) Then lastParts.Add(info.LastBand)
        If Not String.IsNullOrEmpty(info.LastMode) Then lastParts.Add(info.LastMode)
        If Not String.IsNullOrEmpty(info.LastName) Then lastParts.Add(info.LastName)

        Dim displayText As String
        If lastParts.Count > 0 Then
            displayText = String.Join(". ", parts) & ". Last: " & String.Join(", ", lastParts)
        Else
            displayText = String.Join(". ", parts)
        End If

        PreviousContactBox.Text = displayText
        PreviousContactBox.AccessibleName = "Previous contact: " & displayText

        ' Build screen reader announcement.
        Dim announcement = "Previously worked, " & info.Count & If(info.Count = 1, " contact", " contacts")
        If lastParts.Count > 0 Then
            announcement &= ". Last: " & String.Join(", ", lastParts)
        End If
        ScreenReaderOutput.Speak(announcement, True)

        ' Auto-fill Name if our field is empty and we have a name from the last QSO.
        If String.IsNullOrEmpty(NameBox.Text.Trim()) AndAlso
           Not String.IsNullOrEmpty(info.LastName) Then
            NameBox.Text = info.LastName
        End If

        ' Auto-fill QTH if our field is empty and we have one.
        If String.IsNullOrEmpty(QTHBox.Text.Trim()) AndAlso
           Not String.IsNullOrEmpty(info.LastQTH) Then
            QTHBox.Text = info.LastQTH
        End If
    End Sub

    ''' <summary>
    ''' Clear the previous contact display.
    ''' </summary>
    Private Sub ClearPreviousContact()
        PreviousContactBox.Text = ""
        PreviousContactBox.AccessibleName = "Previous contact: none"
    End Sub

#End Region

#Region "Recent QSOs Grid"

    ''' <summary>
    ''' Load the last N QSOs from the log file into the grid.
    ''' Also builds the call sign index for instant previous-contact lookup.
    ''' Strategy: SeekToFirst, walk forward collecting ALL records. Keep last
    ''' MaxRecentQSOs for the grid; index every record by call sign.
    ''' </summary>
    Private Sub LoadRecentQSOs()
        RecentGrid.Rows.Clear()
        callIndex.Clear()
        If session Is Nothing Then Return

        ' Walk forward from first record.
        Dim records As New List(Of Dictionary(Of String, String))
        Try
            If Not session.SeekToFirst() Then Return  ' Empty log (no records after header).

            While Not session.EOF
                If Not session.NextRecord() Then Exit While

                Dim recCall = session.GetFieldText(AdifTags.ADIF_Call, False)
                Dim recDate = session.GetFieldText(AdifTags.ADIF_DateOn, False)
                Dim recTime = session.GetFieldText(AdifTags.ADIF_TimeOn, False)
                Dim recMode = session.GetFieldText(AdifTags.ADIF_Mode, False)
                Dim recFreq = session.GetFieldText(AdifTags.ADIF_RXFreq, False)
                Dim recBand = session.GetFieldText(AdifTags.ADIF_Band, False)
                Dim recRSTSent = session.GetFieldText(AdifTags.ADIF_HisRST, False)
                Dim recRSTRcvd = session.GetFieldText(AdifTags.ADIF_MyRST, False)
                Dim recName = session.GetFieldText(AdifTags.ADIF_Name, False)
                Dim recQTH = session.GetFieldText(AdifTags.ADIF_QTH, False)

                ' Update call sign index (every record).
                UpdateCallIndex(recCall, recDate, recBand, recMode, recName, recQTH)

                ' Keep record for grid display (ring buffer of last MaxRecentQSOs).
                Dim rec As New Dictionary(Of String, String) From {
                    {"TIME", recTime},
                    {"CALL", recCall},
                    {"MODE", recMode},
                    {"FREQ", recFreq},
                    {"RST_SENT", recRSTSent},
                    {"RST_RCVD", recRSTRcvd},
                    {"NAME", recName}
                }
                records.Add(rec)
                If records.Count > MaxRecentQSOs Then
                    records.RemoveAt(0)
                End If
            End While
        Catch ex As Exception
            Tracing.TraceLine("LogPanel.LoadRecentQSOs: " & ex.Message,
                              Diagnostics.TraceLevel.Warning)
        End Try

        ' Populate grid (records are already in chronological order, oldest first).
        For Each rec In records
            RecentGrid.Rows.Add(
                FormatTimeForGrid(rec("TIME")),
                rec("CALL"),
                rec("MODE"),
                rec("FREQ"),
                rec("RST_SENT"),
                rec("RST_RCVD"),
                rec("NAME"))
        Next

        ScrollToLastRow()

        ' Update accessible name with row count.
        Dim rowCount = RecentGrid.Rows.Count
        If rowCount > 0 Then
            RecentGrid.AccessibleName = "Recent QSOs, " & rowCount & " entries"
        Else
            RecentGrid.AccessibleName = "Recent QSOs, no entries"
        End If

        Tracing.TraceLine("LogPanel.LoadRecentQSOs: " & records.Count & " grid rows, " &
                          callIndex.Count & " unique calls indexed",
                          Diagnostics.TraceLevel.Info)
    End Sub

    ''' <summary>
    ''' Add a single QSO row to the bottom of the grid (called after successful write).
    ''' Trims oldest row if we exceed MaxRecentQSOs.
    ''' </summary>
    Private Sub AddQSOToGrid(timeOn As String, callSign As String, mode As String,
                              freq As String, rstSent As String, rstRcvd As String,
                              name As String)
        ' Remove oldest row if at capacity.
        If RecentGrid.Rows.Count >= MaxRecentQSOs Then
            RecentGrid.Rows.RemoveAt(0)
        End If

        RecentGrid.Rows.Add(
            FormatTimeForGrid(timeOn),
            callSign,
            mode,
            freq,
            rstSent,
            rstRcvd,
            name)

        ScrollToLastRow()

        ' Update accessible name with new count.
        Dim rowCount = RecentGrid.Rows.Count
        RecentGrid.AccessibleName = "Recent QSOs, " & rowCount & " entries"
    End Sub

    ''' <summary>
    ''' Scroll the grid to show the last (most recent) row.
    ''' </summary>
    Private Sub ScrollToLastRow()
        If RecentGrid.Rows.Count > 0 Then
            RecentGrid.FirstDisplayedScrollingRowIndex = RecentGrid.Rows.Count - 1
        End If
    End Sub

    ''' <summary>
    ''' Format a time string (HHmm) for grid display as HH:mm.
    ''' </summary>
    Private Shared Function FormatTimeForGrid(timeStr As String) As String
        If String.IsNullOrEmpty(timeStr) OrElse timeStr.Length < 4 Then Return timeStr
        Return timeStr.Substring(0, 2) & ":" & timeStr.Substring(2, 2)
    End Function

#End Region

#Region "Field Helpers"

    Private Sub ClearFields()
        CallSignBox.Text = ""
        RSTSentBox.Text = ""
        RSTRcvdBox.Text = ""
        NameBox.Text = ""
        QTHBox.Text = ""
        StateBox.Text = ""
        GridBox.Text = ""
        CommentsBox.Text = ""
        FreqLabel.Text = "Freq: ---"
        ModeLabel.Text = "Mode: ---"
        BandLabel.Text = "Band: ---"
        DateTimeLabel.Text = "UTC: ---"
        DupLabel.Text = "Dup: 0"
    End Sub

    Private Sub SetFieldByTag(tag As String, value As String)
        Select Case tag
            Case AdifTags.ADIF_Call : CallSignBox.Text = value
            Case AdifTags.ADIF_HisRST : RSTSentBox.Text = value
            Case AdifTags.ADIF_MyRST : RSTRcvdBox.Text = value
            Case AdifTags.ADIF_Name : NameBox.Text = value
            Case AdifTags.ADIF_QTH : QTHBox.Text = value
            Case AdifTags.ADIF_State : StateBox.Text = value
            Case AdifTags.ADIF_Grid : GridBox.Text = value
            Case AdifTags.ADIF_Comment : CommentsBox.Text = value
        End Select
    End Sub

    ''' <summary>
    ''' Copy panel TextBox values into the LogSession's field dictionary.
    ''' </summary>
    Private Sub FieldsToSession()
        If session Is Nothing Then Return
        session.SetFieldText(AdifTags.ADIF_Call, CallSignBox.Text.Trim().ToUpper())
        session.SetFieldText(AdifTags.ADIF_HisRST, RSTSentBox.Text.Trim())
        session.SetFieldText(AdifTags.ADIF_MyRST, RSTRcvdBox.Text.Trim())
        session.SetFieldText(AdifTags.ADIF_Name, NameBox.Text.Trim())
        session.SetFieldText(AdifTags.ADIF_QTH, QTHBox.Text.Trim())
        session.SetFieldText(AdifTags.ADIF_State, StateBox.Text.Trim().ToUpper())
        session.SetFieldText(AdifTags.ADIF_Grid, GridBox.Text.Trim().ToUpper())
        session.SetFieldText(AdifTags.ADIF_Comment, CommentsBox.Text.Trim())
    End Sub

    ''' <summary>
    ''' Auto-fill frequency, mode, band from the connected radio.
    ''' </summary>
    Private Sub AutoFillFromRadio()
        If RigControl Is Nothing OrElse Not Power Then
            FreqLabel.Text = "Freq: no radio"
            ModeLabel.Text = "Mode: ---"
            BandLabel.Text = "Band: ---"
            Return
        End If

        ' Frequency
        Dim rxFreq = RigControl.RXFrequency
        Dim txFreq = RigControl.TXFrequency
        FreqLabel.Text = "Freq: " & FormatFreq(rxFreq) & " MHz"

        ' Mode — normalize for ADIF
        Dim modeText = ""
        If RigControl.Mode IsNot Nothing Then
            modeText = RigControl.Mode.ToString().ToUpper()
            Select Case modeText
                Case "LSB", "USB" : modeText = "SSB"
                Case "CWR" : modeText = "CW"
                Case "FSK", "FSKR" : modeText = "RTTY"
            End Select
        End If
        ModeLabel.Text = "Mode: " & modeText

        ' Band
        Dim bandItem = HamBands.Bands.Query(txFreq)
        Dim bandText = If(bandItem IsNot Nothing, bandItem.Name, "---")
        BandLabel.Text = "Band: " & bandText

        ' Push to session for dup checking and log write.
        If session IsNot Nothing Then
            session.SetFieldText(AdifTags.ADIF_RXFreq, FormatFreq(rxFreq))
            session.SetFieldText(AdifTags.ADIF_TXFreq, FormatFreq(txFreq))
            session.SetFieldText(AdifTags.ADIF_Mode, modeText)
            If bandItem IsNot Nothing Then
                session.SetFieldText(AdifTags.ADIF_Band, bandText)
            End If
        End If

        ' Date/Time
        Dim dt = Date.UtcNow
        DateTimeLabel.Text = "UTC: " & dt.ToString("yyyy-MM-dd HH:mm")
        If session IsNot Nothing Then
            If String.IsNullOrEmpty(session.GetFieldText(AdifTags.ADIF_DateOn)) Then
                session.SetFieldText(AdifTags.ADIF_DateOn, dt.ToString("MM/dd/yyyy"))
                session.SetFieldText(AdifTags.ADIF_TimeOn, dt.ToString("HHmm"))
            End If
        End If
    End Sub

#End Region

#Region "Validation"

    ''' <summary>
    ''' Check for missing required fields. Returns a list of human-readable
    ''' field names that the operator still needs to fill in.
    ''' For a basic (non-contest) QSO: frequency, mode, RST sent, RST received.
    ''' Call sign is checked separately before this is called.
    ''' </summary>
    Private Function GetMissingFields() As List(Of String)
        Dim missing As New List(Of String)

        ' Frequency — comes from the radio via AutoFillFromRadio → session.
        If session IsNot Nothing Then
            Dim freq = session.GetFieldText(AdifTags.ADIF_RXFreq)
            If String.IsNullOrEmpty(freq) OrElse freq = "0" Then
                missing.Add("Frequency")
            End If
        Else
            missing.Add("Frequency")
        End If

        ' Mode — comes from the radio via AutoFillFromRadio → session.
        If session IsNot Nothing Then
            Dim modeVal = session.GetFieldText(AdifTags.ADIF_Mode)
            If String.IsNullOrEmpty(modeVal) Then
                missing.Add("Mode")
            End If
        Else
            missing.Add("Mode")
        End If

        ' RST Sent — user-entered field.
        If String.IsNullOrEmpty(RSTSentBox.Text.Trim()) Then
            missing.Add("RST Sent")
        End If

        ' RST Received — user-entered field.
        If String.IsNullOrEmpty(RSTRcvdBox.Text.Trim()) Then
            missing.Add("RST Received")
        End If

        Return missing
    End Function

    ''' <summary>
    ''' Returns True if the operator has started entering a QSO (call sign is
    ''' non-empty) but hasn't saved it yet. Used by Form1 to prompt before close.
    ''' </summary>
    Friend Function HasUnsavedEntry() As Boolean
        Return Not String.IsNullOrEmpty(CallSignBox.Text.Trim())
    End Function

    ''' <summary>
    ''' Prompt the user to save or discard an in-progress QSO.
    ''' Returns True if OK to proceed (saved or discarded), False to cancel.
    ''' </summary>
    Friend Function PromptSaveBeforeClose() As Boolean
        If Not HasUnsavedEntry() Then Return True

        Dim callText = CallSignBox.Text.Trim().ToUpper()

        ' Build the prompt — include missing field info so the user knows
        ' what's needed before they click Yes.
        Dim prompt = "You have an unsaved log entry for " & callText & "."
        Dim missing = GetMissingFields()
        If missing.Count > 0 Then
            prompt &= vbCrLf & "Note: " & String.Join(", ", missing) & " must be filled before save."
        End If
        prompt &= vbCrLf & "Do you want to save it before closing?"

        Dim result = MessageBox.Show(
            prompt,
            "Unsaved QSO",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question)

        Select Case result
            Case DialogResult.Yes
                ' Try to save — if validation fails, cancel the close.
                Dim ok = WriteEntry()
                If Not ok Then Return False
                Return True
            Case DialogResult.No
                ' Discard the entry.
                Return True
            Case Else
                ' Cancel — don't close.
                Return False
        End Select
    End Function

#End Region

#Region "Event Handlers"

    Private Sub CallSignBox_Leave(sender As Object, e As EventArgs) Handles CallSignBox.Leave
        Dim callText = CallSignBox.Text.Trim().ToUpper()
        If String.IsNullOrEmpty(callText) Then Return
        CallSignBox.Text = callText  ' Normalize to uppercase

        ' Auto-fill radio data on first field exit.
        AutoFillFromRadio()

        ' Dup check.
        CheckDup(callText <> currentCall)

        ' Previous contact lookup (always, even if dup checking is off).
        If callText <> currentCall Then
            ShowPreviousContact(callText)
        End If

        ' Callbook lookup (async — fills empty fields when results arrive).
        ' Runs after local lookups so local data takes priority.
        TriggerCallbookLookup(callText)

        currentCall = callText
    End Sub

    Private Sub CheckDup(callChanged As Boolean)
        If Not isDupChecking OrElse session Is Nothing Then Return

        ' Push current values to session for dup key construction.
        session.SetFieldText(AdifTags.ADIF_Call, CallSignBox.Text.Trim().ToUpper())
        session.SetFieldText(AdifTags.ADIF_Mode, session.GetFieldText(AdifTags.ADIF_Mode))
        session.SetFieldText(AdifTags.ADIF_Band, session.GetFieldText(AdifTags.ADIF_Band))

        Dim key = New LogDupChecking.keyElement(session, DupType)
        Dim ct = Dups.DupTest(key)
        ct += 1  ' Count the current entry being worked.
        DupLabel.Text = "Dup: " & CStr(ct)
        isDup = (ct > 1)
        If isDup Then
            SystemSounds.Exclamation.Play()
            ScreenReaderOutput.Speak("Duplicate, " & ct & " previous contacts", True)
        End If
    End Sub

    ''' <summary>
    ''' When the grid receives focus, announce the row count so the operator
    ''' knows how many QSOs are shown before they start arrowing through.
    ''' </summary>
    Private Sub RecentGrid_Enter(sender As Object, e As EventArgs) Handles RecentGrid.Enter
        Dim rowCount = RecentGrid.Rows.Count
        If rowCount > 0 Then
            ScreenReaderOutput.Speak("Recent QSOs, " & rowCount & " entries", True)
        Else
            ScreenReaderOutput.Speak("Recent QSOs, empty", True)
        End If
    End Sub

#End Region

#Region "UI Construction"

    ''' <summary>
    ''' Build all controls programmatically. No designer file needed.
    ''' Layout: EntryPanel (top, fixed height) + GridPanel (bottom, fills remaining space).
    ''' </summary>
    Private Sub BuildControls()
        Me.SuspendLayout()

        ' --- Top panel for entry fields and radio info (fixed height) ---
        EntryPanel = New Panel() With {
            .Dock = DockStyle.Top,
            .Height = 320,
            .AutoScroll = False
        }
        Me.Controls.Add(EntryPanel)

        ' --- Entry fields (inside EntryPanel) ---
        Dim yPos As Integer = 8
        Const labelWidth As Integer = 75
        Const fieldWidth As Integer = 150
        Const fieldHeight As Integer = 22
        Const rowSpacing As Integer = 28
        Const labelX As Integer = 4
        Const fieldX As Integer = 82

        CallSignBox = AddEntryField(EntryPanel, "Call Sign", yPos, labelX, fieldX, labelWidth, fieldWidth, fieldHeight)
        yPos += rowSpacing

        ' RST side by side
        RSTSentBox = AddEntryField(EntryPanel, "RST Sent", yPos, labelX, fieldX, labelWidth, 60, fieldHeight)
        Dim rstRcvdLabel = AddLabel(EntryPanel, "RST Rcvd", fieldX + 68, yPos + 3, 60)
        RSTRcvdBox = AddTextBox(EntryPanel, "RST Received", fieldX + 130, yPos, 60, fieldHeight)
        ' Dup display on same row
        DupLabel = AddLabel(EntryPanel, "Dup: 0", fieldX + 200, yPos + 3, 80)
        DupLabel.AccessibleName = "Duplicate count"
        yPos += rowSpacing

        NameBox = AddEntryField(EntryPanel, "Name", yPos, labelX, fieldX, labelWidth, fieldWidth, fieldHeight)
        yPos += rowSpacing

        QTHBox = AddEntryField(EntryPanel, "QTH", yPos, labelX, fieldX, labelWidth, fieldWidth, fieldHeight)
        yPos += rowSpacing

        ' State and Grid side by side
        StateBox = AddEntryField(EntryPanel, "State", yPos, labelX, fieldX, labelWidth, 50, fieldHeight)
        Dim gridLabel = AddLabel(EntryPanel, "Grid", fieldX + 58, yPos + 3, 35)
        GridBox = AddTextBox(EntryPanel, "Grid Square", fieldX + 95, yPos, 70, fieldHeight)
        yPos += rowSpacing

        CommentsBox = AddEntryField(EntryPanel, "Comments", yPos, labelX, fieldX, labelWidth, 250, fieldHeight)
        yPos += rowSpacing + 4

        ' --- Previous contact info (read-only, tabbable so screen reader can reach it) ---
        Dim prevLabel = AddLabel(EntryPanel, "Previous", labelX, yPos + 3, labelWidth)
        PreviousContactBox = New TextBox() With {
            .Location = New Point(fieldX, yPos),
            .Size = New Size(300, fieldHeight),
            .ReadOnly = True,
            .BorderStyle = BorderStyle.None,
            .BackColor = SystemColors.Control,
            .TabStop = True,
            .AccessibleName = "Previous contact: none",
            .AccessibleRole = AccessibleRole.StaticText
        }
        EntryPanel.Controls.Add(PreviousContactBox)
        yPos += rowSpacing

        ' --- Read-only radio info ---
        Dim infoY = yPos
        FreqLabel = AddLabel(EntryPanel, "Freq: ---", labelX, infoY, 200)
        FreqLabel.AccessibleName = "Frequency"
        infoY += 18
        ModeLabel = AddLabel(EntryPanel, "Mode: ---", labelX, infoY, 100)
        ModeLabel.AccessibleName = "Mode"
        BandLabel = AddLabel(EntryPanel, "Band: ---", labelX + 110, infoY, 100)
        BandLabel.AccessibleName = "Band"
        infoY += 18
        DateTimeLabel = AddLabel(EntryPanel, "UTC: ---", labelX, infoY, 200)
        DateTimeLabel.AccessibleName = "Date and Time UTC"
        infoY += 18
        SerialLabel = AddLabel(EntryPanel, "Serial: 0", labelX, infoY, 100)
        SerialLabel.AccessibleName = "Serial number"

        ' Tab order for entry fields.
        CallSignBox.TabIndex = 0
        RSTSentBox.TabIndex = 1
        RSTRcvdBox.TabIndex = 2
        NameBox.TabIndex = 3
        QTHBox.TabIndex = 4
        StateBox.TabIndex = 5
        GridBox.TabIndex = 6
        CommentsBox.TabIndex = 7
        PreviousContactBox.TabIndex = 8

        ' --- Bottom panel for the Recent QSOs grid (fills remaining space) ---
        GridPanel = New Panel() With {
            .Dock = DockStyle.Fill,
            .Padding = New Padding(4, 4, 4, 4)
        }
        Me.Controls.Add(GridPanel)

        ' Grid label
        Dim gridHeaderLabel = New Label() With {
            .Text = "Recent QSOs",
            .Dock = DockStyle.Top,
            .Height = 18,
            .AutoSize = False,
            .Font = New Font(Me.Font.FontFamily, 9, FontStyle.Bold)
        }
        GridPanel.Controls.Add(gridHeaderLabel)

        ' Build the DataGridView.
        BuildRecentGrid()
        GridPanel.Controls.Add(RecentGrid)

        ' Ensure correct Z-order: GridPanel (Fill) must be added BEFORE EntryPanel (Top)
        ' so that Fill works correctly. WinForms docking order depends on Z-order.
        GridPanel.BringToFront()

        Me.ResumeLayout(False)
    End Sub

    ''' <summary>
    ''' Build the Recent QSOs DataGridView with columns and UIA-friendly settings.
    ''' </summary>
    Private Sub BuildRecentGrid()
        RecentGrid = New DataGridView() With {
            .Name = "RecentQSOsGrid",
            .Dock = DockStyle.Fill,
            .ReadOnly = True,
            .AllowUserToAddRows = False,
            .AllowUserToDeleteRows = False,
            .AllowUserToResizeRows = False,
            .AllowUserToOrderColumns = False,
            .RowHeadersVisible = False,
            .SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            .MultiSelect = False,
            .AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            .BackgroundColor = SystemColors.Window,
            .BorderStyle = BorderStyle.FixedSingle,
            .StandardTab = True,
            .AccessibleName = "Recent QSOs",
            .AccessibleRole = AccessibleRole.Table
        }

        ' Disable editing.
        RecentGrid.EditMode = DataGridViewEditMode.EditProgrammatically

        ' Columns: Time UTC, Call, Mode, Freq, RST Sent, RST Rcvd, Name
        ' Note: DataGridView uses 0-based row indices in its accessibility provider.
        ' NVDA/JAWS will announce "row 0" for the first data row. This is a WinForms
        ' limitation — would require a custom AccessibleObject to override.
        Dim colTime As New DataGridViewTextBoxColumn() With {
            .Name = "colTime",
            .HeaderText = "Time UTC",
            .FillWeight = 12,
            .SortMode = DataGridViewColumnSortMode.NotSortable
        }
        colTime.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleLeft

        Dim colCall As New DataGridViewTextBoxColumn() With {
            .Name = "colCall",
            .HeaderText = "Call",
            .FillWeight = 18,
            .SortMode = DataGridViewColumnSortMode.NotSortable
        }

        Dim colMode As New DataGridViewTextBoxColumn() With {
            .Name = "colMode",
            .HeaderText = "Mode",
            .FillWeight = 10,
            .SortMode = DataGridViewColumnSortMode.NotSortable
        }

        Dim colFreq As New DataGridViewTextBoxColumn() With {
            .Name = "colFreq",
            .HeaderText = "Freq",
            .FillWeight = 18,
            .SortMode = DataGridViewColumnSortMode.NotSortable
        }

        Dim colRSTSent As New DataGridViewTextBoxColumn() With {
            .Name = "colRSTSent",
            .HeaderText = "RST Sent",
            .FillWeight = 12,
            .SortMode = DataGridViewColumnSortMode.NotSortable
        }

        Dim colRSTRcvd As New DataGridViewTextBoxColumn() With {
            .Name = "colRSTRcvd",
            .HeaderText = "RST Rcvd",
            .FillWeight = 12,
            .SortMode = DataGridViewColumnSortMode.NotSortable
        }

        Dim colName As New DataGridViewTextBoxColumn() With {
            .Name = "colName",
            .HeaderText = "Name",
            .FillWeight = 18,
            .SortMode = DataGridViewColumnSortMode.NotSortable
        }

        RecentGrid.Columns.AddRange(colTime, colCall, colMode, colFreq,
                                     colRSTSent, colRSTRcvd, colName)

        ' The grid is one Tab stop after the previous contact box.
        RecentGrid.TabIndex = 9
    End Sub

    Private Function AddEntryField(parent As Panel, labelText As String,
                                    y As Integer, labelX As Integer, fieldX As Integer,
                                    labelWidth As Integer, fieldWidth As Integer,
                                    fieldHeight As Integer) As TextBox
        Dim lbl = AddLabel(parent, labelText, labelX, y + 3, labelWidth)
        Dim tb = AddTextBox(parent, labelText, fieldX, y, fieldWidth, fieldHeight)
        Return tb
    End Function

    Private Function AddLabel(parent As Panel, text As String, x As Integer, y As Integer, w As Integer) As Label
        Dim lbl As New Label() With {
            .Text = text,
            .Location = New Point(x, y),
            .Size = New Size(w, 16),
            .AutoSize = False
        }
        parent.Controls.Add(lbl)
        Return lbl
    End Function

    Private Function AddTextBox(parent As Panel, accessibleName As String, x As Integer, y As Integer,
                                 w As Integer, h As Integer) As TextBox
        Dim tb As New TextBox() With {
            .Location = New Point(x, y),
            .Size = New Size(w, h),
            .AccessibleName = accessibleName,
            .AccessibleRole = AccessibleRole.Text
        }
        parent.Controls.Add(tb)
        Return tb
    End Function

#End Region

#Region "Key Handling"

    ''' <summary>
    ''' Handle Enter key to log the current QSO.
    ''' Intercepts at the command-key level so it works from any field in the panel.
    ''' </summary>
    Protected Overrides Function ProcessCmdKey(ByRef msg As Message, keyData As Keys) As Boolean
        ' Enter — save the QSO.
        If keyData = Keys.Enter OrElse keyData = Keys.Return Then
            If session IsNot Nothing AndAlso
               Not String.IsNullOrEmpty(CallSignBox.Text.Trim()) Then
                WriteEntry()
                Return True
            End If
        End If

        ' Escape — clear the form (confirm if data has been entered).
        If keyData = Keys.Escape Then
            If HasUnsavedEntry() Then
                ' If the operator suppressed the confirmation, just clear silently.
                If CurrentOp IsNot Nothing AndAlso CurrentOp.SuppressClearConfirm Then
                    NewEntry()
                    ScreenReaderOutput.Speak("Entry cleared", True)
                Else
                    ' Show a TaskDialog with a "Don't ask me again" checkbox.
                    Dim callText = CallSignBox.Text.Trim().ToUpper()
                    Dim page As New TaskDialogPage() With {
                        .Caption = "Clear Entry",
                        .Heading = "Clear the log entry for " & callText & "?",
                        .Icon = TaskDialogIcon.Information,
                        .AllowCancel = True
                    }
                    page.Verification = New TaskDialogVerificationCheckBox("Don't ask me again")
                    page.Buttons.Add(TaskDialogButton.Yes)
                    page.Buttons.Add(TaskDialogButton.No)

                    Dim clicked = TaskDialog.ShowDialog(Me.FindForm(), page)

                    If clicked = TaskDialogButton.Yes Then
                        ' Save the "don't ask" preference if checked.
                        If page.Verification.Checked Then
                            If CurrentOp IsNot Nothing Then
                                CurrentOp.SuppressClearConfirm = True
                                Operators.UpdateCurrentOp()
                            End If
                        End If
                        NewEntry()
                        ScreenReaderOutput.Speak("Entry cleared", True)
                    End If
                End If
            Else
                ' Nothing to clear — just announce it.
                ScreenReaderOutput.Speak("Entry is empty", True)
            End If
            Return True
        End If

        Return MyBase.ProcessCmdKey(msg, keyData)
    End Function

#End Region

End Class
