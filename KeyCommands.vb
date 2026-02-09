Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.IO
Imports System.Math
Imports System.Threading
Imports System.Xml.Serialization
Imports adif
Imports Escapes
Imports HamBands
Imports JJArClusterLib
Imports JJLogLib
Imports JJPortaudio
Imports JJTrace
Imports MsgLib
Imports Radios

Public Class KeyCommands
    ' Commands - New commands are added at the end.
    Public Enum CommandValues
        NotACommand = -1
        ShowHelp = 0
        ShowFreq
        SetFreq
        ShowMemory
        CycleContinuous
        LogForm
        LogDateTime
        LogFinalize
        LogFileName
        LogCall
        LogHisRST
        LogMyRST
        LogQTH
        LogState
        LogHandle
        LogRig
        LogAnt
        LogComments
        NewLogEntry
        StartScan
        MemoryScan
        SavedScan
        StopScan
        LogMode
        SearchLog
        ShowMenus
        ShowReceived
        ShowSend
        StopCW
        SendLoggedCall
        SendLoggedName
        DoPanning
        AudioGainUp
        AudioGainDown
        HeadphonesUp
        HeadphonesDown
        LineoutUp
        LineoutDown
        ResumeTheScan
        CWZeroBeat
        ClearRIT
        ReverseBeacon
        ArCluster
        LogGrid
        Toggle1
        LogStats
        RemoteAudio ' PCAudio
        AudioSetup
        StationLookup
        GatherDebug
        ATUMemories
        Reboot
        TXControls
        showSendDirect
        SmeterDBM
    End Enum
    Friend Const FirstMessageCommandValue As Integer = 1000000

    ' Internal ADIF pseudotags.
    Friend Const iADIF_Logform As String = "$LOGFORM"
    Friend Const iADIF_Logwrite As String = "$LOGWRITE"
    Friend Const iADIF_Logfile As String = "$LOGFILE"
    Friend Const iADIF_LogNewEntry As String = "$LOGNEWENTRY"
    Friend Const iADIF_Logsearch As String = "$LOGSEARCH"

    ''' <summary>
    ''' type of key, command or CWText
    ''' </summary>
    <Flags()>
    Friend Enum KeyTypes
        Command = 1
        CWText = 2
        log = 4
        allKeys = (Command Or CWText Or log)
    End Enum

    ''' <summary>
    ''' Command key definition
    ''' </summary>
    Public Class KeyDefType
        Public key As Keys
        Public i As Integer
        ' This is because the xml serializer on Vista doesn't handle enums.
        <XmlIgnore()> Public Property id As CommandValues
            Get
                Return CType(i, CommandValues)
            End Get
            Set(value As CommandValues)
                i = CType(value, Integer)
            End Set
        End Property
        Public Sub New()
            ' default constructor.
        End Sub
        Public Sub New(k As Keys, i As CommandValues)
            key = k
            id = i
        End Sub
    End Class
    Public ReadOnly Property nCommands As Integer
        Get
            Return [Enum].GetNames(GetType(CommandValues)).Length
        End Get
    End Property
    Friend CommandId As CommandValues

    Delegate Sub kFunc()
    Delegate Function menuTextFuncDel() As String
    Friend Enum FunctionGroups
        audio
        cwMessage
        dialog
        general
        help
        logging
        routing
        routingScan
        scan
    End Enum
    Public Class keyTbl
        Friend key As KeyDefType
        Friend KeyType As KeyTypes
        Friend rtn As kFunc
        Friend helpText As String
        Private menuTextFunc As menuTextFuncDel = Nothing
        Private _menuText As String
        Friend ReadOnly Property menuText As String
            Get
                If menuTextFunc IsNot Nothing Then
                    _menuText = menuTextFunc()
                End If
                Return _menuText
            End Get
        End Property
        Friend ADIFTag As String
        Friend UseWhenLogging As Boolean
        Friend Group As FunctionGroups
        ' Use to copy an entry
        Friend Sub New(ByVal k As keyTbl)
            key = k.key
            KeyType = k.KeyType
            rtn = k.rtn
            helpText = k.helpText
            _menuText = k._menuText
            ADIFTag = k.ADIFTag
            UseWhenLogging = k.UseWhenLogging
            Group = k.Group
        End Sub
        ' for a command
        Friend Sub New(ByVal id As CommandValues, ByVal r As kFunc,
                       ByVal h As String, m As String, g As FunctionGroups)
            key = New KeyDefType(Keys.None, id)
            KeyType = KeyTypes.Command
            rtn = r
            helpText = h
            _menuText = m
            ADIFTag = vbNullString
            UseWhenLogging = False
            Group = g
        End Sub
        ' use with a log field
        Friend Sub New(ByVal id As CommandValues, ByVal r As kFunc,
                       ByVal h As String, m As String, a As String, t As KeyTypes,
                       g As FunctionGroups)
            key = New KeyDefType(Keys.None, id)
            rtn = r
            helpText = h
            _menuText = m
            ADIFTag = a
            KeyType = t
            UseWhenLogging = False ' refers to non-logging commands
            Group = g
        End Sub
        ' Use for a macro
        Friend Sub New(ByVal id As CommandValues, ByVal t As KeyTypes,
                       ByVal r As kFunc, ByVal h As String, g As FunctionGroups)
            key = New KeyDefType(Keys.None, id)
            KeyType = t
            rtn = r
            helpText = h
            _menuText = Nothing
            ADIFTag = vbNullString
            UseWhenLogging = True
            Group = g
        End Sub
        ' Use for a non-logging key allowed during logging
        Friend Sub New(ByVal id As CommandValues, ByVal t As KeyTypes,
                       ByVal r As kFunc, ByVal h As String, m As String,
                       u As Boolean, g As FunctionGroups)
            key = New KeyDefType(Keys.None, id)
            KeyType = t
            rtn = r
            helpText = h
            _menuText = m
            ADIFTag = vbNullString
            UseWhenLogging = u
            Group = g
        End Sub
        ' Use for a command with a dynamic operations menu item.
        Friend Sub New(ByVal id As CommandValues, ByVal t As KeyTypes,
                       ByVal r As kFunc, ByVal h As String, m As menuTextFuncDel,
                       u As Boolean, g As FunctionGroups)
            key = New KeyDefType(Keys.None, id)
            KeyType = t
            rtn = r
            helpText = h
            menuTextFunc = m
            ADIFTag = vbNullString
            UseWhenLogging = u
            Group = g
        End Sub
    End Class

    ''' <summary>
    ''' Table of key values and commands
    ''' </summary>
    ''' <remarks>It's in logical order, not CommandValues order</remarks>
    Friend KeyTable() As keyTbl = {
        New keyTbl(CommandValues.ShowHelp, AddressOf Form1.DisplayHelp,
            "Show keys help", Nothing, FunctionGroups.help),
        New keyTbl(CommandValues.ShowFreq, AddressOf displayFreqCmd,
            "Show frequency or pause scan", Nothing, FunctionGroups.routingScan),
        New keyTbl(CommandValues.ResumeTheScan, AddressOf resumeScanCmd,
            "Resume the scan.", "resume scan", FunctionGroups.scan),
        New keyTbl(CommandValues.ShowReceived, AddressOf gotoReceive,
            "goto the received text window", Nothing, FunctionGroups.routing),
        New keyTbl(CommandValues.ShowSend, AddressOf gotoSend,
            "go to the send text window", Nothing, FunctionGroups.routing),
        New keyTbl(CommandValues.showSendDirect, AddressOf gotoSendDirect,
            "go to the send text window and send direct from keyboard", Nothing, FunctionGroups.routing),
        New keyTbl(CommandValues.SmeterDBM, KeyTypes.Command, AddressOf smeterDisplayRTN,
            "Display SMeter in DBM or S-units", AddressOf sMeterMenuString, False, FunctionGroups.general),
        New keyTbl(CommandValues.StopCW, KeyTypes.Command, AddressOf stopCode,
            "Stop sending CW", "cw stop", True, FunctionGroups.general),
        New keyTbl(CommandValues.SetFreq, AddressOf WriteFreq,
            "Enter frequency", "frequency", FunctionGroups.general),
        New keyTbl(CommandValues.ShowMemory, AddressOf DisplayMemory,
            "Bring up the memory dialogue", "memories", FunctionGroups.dialog),
        New keyTbl(CommandValues.CycleContinuous, AddressOf cycleContinuous,
            "Toggle continuous frequency display", Nothing, FunctionGroups.general),
        New keyTbl(CommandValues.LogDateTime, AddressOf SetLogDateTime,
            "Set log date/time", "log date/time", AdifTags.ADIF_DateOn, KeyTypes.log, FunctionGroups.logging),
        New keyTbl(CommandValues.LogFinalize, AddressOf FinalizeLog,
            "Write log entry", "log write", iADIF_Logwrite, KeyTypes.Command, FunctionGroups.logging),
        New keyTbl(CommandValues.LogFileName, AddressOf getLogFileName,
            "Enter log file name", "log file name", iADIF_Logfile, KeyTypes.Command, FunctionGroups.logging),
        New keyTbl(CommandValues.LogMode, AddressOf BringUpLogForm,
            "Log the mode", "log mode", AdifTags.ADIF_Mode, KeyTypes.log, FunctionGroups.logging),
        New keyTbl(CommandValues.LogCall, AddressOf BringUpLogForm,
            "Log callsign", "log call", AdifTags.ADIF_Call, KeyTypes.log, FunctionGroups.logging),
        New keyTbl(CommandValues.LogHisRST, AddressOf BringUpLogForm,
            "Log his RST", "log his RST", AdifTags.ADIF_HisRST, KeyTypes.log, FunctionGroups.logging),
        New keyTbl(CommandValues.LogMyRST, AddressOf BringUpLogForm,
            "Log my RST", "log my RST", AdifTags.ADIF_MyRST, KeyTypes.log, FunctionGroups.logging),
        New keyTbl(CommandValues.LogQTH, AddressOf BringUpLogForm,
            "Log QTH", "log QTH", AdifTags.ADIF_QTH, KeyTypes.log, FunctionGroups.logging),
        New keyTbl(CommandValues.LogState, AddressOf BringUpLogForm,
            "Log state/province", "log state", AdifTags.ADIF_State, KeyTypes.log, FunctionGroups.logging),
        New keyTbl(CommandValues.LogGrid, AddressOf BringUpLogForm,
            "Log Grid square", "log Grid", AdifTags.ADIF_Grid, KeyTypes.log, FunctionGroups.logging),
        New keyTbl(CommandValues.LogHandle, AddressOf BringUpLogForm,
            "Log name", "log name", AdifTags.ADIF_Name, KeyTypes.log, FunctionGroups.logging),
        New keyTbl(CommandValues.LogRig, AddressOf BringUpLogForm,
            "Log rig", "log rig", AdifTags.ADIF_Rig, KeyTypes.log, FunctionGroups.logging),
        New keyTbl(CommandValues.LogAnt, AddressOf BringUpLogForm,
            "Log antenna", "log antenna", AdifTags.ADIF_Antenna, KeyTypes.log, FunctionGroups.logging),
        New keyTbl(CommandValues.LogComments, AddressOf BringUpLogForm,
            "Log comments", "log comments", AdifTags.ADIF_Comment, KeyTypes.log, FunctionGroups.logging),
        New keyTbl(CommandValues.NewLogEntry, AddressOf BringUpLogForm,
            "New log entry", "new log entry", iADIF_LogNewEntry, KeyTypes.Command, FunctionGroups.logging),
        New keyTbl(CommandValues.SearchLog, AddressOf SearchLogCmd,
            "Find a log entry", "log search", iADIF_Logsearch, KeyTypes.Command, FunctionGroups.logging),
        New keyTbl(CommandValues.DoPanning, AddressOf startPanning,
            "Focus to panning", "panning", FunctionGroups.general),
        New keyTbl(CommandValues.StartScan, AddressOf BeginScan,
            "Start/stop scan", "start scan", FunctionGroups.scan),
        New keyTbl(CommandValues.SavedScan, AddressOf useSaved,
            "Use a saved scan", "saved scan", FunctionGroups.scan),
        New keyTbl(CommandValues.StopScan, AddressOf stopTheScan,
            "Stop the current scan", "stop scan", FunctionGroups.scan),
        New keyTbl(CommandValues.MemoryScan, AddressOf memScan,
            "Memory scan", "memory scan", FunctionGroups.scan),
        New keyTbl(CommandValues.ShowMenus, AddressOf ShowMenus,
            "Show the rig's menus.", "menus", FunctionGroups.dialog),
        New keyTbl(CommandValues.AudioGainUp, KeyTypes.Command, AddressOf audioGainUp,
            "Raise RF gain or Flex slice gain.", vbNullString, True, FunctionGroups.audio),
        New keyTbl(CommandValues.AudioGainDown, KeyTypes.Command, AddressOf audioGainDown,
            "Lower RF gain or Flex slice gain.", vbNullString, True, FunctionGroups.audio),
        New keyTbl(CommandValues.HeadphonesUp, KeyTypes.Command, AddressOf headphonesUp,
            "If supported, raise headphone gain.", vbNullString, True, FunctionGroups.audio),
        New keyTbl(CommandValues.HeadphonesDown, KeyTypes.Command, AddressOf headphonesDown,
            "If supported, lower headphone gain.", vbNullString, True, FunctionGroups.audio),
        New keyTbl(CommandValues.LineoutUp, KeyTypes.Command, AddressOf lineoutUp,
            "Raise audio gain or Flex lineout gain.", vbNullString, True, FunctionGroups.audio),
        New keyTbl(CommandValues.LineoutDown, KeyTypes.Command, AddressOf lineoutDown,
            "lower audio gain or Flex lineout gain.", vbNullString, True, FunctionGroups.audio),
        New keyTbl(CommandValues.CWZeroBeat, KeyTypes.Command, AddressOf zerobeatRtn,
            "Zerobeat CW signal.", "Zerobeat CW signal", True, FunctionGroups.general),
        New keyTbl(CommandValues.ClearRIT, KeyTypes.Command, AddressOf clearRitRtn,
            "Clear RIT.", "Clear Rit", True, FunctionGroups.general),
        New keyTbl(CommandValues.ReverseBeacon, KeyTypes.Command, AddressOf reverseBeaconCmd,
            "Bring up a reverse beacon site for a call.", "Reverse Beacon", False, FunctionGroups.general),
        New keyTbl(CommandValues.ArCluster, KeyTypes.Command, AddressOf arClusterCmd,
            "Bring up the DX spotting cluster.", "DX cluster", False, FunctionGroups.general),
        New keyTbl(CommandValues.Toggle1, KeyTypes.Command, AddressOf toggle1,
            "Next (rig-dependent field value 1).", "Next value 1", True, FunctionGroups.general),
        New keyTbl(CommandValues.LogStats, KeyTypes.Command, AddressOf logStatsRTN,
            "Show log statistics", "Show log statistics", False, FunctionGroups.logging),
        New keyTbl(CommandValues.RemoteAudio, KeyTypes.Command, AddressOf PCAudioRtn,
            "PC audio on/off", AddressOf audioMenuString, False, FunctionGroups.audio),
        New keyTbl(CommandValues.AudioSetup, KeyTypes.Command, AddressOf audioSetupRtn,
            "Select audio device", "Select Audio Device", False, FunctionGroups.audio),
        New keyTbl(CommandValues.StationLookup, KeyTypes.Command, AddressOf stationLookupRtn,
            "Station lookup", "Station lookup", False, FunctionGroups.logging),
        New keyTbl(CommandValues.GatherDebug, KeyTypes.Command, AddressOf gatherDebugRtn,
            "Collect debug info", "Collect debug info", False, FunctionGroups.general),
        New keyTbl(CommandValues.ATUMemories, KeyTypes.Command, AddressOf ATUMemoriesRtn,
            "Tuner memories", "Tuner memories", False, FunctionGroups.general),
        New keyTbl(CommandValues.Reboot, KeyTypes.Command, AddressOf rebootRtn,
            "Reboot the radio", "Reboot the radio", False, FunctionGroups.general),
        New keyTbl(CommandValues.TXControls, KeyTypes.Command, AddressOf TXControlsRtn,
            "Transmit controls", "Transmit controls", False, FunctionGroups.general)}

    ' Deleted from KeyTable.
    ' New keyTbl(CommandValues.LogForm, AddressOf BringUpLogForm,
    '        "Bring up log", Nothing, iADIF_Logform, KeyTypes.Command),
    ' New keyTbl(CommandValues.SendLoggedCall, AddressOf callFromLog, _
    '        "send the logged call"), _
    ' New keyTbl(CommandValues.SendLoggedName, AddressOf nameFromLog, _
    '        "send the logged name"), _

    ' default key definitions
    Private defaultKeys() As KeyDefType =
    {New KeyDefType(Keys.F1, CommandValues.ShowHelp),
     New KeyDefType(Keys.F2, CommandValues.ShowFreq),
     New KeyDefType(Keys.F2 Or Keys.Shift, CommandValues.ResumeTheScan),
     New KeyDefType(Keys.F3, CommandValues.ShowReceived),
     New KeyDefType(Keys.F4, CommandValues.ShowSend),
     New KeyDefType(Keys.Control Or Keys.F4, CommandValues.showSendDirect),
     New KeyDefType(Keys.F12, CommandValues.StopCW),
     New KeyDefType(Keys.F Or Keys.Control, CommandValues.SetFreq),
     New KeyDefType(Keys.M Or Keys.Control, CommandValues.ShowMemory),
     New KeyDefType(Keys.M Or Keys.Control Or Keys.Shift, CommandValues.MemoryScan),
     New KeyDefType(Keys.None, CommandValues.SmeterDBM),
     New KeyDefType(Keys.None, CommandValues.CycleContinuous),
     New KeyDefType(Keys.None, CommandValues.LogForm),
     New KeyDefType(Keys.D Or Keys.Alt, CommandValues.LogDateTime),
     New KeyDefType(Keys.D Or Keys.Control Or Keys.Shift, CommandValues.ArCluster),
     New KeyDefType(Keys.W Or Keys.Control, CommandValues.LogFinalize),
     New KeyDefType(Keys.None, CommandValues.LogFileName),  ' Was Ctrl+Shift+L; freed for Logging Mode toggle
     New KeyDefType(Keys.None, CommandValues.LogMode),
     New KeyDefType(Keys.None, CommandValues.LogCall),  ' Was Alt+C; log entry moved to Logging Mode
     New KeyDefType(Keys.C Or Keys.Control, CommandValues.CWZeroBeat),
     New KeyDefType(Keys.C Or Keys.Control Or Keys.Shift, CommandValues.ClearRIT),
     New KeyDefType(Keys.None, CommandValues.LogHisRST),  ' Was Ctrl+H; log entry moved to Logging Mode
     New KeyDefType(Keys.None, CommandValues.LogMyRST),  ' Was Alt+M; log entry moved to Logging Mode
     New KeyDefType(Keys.None, CommandValues.LogQTH),  ' Was Alt+Q; log entry moved to Logging Mode
     New KeyDefType(Keys.None, CommandValues.LogState),  ' Was Alt+S; log entry moved to Logging Mode
     New KeyDefType(Keys.None, CommandValues.LogGrid),  ' Was Alt+G; log entry moved to Logging Mode
     New KeyDefType(Keys.None, CommandValues.LogHandle),  ' Was Alt+N; log entry moved to Logging Mode
     New KeyDefType(Keys.None, CommandValues.LogRig),  ' Was Alt+R; log entry moved to Logging Mode
     New KeyDefType(Keys.R Or Keys.Control Or Keys.Shift, CommandValues.ReverseBeacon),
     New KeyDefType(Keys.None, CommandValues.LogAnt),  ' Was Ctrl+A; log entry moved to Logging Mode
     New KeyDefType(Keys.None, CommandValues.LogComments),  ' Was Alt+E; log entry moved to Logging Mode
     New KeyDefType(Keys.None, CommandValues.NewLogEntry),  ' Was Ctrl+N; log entry moved to Logging Mode
     New KeyDefType(Keys.F Or Keys.Control Or Keys.Shift, CommandValues.SearchLog),
     New KeyDefType(Keys.P Or Keys.Control, CommandValues.DoPanning),
     New KeyDefType(Keys.S Or Keys.Control, CommandValues.StartScan),
     New KeyDefType(Keys.U Or Keys.Control Or Keys.Shift, CommandValues.SavedScan),
     New KeyDefType(Keys.Z Or Keys.Control, CommandValues.StopScan),
     New KeyDefType(Keys.None, CommandValues.ShowMenus),
     New KeyDefType(Keys.PageUp Or Keys.Alt, CommandValues.AudioGainUp),
     New KeyDefType(Keys.PageDown Or Keys.Alt, CommandValues.AudioGainDown),
     New KeyDefType(Keys.PageUp Or Keys.Alt Or Keys.Shift, CommandValues.HeadphonesUp),
     New KeyDefType(Keys.PageDown Or Keys.Alt Or Keys.Shift, CommandValues.HeadphonesDown),
     New KeyDefType(Keys.PageUp Or Keys.Shift, CommandValues.LineoutUp),
     New KeyDefType(Keys.PageDown Or Keys.Shift, CommandValues.LineoutDown),
     New KeyDefType(Keys.F9 Or Keys.Control, CommandValues.Toggle1),
     New KeyDefType(Keys.T Or Keys.Control Or Keys.Shift, CommandValues.LogStats),
     New KeyDefType(Keys.None, CommandValues.RemoteAudio),
     New KeyDefType(Keys.None, CommandValues.AudioSetup),
     New KeyDefType(Keys.L Or Keys.Control, CommandValues.StationLookup),
     New KeyDefType(Keys.None, CommandValues.GatherDebug),
     New KeyDefType(Keys.None, CommandValues.ATUMemories),
     New KeyDefType(Keys.None, CommandValues.Reboot),
     New KeyDefType(Keys.None, CommandValues.TXControls)}

    ''' <summary>
    ''' Dictionary to access the keytable using a key.
    ''' </summary>
    Friend KeyDictionary As Dictionary(Of Keys, keyTbl)
    ''' <summary>
    ''' Add to the key dictionary if not already added.
    ''' </summary>
    ''' <param name="item">the keytbl entry to add</param>
    ''' <returns>True if added, false if already there.</returns>
    Friend Function AddToKeyDictionary(item As keyTbl) As Boolean
        Dim k As Keys = item.key.key
        ' Add if the key isn't already there.
        Dim rv As Boolean = ((k <> Keys.None) AndAlso (lookup(k) Is Nothing))
        If rv Then
            KeyDictionary.Add(key:=k, value:=item)
        End If
        Return rv
    End Function

    ''' <summary>
    ''' (Overloaded) Look for a defined key.
    ''' </summary>
    ''' <param name="k">the key</param>
    ''' <returns>a keytbl entry or nothing</returns>
    Friend Function lookup(k As Keys) As keyTbl
        Dim rv As keyTbl = Nothing
        If Not KeyDictionary.TryGetValue(k, rv) Then
            rv = Nothing
        End If
        Return rv
    End Function

    ''' <summary>
    ''' Dictionary to access the keytable using a CommandValue.
    ''' </summary>
    Private KeydefDictionary As Dictionary(Of CommandValues, keyTbl)
    ''' <summary>
    ''' Add to the CommandValue dictionary if not already added.
    ''' </summary>
    ''' <param name="item">the keytbl entry to add</param>
    ''' <returns>True if added, false if already there.</returns>
    Friend Function AddToKeydefDictionary(item As keyTbl) As Boolean
        Dim k As CommandValues = item.key.id
        ' Add if the key isn't already there.
        Dim rv As Boolean = (lookup(k) Is Nothing)
        If rv Then
            KeydefDictionary.Add(key:=k, value:=item)
        End If
        Return rv
    End Function
    ''' <summary>
    ''' (Overloaded) Look for a defined CommandValue.
    ''' </summary>
    ''' <param name="k">the value</param>
    ''' <returns>a keytbl entry or nothing</returns>
    Friend Function lookup(k As CommandValues) As keyTbl
        Dim rv As keyTbl = Nothing
        If Not KeydefDictionary.TryGetValue(k, rv) Then
            rv = Nothing
        End If
        Return rv
    End Function

    ' Old (deprecated) keydefs data.  Will be converted to keyConfigType_v1.
    Public Class KeyConfigData
        Public Items As Keys()
        <XmlIgnore()> Public Shared ReadOnly Property PathName As String
            Get
                Return BaseConfigDir & "\" & "KeyDefs.xml"
            End Get
        End Property
    End Class
    Public Class KeyConfigType_V1
        Public Items As KeyDefType()
        <XmlIgnore()> Public TraceLevel As Integer ' enum can cause problems
        <XmlIgnore()> Public Shared ReadOnly Property PathName As String
            Get
                Return BaseConfigDir & "\" & "KeyDefs.xml"
            End Get
        End Property
        Public Sub New()
            ' default constructor.
        End Sub
        Public Sub New(sz As Integer)
            ReDim Items(sz)
        End Sub
    End Class

    Private Sub setupData()
        ' Setup the dictionaries.
        KeyDictionary = New Dictionary(Of Keys, keyTbl)
        KeydefDictionary = New Dictionary(Of CommandValues, keyTbl)
        ' setup the KeydefDictionary of CommandValues.
        For Each k As keyTbl In KeyTable
            KeydefDictionary.Add(key:=k.key.id, value:=k)
        Next
    End Sub

    ''' <summary> Load the key definitions </summary>
    Friend Sub New()
        Tracing.TraceLine("KeyCommands new()", TraceLevel.Info)
        setupData()
        Dim cfgFile As Stream = Nothing
        Try
            cfgFile = File.Open(KeyConfigType_V1.PathName, FileMode.Open)
        Catch ex As Exception
            If Err.Number = 53 Then
                ' No key file.  Create one.
                keyTableToDefault(True)
            Else
                Tracing.ErrMessageTrace(ex)
            End If
            If cfgFile IsNot Nothing Then
                cfgFile.Dispose()
            End If
            Return
        End Try
        ' Read any customizations.
        keyTableToDefault(False) ' put default keys into keytable.
        Dim xs As New XmlSerializer(GetType(KeyConfigType_V1))
        Try
            Dim kData As KeyConfigType_V1 = xs.Deserialize(cfgFile)
            cfgFile.Close()
            SetValues(kData.Items, KeyTypes.allKeys, False)
            murgeNewDefaults()
        Catch ex As Exception
            Tracing.TraceLine("KeyCommands new:" & ex.Message & vbCrLf & ex.InnerException.Message, TraceLevel.Error)
            ' See if it's an old format file.
            Dim oldxs As New XmlSerializer(GetType(KeyConfigData))
            Try
                cfgFile.Close()
                cfgFile = File.Open(KeyConfigData.PathName, FileMode.Open)
                Dim oldkData As KeyConfigData = oldxs.Deserialize(cfgFile)
                cfgFile.Close()
                ' oldkData.Items is in CommandValues order.
                Dim newDefs(oldkData.Items.Length - 1) As KeyDefType
                For i As Integer = 0 To newDefs.Length - 1
                    newDefs(i) = New KeyDefType(oldkData.Items(i), i)
                Next
                ' This reformats the keydefs file.
                SetValues(newDefs, KeyTypes.allKeys, True)
                murgeNewDefaults()
            Catch ex2 As Exception
                ' unknown format.  Create a valid keydefs file.
                keyTableToDefault(True)
                Tracing.ErrMessageTrace(ex2, True)
            End Try
        Finally
            cfgFile.Close()
            cfgFile.Dispose()
        End Try
    End Sub
    Friend Sub New(ByVal setDefault As Boolean)
        Tracing.TraceLine("KeyCommands new(" & setDefault.ToString & ")", TraceLevel.Info)
        setupData()
        If setDefault Then
            keyTableToDefault(False)
        End If
    End Sub

    Private Function write() As Boolean
        Tracing.TraceLine("KeyCommands write", TraceLevel.Info)
        Dim cfgFile As Stream = Nothing
        Try
            cfgFile = File.Open(KeyConfigType_V1.PathName, FileMode.Create)
        Catch ex As Exception
            Tracing.ErrMessageTrace(ex)
            If cfgFile IsNot Nothing Then
                cfgFile.Dispose()
            End If
            Return False
        End Try
        Dim rv As Boolean
#If 0 Then
        Dim kData As New KeyConfigType_V1(KeyDictionary.Count - 1)
        For i As Integer = 0 To KeyDictionary.Count - 1
            kData.Items(i) = KeyDictionary.Values(i).key
        Next
#Else
        Dim ktbl As keyTbl() = CurrentKeys()
        Dim kData As New KeyConfigType_V1(ktbl.Length - 1)
        For i = 0 To ktbl.Length - 1
            kData.Items(i) = ktbl(i).key
        Next
#End If
        Dim xs As New XmlSerializer(GetType(KeyConfigType_V1))
        Try
            xs.Serialize(cfgFile, kData)
            rv = True
        Catch ex As Exception
            Tracing.ErrMessageTrace(ex, True)
            rv = False
        Finally
            cfgFile.Close()
            cfgFile.Dispose()
        End Try
        Return rv
    End Function

    ''' <summary>
    ''' set/reset the key table to the default values
    ''' </summary>
    ''' <param name="save">true to save in config file</param>
    Friend Sub keyTableToDefault(ByVal save As Boolean)
        Tracing.TraceLine("keyTableToDefault(" & save.ToString & ")", TraceLevel.Info)
        SetValues(defaultKeys, KeyTypes.allKeys, save)
    End Sub

    ''' <summary>
    ''' set key values for the commands
    ''' If CW messages are present, UpdateCWText() must be called after this.
    ''' </summary>
    ''' <param name="defs">a keydefs array</param>
    ''' <param name="mask">of keys to add, see keyTypes</param>
    ''' <param name="wrt">true if config to be written</param>
    Friend Sub SetValues(defs As KeyDefType(), mask As KeyTypes, wrt As Boolean)
        Tracing.TraceLine("SetValues:" & mask.ToString & " " & wrt.ToString, TraceLevel.Info)
        If mask = KeyTypes.allKeys Then
            ' clear everything
            KeyDictionary.Clear()
        Else
            ' Only clear the desired values to be replaced.
            Dim delCol = New List(Of keyTbl)
            For Each item As keyTbl In KeyDictionary.Values
                If (item.KeyType And mask) = item.KeyType Then
                    delCol.Add(item)
                End If
            Next
            For Each item As keyTbl In delCol
                KeyDictionary.Remove(item.key.key)
            Next
        End If
        ' Now add in the keys
        For Each def As KeyDefType In defs
            ' lookup in the keydefDictionary
            Dim t As keyTbl = lookup(def.id)
            If t IsNot Nothing Then
                t.key.key = def.key
                ' Add to the KeyDictionary.
                AddToKeyDictionary(t)
            Else ' Probably an old format KeyDefs file with a depricated command.
            End If
        Next
        If wrt Then
            write()
        End If
    End Sub

    Private Sub murgeNewDefaults()
        ' Build checkDict, a keydef dictionary from keyDictionary.
        Dim checkDict = New Dictionary(Of CommandValues, keyTbl)
        For Each item As keyTbl In KeyDictionary.Values
            checkDict.Add(item.key.id, item)
        Next
        ' Find any key_id from keyDefDictionary that isn't in checkDict.
        Dim needWrite As Boolean = False
        For Each item As keyTbl In KeydefDictionary.Values
            ' Add the key from the corresponding keydef entry if not already in keyDictionary.
            If Not checkDict.ContainsKey(item.key.id) Then
                AddToKeyDictionary(item)
                needWrite = True
                Tracing.TraceLine("KeyCommands:murged new item:" & item.key.id.ToString & " " & item.key.key.ToString, TraceLevel.Info)
            End If
        Next
        If needWrite Then
            write()
        End If
    End Sub

    Shared nameTable As String() = [Enum].GetNames(GetType(CommandValues))
    Shared idTable As Integer() = [Enum].GetValues(GetType(CommandValues))
    ''' <summary>
    ''' Get the command id from the type name
    ''' </summary>
    ''' <param name="name">commandValues name</param>
    ''' <returns>corresponding command value</returns>
    Shared Function getKeyFromTypename(name As String) As CommandValues
        Dim rv As CommandValues = CommandValues.NotACommand
        For i As Integer = 0 To nameTable.Length - 1
            If nameTable(i) = name Then
                rv = idTable(i)
                Exit For
            End If
        Next
        Return rv
    End Function

    ' Keep track of CW messages.
    Private CWMessageDefs As KeyDefType() = Nothing
    ''' <summary>
    ''' (Overloaded) Update the dictionaries with new CWText.
    ''' </summary>
    ''' <remarks>Assumes changes are in CWText.</remarks>
    Friend Sub UpdateCWText()
        Dim item As keyTbl
        If CWMessageDefs IsNot Nothing Then
            ' Remove old dictionary entries.
            For Each def As KeyDefType In CWMessageDefs
                KeydefDictionary.Remove(def.id)
                item = lookup(def.key)
                ' Note:  If the key dups a command key, we'll keep the command.
                If (item IsNot Nothing) AndAlso (item.KeyType = KeyTypes.CWText) Then
                    KeyDictionary.Remove(def.key)
                End If
            Next
        End If
        ' Remake the CWMessageDefs array, and update the dictionaries.
        ReDim CWMessageDefs(CWText.Length - 1)
        For i As Integer = 0 To CWText.Length - 1
            CWMessageDefs(i) = New KeyDefType(CWText(i).key, FirstMessageCommandValue + i)
            item = New keyTbl(CWMessageDefs(i).id, KeyTypes.CWText, AddressOf sendCWMessage, CWText(i).Label, KeyCommands.FunctionGroups.cwMessage)
            item.key.key = CWMessageDefs(i).key
            If Not AddToKeydefDictionary(item) Then
                MsgBox(InternalError & DupValueError & " " & item.key.id.ToString)
            End If
            ' The key might be a dup, but won't be added.
            AddToKeyDictionary(item)
        Next
    End Sub
    ''' <summary>
    ''' (Overloaded) Update the dictionaries with new CWText.
    ''' </summary>
    ''' <param name="items">provides the new keys</param>
    ''' <remarks>
    ''' This first updates the CWText keys.
    ''' Called from DefineKeys
    ''' </remarks>
    Friend Sub UpdateCWText(items As KeyDefType())
        For i As Integer = 0 To items.Length - 1
            CWText(i).key = items(i).key
        Next
        UpdateCWText()
    End Sub

    Friend Sub sendCWMessage()
        Dim id As Integer = CommandId - FirstMessageCommandValue
        If (id < 0) Or (id >= CWText.Length) Then
            MsgBox(InternalError & BadMessageIDError)
        Else
            Dim msg As String = MacroItems.Expand(CWText(id).Message)
            If msg(msg.Length - 1) <> " " Then
                msg &= " "
            End If
            RigControl.SendCW(msg)
            WriteTextX(WindowIDs.SendDataOut, msg, 0, False)
        End If
    End Sub

    ''' <summary>
    ''' perform the command for this key
    ''' </summary>
    ''' <param name="k">the key</param>
    ''' <returns>true if we handled the command</returns>
    ''' <remarks></remarks>
    Friend Function DoCommand(ByVal k As Keys) As Boolean
        Tracing.TraceLine("Docommand:" & CType(k, Integer).ToString("x8"), TraceLevel.Info)
        Dim rv As Boolean = False
        ' Just return if this is just the shift, alt, or control key.
        Dim theKey As Integer = (k And Keys.KeyCode)
        If (theKey = Keys.Menu) Or (theKey = Keys.ControlKey) Or _
           (theKey = Keys.ShiftKey) Then
            Return rv
        End If
        ' Look in KeyDictionary.
        Dim kt As keyTbl = lookup(k)
        If kt IsNot Nothing Then
            ' kt.rtn() can use CommandId to tell what command was entered.
            CommandId = kt.key.id
            Tracing.TraceLine("Docommand:" & CommandId.ToString, TraceLevel.Info)
            Try
                kt.rtn()
                rv = True
            Catch ex As Exception
                If (RigControl Is Nothing) OrElse Not Power Then
                    Tracing.TraceLine("DoCommand:no rig setup", TraceLevel.Error)
                Else
                    Tracing.TraceLine("DoCommand:", TraceLevel.Error)
                    Tracing.ErrMessageTrace(ex)
                End If
            End Try
        Else
            Tracing.TraceLine("DoCommand:key not found:" & k.ToString, TraceLevel.Info)
        End If
        Return rv
    End Function

    Friend Function CurrentKeys() As keyTbl()
        Dim rv = New List(Of keyTbl)
        ' Build return keytbl array with command and log keys, and CW messages,
        ' in that order.
        ' keyTable contains only command and logging keys.
        For Each item As keyTbl In KeyTable
            rv.Add(New keyTbl(item))
        Next
        If CWMessageDefs IsNot Nothing Then
            For Each def As KeyDefType In CWMessageDefs
                Dim item As keyTbl
                Try
                    item = KeydefDictionary(def.id)
                Catch ex As Exception
                    Tracing.ErrMessageTrace(ex)
                    Continue For
                End Try
                rv.Add(item)
            Next
        End If
        Return rv.ToArray
    End Function

    ''' <summary>
    ''' (overloaded) Get the keys, key names and actions for commands in KeyTable plus macros.
    ''' </summary>
    ''' <param name="keyCommandValues">array of KeyDefType containing commands.</param>
    ''' <param name="keyTextValues">array of KeyDefType containing CWText</param>
    ''' <param name="keyNames">array of key value names</param>
    ''' <param name="actions">array of descriptive text</param>
    Friend Sub HelpText(ByRef keyCommandValues As KeyDefType(), _
                        ByRef keyTextValues As KeyDefType(), _
                        ByRef keyNames As String(), _
                        ByRef actions As String())
        Dim len As Integer = KeyDictionary.Count
        Dim commandCol = New List(Of KeyDefType)
        Dim textCol = New List(Of KeyDefType)
        Dim defdict = New Dictionary(Of CommandValues, KeyDefType)
        ReDim keyNames(len - 1), actions(len - 1)
        Dim i As Integer = 0
        keyCommandValues = Nothing
        keyTextValues = Nothing
        ' The command and log keys come first.
        For Each item As keyTbl In KeyDictionary.Values
            If (item.KeyType = KeyTypes.Command) Or (item.KeyType = KeyTypes.log) Then
                ' Careful! KeyDefType is a reference type.
                commandCol.Add(New KeyDefType(item.key.key, item.key.id))
                keyNames(i) = KeyString(item.key.key)
                actions(i) = item.helpText
                ' Note all items with keys.
                defdict.Add(item.key.id, Nothing)
                i += 1
            End If
        Next
        For Each item As keyTbl In KeyDictionary.Values
            If Not ((item.KeyType = KeyTypes.Command) Or (item.KeyType = KeyTypes.log)) Then
                ' CW text
                Dim j As Integer = i - commandCol.Count
                Dim m As CWMessages.MessageItem = CWText(j)
                textCol.Add(New KeyDefType(m.key, CWMessageDefs(j).id))
                keyNames(i) = KeyString(m.key)
                actions(i) = "CW Message: " & m.Label
                i += 1
            End If
        Next
        If commandCol.Count > 0 Then
            keyCommandValues = commandCol.ToArray
        End If
        If textCol.Count > 0 Then
            keyTextValues = textCol.ToArray
        End If
    End Sub

    ''' <summary>
    ''' (overloaded) Get the key names and actions for commands in KeyTable plus macros.
    ''' </summary>
    ''' <param name="keyNames">array of key names</param>
    ''' <param name="actions">array of actions</param>
    Friend Sub HelpText(ByRef keyNames As String(), ByRef actions As String())
        Dim k1 As KeyDefType() = Nothing
        Dim k2 As KeyDefType() = Nothing
        HelpText(k1, k2, keyNames, actions)
    End Sub

    Private Sub displayFreqCmd()
        ' see below.  Called by the Display Frequency command.
        ' Other users should use the displayFreq() routine.
        ' If scanning, pause the scan.
        If (scanstate <> scans.none) AndAlso ScanInProcess Then
            scan.PauseScan()
        End If
        displayFreq()
    End Sub
    Private Sub displayFreq()
        Form1.FreqOut.Focus()
    End Sub

    Private Sub resumeScanCmd()
        ' Resume the scan if scanning and not in-process.
        If (scanstate <> scans.none) AndAlso (Not ScanInProcess) AndAlso (Not RigControl.Transmit) Then
            scan.resumeScan()
        End If
    End Sub

    Private Sub gotoReceive()
        Form1.ReceivedTextBox.Focus()
    End Sub

    Private Sub gotoSend()
        DirectCW = False
        Form1.SentTextBox.Focus()
    End Sub

    Private Sub gotoSendDirect()
        DirectCW = True
        Form1.SentTextBox.Focus()
    End Sub

    Friend Sub WriteFreq(ByVal str As String)
        ' (overloaded) Send a freq to the radio.  The freq must be a string in HZ.
        RigControl.Frequency = CLng(str)
        'displayFreq(False)
    End Sub
    Private Sub WriteFreq()
        ' (overloaded) Send the entered freq. to the radio.
        If RigControl IsNot Nothing Then
            If FreqInput.ShowDialog() = DialogResult.OK Then
                WriteFreq(FreqInput.Buffer)
            End If
        End If
    End Sub

    Private Sub DisplayMemory()
        If RigControl IsNot Nothing Then
            Dim memObj = CType(RigControl.RigFields.Memories, FlexMemories)
            Try
                memObj.ShowDialog()
                If memObj.ShowFreq Then
                    Form1.gotoHome()
                End If
            Catch ex As Exception
                Tracing.TraceLine("memory display:" & ex.Message, TraceLevel.Error)
            End Try
        End If
    End Sub

    Private Sub cycleContinuous()
        MsgBox(NoLongerSupported)
#If 0 Then
        If NotOnFlex() Then
            Return
        End If
        RadioPort.ContinuousOutput = (RadioPort.ContinuousOutput + 1) Mod ComPort.ContinuousValues.numVals
        'WriteText(WindowIDs.StatusOut, RadioPort.ContinuousName, True)
#End If
    End Sub

    Friend Sub DisplayDecodedText(text As String)
        Dim disposition As Integer = 0 ' default is to concatinate text.
        ' See if constraining text.
        If CurrentOp.ConstrainedDecode Then
            disposition = -CurrentOp.CWDecodeCells
        End If
        WriteTextX(WindowIDs.ReceiveDataOut, text, disposition, False)
    End Sub

    Private Sub BringUpLogForm()
        LogEntry.FieldID = CommandIDToADIF(CommandId)
        Dim saveVisible As Boolean = Form1.Visible
        Form1.Visible = False
        LogEntry.ShowDialog()
        Form1.Visible = saveVisible
    End Sub

    ''' <summary>
    ''' Get the ADIF tag for this key command id.
    ''' </summary>
    ''' <param name="id">from CommandValues</param>
    ''' <returns>ADIF string</returns>
    ''' <remarks></remarks>
    Protected Function CommandIDToADIF(id As CommandValues) As String
        Dim rv As String
        Dim tbl As keyTbl = lookup(id)
        If tbl Is Nothing Then
            ShowInternalError(BadCommandID)
            rv = vbNullString
        Else
            rv = tbl.ADIFTag
        End If
        Return rv
    End Function

    Private Sub SetLogDateTime()
        ' Set to the current date/time.
        LogEntry.SetLogDateTime()
    End Sub

    Private Sub FinalizeLog()
        LogEntry.Write()
    End Sub

    ''' <summary>
    ''' Search the log
    ''' </summary>
    ''' <remarks>Brings up FindLogEntry form.</remarks>
    Friend Sub SearchLogCmd()
        Dim thrd As New Thread(AddressOf searchThread)
        thrd.Start()
    End Sub
    Private Sub searchThread()
        FindLogEntry.ShowDialog()
    End Sub

    ''' <summary>
    ''' Set the log file name and other log characteristics for the current operator.
    ''' </summary>
    Sub getLogFileName()
        ' See if there's a record to write, or perhaps abort this.
        If Not LogEntry.optionalWrite Then
            Return
        End If
        ' Make sure we can change log files.
        If Not ContactLog.Cleanup() Then
            Return
        End If
        LogCharacteristics.theOp = CurrentOp
        If LogCharacteristics.ShowDialog <> DialogResult.OK Then
            Return
        End If
        ConfigContactLog()
    End Sub

    Private Sub BeginScan()
        ' A linear scan.
        If (RigControl IsNot Nothing) AndAlso Power AndAlso
           (Not RigControl.Transmit) AndAlso (scanstate <> scans.memory) Then
            If scanstate = scans.linear Then
                stopTheScan()
            Else
                ' Get scan parameters and perhaps start the scan.
                scan.ShowDialog()
            End If
        End If
    End Sub

    Private Sub memScan()
        Try
            If (Not RigControl.Transmit) AndAlso (scanstate <> scans.linear) Then
                ' Memory scan
                If (scanstate = scans.memory) Then
                    stopTheScan()
                Else
                    If MemoryGroupControl Is Nothing Then
                        MemoryGroupControl = New MemoryGroup
                    End If
                    ' Get scan parameters and possibly start the scan.
                    MemoryScan.ShowDialog()
                End If
            End If
        Catch ex As Exception
            Tracing.TraceLine("memScan exception:" & ex.Message, TraceLevel.Error)
        End Try
    End Sub

    Private Sub useSaved()
        If (RigControl IsNot Nothing) AndAlso Power Then
            If SelectScan.ShowDialog <> DialogResult.OK Then
                Return
            End If
            Dim sd As SavedScanData.ScanData = SavedScans.Item(SelectScan.ItemIndex)
            ' A scan is selected.
            Select Case sd.Type
