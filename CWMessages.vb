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
    ''' Add a new CW message
    ''' </summary>
    Friend Sub Add()
        CWMessageAdd.TheItem = Nothing
        While (CWMessageAdd.ShowDialog() = DialogResult.OK)
            messages.Add(CWMessageAdd.TheItem)
            UpdateOperator()
            Commands.UpdateCWText()
            CWMessageAdd.TheItem = Nothing
        End While
    End Sub

    ''' <summary>
    ''' Update a message item
    ''' </summary>
    ''' <param name="id"></param>
    ''' <remarks>Called from CWMessageUpdate</remarks>
    Friend Sub Update(id As Integer)
        CWMessageAdd.TheItem = Me(id)
        If CWMessageAdd.ShowDialog() = DialogResult.OK Then
            messages.RemoveAt(id)
            messages.Insert(id, CWMessageAdd.TheItem)
            UpdateOperator()
            Commands.UpdateCWText()
        End If
    End Sub

    ''' <summary>
    ''' Remove the item
    ''' </summary>
    ''' <param name="id"></param>
    ''' <remarks>Called from CWMessageUpdate</remarks>
    Friend Sub Remove(id As Integer)
        messages.RemoveAt(id)
        UpdateOperator()
        Commands.UpdateCWText()
    End Sub
End Class
