Imports System.Net
Imports System.Security.Authentication
Imports System.Windows.Forms

Namespace My
    ''' <summary>
    ''' Application-level events and initialization helpers.
    ''' Sprint 8: Launches WPF MainWindow instead of WinForms Form1.
    ''' The My.Application framework is preserved for My.* namespace compatibility.
    ''' </summary>
    Partial Friend Class MyApplication
        ''' <summary>
        ''' The WPF main window — replaces Form1 as the primary UI.
        ''' </summary>
        Friend Shared WpfMainWindow As JJFlexWpf.MainWindow

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

            ' ── Sprint 8: Launch WPF MainWindow ───────────────────────
            ' The My.Application framework runs a WinForms message loop with a hidden bridge form.
            ' We create the WPF MainWindow here and show it. When it closes, we close the bridge form.
            WpfMainWindow = New JJFlexWpf.MainWindow()

            ' When the WPF window closes, shut down the WinForms bridge form too.
            AddHandler WpfMainWindow.Closed,
                Sub(s, args)
                    ' Close the hidden bridge MainForm to end the My.Application message loop.
                    If Me.MainForm IsNot Nothing AndAlso Not Me.MainForm.IsDisposed Then
                        Me.MainForm.Close()
                    End If
                End Sub

            ' Wire up the keyboard command handler so WPF PreviewKeyDown
            ' can route keystrokes to the VB.NET KeyCommands system.
            ' This replaces the ElementHost→Form1 forwarding chain.
            ' Note: DoCommandHandler wiring deferred to after GetConfigInfo creates Commands.

            ' Sprint 10: Wire scan timer tick to dispatch between linear and memory scan.
            ' Replaces Form1.ScanTimer_Tick (Handles ScanTmr.Tick).
            AddHandler WpfMainWindow.ScanTimerTick,
                Sub(s, args)
                    If scanstate = scans.linear Then
                        scan.ScanTimer_Tick(s, args)
                    Else
                        MemoryScan.ScanTimer_Tick(s, args)
                    End If
                End Sub

            ' Sprint 11 Phase 11.8: Wire exit callback so MainWindow_Closing
            ' can trigger the VB-side shutdown sequence.
            WpfMainWindow.AppExitCallback = AddressOf ExitApplication

            ' Sprint 11 Phase 11.8: Wire "Connect to Radio" callback for menu item.
            WpfMainWindow.SelectRadioCallback = AddressOf SelectRadio

            WpfMainWindow.Show()

            ' Sprint 11 Phase 11.8: Run VB-side initialization (moved from Form1_Load).
            ' Must run after Show() so WPF window is visible and Dispatcher is active.
            InitializeApplication()

            ' Wire DoCommandHandler AFTER GetConfigInfo (which creates Commands).
            WpfMainWindow.DoCommandHandler = AddressOf Commands.DoCommand
        End Sub

        Private Sub MyApplication_Shutdown(sender As Object, e As System.EventArgs) Handles Me.Shutdown
            ' Clean up screen reader resources.
            Radios.ScreenReaderOutput.Shutdown()
        End Sub
    End Class
End Namespace
