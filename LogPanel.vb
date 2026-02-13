Imports System.Drawing
Imports System.Windows.Forms
Imports System.Windows.Forms.Integration
Imports adif
Imports JJLogLib
Imports JJTrace
Imports JJCountriesDB
Imports System.Media
Imports Radios

''' <summary>
''' Quick-entry logging panel for Logging Mode.
''' Embedded in Form1 via SplitContainer. Thin wrapper over LogSession —
''' manages its own entry fields and talks to LogSession via SetFieldText/GetFieldText.
'''
''' Layout: WPF LogEntryControl hosted via ElementHost. Entry fields at top,
''' Recent QSOs DataGrid below. WPF provides native UI Automation — JAWS/NVDA
''' get proper "label for" relationships and DataGrid announces row/column headers.
'''
''' Previous Contact lookup: when the operator tabs out of the Call Sign field,
''' we check the in-memory call index (built during log scan) and show the last
''' QSO with that station plus total contact count. Auto-fills Name if empty.
''' </summary>
Friend Class LogPanel
    Inherits UserControl

    ' --- WPF hosted control ---
    Private wpfHost As ElementHost
    Private wpfControl As JJFlexWpf.LogEntryControl

    ' --- State ---
    Private session As LogSession
    Private currentCall As String = ""
    Private isDup As Boolean = False

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
            wpfControl.SetSerialLabel("Serial: " & CStr(session.serial))
        End If
        wpfControl.SetDupLabel("Dup: 0")
        isDup = False
        currentCall = ""
        lastLookedUpCall = ""
        ClearPreviousContact()
        FocusCallSign()
    End Sub

    ''' <summary>
    ''' Pre-fill fields from a Station Lookup result.
    ''' Called when the user clicks "Log Contact" in Station Lookup.
    ''' Clears current entry first, then populates fields and focuses RST Sent.
    ''' </summary>
    Friend Sub PreFillFromLookup(callSign As String, name As String,
                                  qth As String, state As String, grid As String)
        NewEntry()  ' Clear and reset

        If Not String.IsNullOrEmpty(callSign) Then
            wpfControl.SetFieldText("CALL", callSign.Trim().ToUpper())
        End If
        If Not String.IsNullOrEmpty(name) Then
            wpfControl.SetFieldText("NAME", name)
        End If
        If Not String.IsNullOrEmpty(qth) Then
            wpfControl.SetFieldText("QTH", qth)
        End If
        If Not String.IsNullOrEmpty(state) Then
            wpfControl.SetFieldText("STATE", state)
        End If
        If Not String.IsNullOrEmpty(grid) Then
            wpfControl.SetFieldText("GRID", grid)
        End If

        ' Trigger dup check and previous contact lookup for the pre-filled call.
        currentCall = callSign.Trim().ToUpper()
        ShowPreviousContact(currentCall)
        CheckDup(True)

        ' Focus RST Sent (the next logical field after call sign pre-fill).
        wpfControl.FocusField("RSTSENT")
    End Sub

    ''' <summary>
    ''' Write the current entry to the log file.
    ''' Returns True on success.
    ''' </summary>
    Friend Function WriteEntry() As Boolean
        If session Is Nothing Then Return False
        Dim callText = wpfControl.GetFieldText("CALL").Trim().ToUpper()
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
        If String.IsNullOrEmpty(wpfControl.GetFieldText("CALL").Trim()) Then
            preservedFields = Nothing
            Return Nothing
        End If
        Dim state As New Dictionary(Of String, String) From {
            {AdifTags.ADIF_Call, wpfControl.GetFieldText("CALL")},
            {AdifTags.ADIF_HisRST, wpfControl.GetFieldText("RSTSENT")},
            {AdifTags.ADIF_MyRST, wpfControl.GetFieldText("RSTRCVD")},
            {AdifTags.ADIF_Name, wpfControl.GetFieldText("NAME")},
            {AdifTags.ADIF_QTH, wpfControl.GetFieldText("QTH")},
            {AdifTags.ADIF_State, wpfControl.GetFieldText("STATE")},
            {AdifTags.ADIF_Grid, wpfControl.GetFieldText("GRID")},
            {AdifTags.ADIF_Comment, wpfControl.GetFieldText("COMMENTS")}
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
            wpfControl.SetSerialLabel("Serial: " & CStr(session.serial))
        End If
        preservedFields = Nothing
        FocusCallSign()
    End Sub

    ''' <summary>
    ''' Focus the call sign field.
    ''' </summary>
    Friend Sub FocusCallSign()
        wpfHost.Focus()
        wpfControl.FocusCallSign()
    End Sub

    ''' <summary>
    ''' Focus a specific entry field by name. Used by Form1 to handle
    ''' Logging Mode field-jump hotkeys (Alt+C, Alt+R, etc.).
    ''' </summary>
    Friend Sub FocusField(fieldName As String)
        wpfHost.Focus()
        wpfControl.FocusField(fieldName)
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
        FillIfEmpty("NAME", result.Name)
        FillIfEmpty("QTH", result.QTH)
        FillIfEmpty("STATE", result.State)
        FillIfEmpty("GRID", result.Grid)

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
        If parts.Count > 0 Then
            ScreenReaderOutput.Speak(String.Join(", ", parts), False)
        End If
    End Sub

    ''' <summary>
    ''' Fill a field via WPF control only if it's currently empty and the value is non-empty.
    ''' Returns True if the field was filled.
    ''' </summary>
    Private Function FillIfEmpty(fieldName As String, value As String) As Boolean
        If String.IsNullOrEmpty(value) Then Return False
        If Not String.IsNullOrEmpty(wpfControl.GetFieldText(fieldName).Trim()) Then Return False
        wpfControl.SetFieldText(fieldName, value)
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

        wpfControl.SetPreviousContact(displayText, "Previous contact: " & displayText)

        ' Build screen reader announcement.
        Dim announcement = "Previously worked, " & info.Count & If(info.Count = 1, " contact", " contacts")
        If lastParts.Count > 0 Then
            announcement &= ". Last: " & String.Join(", ", lastParts)
        End If
        ScreenReaderOutput.Speak(announcement, True)

        ' Auto-fill Name if our field is empty and we have a name from the last QSO.
        If String.IsNullOrEmpty(wpfControl.GetFieldText("NAME").Trim()) AndAlso
           Not String.IsNullOrEmpty(info.LastName) Then
            wpfControl.SetFieldText("NAME", info.LastName)
        End If

        ' Auto-fill QTH if our field is empty and we have one.
        If String.IsNullOrEmpty(wpfControl.GetFieldText("QTH").Trim()) AndAlso
           Not String.IsNullOrEmpty(info.LastQTH) Then
            wpfControl.SetFieldText("QTH", info.LastQTH)
        End If
    End Sub

    ''' <summary>
    ''' Clear the previous contact display.
    ''' </summary>
    Private Sub ClearPreviousContact()
        wpfControl.ClearPreviousContact()
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
        wpfControl.ClearGrid()
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
            wpfControl.AddQsoRow(New JJFlexWpf.RecentQsoRow(
                FormatTimeForGrid(rec("TIME")),
                rec("CALL"),
                rec("MODE"),
                rec("FREQ"),
                rec("RST_SENT"),
                rec("RST_RCVD"),
                rec("NAME")))
        Next

        ' Update accessible name with row count.
        Dim rowCount = wpfControl.GridRowCount
        If rowCount > 0 Then
            wpfControl.SetGridAccessibleName("Recent QSOs, " & rowCount & " entries")
        Else
            wpfControl.SetGridAccessibleName("Recent QSOs, no entries")
        End If

        Tracing.TraceLine("LogPanel.LoadRecentQSOs: " & records.Count & " grid rows, " &
                          callIndex.Count & " unique calls indexed",
                          Diagnostics.TraceLevel.Info)
    End Sub

    ''' <summary>
    ''' Add a single QSO row to the bottom of the grid (called after successful write).
    ''' </summary>
    Private Sub AddQSOToGrid(timeOn As String, callSign As String, mode As String,
                              freq As String, rstSent As String, rstRcvd As String,
                              name As String)
        wpfControl.AddQsoRow(New JJFlexWpf.RecentQsoRow(
            FormatTimeForGrid(timeOn),
            callSign,
            mode,
            freq,
            rstSent,
            rstRcvd,
            name))

        ' Update accessible name with new count.
        Dim rowCount = wpfControl.GridRowCount
        wpfControl.SetGridAccessibleName("Recent QSOs, " & rowCount & " entries")
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
        wpfControl.ClearAllFields()
        wpfControl.SetFreqLabel("Freq: ---")
        wpfControl.SetModeLabel("Mode: ---")
        wpfControl.SetBandLabel("Band: ---")
        wpfControl.SetDateTimeLabel("UTC: ---")
        wpfControl.SetDupLabel("Dup: 0")
    End Sub

    Private Sub SetFieldByTag(tag As String, value As String)
        Select Case tag
            Case AdifTags.ADIF_Call : wpfControl.SetFieldText("CALL", value)
            Case AdifTags.ADIF_HisRST : wpfControl.SetFieldText("RSTSENT", value)
            Case AdifTags.ADIF_MyRST : wpfControl.SetFieldText("RSTRCVD", value)
            Case AdifTags.ADIF_Name : wpfControl.SetFieldText("NAME", value)
            Case AdifTags.ADIF_QTH : wpfControl.SetFieldText("QTH", value)
            Case AdifTags.ADIF_State : wpfControl.SetFieldText("STATE", value)
            Case AdifTags.ADIF_Grid : wpfControl.SetFieldText("GRID", value)
            Case AdifTags.ADIF_Comment : wpfControl.SetFieldText("COMMENTS", value)
        End Select
    End Sub

    ''' <summary>
    ''' Copy panel field values into the LogSession's field dictionary.
    ''' </summary>
    Private Sub FieldsToSession()
        If session Is Nothing Then Return
        session.SetFieldText(AdifTags.ADIF_Call, wpfControl.GetFieldText("CALL").Trim().ToUpper())
        session.SetFieldText(AdifTags.ADIF_HisRST, wpfControl.GetFieldText("RSTSENT").Trim())
        session.SetFieldText(AdifTags.ADIF_MyRST, wpfControl.GetFieldText("RSTRCVD").Trim())
        session.SetFieldText(AdifTags.ADIF_Name, wpfControl.GetFieldText("NAME").Trim())
        session.SetFieldText(AdifTags.ADIF_QTH, wpfControl.GetFieldText("QTH").Trim())
        session.SetFieldText(AdifTags.ADIF_State, wpfControl.GetFieldText("STATE").Trim().ToUpper())
        session.SetFieldText(AdifTags.ADIF_Grid, wpfControl.GetFieldText("GRID").Trim().ToUpper())
        session.SetFieldText(AdifTags.ADIF_Comment, wpfControl.GetFieldText("COMMENTS").Trim())
    End Sub

    ''' <summary>
    ''' Auto-fill frequency, mode, band from the connected radio.
    ''' </summary>
    Private Sub AutoFillFromRadio()
        If RigControl Is Nothing OrElse Not Power Then
            wpfControl.SetFreqLabel("Freq: no radio")
            wpfControl.SetModeLabel("Mode: ---")
            wpfControl.SetBandLabel("Band: ---")
            Return
        End If

        ' Frequency
        Dim rxFreq = RigControl.RXFrequency
        Dim txFreq = RigControl.TXFrequency
        wpfControl.SetFreqLabel("Freq: " & FormatFreq(rxFreq) & " MHz")

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
        wpfControl.SetModeLabel("Mode: " & modeText)

        ' Band
        Dim bandItem = HamBands.Bands.Query(txFreq)
        Dim bandText = If(bandItem IsNot Nothing, bandItem.Name, "---")
        wpfControl.SetBandLabel("Band: " & bandText)

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
        wpfControl.SetDateTimeLabel("UTC: " & dt.ToString("yyyy-MM-dd HH:mm"))
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
        If String.IsNullOrEmpty(wpfControl.GetFieldText("RSTSENT").Trim()) Then
            missing.Add("RST Sent")
        End If

        ' RST Received — user-entered field.
        If String.IsNullOrEmpty(wpfControl.GetFieldText("RSTRCVD").Trim()) Then
            missing.Add("RST Received")
        End If

        Return missing
    End Function

    ''' <summary>
    ''' Returns True if the operator has started entering a QSO (call sign is
    ''' non-empty) but hasn't saved it yet. Used by Form1 to prompt before close.
    ''' </summary>
    Friend Function HasUnsavedEntry() As Boolean
        Return Not String.IsNullOrEmpty(wpfControl.GetFieldText("CALL").Trim())
    End Function

    ''' <summary>
    ''' Prompt the user to save or discard an in-progress QSO.
    ''' Returns True if OK to proceed (saved or discarded), False to cancel.
    ''' </summary>
    Friend Function PromptSaveBeforeClose() As Boolean
        If Not HasUnsavedEntry() Then Return True

        Dim callText = wpfControl.GetFieldText("CALL").Trim().ToUpper()

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

    Private Sub OnCallSignLeave(sender As Object, e As EventArgs)
        Dim callText = wpfControl.GetFieldText("CALL").Trim().ToUpper()
        If String.IsNullOrEmpty(callText) Then Return
        wpfControl.SetFieldText("CALL", callText)  ' Normalize to uppercase

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
        session.SetFieldText(AdifTags.ADIF_Call, wpfControl.GetFieldText("CALL").Trim().ToUpper())
        session.SetFieldText(AdifTags.ADIF_Mode, session.GetFieldText(AdifTags.ADIF_Mode))
        session.SetFieldText(AdifTags.ADIF_Band, session.GetFieldText(AdifTags.ADIF_Band))

        Dim key = New LogDupChecking.keyElement(session, DupType)
        Dim ct = Dups.DupTest(key)
        ct += 1  ' Count the current entry being worked.
        wpfControl.SetDupLabel("Dup: " & CStr(ct))
        isDup = (ct > 1)
        If isDup Then
            SystemSounds.Exclamation.Play()
            ScreenReaderOutput.Speak("Duplicate, " & ct & " previous contacts", True)
        End If
    End Sub

    Private Sub OnEnterPressed(sender As Object, e As EventArgs)
        If session IsNot Nothing AndAlso
           Not String.IsNullOrEmpty(wpfControl.GetFieldText("CALL").Trim()) Then
            WriteEntry()
        End If
    End Sub

    Private Sub OnEscapePressed(sender As Object, e As EventArgs)
        If HasUnsavedEntry() Then
            ' If the operator suppressed the confirmation, just clear silently.
            If CurrentOp IsNot Nothing AndAlso CurrentOp.SuppressClearConfirm Then
                NewEntry()
                ScreenReaderOutput.Speak("Entry cleared", True)
            Else
                ' Show a TaskDialog with a "Don't ask me again" checkbox.
                Dim callText = wpfControl.GetFieldText("CALL").Trim().ToUpper()
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
    End Sub

    Private Sub OnRecentGridGotFocus(sender As Object, e As EventArgs)
        Dim rowCount = wpfControl.GridRowCount
        If rowCount > 0 Then
            ScreenReaderOutput.Speak("Recent QSOs, " & rowCount & " entries", True)
        Else
            ScreenReaderOutput.Speak("Recent QSOs, empty", True)
        End If
    End Sub

#End Region

#Region "UI Construction"

    ''' <summary>
    ''' Build the WPF-hosted control via ElementHost.
    ''' </summary>
    Private Sub BuildControls()
        Me.SuspendLayout()

        ' Create the WPF control.
        wpfControl = New JJFlexWpf.LogEntryControl()

        ' Wire WPF events to VB.NET handlers.
        AddHandler wpfControl.CallSignLeave, AddressOf OnCallSignLeave
        AddHandler wpfControl.EnterPressed, AddressOf OnEnterPressed
        AddHandler wpfControl.EscapePressed, AddressOf OnEscapePressed

        ' Create the ElementHost to bridge WPF into WinForms.
        wpfHost = New ElementHost() With {
            .Dock = DockStyle.Fill,
            .Child = wpfControl
        }
        Me.Controls.Add(wpfHost)

        Me.ResumeLayout(False)
    End Sub

#End Region

#Region "Key Handling"

    ''' <summary>
    ''' Handle keys that need to be caught at the WinForms level.
    ''' Enter and Escape are handled by WPF PreviewKeyDown and forwarded via events.
    ''' This override catches any keys that escape the WPF handling.
    ''' </summary>
    Protected Overrides Function ProcessCmdKey(ByRef msg As Message, keyData As Keys) As Boolean
        ' Enter — save the QSO (fallback if WPF didn't catch it).
        If keyData = Keys.Enter OrElse keyData = Keys.Return Then
            If session IsNot Nothing AndAlso
               Not String.IsNullOrEmpty(wpfControl.GetFieldText("CALL").Trim()) Then
                WriteEntry()
                Return True
            End If
        End If

        ' Escape — clear the form (fallback if WPF didn't catch it).
        If keyData = Keys.Escape Then
            OnEscapePressed(Me, EventArgs.Empty)
            Return True
        End If

        Return MyBase.ProcessCmdKey(msg, keyData)
    End Function

#End Region

End Class
