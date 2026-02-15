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

            WpfMainWindow.Show()
        End Sub

        Private Sub MyApplication_Shutdown(sender As Object, e As System.EventArgs) Handles Me.Shutdown
            ' Clean up screen reader resources.
            Radios.ScreenReaderOutput.Shutdown()
        End Sub
    End Class
End Namespace
