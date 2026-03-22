Imports System.Windows.Forms

''' <summary>
''' WinForms "Connecting..." window that holds focus during radio connection
''' and SmartLink authentication. WinForms windows work natively from VB.NET
''' without the WPF interop focus issues that drop keyboard focus to Explorer.
''' </summary>
Public Class ConnectingForm
    Inherits Form

    Private ReadOnly _statusLabel As Label
    Private ReadOnly _focusTimer As Timer

    Public Sub New(initialMessage As String)
        Text = "Connecting"
        Width = 350
        Height = 120
        FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.CenterParent
        TopMost = True
        ShowInTaskbar = False
        MaximizeBox = False
        MinimizeBox = False
        ControlBox = False

        AccessibleName = "Connecting"
        AccessibleRole = AccessibleRole.Dialog

        _statusLabel = New Label() With {
            .Text = initialMessage,
            .Dock = DockStyle.Fill,
            .TextAlign = ContentAlignment.MiddleCenter,
            .Font = New Drawing.Font(Font.FontFamily, 11),
            .AccessibleName = initialMessage,
            .AccessibleRole = AccessibleRole.StaticText
        }
        Controls.Add(_statusLabel)

        ' Focus reclaim timer — WebView2 auth steals focus during SmartLink,
        ' this pulls it back every 200ms.
        _focusTimer = New Timer() With {.Interval = 200}
        AddHandler _focusTimer.Tick, Sub(s, e)
                                         If Visible AndAlso Not ContainsFocus Then
                                             Activate()
                                         End If
                                     End Sub
        _focusTimer.Start()
    End Sub

    ''' <summary>
    ''' Update the status message (thread-safe).
    ''' </summary>
    Public Sub UpdateStatus(message As String)
        If InvokeRequired Then
            Invoke(Sub() UpdateStatus(message))
            Return
        End If
        _statusLabel.Text = message
        _statusLabel.AccessibleName = message
    End Sub

    ''' <summary>
    ''' Close the form (thread-safe).
    ''' </summary>
    Public Sub CloseForm()
        If InvokeRequired Then
            BeginInvoke(Sub() CloseForm())
            Return
        End If
        _focusTimer.Stop()
        Close()
    End Sub

    Protected Overrides Sub OnFormClosed(e As FormClosedEventArgs)
        _focusTimer.Stop()
        _focusTimer.Dispose()
        MyBase.OnFormClosed(e)
    End Sub
End Class
