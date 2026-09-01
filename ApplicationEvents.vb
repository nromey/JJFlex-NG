Imports System.Net
Imports System.Security.Authentication
Imports System.Windows.Forms
Imports Radios

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
            ' The Startup thread IS the UI thread: this same thread goes on to
            ' create ShellForm and run the message loop. Record its identity
            ' before anything can connect, so RunConnectPhaseOffUiThread can
            ' recognise the UI thread even while there is no message loop to
            ' infer it from — which is the whole of startup, including the
            ' first connect. Inferring it from Application.MessageLoop is what
            ' ran the 2026-08-29 startup connect inline and froze the app for
            ' three 45-second station-name waits (#402).
            UiThreadId = Environment.CurrentManagedThreadId

            ' Take the framework's DefaultTraceListener out of Trace.Listeners
            ' before anything can write a line. Its only output is
            ' OutputDebugStringW, which nobody reads unless a debugger is
            ' attached - and which waits up to 10,000 ms on DBWIN_BUFFER_READY
            ' when a debug monitor has registered and stopped servicing the
            ' buffer. That is #434: a launch where every single trace line cost
            ' exactly ten seconds, so the main window was about 85 minutes away
            ' and never appeared. JJTrace's own static constructor does this
            ' too; the explicit call is here because other assemblies call
            ' System.Diagnostics.Trace.WriteLine directly and this is the first
            ' line of the process.
            JJTrace.Tracing.DetachDefaultListener()

            ' Initialize native library resolver FIRST (enables x86/x64 DLL loading)
            NativeLoader.Initialize()

            ' Enforce a modern TLS floor for all outbound HTTPS/TLS traffic in the app domain.
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 Or SecurityProtocolType.Tls13

            ' Enable global crash capture and reporting.
            AddHandler System.Windows.Forms.Application.ThreadException, AddressOf CrashReporter.OnThreadException
            AddHandler AppDomain.CurrentDomain.UnhandledException, AddressOf CrashReporter.OnUnhandledException

            ' #171 silent verification channel - set the render/record switches
            ' BEFORE any output subsystem initializes, because ScreenReaderOutput
            ' and EarconPlayer both consult RenderEnabled to decide whether to
            ' bring up Prism and open audio devices at all. Two independent
            ' switches, deliberately not a mode: render off alone is the fast
            ' automated path (nothing sounds, nothing steals the operator's
            ' screen reader); record on alone transcribes exactly what a human
            ' hears; both together give a "that sounded wrong" report its event
            ' stream. The parse itself lives in ParseStartupSwitches - one
            ' source of truth, because Application.Designer.vb also parses at
            ' construction time to exempt render-off instances from
            ' single-instance forwarding. Configure writes the session-start
            ' marker the instant the transcript opens - a reader treats a
            ' transcript without that marker as a broken instrument, never as
            ' "no output".
            Dim outputSwitches = Radios.OutputChannelRecorder.ParseStartupSwitches(e.CommandLine)

            ' The operator can also ask for a transcript from Settings >
            ' Diagnostics, for when they can reproduce a problem with what the
            ' app SAID. Read here rather than in GetConfigInfo because that runs
            ' after the main window loads, which is far too late — the greeting,
            ' the whole connect walk and any startup complaint would be missing
            ' from the very transcript somebody is collecting to explain them.
            '
            ' Readable this early only because RadioConfig.AppDataRoot resolves
            ' from the environment rather than from startup state.
            '
            ' OR, never AND: a --record switch or JJFLEX_RECORD=1 already won,
            ' and a saved preference must never be able to silence a harness.
            Try
                If Not outputSwitches.Record Then
                    outputSwitches.Record =
                        Radios.DiagnosticsConfig.Load(Radios.RadioConfig.AppDataRoot).RecordSpokenOutput
                End If
            Catch ex As Exception
                ' A transcript is a diagnostic aid. Failing to read the
                ' preference must never stop the app from starting.
                JJTrace.Tracing.TraceLine("Startup: could not read RecordSpokenOutput: " & ex.Message,
                                  TraceLevel.Warning)
            End Try

            Radios.OutputChannelRecorder.Configure(outputSwitches.Render, outputSwitches.Record, outputSwitches.RecordPath)

            ' Initialize screen reader output (Prism) for accessibility announcements.
            Radios.ScreenReaderOutput.Initialize()

            ' Put the operator's saved verbosity in force BEFORE anything is
            ' spoken. The full audio config is not applied until MainWindow
            ' exists, which is after connect - so without this, every word of
            ' startup came out at the Chatty default however the setting was
            ' left. Verbosity is the one setting that has to be live before the
            ' first utterance, because it decides whether there is one.
            JJFlexWpf.AudioOutputConfig.ApplySpeechVerbosityEarly()

            ' Greet at launch, which is where a greeting belongs. The arrival
            ' announcement at Home is a separate message saying where you landed
            ' and in which tuning mode - see MainWindow.SpeakWelcome.
            '
            ' Queued, so the connect dialog announcing itself lands behind this
            ' rather than cutting it off. Before the intent enum existed there
            ' was no way to express that, which is why the old single message
            ' carried a 2-second sleep instead.
            Radios.ScreenReaderOutput.SpeakGreeting()

            ' Give the launch wait a voice. Measured 2026-08-24 from three
            ' consecutive speech transcripts: 5.6, 6.0 and 6.0 seconds between
            ' this greeting and the first word about radios, with nothing said
            ' in between. Six seconds of silence is indistinguishable from a
            ' hang, and a blind operator has no filling window to watch.
            '
            ' Stopped by DiscoveringRadiosWindow when it announces itself, and
            ' by the auto-connect path, which are the two ways this wait ends.
            ' It also has its own ceiling, so neither of them failing to call
            ' Stop can leave it talking.
            Radios.ProgressVoice.Start(
                "startup discovery",
                "Starting up.",
                "Starting up, looking for radios on your network.",
                "Still looking.",
                "Still looking for radios.")

            ' Initialize NAudio-based earcon player for UI sound effects.
            ' Traced with elapsed time: this and the two after it sit inside
            ' the measured launch gap and none of them said anything about how
            ' long they took, which is why the six seconds could be measured
            ' but not attributed. See task #215.
            Dim phaseClock = Stopwatch.StartNew()
            JJFlexWpf.EarconPlayer.Initialize()
            JJTrace.Tracing.TraceLine($"Startup phase: EarconPlayer.Initialize took {phaseClock.ElapsedMilliseconds} ms", TraceLevel.Info)

            ' #321 - if prism.dll did not load we have NO speech, and until now
            ' the app said so only in the trace, on Help > About and in crash
            ' bundles. All three are things you read; the operator this fires
            ' for is a blind operator whose application has just gone silent.
            '
            ' Two channels, and each does a different job. The earcon does not
            ' touch the speech stack at all, so it survives whatever broke - it
            ' is the alarm. The dialog is the explanation, and it is genuinely
            ' reachable: the operator's screen reader is NOT the broken part,
            ' our bridge to it is, so NVDA or JAWS reads an ordinary Windows
            ' dialog through the platform exactly as it always does.
            '
            ' Raised HERE, before ShellForm exists, so nothing of ours can sit
            ' on top of it - which is the trap #331 records on the connect path.
            RaiseSpeechFailureAlertIfNeeded()

            ' Initialize compiled help file launcher.
            phaseClock.Restart()
            JJFlexWpf.HelpLauncher.Initialize()
            JJTrace.Tracing.TraceLine($"Startup phase: HelpLauncher.Initialize took {phaseClock.ElapsedMilliseconds} ms", TraceLevel.Info)

            ' Purge connection profiles older than 7 days.
            phaseClock.Restart()
            Radios.ConnectionProfiler.PurgeOldProfiles()
            JJTrace.Tracing.TraceLine($"Startup phase: PurgeOldProfiles took {phaseClock.ElapsedMilliseconds} ms", TraceLevel.Info)

            ' ── Create the ShellForm and get the WPF content ───────────────
            ' We create ShellForm here (before OnCreateMainForm) so we can wire
            ' callbacks. OnCreateMainForm will use the same instance.
            phaseClock.Restart()
            TheShellForm = New ShellForm()
            WpfMainWindow = TheShellForm.WpfContent
            JJTrace.Tracing.TraceLine($"Startup phase: ShellForm + WPF content took {phaseClock.ElapsedMilliseconds} ms", TraceLevel.Info)

            ' Wire WPF dispatcher exception capture. WPF runs inside ElementHost
            ' so there is no System.Windows.Application.Current to subscribe to;
            ' we attach to the WpfMainWindow's Dispatcher instead. Without this,
            ' WPF dispatcher exceptions (event handlers, Dispatcher.BeginInvoke
            ' callbacks, deferred work) would fall through to the AppDomain
            ' handler and terminate the process. With it, we save the crash and
            ' set e.Handled = True so the app stays alive — same soft-recover
            ' behaviour as the WinForms ThreadException handler above.
            AddHandler WpfMainWindow.Dispatcher.UnhandledException, AddressOf CrashReporter.OnDispatcherUnhandledException

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

            ' CW message manager (#329). CWText is created when the current
            ' operator is set, so it can be Nothing here and the menu handler
            ' has to cope - which it does by saying so rather than by doing
            ' nothing, since a menu item that silently declines is the defect
            ' this whole finding is about.
            WpfMainWindow.ManageCWMessagesCallback =
                Sub()
                    If CWText Is Nothing Then
                        Radios.ScreenReaderOutput.Speak(
                            Radios.Lexicon.Get("settings.cw.no_operator_for_messages"),
                            Radios.VerbosityLevel.Critical, interrupt:=True)
                        Return
                    End If
                    CWText.Manage()
                End Sub

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
            ' Settings' Audio tab opens the same picker and needs the same path,
            ' with or without a radio connected. globals owns this string; hand
            ' it over rather than letting the WPF side rebuild it from parts.
            WpfMainWindow.AudioDevicesFilePath = AudioDevicesFile

            ' Wire the operator manager for the Radio menu (QB Track A stub
            ' audit). Same Lister-over-Operators surface the app raises at
            ' first run when no operator exists; the Operators.ConfigEvent
            ' handler picks up any operator change the dialog makes.
            WpfMainWindow.ShowOperatorsCallback = Sub()
                If Operators Is Nothing Then
                    Radios.ScreenReaderOutput.Speak(Radios.Lexicon.Get("connect.startup.operators_not_loaded"), Radios.VerbosityLevel.Critical, True)
                    Return
                End If
                Lister.TheList = Operators
                Lister.ShowDialog()
            End Sub

            ' QB Track H (2026-08-07): the legacy ShowKeysDialog/SetupKeysDialog
            ' callbacks (GetKeyActionsCallback / GetAvailableActionsCallback) are
            ' retired with the dialogs. The Keys surface reads the registry
            ' directly through KeyCommandsRef.
            WpfMainWindow.KeyCommandsRef = Commands

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
                ' Informational rows (field keys, universal Home keys, filter
                ' chords, leader commands, PTT, and the TX-slice / power /
                ' antenna door rows) come from the KeyInventory table — the
                ' same data that drives the '?' handler, per-field help, and
                ' the key manifest. QB Track H (2026-08-07): the hand-built
                ' list that lived here had drifted from the real handlers.
                ' QB Track L: Track I's four inline door rows moved into
                ' KeyInventory.FinderDoors.
                result.AddRange(JJFlexWpf.KeyInventory.CommandFinderItems())
                Return result
            End Function

            WpfMainWindow.ExecuteCommandCallback = Sub(tag)
                If TypeOf tag Is CommandValues Then
                    Dim cmdId = DirectCast(tag, CommandValues)
                    Dim kt = Commands.Lookup(cmdId)
                    If kt IsNot Nothing AndAlso kt.Handler IsNot Nothing Then
                        ' Radio/Classic/Modern-scope commands need a connected radio.
                        ' Without one, announce "no radio connected" instead of letting
                        ' the handler go silent. Global scope works without a radio by
                        ' definition; Logging scope has its own upstream guard.
                        ' RunsWithoutRadio opt-out: a small locked list of commands
                        ' (SetFreq, ShowMemory) work meaningfully with no radio; for
                        ' those, fall through to the handler.
                        If RigControl Is Nothing AndAlso
                           Not kt.RunsWithoutRadio AndAlso
                           (kt.Scope = Radios.KeyScope.Radio OrElse
                            kt.Scope = Radios.KeyScope.Classic OrElse
                            kt.Scope = Radios.KeyScope.Modern) Then
                            Radios.ScreenReaderOutput.SpeakNoRadioConnected(kt.ShortActionLabel)
                            Return
                        End If

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
                ' GetMemoryMode / SetMemoryMode removed with AdjustVFO (#357).
                ' The only writer was a key handler no keystroke could reach,
                ' and the global they wrote to had no reader anywhere.
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
                    ' #49 family: a corrupt filter preset file is sidelined by
                    ' Load (never overwritten by the next save) and announced,
                    ' instead of silently becoming the defaults.
                    Dim corruptPath As String = Nothing
                    Dim presets = Radios.FilterPresets.Load(BaseConfigDir & "\Radios", opName, corruptPath)
                    If corruptPath IsNot Nothing Then
                        Radios.ScreenReaderOutput.Speak(
                            Radios.Lexicon.Get("connect.startup.filter_presets_corrupt",
                                               ("fileName", IO.Path.GetFileName(corruptPath))),
                            Radios.VerbosityLevel.Critical)
                    End If
                    handlers.FilterPresets = presets
                    WpfMainWindow.SetNativeMenuFilterPresetsCallback?.Invoke(presets)
                End If

                ' Wire band memory and license config (Sprint 17 Track C).
                handlers.GetConfigDirectory = Function() BaseConfigDir

                ' Per-radio store root: the authoritative assignment lives in
                ' GetConfigInfo (true startup) — this wiring runs only after a
                ' radio window opens, which is too late for connect-time reads
                ' (learned live 2026-08-06). Kept here as harmless redundancy.
                Radios.RadioConfig.BaseDirectory = BaseConfigDir
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
                            Radios.Lexicon.Get("connect.startup.first_run_license"))
                    End If

                    ' Sprint 28 Phase 1 — load per-operator accessibility preferences
                    ' (double-tap tolerance today; future sprints may add more).
                    ' Load sets AccessibilityConfig.Current as a side effect; UI-layer
                    ' consumers read the static Current property directly rather than
                    ' threading the config through their call chains.
                    Radios.AccessibilityConfig.Load(BaseConfigDir, opName)
                End If
            End Sub
        End Sub

        ''' <summary>
        ''' #321 — tell the operator, through a channel that does not depend on
        ''' the broken one, that the application has no speech this session.
        '''
        ''' The decision itself lives in
        ''' <see cref="Radios.Speech.SpeechFailureAlert"/> so it can be tested;
        ''' this is only the two acts of telling. Nothing here may throw: a
        ''' failure to REPORT a startup failure must not become a second one.
        ''' </summary>
        Private Sub RaiseSpeechFailureAlertIfNeeded()
            Try
                If Not Radios.Speech.SpeechFailureAlert.ShouldAlert(
                        Radios.OutputChannelRecorder.RenderEnabled,
                        Radios.ScreenReaderOutput.IsAvailable) Then Return

                JJTrace.Tracing.TraceLine(Radios.Speech.SpeechFailureAlert.TraceLine,
                                          TraceLevel.Error)

                ' The alarm first, and it is the only earcon in the app that
                ' ignores the mute switches — see SpeechUnavailableAlarm. It
                ' plays while the dialog is still being built, so the sound
                ' arrives before the silence has to be interpreted.
                Try
                    JJFlexWpf.EarconPlayer.SpeechUnavailableAlarm()
                Catch ex As Exception
                    JJTrace.Tracing.TraceLine(
                        "Speech failure alarm could not sound: " & ex.Message, TraceLevel.Error)
                End Try

                ' Ownerless on purpose: no window of ours exists yet, which is
                ' precisely what keeps this from ending up underneath one.
                MessageBox.Show(Radios.Speech.SpeechFailureAlert.AlertMessage,
                                Radios.Speech.SpeechFailureAlert.AlertTitle,
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning)
            Catch ex As Exception
                JJTrace.Tracing.TraceLine(
                    "RaiseSpeechFailureAlertIfNeeded failed: " & ex.Message, TraceLevel.Error)
            End Try
        End Sub

        Private Sub MyApplication_Shutdown(sender As Object, e As System.EventArgs) Handles Me.Shutdown
            ' From here on, a failure is still traced but never opens a window.
            ' A modal fighting a teardown is how an app ends up with no exit path
            ' at all, and teardown is exactly when late failures arrive.
            JJFlexWpf.DiagnosticOffer.BeginShutdown()
            ' Permanent teardown telemetry (Sprint 32 merge fix). A field report of
            ' "no CW goodbye" is undiagnosable after the fact without knowing which
            ' guard condition held and what else was sounding — the 2026-08-19
            ' report turned out to be a mis-attributed Alt+F4, which only this
            ' trace can distinguish from a real suppression next time.
            JJTrace.Tracing.TraceLine(
                "Shutdown: farewell guard — CwNotificationsEnabled=" &
                Radios.ScreenReaderOutput.CwNotificationsEnabled.ToString() &
                ", PlayCwSK wired=" & (Radios.ScreenReaderOutput.PlayCwSK IsNot Nothing).ToString() &
                ", SkAlreadyPlayedThisSession=" & Radios.ScreenReaderOutput.SkAlreadyPlayedThisSession.ToString() &
                ", AtuProgressToneRunning=" & JJFlexWpf.EarconPlayer.IsATUProgressEarconRunning.ToString() &
                ", BenchToneRunning=" & JJFlexWpf.EarconPlayer.IsBenchToneRunning.ToString())
            ' Play SK prosign on app close. The wait is DERIVED from the farewell
            ' about to be sent -- FlexBase.SkFarewellWaitMs() asks the CW side how
            ' long this string takes at this speed, then clamps it. It was a flat
            ' 5 seconds until #143, chosen for exactly the right case (the richer
            ' "73 de JJF SK" at speed >= 25 WPM) and short for it by about a
            ' second. Roughly 10-15 WPM and 25-31 WPM were being truncated; 20,
            ' which is what Noel runs, fits comfortably, which is why nobody saw
            ' it. Both SK paths call the same method so they cannot drift.
            ' Skip if FlexBase.Disconnect already played SK during this session —
            ' otherwise the user hears 73 twice when exiting from a connected state
            ' (Disconnect path → SK once, then Shutdown → SK again).
            If Radios.ScreenReaderOutput.CwNotificationsEnabled AndAlso
               Radios.ScreenReaderOutput.PlayCwSK IsNot Nothing AndAlso
               Not Radios.ScreenReaderOutput.SkAlreadyPlayedThisSession Then
                Try
                    Dim swFarewell = System.Diagnostics.Stopwatch.StartNew()
                    Dim waitMs = Radios.FlexBase.SkFarewellWaitMs()
                    Dim finished = Radios.ScreenReaderOutput.PlayCwSK.Invoke().Wait(waitMs)
                    JJTrace.Tracing.TraceLine(
                        "Shutdown: farewell " & If(finished, "completed", "TIMED OUT") &
                        " after " & swFarewell.ElapsedMilliseconds.ToString() &
                        "ms of " & waitMs.ToString() & "ms allowed")
                Catch ex As Exception
                    ' Swallowing this silently is how a suppressed farewell became
                    ' undiagnosable; trace it, still never open a window.
                    JJTrace.Tracing.TraceLine("Shutdown: farewell threw: " & ex.ToString())
                End Try
            Else
                JJTrace.Tracing.TraceLine("Shutdown: farewell skipped by guard")
            End If
            ' Shut down meter sonification engine.
            JJFlexWpf.MeterToneEngine.Shutdown()
            ' Clean up NAudio earcon player.
            JJFlexWpf.EarconPlayer.Dispose()
            ' Clean up screen reader resources.
            Radios.ScreenReaderOutput.Shutdown()
            ' Seal the output transcript (session-end marker). Last of the
            ' output teardown on purpose: the CW farewell and any final speech
            ' above still land in the transcript. A transcript that ends
            ' without this marker means the app crashed or was killed - every
            ' line before it is still valid, each was flushed as written.
            Radios.OutputChannelRecorder.Close()
            ' Belt-and-suspenders archive: if ExitApplication wasn't reached (e.g.
            ' shutdown via WPF route that bypasses Form-side Closing handlers), the
            ' session is still active here. Idempotent — no-op if ExitApplication
            ' already archived. Per memory/project_trace_persistence_design.md.
            ArchiveCurrentTraceSession(JJTrace.TraceSessionOutcome.CleanExit, "MyApplication_Shutdown event")
        End Sub
    End Class
End Namespace
