Imports System.Collections
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
'Imports System.Collections.Specialized
'Imports System.Configuration
Imports System.Diagnostics
Imports System.IO
Imports System.IO.Compression
Imports System.Globalization
Imports System.IO.Ports
Imports System.Net
Imports System.Reflection
Imports System.Threading
Imports System.Xml.Serialization
Imports adif
Imports JJCountriesDB
Imports JJFlexControl
Imports JJLogLib
Imports JJPortaudio
Imports JJTrace
Imports JJW2WattMeter
Imports MsgLib
Imports System.Linq
Imports Radios

Module globals
    Public Const CopyRight As String = "Copyright 2013 by J.J. Shaffer"

    ''' <summary>
    ''' UI mode: Classic preserves legacy menus, Modern provides reorganized slice-centric menus.
    ''' Logging mode is reserved for a future sprint.
    ''' Persisted as an integer in operator XML so adding values never breaks existing files.
    ''' </summary>
    Public Enum UIMode
        Classic = 0
        Modern = 1
        Logging = 2   ' Reserved — falls back to Classic until the Logging sprint ships.
    End Enum

    ''' <summary>
    ''' Session-only flag — True while in Logging Mode overlay.
    ''' Never touches UIModeSetting so the persisted Classic/Modern choice is preserved.
    ''' </summary>
    Private _isInLoggingMode As Boolean = False

    ''' <summary>
    ''' The active UI mode for the current operator.
    ''' Logging is a session-only overlay that never writes to the operator config.
    ''' Classic/Modern persist normally.
    ''' </summary>
    Friend Property ActiveUIMode As UIMode
        Get
            If _isInLoggingMode Then Return UIMode.Logging
            If CurrentOp Is Nothing Then Return UIMode.Classic
            Return CurrentOp.CurrentUIMode
        End Get
        Set(value As UIMode)
            If value = UIMode.Logging Then
                ' Logging is session-only — just set the flag, don't touch persisted settings.
                _isInLoggingMode = True
                Return
            End If
            ' Leaving Logging (or switching Classic/Modern) — clear the flag and persist.
            _isInLoggingMode = False
            If CurrentOp Is Nothing Then Return
            CurrentOp.UIModeSetting = CInt(value)
            Operators.UpdateCurrentOp()
        End Set
    End Property

    ''' <summary>
    ''' Remembers Classic or Modern so toggling out of Logging returns to the right mode.
    ''' Not persisted — resets to the operator's saved mode on startup.
    ''' </summary>
    Friend LastNonLogMode As UIMode = UIMode.Classic
    Friend Const ErrorHdr As String = "Error"
    Friend Const MessageHdr As String = "Message"
    Friend Const ExceptionHdr As String = "Exception"
    Friend Const OnWord As String = "On"
    Friend Const OffWord As String = "Off"
    Friend Const LockedWord As String = "Locked"
    Friend Const NoneWord As String = "none"
    Friend Const Loaded As String = "loaded"
    Friend Const Loading As String = "loading"
    Friend Const Running As String = "running"
    Friend Const Paused As String = "paused"
    Friend Const NoRig As String = "No rig is connected."
    Friend Const RebootHdr As String = "Reboot"
    Friend Const msgReboot As String = "Reboot the radio?"
    Friend Const Rebooting As String = "Rebooting ..."
    Friend Const mustHaveLog As String = "A log file must be defined."
    Friend Const NotSupportedForThisRig As String = "This function is not supported on this radio."
    Friend Const NotSupportedForThisInstance As String = "This function is not supported in this situation."
    Friend Const NotSupportedForRemoteRig As String = "This function is not supported on a remote radio."
    Friend Const NoLongerSupported As String = "This function is no longer supported."
    Friend Const RequiresBrailleDisplay As String = "This function requires a braille display."
    Friend Const NotValidHost As String = "Hostname must be host or host:port."
    Friend Const NoAudioDevice As String = "No output audio device is configured."

#If 0 Then
    Friend AppSettings As AppSettingsSection
    Friend Function GetConfigValue(key As String) As String
        Dim rv As String
        If (AppSettings IsNot Nothing) AndAlso (AppSettings.Settings(key) IsNot Nothing) Then
            rv = AppSettings.Settings(key).Value
        Else
            rv = vbNullString
        End If
        Return rv
    End Function
