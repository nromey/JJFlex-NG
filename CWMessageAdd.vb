Imports JJTrace

''' <summary>
''' SUPERSEDED 2026-09-01 by JJFlexWpf.Dialogs.CWMessageAddDialog (#329), and
''' left here on purpose rather than deleted in the same change.
''' </summary>
''' <remarks>
''' Nothing constructs this form any more: CWMessages.Add and .Update drove it
''' until the WPF editor was wired, and they now build the WPF one. Its
''' companion CWMessageUpdate had already lost its last caller in the migration,
''' which is what made the whole feature unreachable.
'''
''' RECORDED RATHER THAN DELETED because that is the half of this defect class
''' that actually bites. #457 - the equalisers a tester reported missing - went
''' dark in exactly this shape: Sprint 8 orphaned the control that hosted them,
''' and Sprint 11 then deleted FlexEq.cs as a "dead WinForms file", which it
''' truthfully was, without anyone connecting the two facts. Deleting this is
''' right and should happen; doing it in the same breath as orphaning it is how
''' the reason gets lost.
'''
''' Read it as the reference for what the WPF editor is a port OF. The
''' behaviour it defines - a key already bound to any command is refused rather
''' than stolen, and a message needs a key, a label and some text - is Jim's,
''' and is carried over unchanged.
''' </remarks>
Public Class CWMessageAdd
    Private Const addTitle As String = "Add a CW Message"
    Private Const updateTitle As String = "Update this CW Message"
    Private Const alreadyDefined As String = "This key is already defined."
    Private Const mustHaveText As String = "You must specify a key, a label, and some text."
    ''' <summary>
    ''' set TheItem to indicate an update.
    ''' Set to Nothing to indicate an add.
    ''' </summary>
    ''' <value>a MessageItem object</value>
    ''' <returns>a MessageItem object</returns>
    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Friend Property TheItem As CWMessages.MessageItem
    Private newItem As CWMessages.MessageItem
    Private isUpdate As Boolean
    Private wasActive As Boolean

    Private Sub CWMessageForm_Load(sender As System.Object, e As System.EventArgs) Handles MyBase.Load
        DialogResult = System.Windows.Forms.DialogResult.None
        wasActive = False
        If TheItem Is Nothing Then
            ' It's an add.
            Text = addTitle
            isUpdate = False
            clearItem()
        Else
            ' It's an update
            Text = updateTitle
            KeyTextBox.Text = KeyString(TheItem.key)
            MessageTextBox.Text = TheItem.message
            LabelTextBox.Text = TheItem.Label
            isUpdate = True
            ' A key has been specified.
            keySpecified = True
            newItem = New CWMessages.MessageItem(TheItem.key, TheItem.message, TheItem.Label)
        End If
    End Sub

    Private Sub clearItem()
        newItem = New CWMessages.MessageItem
        keySpecified = False
        KeyTextBox.Text = ""
        MessageTextBox.Text = ""
        LabelTextBox.Text = ""
    End Sub

    Private keySpecified As Boolean
    Private Sub KeyTextBox_KeyDown(sender As System.Object, e As System.Windows.Forms.KeyEventArgs) Handles KeyTextBox.KeyDown
        ' Quit if preliminary press or a tab.
        If (e.KeyCode = Keys.Tab) Or (e.KeyCode = Keys.Alt) Or _
           (e.KeyCode = Keys.Shift) Or (e.KeyCode = Keys.Control) Then
            Return
        End If
        Dim k As Keys = (e.KeyCode Or e.Modifiers)
        e.SuppressKeyPress = True
        ' Allow no-key.
        If k = Keys.Delete Then
            k = Keys.None
        Else
            ' Check for a dup
            If Commands.Lookup(k) IsNot Nothing Then
                MsgBox(alreadyDefined)
                Return
            End If
        End If
        KeyTextBox.Text = KeyString(k)
        newItem.key = k
        keySpecified = True
    End Sub

    Private Sub OkButton_Click(sender As System.Object, e As System.EventArgs) Handles OkButton.Click
        ' Must have a key and some text
        If (Not keySpecified) Or (MessageTextBox.Text = "") Or (LabelTextBox.Text = "") Then
            MsgBox(mustHaveText)
        Else
            newItem.Label = LabelTextBox.Text
            newItem.message = MessageTextBox.Text
            TheItem = newItem
            DialogResult = System.Windows.Forms.DialogResult.OK
        End If
    End Sub

    Private Sub CnclButton_Click(sender As System.Object, e As System.EventArgs) Handles CnclButton.Click
        DialogResult = System.Windows.Forms.DialogResult.Cancel
    End Sub

    Private Sub CWMessageAdd_Activated(sender As System.Object, e As System.EventArgs) Handles MyBase.Activated
        If Not wasActive Then
            wasActive = True
            LabelTextBox.Focus()
        End If
    End Sub
End Class