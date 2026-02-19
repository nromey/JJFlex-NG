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

            ' Run VB-side initialization (moved from Form1_Load).
            InitializeApplication()

            ' Wire DoCommandHandler AFTER GetConfigInfo (which creates Commands).
            WpfMainWindow.DoCommandHandler = AddressOf Commands.DoCommand

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
            End Sub
        End Sub

        Private Sub MyApplication_Shutdown(sender As Object, e As System.EventArgs) Handles Me.Shutdown
            ' Clean up screen reader resources.
            Radios.ScreenReaderOutput.Shutdown()
        End Sub
    End Class
End Namespace