#End If

    Friend ProgramInstance As Integer = 0
    Friend BootTrace As Boolean
    Friend ProgramDirectory As String ' This program's directory.
    Friend Commands As KeyCommands
    Friend ContactLog As LogClass
    Friend LookupStation As JJFlexWpf.StationLookupWindow = Nothing

    Friend Dups As LogDupChecking
    ''' <summary>
    ''' dup checking type
    ''' </summary>
    Friend ReadOnly Property DupType As LogDupChecking.DupTypes
        Get
            If Dups Is Nothing Then
                Return LogDupChecking.DupTypes.none
            Else
                Return Dups.dupType
            End If
        End Get
    End Property
    ''' <summary>
    ''' True if dup checking
    ''' </summary>
    Friend ReadOnly Property isDupChecking As Boolean
        Get
            Return DupType <> LogDupChecking.DupTypes.none
        End Get
    End Property

    Friend FindDialog As Boolean = False

    Friend DirectCW As Boolean = False
    Friend CWText As CWMessages
    Enum WindowIDs
        ReceiveDataOut
        SendDataOut
    End Enum
    Delegate Sub WrtTxt(ByVal TextboxID As WindowIDs, ByVal text As String, ByVal clearFlag As Boolean)
    Friend WriteText As WrtTxt
    Delegate Sub WrtTxtX(ByVal tbid As WindowIDs, ByVal s As String, _
                         ByVal cur As Integer, ByVal c As Boolean)
    Friend WriteTextX As WrtTxtX
    Delegate Sub tbrtn(ByVal tbid As WindowIDs)
    ''' <summary>
    ''' True if ending the program.
    ''' Access with volatile read and write.
    ''' </summary>
    Friend Ending As Boolean = False

    ''' <summary>
    ''' SMeter raw and calibrated values.
    ''' </summary>
    Friend SMeter As Levels

    ' region - Config data stuff.
#Region "config"
    Friend myAssembly As Assembly
    Friend myAssemblyName As AssemblyName
    Friend myVersion As Version
    ''' <summary>
    ''' configuration event types
    ''' </summary>
    Friend Enum ConfigEvents
        OperatorChanged
        RigChanged
    End Enum
    ''' <summary>
    ''' type of the config event argument.
    ''' </summary>
    Friend Class ConfigArgs
        Inherits EventArgs
        Public TheEvent As ConfigEvents
        Public TheData As Object
        ''' <summary>
        ''' define a config event
        ''' </summary>
        ''' <param name="e">the event from ConfigEvents</param>
        ''' <param name="d">Event dependent data</param>
        Public Sub New(ByVal e As ConfigEvents, ByVal d As Object)
            TheEvent = e
            TheData = d
        End Sub
    End Class

    Friend Const ProgramName = "JJFlexRadio"
    Friend Const DocName As String = ProgramName & "Readme.htm"
    Const reqOpMsgTitle As String = "You must define a default operator."
    Const reqOpMsg As String = _
        "If you do not define a default operator, the program will exit." & vbCrLf & _
        "Do you wish to define one?"
    Const noDefaultRigTitle As String = "No default rig"
    Const noDefaultRig As String = _
        "You must define a default rig." & vbCrLf & _
        "Do you wish to define one?"
    Friend MenusLoaded As Boolean
    Friend MemoriesLoaded As Boolean
    Friend BaseConfigDir As String
    Friend ReadOnly Property BootTraceFileName As String
        Get
            Dim rv As String = BaseConfigDir & "\" & ProgramName
            If ProgramInstance > 1 Then
                rv &= ProgramInstance.ToString
            End If
            Return rv & "Trace.txt"
        End Get
    End Property
    Friend ReadOnly Property OldTraceFileName As String
        Get
            Dim rv As String = BaseConfigDir & "\" & ProgramName
            If ProgramInstance > 1 Then
                rv &= ProgramInstance.ToString
            End If
            Return rv & "TraceOld.txt"
        End Get
    End Property
    Friend ReadOnly Property TraceArchiveDir As String
        Get
            Return BaseConfigDir & "\Traces"
        End Get
    End Property
    Friend ReadOnly Property DailyTraceFilePrefix As String
        Get
            Return ProgramName & "Trace"
        End Get
    End Property

    Private Sub RotateBootTraceIfNeeded()
        Dim tracePath As String = BootTraceFileName
        If Not File.Exists(tracePath) Then
            Return
        End If

        Try
            If File.Exists(OldTraceFileName) Then
                File.Delete(OldTraceFileName)
            End If
            File.Move(tracePath, OldTraceFileName)
        Catch ex As Exception
            Tracing.ErrMessageTrace(ex)
        End Try
    End Sub

    Private Sub ArchiveTraceFile(tracePath As String, traceDate As Date)
        Try
            Dim yearDir As String = Path.Combine(TraceArchiveDir, traceDate.ToString("yyyy"))
            Dim monthDir As String = Path.Combine(yearDir, traceDate.ToString("MM"))
            Directory.CreateDirectory(monthDir)
            Dim zipName As String = Path.Combine(monthDir, $"trace{traceDate:MMddyyyy}.zip")
            If File.Exists(zipName) Then
                File.Delete(zipName)
            End If
            Using archive As ZipArchive = ZipFile.Open(zipName, ZipArchiveMode.Create)
                ZipUtils.AddFileToArchive(archive, tracePath, "")
            End Using
        Catch ex As Exception
            Tracing.ErrMessageTrace(ex)
        End Try
    End Sub

    Private Sub ArchiveOldDailyTraces()
        If CurrentOp Is Nothing OrElse Not CurrentOp.KeepDailyTraceLogs Then Return
        Try
            If Not Directory.Exists(BaseConfigDir) Then Return
            Dim today As Date = Date.Now.Date
            For Each tracePath As String In Directory.GetFiles(BaseConfigDir, DailyTraceFilePrefix & "*.txt")
                If tracePath.IndexOf("BootTrace", StringComparison.OrdinalIgnoreCase) >= 0 Then Continue For
                Dim name As String = Path.GetFileNameWithoutExtension(tracePath)
                Dim stampPart As String = name.Substring(DailyTraceFilePrefix.Length)
                Dim stamp As DateTime
                If Not DateTime.TryParseExact(stampPart, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, stamp) Then
                    Continue For
                End If
                If stamp.Date < today Then
                    ArchiveTraceFile(tracePath, stamp.Date)
                    Try
                        File.Delete(tracePath)
                    Catch ex As Exception
                        Tracing.ErrMessageTrace(ex)
                    End Try
                End If
            Next
        Catch ex As Exception
            Tracing.ErrMessageTrace(ex)
        End Try
    End Sub

    Friend Sub StartDailyTraceIfEnabled()
        If CurrentOp Is Nothing OrElse Not CurrentOp.KeepDailyTraceLogs Then Return
        ArchiveOldDailyTraces()
        Try
            Dim tracePath As String = Path.Combine(BaseConfigDir, $"{DailyTraceFilePrefix}{Date.Now:yyyyMMddHHmmss}.txt")
            Tracing.TheSwitch.Level = TraceLevel.Info
            Tracing.TraceFile = tracePath
            Tracing.On = True
            LastUserTraceFile = tracePath
            Tracing.TraceLine($"Daily tracing on {Date.Now:O} level={Tracing.TheSwitch.Level}")
        Catch ex As Exception
            Tracing.ErrMessageTrace(ex)
        End Try
    End Sub

    Friend Power As Boolean = False
    Friend LastUserTraceFile As String ' Last user-started trace file (see DebugInfo)
    Friend WithEvents Operators As PersonalData = Nothing
    Friend WithEvents Knob As FlexKnob = Nothing
    ''' <summary>
    ''' (ReadOnly) the current operator
    ''' </summary>
    Friend ReadOnly Property CurrentOp As PersonalData.personal_v1
        Get
            Return Operators.CurrentItem
        End Get
    End Property
    ''' <summary>
    ''' ID of the current operator
    ''' </summary>
    Friend Property CurrentOpID As Integer
        Get
            Return Operators.CurrentID
        End Get
        Set(ByVal value As Integer)
            Operators.CurrentID = value
        End Set
    End Property
    Friend CurrentRig As FlexBase.RigData
    Friend CurrentRigID As Integer
    Friend WithEvents RigControl As FlexBase
    ''' <summary>
    ''' Current rig's open parameters.
    ''' </summary>
    Friend OpenParms As FlexBase.OpenParms
    Friend StationName As String

    ''' <summary>
    ''' Convenience accessor for the WPF MainWindow UserControl.
    ''' Used by VB.NET code that previously referenced Form1 directly.
    ''' Returns the WPF MainWindow instance from ApplicationEvents.
    ''' </summary>
    Friend ReadOnly Property WpfMainWindow As JJFlexWpf.MainWindow
        Get
            Return My.MyApplication.WpfMainWindow
        End Get
    End Property

    ''' <summary>
    ''' Convenience accessor for the ShellForm (WinForms host).
    ''' Used for window-level operations (Title, Activate, etc.).
    ''' </summary>
    Friend ReadOnly Property AppShellForm As ShellForm
        Get
            Return My.MyApplication.TheShellForm
        End Get
    End Property

    Friend Const HamqthLookupID As String = "JJRadio"
    Friend Const HamqthLookupPassword As String = "JJRadio"

    ''' <summary>
    ''' Create a new StationLookupWindow, passing current operator callbook settings.
    ''' WPF windows can't be reshown after closing, so create a new one each time.
    ''' </summary>
    Friend Function CreateStationLookupWindow() As JJFlexWpf.StationLookupWindow
        Dim source As String = ""
        Dim username As String = ""
        Dim password As String = ""
        Dim opCall As String = ""
        Dim opGrid As String = ""

        If CurrentOp IsNot Nothing Then
            source = If(CurrentOp.CallbookLookupSource, "")
            username = If(CurrentOp.CallbookUsername, "")
            password = If(CurrentOp.DecryptedCallbookPassword, "")
            opCall = If(CurrentOp.callSign, "")
            opGrid = If(CurrentOp.GridSquare, "")
        End If

        Return New JJFlexWpf.StationLookupWindow(source, username, password, opCall, opGrid)
    End Function

    ''' <summary>
    ''' W2 wattmeter.
    ''' </summary>
    Friend W2WattMeter As W2
    Friend W2ConfigFile As String
    Private W2ConfigFileBasename As String = "w2.xml"

    Friend Sub GetConfigInfo()
        myAssembly = Assembly.GetEntryAssembly
        myAssemblyName = myAssembly.GetName
        myVersion = myAssemblyName.Version
        BaseConfigDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) &
            "\" & ProgramName
        Try
            If Not Directory.Exists(BaseConfigDir) Then
                ' show welcome screen.
                Dim rslt As DialogResult
                Do
                    rslt = Welcome.ShowDialog
                    If rslt = DialogResult.Abort Then
                        End
                    End If
                Loop While rslt = DialogResult.Cancel
                Directory.CreateDirectory(BaseConfigDir)
            End If
        Catch ex As Exception
            Tracing.ErrMessageTrace(ex)
            Exit Sub
        End Try

        ProgramInstance = Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName).Length
        BootTrace = (Not Debugger.IsAttached)
        'BootTrace = True
        If BootTrace Then
            RotateBootTraceIfNeeded()
            Dim bootLevel As TraceLevel = TraceLevel.Info
            Tracing.TheSwitch.Level = bootLevel
            Tracing.TraceFile = BootTraceFileName
            Tracing.On = True
            Tracing.TraceLine("Boot Tracing on instance:" & ProgramInstance & " " & myAssembly.Location & " " & myVersion.ToString() & " " & Date.Now & " level=" & bootLevel.ToString)
        End If

        Tracing.TraceLine("GetConfigInfo:" & BaseConfigDir, TraceLevel.Info)
        ' Audio device selection file name.
        AudioDevicesFile = BaseConfigDir & "\" & audioDevicesBasename

        ' Load keyboard command config data.
        Commands = New KeyCommands

        ' Load operator and rig data.
        Operators = New PersonalData(BaseConfigDir)
        ' There must be a default operator!
        While CurrentOp Is Nothing
            SetCurrentOp(Operators.TheDefault, Operators.DefaultID)
            If CurrentOp Is Nothing Then
                If MessageBox.Show(AppShellForm, reqOpMsg, reqOpMsgTitle, MessageBoxButtons.YesNo) <> DialogResult.Yes Then
                    End
                End If
                Lister.TheList = Operators
                Lister.ShowDialog()
            End If
        End While

        ' setup log file
        ConfigContactLog()

        ' Now that the operator is known, rebuild the operations menu (for trace toggle).
        ' Sprint 10: Route to WPF MainWindow instead of Form1.
        If WpfMainWindow IsNot Nothing Then WpfMainWindow.SetupOperationsMenu()

        ' Check for W2 watt meter.
        W2ConfigFile = BaseConfigDir & "\" & W2ConfigFileBasename
        ConfigW2(True) ' only setup if already configured.
    End Sub

    Friend Sub SetCurrentOp(ByVal op As PersonalData.personal_v1,
                            ByVal id As Integer)
        If op IsNot Nothing Then
            Tracing.TraceLine("SetCurrentOp(" & op.fullName & "," & id.ToString & ")", TraceLevel.Info)
        Else
            Tracing.TraceLine("SetCurrentOp:no operator", TraceLevel.Error)
            id = -1
        End If
        CurrentOpID = id
        If id = -1 Then
            Return
        End If

        ' Initialize the optional message processor
        OptionalMessage.Setup(AddressOf Operators.UpdateOptionalMessages, AddressOf Operators.RetrieveOptionalMessages)
        ' Update the key dictionaries.
        Commands.UpdateCWText()
        ' Setup macros
        MacroItems.Items(MacroItems.MacroIDS.myCallSign).Acquire =
            Function()
                Return op.callSign
            End Function
        MacroItems.Items(MacroItems.MacroIDS.myName).Acquire =
            Function()
                Return op.handl
            End Function
        MacroItems.Items(MacroItems.MacroIDS.myQTH).Acquire =
            Function()
                Return op.qth
            End Function

        ' Get the default profile for legacy users.
        If CurrentOp.Profiles Is Nothing Then
            CurrentOp.Profiles = New List(Of Profile_t)
        End If
        If (CurrentOp.Profiles.Count = 0) Then
            ' no profile defined with the user's data.
            ' Old default was a global and tx profile.
            CurrentOp.Profiles.Add(New Profile_t(Profile_t.GenerateProfileName(CurrentOp.callSign), ProfileTypes.global, True))
            CurrentOp.Profiles.Add(New Profile_t("Default", ProfileTypes.tx, True))
        End If
    End Sub

    Friend Sub ConfigContactLog()
#If 0 Then
        Logs.NewLog(CurrentOp.HamqthID, CurrentOp.HamqthPassword)
#End If
        Logs.NewLog(HamqthLookupID, HamqthLookupPassword)
        ContactLog = New LogClass(CurrentOp.LogFile)
        logSetup()
        ' Setup macros
        MacroItems.Items(MacroItems.MacroIDS.callSign).Acquire =
            Function()
                Return LogEntry.getFieldTextValue(AdifTags.ADIF_Call)
            End Function
        MacroItems.Items(MacroItems.MacroIDS.name).Acquire =
            Function()
                Return LogEntry.getFieldTextValue(AdifTags.ADIF_Name)
            End Function
        MacroItems.Items(MacroItems.MacroIDS.QTH).Acquire =
            Function()
                Return LogEntry.getFieldTextValue(AdifTags.ADIF_QTH)
            End Function
        MacroItems.Items(MacroItems.MacroIDS.myRST).Acquire =
            Function()
                Return LogEntry.getFieldTextValue(AdifTags.ADIF_MyRST)
            End Function
        MacroItems.Items(MacroItems.MacroIDS.RST).Acquire =
            Function()
                Return LogEntry.getFieldTextValue(AdifTags.ADIF_HisRST)
            End Function
        MacroItems.Items(MacroItems.MacroIDS.mySerial).Acquire =
            Function()
                Return LogEntry.getFieldTextValue(AdifTags.ADIF_SentSerial)
            End Function
    End Sub

    ''' <summary>
    ''' start dup checking
    ''' </summary>
    Private Sub logSetup()
        ' Can't do this if there's no log.
        ' Sprint 10: Route to WPF StatusBar via StatusBox adapter.
        StatusBox?.Write("LogFile", " ")
        If (ContactLog.Name = vbNullString) Or (Not File.Exists(ContactLog.Name)) Then
            Return
        End If
        Dim session = New LogSession(ContactLog)
        If Not session.Start() Then
            Tracing.TraceLine("startDupChecking couldn't start session", TraceLevel.Error)
            Return
        End If
        StatusBox?.Write("LogFile", LogCharacteristics.TrimmedFilename(ContactLog.Name, 20))
        ' Set the keys from the log form.
        Dim defs = New Collection(Of KeyCommands.KeyDefType)
        For Each fld As LogField In session.FormData.Fields.Values
            If fld.KeyName <> vbNullString Then
                ' First use the name to get the id.
                fld.KeyID = KeyCommands.getKeyFromTypename(fld.KeyName)
                ' Get the entry to set in my keyTable.
                Dim t As KeyCommands.keyTbl = Commands.lookup(CType(fld.KeyID, KeyCommands.CommandValues))
                If (t IsNot Nothing) Then
                    defs.Add(t.key)
                End If
            End If
        Next
        ' Add any keys for use when logging.
        For Each ktbl As KeyCommands.keyTbl In Commands.KeyTable
            If ktbl.UseWhenLogging Then
                defs.Add(ktbl.key)
            End If
        Next
        Commands.SetValues(defs.ToArray, KeyCommands.KeyTypes.log, False)

        ' Setup dup checking and other log calculations and fixup.
        Dim dupCheck As LogDupChecking.DupTypes
        dupCheck = CType(CInt(session.GetHeaderFieldText(AdifTags.HDR_DupCheck)), LogDupChecking.DupTypes)
        Dups = Nothing
        Tracing.TraceLine("startDupChecking:" & dupCheck.ToString, TraceLevel.Info)
        If dupCheck <> LogDupChecking.DupTypes.none Then
            Dups = New LogDupChecking(dupCheck)
        End If

        Dim countriesdb As CountriesDB = Nothing
        ' For each log record...
        While (Not session.EOF) AndAlso session.NextRecord()
            Dim needUpdate As Boolean = False ' set if need to update the record.
            If session.NeedFrequencyFix Then
                ' Fix bogus frequencies.
                Dim item As LogFieldElement
                item = session.getField(AdifTags.ADIF_RXFreq, False, session.FieldDictionary)
                If (item IsNot Nothing) AndAlso (item.Data <> vbNullString) Then
                    item.Data = fixFreq(item.Data)
                End If
                item = session.getField(AdifTags.ADIF_TXFreq, False, session.FieldDictionary)
                If (item IsNot Nothing) AndAlso (item.Data <> vbNullString) Then
                    item.Data = fixFreq(item.Data)
                End If
                needUpdate = True
            End If

            ' maintain dup checking
            If dupCheck <> LogDupChecking.DupTypes.none Then
                Dim key As New LogDupChecking.keyElement(session, DupType)
                Dups.AddToDictionary(key)
            End If

            ' See if need to update the DXCC info.
            If session.FormData.NeedCountryInfo Then
                Dim callItem As LogFieldElement = session.getField(AdifTags.ADIF_Call, False, session.FieldDictionary)
                If (callItem IsNot Nothing) AndAlso (callItem.Data <> vbNullString) Then
                    Dim dxccItem As LogFieldElement = session.getField(AdifTags.ADIF_DXCC, False, session.FieldDictionary)
                    If (dxccItem IsNot Nothing) AndAlso (dxccItem.Data = vbNullString) Then
                        ' no DXCC info.
                        If countriesdb Is Nothing Then
                            countriesdb = New CountriesDB
                        End If
                        Dim rec As Record = countriesdb.LookupByCall(callItem.Data)
                        If rec IsNot Nothing Then
                            dxccItem.Data = rec.CountryID
                            needUpdate = True
                        End If
                    End If
                End If
            End If

            ' Perform any housekeeping such as score calculation.
            If session.FormData.WriteEntry IsNot Nothing Then
                session.FormData.WriteEntry(session.FieldDictionary, Nothing)
            End If

            If needUpdate Then
                session.Update()
            End If
        End While

        session.EndSession()
    End Sub

    ''' <summary>
    ''' Get the station name.
    ''' </summary>
    Friend Function getStationName() As String
        Dim rv As String = CurrentOp.callSign
        If rv = vbNullString Then
            rv = Dns.GetHostName()
        End If
        If rv = vbNullString Then
            rv = "unknown"
        End If
        Return rv
    End Function

    ' FlexControl knob stuff
    Private knobThread As Thread = Nothing
    Friend Sub SetupKnob()
        knobThread = New Thread(AddressOf knobThreadProc)
        knobThread.Name = "knob thread"
        knobThread.Start()
    End Sub

    Friend Sub StopKnob()
        If knobThread IsNot Nothing Then
            knobThread.Interrupt()
            Try
                If knobThread.IsAlive Then
                    knobThread.Join()
                End If
            Catch ex As Exception
                ' ignore
            End Try
        End If
    End Sub

    Private Sub knobThreadProc()
        Try
            ' setup the knob and let it run
            Knob = New FlexKnob
            Thread.Sleep(Timeout.Infinite)
        Catch ex As ThreadInterruptedException
            ' done with the knob
            If Knob IsNot Nothing Then
                Knob.Dispose()
                Knob = Nothing
            End If
        End Try
    End Sub

    ' Remove consequtive periods
    Private Function fixFreq(inFreq As String) As String
        Dim rv As String = vbNullString
        Dim wasPeriod As Boolean = False
        For i As Integer = 0 To inFreq.Length - 1
            If inFreq(i) = "." Then
                If wasPeriod Then
                    Continue For
                Else
                    wasPeriod = True
                End If
            Else
                wasPeriod = False
            End If
            rv &= inFreq(i)
        Next
        Return rv
    End Function

    Friend Sub ConfigW2(suppressDialog As Boolean)
        Tracing.TraceLine("ConfigW2:" & suppressDialog, TraceLevel.Info)
        If W2WattMeter IsNot Nothing Then
            W2WattMeter.Dispose()
        End If
        W2WattMeter = New W2(W2ConfigFile)
        If suppressDialog Then
            ' Called from GetConfigInfo().
            ' Only setup if already configured.
            If W2WattMeter.IsConfigured Then
                W2WattMeter.Setup() ' no config dialogue
            End If
        Else
            ' User wants to configure.
            W2WattMeter.Setup(True)
        End If
    End Sub
    ''' <summary>
    ''' Configure W2 wattmeter.
    ''' </summary>
    Friend Sub ConfigW2()
        ConfigW2(False)
    End Sub

    ''' <summary>
    ''' Validatte a path or file name
    ''' </summary>
    ''' <param name="name"></param>
    ''' <returns>true if good</returns>
    Friend Function IsValidFileNameOrPath(ByVal name As String) As Boolean
        ' Determines if the name is empty or all white space.
        If (name = vbNullString) OrElse (name.Trim = vbNullString) Then
            Return False
        End If

        ' Determines if there are bad characters in the name. 
        For Each badChar As Char In System.IO.Path.GetInvalidPathChars
            If InStr(name, badChar) > 0 Then
                Return False
            End If
        Next

        ' The name passes basic validation. 
        Return True
    End Function
#End Region

    ' region - Scan stuff
#Region "scan"
    Friend SavedScans As SavedScanData
    Friend Enum scans
        none
        linear
        memory
    End Enum
    Friend scanstate As scans = scans.none
    Friend ReadOnly Property ScanInProcess As Boolean
        Get
            Return If(WpfMainWindow IsNot Nothing, WpfMainWindow.ScanTimerEnabled, False)
        End Get
    End Property
    Friend MemoryGroupControl As MemoryGroup
    Friend speechStatus, autoModeStatus As Boolean
    Friend modeStatus As String
    ''' <summary>
    ''' Sprint 10: Scan timer routed to WPF MainWindow.ScanTimer (DispatcherTimer).
    ''' Replaces old Form1.ScanTmr (WinForms Timer) dependency.
    ''' </summary>
    Friend ReadOnly Property scanTimer As System.Windows.Threading.DispatcherTimer
        Get
            Return If(WpfMainWindow IsNot Nothing, WpfMainWindow.ScanTimer, Nothing)
        End Get
    End Property
    ''' <summary>
    ''' Sprint 10: Status writer routed to WPF MainWindow.WriteStatus().
    ''' Replaces old Form1.StatusBox (RadioBoxes.MainBox) dependency.
    ''' </summary>
    Private _statusBoxAdapter As StatusBoxAdapter
    Friend ReadOnly Property StatusBox As StatusBoxAdapter
        Get
            If _statusBoxAdapter Is Nothing AndAlso WpfMainWindow IsNot Nothing Then
                _statusBoxAdapter = New StatusBoxAdapter(WpfMainWindow)
            End If
            Return _statusBoxAdapter
        End Get
    End Property
#End Region

    Friend Const MHZSIZE As Integer = 5
    Friend Const KHZSIZE As Integer = 6
    Friend Const FREQSIZE As Integer = MHZSIZE + KHZSIZE
    Friend Const SMETERSIZE As Integer = 4
    Friend Const RITOFFSETSIZE As Integer = 4 ' 4 digits

    Private Function iFormatFreq(ByVal str As String) As String
        ' Format the frequency for display.
        If str.Length <> FREQSIZE OrElse Not IsNumeric(str) Then
            Return Nothing
        End If
        Dim mhzi As Integer = CInt(str.Substring(0, MHZSIZE))
        ' note that CStr(mhzi) removes leading zeros.
        Dim khz As String = str.Substring(MHZSIZE, KHZSIZE)
        str = khz.Insert(3, ".")
        Return CStr(mhzi) & "." & str
    End Function
    ''' <summary>
    ''' (Overloaded) format the frequency for display
    ''' </summary>
    ''' <param name="IFText">Text from the IF command</param>
    ''' <returns>displayable frequency</returns>
    Friend Function FormatFreq(ByVal IFText As String) As String
        ' Format from "IF" data, or just a frequency.
        Dim freq, rit As String
        Dim vfo As String = ""
        Dim split As String = ""
        Dim i As Integer = 0
        Try
            freq = iFormatFreq(IFText.Substring(i, FREQSIZE))
        Catch ex As Exception
            Tracing.TraceLine("FormatFreq bogus string:" & IFText & " " & ex.Message, TraceLevel.Error)
            Return ""
        End Try
        ' Get RIT offset
        i += 16
        If i >= IFText.Length Then
            Return freq
        End If
        Try
            rit = IFText(i)
            If Not ((rit = " ") OrElse (rit = "+") OrElse (rit = "-")) Then
                ' bogus IF packet
                Return freq
            End If
            If rit = " " Then
                rit = "+"
            End If
            i += 1
            rit &= IFText.Substring(i, RITOFFSETSIZE)
            If ((IFText.Substring(i + RITOFFSETSIZE, 1) = "1") Or _
                (IFText.Substring(i + RITOFFSETSIZE + 1, 1) = "1")) Then
                ' RIT/XIT enabled
                freq &= rit
                If (IFText.Substring(i + RITOFFSETSIZE + 1, 1) = "1") Then
                    ' Xit
                    freq &= "x"
                End If
            End If
            i += RITOFFSETSIZE + 2 + 1
            ' Get the VFO.
            If IFText(i + 4) = "0" Then
                vfo = "A"
            Else
                vfo = "B"
            End If
            If (IFText.Substring(i + 6, 1) = "1") Then
                ' split
                split = "S"
            End If
        Catch ex As Exception
            ' Can happen if just the frequency is passed or the data is bogus.
            ' Return the frequency so far.
            Tracing.TraceLine("FormatFreq exception:" & IFText & " " & ex.Message, TraceLevel.Error)
        End Try
        ' Note that split and vfo are empty if not applicable.
        Return split & vfo & freq
    End Function
    ''' <summary>
    ''' (Overloaded) format the frequency for display
    ''' </summary>
    ''' <param name="freq">64-bit frequency</param>
    ''' <returns>displayable frequency</returns>
    Friend Function FormatFreq(ByVal freq As ULong) As String
        Return FormatFreqUlong(freq)
    End Function

    Private Const threeZeros As String = "000"
    Friend Function FormatFreqUlong(ByVal freq As ULong) As String
        Dim rv As String = ""
        Dim str As String = freq.ToString
        ' Make string at least 7 characters long.
        For i As Integer = 0 To 6 - str.Length
            str = "0" & str
        Next
        Dim len = str.Length
        rv = str.Substring(0, len - 6) & "."c & str.Substring(len - 6, 3) &
                "."c & str.Substring(len - 3)
        Return rv
    End Function

    ''' <summary>
    ''' get numeric frequency string
    ''' </summary>
    ''' <param name="str">string containing frequency as mm.kkk.hhh </param>
    ''' <returns>int64 value</returns>
    Friend Function FreqInt64(ByVal str As String) As ULong
        Dim str2 As String = "0"
        For Each c As Char In str
            If IsNumeric(c) Then
                str2 &= c
            End If
        Next
        Return CLng(str2)
    End Function
    ''' <summary>
    ''' get numeric frequency string
    ''' </summary>
    ''' <param name="str">frequency string</param>
    ''' <returns>numeric frequency as a double </returns>
    Friend Function FreqDouble(ByVal str As String) As Double
        Dim str2 As String = ""
        Dim decSW As Boolean = False
        For Each c As Char In str
            If IsNumeric(c) Then
                str2 &= c
            ElseIf c = "."c Then
                If Not decSW Then
                    decSW = True
                    str2 &= c
                End If
            End If
        Next
        Return CDbl(str2)
    End Function

    Friend Function FormatSMeter(ByVal str As String) As String
        Return str
    End Function

    Friend Const DupEntryMsg As String = " is already on file."
    Friend Const BadFreqMSG As String = " must be of the form mhz.khz.hz, mhz.khz, or khz."
    Friend Function FormatFreqForRadio(ByVal str As String) As String
        ' Return 11-digit freq or nothing.
        Dim s() As String
        Dim st As String = ""
        Dim i As Integer = 0
        Dim err As Boolean = (str Is Nothing)
        If Not err Then
            Dim sep() As Char = {"."c}

            s = str.Split(sep, 3, StringSplitOptions.None)
            For Each st In s
                If st = "" OrElse Not IsNumeric(st) Then
                    err = True
                End If
                i += 1
            Next
            If (i = 3) AndAlso (s(2).IndexOf("."c) > -1) Then
                err = True
            End If
            If Not err Then
                ' They're all numeric.
                Select Case i
                    Case 1
                        ' just khz
                        st = s(0) & "000" ' hz = 0
                        If st.Length > FREQSIZE Then
                            err = True
                        End If
                    Case 2
                        ' khz, s(1), must be 1 to KHZSIZE (6) digits.  Pad to 3 if less.
                        st = s(1)
                        For i = st.Length + 1 To KHZSIZE
                            st &= "0"
                        Next
                        If st.Length > KHZSIZE OrElse s(0).Length > MHZSIZE Then
                            err = True
                        Else
                            st = st.Insert(0, s(0))
                        End If
                    Case 3
                        If s(0).Length > MHZSIZE OrElse s(1).Length <> 3 OrElse s(2).Length > 3 Then
                            err = True
                        Else
                            st = s(1) & s(2)
                            ' May need to expand this to KHZSIZE digits.
                            For i = st.Length + 1 To KHZSIZE
                                st &= "0"
                            Next
                            st = st.Insert(0, s(0))
                        End If
                End Select
                If Not err Then
                    ' pad with leading zeros.
                    For i = 1 To FREQSIZE - st.Length
                        st = st.Insert(0, "0")
                    Next
                End If
            End If
        End If
        If err Then
            Return Nothing
        Else
            Return st
        End If
    End Function
    ''' <summary>
    ''' convert a numeric frequency to a string for the radio
    ''' </summary>
    ''' <param name="intFreq">long integer frequency</param>
    ''' <returns>the number</returns>
    Friend Function FormatUlongFreqForRadio(ByVal intFreq As ULong) As String
        Dim str As String = CStr(intFreq)
        ' Needs to be 11 digits, pad on left with 0's.
        Dim pad As String = ""
        For i As Integer = str.Length To 11 - 1
            pad &= "0"
        Next
        Return pad & str
    End Function
    Friend Function UlongFreq(ByVal str As String) As ULong
        If str Is Nothing Then
            Return 0
        End If
        Dim rv As ULong
        Try
            rv = CULng(FormatFreqForRadio(str))
        Catch ex As Exception
            Tracing.TraceLine("ulongFreq error:" & str, TraceLevel.Error)
            rv = 0
        End Try
        Return rv
    End Function

    Friend Delegate Function awaitFuncDel() As Boolean
    Friend Function Await(func As awaitFuncDel, ms As Integer)
        Return Await(func, ms, 25)
    End Function
    Friend Function Await(func As awaitFuncDel, ms As Integer, waitMS As Integer)
        Dim iterations As Integer = ms / waitMS
        Dim rv As Boolean = func()
        While (Not rv) And (iterations > 0)
            Thread.Sleep(waitMS)
            iterations -= 1
            rv = func()
        End While
        Return rv
    End Function

    ''' <summary>
    ''' (overloaded) Return true if hostname is valid.
    ''' It may be host colon port.
    ''' </summary>
    ''' <param name="host">the entire hostname</param>
    ''' <param name="name">returned hostname</param>
    ''' <param name="port">returned integer port (default is 23)</param>
    ''' <returns>true on success</returns>
    Friend Function IsValidHostname(host As String, ByRef name As String, ByRef port As Integer) As Boolean
        Dim rv As Boolean = (host <> vbNullString)
        name = host
        port = 23 ' default to telnet port#
        If Not rv Then
            Return rv
        End If

        Dim id As Integer = host.IndexOf(":") + 1
        If id > 0 Then
            If ((id < 2) Or (id >= host.Length)) OrElse _
               Not System.Int32.TryParse(host.Substring(id), port) Then
                rv = False
            Else
                name = host.Substring(0, id - 1)
            End If
        End If
        Return rv
    End Function
    Friend Function IsValidHostname(host As String) As Boolean
        Dim dummy1 As String = vbNullString
        Dim dummy2 As Integer = 0
        Return IsValidHostname(host, dummy1, dummy2)
    End Function

    ''' <summary>
    ''' If the value isn't empty, set the field to it.
    ''' Select all the text in the fld.
    ''' </summary>
    ''' <param name="fld">the screen field</param>
    ''' <param name="val">the string value</param>
    Friend Sub SelectFieldText(ByVal fld As TextBox, ByVal val As String)
        If val <> vbNullString Then
            fld.Text = val
        End If
        fld.SelectionStart = 0
        fld.SelectionLength = fld.Text.Length
    End Sub

    ''' <summary>
    ''' get the descriptive string for this key
    ''' </summary>
    ''' <param name="k">the key</param>
    ''' <returns>the string</returns>
    ''' <remarks></remarks>
    Friend Function KeyString(ByVal k As Keys) As String
        Dim str As String
        Dim n As String = k.ToString
        Dim id As Integer = n.IndexOf(", ")
        If id > -1 Then
            ' Reformat the string.
            str = n.Substring(id + 2) & "-"
            str &= n.Substring(0, id)
        Else
            str = n
        End If
        Return str
    End Function

