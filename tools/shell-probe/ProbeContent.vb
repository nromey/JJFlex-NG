Option Strict On
Imports System.Windows
Imports System.Windows.Automation
Imports System.Windows.Automation.Peers
Imports System.Windows.Controls
Imports System.Windows.Input
Imports System.Windows.Interop

''' <summary>
''' A copy of JJFlexWpf.Controls.SilentTextBox: a read-only TextBox whose peer is
''' a plain FrameworkElementAutomationPeer, control type Custom, class name
''' "FrequencyDisplay". That is what Home's frequency field exposes.
''' </summary>
Friend Class SilentTextBox
    Inherits TextBox

    Protected Overrides Function OnCreateAutomationPeer() As AutomationPeer
        Return New SilentTextBoxPeer(Me)
    End Function

    Private NotInheritable Class SilentTextBoxPeer
        Inherits FrameworkElementAutomationPeer

        Public Sub New(owner As FrameworkElement)
            MyBase.New(owner)
        End Sub

        Protected Overrides Function GetClassNameCore() As String
            Return "FrequencyDisplay"
        End Function

        Protected Overrides Function GetAutomationControlTypeCore() As AutomationControlType
            Return AutomationControlType.Custom
        End Function
    End Class
End Class

''' <summary>
''' The window's content, standing in for the product's MainWindow user control:
''' a named UserControl (the product's is "JJ Flexible Radio Access Main
''' Window"), holding a frequency-style field, three buttons and a text box.
''' Shared by both shells so the only variable is the host.
''' </summary>
Friend NotInheritable Class ProbeContent
    Inherits UserControl

    Friend ReadOnly Field As SilentTextBox
    Friend ReadOnly Notes As TextBox

    ''' <summary>The Win32 owner for dialogs and message boxes: the shell's handle.</summary>
    Friend OwnerHandle As Func(Of IntPtr)

    Friend Sub New()
        AutomationProperties.SetName(Me, "JJ Shell Probe Main Window")

        Dim panel As New StackPanel() With {.Margin = New Thickness(8)}

        ' Home's field sits inside a UserControl named "JJ Flexible Home" that
        ' is neither focusable nor a tab stop; the TextBox inside carries the
        ' field's name. Copied as-is.
        Dim home As New UserControl() With {.Focusable = False, .IsTabStop = False}
        AutomationProperties.SetName(home, "JJ Flexible Home")
        Field = New SilentTextBox() With {
            .IsReadOnly = True,
            .FontFamily = New System.Windows.Media.FontFamily("Consolas"),
            .FontSize = 14,
            .TabIndex = 0,
            .Text = "A  14.100.000  USB  S5"
        }
        AutomationProperties.SetName(Field, "Home, frequency and VFO")
        home.Content = Field
        panel.Children.Add(home)

        panel.Children.Add(MakeButton("Open dialog", AddressOf OpenDialog))
        panel.Children.Add(MakeButton("Message box", AddressOf ShowMessageBox))
        panel.Children.Add(MakeButton("Speak", AddressOf SpeakOnce))

        Dim label As New Label() With {.Content = "Notes"}
        Notes = New TextBox() With {.Width = 300, .HorizontalAlignment = HorizontalAlignment.Left}
        AutomationProperties.SetName(Notes, "Notes")
        label.Target = Notes
        panel.Children.Add(label)
        panel.Children.Add(Notes)

        Content = panel

        ' Evidence that keys reach WPF: every key that arrives here, where it
        ' was focused, and whether anything took it.
        AddHandler PreviewKeyDown, Sub(s, e) ProbeLog.Log("WPF PreviewKeyDown key=" & e.Key.ToString() &
                                                          " sys=" & e.SystemKey.ToString() &
                                                          " mods=" & Keyboard.Modifiers.ToString() &
                                                          " focus=" & Describe(Keyboard.FocusedElement))
        AddHandler GotKeyboardFocus, Sub(s, e) ProbeLog.Log("WPF GotKeyboardFocus " & Describe(e.NewFocus) &
                                                            " (from " & Describe(e.OldFocus) & ")")
        AddHandler Field.SelectionChanged, Sub(s, e) ProbeLog.Log("WPF field caret=" & Field.CaretIndex.ToString())
        AddHandler Notes.TextChanged, Sub(s, e) ProbeLog.Log("WPF Notes text=""" & Notes.Text & """")
    End Sub

    Friend Shared Function Describe(o As Object) As String
        If o Is Nothing Then Return "(nothing)"
        Dim d = TryCast(o, DependencyObject)
        Dim name = If(d Is Nothing, "", AutomationProperties.GetName(d))
        Return o.GetType().Name & " """ & name & """"
    End Function

    Private Shared Function MakeButton(text As String, act As Action) As Button
        Dim b As New Button() With {.Content = text, .Margin = New Thickness(0, 6, 0, 0),
                                    .HorizontalAlignment = HorizontalAlignment.Left, .Padding = New Thickness(8, 2, 8, 2)}
        AddHandler b.Click, Sub(s, e)
                                ProbeLog.Log("Button Click: " & text)
                                act()
                            End Sub
        Return b
    End Function

    ''' <summary>A WPF dialog owned by the shell through its Win32 handle, as JJFlexDialog does.</summary>
    Friend Sub OpenDialog()
        Dim dlg As New Window() With {
            .Title = "Probe dialog",
            .Width = 360, .Height = 180,
            .ShowInTaskbar = False,
            .ResizeMode = ResizeMode.NoResize,
            .WindowStartupLocation = WindowStartupLocation.CenterOwner
        }
        Dim owner = OwnerHandle()
        If owner <> IntPtr.Zero Then
            Dim helper As New WindowInteropHelper(dlg) With {.Owner = owner}
        End If
        Dim p As New StackPanel() With {.Margin = New Thickness(8)}
        Dim box As New TextBox() With {.Width = 240, .HorizontalAlignment = HorizontalAlignment.Left}
        AutomationProperties.SetName(box, "Dialog field")
        Dim l As New Label() With {.Content = "Dialog field", .Target = box}
        p.Children.Add(l)
        p.Children.Add(box)
        Dim ok As New Button() With {.Content = "OK", .IsDefault = True, .Width = 80, .Margin = New Thickness(0, 8, 0, 0),
                                     .HorizontalAlignment = HorizontalAlignment.Left}
        AddHandler ok.Click, Sub(s, e) dlg.DialogResult = True
        Dim cancel As New Button() With {.Content = "Cancel", .IsCancel = True, .Width = 80, .Margin = New Thickness(0, 4, 0, 0),
                                         .HorizontalAlignment = HorizontalAlignment.Left}
        p.Children.Add(ok)
        p.Children.Add(cancel)
        dlg.Content = p
        AddHandler dlg.Loaded, Sub(s, e) box.Focus()
        ProbeLog.Log("Dialog: opening, Win32 owner " & Win32.Hex(owner))
        Dim result = dlg.ShowDialog()
        ProbeLog.Log("Dialog: closed, result " & If(result.HasValue, result.Value.ToString(), "none") &
                     ", focus now " & Describe(Keyboard.FocusedElement))
    End Sub

    ''' <summary>A WinForms message box owned through a handle wrapper.</summary>
    Friend Sub ShowMessageBox()
        Dim owner = OwnerHandle()
        ProbeLog.Log("MessageBox: opening, owner " & Win32.Hex(owner))
        Dim r = System.Windows.Forms.MessageBox.Show(New Win32Owner(owner),
            "This is the probe's message box. Press Enter to close it.", "Probe message box")
        ProbeLog.Log("MessageBox: closed, result " & r.ToString() & ", focus now " & Describe(Keyboard.FocusedElement))
    End Sub

    Private Shared _speechReady As Boolean

    ''' <summary>One utterance through the product's own speech path (Radios.ScreenReaderOutput, Prism underneath).</summary>
    Friend Sub SpeakOnce()
        SpeakShared()
    End Sub

    ''' <summary>The same utterance, callable from a shell with no content (the raw baseline).</summary>
    Friend Shared Sub SpeakShared()
        Try
            If Not _speechReady Then
                Radios.ScreenReaderOutput.Initialize()
                _speechReady = True
                ProbeLog.Log("Speech: ScreenReaderOutput initialized; available=" &
                             Radios.ScreenReaderOutput.IsAvailable.ToString() &
                             " reader=" & If(Radios.ScreenReaderOutput.ScreenReaderName, "(none)"))
            End If
            Radios.ScreenReaderOutput.Speak("Shell probe speaking through Prism.")
            ProbeLog.Log("Speech: Speak called with ""Shell probe speaking through Prism.""")
        Catch ex As Exception
            ProbeLog.Log("Speech: FAILED " & ex.GetType().Name & ": " & ex.Message)
        End Try
    End Sub
End Class
