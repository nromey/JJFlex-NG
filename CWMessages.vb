Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports JJTrace
Imports Radios

''' <summary>
''' CW messages
''' </summary>
''' <remarks>
''' This object is instanciated when current op is set.
''' The messages are part of the operator data.
''' When changed, Operators.UpdateCWText(CurrentOp) and 
''' Commands.UpdateCWText() must be called.
''' </remarks>
Public Class CWMessages
    Public Class MessageItem
        ''' <summary>key value</summary> 
        Public key As Keys
        ''' <summary>message to send</summary>
        Public message As String
        ''' <summary>message name or label</summary>
        Public Label As String
        Public Sub New()
        End Sub
        Public Sub New(k As Keys, m As String, l As String)
            key = k
            message = m
            Label = l
        End Sub
    End Class
    Private Shared messages As List(Of MessageItem)

    ''' <summary>
    ''' number of messages
    ''' </summary>
    Friend ReadOnly Property Length
        Get
            Return messages.Count
        End Get
    End Property
    ''' <summary>
    ''' return a message
    ''' </summary>
    ''' <param name="id"></param>
    ''' <returns>a MessageItem</returns>
    Default Friend ReadOnly Property Items(id As Integer) As MessageItem
        Get
            Return messages(id)
        End Get
    End Property

    ''' <summary>
    ''' new CWMessages
    ''' </summary>
    ''' <param name="MsgArray">Array of messages</param>
    Friend Sub New(MsgArray As MessageItem())
        messages = New List(Of MessageItem)
        If MsgArray IsNot Nothing Then
            messages.AddRange(MsgArray)
        End If
    End Sub

    ''' <summary>
    ''' Update the CWText for the current operator.
    ''' </summary>
    Friend Sub UpdateOperator()
        Operators.UpdateCWText(CurrentOp, messages.ToArray)
    End Sub

    ' F-key to Ctrl+number migration map.
    Private Shared ReadOnly FKeyMigration As Dictionary(Of Keys, Keys) = New Dictionary(Of Keys, Keys) From {
        {Keys.F5, Keys.D1 Or Keys.Control},
        {Keys.F6, Keys.D2 Or Keys.Control},
        {Keys.F7, Keys.D3 Or Keys.Control},
        {Keys.F8, Keys.D4 Or Keys.Control},
        {Keys.F9, Keys.D5 Or Keys.Control},
        {Keys.F10, Keys.D6 Or Keys.Control},
        {Keys.F11, Keys.D7 Or Keys.Control}
    }

    ''' <summary>
    ''' One-time migration: remap CW message keys from F5-F11 to Ctrl+1..Ctrl+7.
    ''' Called on operator load. Returns True if any keys were migrated.
    ''' </summary>
    Friend Function MigrateFKeysToCtrlNumber() As Boolean
        Dim changed As Boolean = False
        For Each msg In messages
            Dim newKey As Keys = Nothing
            If FKeyMigration.TryGetValue(msg.key, newKey) Then
                Tracing.TraceLine("CWMessages: migrating " & msg.key.ToString & " to " & newKey.ToString, TraceLevel.Info)
                msg.key = newKey
                changed = True
            End If
        Next
        If changed Then
            UpdateOperator()
        End If
        Return changed
    End Function

    ''' <summary>
    ''' Open the CW message manager: the list, with add, update and delete.
    ''' </summary>
    ''' <remarks>
    ''' <para>
    ''' #329. Until 2026-09-01 nothing called this feature at all. The Tools
    ''' menu carried "Manage CW Messages - not yet implemented" while both WPF
    ''' dialogs sat finished in the tree since Sprint 9, the WinForms
    ''' CWMessageUpdate that used to open them had lost its last caller, and
    ''' Add, Update and Remove below were reachable only from that form. The
    ''' data store worked, the send path worked, and the only missing piece was
    ''' the glue in this file.
    ''' </para>
    ''' <para>
    ''' Two shipped surfaces were already telling the operator this existed:
    ''' the Hotkey Editor refuses to steal a CW message's key with "which is
    ''' managed under CW Messages", and the keyboard reference said to
    ''' configure them in Settings. So an operator could be sent looking for a
    ''' door that was not there.
    ''' </para>
    ''' <para>
    ''' The glue lives in VB because it is the side that owns the data. The
    ''' dialogs take delegates and reference neither CWMessages nor FlexBase,
    ''' which is what lets them stay in JJFlexWpf.
    ''' </para>
    ''' </remarks>
    Friend Sub Manage()
        Dim dlg As New JJFlexWpf.Dialogs.CWMessageUpdateDialog()

        ' The list shows the key beside the label. Jim's WinForms list showed
        ' the label alone, which reads as a bare list of names to a screen
        ' reader - "CQ, DE, 73" tells you nothing about which key sends which,
        ' and the whole point of the feature is the mapping.
        dlg.GetMessageLabels =
            Function()
                Dim labels As New List(Of String)
                For i As Integer = 0 To messages.Count - 1
                    Dim m = messages(i)
                    If m.key = Keys.None Then
                        labels.Add(m.Label)
                    Else
                        labels.Add($"{m.Label}, {KeyString(m.key)}")
                    End If
                Next
                Return labels.ToArray()
            End Function
        dlg.AddMessage = Sub() Add()
        dlg.UpdateMessage = Sub(id As Integer) Update(id)
        dlg.DeleteMessage = Sub(id As Integer) Remove(id)

        dlg.ShowDialog()
    End Sub

    ''' <summary>
    ''' Show the editor and return the operator's item, or Nothing if they
    ''' cancelled.
    ''' </summary>
    ''' <param name="existing">the item being edited, or Nothing to add</param>
    Private Function EditItem(existing As MessageItem) As MessageItem
        Dim dlg As New JJFlexWpf.Dialogs.CWMessageAddDialog()

        If existing IsNot Nothing Then
            dlg.ExistingItem = New JJFlexWpf.Dialogs.CWMessageData With {
                .KeyDisplay = KeyString(existing.key),
                .Label = existing.Label,
                .Message = existing.message,
                .KeySpecified = True
            }
        End If

        ' Jim's rule, unchanged: a key already bound to any command is refused
        ' outright rather than stolen. The Hotkey Editor is where bindings get
        ' contested; this dialog only claims free keys.
        dlg.IsKeyDuplicate =
            Function(k As System.Windows.Input.Key, mods As System.Windows.Input.ModifierKeys)
                Return Commands.Lookup(JJFlexWpf.WpfKeyConverter.ToWinFormsKeys(k, mods)) IsNot Nothing
            End Function
        dlg.FormatKey =
            Function(k As System.Windows.Input.Key, mods As System.Windows.Input.ModifierKeys)
                Return KeyString(JJFlexWpf.WpfKeyConverter.ToWinFormsKeys(k, mods))
            End Function
        dlg.ConvertKey =
            Function(k As System.Windows.Input.Key, mods As System.Windows.Input.ModifierKeys)
                Return CObj(JJFlexWpf.WpfKeyConverter.ToWinFormsKeys(k, mods))
            End Function

        If dlg.ShowDialog() <> True OrElse dlg.ResultItem Is Nothing Then
            Return Nothing
        End If

        Dim r = dlg.ResultItem
        Dim key As Keys = If(TypeOf r.KeyValue Is Keys, DirectCast(r.KeyValue, Keys), Keys.None)
        Return New MessageItem(key, r.Message, r.Label)
    End Function

    ''' <summary>
    ''' Add a new CW message
    ''' </summary>
    Friend Sub Add()
        Dim item = EditItem(Nothing)
        If item Is Nothing Then Return
        messages.Add(item)
        UpdateOperator()
        Commands.UpdateCWText()
        Tracing.TraceLine($"CWMessages.Add: {item.Label} on {KeyString(item.key)}", TraceLevel.Info)
    End Sub

    ''' <summary>
    ''' Update a message item
    ''' </summary>
    ''' <param name="id"></param>
    ''' <remarks>Called from the CW message manager, <see cref="Manage"/>.</remarks>
    Friend Sub Update(id As Integer)
        If id < 0 OrElse id >= messages.Count Then Return
        Dim item = EditItem(Me(id))
        If item Is Nothing Then Return
        messages.RemoveAt(id)
        messages.Insert(id, item)
        UpdateOperator()
        Commands.UpdateCWText()
        Tracing.TraceLine($"CWMessages.Update: {item.Label} on {KeyString(item.key)}", TraceLevel.Info)
    End Sub

    ''' <summary>
    ''' Remove the item
    ''' </summary>
    ''' <param name="id"></param>
    ''' <remarks>Called from the CW message manager, <see cref="Manage"/>.</remarks>
    Friend Sub Remove(id As Integer)
        If id < 0 OrElse id >= messages.Count Then Return
        messages.RemoveAt(id)
        UpdateOperator()
        Commands.UpdateCWText()
    End Sub
End Class