#If 0 Then
                Case SavedScanData.ScanTypes.memory
                    MemoryScan.doPreset(sd, True)
#End If
                Case SavedScanData.ScanTypes.linear
                    scan.doPreset(sd, True)
            End Select
        End If
    End Sub

    Private Sub stopTheScan()
        If (RigControl IsNot Nothing) AndAlso Power Then
            scan.StopScan()
        End If
    End Sub

    ''' <summary>
    ''' Show the rig's menus
    ''' </summary>
    ''' <remarks></remarks>
    Friend Sub ShowMenus()
        MsgBox(NotSupportedForThisRig)
        Return
#If 0 Then
        If (RigControl IsNot Nothing) AndAlso RigControl.IsOpen Then
            If (RigControl.RigFields IsNot Nothing) AndAlso
               (RigControl.RigFields.Menus IsNot Nothing) Then
                RigControl.RigFields.Menus.ShowDialog()
            Else
                Menus.ShowDialog()
            End If
        End If
#End If
    End Sub

    Private Sub stopCode()
        If (RigControl IsNot Nothing) Then
            RigControl.StopCW()
        End If
    End Sub

    ' Also run from the actions menu item.
    Friend Sub startPanning()
        If CurrentOp.BrailleDisplaySize = 0 Then
            MsgBox(RequiresBrailleDisplay)
            Return
        End If
        If (RigControl IsNot Nothing) AndAlso RigControl.myCaps.HasCap(Radios.RigCaps.Caps.Pan) Then
            If OpenParms.PanField IsNot Nothing Then
                OpenParms.PanField.Focus()
            End If
        End If
    End Sub

    ' Audio gain is RF gain for non-flex rigs.
    Private Sub audioGainUp()
        If RigControl IsNot Nothing Then
            RigControl.AudioGain += 5
        End If
    End Sub

    Private Sub audioGainDown()
        If RigControl IsNot Nothing Then
            RigControl.AudioGain -= 5
        End If
    End Sub

    ' This is not used for non-flex rigs.
    Private Sub headphonesUp()
        If RigControl IsNot Nothing Then
            RigControl.HeadphoneGain += 5
        End If
    End Sub

    Private Sub headphonesDown()
        If RigControl IsNot Nothing Then
            RigControl.HeadphoneGain -= 5
        End If
    End Sub

    ' Lineout is audio gain for non-flex rigs.
    Private Sub lineoutUp()
        If RigControl IsNot Nothing Then
            If RigControl.PCAudio Then
                Return
            End If
            RigControl.LineoutGain += 5
        End If
    End Sub
    Private Sub lineoutDown()
        If RigControl IsNot Nothing Then
            If RigControl.PCAudio Then
                Return
            End If
            RigControl.LineoutGain -= 5
        End If
    End Sub

    Private Sub clearRitRtn()
        Dim r As FlexBase.RITData
        If RigControl IsNot Nothing Then
            r = RigControl.RIT
            r.Value = 0
            RigControl.RIT = r
        End If
    End Sub

    Private Sub zerobeatRtn()
        If RigControl IsNot Nothing Then
            RigControl.CWZeroBeat()
        End If
    End Sub

    Private Sub reverseBeaconCmd()
        ReverseBeacon.ShowDialog()
    End Sub

    Friend Sub toggle1()
        If (OpenParms IsNot Nothing) AndAlso (OpenParms.NextValue1 IsNot Nothing) AndAlso
           Power Then
            OpenParms.NextValue1()
        End If
    End Sub

    Private Sub logStatsRTN()
        Dim obj As New LogStats()
        obj.ShowLogStats()
    End Sub

    ' Region - remote audio
