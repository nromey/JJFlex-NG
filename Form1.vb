#Const LeaveBootTraceOn = 2
Imports System.Collections
Imports System.Collections.Specialized
Imports System.Configuration
Imports System.Diagnostics
Imports System.Drawing
Imports System.IO
Imports System.Math
Imports System.Threading
Imports JJLogIO
Imports JJLogLib
Imports JJTrace
Imports JJW2WattMeter
Imports MsgLib
Imports RadioBoxes
Imports Radios

Public Class Form1
    Const notConnected As String = "The radio didn't connect."
    Const nowDisconnected As String = "The radio disconnected"
    Const noRadioSelected As String = "No radio was selected."
    Const antennaTuneButtonBaseText As String = "Ant Tune"
    Const memorizedText As String = "Memorized"
    Private ReadOnly Property antennaTuneButtonText As String
        Get
            Dim rv = antennaTuneButtonBaseText
            If ((RigControl IsNot Nothing) And
                RigControl.FlexTunerUsingMemoryNow) Then
                rv &= " mem"
            End If
            Return rv
        End Get
    End Property
    Private Function TBIDToTB(ByVal tbid As WindowIDs) As TextBox
        Dim tb As TextBox = Nothing
        Select Case tbid
            Case WindowIDs.ReceiveDataOut
                tb = ReceivedTextBox
            Case WindowIDs.SendDataOut
                tb = SentTextBox
        End Select
        Return tb
    End Function

    Dim onExitScreenSaver As Boolean
    Const pollTimerInterval As Integer = 100 ' .1 seconds
    Dim WithEvents thePollTimer As System.Windows.Forms.Timer
    Private Property PollTimer As Boolean
        Get
            If thePollTimer IsNot Nothing Then
                Return thePollTimer.Enabled
            Else
                Return False
            End If
        End Get
        Set(value As Boolean)
            Tracing.TraceLine("PollTimer:" & value.ToString, TraceLevel.Info)
            If value Then
                thePollTimer = New System.Windows.Forms.Timer(components)
                AddHandler thePollTimer.Tick, AddressOf PollTimer_Tick
                thePollTimer.Interval = pollTimerInterval
                thePollTimer.Start()
            Else
                If thePollTimer IsNot Nothing Then
                    thePollTimer.Stop()
                    thePollTimer.Dispose()
                End If
            End If
        End Set
    End Property

#If 0 Then
    Private Function getappSettings() As AppSettingsSection
        Dim appSettings As AppSettingsSection
        Try
            Dim config As Configuration = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None)
            appSettings = CType(config.GetSection("appSettings"), AppSettingsSection)
            If appSettings.Settings.Count = 0 Then
                appSettings = Nothing
            End If
        Catch ex As Exception
            MessageBox.Show(ex.Message, ExceptionHdr, MessageBoxButtons.OK)
            appSettings = Nothing
        End Try
        Return appSettings
    End Function