#Region "split VFOs"
    ''' <summary>
    ''' Split VFOs
    ''' </summary>
    ''' <remarks>
    ''' If set to false, the TXVFO is set to the RXVFO.
    ''' If set to true:
    ''' If already split, no action.
    ''' If RXVFO is 1 (was VFOB), TXVFO = 0 (was VFOA).
    ''' Otherwise TXVFO = 1, (was VFOB).
    ''' Thus if RXVFO is A or B, it works like it used to, otherwise TXVFO is B.
    ''' </remarks>
    Friend Property SplitVFOs As Boolean
        Get
            Return RigControl.CanTransmit And RigControl.ValidVFO(RigControl.TXVFO) And
                (RigControl.RXVFO <> RigControl.TXVFO)
        End Get
        Set(value As Boolean)
            If (value = SplitVFOs) Or Not RigControl.CanTransmit Then
                Return
            End If
            If value Then
                If RigControl.RXVFO = 1 Then
                    ' RX is 1, use 0
                    RigControl.TXVFO = 0
                Else
                    ' use 1 regardless.
                    RigControl.TXVFO = 1
                End If
            Else
                RigControl.TXVFO = RigControl.RXVFO
            End If
        End Set
    End Property

    ''' <summary>
    ''' Show XMIT frequency
    ''' </summary>
    Friend Property ShowXMITFrequency As Boolean
        Get
            Return RigControl.ShowingXmitFrequency
        End Get
        Set(value As Boolean)
            ' Inform the rig
            RigControl.ShowingXmitFrequency = value
        End Set
    End Property

    ''' <summary>
    ''' Virtual Receive frequency using ShowXMITFrequency
    ''' </summary>
    Property RXFrequency As ULong
        Get
            Return RigControl.VirtualRXFrequency
        End Get
        Set(value As ULong)
            RigControl.VirtualRXFrequency = value
        End Set
    End Property

    Friend Sub changeSliceAudio(oldval As Integer, newval As Integer)
        ' unmute the new slice
        RigControl.SetVFOAudio(newval, True)

        ' mute the old slice if not being used.
        If (RigControl.RXVFO <> oldval) And (RigControl.TXVFO <> oldval) Then
            RigControl.SetVFOAudio(oldval, False)
        End If
    End Sub

    Friend Property MemoryMode As Boolean
#End Region

    ' Region remote audio
#Region "remote audio"
    Private Const audioDevicesBasename As String = "audioDevices.xml"
    Friend AudioDevicesFile As String
    Friend InputAudioDevice, OutputAudioDevice As JJPortaudio.Devices.Device

    Friend Sub GetNewAudioDevices()
        Dim dev = New JJPortaudio.Devices(AudioDevicesFile)
        dev.Setup()
        InputAudioDevice = dev.getNewDevice(JJPortaudio.Devices.DeviceTypes.input)
        OutputAudioDevice = dev.getNewDevice(JJPortaudio.Devices.DeviceTypes.output)
    End Sub

    ''' <summary>
    ''' PC audio
    ''' </summary>
    Friend Property PCAudio As Boolean
        Get
            Dim rv As Boolean = False
            If RigControl IsNot Nothing Then
                rv = RigControl.PCAudio
            End If
            Return rv
        End Get
        Set(value As Boolean)
            If RigControl Is Nothing Then
                Return
            End If
            If RigControl.PCAudio <> value Then
                If value AndAlso Not EnsureAudioDevicesConfigured(True) Then
                    Return
                End If
                RigControl.PCAudio = value
            End If
        End Set
    End Property

    Friend Function EnsureAudioDevicesConfigured(prompt As Boolean) As Boolean
        If String.IsNullOrEmpty(AudioDevicesFile) Then
            Return True
        End If

        Try
            Dim devices As New JJPortaudio.Devices(AudioDevicesFile)
            If Not devices.Setup() Then
                Return False
            End If

            Dim inputDev = devices.GetConfiguredDevice(JJPortaudio.Devices.DeviceTypes.input, False)
            Dim outputDev = devices.GetConfiguredDevice(JJPortaudio.Devices.DeviceTypes.output, False)

            If (inputDev IsNot Nothing) AndAlso (outputDev IsNot Nothing) Then
                Return True
            End If

            If Not prompt Then
                Return False
            End If

            Dim msg = "Audio devices are not configured. Select input and output devices now?"
            If MessageBox.Show(AppShellForm, msg, MessageHdr, MessageBoxButtons.YesNo, MessageBoxIcon.Information) <> DialogResult.Yes Then
                Return False
            End If

            InputAudioDevice = devices.getNewDevice(JJPortaudio.Devices.DeviceTypes.input)
            If InputAudioDevice Is Nothing Then
                Return False
            End If

            OutputAudioDevice = devices.getNewDevice(JJPortaudio.Devices.DeviceTypes.output)
            Return (OutputAudioDevice IsNot Nothing)
        Catch ex As Exception
            Tracing.TraceLine("Audio device check failed: " & ex.Message, TraceLevel.Error)
            Return False
        End Try
    End Function
