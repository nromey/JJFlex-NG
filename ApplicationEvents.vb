Imports System.Net
Imports System.Security.Authentication
Imports System.Windows.Forms

Namespace My
    ''' <summary>
    ''' Application-level events and initialization helpers.
    ''' The My.Application framework is preserved for My.* namespace compatibility.
    '''
    ''' Architecture: ShellForm (WinForms) hosts WPF MainWindow content via ElementHost.
    ''' ShellForm owns the HWND, taskbar entry, and message loop.
    ''' ElementHost provides keyboard routing and screen reader bridging.
    ''' </summary>
    Partial Friend Class MyApplication
        ''' <summary>
        ''' The WPF main content — hosted inside ShellForm via ElementHost.
        ''' </summary>
        Friend Shared WpfMainWindow As JJFlexWpf.MainWindow

        ''' <summary>
        ''' The WinForms shell form — created in Startup, used as MainForm.
        ''' </summary>
        Friend Shared TheShellForm As ShellForm

        Private Sub MyApplication_Startup(sender As Object, e As ApplicationServices.StartupEventArgs) Handles Me.Startup
            ' Initialize native library resolver FIRST (enables x86/x64 DLL loading)
            NativeLoader.Initialize()

            ' Enforce a modern TLS floor for all outbound HTTPS/TLS traffic in the app domain.
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 Or SecurityProtocolType.Tls13

            ' Enable global crash capture and reporting.
            AddHandler System.Windows.Forms.Application.ThreadException, AddressOf CrashReporter.OnThreadException
            AddHandler AppDomain.CurrentDomain.UnhandledException, AddressOf CrashReporter.OnUnhandledException

            ' Initialize screen reader output (CrossSpeak/Tolk) for accessibility announcements.
            Radios.ScreenReaderOutput.Initialize()

            ' Initialize NAudio-based earcon player for UI sound effects.
            JJFlexWpf.EarconPlayer.Initialize()

            ' Initialize compiled help file launcher.
            JJFlexWpf.HelpLauncher.Initialize()

            ' Purge connection profiles older than 7 days.
            Radios.ConnectionProfiler.PurgeOldProfiles()

            ' ── Create the ShellForm and get the WPF content ───────────────
            ' We create ShellForm here (before OnCreateMainForm) so we can wire
            ' callbacks. OnCreateMainForm will use the same instance.
            TheShellForm = New ShellForm()
            WpfMainWindow = TheShellForm.WpfContent

            ' Wire scan timer tick to dispatch between linear and memory scan.
            AddHandler WpfMainWindow.ScanTimerTick,
                Sub(s, args)
                    If scanstate = scans.linear Then
                        scan.ScanTimer_Tick(s, args)
                    Else
                        MemoryScan.ScanTimer_Tick(s, args)
                    End If
                End Sub

            ' Wire exit callback so RequestShutdown can trigger VB-side shutdown.
            WpfMainWindow.AppExitCallback = AddressOf ExitApplication

            ' Wire "Connect to Radio" callback for menu item.
            WpfMainWindow.SelectRadioCallback = AddressOf SelectRadio

            ' Wire close callback so Exit menu item can close the ShellForm.
            WpfMainWindow.CloseShellCallback = Sub() TheShellForm.Close()

            ' Wire Connection Test results callback (Sprint 15.5)
            WpfMainWindow.ShowTestResultsCallback = AddressOf ShowTestResults

            ' Wire UI mode persistence — saves to operator profile when user switches modes.
            WpfMainWindow.SaveUIModeCallback = Sub(mode)
                ActiveUIMode = CType(mode, UIMode)
            End Sub

            ' Run VB-side initialization (moved from Form1_Load).
            ' Note: MigrateConfigFiles runs inside InitializeApplication before openTheRadio
            ' so auto-connect can find renamed config files.
            InitializeApplication()

            ' Wire DoCommandHandler AFTER GetConfigInfo (which creates Commands).
            WpfMainWindow.DoCommandHandler = AddressOf Commands.DoCommand

            ' Wire Speak Status / Status Dialog callbacks for menu items.
            WpfMainWindow.SpeakStatusCallback = Sub()
                Dim kt = Commands.Lookup(CommandValues.SpeakStatus)
                If kt IsNot Nothing AndAlso kt.Handler IsNot Nothing Then kt.Handler.Invoke()
            End Sub
            WpfMainWindow.ShowStatusDialogCallback = Sub()
                Dim kt = Commands.Lookup(CommandValues.ShowStatusDialog)
                If kt IsNot Nothing AndAlso kt.Handler IsNot Nothing Then kt.Handler.Invoke()
            End Sub

            ' Wire audio device callback for NativeMenuBar Audio menu.
            WpfMainWindow.AudioSetupCallback = AddressOf GetNewAudioDevices

            ' Wire key/command callbacks for WPF dialogs (Sprint 16 Track C).
            WpfMainWindow.GetKeyActionsCallback = Function()
                Dim result = New List(Of JJFlexWpf.Dialogs.KeyActionItem)
                For Each kt In Commands.KeyTable
                    result.Add(New JJFlexWpf.Dialogs.KeyActionItem With {
                        .KeyName = KeyString(kt.KeyDef.Key),
                        .KeyDescription = KeyString(kt.KeyDef.Key),
                        .ActionName = kt.KeyDef.Id.ToString(),
                        .ActionDescription = kt.HelpText
                    })
                Next
                Return result
            End Function

            WpfMainWindow.GetAvailableActionsCallback = Function()
                Dim result = New List(Of JJFlexWpf.Dialogs.ActionItem)
                For Each kt In Commands.KeyTable
                    result.Add(New JJFlexWpf.Dialogs.ActionItem With {
                        .Name = kt.KeyDef.Id.ToString(),
                        .Description = kt.HelpText
                    })
                Next
                Return result
            End Function

            WpfMainWindow.GetCommandFinderItemsCallback = Function()
                Dim result = New List(Of JJFlexWpf.Dialogs.CommandFinderItem)
                Dim currentKeys = Commands.CurrentKeys()
                JJTrace.Tracing.TraceLine($"CommandFinder: KeyTable has {Commands.KeyTable.Length} entries, CurrentKeys returned {currentKeys.Length} entries", TraceLevel.Info)
                For Each kt In currentKeys
                    result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                        .Description = kt.HelpText,
                        .KeyDisplay = KeyString(kt.KeyDef.Key),
                        .Scope = kt.Scope.ToString(),
                        .Group = kt.Group.ToString(),
                        .MenuText = kt.MenuText,
                        .Keywords = kt.Keywords,
                        .Tag = kt.KeyDef.Id
                    })
                Next
                JJTrace.Tracing.TraceLine($"CommandFinder: {result.Count} key commands loaded", TraceLevel.Info)
                ' Add informational items for field-specific and filter keys.
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Adjust filter edges (on Freq field)", .KeyDisplay = "[ ]",
                    .Scope = "Radio", .Group = "Filter",
                    .Keywords = New String() {"filter", "narrow", "widen", "edge"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Slide passband left/right", .KeyDisplay = "Shift+[ ]",
                    .Scope = "Radio", .Group = "Filter",
                    .Keywords = New String() {"filter", "slide", "passband", "shift"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Squeeze or pull filter edges", .KeyDisplay = "Ctrl+[ ]",
                    .Scope = "Radio", .Group = "Filter",
                    .Keywords = New String() {"filter", "squeeze", "pull", "narrow", "widen"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Cycle filter presets", .KeyDisplay = "Alt+[ ]",
                    .Scope = "Radio", .Group = "Filter",
                    .Keywords = New String() {"filter", "preset", "cycle"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Double-tap to adjust single filter edge", .KeyDisplay = "[[ or ]]",
                    .Scope = "Radio", .Group = "Filter",
                    .Keywords = New String() {"filter", "edge", "adjust", "lower", "upper"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Toggle mute (on Slice field)", .KeyDisplay = "M",
                    .Scope = "Radio", .Group = "FreqOut",
                    .Keywords = New String() {"mute", "unmute", "audio", "slice"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Set transmit slice (on Slice field)", .KeyDisplay = "T",
                    .Scope = "Radio", .Group = "FreqOut",
                    .Keywords = New String() {"transmit", "tx", "slice"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Enter frequency digits (on Freq field)", .KeyDisplay = "0-9",
                    .Scope = "Radio", .Group = "FreqOut",
                    .Keywords = New String() {"frequency", "enter", "type", "digits"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Round to nearest kHz", .KeyDisplay = "K",
                    .Scope = "Radio", .Group = "FreqOut",
                    .Keywords = New String() {"frequency", "round", "kilohertz", "khz"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Type exact value (on value field)", .KeyDisplay = "Enter",
                    .Scope = "Radio", .Group = "ValueField",
                    .Keywords = New String() {"value", "enter", "type", "exact", "number"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Large step adjust (on value field)", .KeyDisplay = "PgUp / PgDn",
                    .Scope = "Radio", .Group = "ValueField",
                    .Keywords = New String() {"value", "page", "large", "step"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Set to min or max (on value field)", .KeyDisplay = "Home / End",
                    .Scope = "Radio", .Group = "ValueField",
                    .Keywords = New String() {"value", "minimum", "maximum", "home", "end"}})
                ' Leader key commands (Ctrl+J → second key)
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Toggle Noise Reduction", .KeyDisplay = "Ctrl+J, N",
                    .Scope = "Radio", .Group = "DSP",
                    .Keywords = New String() {"NR", "noise", "reduction", "leader", "toggle"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Toggle Noise Blanker", .KeyDisplay = "Ctrl+J, B",
                    .Scope = "Radio", .Group = "DSP",
                    .Keywords = New String() {"NB", "noise", "blanker", "leader", "toggle"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Toggle Wideband NB", .KeyDisplay = "Ctrl+J, W",
                    .Scope = "Radio", .Group = "DSP",
                    .Keywords = New String() {"WNB", "wideband", "noise", "blanker", "leader", "toggle"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Toggle Neural NR", .KeyDisplay = "Ctrl+J, R",
                    .Scope = "Radio", .Group = "DSP",
                    .Keywords = New String() {"RNN", "neural", "noise", "reduction", "leader", "toggle"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Toggle Spectral NR", .KeyDisplay = "Ctrl+J, S",
                    .Scope = "Radio", .Group = "DSP",
                    .Keywords = New String() {"NRS", "spectral", "noise", "reduction", "leader", "toggle"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Toggle Auto Notch", .KeyDisplay = "Ctrl+J, A",
                    .Scope = "Radio", .Group = "DSP",
                    .Keywords = New String() {"ANF", "auto", "notch", "leader", "toggle"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Toggle Audio Peak Filter (CW)", .KeyDisplay = "Ctrl+J, P",
                    .Scope = "Radio", .Group = "DSP",
                    .Keywords = New String() {"APF", "audio", "peak", "filter", "cw", "leader", "toggle"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Speak TX Filter Width", .KeyDisplay = "Ctrl+J, F",
                    .Scope = "Radio", .Group = "audio",
                    .Keywords = New String() {"TX", "filter", "bandwidth", "width", "sculpt", "leader"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Leader Key Help", .KeyDisplay = "Ctrl+J, H",
                    .Scope = "Global", .Group = "help",
                    .Keywords = New String() {"leader", "help", "commands", "list"}})
                ' TX Filter sculpting shortcuts
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Nudge TX filter low edge down", .KeyDisplay = "Ctrl+Shift+[",
                    .Scope = "Radio", .Group = "audio",
                    .Keywords = New String() {"TX", "filter", "low", "down", "sculpt", "transmit"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Nudge TX filter low edge up", .KeyDisplay = "Ctrl+Shift+]",
                    .Scope = "Radio", .Group = "audio",
                    .Keywords = New String() {"TX", "filter", "low", "up", "sculpt", "transmit"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Nudge TX filter high edge down", .KeyDisplay = "Ctrl+Alt+[",
                    .Scope = "Radio", .Group = "audio",
                    .Keywords = New String() {"TX", "filter", "high", "down", "sculpt", "transmit"}})
                result.Add(New JJFlexWpf.Dialogs.CommandFinderItem With {
                    .Description = "Nudge TX filter high edge up", .KeyDisplay = "Ctrl+Alt+]",
                    .Scope = "Radio", .Group = "audio",
                    .Keywords = New String() {"TX", "filter", "high", "up", "sculpt", "transmit"}})
                Return result
            End Function

            WpfMainWindow.ExecuteCommandCallback = Sub(tag)
                If TypeOf tag Is CommandValues Then
                    Dim cmdId = DirectCast(tag, CommandValues)
                    Dim kt = Commands.Lookup(cmdId)
                    If kt IsNot Nothing AndAlso kt.Handler IsNot Nothing Then
                        Commands.CommandId = cmdId
                        Try
                            kt.Handler()
                        Catch ex As Exception
                            JJTrace.Tracing.TraceLine("ExecuteCommand:" & ex.Message, TraceLevel.Error)
                        End Try
                    End If
                End If
            End Sub

            ' Wire auto-connect callbacks — needs CurrentOp from InitializeApplication.
            WpfMainWindow.IsAutoConnectEnabled = Function()
                If CurrentOp Is Nothing Then Return False
                Dim opName = PersonalData.UniqueOpName(CurrentOp)
                Dim config = Radios.AutoConnectConfig.Load(BaseConfigDir, opName)
                Return config.GlobalAutoConnectEnabled
            End Function

            WpfMainWindow.GetAutoConnectRadioName = Function() As String
                If CurrentOp Is Nothing Then Return Nothing
                Dim opName = PersonalData.UniqueOpName(CurrentOp)
                Dim config = Radios.AutoConnectConfig.Load(BaseConfigDir, opName)
                If config.Enabled AndAlso Not String.IsNullOrEmpty(config.RadioSerial) Then
                    Return config.RadioName
                End If
                Return Nothing
            End Function

            WpfMainWindow.SetAutoConnectEnabled = Sub(enabled As Boolean)
                If CurrentOp Is Nothing Then Return
                Dim opName = PersonalData.UniqueOpName(CurrentOp)
                Dim config = Radios.AutoConnectConfig.Load(BaseConfigDir, opName)
                config.GlobalAutoConnectEnabled = enabled
                config.Save(BaseConfigDir, opName)
            End Sub

            WpfMainWindow.ClearAutoConnectRadio = Sub()
                If CurrentOp Is Nothing Then Return
                Dim opName = PersonalData.UniqueOpName(CurrentOp)
                Dim config = Radios.AutoConnectConfig.Load(BaseConfigDir, opName)
                config.ClearAutoConnectRadio()
                config.Save(BaseConfigDir, opName)
            End Sub

            ' Wire FreqOutHandlers delegates for VB.NET globals access (Sprint 12).
            ' These are wired after InitializeApplication so RigControl is available.
            WpfMainWindow.FreqOutHandlersWireCallback = Sub(handlers)
                handlers.GetSplitVFOs = Function() SplitVFOs
                handlers.SetSplitVFOs = Sub(v) SplitVFOs = v
                handlers.GetShowXmitFrequency = Function() ShowXMITFrequency
                handlers.SetShowXmitFrequency = Sub(v) ShowXMITFrequency = v
                handlers.GetMemoryMode = Function() MemoryMode
                handlers.SetMemoryMode = Sub(v) MemoryMode = v
                handlers.GetRXFrequency = Function() RXFrequency
                handlers.SetRXFrequency = Sub(v) RXFrequency = v
                ' These lambdas access RigControl at call time (module variable),
                ' so they work even if RigControl is Nothing when wired.
                handlers.FormatFreq = Function(s) RigControl.Callouts.FormatFreq(ULong.Parse(s))
                handlers.FreqInt64 = Function(s) RigControl.Callouts.FormatFreqForRadio(s)

                ' Load filter presets for the current operator and wire to both
                ' FreqOutHandlers (for Alt+[/] preset cycling) and NativeMenuBar (for menu).
                If CurrentOp IsNot Nothing Then
                    Dim opName = PersonalData.UniqueOpName(CurrentOp)
                    Dim presets = Radios.FilterPresets.Load(BaseConfigDir & "\Radios", opName)
                    handlers.FilterPresets = presets
                    WpfMainWindow.SetNativeMenuFilterPresetsCallback?.Invoke(presets)
                End If

                ' Wire band memory and license config (Sprint 17 Track C).
                handlers.GetConfigDirectory = Function() BaseConfigDir
                handlers.GetOperatorName = Function()
                    If CurrentOp IsNot Nothing Then
                        Return PersonalData.UniqueOpName(CurrentOp)
                    End If
                    Return "default"
                End Function
                If CurrentOp IsNot Nothing Then
                    Dim opName = PersonalData.UniqueOpName(CurrentOp)
                    handlers.BandMem = Radios.BandMemory.Load(BaseConfigDir, opName)
                    Dim isFirstRun = Not Radios.LicenseConfig.Exists(BaseConfigDir, opName)
                    handlers.License = Radios.LicenseConfig.Load(BaseConfigDir, opName)
                    If isFirstRun Then
                        ' First run: save defaults and prompt user to set license class
                        handlers.License.Save(BaseConfigDir, opName)
                        Radios.ScreenReaderOutput.Speak(
                            "Welcome to JJ Flexible Radio Access. Your license class defaults to Extra. " &
                            "Open Settings from the Tools menu to change your license class.")
                    End If
                End If
            End Sub
        End Sub

        Private Sub MyApplication_Shutdown(sender As Object, e As System.EventArgs) Handles Me.Shutdown
            ' Shut down meter sonification engine.
            JJFlexWpf.MeterToneEngine.Shutdown()
            ' Clean up NAudio earcon player.
            JJFlexWpf.EarconPlayer.Dispose()
            ' Clean up screen reader resources.
            Radios.ScreenReaderOutput.Shutdown()
        End Sub
    End Class
End Namespace
