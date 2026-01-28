Imports System.Net
Imports System.Security.Authentication
Imports System.Windows.Forms

Namespace My
    ''' <summary>
    ''' Application-level events and initialization helpers.
    ''' </summary>
    Partial Friend Class MyApplication
        Private Sub MyApplication_Startup(sender As Object, e As ApplicationServices.StartupEventArgs) Handles Me.Startup
            ' Enforce a modern TLS floor for all outbound HTTPS/TLS traffic in the app domain.
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 Or SecurityProtocolType.Tls13

            ' Enable global crash capture and reporting.
            AddHandler System.Windows.Forms.Application.ThreadException, AddressOf CrashReporter.OnThreadException
            AddHandler AppDomain.CurrentDomain.UnhandledException, AddressOf CrashReporter.OnUnhandledException
        End Sub
    End Class
End Namespace
