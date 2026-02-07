Imports System.Drawing
Imports System.Windows.Forms
Imports adif
Imports JJLogLib
Imports JJTrace
Imports Radios

''' <summary>
''' Quick-entry logging panel for Logging Mode.
''' Embedded in Form1 via SplitContainer. Thin wrapper over LogSession —
''' manages its own TextBoxes and talks to LogSession via SetFieldText/GetFieldText.
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

    ' --- State ---
    Private session As LogSession
    Private currentCall As String = ""
    Private isDup As Boolean = False

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
        AutoFillFromRadio()
        If session IsNot Nothing Then
            SerialLabel.Text = "Serial: " & CStr(session.serial)
        End If
        DupLabel.Text = "Dup: 0"
        isDup = False
        currentCall = ""
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

#Region "Event Handlers"

    Private Sub CallSignBox_Leave(sender As Object, e As EventArgs) Handles CallSignBox.Leave
        Dim callText = CallSignBox.Text.Trim().ToUpper()
        If String.IsNullOrEmpty(callText) Then Return
        CallSignBox.Text = callText  ' Normalize to uppercase

        ' Auto-fill radio data on first field exit.
        AutoFillFromRadio()

        ' Dup check.
        CheckDup(callText <> currentCall)
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
            Console.Beep(880, 400)
            ScreenReaderOutput.Speak("Duplicate, " & ct & " previous contacts", True)
        End If
    End Sub

#End Region

#Region "UI Construction"

    ''' <summary>
    ''' Build all controls programmatically. No designer file needed.
    ''' </summary>
    Private Sub BuildControls()
        Me.SuspendLayout()

        ' --- Entry fields (left column: labels, right column: text boxes) ---
        Dim yPos As Integer = 8
        Const labelWidth As Integer = 75
        Const fieldWidth As Integer = 150
        Const fieldHeight As Integer = 22
        Const rowSpacing As Integer = 28
        Const labelX As Integer = 4
        Const fieldX As Integer = 82

        CallSignBox = AddEntryField("Call Sign", AdifTags.ADIF_Call, yPos, labelX, fieldX, labelWidth, fieldWidth, fieldHeight)
        yPos += rowSpacing

        ' RST side by side
        RSTSentBox = AddEntryField("RST Sent", AdifTags.ADIF_HisRST, yPos, labelX, fieldX, labelWidth, 60, fieldHeight)
        Dim rstRcvdLabel = AddLabel("RST Rcvd", fieldX + 68, yPos + 3, 60)
        RSTRcvdBox = AddTextBox("RST Received", fieldX + 130, yPos, 60, fieldHeight)
        ' Dup display on same row
        DupLabel = AddLabel("Dup: 0", fieldX + 200, yPos + 3, 80)
        DupLabel.AccessibleName = "Duplicate count"
        yPos += rowSpacing

        NameBox = AddEntryField("Name", AdifTags.ADIF_Name, yPos, labelX, fieldX, labelWidth, fieldWidth, fieldHeight)
        yPos += rowSpacing

        QTHBox = AddEntryField("QTH", AdifTags.ADIF_QTH, yPos, labelX, fieldX, labelWidth, fieldWidth, fieldHeight)
        yPos += rowSpacing

        ' State and Grid side by side
        StateBox = AddEntryField("State", AdifTags.ADIF_State, yPos, labelX, fieldX, labelWidth, 50, fieldHeight)
        Dim gridLabel = AddLabel("Grid", fieldX + 58, yPos + 3, 35)
        GridBox = AddTextBox("Grid Square", fieldX + 95, yPos, 70, fieldHeight)
        yPos += rowSpacing

        CommentsBox = AddEntryField("Comments", AdifTags.ADIF_Comment, yPos, labelX, fieldX, labelWidth, 250, fieldHeight)
        yPos += rowSpacing + 8

        ' --- Read-only radio info ---
        Dim infoY = yPos
        FreqLabel = AddLabel("Freq: ---", labelX, infoY, 200)
        FreqLabel.AccessibleName = "Frequency"
        infoY += 18
        ModeLabel = AddLabel("Mode: ---", labelX, infoY, 100)
        ModeLabel.AccessibleName = "Mode"
        BandLabel = AddLabel("Band: ---", labelX + 110, infoY, 100)
        BandLabel.AccessibleName = "Band"
        infoY += 18
        DateTimeLabel = AddLabel("UTC: ---", labelX, infoY, 200)
        DateTimeLabel.AccessibleName = "Date and Time UTC"
        infoY += 18
        SerialLabel = AddLabel("Serial: 0", labelX, infoY, 100)
        SerialLabel.AccessibleName = "Serial number"

        ' Tab order (set TabIndex sequentially).
        CallSignBox.TabIndex = 0
        RSTSentBox.TabIndex = 1
        RSTRcvdBox.TabIndex = 2
        NameBox.TabIndex = 3
        QTHBox.TabIndex = 4
        StateBox.TabIndex = 5
        GridBox.TabIndex = 6
        CommentsBox.TabIndex = 7

        Me.ResumeLayout(False)
    End Sub

    Private Function AddEntryField(labelText As String, adifTag As String,
                                    y As Integer, labelX As Integer, fieldX As Integer,
                                    labelWidth As Integer, fieldWidth As Integer,
                                    fieldHeight As Integer) As TextBox
        Dim lbl = AddLabel(labelText, labelX, y + 3, labelWidth)
        Dim tb = AddTextBox(labelText, fieldX, y, fieldWidth, fieldHeight)
        Return tb
    End Function

    Private Function AddLabel(text As String, x As Integer, y As Integer, w As Integer) As Label
        Dim lbl As New Label() With {
            .Text = text,
            .Location = New Point(x, y),
            .Size = New Size(w, 16),
            .AutoSize = False
        }
        Me.Controls.Add(lbl)
        Return lbl
    End Function

    Private Function AddTextBox(accessibleName As String, x As Integer, y As Integer,
                                 w As Integer, h As Integer) As TextBox
        Dim tb As New TextBox() With {
            .Location = New Point(x, y),
            .Size = New Size(w, h),
            .AccessibleName = accessibleName,
            .AccessibleRole = AccessibleRole.Text
        }
        Me.Controls.Add(tb)
        Return tb
    End Function

#End Region

End Class