#End Region

    ' region - internal errors
#Region "internal errors"
    ''' <summary>
    ''' Show an internal error
    ''' </summary>
    ''' <param name="num">internal error number</param>
    Friend Sub ShowInternalError(num As Integer)
        Dim text As String = InternalError & num
        Tracing.TraceLine("InternalError error:" & text, TraceLevel.Error)
        MessageBox.Show(AppShellForm, text, "JJFlexRadio error", MessageBoxButtons.OK)
    End Sub

    ' Internal errors.
    Friend Const InternalError As String = "Internal error #"
    Friend Const MSReplace As Integer = 1 ' MemoryScan replace.
    Friend Const MSRemove As Integer = 2 ' MemoryScan remove
    Friend Const ScanReplace As Integer = 3 ' Scan replace.
    Friend Const ScanRemove As Integer = 4 ' Scan remove
    Friend Const MSReplaceAdd As Integer = 5 ' MemoryScan replace add.
    Friend Const ScanReplaceAdd As Integer = 6 ' Scan replace add.
    Friend Const MySplitERR As Integer = 7 ' LogEntry, escape at end of string
    Friend Const LogFldMismatch1 As Integer = 8 ' LogEntry.ShowEntries, field mismatch
    Friend Const LogFldMismatch2 As Integer = 9 ' LogEntry.Read, field mismatch
    Friend Const LogVersionErr As Integer = 10 ' bad data version.
    Friend Const ImportHangup As Integer = 11 ' Excessive looping in import().
    'Friend Const NoReadB4Update As Integer = 12
    Friend Const NoSession As Integer = 13 ' No log sessions are established.
    Friend Const MenuMalfunction As Integer = 14 ' the menu should be setup
    Friend Const LogHeaderVersionError As Integer = 15 ' bad log header version
    Friend Const BandProblem As Integer = 16 ' can't get a known band's data.
    Friend Const NoRigError As Integer = 17 ' no rig defined
    Friend Const DupValueError As Integer = 18 ' adding a duplicate CommandValue.
    Friend Const BadMessageIDError As Integer = 19 ' bad cw message id when sending.
    Friend Const DupNotFoundError As Integer = 20 ' dup key element not found.
    Friend Const SessionADIFNotFound As Integer = 21 ' required session.FieldDictionary item not found.
    Friend Const BadCommandID As Integer = 22 ' invalid CommandID.