#Region "remote audio"
    Private Sub PCAudioRtn()
        PCAudio = Not PCAudio
        ' need to update the op menu
        Form1.SetupOperationsMenu()
    End Sub

    ''' <summary>
    ''' return the PC audio string for the Operations menu containing the action to perform
    ''' </summary>
    ''' <returns>String</returns>
    Private Function audioMenuString() As String
        Dim str As String
        If PCAudio Then
            str = "off"
        Else
            str = "on"
        End If
        Return "PC audio " & str
    End Function

    Private Sub audioSetupRtn()
        GetNewAudioDevices()
    End Sub
#End Region

    ' Region for clusters
#Region "clusters"
    Private Const loginNameTitle As String = "Specify login name"
    Private Const mustHaveClusterInfo As String = "The cluster hostname and login id must be specified."
    Private WithEvents clusterScreen As ClusterForm
    Friend ClusterScreens As List(Of ClusterForm) = New List(Of ClusterForm)
    Private Sub arClusterCmd()
        Dim msg = New OptionalMessageElement("", "clusterLogin")
        msg.Title = loginNameTitle
        msg.Control = New LoginName(msg)
        OptionalMessage.Show(msg)
        If msg.Result = DialogResult.OK Then
            Dim beepSetting As ClusterForm.BeepType
            If CurrentOp.ClusterBeep Then
                beepSetting = ClusterForm.BeepType.On
            Else
                beepSetting = CurrentOp.ClusterBeepSetting
            End If
            Dim host As String = vbNullString
            Dim loginName As String = vbNullString
            If msg.ApplicationData Is Nothing Then
                ' Use operator's values.
                host = CurrentOp.ClusterHostname
                loginName = CurrentOp.ClusterLoginName
            Else
                host = msg.ApplicationData(0)
                loginName = msg.ApplicationData(1)
            End If
            If (host = vbNullString) Or (loginName = vbNullString) Then
                MsgBox(mustHaveClusterInfo)
                Return
            End If
            clusterScreen = New ClusterForm(host, loginName, beepSetting, CurrentOp.ClusterTrackPosition)
            AddHandler clusterScreen.ClusterLoginEvent, AddressOf clusterLoginHandler
            AddHandler clusterScreen.BeepChangeEvent, AddressOf BeepChangeHandler
            AddHandler clusterScreen.TrackChangeEvent, AddressOf trackChangeHandler
            AddHandler clusterScreen.SpotInfoEvent, AddressOf spotInfoHandler
            ClusterScreens.Add(clusterScreen)
            clusterScreen.Login()
            clusterScreen.Show()
            Form1.BringToFront()
        End If
    End Sub

    Private Sub clusterLoginHandler(e As ClusterForm.ClusterLoginArg)
        If e.Error Then
            MsgBox(e.Message)
            TextOut.PerformGenericFunction(clusterScreen, _
                                           Sub()
                                               clusterScreen.Close()
                                           End Sub)
        Else
            Console.Beep()
        End If
    End Sub

    ''' <summary>
    ''' Cluster must shuttdown.
    ''' </summary>
    ''' <remarks>
    ''' Only called to stop any login in process.
    ''' </remarks>
    Friend Sub ClusterShutdown()
        If (ClusterScreens Is Nothing) OrElse (ClusterScreens.Count = 0) Then
            Return
        End If
        Tracing.TraceLine("ClusterShutdown", TraceLevel.Info)
        For i As Integer = 0 To ClusterScreens.Count - 1
            Dim cluster As ClusterForm = ClusterScreens(i)
            Try
                If cluster IsNot Nothing Then
                    cluster.LoginCancel()
                    cluster.Close()
                    cluster.Dispose()
                End If
            Catch ex As Exception
                Tracing.TraceLine("ClusterShutdown:" & ex.Message, TraceLevel.Error)
            End Try
        Next
        ClusterScreens.Clear()
    End Sub

    ''' <summary>
    ''' Beep on/off button change
    ''' </summary>
    ''' <param name="e">BeepChangeArg</param>
    Private Sub BeepChangeHandler(e As ClusterForm.BeepChangeArg)
        CurrentOp.ClusterBeepSetting = e.BeepSetting
        CurrentOp.ClusterBeep = False
        Operators.UpdateCurrentOp()
    End Sub

    ''' <summary>
    ''' Track last entry change
    ''' </summary>
    ''' <param name="e">TrackChangeArg</param>
    Private Sub trackChangeHandler(e As ClusterForm.TrackChangeArg)
        CurrentOp.ClusterTrackPosition = e.TrackOn
        Operators.UpdateCurrentOp()
    End Sub

    Private Sub spotInfoHandler(e As ClusterForm.SpotInfoArg)
        If (RigControl Is Nothing) OrElse Not Power Then
            Return
        End If
        ' First try khz.
        Dim freq As ULong = CType(e.Frequency * 1000, ULong)
        Dim go As Boolean = (Bands.Query(freq) IsNot Nothing)
        If Not go Then
            ' Try mhz
            freq = CType(e.Frequency * 1000000, ULong)
            go = (Bands.Query(freq) IsNot Nothing)
        End If
        If go Then
            Clipboard.Clear()
            Clipboard.SetText(e.Callsign)
            RigControl.Frequency = freq
            Form1.BringToFront()
            displayFreq()
        End If
    End Sub
