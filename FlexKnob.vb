Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Linq
Imports System.Text
Imports System.Windows.Forms
Imports JJFlexControl
Imports JJTrace
Imports Radios

Friend Class FlexKnob
    Private knob As FlexControl

    Private ReadOnly Property configFileName As String
        Get
            Return BaseConfigDir + "\"c + PersonalData.UniqueOpName(CurrentOp) & "_FlexKnob.xml"
        End Get
    End Property

    Friend Enum KnobStatus_t
        off
        locked
        fullOn
    End Enum
    Friend KnobStatus As KnobStatus_t
    Friend Class KnobOnOffArgs
        Inherits EventArgs
        Public Status As KnobStatus_t
        Public Sub New(st As KnobStatus_t)
            Status = st
        End Sub
    End Class
    Friend Shared Event KnobOnOffEvent As EventHandler(Of KnobOnOffArgs)
    Private Sub raiseKnobOnOff()
        If Not knobOn Then
            KnobStatus = KnobStatus_t.off
        Else
            If knobLocked Then
                KnobStatus = KnobStatus_t.locked
            Else
                KnobStatus = KnobStatus_t.fullOn
            End If
        End If
        RaiseEvent KnobOnOffEvent(Me, New KnobOnOffArgs(KnobStatus))
    End Sub

    Private ReadOnly Property knobActionsMap As Dictionary(Of String, FlexControl.Action_t)
        Get
            Return knob.KnobActionsMap
        End Get
    End Property

    Private allowedActions As List(Of FlexControl.Action_t)
    'New FlexControl.KeyAction_t("C", "RITOnOnLeave"),
    Private defaultKeys As List(Of FlexControl.KeyAction_t) = New List(Of FlexControl.KeyAction_t)() From {
            New FlexControl.KeyAction_t("D", "KnobDown"),
            New FlexControl.KeyAction_t("U", "KnobUp"),
            New FlexControl.KeyAction_t("S", "RITOffOnLeave"),
            New FlexControl.KeyAction_t("L", "RITClear"),
            New FlexControl.KeyAction_t("X1S", "NextVFO"),
            New FlexControl.KeyAction_t("X1C", "KnobLock"),
            New FlexControl.KeyAction_t("X2S", "NextValue1"),
            New FlexControl.KeyAction_t("X3S", "StepIncrease"),
            New FlexControl.KeyAction_t("X3C", "StepDecrease")
        }

    Private Sub setupAllowedActions()
        allowedActions = New List(Of FlexControl.Action_t)()
        allowedActions.Add(New FlexControl.Action_t("None", "None", Nothing))
        allowedActions.Add(New FlexControl.Action_t("KnobDown", "Frequency down", AddressOf knobDown))
        allowedActions.Add(New FlexControl.Action_t("KnobUp", "Frequency up", AddressOf knobUp))
        allowedActions.Add(New FlexControl.Action_t("StepIncrease", "Knob step size increase", AddressOf increaseStepSize, Function(ByVal action As FlexControl.Action_t) stepsize.Value.ToString()))
        allowedActions.Add(New FlexControl.Action_t("StepDecrease", "Knob step size decrease", AddressOf decreaseStepSize, Function(ByVal action As FlexControl.Action_t) stepsize.Value.ToString()))
        allowedActions.Add(New FlexControl.Action_t("NextVFO", "Next VFO", AddressOf NextVFO))
        allowedActions.Add(New FlexControl.Action_t("NextValue1", "toggle noise reduction or APF", AddressOf NextValue1))
        allowedActions.Add(New FlexControl.Action_t("RITOffOnLeave", "Start/stop changing the RIT, turn off if stopping", AddressOf RITOffOnLeave))
        'allowedActions.Add(New FlexControl.Action_t("RITOnOnLeave", "Start/stop changing the RIT, leave on if stopping", AddressOf RITOnOnLeave))
        allowedActions.Add(New FlexControl.Action_t("RITClear", "Clear the RIT", AddressOf RITClear))
        allowedActions.Add(New FlexControl.Action_t("KnobOnOff", "Knob enable",
            AddressOf knobOnOff, Function(ByVal action As FlexControl.Action_t) knobOn.ToString(), True))
        allowedActions.Add(New FlexControl.Action_t("KnobLock", "Lock knob tuning",
            AddressOf knobLock, Function(ByVal action As FlexControl.Action_t) knobLocked.ToString()))
    End Sub

    Friend Sub New()
        setupAllowedActions()
        knob = New FlexControl(configFileName, allowedActions, defaultKeys, False)
        AddHandler knob.KnobOutput, AddressOf keyHandler
        raiseKnobOnOff()
    End Sub

    Friend Sub Dispose()
        Try
            If knob IsNot Nothing Then knob.Dispose()
        Catch ex As Exception
            ' System.IO.Ports v9.0.0 can throw on shutdown (BUG-004)
            System.Diagnostics.Trace.WriteLine($"FlexKnob.Dispose error (harmless): {ex.Message}")
        End Try
    End Sub

    Friend Sub Config()
        knob.SelectPort()
        knob.KeyConfigure()
    End Sub

    Private Sub keyHandler(ByVal cmd As String)
        Dim action As FlexControl.Action_t = Nothing

        'If knobActionsMap.TryGetValue(cmd, action) AndAlso
        '   (action.Action IsNot Nothing) Then
        If knobActionsMap.ContainsKey(cmd) Then
            action = knobActionsMap(cmd)
            If ((action.Action IsNot Nothing) AndAlso (action.AlwaysActive Or knobOn)) Then
                action.Action(action)
            End If
        End If
    End Sub

    Private Sub knobUp(ByVal parm As Object)
        Tracing.TraceLine("knobUp:", TraceLevel.Info)

        If Not (knobLocked Or RigControl.Transmit) Then
            If inRIT Then
                Dim r As FlexBase.RITData = RigControl.RIT
                r.Value += stepsize.Value
                RigControl.RIT = r
            Else
                RigControl.VirtualRXFrequency += CUInt(stepsize.Value)
            End If
        End If
    End Sub

    Private Sub knobDown(ByVal parm As Object)
        Tracing.TraceLine("knobDown:", TraceLevel.Info)

        If Not (knobLocked Or RigControl.Transmit) Then
            If inRIT Then
                Dim r As FlexBase.RITData = RigControl.RIT
                r.Value -= stepsize.Value
                RigControl.RIT = r
            Else
                RigControl.VirtualRXFrequency -= CUInt(stepsize.Value)
            End If
        End If
    End Sub

    Private Class stepsize_t
        Public ID As Integer
        Public Change As Integer
        Public Value As Integer
        Public MinValue As Integer

        Public Sub New(ByVal i As Integer, ByVal c As Integer,
                       ByVal v As Integer, m As Integer)
            ID = i
            Change = c
            Value = v
            MinValue = m
        End Sub
    End Class

    Private Shared modeToStepsize As Dictionary(Of String, stepsize_t) = New Dictionary(Of String, stepsize_t)() From {
            {"CW", New stepsize_t(0, 10, 10, 10)},
            {"AM", New stepsize_t(1, 1000, 5000, 1000)},
            {"dflt", New stepsize_t(2, 10, 50, 10)}
        }

    ' Default stepsize values, one per mode, colon separated.
    Private Shared ReadOnly Property stepsizeConfig As String
        Get
            Dim rv As String = ""
            Dim e = modeToStepsize.Values.GetEnumerator()

            While e.MoveNext()
                If rv <> "" Then rv += ":"c
                rv += e.Current.Value.ToString()
            End While

            Return rv
        End Get
    End Property

    ' Get the stepsize_t for the current mode.
    Private ReadOnly Property stepsize As stepsize_t
        Get
            Dim cfg As String() = knob.GetSavedStringValue("StepIncrease", stepsizeConfig).Split(New Char() {":"c})
            Dim e = modeToStepsize.GetEnumerator()

            While e.MoveNext()
                e.Current.Value.Value = Int32.Parse(cfg(e.Current.Value.ID))
            End While

            Dim modeString As String = RigControl.Mode.ToString()
            Dim s As stepsize_t = Nothing

            If modeToStepsize.TryGetValue(modeString, s) Then
                Return s
            Else
                Return modeToStepsize("dflt")
            End If
        End Get
    End Property

    Private Sub increaseStepSize(ByVal parm As Object)
        Tracing.TraceLine("increaseStepSize:", TraceLevel.Info)
        Dim s As stepsize_t = stepsize
        ' Set to the change value if at the min value.
        If (s.Value = s.MinValue) And (s.Value <> s.Change) Then
            s.Value = s.Change
        Else
            s.Value += s.Change
        End If
        Dim cfg As String = stepsizeConfig
        knob.SaveStringValue("StepIncrease", cfg)
        knob.SaveStringValue("StepDecrease", cfg)
    End Sub

    Private Sub decreaseStepSize(ByVal parm As Object)
        Tracing.TraceLine("decreaseStepSize:", TraceLevel.Info)
        Dim s As stepsize_t = stepsize
        Dim val = s.Value - s.Change
        If val <= 0 Then val = s.MinValue
        s.Value = val
        Dim cfg As String = stepsizeConfig
        knob.SaveStringValue("StepIncrease", cfg)
        knob.SaveStringValue("StepDecrease", cfg)
    End Sub

    ' This only works if transmit mode.
    Private Sub NextVFO(ByVal parm As Object)
        Tracing.TraceLine("NextVFO:", TraceLevel.Info)
        If RigControl.Transmit Then Return
        If Not SplitVFOs Then
            Dim oldVFO = RigControl.RXVFO
            RigControl.RXVFO = RigControl.NextVFO(RigControl.RXVFO)
            RigControl.TXVFO = RigControl.RXVFO
            changeSliceAudio(oldVFO, RigControl.RXVFO)
        End If
    End Sub

    Private Sub NextValue1(ByVal parm As Object)
        Tracing.TraceLine("NextValue1:", TraceLevel.Info)
        Commands.toggle1()
    End Sub

    Private ReadOnly Property inRIT As Boolean
        Get
            Dim rv As Boolean = False
            If RigControl IsNot Nothing Then
                rv = RigControl.RIT.Active
            End If
            Return rv
        End Get
    End Property

    Private Sub RITOffOnLeave(ByVal parm As Object)
        Tracing.TraceLine("RITOffOnLeave:", TraceLevel.Info)
        Dim r As FlexBase.RITData = RigControl.RIT
        r.Active = Not r.Active
        RigControl.RIT = r
    End Sub

    Private Sub RITOnOnLeave(ByVal parm As Object)
        Tracing.TraceLine("RITOnOnLeave:", TraceLevel.Info)
        Dim r As FlexBase.RITData = RigControl.RIT

        If Not inRIT Then
            r.Active = True
        End If

        RigControl.RIT = r
    End Sub

    Private Sub RITClear(ByVal parm As Object)
        Tracing.TraceLine("RITClear:", TraceLevel.Info)
        Dim r As FlexBase.RITData = RigControl.RIT
        r.Value = 0
        RigControl.RIT = r
    End Sub

    Private Const knobOnDefault As Boolean = True

    Private Property knobOn As Boolean
        Get
            Return knob.GetSavedBooleanValue("KnobOnOff", knobOnDefault)
        End Get
        Set(ByVal value As Boolean)
            If value <> knob.GetSavedBooleanValue("KnobOnOff", knobOnDefault) Then
                knob.SaveBooleanValue("KnobOnOff", value)
                raiseKnobOnOff()
            End If
        End Set
    End Property

    Private Sub knobOnOff(ByVal parm As Object)
        knobOn = Not knobOn
        Tracing.TraceLine("knobOnOff:" & knobOn.ToString(), TraceLevel.Info)
    End Sub

    Private Const knobLockDefault As Boolean = False

    Private Property knobLocked As Boolean
        Get
            Return knob.GetSavedBooleanValue("KnobLock", knobLockDefault)
        End Get
        Set(ByVal value As Boolean)
            If value <> knob.GetSavedBooleanValue("KnobLock", knobLockDefault) Then
                knob.SaveBooleanValue("KnobLock", value)
                raiseKnobOnOff()
            End If
        End Set
    End Property

    Private Sub knobLock(ByVal parm As Object)
        knobLocked = Not knobLocked
        Tracing.TraceLine("knobLock:" & knobLocked.ToString(), TraceLevel.Info)
    End Sub
End Class
