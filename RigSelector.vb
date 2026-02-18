Imports System.Diagnostics
Imports System.IO
Imports System.Threading
Imports System.Xml.Serialization
Imports Flex.Smoothlake.FlexLib
Imports JJTrace
Imports MsgLib
Imports Radios

Public Class RigSelector
    Private Const mustSelect As String = "You must select a radio."
    Private Const connectOnText As String = "Auto-Connect Settings..."
    Private Const connectOffText As String = "Auto-Connect Settings..."
    Private Class radio_t
        Public Rig As FlexBase.RigData
        Public Property AutoConnect As Boolean
        Public Property LowBW As Boolean
        Public ReadOnly Property Display As String
            Get
                Dim autoConn As String = If(AutoConnect, "[AutoConnect] ", "")
                Dim lbw As String = If(LowBW, "[LowBW] ", "")
                Dim namePart As String = If(String.IsNullOrWhiteSpace(Rig.Name), "Unknown", Rig.Name)
                Dim modelPart As String = If(String.IsNullOrWhiteSpace(Rig.ModelName), "Unknown", Rig.ModelName)
                Dim serialPart As String = If(String.IsNullOrWhiteSpace(Rig.Serial), "NoSerial", Rig.Serial)
                Return $"{autoConn}{lbw}{namePart} {modelPart} {serialPart}"
            End Get
        End Property
        Public Overrides Function ToString() As String
            Return Display
        End Function
        Public Sub New(r As FlexBase.RigData)
            Rig = r
        End Sub
    End Class
    Private RadiosList As List(Of radio_t)

    ' autoConnect config data
    Public Class AutoConnectData
        Public Desired As Boolean
        Public Serial As String = ""
        Public LowBW As Boolean
    End Class
    Private autoConnectItem As AutoConnectData
    ' autoConnect config file name.
    Private ReadOnly Property autoConnectFileName As String
        Get
            Return BaseConfigDir & "\" & PersonalData.UniqueOpName(CurrentOp) & "_" &
                "autoConnect.xml"
        End Get
    End Property

    ' New unified auto-connect config
    Private _unifiedAutoConnectConfig As Radios.AutoConnectConfig
    Private ReadOnly Property OperatorName As String
        Get
            Return PersonalData.UniqueOpName(CurrentOp)
        End Get
    End Property

    ' Global auto-connect checkbox
    Private WithEvents GlobalAutoConnectCheckbox As CheckBox

    ''' <summary>
    ''' set if initial bringup
    ''' Set externally before showDialog.
    ''' </summary>
    Private Property initialBringup As Boolean
    ''' <summary>
    ''' Radio config parameters.
    ''' Set externally before showDialog.
    ''' </summary>
    Private Callouts As FlexBase.OpenParms
    Private mainWindow As Form

    Public Sub New(init As Boolean, cal As FlexBase.OpenParms, mainWin As Form)
        ' This call is required by the designer.
        InitializeComponent()

        ' Add any initialization after the InitializeComponent() call.
        initialBringup = init
        Callouts = cal
        mainWindow = mainWin

        ' Load unified auto-connect config
        _unifiedAutoConnectConfig = Radios.AutoConnectConfig.Load(BaseConfigDir, OperatorName)

        ' Add global auto-connect checkbox
        GlobalAutoConnectCheckbox = New CheckBox()
        GlobalAutoConnectCheckbox.Text = "Enable auto-connect on startup"
        GlobalAutoConnectCheckbox.Location = New System.Drawing.Point(12, 155)
        GlobalAutoConnectCheckbox.Size = New System.Drawing.Size(250, 24)
        GlobalAutoConnectCheckbox.Checked = _unifiedAutoConnectConfig.GlobalAutoConnectEnabled
        GlobalAutoConnectCheckbox.AccessibleName = "Enable auto-connect on startup"
        GlobalAutoConnectCheckbox.AccessibleRole = AccessibleRole.CheckButton
        GlobalAutoConnectCheckbox.TabIndex = 15
        Me.Controls.Add(GlobalAutoConnectCheckbox)

        ' Update context menu accessibility
        RadiosBoxAutoConnectMenuItem.AccessibleName = "Auto-connect settings for selected radio"
        RadiosBoxAutoConnectMenuItem.Text = "Auto-Connect Settings..."

        ' Hide the Login button - Remote button now handles account selection
        ' (keeping the control in Designer for backward compatibility, just hiding it)
        LoginButton.Visible = False
        LoginButton.TabStop = False ' Ensure it's not reachable via Tab key
    End Sub

    Private Sub RigSelector_Load(sender As Object, e As EventArgs) Handles MyBase.Load
        Tracing.TraceLine("RigSelector_Load:" & initialBringup.ToString(), TraceLevel.Info)
        DialogResult = DialogResult.None
        RadiosList = New List(Of radio_t)()

        ' get autoConnect config info.
        Dim fs As Stream = Nothing
        autoConnectItem = New AutoConnectData ' an empty item
        Try
            fs = File.Open(autoConnectFileName, FileMode.Open)
            ' Just skip if the file wasn't found.  Use the empty item.
            Dim xs = New XmlSerializer(GetType(AutoConnectData))
            autoConnectItem = xs.Deserialize(fs)
            Tracing.TraceLine("RigSelector_Load:autoConnect " & autoConnectItem.Serial, TraceLevel.Info)
        Catch nf As FileNotFoundException
            ' no action
        Catch ex As Exception
            MsgBox(ex.Message)
        Finally
            If fs IsNot Nothing Then
                fs.Dispose()
            End If
        End Try

        ' RigControl must have been created.
        AddHandler FlexBase.RadioFound, AddressOf radioFoundHandler

        ' Show any local radios.
        RigControl.LocalRadios()

        ' Start the autoConnect timer if appropriate.
        If initialBringup And
           (autoConnectItem.Serial <> "") And autoConnectItem.Desired Then
            Tracing.TraceLine("RigSelector_Load:autoconnect timer started", TraceLevel.Info)
            AutoConnectTimer.Enabled = True
        Else
            AutoConnectTimer.Enabled = False
        End If
    End Sub

    Private connectThread As Thread = Nothing
    Private Sub radioFoundHandler(sender As Object, e As FlexBase.RigData)
        Tracing.TraceLine("radioFoundHandler:" & e.Serial, TraceLevel.Info)
        Dim radio = New radio_t(e)
        If (autoConnectItem.Serial = radio.Rig.Serial) Then
            ' matches the autoConnect item
            radio.AutoConnect = autoConnectItem.Desired
            radio.LowBW = autoConnectItem.LowBW
        End If
        Dim isNew As Boolean = True
        SyncLock RadiosList
            ' remove dup
            For i As Integer = 0 To RadiosList.Count - 1
                If RadiosList(i).Rig.Serial = radio.Rig.Serial Then
                    RadiosList.RemoveAt(i)
                    isNew = False
                    Exit For
                End If
            Next
            RadiosList.Add(radio)
        End SyncLock
        redisplayRadiosBox()
        If isNew Then
            Radios.ScreenReaderOutput.Speak($"Radio found: {radio.Display}", False)
        End If
    End Sub

    Private Sub redisplayRadiosBox()
        TextOut.PerformGenericFunction(RadiosBox,
            Sub()
                Dim prevSelected As String = Nothing
                If RadiosBox.SelectedIndex >= 0 Then
                    Dim prev = TryCast(RadiosBox.SelectedItem, radio_t)
                    If prev IsNot Nothing Then prevSelected = prev.Rig.Serial
                End If

                RadiosBox.BeginUpdate()
                RadiosBox.Items.Clear()
                SyncLock RadiosList
                    For Each r In RadiosList
                        RadiosBox.Items.Add(r)
                    Next
                End SyncLock
                RadiosBox.EndUpdate()

                ' Restore selection
                If prevSelected IsNot Nothing Then
                    For i As Integer = 0 To RadiosBox.Items.Count - 1
                        Dim item = TryCast(RadiosBox.Items(i), radio_t)
                        If item IsNot Nothing AndAlso item.Rig.Serial = prevSelected Then
                            RadiosBox.SelectedIndex = i
                            Exit For
                        End If
                    Next
                End If
            End Sub)
    End Sub

    Private Sub RadiosBox_SelectedIndexChanged(sender As Object, e As EventArgs) Handles RadiosBox.SelectedIndexChanged
        If RadiosBox.SelectedIndex = -1 Then
            Return
        End If

        setupRadiosBoxContext()

        ' Announce the newly selected radio when the user is navigating with arrows.
        ' Only speak when the ListBox has focus (skip programmatic changes from redisplayRadiosBox).
        If RadiosBox.Focused Then
            Dim radio = TryCast(RadiosBox.SelectedItem, radio_t)
            If radio IsNot Nothing Then
                Radios.ScreenReaderOutput.Speak($"{radio.Display}. {RadiosBox.SelectedIndex + 1} of {RadiosBox.Items.Count}", True)
            End If
        End If
    End Sub

    Private Sub setupRadiosBoxContext()
        If RadiosBox.SelectedIndex = -1 Then Return
        Dim radio As radio_t = CType(RadiosBox.SelectedItem, radio_t)
        ' setup for autoConnect on/off
        If radio.AutoConnect Then
            RadiosBoxAutoConnectMenuItem.Text = connectOffText
        Else
            RadiosBoxAutoConnectMenuItem.Text = connectOnText
        End If
    End Sub

    Private Sub RemoteButton_Click(sender As Object, e As EventArgs) Handles RemoteButton.Click
        Radios.ScreenReaderOutput.Speak("Connecting to SmartLink", True)
        RemoteButton.Enabled = False
        RemoteButton.Text = "Connecting..."

        Dim countBefore As Integer = RadiosList.Count

        ' Run RemoteRadios on a background thread so the UI stays responsive
        ' during the SmartLink auth flow (which can take several seconds for PKCE login).
        Dim t As New Thread(
            Sub()
                RigControl.RemoteRadios()

                ' Marshal UI updates back to the UI thread
                Me.BeginInvoke(
                    Sub()
                        RemoteButton.Enabled = True
                        RemoteButton.Text = "Remote"

                        Dim countAfter As Integer = RadiosList.Count
                        If countAfter > countBefore Then
                            Dim newCount = countAfter - countBefore
                            Radios.ScreenReaderOutput.Speak($"Found {newCount} remote radio{If(newCount > 1, "s", "")}", True)
                        Else
                            Radios.ScreenReaderOutput.Speak("No remote radios found. Check your SmartLink account and radio power.", True)
                        End If

                        RadiosBox.Focus()
                    End Sub)
            End Sub)
        t.IsBackground = True
        t.SetApartmentState(ApartmentState.STA)  ' Required for WebView2 auth dialogs
        t.Start()
    End Sub

    Private Sub LoginButton_Click(sender As Object, e As EventArgs) Handles LoginButton.Click
        RigControl.ClearWebCache()
        RigControl.RemoteRadios()
        RadiosBox.Focus()
    End Sub

    Private Sub AutoConnectTimer_Tick(sender As Object, e As EventArgs) Handles AutoConnectTimer.Tick
        Dim radio As radio_t = Nothing
        SyncLock RadiosList
            For i As Integer = 0 To RadiosList.Count - 1
                'Dim id = i ' to avoid warning
                If RadiosList(i).Rig.Serial = autoConnectItem.Serial Then
                    radio = RadiosList(i)
                    Exit For
                End If
            Next
        End SyncLock
        If radio IsNot Nothing Then
            ' select radio and connect.
            Tracing.TraceLine("autoConnect:" & radio.Rig.Serial & " " & radio.LowBW.ToString(), TraceLevel.Info)
            CurrentRig = radio.Rig

            ' Announce connecting for screen reader users (legacy auto-connect path)
            Dim radioName As String = If(String.IsNullOrWhiteSpace(radio.Rig.Name), "radio", radio.Rig.Name)
            Radios.ScreenReaderOutput.Speak($"Connecting to {radioName}", True)

            If RigControl.Connect(CurrentRig.Serial, radio.LowBW) Then
                Radios.ScreenReaderOutput.Speak($"Connected to {radioName}", True)
                DialogResult = DialogResult.OK
                Me.Close()
            End If
        End If
    End Sub

    Private Sub ConnectButton_Click(sender As Object, e As EventArgs) Handles ConnectButton.Click
        If RadiosBox.SelectedIndex = -1 Then
            MsgBox(mustSelect)
            RadiosBox.Focus()
            Return
        End If

        ' This will exit this form.
        Dim radio As radio_t = CType(RadiosBox.SelectedItem, radio_t)
        Tracing.TraceLine("ConnectButton_Click:" & radio.Rig.Serial & " " & radio.LowBW.ToString(), TraceLevel.Info)
        CurrentRig = radio.Rig

        ' Announce connecting for screen reader users
        Dim radioName As String = If(String.IsNullOrWhiteSpace(radio.Rig.Name), "radio", radio.Rig.Name)
        Radios.ScreenReaderOutput.Speak($"Connecting to {radioName}", True)

        If RigControl.Connect(CurrentRig.Serial, radio.LowBW) Then
            Radios.ScreenReaderOutput.Speak($"Connected to {radioName}", True)
            DialogResult = DialogResult.OK
        Else
            Radios.ScreenReaderOutput.Speak($"Failed to connect to {radioName}", True)
            DialogResult = DialogResult.No
        End If
    End Sub

    Private Sub LowBWConnectButton_Click(sender As Object, e As EventArgs) Handles LowBWConnectButton.Click
        If RadiosBox.SelectedIndex = -1 Then
            MsgBox(mustSelect)
            RadiosBox.Focus()
            Return
        End If
        Dim radio As radio_t = CType(RadiosBox.SelectedItem, radio_t)
        radio.LowBW = Not radio.LowBW
        If (autoConnectItem.Serial = radio.Rig.Serial) Then
            autoConnectItem.LowBW = radio.LowBW
        End If
        redisplayRadiosBox()
        RadiosBox.Focus()
    End Sub

    Private Sub CnclButton_Click(sender As Object, e As EventArgs) Handles CnclButton.Click
        Tracing.TraceLine("RigSelector.CnclButton_Click", TraceLevel.Info)
        DialogResult = DialogResult.Cancel
    End Sub

    Private Sub RadiosBox_Enter(sender As Object, e As EventArgs) Handles RadiosBox.Enter
        Me.AcceptButton = ConnectButton
        ' Announce the selected radio or list status when focus enters.
        ' Use interrupt=True to override the native "listview window" announcement.
        If RadiosBox.Items.Count = 0 Then
            Radios.ScreenReaderOutput.Speak("Radio list is empty. Press Remote to find SmartLink radios.", True)
        ElseIf RadiosBox.SelectedIndex >= 0 Then
            Dim radio = TryCast(RadiosBox.SelectedItem, radio_t)
            If radio IsNot Nothing Then
                Radios.ScreenReaderOutput.Speak($"Radio list. {radio.Display}. {RadiosBox.SelectedIndex + 1} of {RadiosBox.Items.Count}", True)
            End If
        Else
            Radios.ScreenReaderOutput.Speak($"Radio list. {RadiosBox.Items.Count} radios. Use arrow keys to browse.", True)
        End If
    End Sub

    Private Sub RadiosBox_Leave(sender As Object, e As EventArgs) Handles RadiosBox.Leave
        Me.AcceptButton = Nothing
    End Sub

    Private Sub ConnectMenuItem_Click(sender As Object, e As EventArgs) Handles ConnectMenuItem.Click
        ConnectButton_Click(RadiosBoxContextMenuStrip, Nothing)
    End Sub

    Private Sub RadiosBoxLowBWMenuItem_Click(sender As Object, e As EventArgs) Handles RadiosBoxLowBWMenuItem.Click
        LowBWConnectButton_Click(RadiosBoxLowBWMenuItem, Nothing)
    End Sub

    Private Sub RadiosBoxAutoConnectMenuItem_Click(sender As Object, e As EventArgs) Handles RadiosBoxAutoConnectMenuItem.Click
        If RadiosBox.SelectedIndex = -1 Then
            MsgBox(mustSelect)
            RadiosBox.Focus()
            Return
        End If

        Dim radio As radio_t = CType(RadiosBox.SelectedItem, radio_t)
        Tracing.TraceLine("RadiosBoxAutoConnectMenuItem_Click:" & radio.Rig.Serial, TraceLevel.Info)

        ' Determine current state for this radio
        Dim currentAutoConnect As Boolean = radio.AutoConnect
        Dim currentLowBW As Boolean = radio.LowBW

        ' Check if a DIFFERENT radio currently has auto-connect enabled
        If _unifiedAutoConnectConfig IsNot Nothing AndAlso
           _unifiedAutoConnectConfig.HasDifferentAutoConnectRadio(radio.Rig.Serial) AndAlso
           Not currentAutoConnect Then
            ' Show confirmation dialog
            Dim currentRadioName = _unifiedAutoConnectConfig.RadioName
            If String.IsNullOrEmpty(currentRadioName) Then currentRadioName = "Another radio"

            Dim result = MessageBox.Show(
                $"{currentRadioName} currently has auto-connect enabled." & vbCrLf & vbCrLf &
                $"Switch auto-connect to {radio.Rig.Name}?",
                "Switch Auto-Connect",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1)

            If result <> DialogResult.Yes Then
                Return
            End If
        End If

        ' Show settings dialog
        Dim newAutoConnect As Boolean = Not currentAutoConnect ' Toggle by default
        Dim newLowBW As Boolean = currentLowBW

        If Radios.AutoConnectSettingsDialog.ShowSettingsDialog(Me, radio.Rig.Name, newAutoConnect, newLowBW) Then
            ' User confirmed - update settings

            ' Clear auto-connect from any other radio in the list
            If newAutoConnect Then
                For Each r In RadiosList
                    If r.Rig.Serial <> radio.Rig.Serial Then
                        r.AutoConnect = False
                    End If
                Next
            End If

            ' Update this radio
            radio.AutoConnect = newAutoConnect
            radio.LowBW = newLowBW

            ' Update legacy config
            If newAutoConnect Then
                autoConnectItem.Desired = True
                autoConnectItem.Serial = radio.Rig.Serial
                autoConnectItem.LowBW = newLowBW
            Else
                If autoConnectItem.Serial = radio.Rig.Serial Then
                    autoConnectItem.Desired = False
                End If
            End If

            ' Update unified config
            If _unifiedAutoConnectConfig IsNot Nothing Then
                If newAutoConnect Then
                    _unifiedAutoConnectConfig.SetAutoConnectRadio(
                        radio.Rig.Serial,
                        radio.Rig.Name,
                        radio.Rig.Remote,
                        RigControl.CurrentSmartLinkEmail,
                        newLowBW)
                Else
                    If _unifiedAutoConnectConfig.RadioSerial = radio.Rig.Serial Then
                        _unifiedAutoConnectConfig.ClearAutoConnectRadio()
                    End If
                End If
                Try
                    _unifiedAutoConnectConfig.Save(BaseConfigDir, OperatorName)
                Catch ex As Exception
                    Tracing.TraceLine("Failed to save unified config: " & ex.Message, TraceLevel.Error)
                End Try
            End If

            ' Save legacy config
            Dim fs As Stream = Nothing
            Try
                fs = File.Open(autoConnectFileName, FileMode.Create)
                Dim xs = New XmlSerializer(GetType(AutoConnectData))
                xs.Serialize(fs, autoConnectItem)
            Catch ex As Exception
                MsgBox(ex.Message)
            Finally
                If fs IsNot Nothing Then
                    fs.Dispose()
                End If
            End Try

            ' Refresh the display
            setupRadiosBoxContext()
            redisplayRadiosBox()

            ' Announce the change
            If newAutoConnect Then
                Radios.ScreenReaderOutput.Speak($"Auto-connect set for {radio.Rig.Name}", True)
            Else
                Radios.ScreenReaderOutput.Speak($"Auto-connect cleared for {radio.Rig.Name}", True)
            End If
        End If
    End Sub

    Private Sub RigSelector_FormClosing(sender As Object, e As FormClosingEventArgs) Handles MyBase.FormClosing
        Tracing.TraceLine("RigSelector_FormClosing", TraceLevel.Info)
        AutoConnectTimer.Dispose()
        RemoveHandler FlexBase.RadioFound, AddressOf radioFoundHandler
    End Sub

    ''' <summary>
    ''' Handles the global auto-connect checkbox change.
    ''' Saves the setting immediately.
    ''' </summary>
    Private Sub GlobalAutoConnectCheckbox_CheckedChanged(sender As Object, e As EventArgs) Handles GlobalAutoConnectCheckbox.CheckedChanged
        If _unifiedAutoConnectConfig Is Nothing Then Return

        _unifiedAutoConnectConfig.GlobalAutoConnectEnabled = GlobalAutoConnectCheckbox.Checked
        Try
            _unifiedAutoConnectConfig.Save(BaseConfigDir, OperatorName)
            Tracing.TraceLine("GlobalAutoConnectEnabled changed to: " & GlobalAutoConnectCheckbox.Checked.ToString(), TraceLevel.Info)

            ' Announce the change for screen reader users
            If GlobalAutoConnectCheckbox.Checked Then
                Radios.ScreenReaderOutput.Speak("Auto-connect on startup enabled", True)
            Else
                Radios.ScreenReaderOutput.Speak("Auto-connect on startup disabled", True)
            End If
        Catch ex As Exception
            Tracing.TraceLine("Failed to save GlobalAutoConnectEnabled: " & ex.Message, TraceLevel.Error)
        End Try
    End Sub
End Class