#End If

    ' Status line
    Private statusFields() As MainBox.Field =
    {New MainBox.Field("Power", 3, "pwr:", "  ", Nothing),
     New MainBox.Field("Memories", 5, "memories:", "  ", Nothing),
     New MainBox.Field("Scan", 7, "scan:", "  ", Nothing),
     New MainBox.Field("Knob", 7, "Knob:", "  ", Nothing),
     New MainBox.Field("LogFile", 20, "log:", "", Nothing)
    }
    Private Sub statusSetup()
        StatusBox.Populate(statusFields)
        StatusBox.Clear()
        StatusBox.Write("Scan", OffWord)
    End Sub

    Private WithEvents testMenuItem As System.Windows.Forms.ToolStripMenuItem
    ' Filters menu (moved to main Actions menu for accessibility)
    Private filtersMenuItem As ToolStripMenuItem
    Private filtersNoiseMenuItem As ToolStripMenuItem
    Private filtersNRMenuItem As ToolStripMenuItem
    Private filtersNRRnnItem As ToolStripMenuItem
    Private filtersNRSpectralItem As ToolStripMenuItem
    Private filtersNRLegacyItem As ToolStripMenuItem
    Private filtersANFMenuItem As ToolStripMenuItem
    Private filtersANFFftItem As ToolStripMenuItem
    Private filtersANFLegacyItem As ToolStripMenuItem
    Private filtersCWMenuItem As ToolStripMenuItem
    Private filtersCWAutotuneItem As ToolStripMenuItem
    Private keepDailyTraceMenuItem As ToolStripMenuItem
    Private Enum AdvancedGateState
        None
        Pending
        Disabled
    End Enum

    Private currentAdvancedGateState As AdvancedGateState
    Private lastAdvancedGateMessage As String

    ' Accessibility: describe menu item state (checked/unchecked/disabled) for screen readers.
    Private Sub UpdateMenuAccessibilityStates()
        If MenuStrip1 Is Nothing Then Return
        For Each item As ToolStripItem In MenuStrip1.Items
            AttachMenuAccessibilityHandlers(TryCast(item, ToolStripMenuItem))
        Next
    End Sub

    Private Function StripMenuMnemonics(text As String) As String
        If String.IsNullOrEmpty(text) Then Return text
        Const placeholder As Char = ChrW(1)
        Dim tmp = text.Replace("&&", placeholder)
        tmp = tmp.Replace("&", "")
        tmp = tmp.Replace(placeholder, "&")
        Return tmp
    End Function

    Private Sub AttachMenuAccessibilityHandlers(menuItem As ToolStripMenuItem)
        If menuItem Is Nothing Then Return
        NormalizeMenuItemAccessibility(menuItem)
        AddHandler menuItem.CheckedChanged,
            Sub(sender As Object, args As EventArgs)
                NormalizeMenuItemAccessibility(TryCast(sender, ToolStripMenuItem))
            End Sub
        AddHandler menuItem.EnabledChanged,
            Sub(sender As Object, args As EventArgs)
                NormalizeMenuItemAccessibility(TryCast(sender, ToolStripMenuItem))
            End Sub
        AddHandler menuItem.DropDownOpening,
            Sub()
                For Each child As ToolStripItem In menuItem.DropDownItems
                    AttachMenuAccessibilityHandlers(TryCast(child, ToolStripMenuItem))
                Next
            End Sub
        For Each child As ToolStripItem In menuItem.DropDownItems
            AttachMenuAccessibilityHandlers(TryCast(child, ToolStripMenuItem))
        Next
    End Sub

    Private Sub NormalizeMenuItemAccessibility(menuItem As ToolStripMenuItem)
        If menuItem Is Nothing Then Return
        Dim label = StripMenuMnemonics(menuItem.Text)
        If Not String.Equals(menuItem.Text, label, StringComparison.Ordinal) Then
            menuItem.Text = label
        End If

        Dim state As String = Nothing
        If menuItem.CheckOnClick OrElse menuItem.Checked OrElse menuItem.CheckState <> CheckState.Unchecked Then
            If Not menuItem.Enabled Then
                state = "unavailable"
            ElseIf menuItem.CheckState = CheckState.Indeterminate Then
                state = "mixed"
            ElseIf menuItem.Checked Then
                state = "checked"
            Else
                state = "not checked"
            End If
            menuItem.AccessibleRole = AccessibleRole.CheckButton
        ElseIf menuItem.AccessibleRole = AccessibleRole.None Then
            menuItem.AccessibleRole = AccessibleRole.MenuItem
        End If

        If String.IsNullOrEmpty(state) Then
            menuItem.AccessibleName = label
            menuItem.AccessibleDescription = Nothing
        Else
            menuItem.AccessibleName = label & " " & state
            menuItem.AccessibleDescription = state
        End If
    End Sub

    Private Sub Form1_Load(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles MyBase.Load
        ' Welcome announcement for screen reader users
        Radios.ScreenReaderOutput.Speak("Welcome to JJ Flex")
        System.Threading.Thread.Sleep(1500) ' Give time for welcome message to complete

        statusSetup() ' setup the status line.

        ' Create main objects.
        GetConfigInfo()

        ' One-time upgrade prompt: offer Modern mode to existing operators who predate the feature.
        CheckUIModUpgradePrompt()

        ' set the station name.
        StationName = getStationName()
        Tracing.TraceLine("StationName:" & StationName, TraceLevel.Info)

        ' Get program name
        Dim pgmName = StationName
        If ProgramInstance > 1 Then
            pgmName &= ProgramInstance.ToString
        End If
        Me.Text &= " " & pgmName
        Me.Refresh()

#If LeaveBootTraceOn >= 1 Then
        ' debug build, add the Test menu item.
        Me.testMenuItem = New System.Windows.Forms.ToolStripMenuItem()
        Me.testMenuItem.AccessibleRole = System.Windows.Forms.AccessibleRole.MenuItem
        Me.testMenuItem.Name = "testFunction"
        Me.testMenuItem.Size = New System.Drawing.Size(275, 26)
        Me.testMenuItem.Text = "&Test function"
        AddHandler Me.testMenuItem.Click, AddressOf testMenuItem_Click
        Me.ActionsToolStripMenuItem.DropDownItems.AddRange(New System.Windows.Forms.ToolStripItem() {Me.testMenuItem})
#End If

        ' Add handlers to handle any future configuration changes.
        AddHandler Operators.ConfigEvent, AddressOf operatorChanged

        WriteText = AddressOf iWriteText
        WriteTextX = AddressOf iWriteTextX

        ProgramDirectory = Directory.GetCurrentDirectory
        Tracing.TraceLine("Form1 load:" & ProgramDirectory, TraceLevel.Info)

        ' Turn off the screen saver.
        onExitScreenSaver = setScreenSaver(False)

        InitFiltersActionsMenu()

        openTheRadio(True) ' initial call

        FreqOut.BringToFront()

        ' Refresh menu accessibility state whenever the menu is activated so screen readers
        ' hear native checked/disabled status.
        AddHandler MenuStrip1.MenuActivate, Sub() UpdateMenuAccessibilityStates()
        AttachMenuAccessibilityHandlers(ActionsToolStripMenuItem)
        AttachMenuAccessibilityHandlers(ScreenFieldsMenu)
        AttachMenuAccessibilityHandlers(OperationsMenuItem)
        AttachMenuAccessibilityHandlers(HelpToolStripMenuItem)

        ' --- UI Mode setup ---
        ' Add "Enter Logging Mode" and "Switch to Modern UI" to the Classic Actions menu (before Exit).
        Dim enterLoggingClassicItem = New ToolStripMenuItem() With {
            .Text = "Enter Logging Mode",
            .AccessibleName = "Enter Logging Mode",
            .AccessibleRole = AccessibleRole.MenuItem
        }
        AddHandler enterLoggingClassicItem.Click, Sub(s As Object, ev As EventArgs) EnterLoggingMode()
        Dim switchToModernItem = New ToolStripMenuItem() With {
            .Text = "Switch to Modern UI",
            .AccessibleName = "Switch to Modern UI",
            .AccessibleRole = AccessibleRole.MenuItem
        }
        AddHandler switchToModernItem.Click, Sub(s As Object, ev As EventArgs) ToggleUIMode()
        Dim exitIndex = ActionsToolStripMenuItem.DropDownItems.IndexOf(FileExitToolStripMenuItem)
        If exitIndex >= 0 Then
            ActionsToolStripMenuItem.DropDownItems.Insert(exitIndex, enterLoggingClassicItem)
            ActionsToolStripMenuItem.DropDownItems.Insert(exitIndex + 1, switchToModernItem)
            ActionsToolStripMenuItem.DropDownItems.Insert(exitIndex + 2, New ToolStripSeparator())
        Else
            ActionsToolStripMenuItem.DropDownItems.Add(New ToolStripSeparator())
            ActionsToolStripMenuItem.DropDownItems.Add(enterLoggingClassicItem)
            ActionsToolStripMenuItem.DropDownItems.Add(switchToModernItem)
        End If
        AttachMenuAccessibilityHandlers(enterLoggingClassicItem)
        AttachMenuAccessibilityHandlers(switchToModernItem)

        ' Build menu structures and apply the current operator's mode.
        BuildModernMenus()
        BuildLoggingMenus()
        BuildLoggingPanels()
        ApplyUIMode()
    End Sub

    ''' <summary>
    ''' Open the radio.
    ''' This will terminate the program if user elects to abort.
    ''' </summary>
    ''' <returns>True on success</returns>
    Friend Function openTheRadio(initialCall As Boolean) As Boolean
        Try
            Dim rv As Boolean
            OpenParms = New FlexBase.OpenParms()
            OpenParms.ProgramName = ProgramName
            OpenParms.CWTextReceiver = AddressOf Commands.DisplayDecodedText
            ' Frequency formatters
            OpenParms.FormatFreqForRadio = AddressOf UlongFreq
            OpenParms.FormatFreq = AddressOf FormatFreqUlong
            OpenParms.GotoHome = AddressOf gotoHome
            OpenParms.ConfigDirectory = BaseConfigDir & "\Radios"
            OpenParms.AudioDevicesFile = AudioDevicesFile
            OpenParms.GetOperatorName = AddressOf currentOperatorName
            OpenParms.StationName = StationName
            OpenParms.BrailleCells = CurrentOp.BrailleDisplaySize
            OpenParms.License = CurrentOp.License
            OpenParms.Profiles = CurrentOp.Profiles

            ' Check for auto-connect on initial startup
            If initialCall Then
                Dim autoConnectResult = TryAutoConnectOnStartup()
                If autoConnectResult = AutoConnectStartupResult.Connected Then
                    ' Auto-connect succeeded - skip RigSelector
                    rv = True
                    radioSelected = DialogResult.OK
                    Me.Activate()    ' Reclaim focus after WebView2 auth window
                    GoTo RadioConnected
                ElseIf autoConnectResult = AutoConnectStartupResult.UserCancelled Then
                    ' User cancelled from the failure dialog
                    rv = False
                    radioSelected = DialogResult.Cancel
                    Return rv
                End If
                ' Otherwise (Failed or ShowSelector), fall through to show RigSelector
            End If

            ' Note this creates a new rigControl object.
            selectorThread = New Thread(AddressOf selectorProc)
            selectorThread.Name = "selectorThread"
            selectorThread.SetApartmentState(ApartmentState.STA)
            selectorThread.Start(initialCall)
            selectorThread.Join()
            Me.Activate()
            rv = (radioSelected = DialogResult.OK)

RadioConnected:

            If rv Then
                ' add handlers for RigControl events.
                AddHandler RigControl.PowerStatus, AddressOf powerStatusHandler
                AddHandler RigControl.NoSliceError, AddressOf noSliceErrorHandler

                AddHandler RigControl.FeatureLicenseChanged, AddressOf RigControl_FeatureLicenseChanged

                Tracing.TraceLine("OpenTheRadio:rig is starting", TraceLevel.Info)
                rv = RigControl.Start()
                If Not rv Then
                    radioSelected = DialogResult.Abort
                End If
            End If

            If rv Then

                setupBoxes()
                UpdateAdvancedFeatureMenus()
                UpdateFiltersActionsMenu()
                ' Rig dependent menu items.
                ' disable window controls initially.
                enableDisableWindowControls(False)

                ' Start polling for changes
                PollTimer = True
            Else
                Tracing.TraceLine("OpenTheRadio:rig's open failed", TraceLevel.Error)
                If radioSelected = DialogResult.Abort Then
                    ' radio couldn't start
                    CloseTheRadio()
                ElseIf radioSelected = DialogResult.No Then
                    MessageBox.Show(notConnected, ErrorHdr, MessageBoxButtons.OK)
                Else
                    'MessageBox.Show(noRadioSelected, MessageHdr, MessageBoxButtons.OK)
                    ' No radio was desired.  Perhaps turn off tracing.
#If LeaveBootTraceOn = 0 Then
                turnTracingOff()
#End If
                End If
            End If
            Return rv
        Catch ex As Exception
            Tracing.TraceLine("openTheRadio exception:" & ex.Message & Environment.NewLine & ex.StackTrace, TraceLevel.Error)
            Return False
        End Try
    End Function

    ''' <summary>
    ''' Result of auto-connect attempt on startup.
    ''' </summary>
    Private Enum AutoConnectStartupResult
        ''' <summary>No auto-connect configured, show RigSelector.</summary>
        ShowSelector
        ''' <summary>Auto-connect succeeded.</summary>
        Connected
        ''' <summary>Auto-connect failed, but user chose to pick another radio.</summary>
        Failed
        ''' <summary>User cancelled from the failure dialog.</summary>
        UserCancelled
    End Enum

    ''' <summary>
    ''' Current auto-connect configuration (loaded on startup).
    ''' </summary>
    Private _autoConnectConfig As Radios.AutoConnectConfig

    ''' <summary>
    ''' Attempts to auto-connect to a saved radio on startup.
    ''' </summary>
    Private Function TryAutoConnectOnStartup() As AutoConnectStartupResult
        Try
            ' Load auto-connect configuration
            Dim operatorName = PersonalData.UniqueOpName(CurrentOp)
            _autoConnectConfig = Radios.AutoConnectConfig.Load(BaseConfigDir, operatorName)

            If Not _autoConnectConfig.ShouldAutoConnect Then
                Tracing.TraceLine("TryAutoConnectOnStartup: no auto-connect configured", TraceLevel.Info)
                Return AutoConnectStartupResult.ShowSelector
            End If

            Tracing.TraceLine("TryAutoConnectOnStartup: attempting " & _autoConnectConfig.RadioName, TraceLevel.Info)

            ' Create RigControl for the connection attempt
            RigControl = New FlexBase(OpenParms)

            ' Try to connect
            Dim connected = RigControl.TryAutoConnect(_autoConnectConfig)

            If connected Then
                Tracing.TraceLine("TryAutoConnectOnStartup: success", TraceLevel.Info)
                Return AutoConnectStartupResult.Connected
            End If

            ' Connection failed - show the failure dialog
            Tracing.TraceLine("TryAutoConnectOnStartup: failed, showing dialog", TraceLevel.Info)
            Dim dialogResult = Radios.AutoConnectFailedDialog.ShowDialog(Me, _autoConnectConfig.RadioName)

            Select Case dialogResult
                Case Radios.AutoConnectFailedResult.TryAgain
                    ' Retry with a fresh RigControl to clear stale WAN state
                    RigControl.Dispose()
                    RigControl = New FlexBase(OpenParms)
                    connected = RigControl.TryAutoConnect(_autoConnectConfig)
                    If connected Then
                        Return AutoConnectStartupResult.Connected
                    End If
                    ' Still failed - fall through to show selector
                    RigControl.Dispose()
                    RigControl = Nothing
                    Return AutoConnectStartupResult.Failed

                Case Radios.AutoConnectFailedResult.DisableAutoConnect
                    ' Disable auto-connect for this radio
                    _autoConnectConfig.Enabled = False
                    _autoConnectConfig.Save(BaseConfigDir, operatorName)
                    RigControl.Dispose()
                    RigControl = Nothing
                    Return AutoConnectStartupResult.ShowSelector

                Case Radios.AutoConnectFailedResult.ChooseAnotherRadio
                    ' User wants to pick a different radio
                    RigControl.Dispose()
                    RigControl = Nothing
                    Return AutoConnectStartupResult.Failed

                Case Else ' Cancel
                    RigControl.Dispose()
                    RigControl = Nothing
                    Return AutoConnectStartupResult.UserCancelled
            End Select

            Return AutoConnectStartupResult.ShowSelector
        Catch ex As Exception
            Tracing.TraceLine("TryAutoConnectOnStartup exception: " & ex.Message, TraceLevel.Error)
            If RigControl IsNot Nothing Then
                RigControl.Dispose()
                RigControl = Nothing
            End If
            Return AutoConnectStartupResult.ShowSelector
        End Try
    End Function

    Private radioSelected As DialogResult
    Private Sub selectorProc(o As Object)
        Dim initialCall = CType(o, Boolean)
        ' new rig
        Dim selector As RigSelector = New RigSelector(initialCall, OpenParms, Me)
        Dim theForm As Form = CType(selector, Form)
        RigControl = New FlexBase(OpenParms)
        radioSelected = theForm.ShowDialog()
        If radioSelected <> DialogResult.OK Then
            'enableDisableWindowControls(False)
            RigControl.Dispose()
            RigControl = Nothing
        End If
        theForm.Dispose()
    End Sub

    Friend Sub CloseTheRadio()
        Tracing.TraceLine("CloseTheRadio", TraceLevel.Info)
        clearMainWindow()
        StopKnob()
        PollTimer = False
        If SMeter IsNot Nothing Then
            SMeter.Peak = False
        End If
        If RigControl IsNot Nothing Then
            If RigControl.RigFields IsNot Nothing Then
                If RigControl.RigFields.RigControl IsNot Nothing Then
                    If enableDisableControls IsNot Nothing Then
                        enableDisableControls.Remove(RigControl.RigFields.RigControl)
                    End If
                    Controls.Remove(RigControl.RigFields.RigControl)
                End If
                RemoveHandler RigControl.RigFields.RigControl.KeyDown, AddressOf doCommand_KeyDown
            End If
            ' We need to turn power off explicitly here, not via interrupt.
            Power = False
            RemoveHandler RigControl.PowerStatus, AddressOf powerStatusHandler
            RigControl.Dispose()
            RigControl = Nothing
        End If
    End Sub

    Private Sub PollTimer_Tick(sender As System.Object, e As System.EventArgs)
        UpdateStatus()
    End Sub

    Const initialFreqPos As Integer = -3
    Friend firstFreqDisplay As Boolean = True
    Private Function getFreqPos() As Integer
        Dim rv As Integer
        Try
            ' Check for all blanks
            If Not FreqOut.IsEmpty(freqID) Then
                ' Get current freq
                Dim str As String = FreqOut.Read(freqID)
                ' Set freqPos.
                Dim cursor As Integer = FreqOut.SelectionStart - FreqOut.Position(freqID)
                If str(cursor) = "."c Then
                    cursor -= 1
                End If
                rv = 0
                While cursor < str.Length - 1
                    If (str(cursor) <> "."c) Then
                        rv -= 1
                    End If
                    cursor += 1
                End While
            Else
                ' frequency is empty.
                rv = 1
            End If
        Catch ex As Exception
            rv = 1
            Tracing.TraceLine("getFreqPos exception:" & ex.Message, TraceLevel.Error)
        End Try
        Return rv
    End Function

    Const xmitKey = Keys.X
    Private Sub AdjustFreq(ByVal p As fieldFuncParm)
        If p.fromRig Then
            ' show transmit or virtual receive frequency.
            Dim freq As ULong
            If RigControl.Transmit Then
                freq = RigControl.TXFrequency
            Else
                freq = RXFrequency
            End If
            FreqOut.Write(p.ID, FormatFreq(freq))
        Else
            ' Supported keys: up and down arrow, D, U, A - D, =, space, S, T, V, X, K, minus, plus, and digits 0-9.
            ' You may not change the frequency here if transmitting.
            If (p.k <> xmitKey) And RigControl.Transmit Then
                Tracing.TraceLine("AdjustFreq:can't change while transmitting", TraceLevel.Error)
                Return
            End If

            Dim factor As Int64
            Dim longFreq As ULong
            Select Case p.k
                Case Keys.Up, Keys.Down, Keys.U, Keys.D
                    p.rv = True
                    getFreqAndFactor(longFreq, factor)
                    If (longFreq <> 0) And (factor > 0) Then
                        ' Get new value.
                        Select Case p.k
                            Case Keys.Up, Keys.U
                                longFreq += factor
                            Case Keys.Down, Keys.D
                                If factor < longFreq Then
                                    longFreq -= factor
                                End If
                        End Select
                        ' display and send to radio.  Use virtual receive frequency.
                        RXFrequency = longFreq
                    End If
                Case Keys.K
                    ' Round to nearest khz.
                    Dim freq As Double = CType(RigControl.Frequency, Double)
                    freq = 1000 * Math.Round(freq / 1000)
                    RXFrequency = CType(freq, ULong)
                Case Keys.S, Keys.T
                    ' Treat "s" and "t" as they are with split.
                    adjustSplit(p)
                Case Keys.V
                    ' vox on/off
                    RigControl.Vox = RigControl.ToggleOffOn(RigControl.Vox)
                Case xmitKey
                    ' transmit on/off
                    toggleTransmit()
                Case Keys.OemMinus, Keys.Subtract
                    freqShiftFactor = -1
                Case (Keys.Oemplus Or Keys.Shift), Keys.Add
                    freqShiftFactor = 1
                Case Else
                    ' check for a digit
                    Dim baseNum As Integer = 0
                    If ((p.k >= Keys.D0) And (p.k <= Keys.D9)) Then
                        baseNum = Keys.D0
                    ElseIf ((p.k >= Keys.NumPad0) And (p.k <= Keys.NumPad9)) Then
                        baseNum = Keys.NumPad0
                    End If
                    If baseNum <> 0 Then
                        p.rv = True
                        getFreqAndFactor(longFreq, factor)
                        If (longFreq <> 0) And (factor > 0) Then
                            ' Get new value.
                            Dim num As Integer = p.k - baseNum
                            num *= freqShiftFactor * factor
                            longFreq += num
                            ' display and send to radio.
                            ' Note we don't use the virtual receive freq here.
                            If SplitVFOs Then
                                RigControl.CopyVFO(RigControl.CurVFO, RigControl.NextVFO(RigControl.CurVFO))
                                RigControl.TXFrequency = longFreq
                            Else
                                RigControl.RXFrequency = longFreq
                            End If
                        End If
                    End If
            End Select
        End If
    End Sub

    ' This is 1 for plus, -1 for minus.
    Private freqShiftFactor As Integer = 1
    ' Used in adjustFreq above.
    Private Sub getFreqAndFactor(ByRef freq As ULong, ByRef fact As Int64)
        freq = 0
        fact = -1
        Dim fp As Integer = getFreqPos()
        If fp <= 0 Then
            freq = FreqInt64(FreqOut.Read(freqID))
            If freq <> 0 Then
                fact = 10 ^ (System.Math.Abs(fp))
            End If
        End If
    End Sub

    Private Sub adjustRit(p As fieldFuncParm)
        If p.fromRig Then
            If RigControl.MyCaps.HasCap(RigCaps.Caps.RITGet) Then
                FreqOut.Write(p.ID, setRIT(RigControl.RIT, False))
            End If
        Else
            AdjustRITXIT(p, RigControl.RIT)
        End If
    End Sub

    Private Sub adjustXit(p As fieldFuncParm)
        If p.fromRig Then
            If RigControl.MyCaps.HasCap(RigCaps.Caps.TXITGet) Then
                FreqOut.Write("XIT", setRIT(RigControl.XIT, True))
            End If
        Else
            AdjustRITXIT(p, RigControl.XIT)
        End If
    End Sub

    Private Sub AdjustRITXIT(ByVal p As fieldFuncParm, fld As FlexBase.RITData)
        Dim rv As FlexBase.RITData = New FlexBase.RITData(fld)
        ' Get cursor position within field.
        Dim cursor As Integer = FreqOut.SelectionStart - FreqOut.Position(p.ID)
        If (cursor < 0) Or (cursor > 4) Then
            ' bogus position
            Tracing.TraceLine("adjustRITXIT:bad position " & cursor.ToString, TraceLevel.Error)
            Return
        End If
        Dim fact As Integer
        ' get multiplication factor, 0 is first position, the sign.
        If cursor = 0 Then
            fact = 0
        Else
            fact = Math.Pow(10, 4 - cursor)
        End If
        p.rv = True
        Select Case p.k
            Case Keys.Space
                rv.Active = Not rv.Active
            Case Keys.Z, Keys.C
                ' Clear
                rv.Value = 0
            Case Keys.Up, Keys.U
                rv.Value += fact
            Case Keys.Down, Keys.D
                rv.Value -= fact
            Case Keys.Oemplus
                ' If RIT, turn on XIT, set XIT to RIT, and clear RIT.
                If p.ID = "RIT" Then
                    Dim dat = New FlexBase.RITData(rv)
                    RigControl.XIT = dat
                    rv.Value = 0
                End If
            Case Keys.OemMinus, Keys.Subtract
                freqShiftFactor = -1
            Case (Keys.Oemplus Or Keys.Shift), Keys.Add
                freqShiftFactor = 1
            Case Keys.V
                ' vox on/off
                RigControl.Vox = RigControl.ToggleOffOn(RigControl.Vox)
            Case xmitKey
                ' transmit on/off
                toggleTransmit()
            Case Else
                ' Check for numeric keys
                Dim baseNum As Integer = 0
                If ((p.k >= Keys.D0) And (p.k <= Keys.D9)) Then
                    baseNum = Keys.D0
                ElseIf ((p.k >= Keys.NumPad0) And (p.k <= Keys.NumPad9)) Then
                    baseNum = Keys.NumPad0
                End If
                If baseNum <> 0 Then
                    rv.Value += freqShiftFactor * fact * (p.k - baseNum)
                Else
                    p.rv = False
                End If
        End Select
        If p.rv Then
            If (p.ID = "RIT") Then
                RigControl.RIT = rv
            Else
                RigControl.XIT = rv
            End If
        End If
    End Sub

    Private Sub adjustSMeter(p As fieldFuncParm)
        If p.fromRig Then
            FreqOut.Write(p.ID, formatMeter(SMeter.Value))
        End If
    End Sub

    ''' <summary>
    ''' toggle split or show XMit frequency if in split mode.
    ''' </summary>
    ''' <param name="p"></param>
    ''' <remarks>toggle with space or up/down. show XMit freq with "t".</remarks>
    Private Sub adjustSplit(ByVal p As fieldFuncParm)
        If p.fromRig Then
            If ShowXMITFrequency Then
                FreqOut.Write(p.ID, "T")
            ElseIf SplitVFOs Then
                FreqOut.Write(p.ID, "S")
            Else
                FreqOut.Write(p.ID, " ")
            End If
        Else
            Dim oldTX As Integer = RigControl.TXVFO
            p.rv = True
            Select Case p.k
                Case Keys.Up, Keys.Down, Keys.Space
                    ' If showXmitFrequency, turn it off, otherwise toggle split.
                    If ShowXMITFrequency Then
                        ShowXMITFrequency = False
                    Else
                        SplitVFOs = Not SplitVFOs
                    End If
                Case Keys.S
                    ' If showXmitFreq, turn it off, else toggle split.
                    If ShowXMITFrequency Then
                        ShowXMITFrequency = False
                    Else
                        SplitVFOs = Not SplitVFOs
                    End If
                Case Keys.T
                    ' if split, toggle showXmitFreq.
                    ' otherwise turn on split and showXmitFreq.
                    If SplitVFOs Then
                        ShowXMITFrequency = Not ShowXMITFrequency
                    Else
                        SplitVFOs = True
                        ShowXMITFrequency = True
                    End If
                Case Else
                    p.rv = False
            End Select
            If p.rv Then
                If SplitVFOs Then
                    ' enable the TXVFO's audio
                    RigControl.SetVFOAudio(RigControl.TXVFO, True)
                Else
                    ' disable the oldTX VFO's audio if not the RXVFO.
                    If RigControl.ValidVFO(oldTX) And (oldTX <> RigControl.RXVFO) Then
                        RigControl.SetVFOAudio(oldTX, False)
                    End If
                End If
            End If
        End If
    End Sub

    ''' <summary>
    ''' toggle the vox
    ''' </summary>
    ''' <param name="p"></param>
    ''' <remarks>toggle with space or up/down</remarks>
    Private Sub adjustVox(ByVal p As fieldFuncParm)
        If p.fromRig Then
            If RigControl.Vox Then
                FreqOut.Write(p.ID, "V")
            Else
                FreqOut.Write(p.ID, " ")
            End If
        Else
            If (p.k = Keys.Up) Or (p.k = Keys.Down) Or (p.k = Keys.Space) Then
                p.rv = True
                RigControl.Vox = RigControl.ToggleOffOn(RigControl.Vox)
            ElseIf p.k = Keys.V Then
                p.rv = True
                RigControl.Vox = FlexBase.OffOnValues.on
            End If
        End If
    End Sub
    ''' <summary>
    ''' adjust the VFO according to the key pressed.
    ''' </summary>
    ''' <param name="p"></param>
    ''' <remarks>
    ''' The keys are:  up/down/space - go to the next VFO.
    '''   m - toggle memory mode.
    '''   a or b - go to VFO a or b.
    '''   v - set the VFO to the current memory and switch to the VFO.
    '''   = - Set the next VFO to the current VFO, but doesn't switch VFOs.
    ''' </remarks>
    Private Sub adjustVFO(ByVal p As fieldFuncParm)
        If p.fromRig Then
            If MemoryMode Then
                If RigControl.CurrentMemoryChannel = -1 Then
                    MemoryMode = False
                    FreqOut.Write(p.ID, vfoLetter(RigControl.CurVFO))
                Else
                    FreqOut.Write(p.ID, "m")
                End If
            Else
                FreqOut.Write(p.ID, vfoLetter(RigControl.CurVFO))
            End If
        Else
            ' keyboard:
            ' Make sure not transmitting.
            If RigControl.Transmit Then
                Tracing.TraceLine("adjustVFO:can't change vfo while transmitting", TraceLevel.Error)
                Return
            End If
            Dim oldVal As Integer = RigControl.RXVFO
            p.rv = True
            Select Case p.k
                Case Keys.Up
                    Dim splt As Boolean = SplitVFOs
                    Dim v2 As Integer
                    v2 = RigControl.NextVFO(oldVal)
                    Tracing.TraceLine("dbgRX:" & oldVal & " " & v2)
                    RigControl.RXVFO = v2
                    While RigControl.RXVFO <> v2
                        Thread.Sleep(10)
                    End While
                    If Not splt And RigControl.CanTransmit Then
                        Tracing.TraceLine("dbgTX:" & RigControl.RXVFO)
                        RigControl.TXVFO = RigControl.RXVFO
                    End If
                    changeSliceAudio(oldVal, RigControl.RXVFO)
                Case Keys.Down
                    Dim splt As Boolean = SplitVFOs
                    RigControl.RXVFO = RigControl.PriorVFO(RigControl.RXVFO)
                    If Not splt And RigControl.CanTransmit Then
                        RigControl.TXVFO = RigControl.RXVFO
                    End If
                    changeSliceAudio(oldVal, RigControl.RXVFO)
                Case Keys.M
                    If RigControl.CurrentMemoryChannel <> -1 Then
                        MemoryMode = (Not MemoryMode)
                        If MemoryMode Then
                            RigControl.SelectMemory()
                        End If
                    Else
                        MemoryMode = False
                    End If
                Case Keys.V
                    ' just turn off memory mode.
                    MemoryMode = False
                Case Else
                    ' check for a digit
                    If (p.k >= Keys.D0) And (p.k <= Keys.D9) Then
                        Dim v As Integer = p.k - Keys.D0
                        If RigControl.ValidVFO(v) Then
                            Dim splt As Boolean = SplitVFOs
                            RigControl.RXVFO = v
                            If Not splt And RigControl.CanTransmit Then
                                RigControl.TXVFO = RigControl.RXVFO
                            End If
                            changeSliceAudio(oldVal, RigControl.RXVFO)
                        End If
                    Else
                        p.rv = False
                    End If
            End Select
        End If
    End Sub

    Private Sub adjustMem(ByVal p As fieldFuncParm)
        If p.fromRig Then
            FreqOut.Write(p.ID, RigControl.CurrentMemoryChannel.ToString)
        Else
            p.rv = False
            Dim incr As Integer
            If p.k = Keys.Up Then
                incr = 1
            ElseIf p.k = Keys.Down Then
                incr = -1
            Else
                Return
            End If
            p.rv = True
            Dim val As Integer = RigControl.CurrentMemoryChannel + incr
            If val >= RigControl.NumberOfMemories Then
                val = 0
            ElseIf val < 0 Then
                val = RigControl.NumberOfMemories - 1
            End If
            RigControl.CurrentMemoryChannel = val
            RigControl.SelectMemory()
        End If
    End Sub

    Private Sub adjustOffset(p As fieldFuncParm)
        If p.fromRig Then
            FreqOut.Write("Offset", formatOffset(RigControl.OffsetDirection))
        Else
            Select Case p.k
                Case Keys.Oemplus
                    RigControl.OffsetDirection = FlexBase.OffsetDirections.plus
                    p.rv = True
                Case Keys.OemMinus
                    RigControl.OffsetDirection = FlexBase.OffsetDirections.minus
                    p.rv = True
                Case Keys.Space, Keys.Down
                    ' cycle the value
                    Dim n As Integer = [Enum].GetValues(GetType(FlexBase.OffsetDirections)).Length
                    Dim val As FlexBase.OffsetDirections = RigControl.OffsetDirection
                    RigControl.OffsetDirection = CType(((val + 1) Mod n), FlexBase.OffsetDirections)
                    p.rv = True
                Case Keys.Up
                    ' cycle the value up
                    Dim n As Integer = [Enum].GetValues(GetType(FlexBase.OffsetDirections)).Length
                    Dim val As FlexBase.OffsetDirections = RigControl.OffsetDirection
                    If val = 0 Then
                        val = n - 1
                    Else
                        val -= 1
                    End If
                    RigControl.OffsetDirection = CType(val, FlexBase.OffsetDirections)
                    p.rv = True
            End Select
        End If
    End Sub

    Private Const soundChar As Char = "s"
    Private Const muteChar As Char = "m"
    Private Const transmitChar As Char = "\"
    Private Sub adjustRigField(p As fieldFuncParm)
        ' p.ID is the VFO or slice.
        Dim vfo As Integer = System.Int32.Parse(p.ID)
        Dim state = RigControl.SliceState(vfo)

        If p.fromRig Then
            Dim theChar As Char
            Select Case state
                Case FlexBase.SliceStates.mine
                    If RigControl.Transmit And (vfo = RigControl.TXVFO) Then
                        theChar = transmitChar
                    Else
                        If RigControl.GetVFOAudio(vfo) Then
                            theChar = soundChar
                        Else
                            theChar = muteChar
                        End If
                        ' uppercase for the transmit VFO.
                        If vfo = RigControl.TXVFO Then
                            theChar = Char.ToUpper(theChar)
                        End If
                    End If
                Case FlexBase.SliceStates.available
                    theChar = "."
                Case Else
                    theChar = "-"
            End Select
            FreqOut.Write(p.ID, theChar)

        Else
            ' from the user
            ' must be without modifiers.
            If (p.k And Keys.Modifiers) <> 0 Then
                Return
            End If
            p.rv = True
            Select Case p.k
                Case Keys.D0, Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5, Keys.D6, Keys.D7
                    Dim k = System.Int32.Parse(p.k) - 48
                    DirectCast(RigControl, FlexBase).CopyVFO(CInt(p.ID), CInt(k))
                Case Keys.OemPeriod
                    If state = FlexBase.SliceStates.available Then
                        RigControl.NewSlice()
                    ElseIf state = FlexBase.SliceStates.mine Then
                        RigControl.RemoveSlice(vfo)
                    End If
                Case Keys.A
                    If state = FlexBase.SliceStates.mine Then
                        ' active slice (RXVFO)
                        RigControl.RXVFO = vfo
                    End If
                Case Keys.T
                    If (state = FlexBase.SliceStates.mine) And RigControl.CanTransmit Then
                        ' transmit slice (TXVFO)
                        RigControl.TXVFO = vfo
                    End If
                Case Keys.X
                    ' transceive
                    If state = FlexBase.SliceStates.mine Then
                        RigControl.RXVFO = vfo
                        If RigControl.CanTransmit Then
                            RigControl.TXVFO = vfo
                        End If
                    End If
                Case Keys.L
                    If state = FlexBase.SliceStates.mine Then
                        ' pan left side
                        RigControl.SetVFOPan(vfo, FlexBase.MinPan)
                    End If
                Case Keys.C
                    If state = FlexBase.SliceStates.mine Then
                        ' pan center
                        Dim val = ((FlexBase.MaxPan - FlexBase.MinPan) / 2) + FlexBase.MinPan
                        RigControl.SetVFOPan(vfo, val)
                    End If
                Case Keys.R
                    If state = FlexBase.SliceStates.mine Then
                        ' pan right side
                        RigControl.SetVFOPan(vfo, FlexBase.MaxPan)
                    End If
                Case Keys.PageUp
                    If state = FlexBase.SliceStates.mine Then
                        ' pan left
                        Dim val = RigControl.GetVFOPan(vfo) - 10
                        RigControl.SetVFOPan(vfo, val)
                    End If
                Case Keys.PageDown
                    If state = FlexBase.SliceStates.mine Then
                        ' pan right
                        Dim val = RigControl.GetVFOPan(vfo) + 10
                        RigControl.SetVFOPan(vfo, val)
                    End If
                Case Keys.Up
                    If state = FlexBase.SliceStates.mine Then
                        ' volume up
                        Dim val = RigControl.GetVFOGain(vfo) + 10
                        RigControl.SetVFOGain(vfo, val)
                    End If
                Case Keys.Down
                    If state = FlexBase.SliceStates.mine Then
                        ' volume down
                        Dim val = RigControl.GetVFOGain(vfo) - 10
                        RigControl.SetVFOGain(vfo, val)
                    End If
                Case Keys.M, Keys.S, Keys.Space
                    If state = FlexBase.SliceStates.mine Then
                        ' setup future states array, used because state changes are queued.
                        Dim futures(RigControl.MyNumSlices - 1) As Boolean
                        For i As Integer = 0 To RigControl.MyNumSlices - 1
                            futures(i) = RigControl.GetVFOAudio(i)
                        Next

                        Dim k As Keys = p.k
                        ' space is a toggle.
                        If k = Keys.Space Then
                            If RigControl.GetVFOAudio(vfo) Then
                                k = Keys.M ' mute
                            Else
                                k = Keys.S ' sound
                            End If
                        End If
                        If k = Keys.M Then
                            ' mute
                            RigControl.SetVFOAudio(vfo, False)
                            futures(vfo) = False
                        Else
                            ' sound
                            RigControl.SetVFOAudio(vfo, True)
                            futures(vfo) = True
                        End If
                        ' If only one slice is sounding, make it the active slice.
                        Dim active As Integer = -1
                        For i As Integer = 0 To RigControl.MyNumSlices - 1
                            If futures(i) Then
                                If active = -1 Then
                                    ' only sounding slice so far.
                                    active = i
                                Else
                                    ' multiple sounding slices
                                    active = -1
                                    Exit For
                                End If
                            End If
                        Next
                        If active <> -1 Then
                            ' Only one sounding slice.
                            Dim wasSplit = SplitVFOs
                            RigControl.RXVFO = active
                            If Not wasSplit And RigControl.CanTransmit Then
                                RigControl.TXVFO = active
                            End If
                        End If
                    End If
                Case Else
                    ' unhandled
                    p.rv = False
            End Select
        End If
    End Sub

    ''' <summary>
    ''' Default key handler for the Freqout box.
    ''' Sets e.SuppressKeyPress if handles the key.
    ''' </summary>
    ''' <param name="e">KeyEventArgs</param>
    Private Sub FreqoutKeyHandler(e As KeyEventArgs)
    End Sub

    ' Display the freqOut field.
    ' Also keep track of our position within the frequency value.
    ' If the actual frequency changed, select it for a brief period.
    Private Sub writeFreq()
        Dim str As String = FreqOut.Read(freqID)
        Dim i As Integer
        Dim realFreq As Boolean = (str.IndexOf("."c) >= 0)
        If firstFreqDisplay And realFreq Then
            firstFreqDisplay = False
            Dim fp = initialFreqPos
            ' Set the cursor
            i = str.Length - 1
            While fp < 0
                i -= 1
                If str(i) = "."c Then
                    i -= 1
                End If
                fp += 1
            End While
            FreqOut.SelectionStart = i + FreqOut.Position(freqID)
        End If
        FreqOut.Display()
    End Sub

    Private currentFields() As MainBox.Field
    Private usingMem As Boolean = False
    ' Update the freqOut display.
    ' Write it to the screen if anything actually changed.
    Private Sub showFrequency()
        Tracing.TraceLine("showFrequency", TraceLevel.Verbose)
        If MemoryMode And (Not usingMem) Then
            ' Now using a memory.
            currentFields = memFreqFields
            FreqOut.Populate(memFreqFields)
            usingMem = True
        ElseIf (Not MemoryMode) And usingMem Then
            ' Now it's a vfo.
            currentFields = vfoFreqFields
            FreqOut.Populate(vfoFreqFields)
            usingMem = False
        End If
        ' Otherwise there was no change.
        For id As Integer = 0 To currentFields.Length - 1
            ' This sets parm.FromRig.
            Dim parm = New fieldFuncParm(currentFields(id).Key)
            FreqOut.Function(parm.ID, parm)
        Next
        If FreqOut.Changed Then
            writeFreq()
        End If
    End Sub
    Private Function setRIT(ByVal rit As FlexBase.RITData, ByVal xit As Boolean) As String
        Dim rv As String
        If rit.Active Then
            If rit.Value < 0 Then
                rv = "-"
            Else
                rv = "+"
            End If
            rv &= Abs(rit.Value).ToString("d4")
        Else
            ' not active
            If xit Then
                rv = " xxxx"
            Else
                rv = " rrrr"
            End If
        End If
        Return rv
    End Function
    ' Actually returns the number's string
    Private Function vfoLetter(ByVal v As Integer) As String
        Return v.ToString
    End Function

    Private Function formatMeter(val As Integer) As String
        If (RigControl Is Nothing) Then
            Return " "
        End If
        ' If not transmitting, may be S9 plus.
        Dim rv As String
        If RigControl.Transmit Then
            ' Note that we read the swr directly.
            If (W2WattMeter IsNot Nothing) AndAlso W2WattMeter.ShowSWR Then
                Dim rv2 As String = W2WattMeter.SWR()
                If rv2.Length > meterSize Then
                    rv = rv2.Substring(0, meterSize)
                Else
                    rv = rv2
                End If
            Else
                rv = val.ToString()
            End If
        Else
            ' Receive
            If RigControl.SmeterInDBM Then
                rv = val
            Else
                ' s-units
                If val > 9 Then
                    rv = "+" & (val - 9).ToString()
                Else
                    rv = val.ToString()
                End If
            End If
        End If
            Return rv
    End Function

    Private Function formatOffset(offset As FlexBase.OffsetDirections) As Char
        Static outChars As Char() = {" ", "-", "+", "e"}
        Dim rv As Char
        Try
            If (RigControl.Mode IsNot Nothing) AndAlso (RigControl.Mode.ToString = "FM") Then
                rv = outChars(offset)
            Else
                rv = " "
            End If
        Catch ex As Exception
            Tracing.TraceLine("formatOffset error:" & offset, TraceLevel.Error)
            rv = " "
        End Try
        Return rv
    End Function

    Private oldSWR As String = ""
    Friend Sub UpdateStatus()
        Tracing.TraceLine("updateStatus", TraceLevel.Verbose)
        If Ending Then
            Return
        End If
        If RigControl IsNot Nothing Then
            UpdateAdvancedFeatureMenus()
        End If
        If (RigControl Is Nothing) OrElse Not RigControl.IsConnected Then
            'powerNowOff()
            Return
        End If

        Try
            ' don't assume power on initially.
            If Power Then
                showFrequency()

                Tracing.TraceLine("UpdateStatus:doing combos", TraceLevel.Verbose)
                For Each c As Combo In combos
                    If c.Enabled Then
                        c.UpdateDisplay()
                    End If
                Next

                ' Update the rig-dependent fields.
                If (RigControl.RigFields IsNot Nothing) AndAlso RigControl.RigFields.RigControl.Enabled Then
                    Tracing.TraceLine("UpdateStatus:doing RigFields", TraceLevel.Verbose)
                    RigControl.RigFields.RigUpdate()
                End If

                ' See if Flex is tuning.
                If (OpenParms.GetSWRText IsNot Nothing) AndAlso
                   RigControl.FlexTunerOn And
                   (RigControl.FlexTunerType = FlexBase.FlexTunerTypes.manual) Then
                    Dim SWRtext = OpenParms.GetSWRText()
                    If SWRtext <> oldSWR Then
                        oldSWR = SWRtext
                        setButtonText(AntennaTuneButton, oldSWR)
                    End If
                End If

#If 0 Then
                ' See if any data to display.
                Tracing.TraceLine("UpdateStatus:doing receive data", TraceLevel.Verbose)
                If RigControl.CanReceiveData Then
                    WriteText(WindowIDs.ReceiveDataOut, RigControl.DataReceived, False)
                End If
#End If

            Else
                ' power is off
            End If

            ' Update the status.
            If StatusBox.Changed Then
                StatusBox.Display()
            End If
        Catch ex As Exception
            If Not Power Then
                Tracing.TraceLine("updateStatus:power is off", TraceLevel.Error)
            Else
                Tracing.ErrMessageTrace(ex, True, True)
                'Tracing.TraceLine("updateStatus:" & ex.Message, TraceLevel.Error)
                powerNowOff()
            End If
        End Try
        Tracing.TraceLine("updateStatus:done", TraceLevel.Verbose)
    End Sub

    ' enable or disable a control.
    Private Delegate Sub enableDisableBoxDel(box As Control, value As Boolean)
    Private Shared enableDisableFunc As enableDisableBoxDel =
        Sub(box As Control, value As Boolean)
            box.Enabled = value
        End Sub
    Private Sub enableDisableBox(box As Control, value As Boolean)
        If box.InvokeRequired Then
            box.Invoke(enableDisableFunc, New Object() {box, value})
        Else
            enableDisableFunc(box, value)
        End If
    End Sub
    Private Sub enableDisableWindowControls(value As Boolean)
        For Each c As Control In enableDisableControls
            enableDisableBox(c, value)
        Next
    End Sub

    Private Sub knobOnOffHandler(sender As Object, e As FlexKnob.KnobOnOffArgs)
        Tracing.TraceLine("knobOnOffHandler:" & e.Status.ToString, TraceLevel.Info)
        Dim onOffText As String = ""
        Select Case e.Status
            Case FlexKnob.KnobStatus_t.fullOn
                onOffText = OnWord
            Case FlexKnob.KnobStatus_t.off
                onOffText = OffWord
            Case FlexKnob.KnobStatus_t.locked
                onOffText = LockedWord
        End Select
        StatusBox.Write("Knob", onOffText)
    End Sub

    Private Sub powerStatusHandler(sender As Object, status As Boolean)
        If status Then
            powerNowOn()
        Else
            powerNowOff()
        End If
    End Sub

    Private Sub powerNowOn()
        Tracing.TraceLine("Form1 powerNowOn", TraceLevel.Error)
        TextOut.PerformGenericFunction(FreqOut,
            Sub()
                ' setup filter box
                If RigControl.RigFields IsNot Nothing Then
                    SuspendLayout()
                    RigControl.RigFields.RigControl.Location = RigFieldsBox.Location
                    RigControl.RigFields.RigControl.Size = RigFieldsBox.Size
                    RigControl.RigFields.RigControl.TabIndex = RigFieldsBox.TabIndex
                    AddHandler RigControl.RigFields.RigControl.KeyDown, AddressOf doCommand_KeyDown
                    Controls.Add(RigControl.RigFields.RigControl)
                    enableDisableControls.Add(RigControl.RigFields.RigControl)
                    ResumeLayout()
                End If
#If LeaveBootTraceOn = 0 Then
                turnTracingOff()
#End If
                StartDailyTraceIfEnabled()
                SetupOperationsMenu()
                setupFreqout()
            End Sub)
        'Tracing.TraceLine("rig caps:" & RigControl.MyCaps.ToString(), TraceLevel.Info)
        invokeConfigVariableControls()
        enableDisableWindowControls(True)
        AddHandler FlexKnob.KnobOnOffEvent, AddressOf knobOnOffHandler
        SetupKnob()
        StatusBox.Write("Power", OnWord)
        StatusBox.Write("Memories", RigControl.NumberOfMemories.ToString)
        'knobOnOffHandler(Me, RigControl.KnobStatus)
        AddHandler RigControl.TransmitChange, AddressOf transmitChangeProc
        AddHandler RigControl.FlexAntTunerStartStop, AddressOf FlexAntTuneStartStopHandler
        AddHandler RigControl.ConnectedEvent, AddressOf connectedEventHandler
        Power = True
    End Sub

    Friend Sub powerNowOff()
        Tracing.TraceLine("Form1 powerNowOff", TraceLevel.Info)
        RemoveHandler FlexKnob.KnobOnOffEvent, AddressOf knobOnOffHandler
        If RigControl IsNot Nothing Then
            RemoveHandler RigControl.TransmitChange, AddressOf transmitChangeProc
            RemoveHandler RigControl.FlexAntTunerStartStop, AddressOf FlexAntTuneStartStopHandler
            RemoveHandler RigControl.ConnectedEvent, AddressOf connectedEventHandler
        End If
        Power = False
        If Not Ending Then
            clearMainWindow()
            StatusBox.Write("Power", OffWord)
            enableDisableWindowControls(False)
        End If
    End Sub

    Private Sub turnTracingOff()
        If BootTrace Then
            Tracing.TraceLine("Boot tracing off")
            Tracing.On = False
            BootTrace = False
        End If
    End Sub

    Private Sub connectedEventHandler(sender As Object, e As FlexBase.ConnectedArg)
        Tracing.TraceLine("connectedEventHandler:" & Power.ToString & " " & e.Connected.ToString, TraceLevel.Info)
        If Power And Not e.Connected Then
            powerNowOff()
            MessageBox.Show(nowDisconnected, ErrorHdr, MessageBoxButtons.OK)
        End If
    End Sub

    Private Sub FileExitToolStripMenuItem_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles FileExitToolStripMenuItem.Click
        Tracing.TraceLine("Form1 FileExitToolstripMenuItem", TraceLevel.Info)
        Ending = True

        ' Check the Logging Mode panel for an unsaved QSO entry.
        If LoggingLogPanel IsNot Nothing AndAlso Not LoggingLogPanel.PromptSaveBeforeClose() Then
            Ending = False
            Return
        End If

        ' Ask user to write an entry if necessary (Classic log entry form).
        ' If false is returned, they want to cancel the exit.
        ' Note that if no write is necessary, we'll exit w/o asking.
        If Not LogEntry.optionalWrite Then
            Ending = False
            Return
        End If
        Try
            LogEntry.Close()
            Logs.Done()
            If LookupStation IsNot Nothing Then
                LookupStation.Finished()
            End If
            'remoteLan = False
            If Commands IsNot Nothing Then
                Commands.ClusterShutdown()
            End If
            CloseTheRadio()
            If W2WattMeter IsNot Nothing Then
                W2WattMeter.Dispose()
            End If
            setScreenSaver(onExitScreenSaver)
            Tracing.TraceLine("exit:screen saver set:" & onExitScreenSaver.ToString, TraceLevel.Info)
        Catch ex As Exception
            Tracing.TraceLine("Form1 FileExitToolstripMenuItem:" & ex.Message, TraceLevel.Error)
        End Try
        Tracing.TraceLine("End.")
        Tracing.On = False
        Me.Dispose()
        Environment.Exit(0)
    End Sub

    Private Sub W2ConfigToolStripMenuItem_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles W2ConfigToolStripMenuItem.Click
        ConfigW2()
    End Sub

    Private Sub ListOperatorsMenuItem_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles ListOperatorsMenuItem.Click
        ' Cleanup any outstanding log activity beforehand.
        'If Not ContactLog.Cleanup() Then
        'Return
        'End If
        Lister.TheList = Operators
        Lister.ShowDialog()
    End Sub
    ' Handles Operators.ConfigEvent
    Private Sub operatorChanged(ByVal sender As Object, ByVal e As ConfigArgs)
        If CurrentOp IsNot Nothing Then
            While (ContactLog IsNot Nothing) AndAlso (Not ContactLog.Cleanup)
            End While
            ConfigContactLog()
        End If

        If RigControl IsNot Nothing Then
            RigControl.OperatorChangeHandler()
        End If

        ' Apply the new operator's UI mode preference.
        ApplyUIMode()
    End Sub

    Private selectorThread As Thread
    Private Sub SelectRigMenuItem_Click(sender As Object, e As EventArgs) Handles SelectRigMenuItem.Click
        Tracing.TraceLine("SelectRigMenuItem_Click", TraceLevel.Info)
        Try
            If RigControl IsNot Nothing Then
                ' Announce disconnection for screen reader users
                Dim radioName = RigControl.RadioNickname
                If Not String.IsNullOrEmpty(radioName) Then
                    Radios.ScreenReaderOutput.Speak("Disconnecting from " & radioName, True)
                Else
                    Radios.ScreenReaderOutput.Speak("Disconnecting from radio", True)
                End If
                CloseTheRadio()
            End If
            openTheRadio(False) ' a subsequent open
        Catch ex As Exception
            Tracing.TraceLine("SelectRigMenuItem_Click:exception " & ex.Message, TraceLevel.Error)
        End Try
    End Sub

    Private Delegate Sub cvcdel()
    Private Sub invokeConfigVariableControls()
        Tracing.TraceLine("invokeConfigVariableControls", TraceLevel.Info)
        Dim cvcRtn As cvcdel = AddressOf configVariableControls
        ' TransmitButton is always present.
        If TransmitButton.InvokeRequired Then
            TransmitButton.Invoke(cvcRtn)
        Else
            cvcRtn()
        End If
        'Tracing.TraceLine("invokeConfigVariableControls:done", TraceLevel.Info)
    End Sub
    Private Sub rigCapsChanged(arg As FlexBase.CapsChangeArg)
        Tracing.TraceLine("rigCapsChanged:" + arg.NewCaps.ToString(), TraceLevel.Info)
        invokeConfigVariableControls()
        enableDisableWindowControls(True)
    End Sub

    Private Function doCommand(ByVal e As System.Windows.Forms.KeyEventArgs) As Boolean
        Dim rv As Boolean = Commands.DoCommand(e.KeyData)
        Return rv
    End Function

    Public Sub DisplayHelp()
        DisplayHelp(ShowHelpTypes.standard)
    End Sub

    Private Sub DisplayHelp(helpType As ShowHelpTypes)
        Dim helper = New ShowHelp(helpType)
        helper.ShowDialog()
        helper.Dispose()
    End Sub

    Private Sub clearMainWindow()
        FreqOut.Clear()
        For Each o As Object In Me.Controls
            Select Case o.GetType.Name
                Case "TextBox"
                    Dim t As TextBox = o
                    If t.InvokeRequired Then
                        t.Invoke(Sub() t.Text = "")
                    Else
                        t.Text = ""
                    End If
                Case "ListBox"
                    Dim l As ListBox = o
                    If l.InvokeRequired Then
                        l.Invoke(Sub() l.SelectedIndex = -1)
                    Else
                        l.SelectedIndex = -1
                    End If
                Case "ComboBox"
                    Dim cb As ComboBox = o
                    If cb.InvokeRequired Then
                        cb.Invoke(Sub()
                                      cb.Text = ""
                                      cb.SelectedIndex = -1
                                  End Sub)
                    Else
                        cb.Text = ""
                        cb.SelectedIndex = -1
                    End If
            End Select
        Next
    End Sub

    Friend Sub doCommand_KeyDown(ByVal sender As System.Object, ByVal e As System.Windows.Forms.KeyEventArgs) Handles ModeControl.BoxKeydown, AntennaTuneButton.KeyDown, TXTuneControl.BoxKeydown, StatusBox.BoxKeydown, ReceivedTextBox.KeyDown, TransmitButton.KeyDown
        e.SuppressKeyPress = doCommand(e)
        Tracing.TraceLine("doCommand_KeyDown: DoCommand returned " & e.SuppressKeyPress.ToString, TraceLevel.Info)
    End Sub

    Private Sub LogCharacteristicsMenuItem_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles LogCharacteristicsMenuItem.Click
        ' Log Characteristics opens the log file independently to read/write the header.
        ' Close all sessions on ContactLog so the file isn't locked, then re-open after.
        Dim wasInLogging = (ActiveUIMode = UIMode.Logging AndAlso LoggingPanelSession IsNot Nothing)
        If wasInLogging Then
            LoggingLogPanel.SaveState()
            LoggingPanelSession.EndSession()
            LoggingPanelSession = Nothing
        End If
        LogEntry.EndSession()   ' Safe even if no session is active

        ' Set log file characteristics.
        Commands.getLogFileName()

        ' Re-open sessions that were active.
        LogEntry.StartLogSession()
        If wasInLogging Then
            InitializeLoggingSession()
        End If
    End Sub

    Delegate Sub setText(ByVal tb As TextBox, ByVal s As String,
                         ByVal cursor As Integer, ByVal cl As Boolean)
    Private Sub toTextbox(ByVal tb As TextBox, ByVal s As String,
                          ByVal cursor As Integer, ByVal cl As Boolean)
        Dim txt As String
        If cl Then
            txt = s
        Else
            txt = tb.Text & s
        End If
        ' Preserve cursor position if passed cursor is -1.
        If cursor = -1 Then
            cursor = tb.SelectionStart
        ElseIf cursor < 0 Then
            Dim maxLen As Integer = -cursor
            If txt.Length > maxLen Then
                txt = txt.Substring(txt.Length - maxLen)
            End If
            cursor = txt.Length
        ElseIf cursor = 0 Then
            ' Set cursor to end of text.
            cursor = txt.Length
        End If
        tb.Text = txt
        tb.SelectionStart = cursor
        tb.ScrollToCaret()
    End Sub
    ''' <summary>
    ''' Write text to a main window textBox.
    ''' </summary>
    ''' <param name="tbid"></param>
    ''' <param name="s"></param>
    ''' <param name="cl"></param>
    ''' <remarks></remarks>
    Private Sub iWriteText(ByVal tbid As WindowIDs, ByVal s As String, ByVal cl As Boolean)
        iWriteTextX(tbid, s, -1, cl)
    End Sub
    Private Sub iWriteTextX(ByVal tbid As WindowIDs, ByVal s As String,
                           ByVal cursor As Integer, ByVal cl As Boolean)
        If Not Ending Then
            Dim tb As TextBox = TBIDToTB(tbid)
            Try
                If tb.InvokeRequired Then
                    Dim rtn As New setText(AddressOf toTextbox)
                    tb.Parent.Invoke(rtn, New Object() {tb, s, cursor, cl})
                Else
                    toTextbox(tb, s, cursor, cl)
                End If
            Catch ex As Exception
                Tracing.ErrMessageTrace(ex)
            End Try
        End If
    End Sub

    Private Sub ImportMenuItem_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles ImportMenuItem.Click
        ImportForm.ShowDialog()
    End Sub

    Private Sub ExportMenuItem_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles ExportMenuItem.Click
        ExportForm.ShowDialog()
    End Sub

    Private Sub ScreenSaverMenuItem_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles ScreenSaverMenuItem.Click
        ' Toggle the screen saver.
        'onExitScreenSaver = Not ScreenSaver.GetScreenSaverActive()
        'ScreenSaver.SetScreenSaverActive(onExitScreenSaver)
        ScreenSaver.SetScreenSaverActive(Not ScreenSaver.GetScreenSaverActive())
    End Sub
    ''' <summary>
    ''' set the screen saver on or off
    ''' </summary>
    ''' <param name="val">true to set it on</param>
    ''' <returns>original value</returns>
    ''' <remarks></remarks>
    Private Function setScreenSaver(ByVal val As Boolean) As Boolean
        Dim orig As Boolean = ScreenSaver.GetScreenSaverActive
        ScreenSaver.SetScreenSaverActive(val)
        Return orig
    End Function

    Private Sub ChangeKeysMenuItem_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles ChangeKeysMenuItem.Click
        DefineCommands.ShowDialog()
    End Sub

    Private Sub RestoreKeyMappingMenuItem_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles RestoreKeyMappingMenuItem.Click
        Commands.keyTableToDefault(True)
    End Sub

    Private Sub HelpPageItem_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles HelpPageItem.Click
        Dim fn As String =
            ProgramDirectory & "\JJFlexRadioReadme.htm"
        Dim psi As New System.Diagnostics.ProcessStartInfo(fn)
        psi.UseShellExecute = True
        System.Diagnostics.Process.Start(psi)
    End Sub

    Private Sub HelpKeysItem_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles HelpKeysItem.Click
        DisplayHelp(ShowHelpTypes.standard)
    End Sub

    Private Sub HelpKeysAlphaItem_Click(sender As Object, e As EventArgs) Handles HelpKeysAlphaItem.Click
        DisplayHelp(ShowHelpTypes.alphabetic)
    End Sub

    Private Sub HelpKeysGroupItem_Click(sender As Object, e As EventArgs) Handles HelpKeysGroupItem.Click
        DisplayHelp(ShowHelpTypes.byGroup)
    End Sub

    Private Sub HelpAboutItem_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles HelpAboutItem.Click
        AboutProgram.ShowDialog()
    End Sub

    ''' <summary>
    ''' ComboBoxes - used to call methods.
    ''' </summary>
    Private combos As List(Of Combo)
    ''' <summary>
    ''' Controls to be enabled/disabled according to the power status.
    ''' </summary>
    Private enableDisableControls As List(Of Control)

    ' FreqBox handling.  Also see setupFreqout().
    Private Const meterSize As Integer = 4 ' field size
    Private vfoFreqFields() As MainBox.Field
    ' the fixed stuff, (i.e.) freq, RIT, etc.
    Private vfoFields() As MainBox.Field =
        {New MainBox.Field("SMeter", meterSize, "", "", AddressOf adjustSMeter),
         New MainBox.Field("Split", 1, "", "", AddressOf adjustSplit),
         New MainBox.Field("VOX", 1, "", "", AddressOf adjustVox),
         New MainBox.Field("VFO", 1, "", "", AddressOf adjustVFO),
         New MainBox.Field("Freq", 12, "", "", AddressOf AdjustFreq),
         New MainBox.Field("Offset", 1, "", "", AddressOf adjustOffset),
         New MainBox.Field("RIT", 5, "", "", AddressOf adjustRit),
         New MainBox.Field("XIT", 5, " ", "", AddressOf adjustXit)}
    Private memFreqFields() As MainBox.Field
    ' the fixed stuff, (i.e.) freq, RIT, etc.
    Private memFields() As MainBox.Field =
        {New MainBox.Field("SMeter", 4, "", "", AddressOf adjustSMeter),
         New MainBox.Field("Split", 1, "", "", AddressOf adjustSplit),
         New MainBox.Field("VOX", 1, "", "", AddressOf adjustVox),
         New MainBox.Field("VFO", 1, "", "", AddressOf adjustVFO),
         New MainBox.Field("Memory", 3, "", "", AddressOf adjustMem),
         New MainBox.Field("Freq", 12, "", "", AddressOf AdjustFreq),
         New MainBox.Field("Offset", 1, "", "", AddressOf adjustOffset),
         New MainBox.Field("RIT", 5, " ", "", AddressOf adjustRit),
         New MainBox.Field("XIT", 5, " ", "", AddressOf adjustXit)}
    Private Const freqID As String = "Freq"
    ' Passed to field functions.
    Class fieldFuncParm
        Public ID As String
        Public fromRig As Boolean = False
        Public k As Keys
        Public rv As Boolean
        Public Sub New(i As String, ByVal key As Keys, ByVal ret As Boolean)
            ID = i
            k = key
            rv = ret
        End Sub
        Public Sub New(i As String)
            ID = i
            fromRig = True
        End Sub
    End Class

    ' Return the id of the freqOut field we're in, or -1 if not.
    Private Function freqoutField() As Integer
        Dim rv As Integer = -1
        Dim pos As Integer = FreqOut.SelectionStart
        For i As Integer = 0 To FreqOut.NumberOfFields - 1
            If (pos >= FreqOut.Position(i)) And (pos < FreqOut.Position(i) + FreqOut.Length(i)) Then
                rv = i
                Exit For
            End If
        Next
        Return rv
    End Function
    Friend modeList As ArrayList

    Friend Class TrueFalseElement
        Private val As Boolean
        Public ReadOnly Property Display
            Get
                Return val.ToString
            End Get
        End Property
        Public ReadOnly Property RigItem
            Get
                Return val
            End Get
        End Property
        Public Sub New(ByVal a As Boolean)
            val = a
        End Sub
    End Class
    Private TXTuneList As ArrayList

    ' Callback for peak level.
    Private Function getSMeter() As Integer
        Dim rv As Integer = 0
        If (RigControl IsNot Nothing) AndAlso Power Then
            If RigControl.Transmit Then
                If (W2WattMeter IsNot Nothing) AndAlso W2WattMeter.IsUseable Then
                    ' Don't read meter here if showing SWR.
                    If Not W2WattMeter.ShowSWR Then
                        rv = CType(W2WattMeter.ForwardPower, Integer)
                    End If
                Else
                    rv = CType(RigControl.SMeter, Integer)
                End If
            Else
                rv = CType(RigControl.SMeter, Integer)
            End If
        End If
        Return rv
    End Function

    ' Called when Transmit changes.
    Private Sub transmitChangeProc(sender As Object, transmit As Boolean)
        SMeter.ResetPeak()
    End Sub

    Friend Sub setupBoxes()
        Tracing.TraceLine("setupBoxes", TraceLevel.Info)
        If RigControl Is Nothing Then
            Tracing.TraceLine("SetupBoxes:no rig", TraceLevel.Error)
            Return
        End If
        clearMainWindow()

        ' These levels' values are shown as the peak over a time period.
        'SMeter = New Levels(Function() RigControl.SMeter)
        SMeter = New Levels(AddressOf getSMeter)
        'SMeter.Cycle = 2000
        SMeter.Peak = True

        combos = New List(Of Combo)
        enableDisableControls = New List(Of Control)

        ModeControl.Visible = (ActiveUIMode <> UIMode.Logging)
        ModeControl.Clear()
        ModeControl.Enabled = True
        modeList = New ArrayList
        For Each Val As String In RigCaps.ModeTable
            modeList.Add(Val)
        Next
        ModeControl.TheList = modeList
        ModeControl.UpdateDisplayFunction = Function() RigControl.Mode
        ModeControl.UpdateRigFunction =
            Sub(v As String)
                If Not Power Then
                    Tracing.TraceLine("mode:no power", TraceLevel.Error)
                    Return
                End If
                RigControl.Mode = v
            End Sub
        combos.Add(ModeControl)
        enableDisableControls.Add(ModeControl)

        ' Add other controls to the enable/disable collection.
        enableDisableControls.Add(SentTextBox)

        ' Add any config variable controls.
        invokeConfigVariableControls()
    End Sub

    Private Sub configVariableControls()
        Tracing.TraceLine("configVariableControls", TraceLevel.Info)
        enableDisableControls.Remove(TransmitButton)
        'TransmitButton.Enabled = False
        TransmitButton.Visible = (ActiveUIMode <> UIMode.Logging)
        If RigControl.MyCaps.HasCap(RigCaps.Caps.ManualTransmit) Then
            'TransmitButton.Enabled = True
            'TransmitButton.Visible = True
            enableDisableControls.Add(TransmitButton)
        End If

        setButtonText(AntennaTuneButton, antennaTuneButtonText)
        'TXTuneControl.Enabled = False
        TXTuneControl.Visible = (ActiveUIMode <> UIMode.Logging)
        'AntennaTuneButton.Enabled = False
        AntennaTuneButton.Visible = (ActiveUIMode <> UIMode.Logging)
        combos.Remove(TXTuneControl)
        enableDisableControls.Remove(TXTuneControl)
        enableDisableControls.Remove(AntennaTuneButton)
        If RigControl.MyCaps.HasCap(RigCaps.Caps.ATSet) Then
            ' auto tuner
            Tracing.TraceLine("configVariableControls:autoTuner", TraceLevel.Info)
            TXTuneList = New ArrayList
            TXTuneList.Add(New TrueFalseElement(False))
            TXTuneList.Add(New TrueFalseElement(True))
            TXTuneControl.TheList = TXTuneList
            TXTuneControl.UpdateDisplayFunction =
                Function()
                    Return (RigControl.FlexTunerType = FlexBase.FlexTunerTypes.auto)
                End Function
            TXTuneControl.UpdateRigFunction =
                Sub(v As Boolean)
                    If Not Power Then
                        Tracing.TraceLine("TXTune:no power", TraceLevel.Error)
                        Return
                    End If
                    Dim val = FlexBase.FlexTunerTypes.manual
                    If (v And RigControl.MyCaps.HasCap(RigCaps.Caps.ATGet)) Then
                        val = FlexBase.FlexTunerTypes.auto
                    End If
                    RigControl.FlexTunerType = val
                End Sub
            combos.Add(TXTuneControl)
            enableDisableControls.Add(TXTuneControl)
            TXTuneControl.Clear()
            'TXTuneControl.Enabled = True
            'TXTuneControl.Visible = True

            'AntennaTuneButton.Enabled = True
            'AntennaTuneButton.Visible = True
            enableDisableControls.Add(AntennaTuneButton)
        ElseIf RigControl.MyCaps.HasCap(RigCaps.Caps.ManualATSet) Then
            Tracing.TraceLine("configVariableControls:manual tuner", TraceLevel.Info)
            'AntennaTuneButton.Enabled = True
            'AntennaTuneButton.Visible = True
            enableDisableControls.Add(AntennaTuneButton)
        End If
        Tracing.TraceLine("configVariableControls:done", TraceLevel.Info)
    End Sub

    Private Sub setupFreqout()
        ' setup the main frequency box fields.
        ' Note the slices are on the left side, named "0" through "n".
        Dim VFOs As Integer = RigControl.TotalNumSlices
        ReDim vfoFreqFields(VFOs + vfoFields.Length - 1)
        ReDim memFreqFields(VFOs + memFields.Length - 1)
        For i As Integer = 0 To VFOs - 1
            Dim name As String = i.ToString() ' name is really an index
            vfoFreqFields(i) = New MainBox.Field(name, 1, "", "", AddressOf adjustRigField)
            memFreqFields(i) = New MainBox.Field(name, 1, "", "", AddressOf adjustRigField)
        Next
        ' Setup the fixed stuff, (i.e.) freq, RIT, etc.
        For i As Integer = VFOs To VFOs + vfoFields.Length - 1
            vfoFreqFields(i) = vfoFields(i - VFOs)
        Next
        For i As Integer = VFOs To VFOs + memFields.Length - 1
            memFreqFields(i) = memFields(i - VFOs)
        Next

        FreqOut.Populate(vfoFreqFields)
        ' default current FreqOut display fields
        currentFields = vfoFreqFields
    End Sub

    Private Sub FreqOut_BoxKeydown(ByVal sender As System.Object, ByVal e As System.Windows.Forms.KeyEventArgs) Handles FreqOut.BoxKeydown
        If Not Power Then
            Tracing.TraceLine("freqout:no power", TraceLevel.Error)
            Return
        End If

        Dim fld As MainBox.Field = FreqOut.PositionToField(FreqOut.SelectionStart)
        If (RigControl IsNot Nothing) And (fld IsNot Nothing) And
            Not (e.Alt Or e.Control) Then
            ' Get the field's key.
            Dim fldKey As String = fld.Key
            ' execute the field's function if there is one.
            ' Note that freqOut.Function returns true if there's a function.
            ' The function sets parm.rv if it processed the key.
            Dim parm As New fieldFuncParm(fldKey, e.KeyData, False)
            FreqOut.Function(fldKey, parm)
            e.SuppressKeyPress = parm.rv
        End If
        ' if not already handled...
        If Not e.SuppressKeyPress Then
            FreqoutKeyHandler(e)
        End If
        If Not e.SuppressKeyPress Then
            e.SuppressKeyPress = doCommand(e)
        End If
    End Sub

    Private Sub TransmitButton_Click(sender As System.Object, e As System.EventArgs) Handles TransmitButton.Click
        Tracing.TraceLine("TransmitButton_Click", TraceLevel.Info)
        toggleTransmit()
    End Sub
    Private Sub toggleTransmit()
        If Not Power Then
            Tracing.TraceLine("toggleTransmit:no power", TraceLevel.Error)
            Return
        End If

        Tracing.TraceLine("toggling transmit:" & RigControl.Transmit.ToString, TraceLevel.Info)
        RigControl.Transmit = Not RigControl.Transmit
    End Sub

    Delegate Sub SetButtonTextDel()
    Private Sub setButtonText(b As Button, text As String)
        Dim setText As SetButtonTextDel = Sub()
                                              b.Text = text
                                              b.AccessibleName = text
                                          End Sub
        If b.InvokeRequired Then
            b.Invoke(setText)
        Else
            setText()
        End If
    End Sub

    Private Sub AntennaTuneButton_Enter(sender As Object, e As EventArgs) Handles AntennaTuneButton.Enter
        If Not Power Then
            Tracing.TraceLine("antennaTune:no power", TraceLevel.Error)
            Return
        End If
        setButtonText(AntennaTuneButton, antennaTuneButtonText)
    End Sub

    Private Sub AntennaTuneButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles AntennaTuneButton.Click
        If Not Power Then
            Tracing.TraceLine("antennaTune:no power", TraceLevel.Error)
            Return
        End If
        oldSWR = ""
        ' just toggle the tuner on/off.
        RigControl.FlexTunerOn = Not RigControl.FlexTunerOn
    End Sub

    Private Sub AntennaTuneButton_Leave(sender As Object, e As EventArgs) Handles AntennaTuneButton.Leave
        If Not Power Then
            Return
        End If
        setButtonText(AntennaTuneButton, antennaTuneButtonText)
    End Sub

    Private Sub FlexAntTuneStartStopHandler(e As FlexBase.FlexAntTunerArg)
        TextOut.PerformGenericFunction(AntennaTuneButton,
            Sub()
                If RigControl.FlexTunerType = FlexBase.FlexTunerTypes.manual Then
                    If (e.Status = "OK") Then
                        setButtonText(AntennaTuneButton, e.SWR)
                    End If
                Else
                    setButtonText(AntennaTuneButton, e.Status)
        End If
                'AntennaTuneButton.Focus()
            End Sub)
    End Sub

    ' Public iSize As SizeF
    Private Sub Form1_Layout(ByVal sender As System.Object, ByVal e As System.Windows.Forms.LayoutEventArgs) Handles MyBase.Layout
#If 0 Then
        If e.AffectedControl.Name = "Form1" Then
            Select Case e.AffectedProperty
                Case "Visible"
                    iSize = New SizeF(Width, Height)
                Case "Bounds"
                    Dim sz As Size = e.AffectedControl.Size
                    Dim sc = New SizeF(sz.Width / iSize.Width, sz.Height / iSize.Height)
                    Scale(sc)
            End Select
        End If
#End If
    End Sub

    Private Sub Form1_Activated(sender As System.Object, e As System.EventArgs) Handles MyBase.Activated
        BringToFront()
    End Sub

    Private Sub Form1_Shown(sender As Object, e As EventArgs) Handles MyBase.Shown
        ' After initial display, reclaim focus in case another process grabbed it during load.
        Me.Activate()
        If ActiveUIMode = UIMode.Logging AndAlso LoggingLogPanel IsNot Nothing Then
            LoggingLogPanel.FocusCallSign()
        ElseIf FreqOut.Visible Then
            FreqOut.Focus()
        End If
    End Sub

    Private Sub CWMessageUpdateMenuItem_Click(sender As System.Object, e As System.EventArgs) Handles CWMessageUpdateMenuItem.Click
        CWMessageUpdate.ShowDialog()
    End Sub

    Private Function isFunction(e As KeyEventArgs) As Boolean
        Dim rv As Boolean
        rv = (e.Control Or e.Alt Or
              ((e.KeyCode >= Keys.F1) And (e.KeyCode <= Keys.F24)))
        Return rv
    End Function
    Private Sub SentTextBox_KeyDown(ByVal sender As System.Object, ByVal e As System.Windows.Forms.KeyEventArgs) Handles SentTextBox.KeyDown
        If RigControl Is Nothing Then
            Return
        End If

        If isFunction(e) Then
            ' not just a character to send.
            ' check for clipboard functions
            If e.Control Then
                Select Case e.KeyCode
                    Case Keys.B
                        ' start/stop buffering
                        RigControl.CWBuffering = Not RigControl.CWBuffering
                        e.SuppressKeyPress = True
                    Case Keys.C
                        ' copy to clipboard
                        Dim tb As TextBox = sender
                        Try
                            Clipboard.SetText(tb.Text.Substring(tb.SelectionStart, tb.SelectionLength))
                        Catch ex As Exception
                            Tracing.TraceLine("SentTextBox_KeyDown exception:" & ex.Message, TraceLevel.Error)
                        End Try
                        e.SuppressKeyPress = True
                    Case Keys.V
                        ' paste from clipboard
                        Dim str As String
                        str = Clipboard.GetText
                        RigControl.SendCW(str)
                        WriteTextX(WindowIDs.SendDataOut, str, 0, False)
                        e.SuppressKeyPress = True
                    Case Else
                        e.SuppressKeyPress = doCommand(e)
                End Select
            Else
                ' See if it's some other command.
                e.SuppressKeyPress = doCommand(e)
            End If
        Else
            ' send this character in the keyPress routine.
        End If
    End Sub

    Private cwBuf As String = ""
    Private Sub SentTextBox_KeyPress(sender As System.Object, e As System.Windows.Forms.KeyPressEventArgs) Handles SentTextBox.KeyPress
        If Not Power Then
            Tracing.TraceLine("SentTextBox:no power", TraceLevel.Error)
            Return
        End If

        Tracing.TraceLine("SentTextBox_KeyPress:" & AscW(e.KeyChar), TraceLevel.Info)
        If DirectCW Then
            RigControl.SendCW(e.KeyChar)
            Return
        End If
        ' check for backspace
        If (e.KeyChar = ChrW(8)) And (cwBuf.Length <> 0) Then
            cwBuf = cwBuf.Substring(0, cwBuf.Length - 1)
            Return
        End If
        cwBuf &= e.KeyChar
        If Char.IsWhiteSpace(e.KeyChar) Then
            RigControl.SendCW(cwBuf)
            cwBuf = ""
        End If
    End Sub

    Private Sub ShowBandsMenuItem_Click(sender As System.Object, e As System.EventArgs) Handles ShowBandsMenuItem.Click
        ShowBands.ShowDialog()
    End Sub

    Private Sub DiversityMenuItem_Click(sender As System.Object, e As System.EventArgs) Handles DiversityMenuItem.Click
        Dim filters As Flex6300Filters = Nothing
        If (RigControl IsNot Nothing) AndAlso (RigControl.RigFields IsNot Nothing) Then
            filters = TryCast(RigControl.RigFields.RigControl, Flex6300Filters)
        End If
        If filters Is Nothing Then
            Return
        End If
        filters.ToggleDiversity()
    End Sub

    Private Sub EscMenuItem_Click(sender As System.Object, e As System.EventArgs) Handles EscMenuItem.Click
        Dim filters As Flex6300Filters = Nothing
        If (RigControl IsNot Nothing) AndAlso (RigControl.RigFields IsNot Nothing) Then
            filters = TryCast(RigControl.RigFields.RigControl, Flex6300Filters)
        End If
        If filters Is Nothing Then
            Return
        End If
        filters.ShowEscDialog()
    End Sub

    Private Sub FeatureAvailabilityMenuItem_Click(sender As System.Object, e As System.EventArgs) Handles FeatureAvailabilityMenuItem.Click
        If RigControl Is Nothing Then
            Return
        End If
        Dim theForm As New FlexInfo(RigControl, FlexInfo.FlexInfoTab.FeatureAvailability)
        theForm.ShowDialog()
        theForm.Dispose()
    End Sub

    Private Sub UpdateAdvancedFeatureMenus()
        Dim rig = RigControl
        If rig Is Nothing Then
            DiversityMenuItem.Enabled = False
            EscMenuItem.Enabled = False
            DiversityMenuItem.Visible = False
            EscMenuItem.Visible = False
            LogAdvancedGateState(AdvancedGateState.Disabled, "Diversity gating: radio not ready")
            Return
        End If

        If Not rig.DiversityHardwareSupported Then
            DiversityMenuItem.Enabled = False
            EscMenuItem.Enabled = False
            DiversityMenuItem.Visible = False
            EscMenuItem.Visible = False
            LogAdvancedGateState(AdvancedGateState.Disabled, "Diversity gating: model lacks diversity support")
            Return
        End If

        Dim enableAdvanced As Boolean = rig.DiversityReady
        DiversityMenuItem.Enabled = enableAdvanced
        EscMenuItem.Enabled = enableAdvanced

        Dim licenseReported As Boolean = rig.DiversityLicenseReported
        Dim gateMessage = rig.DiversityGateMessage
        Dim nextState As AdvancedGateState
        Dim logMessage As String = Nothing
        If Not String.IsNullOrEmpty(gateMessage) Then
            nextState = If(licenseReported, AdvancedGateState.Disabled, AdvancedGateState.Pending)
            logMessage = "Diversity gating: " & gateMessage
        Else
            nextState = AdvancedGateState.None
        End If

        LogAdvancedGateState(nextState, logMessage)

        Dim showAdvancedItems As Boolean = (nextState = AdvancedGateState.None) AndAlso enableAdvanced
        DiversityMenuItem.Visible = showAdvancedItems
        EscMenuItem.Visible = showAdvancedItems
    End Sub

    Private Sub LogAdvancedGateState(nextState As AdvancedGateState, message As String)
        If nextState = AdvancedGateState.None Then
            currentAdvancedGateState = AdvancedGateState.None
            lastAdvancedGateMessage = Nothing
            Return
        End If

        If (currentAdvancedGateState <> nextState) OrElse (lastAdvancedGateMessage <> message) Then
            If Not String.IsNullOrEmpty(message) Then
                Tracing.TraceLine(message, TraceLevel.Info)
            End If
            currentAdvancedGateState = nextState
            lastAdvancedGateMessage = message
        End If
    End Sub

    Private Sub ActionsToolStripMenuItem_DropDownOpening(sender As Object, e As EventArgs) Handles ActionsToolStripMenuItem.DropDownOpening
        UpdateAdvancedFeatureMenus()
        UpdateFiltersActionsMenu()
    End Sub

    Private Sub RigControl_FeatureLicenseChanged(sender As Object, e As EventArgs)
        If InvokeRequired Then
            BeginInvoke(New Action(Sub()
                                       UpdateAdvancedFeatureMenus()
                                       UpdateFiltersActionsMenu()
                                   End Sub))
        Else
            UpdateAdvancedFeatureMenus()
            UpdateFiltersActionsMenu()
        End If
    End Sub

    ''' <summary>
    ''' Ensure the Filters submenu exists on the Actions menu and update its gating/checked state.
    ''' </summary>
    Private Sub InitFiltersActionsMenu()
        If filtersMenuItem IsNot Nothing Then Return

        filtersMenuItem = New ToolStripMenuItem("&Filters")
        filtersMenuItem.AccessibleRole = AccessibleRole.MenuItem
        filtersMenuItem.AccessibleName = "Filters"
        filtersMenuItem.Visible = True

        filtersNoiseMenuItem = New ToolStripMenuItem("Noise && Mitigation") With {.AccessibleRole = AccessibleRole.MenuItem}
        filtersNRMenuItem = New ToolStripMenuItem("Noise &Reduction") With {.AccessibleRole = AccessibleRole.MenuItem}
        filtersNRRnnItem = New ToolStripMenuItem("&Neural (RNN)") With {.CheckOnClick = True, .AccessibleRole = AccessibleRole.MenuItem}
        filtersNRSpectralItem = New ToolStripMenuItem("&Spectral (NRF/NRS)") With {.CheckOnClick = True, .AccessibleRole = AccessibleRole.MenuItem}
        filtersNRLegacyItem = New ToolStripMenuItem("&Legacy (NRL)") With {.CheckOnClick = True, .AccessibleRole = AccessibleRole.MenuItem}
        filtersNRMenuItem.DropDownItems.AddRange(New ToolStripItem() {filtersNRRnnItem, filtersNRSpectralItem, filtersNRLegacyItem})

        filtersANFMenuItem = New ToolStripMenuItem("Auto &Notch") With {.AccessibleRole = AccessibleRole.MenuItem}
        filtersANFFftItem = New ToolStripMenuItem("FFT (ANFT)") With {.CheckOnClick = True, .AccessibleRole = AccessibleRole.MenuItem}
        filtersANFLegacyItem = New ToolStripMenuItem("Legacy (ANFL)") With {.CheckOnClick = True, .AccessibleRole = AccessibleRole.MenuItem}
        filtersANFMenuItem.DropDownItems.AddRange(New ToolStripItem() {filtersANFFftItem, filtersANFLegacyItem})

        filtersNoiseMenuItem.DropDownItems.Add(filtersNRMenuItem)
        filtersNoiseMenuItem.DropDownItems.Add(filtersANFMenuItem)

        filtersCWMenuItem = New ToolStripMenuItem("C&W") With {.AccessibleRole = AccessibleRole.MenuItem}
        filtersCWAutotuneItem = New ToolStripMenuItem("&Autotune") With {.AccessibleRole = AccessibleRole.MenuItem}
        AddHandler filtersCWAutotuneItem.Click, Sub(sender, args)
                                                    Try
                                                        If RigControl IsNot Nothing Then
                                                            RigControl.CWAutotune()
                                                        End If
                                                    Catch ex As Exception
                                                        Tracing.TraceLine("CW Autotune error: " & ex.Message, TraceLevel.Error)
                                                    End Try
                                                End Sub
        filtersCWMenuItem.DropDownItems.Add(filtersCWAutotuneItem)

        filtersMenuItem.DropDownItems.Add(filtersNoiseMenuItem)
        filtersMenuItem.DropDownItems.Add(filtersCWMenuItem)

        AddHandler filtersNRRnnItem.CheckedChanged, Sub(sender, args)
                                                        If RigControl IsNot Nothing Then RigControl.NeuralNoiseReduction = If(filtersNRRnnItem.Checked, Radios.FlexBase.OffOnValues.on, Radios.FlexBase.OffOnValues.off)
                                                    End Sub
        AddHandler filtersNRSpectralItem.CheckedChanged, Sub(sender, args)
                                                             If RigControl IsNot Nothing Then RigControl.SpectralNoiseReduction = If(filtersNRSpectralItem.Checked, Radios.FlexBase.OffOnValues.on, Radios.FlexBase.OffOnValues.off)
                                                         End Sub
        AddHandler filtersNRLegacyItem.CheckedChanged, Sub(sender, args)
                                                           If RigControl IsNot Nothing Then RigControl.NoiseReductionLegacy = If(filtersNRLegacyItem.Checked, Radios.FlexBase.OffOnValues.on, Radios.FlexBase.OffOnValues.off)
                                                       End Sub
        AddHandler filtersANFFftItem.CheckedChanged, Sub(sender, args)
                                                        If RigControl IsNot Nothing Then RigControl.AutoNotchFFT = If(filtersANFFftItem.Checked, Radios.FlexBase.OffOnValues.on, Radios.FlexBase.OffOnValues.off)
                                                    End Sub
        AddHandler filtersANFLegacyItem.CheckedChanged, Sub(sender, args)
                                                           If RigControl IsNot Nothing Then RigControl.AutoNotchLegacy = If(filtersANFLegacyItem.Checked, Radios.FlexBase.OffOnValues.on, Radios.FlexBase.OffOnValues.off)
                                                       End Sub

        ' Insert near the advanced items for visibility.
        Dim insertIndex As Integer = ActionsToolStripMenuItem.DropDownItems.IndexOf(EscMenuItem)
        If insertIndex < 0 Then insertIndex = ActionsToolStripMenuItem.DropDownItems.Count
        ActionsToolStripMenuItem.DropDownItems.Insert(insertIndex, filtersMenuItem)
    End Sub

    Private Sub UpdateFiltersActionsMenu()
        If ActionsToolStripMenuItem Is Nothing Then Return
        InitFiltersActionsMenu()

        Dim rig = RigControl
        If rig Is Nothing Then
            filtersMenuItem.Enabled = False
            Exit Sub
        End If
        Dim hasRig As Boolean = (rig IsNot Nothing)
        Dim mode As String = If(rig?.Mode, "").ToLowerInvariant()
        Dim hasSlice As Boolean = hasRig AndAlso rig.HasActiveSlice
        Dim cwOrFm As Boolean = mode.StartsWith("cw") OrElse mode.Contains("fm")
        Dim fmMode As Boolean = mode.Contains("fm")

        ' NR/ANF gating
        Dim licenseReported As Boolean = If(hasRig, rig.NoiseReductionLicenseReported, False)
        Dim licenseEnabled As Boolean = If(hasRig, rig.NoiseReductionLicensed, False)
        Dim nrTip As String = ""
        If Not hasSlice Then
            nrTip = "No active slice."
        ElseIf licenseReported AndAlso Not licenseEnabled Then
            nrTip = "NR/ANF feature disabled by license."
        ElseIf hasRig AndAlso Not licenseReported Then
            nrTip = "NR/ANF license check pending."
        End If

        Dim nrAllowed As Boolean = hasSlice AndAlso licenseEnabled AndAlso Not cwOrFm
        Dim anfAllowed As Boolean = hasSlice AndAlso licenseEnabled AndAlso Not fmMode

        filtersNRRnnItem.Checked = hasRig AndAlso (rig.NeuralNoiseReduction = Radios.FlexBase.OffOnValues.on)
        filtersNRSpectralItem.Checked = hasRig AndAlso (rig.SpectralNoiseReduction = Radios.FlexBase.OffOnValues.on)
        filtersNRLegacyItem.Checked = hasRig AndAlso (rig.NoiseReductionLegacy = Radios.FlexBase.OffOnValues.on)
        filtersANFFftItem.Checked = hasRig AndAlso (rig.AutoNotchFFT = Radios.FlexBase.OffOnValues.on)
        filtersANFLegacyItem.Checked = hasRig AndAlso (rig.AutoNotchLegacy = Radios.FlexBase.OffOnValues.on)

        Dim nrItems = New ToolStripMenuItem() {filtersNRRnnItem, filtersNRSpectralItem, filtersNRLegacyItem}
        For Each item In nrItems
            item.Enabled = nrAllowed
            item.ToolTipText = nrTip
        Next
        Dim anfItems = New ToolStripMenuItem() {filtersANFFftItem, filtersANFLegacyItem}
        For Each item In anfItems
            item.Enabled = anfAllowed
            item.ToolTipText = nrTip
        Next

        filtersNRMenuItem.Enabled = nrAllowed OrElse anfAllowed OrElse (nrTip <> "")
        filtersANFMenuItem.Enabled = anfAllowed OrElse (nrTip <> "")
        filtersNoiseMenuItem.Enabled = filtersNRMenuItem.Enabled OrElse filtersANFMenuItem.Enabled

        ' CW Autotune gating
        Dim cwCap As Boolean = hasRig AndAlso rig.SupportsCwAutotune
        Dim cwMode As Boolean = mode.StartsWith("cw")
        Dim cwTip As String = ""
        If Not cwCap Then
            cwTip = "CW autotune not supported on this radio."
        ElseIf Not hasSlice Then
            cwTip = "No active slice."
        ElseIf Not cwMode Then
            cwTip = "Switch to CW to use autotune."
        End If
        Dim cwEnable As Boolean = cwCap AndAlso hasSlice AndAlso cwMode

        filtersCWAutotuneItem.Enabled = cwEnable
        filtersCWAutotuneItem.ToolTipText = cwTip
        filtersCWMenuItem.Enabled = cwEnable OrElse (cwTip <> "")

        ' Parent Filters menu enabled if any subitems enabled or we have explanatory tooltip
        filtersMenuItem.Enabled = filtersNoiseMenuItem.Enabled OrElse filtersCWMenuItem.Enabled
    End Sub

#If 0 Then
    Private Sub ReceiveTextBox_KeyDown(sender As System.Object, e As System.Windows.Forms.KeyEventArgs)
        ' Allow clipboard stuff
        ' ctrl-C copies all text to the clipboard.
        ' ctrl-X cuts all text.
        If e.Control And (ReceivedTextBox.Text.Length > 0) Then
            If e.KeyCode = Keys.C Then
                copyReceivedText()
                e.SuppressKeyPress = True
            ElseIf (e.KeyCode = Keys.X) Then
                copyReceivedText()
                ReceivedTextBox.Text = ""
                e.SuppressKeyPress = True
            Else
                doCommand_KeyDown(sender, e)
            End If
        Else
            doCommand_KeyDown(sender, e)
        End If
    End Sub
    Private Sub copyReceivedText()
        ReceivedTextBox.Enabled = False
        ReceivedTextBox.SelectAll()
        If ReceivedTextBox.SelectedText IsNot Nothing AndAlso _
           ReceivedTextBox.SelectedText.Length > 0 Then
            Clipboard.SetText(ReceivedTextBox.SelectedText)
        End If
        ReceivedTextBox.Enabled = True
        ReceivedTextBox.Focus()
    End Sub
#End If

    Private Sub setupScreenFields()
        ScreenFieldsMenu.DropDownItems.Clear()
        Dim rig = RigControl
        If (rig Is Nothing) OrElse (rig.RigFields Is Nothing) OrElse (rig.RigFields.ScreenFields Is Nothing) Then
            Return
        End If

        Dim nrAllowed As Boolean = rig.NoiseReductionLicenseReported AndAlso rig.NoiseReductionLicensed AndAlso rig.HasActiveSlice
        Dim diversityAllowed As Boolean = rig.DiversityReady AndAlso String.IsNullOrEmpty(rig.DiversityGateMessage)

        For Each ctl As Control In rig.RigFields.ScreenFields
            If ctl Is Nothing OrElse Not ctl.Enabled Then
                Continue For
            End If

            Dim nameLower As String = If(ctl.Name, "").ToLowerInvariant()
            Dim tagLower As String = If(If(ctl.Tag, "").ToString(), "").ToLowerInvariant()

            Dim isNoiseFeature As Boolean = nameLower.Contains("noise") OrElse nameLower.Contains("anf") OrElse tagLower.Contains("noise") OrElse tagLower.Contains("anf") OrElse tagLower.Contains("n.r.")
            Dim isDiversityFeature As Boolean = nameLower.Contains("diversity") OrElse tagLower.Contains("diversity")
            Dim isEscFeature As Boolean = nameLower.Contains("esc") OrElse tagLower.Contains("esc")

            If isNoiseFeature AndAlso Not nrAllowed Then
                Continue For
            End If
            If (isDiversityFeature OrElse isEscFeature) AndAlso Not diversityAllowed Then
                Continue For
            End If

            Dim item = New ToolStripMenuItem
            item.Tag = ctl
            item.AccessibleRole = System.Windows.Forms.AccessibleRole.MenuItem
            item.AutoSize = True
            item.Text = CStr(ctl.Tag)
            AddHandler item.Click, AddressOf ScreenField_Click
            ScreenFieldsMenu.DropDownItems.Add(item)
        Next
    End Sub

    Private Sub ScreenField_Click(sender As Object, e As EventArgs)
        Dim item As Control = sender.tag
        item.Focus()
    End Sub

    Private Sub ScreenFieldsMenu_DropDownOpening(sender As System.Object, e As System.EventArgs) Handles ScreenFieldsMenu.DropDownOpening
        Tracing.TraceLine("ScreenFieldsMenu_DropDownOpening", TraceLevel.Info)
        setupScreenFields()
    End Sub

    Private Function compareNames(m1 As KeyCommands.keyTbl, m2 As KeyCommands.keyTbl)
        ' null items sort last
        Dim x As String = m1.menuText
        Dim y As String = m2.menuText
        If x = vbNullString Then
            If y = vbNullString Then
                Return 0
            Else
                Return 1
            End If
        ElseIf y = vbNullString Then
            Return -1
        End If
        Dim minLen As Integer = Math.Min(x.Length, y.Length)
        Dim xs As String = x.Substring(0, minLen)
        Dim ys As String = y.Substring(0, minLen)
        Dim rv As Integer = xs.CompareTo(ys)
        If rv = 0 Then
            rv = x.Length.CompareTo(y.Length)
        End If
        Return rv
    End Function

    ''' <summary>
    ''' Setup the operations menu
    ''' </summary>
    ''' <remarks>
    ''' This is called from KeyCommands when the command table is initialized from the config data.
    ''' </remarks>
    Friend Sub SetupOperationsMenu()
        If Commands Is Nothing Then
            Return
        End If
        OperationsMenuItem.DropDownItems.Clear()
        Dim sortedTable = New List(Of KeyCommands.keyTbl)
        sortedTable.AddRange(Commands.KeyTable)
        sortedTable.Sort(AddressOf compareNames)
        For Each keyItem As KeyCommands.keyTbl In sortedTable
            If keyItem.menuText = vbNullString Then
                Exit For
            End If
            ' Skip legacy JJ Radio "Menus" entry (not supported on JJFlexRadio).
            If String.Equals(keyItem.menuText, "Menus", StringComparison.OrdinalIgnoreCase) Then
                Continue For
            End If
            ' Skip logging commands in Classic/Modern — they live in Logging Mode now.
            If ActiveUIMode <> UIMode.Logging AndAlso keyItem.Group = KeyCommands.FunctionGroups.logging Then
                Continue For
            End If
            Dim item = New ToolStripMenuItem
            item.Tag = keyItem
            item.AccessibleRole = System.Windows.Forms.AccessibleRole.MenuItem
            item.AutoSize = True
            item.Text = keyItem.menuText
            AddHandler item.Click, AddressOf OperationsMenuItem_Click
            OperationsMenuItem.DropDownItems.Add(item)
        Next

        ' Only add the daily trace toggle if operator data is ready.
        Dim haveOp As Boolean = (Operators IsNot Nothing) AndAlso (CurrentOp IsNot Nothing)
        If Not haveOp Then
            keepDailyTraceMenuItem = Nothing
            Return
        End If

        ' Daily trace logging toggle
        Dim keepDaily As Boolean = CurrentOp.KeepDailyTraceLogs
        keepDailyTraceMenuItem = New ToolStripMenuItem("Keep Daily Trace Logs") With {
            .AccessibleRole = AccessibleRole.MenuItem,
            .CheckOnClick = True,
            .Checked = keepDaily
        }
        AddHandler keepDailyTraceMenuItem.Click,
            Sub()
                If (Operators Is Nothing) OrElse (CurrentOp Is Nothing) Then
                    Tracing.TraceLine("KeepDailyTrace toggle ignored: no current operator", TraceLevel.Warning)
                    Return
                End If
                CurrentOp.KeepDailyTraceLogs = Not CurrentOp.KeepDailyTraceLogs
                keepDailyTraceMenuItem.Checked = CurrentOp.KeepDailyTraceLogs
                Operators.UpdateCurrentOp()
                If CurrentOp.KeepDailyTraceLogs Then
                    StartDailyTraceIfEnabled()
                Else
                    ' Stop automatic daily trace; manual traces remain untouched.
                    If Tracing.On AndAlso Not String.IsNullOrEmpty(LastUserTraceFile) AndAlso LastUserTraceFile.Contains(ProgramName & "Trace") Then
                        Tracing.TraceLine("Daily tracing disabled; stopping automatic trace", TraceLevel.Info)
                        Tracing.On = False
                        LastUserTraceFile = ""
                    End If
                End If
            End Sub
        OperationsMenuItem.DropDownItems.Add(New ToolStripSeparator())
        OperationsMenuItem.DropDownItems.Add(keepDailyTraceMenuItem)
    End Sub

    Private Sub OperationsMenuItem_Click(sender As Object, e As EventArgs)
        Dim item As KeyCommands.keyTbl = sender.tag
        Commands.CommandId = item.key.id
        Try
            item.rtn()
        Catch ex As Exception
            If (RigControl Is Nothing) OrElse Not Power Then
                Tracing.TraceLine("OperationsMenuItem_Click:no rig setup", TraceLevel.Error)
            Else
                Tracing.TraceLine("OperationsMenuItem_Click:", TraceLevel.Error)
                Tracing.ErrMessageTrace(ex)
            End If
        End Try
    End Sub

    Private traceFile As String = ""
    Private Sub TraceMenuItem_Click(sender As System.Object, e As System.EventArgs) Handles TraceMenuItem.Click
        TraceAdmin.ShowDialog()
    End Sub

    Private Sub Form1_FormClosing(sender As System.Object, e As System.Windows.Forms.FormClosingEventArgs) Handles MyBase.FormClosing
        If Not Ending Then
            FileExitToolStripMenuItem_Click(sender, e)
        End If
    End Sub

    Friend Sub gotoHome()
        TextOut.PerformGenericFunction(Me,
            Sub()
                Me.BringToFront()
                FreqOut.Focus()
            End Sub)
    End Sub

    Private Function currentOperatorName() As String
        Return CurrentOp.UserBasename
    End Function

    Private Sub ClearOptionalMenuItem_Click(sender As System.Object, e As System.EventArgs) Handles ClearOptionalMenuItem.Click
        OptionalMessage.Clear()
    End Sub

    Private Sub ScanTimer_Tick(sender As System.Object, e As System.EventArgs) Handles ScanTmr.Tick
        If scanstate = scans.linear Then
            scan.ScanTimer_Tick(sender, e)
        Else
            MemoryScan.ScanTimer_Tick(sender, e)
        End If
    End Sub

    Private Sub LOTWMergeMenuItem_Click(sender As System.Object, e As System.EventArgs) Handles LOTWMergeMenuItem.Click
        Dim lotwForm As Form = New LOTWMerge
        lotwForm.ShowDialog()
        lotwForm.Dispose()
    End Sub

    Private Sub FlexKnobMenuItem_Click(sender As Object, e As EventArgs) Handles FlexKnobMenuItem.Click
        If Knob IsNot Nothing Then
            Knob.Config()
        End If
    End Sub

    ' For testing when Debugger is attached
    Private Sub testMenuItem_Click(ByVal sender As System.Object, ByVal e As System.EventArgs)
        RigControl.TestRoutine()
    End Sub

    Private Function getControlsOfInterest() As List(Of Control)
        Dim rv As List(Of Control) = New List(Of Control)
        For Each ctl As Control In Me.Controls
            rv.Add(ctl)
        Next
        rv.AddRange(RigControl.RigFields.ScreenFields)
        Return rv
    End Function

    Private Sub StationNamesMenuItem_Click(sender As Object, e As EventArgs) Handles StationNamesMenuItem.Click
        ShowStationNames.ShowDialog()
    End Sub

    Private Sub noSliceErrorHandler(sender As Object, msg As String)
        MessageBox.Show(msg, ErrorHdr, MessageBoxButtons.OK)
        CloseTheRadio()
    End Sub

    Private Sub LocalPTTMenuItem_Click(sender As Object, e As EventArgs) Handles LocalPTTMenuItem.Click
        If RigControl IsNot Nothing Then
            RigControl.LocalPTT = True
        End If
    End Sub

    Private Sub ProfilesMenuItem_Click(sender As Object, e As EventArgs) Handles ProfilesMenuItem.Click
        Dim profile = New Profile
        Dim theForm = CType(profile, Form)
        theForm.ShowDialog()
        profile.Dispose()
    End Sub

    Private Sub ExportOperatorMenuItem_Click(sender As Object, e As EventArgs) Handles ExportSetupMenuItem.Click
        ExportSetup.ExportSetup()
    End Sub

#Region "UI Mode (Classic / Modern / Logging)"

    ' -----------------------------------------------------------------------
    '  Modern menu references — created programmatically in BuildModernMenus.
    '  Logging menu references — created programmatically in BuildLoggingMenus.
    '  Classic menus (ActionsToolStripMenuItem, ScreenFieldsMenu,
    '  OperationsMenuItem, HelpToolStripMenuItem) are Designer-defined.
    ' -----------------------------------------------------------------------
    Private ModernRadioMenu As ToolStripMenuItem
    Private ModernSliceMenu As ToolStripMenuItem
    Private ModernFilterMenu As ToolStripMenuItem
    Private ModernAudioMenu As ToolStripMenuItem
    Private ModernToolsMenu As ToolStripMenuItem

    Private LoggingLogMenu As ToolStripMenuItem
    Private LoggingNavigateMenu As ToolStripMenuItem
    Private LoggingModeMenu As ToolStripMenuItem
    ' Help menu is shared across all modes (HelpToolStripMenuItem).

    ' Logging Mode panels — created in BuildLoggingPanels, shown/hidden by ShowXxxUI.
    Private LoggingSplitContainer As SplitContainer
    Private LoggingRadioPane As RadioPane
    Private LoggingLogPanel As LogPanel
    Private LoggingPanelSession As LogSession  ' Separate session for the embedded panel

    ''' <summary>
    ''' Show the one-time "Try Modern UI?" prompt for existing operators
    ''' who predate the UIMode feature.
    ''' </summary>
    Private Sub CheckUIModUpgradePrompt()
        If CurrentOp Is Nothing Then Return
        If CurrentOp.UIModeDismissed Then Return

        ' Mark it dismissed immediately so it never shows again regardless of answer.
        CurrentOp.UIModeDismissed = True

        Dim msg As String = "JJFlex now has a Modern UI mode with reorganized menus." & vbCrLf &
                            "Want to try it? You can switch back anytime with Ctrl+Shift+M."
        Dim result = MessageBox.Show(msg, "Try Modern Mode?", MessageBoxButtons.YesNo, MessageBoxIcon.Information)
        If result = DialogResult.Yes Then
            CurrentOp.UIModeSetting = CInt(UIMode.Modern)
        Else
            CurrentOp.UIModeSetting = CInt(UIMode.Classic)
        End If

        Operators.UpdateCurrentOp()
    End Sub

    ''' <summary>
    ''' Build the Modern menu structure programmatically and add to MenuStrip1.
    ''' Called once during Form1_Load. Menus start hidden; ApplyUIMode controls visibility.
    ''' </summary>
    Private Sub BuildModernMenus()
        ' --- Radio menu ---
        ModernRadioMenu = New ToolStripMenuItem() With {
            .Name = "ModernRadioMenu",
            .Text = "Radio",
            .AccessibleRole = AccessibleRole.MenuPopup,
            .Visible = False
        }
        ' Items that delegate to existing handlers
        AddModernMenuItem(ModernRadioMenu, "Connect to Radio", AddressOf SelectRigMenuItem_Click)
        AddModernMenuItem(ModernRadioMenu, "Manage SmartLink Accounts",
            Sub(s As Object, ev As EventArgs)
                Radios.SmartLinkAccountManager.ShowAccountManager(Me, BaseConfigDir, CurrentOp.callSign)
            End Sub)
        AddModernMenuItem(ModernRadioMenu, "Operators", AddressOf ListOperatorsMenuItem_Click)
        AddModernMenuItem(ModernRadioMenu, "Profiles", AddressOf ProfilesMenuItem_Click)
        AddModernMenuItem(ModernRadioMenu, "Connected Stations", AddressOf StationNamesMenuItem_Click)
        ModernRadioMenu.DropDownItems.Add(New ToolStripSeparator())
        AddModernMenuItem(ModernRadioMenu, "Disconnect",
            Sub(s As Object, ev As EventArgs)
                If RigControl IsNot Nothing Then
                    Dim radioName = RigControl.RadioNickname
                    If Not String.IsNullOrEmpty(radioName) Then
                        Radios.ScreenReaderOutput.Speak("Disconnecting from " & radioName, True)
                    Else
                        Radios.ScreenReaderOutput.Speak("Disconnecting from radio", True)
                    End If
                    RigControl.Disconnect()
                End If
            End Sub)
        AddModernMenuItem(ModernRadioMenu, "Exit", AddressOf FileExitToolStripMenuItem_Click)

        ' --- Slice menu ---
        ModernSliceMenu = New ToolStripMenuItem() With {
            .Name = "ModernSliceMenu",
            .Text = "Slice",
            .AccessibleRole = AccessibleRole.MenuPopup,
            .Visible = False
        }
        ' Selection submenu
        Dim sliceSelection = AddModernSubmenu(ModernSliceMenu, "Selection")
        AddModernStubItem(sliceSelection, "Select Slice")
        AddModernStubItem(sliceSelection, "Next Slice")
        AddModernStubItem(sliceSelection, "Previous Slice")
        AddModernStubItem(sliceSelection, "Set TX Slice")
        AddModernStubItem(sliceSelection, "Set Active Slice")
        ' Mode submenu
        Dim sliceMode = AddModernSubmenu(ModernSliceMenu, "Mode")
        AddModernStubItem(sliceMode, "CW")
        AddModernStubItem(sliceMode, "USB")
        AddModernStubItem(sliceMode, "LSB")
        AddModernStubItem(sliceMode, "AM")
        AddModernStubItem(sliceMode, "FM")
        AddModernStubItem(sliceMode, "DIGU")
        AddModernStubItem(sliceMode, "DIGL")
        ' Audio submenu
        Dim sliceAudio = AddModernSubmenu(ModernSliceMenu, "Audio")
        AddModernStubItem(sliceAudio, "Mute/Unmute")
        AddModernStubItem(sliceAudio, "Volume Up")
        AddModernStubItem(sliceAudio, "Volume Down")
        AddModernStubItem(sliceAudio, "Pan Left")
        AddModernStubItem(sliceAudio, "Pan Center")
        AddModernStubItem(sliceAudio, "Pan Right")
        ' Tuning submenu
        Dim sliceTuning = AddModernSubmenu(ModernSliceMenu, "Tuning")
        AddModernStubItem(sliceTuning, "RIT On/Off")
        AddModernStubItem(sliceTuning, "RIT Value")
        AddModernStubItem(sliceTuning, "XIT On/Off")
        AddModernStubItem(sliceTuning, "XIT Value")
        AddModernStubItem(sliceTuning, "Step Size")
        ' Receiver submenu
        Dim sliceReceiver = AddModernSubmenu(ModernSliceMenu, "Receiver")
        AddModernStubItem(sliceReceiver, "AGC Mode")
        AddModernStubItem(sliceReceiver, "AGC Threshold")
        AddModernStubItem(sliceReceiver, "Squelch On/Off")
        AddModernStubItem(sliceReceiver, "Squelch Level")
        AddModernStubItem(sliceReceiver, "RF Gain")
        ' DSP submenu
        Dim sliceDSP = AddModernSubmenu(ModernSliceMenu, "DSP")
        Dim dspNR = AddModernSubmenu(sliceDSP, "Noise Reduction")
        AddModernStubItem(dspNR, "Neural NR (RNN)")
        AddModernStubItem(dspNR, "Spectral NR (NRS)")
        AddModernStubItem(dspNR, "Legacy NR")
        Dim dspANF = AddModernSubmenu(sliceDSP, "Auto Notch")
        AddModernStubItem(dspANF, "FFT Auto-Notch")
        AddModernStubItem(dspANF, "Legacy Auto-Notch")
        AddModernStubItem(sliceDSP, "Noise Blanker (NB)")
        AddModernStubItem(sliceDSP, "Wideband NB (WNB)")
        AddModernStubItem(sliceDSP, "Audio Peak Filter (APF)")
        ' Antenna submenu
        Dim sliceAntenna = AddModernSubmenu(ModernSliceMenu, "Antenna")
        AddModernStubItem(sliceAntenna, "RX Antenna")
        AddModernStubItem(sliceAntenna, "TX Antenna")
        AddModernStubItem(sliceAntenna, "Diversity On/Off")
        ' FM submenu
        Dim sliceFM = AddModernSubmenu(ModernSliceMenu, "FM")
        AddModernStubItem(sliceFM, "Repeater Offset")
        AddModernStubItem(sliceFM, "Pre-De-Emphasis")
        AddModernStubItem(sliceFM, "Tone")

        ' --- Filter menu ---
        ModernFilterMenu = New ToolStripMenuItem() With {
            .Name = "ModernFilterMenu",
            .Text = "Filter",
            .AccessibleRole = AccessibleRole.MenuPopup,
            .Visible = False
        }
        AddModernStubItem(ModernFilterMenu, "Narrow")
        AddModernStubItem(ModernFilterMenu, "Widen")
        AddModernStubItem(ModernFilterMenu, "Shift Low Edge")
        AddModernStubItem(ModernFilterMenu, "Shift High Edge")
        AddModernStubItem(ModernFilterMenu, "Presets")
        AddModernStubItem(ModernFilterMenu, "Reset Filter")

        ' --- Audio menu ---
        ModernAudioMenu = New ToolStripMenuItem() With {
            .Name = "ModernAudioMenu",
            .Text = "Audio",
            .AccessibleRole = AccessibleRole.MenuPopup,
            .Visible = False
        }
        AddModernStubItem(ModernAudioMenu, "PC Audio Boost")
        AddModernStubItem(ModernAudioMenu, "Local Audio")
        AddModernStubItem(ModernAudioMenu, "Audio Test")
        AddModernStubItem(ModernAudioMenu, "Record/Playback")
        AddModernStubItem(ModernAudioMenu, "Route/DAX")

        ' --- Tools menu ---
        ModernToolsMenu = New ToolStripMenuItem() With {
            .Name = "ModernToolsMenu",
            .Text = "Tools",
            .AccessibleRole = AccessibleRole.MenuPopup,
            .Visible = False
        }
        AddModernStubItem(ModernToolsMenu, "Command Finder")
        AddModernStubItem(ModernToolsMenu, "Speak Status")
        AddModernStubItem(ModernToolsMenu, "Status Dialog")
        AddModernMenuItem(ModernToolsMenu, "Station Lookup    Ctrl+L",
            Sub(s As Object, ev As EventArgs)
                If LookupStation Is Nothing Then LookupStation = New StationLookup()
                LookupStation.ShowDialog()
            End Sub)
        ModernToolsMenu.DropDownItems.Add(New ToolStripSeparator())
        AddModernMenuItem(ModernToolsMenu, "Enter Logging Mode",
            Sub(s As Object, ev As EventArgs) EnterLoggingMode())
        AddModernMenuItem(ModernToolsMenu, "Switch to Classic UI",
            Sub(s As Object, ev As EventArgs) ToggleUIMode())
        ModernToolsMenu.DropDownItems.Add(New ToolStripSeparator())
        AddModernStubItem(ModernToolsMenu, "Hotkey Editor")
        AddModernMenuItem(ModernToolsMenu, "Band Plans", AddressOf ShowBandsMenuItem_Click)
        AddModernMenuItem(ModernToolsMenu, "Feature Availability", AddressOf FeatureAvailabilityMenuItem_Click)

        ' --- Add Modern menus to the menu strip ---
        ' Insert before HelpToolStripMenuItem so Help stays rightmost.
        Dim helpIndex As Integer = MenuStrip1.Items.IndexOf(HelpToolStripMenuItem)
        If helpIndex < 0 Then helpIndex = MenuStrip1.Items.Count
        MenuStrip1.Items.Insert(helpIndex, ModernRadioMenu)
        MenuStrip1.Items.Insert(helpIndex + 1, ModernSliceMenu)
        MenuStrip1.Items.Insert(helpIndex + 2, ModernFilterMenu)
        MenuStrip1.Items.Insert(helpIndex + 3, ModernAudioMenu)
        MenuStrip1.Items.Insert(helpIndex + 4, ModernToolsMenu)

        ' Attach accessibility handlers to all Modern menus.
        AttachMenuAccessibilityHandlers(ModernRadioMenu)
        AttachMenuAccessibilityHandlers(ModernSliceMenu)
        AttachMenuAccessibilityHandlers(ModernFilterMenu)
        AttachMenuAccessibilityHandlers(ModernAudioMenu)
        AttachMenuAccessibilityHandlers(ModernToolsMenu)
    End Sub

    ''' <summary>
    ''' Add a menu item that delegates to an existing handler.
    ''' </summary>
    Private Function AddModernMenuItem(parent As ToolStripMenuItem, text As String, handler As EventHandler) As ToolStripMenuItem
        Dim item = New ToolStripMenuItem() With {
            .Text = text,
            .AccessibleName = text.Replace("&", ""),
            .AccessibleRole = AccessibleRole.MenuItem
        }
        AddHandler item.Click, handler
        parent.DropDownItems.Add(item)
        Return item
    End Function

    ''' <summary>
    ''' Add a submenu (parent item with children) under a parent menu.
    ''' </summary>
    Private Function AddModernSubmenu(parent As ToolStripMenuItem, text As String) As ToolStripMenuItem
        Dim item = New ToolStripMenuItem() With {
            .Text = text,
            .AccessibleName = text.Replace("&", ""),
            .AccessibleRole = AccessibleRole.MenuItem
        }
        parent.DropDownItems.Add(item)
        Return item
    End Function

    ''' <summary>
    ''' Add a stub menu item that announces "coming soon" when clicked.
    ''' </summary>
    Private Function AddModernStubItem(parent As ToolStripMenuItem, text As String) As ToolStripMenuItem
        Dim item = New ToolStripMenuItem() With {
            .Text = text,
            .AccessibleName = text.Replace("&", "") & " - coming soon",
            .AccessibleDescription = "Coming soon. Use Classic mode for full features.",
            .AccessibleRole = AccessibleRole.MenuItem,
            .Enabled = False
        }
        parent.DropDownItems.Add(item)
        Return item
    End Function

    ''' <summary>
    ''' Apply the current UI mode by showing/hiding the appropriate menus.
    ''' Called from Form1_Load, powerNowOn, operator change, and toggle handler.
    ''' </summary>
    Friend Sub ApplyUIMode()
        Dim mode = ActiveUIMode
        Tracing.TraceLine("ApplyUIMode: " & mode.ToString(), TraceLevel.Info)

        ' Track the base mode (Classic or Modern) so we can restore it when leaving Logging.
        If mode <> UIMode.Logging Then
            LastNonLogMode = mode
        End If

        If mode = UIMode.Logging Then
            ShowLoggingUI()
        ElseIf mode = UIMode.Modern Then
            ShowModernUI()
        Else
            ShowClassicUI()
        End If
    End Sub

    ''' <summary>
    ''' Show Classic menus, hide Modern and Logging menus.
    ''' Show standard radio controls, hide Logging panels.
    ''' </summary>
    Private Sub ShowClassicUI()
        ' Show Classic menus
        ActionsToolStripMenuItem.Visible = True
        ScreenFieldsMenu.Visible = True
        OperationsMenuItem.Visible = True

        ' Hide Modern menus
        If ModernRadioMenu IsNot Nothing Then ModernRadioMenu.Visible = False
        If ModernSliceMenu IsNot Nothing Then ModernSliceMenu.Visible = False
        If ModernFilterMenu IsNot Nothing Then ModernFilterMenu.Visible = False
        If ModernAudioMenu IsNot Nothing Then ModernAudioMenu.Visible = False
        If ModernToolsMenu IsNot Nothing Then ModernToolsMenu.Visible = False

        ' Hide Logging menus and panels
        If LoggingLogMenu IsNot Nothing Then LoggingLogMenu.Visible = False
        If LoggingNavigateMenu IsNot Nothing Then LoggingNavigateMenu.Visible = False
        If LoggingModeMenu IsNot Nothing Then LoggingModeMenu.Visible = False
        If LoggingSplitContainer IsNot Nothing Then LoggingSplitContainer.Visible = False

        ' Show standard radio controls (restores Visible and TabStop)
        ShowRadioControls(True)

        HelpToolStripMenuItem.Visible = True
    End Sub

    ''' <summary>
    ''' Show Modern menus, hide Classic and Logging menus.
    ''' Show standard radio controls, hide Logging panels.
    ''' </summary>
    Private Sub ShowModernUI()
        ' Hide Classic menus
        ActionsToolStripMenuItem.Visible = False
        ScreenFieldsMenu.Visible = False
        OperationsMenuItem.Visible = False

        ' Show Modern menus
        If ModernRadioMenu IsNot Nothing Then ModernRadioMenu.Visible = True
        If ModernSliceMenu IsNot Nothing Then ModernSliceMenu.Visible = True
        If ModernFilterMenu IsNot Nothing Then ModernFilterMenu.Visible = True
        If ModernAudioMenu IsNot Nothing Then ModernAudioMenu.Visible = True
        If ModernToolsMenu IsNot Nothing Then ModernToolsMenu.Visible = True

        ' Hide Logging menus and panels
        If LoggingLogMenu IsNot Nothing Then LoggingLogMenu.Visible = False
        If LoggingNavigateMenu IsNot Nothing Then LoggingNavigateMenu.Visible = False
        If LoggingModeMenu IsNot Nothing Then LoggingModeMenu.Visible = False
        If LoggingSplitContainer IsNot Nothing Then LoggingSplitContainer.Visible = False

        ' Show standard radio controls (restores Visible and TabStop)
        ShowRadioControls(True)

        HelpToolStripMenuItem.Visible = True
    End Sub

    ''' <summary>
    ''' Show Logging menus and panels, hide Classic and Modern menus.
    ''' Hide standard radio controls — the RadioPane provides a minimal display.
    ''' </summary>
    Private Sub ShowLoggingUI()
        Tracing.TraceLine("ShowLoggingUI", TraceLevel.Info)

        ' Hide Classic menus
        ActionsToolStripMenuItem.Visible = False
        ScreenFieldsMenu.Visible = False
        OperationsMenuItem.Visible = False

        ' Hide Modern menus
        If ModernRadioMenu IsNot Nothing Then ModernRadioMenu.Visible = False
        If ModernSliceMenu IsNot Nothing Then ModernSliceMenu.Visible = False
        If ModernFilterMenu IsNot Nothing Then ModernFilterMenu.Visible = False
        If ModernAudioMenu IsNot Nothing Then ModernAudioMenu.Visible = False
        If ModernToolsMenu IsNot Nothing Then ModernToolsMenu.Visible = False

        ' Show Logging menus
        If LoggingLogMenu IsNot Nothing Then LoggingLogMenu.Visible = True
        If LoggingNavigateMenu IsNot Nothing Then LoggingNavigateMenu.Visible = True
        If LoggingModeMenu IsNot Nothing Then LoggingModeMenu.Visible = True

        ' Hide standard radio controls (including TabStop), show Logging panels
        ShowRadioControls(False)
        If LoggingSplitContainer IsNot Nothing Then
            ' Recalculate size in case form layout changed since BuildLoggingPanels.
            LoggingSplitContainer.Location = New Drawing.Point(0, MenuStrip1.Bottom)
            LoggingSplitContainer.Size = New Drawing.Size(
                Me.ClientSize.Width,
                Me.ClientSize.Height - MenuStrip1.Height)
            LoggingSplitContainer.Visible = True
            LoggingSplitContainer.BringToFront()
        End If
        If LoggingRadioPane IsNot Nothing Then LoggingRadioPane.UpdateFromRadio()

        ' Initialize the LogPanel session if needed.
        InitializeLoggingSession()

        HelpToolStripMenuItem.Visible = True
    End Sub

    ''' <summary>
    ''' Toggle between Classic and Modern modes. Saves, applies, and speaks confirmation.
    ''' Wired to Ctrl+Shift+M and to the menu items in both Classic and Modern menus.
    ''' Ignored while in Logging Mode — user must exit Logging Mode first.
    ''' </summary>
    Friend Sub ToggleUIMode()
        If CurrentOp Is Nothing Then Return

        ' Ignore while in Logging Mode.
        If ActiveUIMode = UIMode.Logging Then Return

        Dim newMode As UIMode
        If ActiveUIMode = UIMode.Modern Then
            newMode = UIMode.Classic
        Else
            newMode = UIMode.Modern
        End If

        ActiveUIMode = newMode
        ApplyUIMode()

        Dim msg = "Switched to " & newMode.ToString() & " mode"
        Radios.ScreenReaderOutput.Speak(msg, True)
        Tracing.TraceLine("ToggleUIMode: " & msg, TraceLevel.Info)
    End Sub

    ''' <summary>
    ''' Enter Logging Mode from Classic or Modern.
    ''' Saves the current base mode, switches to Logging, auto-opens log if needed.
    ''' </summary>
    Friend Sub EnterLoggingMode()
        Tracing.TraceLine("EnterLoggingMode: ActiveUIMode=" & ActiveUIMode.ToString(), TraceLevel.Info)
        If CurrentOp Is Nothing Then
            Tracing.TraceLine("EnterLoggingMode: ABORT — no operator", TraceLevel.Warning)
            Return
        End If
        If ActiveUIMode = UIMode.Logging Then
            Tracing.TraceLine("EnterLoggingMode: ABORT — already in Logging Mode", TraceLevel.Warning)
            Return
        End If

        ' Remember which base mode we're in so ExitLoggingMode can restore it.
        LastNonLogMode = ActiveUIMode
        ActiveUIMode = UIMode.Logging
        ApplyUIMode()

        ' Auto-open the operator's last used log file if no session is active.
        If ContactLog Is Nothing AndAlso Not String.IsNullOrEmpty(CurrentOp.LogFile) Then
            ConfigContactLog()
        End If

        ' Initialize callbook lookup if configured.
        If LoggingLogPanel IsNot Nothing AndAlso CurrentOp IsNot Nothing Then
            LoggingLogPanel.InitializeCallbook(
                CurrentOp.CallbookLookupSource,
                CurrentOp.CallbookUsername,
                CurrentOp.DecryptedCallbookPassword,
                If(CurrentOp.callSign, ""))
        End If

        ' Focus the log panel call sign field.
        If LoggingLogPanel IsNot Nothing Then LoggingLogPanel.FocusCallSign()

        Radios.ScreenReaderOutput.Speak("Entering Logging Mode", True)
        Tracing.TraceLine("EnterLoggingMode: from " & LastNonLogMode.ToString(), TraceLevel.Info)
    End Sub

    ''' <summary>
    ''' Exit Logging Mode and return to the saved base mode (Classic or Modern).
    ''' Log session stays open; QSO-in-progress state is preserved via LogPanel.SaveState.
    ''' </summary>
    Friend Sub ExitLoggingMode()
        If CurrentOp Is Nothing Then Return
        If ActiveUIMode <> UIMode.Logging Then Return  ' Not in Logging Mode

        ' Preserve in-progress QSO fields so re-entering Logging Mode restores them.
        If LoggingLogPanel IsNot Nothing Then
            LoggingLogPanel.SaveState()
            LoggingLogPanel.FinishCallbook()
        End If

        Dim returnMode = LastNonLogMode
        ActiveUIMode = returnMode
        ApplyUIMode()

        Dim msg = "Returning to " & returnMode.ToString() & " mode"
        Radios.ScreenReaderOutput.Speak(msg, True)
        Tracing.TraceLine("ExitLoggingMode: " & msg, TraceLevel.Info)
    End Sub

    ''' <summary>
    ''' Open JJ's full LogEntry form as a modal dialog from Logging Mode.
    ''' <summary>
    ''' Open the Station Lookup dialog from Logging Mode.
    ''' Reuses the existing KeyCommands StationLookup form via the global instance.
    ''' </summary>
    Private Sub stationLookupFromLogging()
        If LookupStation Is Nothing Then
            LookupStation = New StationLookup()
        End If
        LookupStation.ShowDialog()
    End Sub

    ''' Provides access to all ADIF fields and record navigation that the
    ''' quick-entry LogPanel doesn't have.  Wired to Ctrl+Alt+L.
    ''' After closing, the LogPanel's recent grid is refreshed.
    ''' </summary>
    Private Sub OpenFullLogEntry()
        If ContactLog Is Nothing Then
            Radios.ScreenReaderOutput.Speak("No log file is open", True)
            Return
        End If

        ' Prompt to save in-progress LogPanel entry before opening the full form.
        If LoggingLogPanel IsNot Nothing AndAlso LoggingLogPanel.HasUnsavedEntry() Then
            Dim result = MessageBox.Show(
                "Save the current log panel entry before opening the full log form?",
                "Unsaved Entry",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question)
            Select Case result
                Case DialogResult.Yes
                    If Not LoggingLogPanel.WriteEntry() Then Return  ' Validation failed, stay in LogPanel.
                Case DialogResult.Cancel
                    Return  ' User cancelled.
                    ' DialogResult.No → discard, continue opening full form.
            End Select
        End If

        ' Open the full LogEntry form — new entry at end of file.
        LogEntry.FieldID = adif.AdifTags.ADIF_Call
        LogEntry.FilePosition = -1
        Radios.ScreenReaderOutput.Speak("Opening full log form", True)
        LogEntry.ShowDialog()

        ' Refresh LogPanel grid after returning (user may have logged QSOs in the full form).
        If LoggingLogPanel IsNot Nothing AndAlso LoggingPanelSession IsNot Nothing Then
            LoggingLogPanel.Initialize(LoggingPanelSession)
            LoggingLogPanel.FocusCallSign()
        End If

        Radios.ScreenReaderOutput.Speak("Returned to logging mode", True)
    End Sub

    ''' <summary>
    ''' Toggle Logging Mode on/off. Wired to Ctrl+Shift+L.
    ''' </summary>
    Friend Sub ToggleLoggingMode()
        If ActiveUIMode = UIMode.Logging Then
            ExitLoggingMode()
        Else
            EnterLoggingMode()
        End If
    End Sub

    ''' <summary>
    ''' Build the Logging Mode menu structure programmatically.
    ''' Called once during Form1_Load. Menus start hidden; ApplyUIMode controls visibility.
    ''' </summary>
    Private Sub BuildLoggingMenus()
        ' --- Log menu ---
        LoggingLogMenu = New ToolStripMenuItem() With {
            .Name = "LoggingLogMenu",
            .Text = "Log",
            .AccessibleRole = AccessibleRole.MenuPopup,
            .Visible = False
        }
        AddModernMenuItem(LoggingLogMenu, "New Entry",
            Sub(s As Object, ev As EventArgs)
                If Commands IsNot Nothing Then
                    Commands.CommandId = KeyCommands.CommandValues.NewLogEntry
                    Commands.KeyTable.First(Function(k) k.key.id = KeyCommands.CommandValues.NewLogEntry).rtn()
                End If
            End Sub)
        AddModernMenuItem(LoggingLogMenu, "Write Entry",
            Sub(s As Object, ev As EventArgs)
                If Commands IsNot Nothing Then
                    Commands.CommandId = KeyCommands.CommandValues.LogFinalize
                    Commands.KeyTable.First(Function(k) k.key.id = KeyCommands.CommandValues.LogFinalize).rtn()
                End If
            End Sub)
        AddModernMenuItem(LoggingLogMenu, "Search Log",
            Sub(s As Object, ev As EventArgs)
                If Commands IsNot Nothing Then Commands.SearchLogCmd()
            End Sub)
        AddModernMenuItem(LoggingLogMenu, "Full Log Form",
            Sub(s As Object, ev As EventArgs)
                OpenFullLogEntry()
            End Sub)
        LoggingLogMenu.DropDownItems.Add(New ToolStripSeparator())
        AddModernMenuItem(LoggingLogMenu, "Log Characteristics", AddressOf LogCharacteristicsMenuItem_Click)
        AddModernMenuItem(LoggingLogMenu, "Import Log", AddressOf ImportMenuItem_Click)
        AddModernMenuItem(LoggingLogMenu, "Export Log", AddressOf ExportMenuItem_Click)
        AddModernMenuItem(LoggingLogMenu, "LOTW Merge", AddressOf LOTWMergeMenuItem_Click)
        LoggingLogMenu.DropDownItems.Add(New ToolStripSeparator())
        AddModernMenuItem(LoggingLogMenu, "Log Statistics",
            Sub(s As Object, ev As EventArgs)
                If Commands IsNot Nothing Then
                    Commands.CommandId = KeyCommands.CommandValues.LogStats
                    Commands.KeyTable.First(Function(k) k.key.id = KeyCommands.CommandValues.LogStats).rtn()
                End If
            End Sub)
        LoggingLogMenu.DropDownItems.Add(New ToolStripSeparator())
        AddModernMenuItem(LoggingLogMenu, "Reset Confirmations",
            Sub(s As Object, ev As EventArgs)
                If CurrentOp IsNot Nothing Then
                    CurrentOp.SuppressClearConfirm = False
                    Operators.UpdateCurrentOp()
                    Radios.ScreenReaderOutput.Speak("Confirmation dialogs restored", True)
                End If
            End Sub)

        ' --- Navigate menu ---
        LoggingNavigateMenu = New ToolStripMenuItem() With {
            .Name = "LoggingNavigateMenu",
            .Text = "Navigate",
            .AccessibleRole = AccessibleRole.MenuPopup,
            .Visible = False
        }
        AddModernStubItem(LoggingNavigateMenu, "First Entry")
        AddModernStubItem(LoggingNavigateMenu, "Previous Entry")
        AddModernStubItem(LoggingNavigateMenu, "Next Entry")
        AddModernStubItem(LoggingNavigateMenu, "Last Entry")

        ' --- Mode menu ---
        LoggingModeMenu = New ToolStripMenuItem() With {
            .Name = "LoggingModeMenu",
            .Text = "Mode",
            .AccessibleRole = AccessibleRole.MenuPopup,
            .Visible = False
        }
        AddModernMenuItem(LoggingModeMenu, "Switch to Classic",
            Sub(s As Object, ev As EventArgs)
                ExitLoggingMode()
                If ActiveUIMode <> UIMode.Classic Then
                    ActiveUIMode = UIMode.Classic
                    ApplyUIMode()
                End If
                Radios.ScreenReaderOutput.Speak("Switched to Classic mode", True)
            End Sub)
        AddModernMenuItem(LoggingModeMenu, "Switch to Modern",
            Sub(s As Object, ev As EventArgs)
                ExitLoggingMode()
                If ActiveUIMode <> UIMode.Modern Then
                    ActiveUIMode = UIMode.Modern
                    ApplyUIMode()
                End If
                Radios.ScreenReaderOutput.Speak("Switched to Modern mode", True)
            End Sub)

        ' --- Add Logging menus to the menu strip (before Help) ---
        Dim helpIndex As Integer = MenuStrip1.Items.IndexOf(HelpToolStripMenuItem)
        If helpIndex < 0 Then helpIndex = MenuStrip1.Items.Count
        MenuStrip1.Items.Insert(helpIndex, LoggingLogMenu)
        MenuStrip1.Items.Insert(helpIndex + 1, LoggingNavigateMenu)
        MenuStrip1.Items.Insert(helpIndex + 2, LoggingModeMenu)

        ' Attach accessibility handlers.
        AttachMenuAccessibilityHandlers(LoggingLogMenu)
        AttachMenuAccessibilityHandlers(LoggingNavigateMenu)
        AttachMenuAccessibilityHandlers(LoggingModeMenu)
    End Sub

    ''' <summary>
    ''' Handle mode toggles, pane switching, and Logging Mode field-jump hotkeys.
    ''' In Logging Mode, Classic log hotkeys are intercepted here and redirected
    ''' to the LogPanel instead of opening the full-screen LogEntry form.
    ''' </summary>
    Protected Overrides Function ProcessCmdKey(ByRef msg As Message, keyData As Keys) As Boolean
        ' --- Mode toggles (always active) ---
        If keyData = (Keys.Control Or Keys.Shift Or Keys.M) Then
            ToggleUIMode()
            Return True
        End If
        If keyData = (Keys.Control Or Keys.Shift Or Keys.L) Then
            ToggleLoggingMode()
            Return True
        End If

        ' --- Logging Mode hotkeys (only when in Logging Mode) ---
        If ActiveUIMode = UIMode.Logging AndAlso LoggingLogPanel IsNot Nothing Then
            ' F6 pane switching.
            If keyData = Keys.F6 OrElse keyData = (Keys.F6 Or Keys.Shift) Then
                ToggleLoggingPaneFocus()
                Return True
            End If

            ' Field-jump hotkeys — redirect Classic log keys to LogPanel fields.
            Select Case keyData
                Case Keys.C Or Keys.Alt               ' Alt+C → Call Sign
                    LoggingLogPanel.FocusField("CALL")
                    Return True
                Case Keys.T Or Keys.Alt                ' Alt+T → RST Sent (senT)
                    LoggingLogPanel.FocusField("RSTSENT")
                    Return True
                Case Keys.R Or Keys.Alt                ' Alt+R → RST Received
                    LoggingLogPanel.FocusField("RSTRCVD")
                    Return True
                Case Keys.N Or Keys.Alt                ' Alt+N → Name
                    LoggingLogPanel.FocusField("NAME")
                    Return True
                Case Keys.Q Or Keys.Alt                ' Alt+Q → QTH
                    LoggingLogPanel.FocusField("QTH")
                    Return True
                Case Keys.S Or Keys.Alt                ' Alt+S → State
                    LoggingLogPanel.FocusField("STATE")
                    Return True
                Case Keys.G Or Keys.Alt                ' Alt+G → Grid
                    LoggingLogPanel.FocusField("GRID")
                    Return True
                Case Keys.E Or Keys.Alt                ' Alt+E → Comments
                    LoggingLogPanel.FocusField("COMMENTS")
                    Return True

                ' Action hotkeys.
                Case Keys.N Or Keys.Control            ' Ctrl+N → New Entry (clear form)
                    LoggingLogPanel.NewEntry()
                    Radios.ScreenReaderOutput.Speak("New entry", True)
                    Return True
                Case Keys.W Or Keys.Control            ' Ctrl+W → Write/Save Entry
                    LoggingLogPanel.WriteEntry()
                    Return True
                Case Keys.N Or Keys.Control Or Keys.Shift  ' Ctrl+Shift+N → Log Characteristics
                    LogCharacteristicsMenuItem_Click(Nothing, EventArgs.Empty)
                    Return True
                Case Keys.L Or Keys.Control Or Keys.Alt  ' Ctrl+Alt+L → Full Log Entry form
                    OpenFullLogEntry()
                    Return True
            End Select
        End If

        Return MyBase.ProcessCmdKey(msg, keyData)
    End Function

    ''' <summary>
    ''' Create the SplitContainer, RadioPane, and LogPanel for Logging Mode.
    ''' Called once during Form1_Load. Panels start hidden; ShowLoggingUI controls visibility.
    ''' </summary>
    Private Sub BuildLoggingPanels()
        ' Create the radio pane (left side).
        LoggingRadioPane = New RadioPane()

        ' Create the log entry panel (right side).
        LoggingLogPanel = New LogPanel()

        ' Create a split container to host both panes.
        LoggingSplitContainer = New SplitContainer() With {
            .Name = "LoggingSplitContainer",
            .Dock = DockStyle.None,
            .Location = New Drawing.Point(0, MenuStrip1.Bottom),
            .Size = New Drawing.Size(Me.ClientSize.Width,
                                     StatusBox.Top - MenuStrip1.Bottom),
            .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right Or AnchorStyles.Bottom,
            .SplitterDistance = 200,
            .FixedPanel = FixedPanel.Panel1,
            .Orientation = Orientation.Vertical,
            .Visible = False,
            .AccessibleName = "Logging Mode",
            .AccessibleRole = AccessibleRole.Pane
        }
        LoggingRadioPane.Dock = DockStyle.Fill
        LoggingLogPanel.Dock = DockStyle.Fill
        LoggingSplitContainer.Panel1.Controls.Add(LoggingRadioPane)
        LoggingSplitContainer.Panel2.Controls.Add(LoggingLogPanel)

        ' Prevent the splitter bar from getting Tab focus.
        LoggingSplitContainer.TabStop = False

        Me.Controls.Add(LoggingSplitContainer)
        LoggingSplitContainer.BringToFront()
    End Sub

    ''' <summary>
    ''' Initialize the LogPanel's session when entering Logging Mode.
    ''' Creates a LogSession from the global ContactLog if needed.
    ''' </summary>
    Private Sub InitializeLoggingSession()
        If LoggingLogPanel Is Nothing Then Return
        If ContactLog Is Nothing Then Return

        ' Create a new session if we don't have one yet.
        If LoggingPanelSession Is Nothing Then
            LoggingPanelSession = New LogSession(ContactLog)
            Dim clean As New LogClass.cleanupClass("LogPanel",
                Function() As Boolean
                    ' Allow cleanup — the panel isn't modal.
                    Return True
                End Function)
            If Not LoggingPanelSession.Start(CurrentOp, clean) Then
                Tracing.TraceLine("InitializeLoggingSession: session start failed",
                                  Diagnostics.TraceLevel.Error)
                LoggingPanelSession = Nothing
                Return
            End If
        End If
        LoggingLogPanel.Initialize(LoggingPanelSession)
    End Sub

    ''' <summary>
    ''' Show or hide the standard radio controls (used during mode transitions).
    ''' </summary>
    Private Sub ShowRadioControls(visible As Boolean)
        FreqOut.Visible = visible
        FreqOut.TabStop = visible
        ModeControl.Visible = visible
        ModeControl.TabStop = visible
        TXTuneControl.Visible = visible
        TXTuneControl.TabStop = visible
        TransmitButton.Visible = visible
        TransmitButton.TabStop = visible
        AntennaTuneButton.Visible = visible
        AntennaTuneButton.TabStop = visible
        ReceivedTextBox.Visible = visible
        ReceivedTextBox.TabStop = visible
        SentTextBox.Visible = visible
        SentTextBox.TabStop = visible
        ReceiveLabel.Visible = visible
        SendLabel.Visible = visible
        StatusBox.Visible = visible
        StatusBox.TabStop = visible
        RigFieldsBox.Visible = visible
        RigFieldsBox.TabStop = visible
        ' Also hide the dynamically-added RigFields panel (e.g. Flex6300Filters).
        If RigControl IsNot Nothing AndAlso RigControl.RigFields IsNot Nothing AndAlso
           RigControl.RigFields.RigControl IsNot Nothing Then
            RigControl.RigFields.RigControl.Visible = visible
            RigControl.RigFields.RigControl.TabStop = visible
        End If
    End Sub

    ''' <summary>
    ''' Toggle focus between RadioPane and LogPanel in Logging Mode.
    ''' F6 / Shift+F6 — standard Windows pane-switching convention.
    ''' </summary>
    Private Sub ToggleLoggingPaneFocus()
        If LoggingRadioPane Is Nothing OrElse LoggingLogPanel Is Nothing Then Return

        If LoggingRadioPane.ContainsFocus Then
            LoggingLogPanel.FocusCallSign()
            Radios.ScreenReaderOutput.Speak("Log entry pane", True)
        Else
            LoggingRadioPane.FocusFirst()
            Radios.ScreenReaderOutput.Speak("Radio pane", True)
        End If
    End Sub

#End Region

End Class