#End Region

    Private Sub stationLookupRtn()
        If LookupStation Is Nothing Then

            LookupStation = New StationLookup()
        End If
        LookupStation.ShowDialog()
    End Sub

    Private Sub gatherDebugRtn()
        DebugInfo.GetDebugInfo()
    End Sub

    Private Sub ATUMemoriesRtn()
        Try
            If RigControl.myCaps.HasCap(RigCaps.Caps.ATMems) Then
                RigControl.AntennaTunerMemories()
            Else
                MsgBox(NotSupportedForThisRig)
            End If
        Catch
            ' Ignore any exception.
        End Try
    End Sub

    Private Sub rebootRtn()
        If RigControl Is Nothing Then
            Return
        End If

        If MessageBox.Show(msgReboot, RebootHdr, MessageBoxButtons.YesNo) = DialogResult.Yes Then
            Form1.powerNowOff()
            Dim rebootThread As Thread = New Thread(AddressOf rebootThreadProc)
            rebootThread.Name = "reboot"
            rebootThread.Start()
            rebootThread.Join()
        End If
    End Sub
    Private Sub rebootThreadProc()
        ' Don't disconnect if remote.
        RigControl.Reboot(Not RigControl.RemoteRig)
    End Sub

    Private Sub TXControlsRtn()
        If RigControl Is Nothing Then
            Return
        End If

        Dim theForm As TXControls
        theForm = New TXControls(RigControl)
        theForm.ShowDialog()
        theForm.Dispose()
        displayFreqCmd()
    End Sub

    Private Sub smeterDisplayRTN()
        If RigControl Is Nothing Then
            Return
        End If

        RigControl.SmeterInDBM = Not RigControl.SmeterInDBM
        ' need to update the op menu
        Form1.SetupOperationsMenu()
    End Sub
    Private Function sMeterMenuString() As String
        If RigControl Is Nothing Then
            Return "SMeter display "
        End If
        Dim txt As String = "SMeter display in "
        If RigControl.SmeterInDBM Then
            txt = txt & "s-units"
        Else
            txt = txt & "dbm"
        End If
        Return txt
    End Function
End Class
