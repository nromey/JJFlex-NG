Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.Linq
Imports JJTrace
Imports Radios

''' <summary>
''' The operator's messages: one list, each slot carrying CW text, a
''' recording, or both, sent by one key whatever mode the radio is in.
''' </summary>
''' <remarks>
''' <para>
''' This object is instanciated when current op is set.
''' The messages are part of the operator data.
''' When changed, Operators.UpdateCWText(CurrentOp) and
''' Commands.UpdateCWText() must be called.
''' </para>
''' <para>
''' Sprint 48 Track A (#151). Jim's structure is kept whole and extended by
''' one field: <see cref="MessageItem.Audio"/> names a recording in the
''' operator's recordings folder, sent in place of the microphone when the
''' radio is in a voice mode. The CW text keeps doing what it did in CW.
''' Noel, 2026-10-05: "Heck, we could make it work for voice and CW, same
''' keys." The class and its members keep their CW names because the key
''' tables, the operator files and the VB side all use them; renaming would
''' be churn with a migration attached and no operator would hear the
''' difference.
''' </para>
''' <para>
''' Jim had this right before we did: messages are OPERATOR data, saved with
''' the operator and carried to any radio they sit down at — which is the
''' same conclusion RecordingStore reached when it declined the radio's DVK
''' as a home for recordings. So a slot's recording is a name in that
''' folder, never a slot on the radio.
''' </para>
''' </remarks>
Public Class CWMessages
    Public Class MessageItem
        ''' <summary>key value</summary>
        Public key As Keys
        ''' <summary>message to send</summary>
        Public message As String
        ''' <summary>message name or label</summary>
        Public Label As String
        ''' <summary>
        ''' The recording sent in a voice mode: its file name, without the
        ''' extension, inside the operator's recordings folder. Nothing or
        ''' empty when the slot has no recording. Older operator files have
        ''' no element for it and load with it Nothing, which is the same
        ''' thing.
        ''' </summary>
        Public Audio As String
        Public Sub New()
        End Sub
        Public Sub New(k As Keys, m As String, l As String)
            key = k
            message = m
            Label = l
        End Sub
        Public Sub New(k As Keys, m As String, l As String, a As String)
            Me.New(k, m, l)
            Audio = a
        End Sub
        ''' <summary>True when the slot carries CW text.</summary>
        Public ReadOnly Property HasText As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(message)
            End Get
        End Property
        ''' <summary>True when the slot names a recording.</summary>
        Public ReadOnly Property HasAudio As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(Audio)
            End Get
        End Property
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
        '
        ' Sprint 48 Track A (#151): and what the slot carries - "CW and
        ' voice", "CW only", "voice only" - because a slot with nothing for
        ' the mode you are in is the one thing the key will refuse, and the
        ' list is where you find out before the contest rather than during.
        ' The key is formatted by the same helper the Hotkey Editor and the
        ' key manifest use ("Ctrl+1"), so speech and text agree everywhere;
        ' KeyString would have read this out as "Control-D1".
        dlg.GetMessageLabels =
            Function()
                Dim labels As New List(Of String)
                For i As Integer = 0 To messages.Count - 1
                    Dim m = messages(i)
                    Dim row As String = m.Label
                    If m.key <> Keys.None Then
                        row &= ", " & JJFlexWpf.KeyManifest.FormatKey(m.key)
                    End If
                    row &= ", " & Radios.Lexicon.Get(PayloadKindKey(m))
                    labels.Add(row)
                Next
                Return labels.ToArray()
            End Function
        dlg.AddMessage = Sub() Add()
        dlg.UpdateMessage = Sub(id As Integer) Update(id)
        dlg.DeleteMessage = Sub(id As Integer) Remove(id)

        dlg.ShowDialog()
    End Sub

    ''' <summary>
    ''' The lexicon key naming what a slot carries.
    ''' </summary>
    Private Shared Function PayloadKindKey(m As MessageItem) As String
        If m.HasText AndAlso m.HasAudio Then Return "settings.cw_message.kind_both"
        If m.HasAudio Then Return "settings.cw_message.kind_audio"
        Return "settings.cw_message.kind_text"
    End Function

    ''' <summary>
    ''' The key the editor offers a new message before the operator presses
    ''' one: the first of Ctrl+1 through Ctrl+9, then Ctrl+0, bound to
    ''' nothing. Keys.None when all ten are taken.
    ''' </summary>
    ''' <remarks>
    ''' Sprint 48 Track A (#151). "Ctrl+1 means send my CQ" is only true if
    ''' the operator's CQ is on Ctrl+1, and asking them to press the chord
    ''' into a read-only box to get there is a step the common case does not
    ''' need. The operator can still press any free key over the suggestion.
    ''' Lookup is scope-aware and already knows the message keys, so a slot
    ''' taken by another message is skipped the same way a command's key is.
    ''' </remarks>
    Private Function SuggestKey() As Keys
        Dim digits As Keys() = {Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5,
                                Keys.D6, Keys.D7, Keys.D8, Keys.D9, Keys.D0}
        For Each d In digits
            Dim k As Keys = d Or Keys.Control
            If Commands.Lookup(k) Is Nothing AndAlso Not messages.Any(Function(m) m.key = k) Then
                Return k
            End If
        Next
        Return Keys.None
    End Function

    ''' <summary>
    ''' Show the editor and return the operator's item, or Nothing if they
    ''' cancelled.
    ''' </summary>
    ''' <param name="existing">the item being edited, or Nothing to add</param>
    Private Function EditItem(existing As MessageItem) As MessageItem
        Dim dlg As New JJFlexWpf.Dialogs.CWMessageAddDialog()

        If existing IsNot Nothing Then
            ' The existing key rides along as the opaque value so an update
            ' that does not press a new key keeps the old one. Before Sprint
            ' 48 Track A only the display text was handed over, and the
            ' dialog converted the key it had CAPTURED - none - so changing a
            ' message's wording unbound it.
            dlg.ExistingItem = New JJFlexWpf.Dialogs.CWMessageData With {
                .KeyDisplay = JJFlexWpf.KeyManifest.FormatKey(existing.key),
                .KeyValue = CObj(existing.key),
                .Label = existing.Label,
                .Message = If(existing.message, ""),
                .Audio = If(existing.Audio, ""),
                .KeySpecified = (existing.key <> Keys.None)
            }
        Else
            Dim suggested As Keys = SuggestKey()
            If suggested <> Keys.None Then
                dlg.SuggestedKey = New JJFlexWpf.Dialogs.CWMessageData With {
                    .KeyDisplay = JJFlexWpf.KeyManifest.FormatKey(suggested),
                    .KeyValue = CObj(suggested),
                    .KeySpecified = True
                }
            End If
        End If

        ' Jim's rule, unchanged: a key already bound to any command is refused
        ' outright rather than stolen. The Hotkey Editor is where bindings get
        ' contested; this dialog only claims free keys.
        dlg.IsKeyDuplicate =
            Function(k As System.Windows.Input.Key, mods As System.Windows.Input.ModifierKeys)
                Dim wanted = JJFlexWpf.WpfKeyConverter.ToWinFormsKeys(k, mods)
                ' Pressing the key a message already has, while updating that
                ' same message, is not a duplicate.
                If existing IsNot Nothing AndAlso wanted = existing.key Then Return False
                Return Commands.Lookup(wanted) IsNot Nothing
            End Function
        dlg.FormatKey =
            Function(k As System.Windows.Input.Key, mods As System.Windows.Input.ModifierKeys)
                Return JJFlexWpf.KeyManifest.FormatKey(JJFlexWpf.WpfKeyConverter.ToWinFormsKeys(k, mods))
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
        Return New MessageItem(key, If(r.Message, ""), r.Label, If(r.Audio, ""))
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
        Tracing.TraceLine($"CWMessages.Add: {item.Label} on {JJFlexWpf.KeyManifest.FormatKey(item.key)}, {Radios.Lexicon.Get(PayloadKindKey(item))}", TraceLevel.Info)
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
        Tracing.TraceLine($"CWMessages.Update: {item.Label} on {JJFlexWpf.KeyManifest.FormatKey(item.key)}, {Radios.Lexicon.Get(PayloadKindKey(item))}", TraceLevel.Info)
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