#End Region

#Region "Form1 → globals migration (Sprint 11 Phase 11.8)"

    ' Constants moved from Form1
    Private Const notConnected As String = "The radio didn't connect."

    ' Screen saver state — saved on startup, restored on exit.
    Private onExitScreenSaver As Boolean

    Private Function setScreenSaver(val As Boolean) As Boolean
        Dim orig As Boolean = JJLogIO.ScreenSaver.GetScreenSaverActive
        JJLogIO.ScreenSaver.SetScreenSaverActive(val)
        Return orig
    End Function

    Private Sub turnTracingOff()
        If BootTrace Then
            Tracing.TraceLine("Boot tracing off")
            Tracing.On = False
            BootTrace = False
        End If
    End Sub

    Private Function currentOperatorName() As String
        Return CurrentOp.UserBasename
    End Function

    ''' <summary>
    ''' Show the one-time "Try Modern UI?" prompt for existing operators
    ''' who predate the UIMode feature.
    ''' </summary>
    Friend Sub CheckUIModUpgradePrompt()
        If CurrentOp Is Nothing Then Return
        If CurrentOp.UIModeDismissed Then Return

        CurrentOp.UIModeDismissed = True

        Dim msg As String = "JJFlex now has a Modern UI mode with reorganized menus." & vbCrLf &
                            "Want to try it? You can switch back anytime with Ctrl+Shift+M."
        Dim result = MessageBox.Show(AppShellForm, msg, "Try Modern Mode?", MessageBoxButtons.YesNo, MessageBoxIcon.Information)
        If result = DialogResult.Yes Then
            CurrentOp.UIModeSetting = CInt(UIMode.Modern)
        Else
            CurrentOp.UIModeSetting = CInt(UIMode.Classic)
        End If

        Operators.UpdateCurrentOp()
    End Sub

    ''' <summary>
    ''' Operator change handler — wired to Operators.ConfigEvent.
    ''' Moved from Form1 during Sprint 11 Phase 11.8.
    ''' </summary>
    Private Sub operatorChanged(sender As Object, e As ConfigArgs)
        If CurrentOp IsNot Nothing Then
            While (ContactLog IsNot Nothing) AndAlso (Not ContactLog.Cleanup)
            End While
            ConfigContactLog()
        End If

        If RigControl IsNot Nothing Then
            RigControl.OperatorChangeHandler()
        End If

        ' Apply the new operator's UI mode preference via WPF.
        If WpfMainWindow IsNot Nothing Then
            WpfMainWindow.ApplyUIMode(CType(ActiveUIMode, JJFlexWpf.MainWindow.UIMode))
        End If
    End Sub

    ' ── Radio open / close ──────────────────────────────────

    Private Enum AutoConnectStartupResult
        ShowSelector
        Connected
        Failed
        UserCancelled
    End Enum

    Private _autoConnectConfig As Radios.AutoConnectConfig

    ''' <summary>
    ''' Attempts to auto-connect to a saved radio on startup.
    ''' </summary>
    Private Function TryAutoConnectOnStartup() As AutoConnectStartupResult
        Try
            Dim operatorName = PersonalData.UniqueOpName(CurrentOp)
            _autoConnectConfig = Radios.AutoConnectConfig.Load(BaseConfigDir, operatorName)

            If Not _autoConnectConfig.ShouldAutoConnect Then
                Tracing.TraceLine("TryAutoConnectOnStartup: no auto-connect configured", TraceLevel.Info)
                Return AutoConnectStartupResult.ShowSelector
            End If

            Tracing.TraceLine("TryAutoConnectOnStartup: attempting " & _autoConnectConfig.RadioName, TraceLevel.Info)

            RigControl = New FlexBase(OpenParms)
            Dim connected = RigControl.TryAutoConnect(_autoConnectConfig)

            If connected Then
                Tracing.TraceLine("TryAutoConnectOnStartup: success", TraceLevel.Info)
                Return AutoConnectStartupResult.Connected
            End If

            Tracing.TraceLine("TryAutoConnectOnStartup: failed, showing dialog", TraceLevel.Info)
            Dim dialogResult = Radios.AutoConnectFailedDialog.ShowDialog(Nothing, _autoConnectConfig.RadioName)

            Select Case dialogResult
                Case Radios.AutoConnectFailedResult.TryAgain
                    RigControl.Dispose()
                    RigControl = New FlexBase(OpenParms)
                    connected = RigControl.TryAutoConnect(_autoConnectConfig)
                    If connected Then
                        Return AutoConnectStartupResult.Connected
                    End If
                    RigControl.Dispose()
                    RigControl = Nothing
                    Return AutoConnectStartupResult.Failed

                Case Radios.AutoConnectFailedResult.DisableAutoConnect
                    _autoConnectConfig.Enabled = False
                    _autoConnectConfig.Save(BaseConfigDir, operatorName)
                    RigControl.Dispose()
                    RigControl = Nothing
                    Return AutoConnectStartupResult.ShowSelector

                Case Radios.AutoConnectFailedResult.ChooseAnotherRadio
                    RigControl.Dispose()
                    RigControl = Nothing
                    Return AutoConnectStartupResult.Failed

                Case Else
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
    Private selectorThread As Thread

    Private Sub selectorProc(o As Object)
        Dim initialCall = CType(o, Boolean)
        Dim selector As RigSelector = New RigSelector(initialCall, OpenParms, Nothing)
        Dim theForm As Form = CType(selector, Form)
        RigControl = New FlexBase(OpenParms)

        ' Wire account selector to auto-use most recent saved account.
        ' Full account selector dialog deferred — WPF dialogs on a WinForms
        ' STA thread have Dispatcher issues. Auto-select is safe and matches
        ' the auto-connect behavior users expect.
        RigControl.ShowAccountSelector = Function(mgr)
                                             Dim accounts = mgr.Accounts
                                             Tracing.TraceLine($"ShowAccountSelector: {accounts.Count} saved account(s)", TraceLevel.Info)
                                             If accounts.Count = 0 Then
                                                 Tracing.TraceLine("ShowAccountSelector: no accounts, triggering new login", TraceLevel.Info)
                                                 Return (True, Nothing, True) ' trigger new login
                                             End If
                                             Dim best = accounts.OrderByDescending(Function(a) a.LastUsed).First()
                                             Tracing.TraceLine($"ShowAccountSelector: auto-selected '{best.FriendlyName}' ({best.Email}), LastUsed={best.LastUsed}, ExpiresAt={best.ExpiresAt}", TraceLevel.Info)
                                             Return (False, best, True) ' use most recent account
                                         End Function

        radioSelected = theForm.ShowDialog()
        If radioSelected <> DialogResult.OK Then
            RigControl.Dispose()
            RigControl = Nothing
        End If
        theForm.Dispose()
    End Sub

    ''' <summary>
    ''' Open the radio — builds OpenParms, runs selector, wires MainWindow.
    ''' Moved from Form1 during Sprint 11 Phase 11.8.
    ''' </summary>
    Friend Function openTheRadio(initialCall As Boolean) As Boolean
        Try
            Dim rv As Boolean
            OpenParms = New FlexBase.OpenParms()
            OpenParms.ProgramName = ProgramName
            OpenParms.CWTextReceiver = AddressOf Commands.DisplayDecodedText
            OpenParms.FormatFreqForRadio = AddressOf UlongFreq
            OpenParms.FormatFreq = AddressOf FormatFreqUlong
            OpenParms.GotoHome = AddressOf WpfMainWindow.gotoHome
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
                    rv = True
                    radioSelected = DialogResult.OK
                    AppShellForm?.Activate()
                    GoTo RadioConnected
                ElseIf autoConnectResult = AutoConnectStartupResult.UserCancelled Then
                    rv = False
                    radioSelected = DialogResult.Cancel
                    Return rv
                End If
            End If

            ' Run RigSelector on T1 (main UI thread) directly.
            ' Previously used a separate STA thread, but that caused slow
            ' focus transitions and NVDA speech delays when tabbing between controls.
            selectorProc(initialCall)
            AppShellForm?.Activate()
            rv = (radioSelected = DialogResult.OK)

RadioConnected:
            If rv Then
                WpfMainWindow.RigControl = RigControl
                WpfMainWindow.OpenParms = OpenParms
                WpfMainWindow.CloseRadioCallback = AddressOf CloseTheRadio
                WpfMainWindow.ShowErrorCallback = Sub(msg, title)
                                                      MessageBox.Show(AppShellForm, msg, title, MessageBoxButtons.OK, MessageBoxIcon.Error)
                                                  End Sub
                WpfMainWindow.PowerOnCallback = Sub()
                                                    SetupKnob()
                                                    StartDailyTraceIfEnabled()
                                                End Sub
                WpfMainWindow.WireRadioEvents()

                ' Ensure ShellForm is visible before Start() so error dialogs
                ' have a parent window and screen readers can announce them.
                If AppShellForm IsNot Nothing AndAlso Not AppShellForm.Visible Then
                    AppShellForm.Show()
                    AppShellForm.Activate()
                    Threading.Thread.Sleep(500) ' Let window settle before any error dialogs
                End If

                Tracing.TraceLine("OpenTheRadio:rig is starting", TraceLevel.Info)
                rv = RigControl.Start()

                ' If Start() failed because SmartLink connection was too slow or dropped
                ' during the guiClient re-add cycle, retry with a fresh connection.
                ' A fresh connection bypasses the slow re-add and usually succeeds quickly.
                If Not rv AndAlso RigControl IsNot Nothing AndAlso
                   Not RigControl.IsConnected AndAlso _autoConnectConfig IsNot Nothing Then

                    Tracing.TraceLine("OpenTheRadio:Start failed with disconnected radio, retrying once", TraceLevel.Info)

                    ' Clean up the dead connection
                    WpfMainWindow?.UnwireRadioEvents()
                    RigControl.Dispose()

                    ' Create new rig instance and reconnect
                    RigControl = New FlexBase(OpenParms)
                    WpfMainWindow.RigControl = RigControl

                    If RigControl.TryAutoConnect(_autoConnectConfig) Then
                        WpfMainWindow.WireRadioEvents()
                        Tracing.TraceLine("OpenTheRadio:retry - rig is starting", TraceLevel.Info)
                        rv = RigControl.Start()
                    End If

                    If Not rv Then
                        Tracing.TraceLine("OpenTheRadio:retry also failed", TraceLevel.Error)
                        Radios.ScreenReaderOutput.Speak("Connection failed")
                    End If
                End If

                If Not rv Then
                    radioSelected = DialogResult.Abort
                End If
            End If

            If rv Then
                WpfMainWindow.OnRadioStarted()
            Else
                Tracing.TraceLine("OpenTheRadio:rig's open failed", TraceLevel.Error)
                If radioSelected = DialogResult.Abort Then
                    CloseTheRadio()
                ElseIf radioSelected = DialogResult.No Then
                    MessageBox.Show(AppShellForm, notConnected, ErrorHdr, MessageBoxButtons.OK)
                Else
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
    ''' Close the radio and unwire all events.
    ''' Moved from Form1 during Sprint 11 Phase 11.8.
    ''' </summary>
    Friend Sub CloseTheRadio()
        Tracing.TraceLine("CloseTheRadio", TraceLevel.Info)

        WpfMainWindow?.UnwireRadioEvents()

        StopKnob()
        If SMeter IsNot Nothing Then
            SMeter.Peak = False
        End If
        If RigControl IsNot Nothing Then
            Power = False
            RigControl.Dispose()
            RigControl = Nothing
            If WpfMainWindow IsNot Nothing Then
                WpfMainWindow.RigControl = Nothing
            End If
        End If
    End Sub

    ''' <summary>
    ''' Select a different radio — disconnect current, open new.
    ''' Moved from Form1.SelectRigMenuItem_Click during Sprint 11 Phase 11.8.
    ''' </summary>
    Friend Sub SelectRadio()
        Tracing.TraceLine("SelectRadio", TraceLevel.Info)
        Try
            If RigControl IsNot Nothing Then
                Dim radioName = RigControl.RadioNickname
                If Not String.IsNullOrEmpty(radioName) Then
                    Radios.ScreenReaderOutput.Speak("Disconnecting from " & radioName, True)
                Else
                    Radios.ScreenReaderOutput.Speak("Disconnecting from radio", True)
                End If
                CloseTheRadio()
            End If
            openTheRadio(False)
        Catch ex As Exception
            Tracing.TraceLine("SelectRadio:exception " & ex.Message, TraceLevel.Error)
        End Try
    End Sub

    ''' <summary>
    ''' Application exit sequence — cleanup and shutdown.
    ''' Moved from Form1.FileExitToolStripMenuItem_Click during Sprint 11 Phase 11.8.
    ''' Returns False to cancel exit, True to proceed.
    ''' </summary>
    Friend Function ExitApplication() As Boolean
        Ending = True

        ' Check for unsaved QSO
        If Not LogEntry.optionalWrite() Then
            Ending = False
            Return False
        End If

        Try
            LogEntry.Close()
            Logs.Done()
            If LookupStation IsNot Nothing Then
                LookupStation.Finished()
            End If
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
            Tracing.TraceLine("ExitApplication:" & ex.Message, TraceLevel.Error)
        End Try
        Tracing.TraceLine("End.")
        Tracing.On = False
        Return True
    End Function

    ''' <summary>
    ''' Application initialization — runs after WPF MainWindow is loaded.
    ''' Moved from Form1_Load during Sprint 11 Phase 11.8.
    ''' </summary>
    Friend Sub InitializeApplication()
        Tracing.TraceLine("InitializeApplication: starting", TraceLevel.Info)

        GetConfigInfo()
        CheckUIModUpgradePrompt()

        StationName = getStationName()
        Tracing.TraceLine("StationName:" & StationName, TraceLevel.Info)

        ' Set window title to include station name (was Form1.Text in Form1_Load)
        Dim pgmName = StationName
        If ProgramInstance > 1 Then
            pgmName &= ProgramInstance.ToString
        End If
        If AppShellForm IsNot Nothing Then
            AppShellForm.Text &= " " & pgmName
        End If

        ' Wire operator change handler
        AddHandler Operators.ConfigEvent, AddressOf operatorChanged

        ' Wire WriteText delegates to MainWindow
        WriteText = Sub(tbid, text, clearFlag)
                        WpfMainWindow.WriteText(CType(tbid, JJFlexWpf.MainWindow.WindowIDs), text, 0, clearFlag)
                    End Sub
        WriteTextX = Sub(tbid, s, cur, c)
                         WpfMainWindow.WriteText(CType(tbid, JJFlexWpf.MainWindow.WindowIDs), s, cur, c)
                     End Sub

        ProgramDirectory = IO.Directory.GetCurrentDirectory()
        onExitScreenSaver = setScreenSaver(False)

        ' Apply the correct UI mode now that operators are loaded
        If WpfMainWindow IsNot Nothing AndAlso CurrentOp IsNot Nothing Then
            WpfMainWindow.ApplyUIMode(CType(ActiveUIMode, JJFlexWpf.MainWindow.UIMode))
        End If

        openTheRadio(True)

        Tracing.TraceLine("InitializeApplication: complete", TraceLevel.Info)
    End Sub

#End Region
End Module
