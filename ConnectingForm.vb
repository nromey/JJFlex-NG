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
''' Task #212 (2026-08-26): the phase announcements named the MOMENTS and
''' nothing covered the WAITS between them, which is where all the time goes.
''' The decision about what to say, and when, now lives in
''' Radios.ConnectNarrator so it can be tested without a radio; this class is
''' the adapter that applies each step to a label, a voice and an earcon.
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
    Private ReadOnly _narrator As Radios.ConnectNarrator
    ''' <summary>The heartbeat currently covering this wait, so the escalation
    ''' prompt can silence it and put it back if the operator keeps waiting.</summary>
    Private _armedVoice As Radios.ConnectWaitVoice
    Private _cancelHandled As Boolean = False
    ''' <summary>True when WE closed the form because the connect finished.
    ''' Distinguishes our own Close() from the operator pressing X or Alt+F4,
    ''' which WinForms reports identically as CloseReason.UserClosing.</summary>
    Private _programmaticClose As Boolean = False
    Private _escalationActive As Boolean = False

    Private Const EscalationIntervalMs As Integer = 60_000      ' 60 s
    Private Const AutoCancelCeilingMs As Integer = 300_000       ' 5 min

    ''' <summary>
    ''' Construct the connecting modal. Caller passes the radio's display name
    ''' and the cancel callback (typically <c>RigControl.RequestCancel</c>).
    ''' Optionally wires up to a ConnectionProfiler so the modal updates text
    ''' as the connection moves through phases.
    ''' </summary>
    ''' <param name="lead">
    ''' The sentence the radio picker spoke as it handed the connect on — see
    ''' <c>RigSelectorDialog.SelectedConnectingLine</c>, which composes it and is
    ''' the only place its wording lives. It becomes this window's opening line
    ''' so the operator gets it even though the picker's own utterance was made
    ''' across a window change and may have been flushed. Nothing to carry means
    ''' the plain form as before. Task #93.
    ''' </param>
    Public Sub New(radioName As String, cancelCallback As Action, profiler As Radios.ConnectionProfiler,
                   Optional lead As String = Nothing)
        _radioName = If(radioName, Radios.Lexicon.Get("connect.connecting.default_radio_name"))
        _cancelCallback = cancelCallback
        _profiler = profiler

        Text = Radios.Lexicon.Get("connect.connecting.title")
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

        AccessibleName = Radios.Lexicon.Get("connect.connecting.title")
        AccessibleRole = AccessibleRole.Dialog

        ' The lead is already a finished sentence, so it is carried whole rather
        ' than having the plain form's trailing ellipsis bolted onto its end.
        Dim initialMessage = If(String.IsNullOrWhiteSpace(lead),
                                Radios.Lexicon.Get("connect.connecting.initial", ("radioName", _radioName)),
                                Radios.Lexicon.Get("connect.connecting.initial_lead", ("lead", lead.Trim())))
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

        ' Focus reclaim — pulls focus back from stray windows. Yields while a
        ' sign-in window is open (round 26 armistice, 2026-08-06): this timer
        ' was built in an earlier round to steal focus back from the Auth0
        ' browser window, and it dutifully fought the sign-in dialog's own
        ' focus watchdog four times a second — "sm connec sm connec sm connec
        ' ... connecting" heard live. Sign-in windows are friendly now; the
        ' operator's keyboard belongs in them.
        '
        ' #331 widened the stand-down from sign-in windows to EVERY modal we
        ' raise, and made it cover z-order as well as focus. Standing the timer
        ' down alone is not enough: this form is TopMost, so a message box owned
        ' by the shell sits UNDERNEATH it however the focus argument comes out.
        ' A screen reader would still read the dialog, which is the half that
        ' matters most here — but a sighted helper looking at the same screen
        ' would see a connecting box and no explanation at all.
        '
        ' Our own escalation prompt is the exception, and it is checked first:
        ' it is owned by this form specifically so it INHERITS topmost, so
        ' dropping topmost for it would undo the thing that makes it reachable.
        ' _escalationActive is already the flag for "our own prompt is up".
        _focusTimer = New System.Windows.Forms.Timer() With {.Interval = 200}
        AddHandler _focusTimer.Tick, Sub(s, e)
                                         If _escalationActive Then Return

                                         Dim standDown = Radios.WindowFocusForcer.FocusReclaimShouldYield
                                         If TopMost = standDown Then TopMost = Not standDown

                                         If Visible AndAlso Not ContainsFocus AndAlso Not standDown Then
                                             Activate()
                                         End If
                                     End Sub
        _focusTimer.Start()

        ' Escalation timer: raises the "Connection slow — keep waiting?" dialog
        ' every 60 s with diagnostic-rich text from the most recent profiler
        ' events. User chooses Keep waiting (resets the timer for another 60 s)
        ' or Cancel (same as Escape).
        ' Only run escalation + auto-cancel if a real cancel callback is wired.
        ' The account-pass constructor below passes none — the WPF picker's brief
        ' SmartLink passes have no meaningful cancel path, and running those
        ' timers there would surface a confusing dialog.
        _escalationTimer = New System.Windows.Forms.Timer() With {.Interval = EscalationIntervalMs}
        AddHandler _escalationTimer.Tick, AddressOf OnEscalationTick

        _autoCancelTimer = New System.Windows.Forms.Timer() With {.Interval = AutoCancelCeilingMs}
        AddHandler _autoCancelTimer.Tick, AddressOf OnAutoCancelTick

        If _cancelCallback IsNot Nothing Then
            _escalationTimer.Start()
            _autoCancelTimer.Start()
        End If

        _narrator = New Radios.ConnectNarrator(_radioName)

        If _profiler IsNot Nothing Then
            _profilerHandler = AddressOf OnProfilerEvent
            AddHandler _profiler.EventRecorded, _profilerHandler
        End If

        ' The connect leg runs BEFORE the first profiler event this form cares
        ' about. Over SmartLink that stretch is a session, a sign-in, a hole
        ' punch and a TLS handshake, and until #212 nothing said a word during
        ' any of it. Armed here, at the moment the window appears, so the clock
        ' runs from when the operator started waiting rather than from whenever
        ' the radio first answers.
        '
        ' ONLY FOR A REAL RADIO CONNECT, which is what having a profiler means.
        ' The picker's SmartLink account passes put this same window up, and they
        ' are not connects — this narrator's lines are all about a radio and its
        ' setup phases. They bring their OWN heartbeat instead, through the
        ' account-pass constructor below (task #294).
        If _profiler IsNot Nothing Then
            _armedVoice = _narrator.OpeningVoice()
            ArmWaitVoice(_armedVoice)
        End If
    End Sub

    ''' <summary>
    ''' This window for a wait that is NOT a radio connect — the picker's
    ''' SmartLink account passes.
    ''' </summary>
    ''' <param name="statusLine">
    ''' The finished sentence describing the operation, composed by whoever
    ''' started it. Same contract as <c>lead</c> above: the caller owns the
    ''' wording because the caller is the only thing that knows what is
    ''' happening.
    ''' </param>
    ''' <param name="waitVoice">
    ''' The heartbeat to run while the pass does. Nothing for a wait that should
    ''' stay silent.
    ''' </param>
    ''' <remarks>
    ''' <para><b>This replaces a constructor that SCRAPED ITS SUBJECT out of a
    ''' status string (task #294).</b> The old one-argument overload took
    ''' "Connecting to SmartLink..." and ran a private <c>ExtractRadioName</c>
    ''' over it looking for a "Connecting to " prefix, so the window believed
    ''' the radio it was connecting to was called "SmartLink" — or, when the
    ''' prefix did not match, "radio".</para>
    ''' <para>That is why the picker's refresh passes were SILENT. The #212
    ''' progress heartbeat had to be gated off wherever the scrape was in play,
    ''' because it would have announced "Still connecting to radio." during an
    ''' operation that is not a connect and has no radio — so the operator
    ''' pressed something, an account refresh ran for seconds, and nothing said
    ''' it was happening. A blind operator has no spinner: a refresh that takes
    ''' several seconds and says nothing is indistinguishable from one that did
    ''' nothing.</para>
    ''' <para>The silence was the symptom; the borrowing was the defect. A
    ''' window that gets its subject by parsing a sentence cannot be relied on
    ''' to name anything, and every future reuse inherits that. So the subject
    ''' is passed in, and there is no longer a constructor that can guess.</para>
    ''' <para>Still ONE window rather than a second progress surface. What this
    ''' window does here — hold the foreground while SmartLink auth may raise a
    ''' browser, and give the wait a voice — is identical for both operations.
    ''' Only the words differ, and words are what a caller supplies.</para>
    ''' </remarks>
    Public Sub New(statusLine As String, waitVoice As Radios.ConnectWaitVoice)
        ' No radio name, no cancel callback and no profiler: this is not a
        ' connect. Without a cancel callback the escalation and auto-cancel
        ' timers stay parked, which is what the old overload relied on too —
        ' there is nothing here for a "Connection slow — keep waiting?" prompt
        ' to cancel.
        Me.New(Nothing, Nothing, Nothing, statusLine)

        _armedVoice = waitVoice
        ArmWaitVoice(waitVoice)
    End Sub

    ''' <summary>
    ''' When a sign-in window is already up as this form appears, don't take
    ''' focus even once on Show — the half-second-later focus squash was the
    ''' whole round 25 problem. The user's keyboard stays in the sign-in form.
    '''
    ''' <para>#331 widened this from sign-in windows to any modal of ours that
    ''' is already in front of the operator, for the same reason and by the same
    ''' rule the reclaim timer now uses. One question, asked in both places.</para>
    ''' </summary>
    Protected Overrides ReadOnly Property ShowWithoutActivation As Boolean
        Get
            Return Radios.WindowFocusForcer.FocusReclaimShouldYield
        End Get
    End Property

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
    ''' <summary>
    ''' Dismiss the form because the connect FINISHED — success or a failure the
    ''' caller is already reporting. Not a cancellation.
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
        ' MUST be set before Close(). WinForms reports a programmatic
        ' Form.Close() as CloseReason.UserClosing - indistinguishable from the
        ' X button - so without this flag OnFormClosing treated every
        ' SUCCESSFUL connect as the operator cancelling: it spoke "Connection
        ' attempt cancelled" at Critical over the real announcements, cancelled
        ' the close, and invoked the cancel callback. Found 2026-08-17 in a
        ' verbose speech trace, sitting between "Connected to Unknown" and
        ' "Connected to FLEX-8600".
        _programmaticClose = True
        Close()
    End Sub

    ' ── The progress heartbeat ────────────────────────────────────────────
    '
    ' ProgressVoice is the mechanism discovery already uses, and #212 asked for
    ' the connect walk to use the same one rather than grow a second. Two
    ' details are load-bearing:
    '
    '   * Armed with NO OPENING LINE. Whatever just changed phase has already
    '     said what is happening; the heartbeat exists only to keep saying
    '     "still". ProgressVoice treats an empty opening as nothing to speak, so
    '     the first word arrives one repeat interval in — which is why a fast
    '     LAN connect, whose phases are sub-second, never hears any of this.
    '
    '   * STOPPED whenever the wait it covered ends, including on the way out
    '     through cancel and close. A heartbeat that outlives its wait is worse
    '     than the silence it replaced: it reassures an operator about work that
    '     finished or failed.

    Private Sub ArmWaitVoice(voice As Radios.ConnectWaitVoice)
        If voice Is Nothing Then Return
        If Not Radios.ScreenReaderOutput.SpeakConnectionProgressEnabled Then Return
        Try
            Radios.ProgressVoice.Start(voice.What,
                                       Nothing, Nothing,
                                       voice.StillTerse, voice.StillChatty,
                                       voice.RepeatMs, voice.MaxMs)
        Catch ex As Exception
            Tracing.TraceLine("ConnectingForm: progress voice failed to start: " & ex.Message, TraceLevel.Warning)
        End Try
    End Sub

    Private Shared Sub StopWaitVoice(reason As String)
        Try
            Radios.ProgressVoice.Stop(reason)
        Catch
        End Try
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

        Dim step_ = _narrator.OnEvent(eventName, data)
        If step_ Is Nothing OrElse step_.IsEmpty Then Return

        If step_.StopVoice Then StopWaitVoice("connect: " & eventName)

        If step_.StatusText IsNot Nothing Then UpdateStatus(step_.StatusText)

        ' Phase announcements ride at Chatty. Critical-level events (errors,
        ' cancel, timeout) are spoken elsewhere at Critical so they pierce
        ' verbosity-off.
        If step_.Speak AndAlso step_.StatusText IsNot Nothing _
           AndAlso Radios.ScreenReaderOutput.SpeakConnectionProgressEnabled Then
            Try
                Radios.ScreenReaderOutput.Speak(step_.StatusText, VerbosityLevel.Chatty)
            Catch
            End Try
        End If

        ' The reason a connect died, said HERE rather than after this window is
        ' torn down. Critical: a failed connect is a state change the operator
        ' has to hear whatever their verbosity, and it is not "connection
        ' progress" they can have switched off.
        If step_.SpeakExtra IsNot Nothing Then
            Try
                Radios.ScreenReaderOutput.Speak(step_.SpeakExtra, VerbosityLevel.Critical, True)
            Catch
            End Try
        End If

        ' Counting earcon for the new phase (1 / 1+1 / 1+1+1).
        If step_.PlayPhaseTone Then
            Try
                JJFlexWpf.EarconPlayer.ConnectPhaseTone(step_.Phase)
            Catch ex As Exception
                Tracing.TraceLine("ConnectingForm: phase earcon failed: " & ex.Message, TraceLevel.Warning)
            End Try
        End If

        ' Armed LAST, so the heartbeat's clock starts after whatever this event
        ' had to say rather than racing it.
        If step_.Arm IsNot Nothing Then
            _armedVoice = step_.Arm
            ArmWaitVoice(step_.Arm)
        ElseIf step_.StopVoice Then
            _armedVoice = Nothing
        End If
    End Sub

    ' ── Cancel paths: Escape, X close, escalation, auto-cancel ────────────

    Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
        ' Escape ALWAYS cancels — no confirmation, no time gating, no waiting
        ' for the modal to escalate first. Aviation framing: quick decisive
        ' abort. Per project_no_silent_keystrokes_rule.md, the cancel speech
        ' is Critical so it pierces verbosity-off.
        If e.KeyCode = Keys.Escape Then
            e.Handled = True
            RequestCancel(Radios.Lexicon.Get("connect.connecting.cancelled"))
            Return
        End If
        MyBase.OnKeyDown(e)
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        ' X close button (and Alt+F4) route through here. If the close came
        ' via our internal Close() after RequestCancel, _cancelHandled is true
        ' and we let the close proceed silently. Otherwise the user clicked X
        ' or pressed Alt+F4 — treat as cancel.
        If Not _cancelHandled AndAlso Not _programmaticClose _
           AndAlso e.CloseReason = CloseReason.UserClosing Then
            e.Cancel = True
            RequestCancel(Radios.Lexicon.Get("connect.connecting.cancelled"))
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

        UpdateStatus(Radios.Lexicon.Get("connect.connecting.cancel_status", ("announcement", announcement)))

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

    ''' <summary>
    ''' Everything that is running on a clock, stopped. The one funnel every
    ''' exit already goes through — CloseForm, OnFormClosed and RequestCancel —
    ''' which is why the progress heartbeat is stopped here rather than at three
    ''' call sites that would each have to remember.
    ''' </summary>
    Private Sub StopTimers()
        Try : _focusTimer?.Stop() : Catch : End Try
        Try : _escalationTimer?.Stop() : Catch : End Try
        Try : _autoCancelTimer?.Stop() : Catch : End Try
        StopWaitVoice("connecting window finished")
    End Sub

    ' ── 60 s escalation ───────────────────────────────────────────────────

    Private Sub OnEscalationTick(sender As Object, e As EventArgs)
        If _cancelHandled OrElse IsDisposed Then Return
        If _escalationActive Then Return ' Already showing an escalation dialog.
        _escalationActive = True

        ' Nothing reassuring should be spoken over a question. The heartbeat is
        ' answering "is this still happening"; the prompt about to appear asks
        ' the operator to decide whether it should be, and the two talking at
        ' once would make the decision harder rather than easier.
        StopWaitVoice("escalation prompt open")

        Try
            Dim diagnostic = BuildDiagnosticMessage()
            Dim prompt = Radios.Lexicon.Get("connect.connecting.slow_prompt",
                                            ("diagnostic", diagnostic), ("newline", Environment.NewLine))

            ' Critical-level speech so the user hears the prompt even at off
            ' verbosity. Then show a modal MessageBox owned by us so it
            ' inherits topmost.
            Try
                Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.connecting.slow_speech"), VerbosityLevel.Critical, True)
            Catch
            End Try

            Dim result = MessageBox.Show(Me, prompt, Radios.Lexicon.Get("connect.connecting.slow_title"),
                                         MessageBoxButtons.YesNo,
                                         MessageBoxIcon.Question,
                                         MessageBoxDefaultButton.Button1)
            If result = DialogResult.No Then
                ' Equivalent to Escape: cancel.
                RequestCancel(Radios.Lexicon.Get("connect.connecting.cancelled"))
                Return
            End If

            ' Keep waiting — restart the 60 s clock for the next escalation, and
            ' put the heartbeat back. The operator has just said they want this
            ' to continue, which makes "is it still going" their live question
            ' again.
            Try
                _escalationTimer.Stop()
                _escalationTimer.Start()
            Catch
            End Try
            ArmWaitVoice(_armedVoice)
        Finally
            _escalationActive = False
        End Try
    End Sub

    Private Function BuildDiagnosticMessage() As String
        ' Pull the most informative recent ConnectionProfiler event and render
        ' a friendly description. Pairs with project_trace_persistence_design.md
        ' (same diagnostic surface, different rendering target).
        If _profiler Is Nothing Then
            Return Radios.Lexicon.Get("connect.connecting.diag_default", ("radioName", _radioName))
        End If

        Try
            Dim events = _profiler.GetEvents()
            If events Is Nothing OrElse events.Count = 0 Then
                Return Radios.Lexicon.Get("connect.connecting.diag_default", ("radioName", _radioName))
            End If

            ' Walk events in reverse for the most informative recent one.
            For i = events.Count - 1 To 0 Step -1
                Dim ev = events(i)
                Select Case ev.Event
                    Case "start_grace_abort", "start_early_abort"
                        Return Radios.Lexicon.Get("connect.connecting.diag_grace_abort", ("radioName", _radioName))
                    Case "station_name_timeout"
                        Return Radios.Lexicon.Get("connect.connecting.diag_station_name_timeout", ("radioName", _radioName))
                    Case "start_connection_lost"
                        Return Radios.Lexicon.Get("connect.connecting.diag_connection_lost", ("radioName", _radioName))
                    Case "start_station_name_wait_begin"
                        Return Radios.Lexicon.Get("connect.connecting.diag_station_name_wait", ("radioName", _radioName))
                    Case "start_antenna_available"
                        Return Radios.Lexicon.Get("connect.connecting.diag_antenna_available", ("radioName", _radioName))
                    Case "start_slices_available"
                        Return Radios.Lexicon.Get("connect.connecting.diag_slices_available", ("radioName", _radioName))
                    Case "connect_call_end"
                        Return Radios.Lexicon.Get("connect.connecting.diag_call_end", ("radioName", _radioName))
                    Case "connect_call_begin"
                        Return Radios.Lexicon.Get("connect.connecting.diag_default", ("radioName", _radioName))
                End Select
            Next

            Return Radios.Lexicon.Get("connect.connecting.diag_default", ("radioName", _radioName))
        Catch ex As Exception
            Tracing.TraceLine("ConnectingForm: BuildDiagnosticMessage threw: " & ex.Message, TraceLevel.Warning)
            Return Radios.Lexicon.Get("connect.connecting.diag_default", ("radioName", _radioName))
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
        RequestCancel(Radios.Lexicon.Get("connect.connecting.timed_out"))
    End Sub

End Class
