Imports HamQTHLookup
Imports JJCountriesDB
Imports JJTrace
Imports MsgLib
Imports Radios

Public Class StationLookup
    Private hamqthLookup As CallbookLookup = Nothing
    Private qrzLookup As QrzLookup.QrzCallbookLookup = Nothing
    Private countriesdb As CountriesDB = Nothing
    Private lookupSource As String = ""
    Private operatorCountry As String = ""

    Private Sub StationLookup_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        ' Use operator's callbook settings if available; fall back to built-in HamQTH account.
        Dim source As String = "HamQTH"
        Dim username As String = HamqthLookupID
        Dim password As String = HamqthLookupPassword

        If CurrentOp IsNot Nothing Then
            Dim opSource = If(CurrentOp.CallbookLookupSource, "")
            Dim opUser = If(CurrentOp.CallbookUsername, "")
            Dim opPass = If(CurrentOp.DecryptedCallbookPassword, "")

            If opSource <> "None" AndAlso opSource <> "" AndAlso
               opUser <> "" AndAlso opPass <> "" Then
                source = opSource
                username = opUser
                password = opPass
            End If
        End If

        lookupSource = source
        Select Case source
            Case "QRZ"
                qrzLookup = New QrzLookup.QrzCallbookLookup(username, password)
                AddHandler qrzLookup.CallsignSearchEvent, AddressOf qrzLookupHandler
            Case Else  ' "HamQTH" or fallback
                hamqthLookup = New CallbookLookup(username, password)
                AddHandler hamqthLookup.CallsignSearchEvent, AddressOf hamqthLookupHandler
        End Select

        countriesdb = New CountriesDB()

        ' Look up operator's own country for comparison in SR announcements.
        If CurrentOp IsNot Nothing AndAlso CurrentOp.callSign <> "" Then
            Dim opRec = countriesdb.LookupByCall(CurrentOp.callSign)
            If opRec IsNot Nothing Then
                operatorCountry = If(opRec.Country, "")
            End If
        End If

        wasActive = False
        DialogResult = DialogResult.None
    End Sub

    Public Sub Finished()
        If hamqthLookup IsNot Nothing Then
            hamqthLookup.Finished()
        End If
        If qrzLookup IsNot Nothing Then
            qrzLookup.Finished()
        End If
        LookupStation.Dispose()
    End Sub

    Private Sub LookupButton_Click(sender As Object, e As EventArgs) Handles LookupButton.Click
        If CallsignBox.Text = vbNullString Then
            CallsignBox.Focus()
            Return
        End If

        ' Clear result fields.
        For Each c As Control In Controls
            If (c.Tag IsNot Nothing) And (c.Tag = " ") Then
                CType(c, TextBox).Text = ""
            End If
        Next

        ' Callbook lookup (async — results arrive via handler).
        If qrzLookup IsNot Nothing AndAlso qrzLookup.CanLookup Then
            NameBox.Focus()
            qrzLookup.LookupCall(CallsignBox.Text)
        ElseIf hamqthLookup IsNot Nothing AndAlso hamqthLookup.CanLookup Then
            NameBox.Focus()
            hamqthLookup.LookupCall(CallsignBox.Text)
        Else
            CountryBox.Focus()
        End If

        ' Country database lookup (synchronous, offline).
        Dim rec As Record = countriesdb.LookupByCall(CallsignBox.Text)
        If rec IsNot Nothing Then
            CountryBox.Text = rec.Country
            If rec.CountryID <> vbNullString Then
                CountryBox.Text += " (" & rec.CountryID & ")"
            End If
            LatlongBox.Text = rec.Latitude & "/" & rec.Longitude
            CQBox.Text = rec.CQZone
            ITUBox.Text = rec.ITUZone
            GMTBox.Text = rec.TimeZone
        End If
    End Sub

    ''' <summary>
    ''' Handle HamQTH lookup result — display in fields and announce via screen reader.
    ''' </summary>
    Private Sub hamqthLookupHandler(item As CallbookLookup.HamQTH)
        If item Is Nothing OrElse item.search Is Nothing Then Return

        If Me.InvokeRequired Then
            Me.BeginInvoke(Sub() hamqthLookupHandler(item))
            Return
        End If

        Dim name = If(item.search.nick, "")
        Dim qth = If(item.search.qth, "")
        Dim grid = If(item.search.grid, "")
        Dim state = If(item.search.State, "")
        Dim country = If(item.search.country, "")

        NameBox.Text = name
        QTHBox.Text = qth
        If grid <> "" Then QTHBox.Text &= " (" & grid & ")"
        StateBox.Text = state

        ' Screen reader announcement.
        AnnounceResult(name, qth, state, country)
    End Sub

    ''' <summary>
    ''' Handle QRZ lookup result — display in fields and announce via screen reader.
    ''' </summary>
    Private Sub qrzLookupHandler(result As QrzLookup.QrzCallbookLookup.QrzDatabase)
        If result Is Nothing OrElse result.Callsign Is Nothing Then Return

        If Me.InvokeRequired Then
            Me.BeginInvoke(Sub() qrzLookupHandler(result))
            Return
        End If

        Dim name = If(result.Callsign.FirstName, "")
        Dim qth = If(result.Callsign.City, "")
        Dim grid = If(result.Callsign.Grid, "")
        Dim state = If(result.Callsign.State, "")
        Dim country = If(result.Callsign.Country, "")

        NameBox.Text = name
        QTHBox.Text = qth
        If grid <> "" Then QTHBox.Text &= " (" & grid & ")"
        StateBox.Text = state

        ' Screen reader announcement.
        AnnounceResult(name, qth, state, country)
    End Sub

    ''' <summary>
    ''' Announce the lookup result via screen reader.
    ''' Speaks name, QTH, state (if available), and country only when the station
    ''' is in a different country than the operator (DX). Details (grid,
    ''' zones, lat/long) are in the read-only fields for Tab-through.
    ''' </summary>
    Private Sub AnnounceResult(name As String, qth As String, state As String, country As String)
        Dim parts As New List(Of String)
        If name <> "" Then parts.Add(name)
        If qth <> "" Then parts.Add(qth)
        If state <> "" Then parts.Add(state)

        ' Include country only when it differs from operator's country (DX station).
        If country <> "" AndAlso
           Not country.Equals(operatorCountry, StringComparison.OrdinalIgnoreCase) Then
            parts.Add(country)
        End If

        If parts.Count > 0 Then
            Dim msg = lookupSource & ": " & String.Join(", ", parts)
            ScreenReaderOutput.Speak(msg, True)
        End If
    End Sub

    Private Sub DoneButton_Click(sender As Object, e As EventArgs) Handles DoneButton.Click
        DialogResult = DialogResult.OK
    End Sub

    Private Sub Box_Enter(sender As Object, e As EventArgs) Handles CallsignBox.Enter, CountryBox.Enter, LatlongBox.Enter, QTHBox.Enter, StateBox.Enter, CQBox.Enter, ITUBox.Enter, GMTBox.Enter, NameBox.Enter
        Dim tb As TextBox = CType(sender, TextBox)
        If tb.Text <> vbNullString Then
            tb.SelectionStart = 0
            tb.SelectionLength = tb.Text.Length
        End If
    End Sub

    Private wasActive As Boolean
    Private Sub StationLookup_Activated(sender As Object, e As EventArgs) Handles MyBase.Activated
        If Not wasActive Then
            wasActive = True
            CallsignBox.Focus()
        End If
    End Sub
End Class
