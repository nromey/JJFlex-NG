Imports System.Threading
Imports System.Windows.Forms
Imports JJTrace
Imports Radios

''' <summary>
''' WinForms "Connecting..." window that holds focus during radio connection
''' and SmartLink authentication.
'''
''' Stuck-modal-escape architecture (2026-05-04): the form runs on its OWN
''' message-pump thread so Escape and the X close button respond even while
''' Start() blocks the main UI thread in its station-name-wait loop. The
''' cancel handler raises a thread-safe flag (FlexBase.RequestCancel) that
''' Start() polls; Start exits with LastStartFailureReason="Cancelled by user"
''' and the existing failure path runs the cleanup.
'''
''' Subscribes to ConnectionProfiler.EventRecorded for state-aware text + the
''' counting earcons (1 / 1+1 / 1+1+1). Phase announcements only fire if a
''' phase takes longer than 500 ms so fast LAN connects stay silent.
'''
''' Two timers manage time-bound escalation:
'''   - 60 s wall clock: surface a diagnostic-rich "Keep waiting / Cancel"
'''     dialog using recent ConnectionProfiler events.
'''   - 5 min wall clock: hard auto-cancel ceiling (per
'''     project_dialog_escape_rule.md — "forced taskkill is the worst-case
'''     escape path"; auto-cancel is the next-worst-case and we provide it
'''     before the user has to reach for taskkill).
''' </summary>
Public Class ConnectingForm
    Inherits Form

    Private ReadOnly _statusLabel As Label
    Private ReadOnly _focusTimer As System.Windows.Forms.Timer
    Private ReadOnly _escalationTimer As System.Windows.Forms.Timer
    Private ReadOnly _autoCancelTimer As System.Windows.Forms.Timer
    Private ReadOnly _cancelCallback As Action
    Private ReadOnly _profiler As Radios.ConnectionProfiler
    Private ReadOnly _radioName As String
    Private _profilerHandler As Action(Of String, Long, Dictionary(Of String, Object))
    Private _phaseStartTickMs As Long = 0
    Private _currentPhase As Integer = 1
    Private _cancelHandled As Boolean = False
    Private _escalationActive As Boolean = False

    ' Phase announcements only fire if the current phase takes longer than
    ' this. Fast LAN connects (~3 s total, with phases sub-second) stay silent.
    Private Const PhaseAnnounceThresholdMs As Integer = 500
    Private Const EscalationIntervalMs As Integer = 60_000      ' 60 s
    Private Const AutoCancelCeilingMs As Integer = 300_000       ' 5 min

    ''' <summary>
    ''' Construct the connecting modal. Caller passes the radio's display name
    ''' and the cancel callback (typically <c>RigControl.RequestCancel</c>).
    ''' Optionally wires up to a ConnectionProfiler so the modal updates text
    ''' as the connection moves through phases.
    ''' </summary>
    Public Sub New(radioName As String, cancelCallback As Action, profiler As Radios.ConnectionProfiler)
        _radioName = If(radioName, "radio")
        _cancelCallback = cancelCallback
        _profiler = profiler

        Text = "Connecting"
        Width = 420
        Height = 150
        FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.CenterScreen
        TopMost = True
        ShowInTaskbar = False
        MaximizeBox = False
        MinimizeBox = False
        ' ControlBox = True so the X close button is visible. Aviation framing:
        ' the X is the ONE explicit cancel control; no other Tab stops exist.
        ControlBox = True
        KeyPreview = True

        AccessibleName = "Connecting"
        AccessibleRole = AccessibleRole.Dialog

        Dim initialMessage = $"Connecting to {_radioName}..."
        _statusLabel = New Label() With {
            .Text = initialMessage,
            .Dock = DockStyle.Fill,
            .TextAlign = ContentAlignment.MiddleCenter,
            .Font = New Drawing.Font(Font.FontFamily, 11),
            .AccessibleName = initialMessage,
            .AccessibleRole = AccessibleRole.StaticText,
            .TabStop = False
        }
        Controls.Add(_statusLabel)

        ' Focus reclaim — pulls focus back if WebView2 / Auth0 steals it.
        _focusTimer = New System.Windows.Forms.Timer() With {.Interval = 200}
        AddHandler _focusTimer.Tick, Sub(s, e)
                                         If Visible AndAlso Not ContainsFocus Then
                                             Activate()
                                         End If
                                     End Sub
        _focusTimer.Start()

        ' Escalation timer: raises the "Connection slow — keep waiting?" dialog
        ' every 60 s with diagnostic-rich text from the most recent profiler
        ' events. User chooses Keep waiting (resets the timer for another 60 s)
        ' or Cancel (same as Escape).
        ' Only run escalation + auto-cancel if a real cancel callback is wired.
        ' The legacy 1-arg constructor (used by the WPF dialog's brief SmartLink
        ' "Connecting to SmartLink..." popup) doesn't have a meaningful cancel
        ' path; running those timers there would surface a confusing dialog.
        _escalationTimer = New System.Windows.Forms.Timer() With {.Interval = EscalationIntervalMs}
        AddHandler _escalationTimer.Tick, AddressOf OnEscalationTick

        _autoCancelTimer = New System.Windows.Forms.Timer() With {.Interval = AutoCancelCeilingMs}
        AddHandler _autoCancelTimer.Tick, AddressOf OnAutoCancelTick

        If _cancelCallback IsNot Nothing Then
            _escalationTimer.Start()
            _autoCancelTimer.Start()
        End If

        _phaseStartTickMs = Environment.TickCount64

        If _profiler IsNot Nothing Then
            _profilerHandler = AddressOf OnProfilerEvent
            AddHandler _profiler.EventRecorded, _profilerHandler
        End If
    End Sub

    ''' <summary>
    ''' Backwards-compatible single-arg constructor (no profiler, no cancel).
    ''' Shouldn't be called by new code — keeps any stale call sites compiling.
    ''' </summary>
    Public Sub New(initialMessage As String)
        Me.New(ExtractRadioName(initialMessage), Nothing, Nothing)
    End Sub

    Private Shared Function ExtractRadioName(msg As String) As String
        If String.IsNullOrEmpty(msg) Then Return "radio"
        Const prefix = "Connecting to "
        If msg.StartsWith(prefix) Then
            Dim tail = msg.Substring(prefix.Length).TrimEnd("."c, " "c)
            Return If(String.IsNullOrEmpty(tail), "radio", tail)
        End If
        Return "radio"
    End Function

    ''' <summary>
    ''' Update the status message (thread-safe).
    ''' </summary>
    Public Sub UpdateStatus(message As String)
        If IsDisposed Then Return
        If InvokeRequired Then
            Try
                BeginInvoke(Sub() UpdateStatus(message))
            Catch
                ' Form may have closed mid-invoke — ignore.
            End Try
            Return
        End If
        _statusLabel.Text = message
        _statusLabel.AccessibleName = message
    End Sub

    ''' <summary>
    ''' Close the form (thread-safe).
    ''' </summary>
    Public Sub CloseForm()
        If IsDisposed Then Return
        If InvokeRequired Then
            Try
                BeginInvoke(Sub() CloseForm())
            Catch
            End Try
            Return
        End If
        StopTimers()
        Close()
    End Sub

    ' ── State-aware text + counting-earcon dispatch ───────────────────────

    Private Sub OnProfilerEvent(eventName As String, elapsedMs As Long, data As Dictionary(Of String, Object))
        ' This fires on whatever thread RecordEvent was called from (FlexLib
        ' receive thread, the Start() thread, etc.). Marshal to the modal's
        ' own message pump.
        If IsDisposed Then Return
        Try
            BeginInvoke(Sub() HandleProfilerEvent(eventName, elapsedMs, data))
        Catch
        End Try
    End Sub

    Private Sub HandleProfilerEvent(eventName As String, elapsedMs As Long, data As Dictionary(Of String, Object))
        If IsDisposed Then Return

        Select Case eventName
            Case "start_slices_available"
                EnterPhase(2, $"Connected to {_radioName}. Waiting for slice...")
            Case "start_antenna_available"
                EnterPhase(3, "Slice acquired. Setting up...")
            Case "station_name_set"
                ' Connection nearly complete — the openTheRadio caller will
                ' close us shortly. Don't change phase or earcon.
            Case "start_early_abort", "start_grace_abort"
                UpdateStatus("Connection slow, retrying...")
            Case "start_cancelled", "start_cancelled_in_station_wait"
                UpdateStatus("Cancelling...")
        End Select
    End Sub

    Private Sub EnterPhase(phase As Integer, text As String)
        If phase <= _currentPhase Then
            UpdateStatus(text)
            Return
        End If

        Dim phaseDurationMs = Environment.TickCount64 - _phaseStartTickMs
        Dim previousPhase = _currentPhase
        _currentPhase = phase
        _phaseStartTickMs = Environment.TickCount64
        UpdateStatus(text)

        ' Don't re-announce or earcon for fast common-case connects. If the
        ' previous phase took less than the threshold, the user gets silent
        ' progression — fast LAN connects stay unobtrusive.
        If phaseDurationMs < PhaseAnnounceThresholdMs Then Return
        If Not Radios.ScreenReaderOutput.SpeakConnectionProgressEnabled Then Return

        ' Phase announcement at default verbosity (Chatty). Critical-level
        ' events (errors, cancel, timeout) are spoken elsewhere with their
        ' own Critical level so they pierce verbosity-off.
        Try
            Radios.ScreenReaderOutput.Speak(text, VerbosityLevel.Chatty)
        Catch
        End Try

        ' Counting earcon for the new phase (1 / 1+1 / 1+1+1).
        Try
            JJFlexWpf.EarconPlayer.ConnectPhaseTone(phase)
        Catch ex As Exception
            Tracing.TraceLine("ConnectingForm: phase earcon failed: " & ex.Message, TraceLevel.Warning)
        End Try
    End Sub

    ' ── Cancel paths: Escape, X close, escalation, auto-cancel ────────────

    Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
        ' Escape ALWAYS cancels — no confirmation, no time gating, no waiting
        ' for the modal to escalate first. Aviation framing: quick decisive
        ' abort. Per project_no_silent_keystrokes_rule.md, the cancel speech
        ' is Critical so it pierces verbosity-off.
        If e.KeyCode = Keys.Escape Then
            e.Handled = True
            RequestCancel("Connection attempt cancelled")
            Return
        End If
        MyBase.OnKeyDown(e)
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        ' X close button (and Alt+F4) route through here. If the close came
        ' via our internal Close() after RequestCancel, _cancelHandled is true
        ' and we let the close proceed silently. Otherwise the user clicked X
        ' or pressed Alt+F4 — treat as cancel.
        If Not _cancelHandled AndAlso e.CloseReason = CloseReason.UserClosing Then
            e.Cancel = True
            RequestCancel("Connection attempt cancelled")
            Return
        End If
        MyBase.OnFormClosing(e)
    End Sub

    Protected Overrides Sub OnFormClosed(e As FormClosedEventArgs)
        StopTimers()
        If _profiler IsNot Nothing AndAlso _profilerHandler IsNot Nothing Then
            Try
                RemoveHandler _profiler.EventRecorded, _profilerHandler
            Catch
            End Try
        End If
        MyBase.OnFormClosed(e)
    End Sub

    Private Sub RequestCancel(announcement As String)
        If _cancelHandled Then Return
        _cancelHandled = True
        StopTimers()

        Try
            Radios.ScreenReaderOutput.Speak(announcement, VerbosityLevel.Critical, True)
        Catch
        End Try

        UpdateStatus(announcement & "...")

        Try
            _cancelCallback?.Invoke()
        Catch ex As Exception
            Tracing.TraceLine("ConnectingForm: cancel callback threw: " & ex.Message, TraceLevel.Error)
        End Try

        ' Give Start() a moment to observe the flag and exit, then close.
        ' Start polls every 25 ms, so 250 ms is comfortably enough; the
        ' openTheRadio failure path will also call CloseForm if we're slower.
        Dim closeTimer = New System.Windows.Forms.Timer() With {.Interval = 250}
        AddHandler closeTimer.Tick, Sub(s, e2)
                                        closeTimer.Stop()
                                        closeTimer.Dispose()
                                        Try : Close() : Catch : End Try
                                    End Sub
        closeTimer.Start()
    End Sub

    Private Sub StopTimers()
        Try : _focusTimer?.Stop() : Catch : End Try
        Try : _escalationTimer?.Stop() : Catch : End Try
        Try : _autoCancelTimer?.Stop() : Catch : End Try
    End Sub

    ' ── 60 s escalation ───────────────────────────────────────────────────

    Private Sub OnEscalationTick(sender As Object, e As EventArgs)
        If _cancelHandled OrElse IsDisposed Then Return
        If _escalationActive Then Return ' Already showing an escalation dialog.
        _escalationActive = True

        Try
            Dim diagnostic = BuildDiagnosticMessage()
            Dim prompt = $"Connection slow — {diagnostic}{Environment.NewLine}{Environment.NewLine}Keep waiting, or cancel?"

            ' Critical-level speech so the user hears the prompt even at off
            ' verbosity. Then show a modal MessageBox owned by us so it
            ' inherits topmost.
            Try
                Radios.ScreenReaderOutput.Speak("Connection slow. Keep waiting, or cancel?", VerbosityLevel.Critical, True)
            Catch
            End Try

            Dim result = MessageBox.Show(Me, prompt, "Connection slow",
                                         MessageBoxButtons.YesNo,
                                         MessageBoxIcon.Question,
                                         MessageBoxDefaultButton.Button1)
            If result = DialogResult.No Then
                ' Equivalent to Escape: cancel.
                RequestCancel("Connection attempt cancelled")
                Return
            End If

            ' Keep waiting — restart the 60 s clock for the next escalation.
            Try
                _escalationTimer.Stop()
                _escalationTimer.Start()
            Catch
            End Try
        Finally
            _escalationActive = False
        End Try
    End Sub

    Private Function BuildDiagnosticMessage() As String
        ' Pull the most informative recent ConnectionProfiler event and render
        ' a friendly description. Pairs with project_trace_persistence_design.md
        ' (same diagnostic surface, different rendering target).
        If _profiler Is Nothing Then
            Return "still trying to reach " & _radioName & "."
        End If

        Try
            Dim events = _profiler.GetEvents()
            If events Is Nothing OrElse events.Count = 0 Then
                Return "still trying to reach " & _radioName & "."
            End If

            ' Walk events in reverse for the most informative recent one.
            For i = events.Count - 1 To 0 Step -1
                Dim ev = events(i)
                Select Case ev.Event
                    Case "start_grace_abort", "start_early_abort"
                        Return $"client registration with {_radioName} keeps dropping during setup."
                    Case "station_name_timeout"
                        Return $"{_radioName} hasn't sent its station name yet — SmartLink may be slow."
                    Case "start_connection_lost"
                        Return $"connection to {_radioName} dropped during setup."
                    Case "start_station_name_wait_begin"
                        Return $"{_radioName} hasn't acknowledged the slice yet."
                    Case "start_antenna_available"
                        Return $"slice acquired but {_radioName} hasn't finished setup."
                    Case "start_slices_available"
                        Return $"{_radioName} responded but is taking a long time to assign a slice."
                    Case "connect_call_end"
                        Return $"transport connected to {_radioName} but the radio is slow to respond."
                    Case "connect_call_begin"
                        Return $"still trying to reach {_radioName}."
                End Select
            Next

            Return "still trying to reach " & _radioName & "."
        Catch ex As Exception
            Tracing.TraceLine("ConnectingForm: BuildDiagnosticMessage threw: " & ex.Message, TraceLevel.Warning)
            Return "still trying to reach " & _radioName & "."
        End Try
    End Function

    ' ── 5-minute auto-cancel ceiling ──────────────────────────────────────

    Private Sub OnAutoCancelTick(sender As Object, e As EventArgs)
        If _cancelHandled OrElse IsDisposed Then Return
        Tracing.TraceLine("ConnectingForm: 5-minute auto-cancel ceiling reached", TraceLevel.Warning)
        Try
            Radios.ConnectionProfiler.Current?.RecordEvent("auto_cancel_ceiling_reached", Nothing)
        Catch
        End Try
        RequestCancel("Connection attempt timed out — cancelled")
    End Sub

End Class
