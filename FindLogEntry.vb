Imports System.Collections.Queue
Imports System.Text
Imports System.Text.StringBuilder
Imports System.Threading
Imports adif
Imports JJLogLib
Imports JJTrace

Public Class FindLogEntry
    Const MustHaveLog As String = "You must specify a log file."
    Const mustClose As String = "You must first close the Find Log Entry form."
    Const searchSelectMessage As String = "Use existing search?"
    Const searchSelectTitle As String = "There was a prior search"
    Const noSearchItems As String = "No items were found."

    ''' <summary>
    ''' Find entries for this call.
    ''' </summary>
    Public Property SearchCall As String

    Private matchData As LogSession = Nothing
    Private Shared q As Queue
    Private searchThread As Thread

    ''' <summary>
    ''' Search result data is stored in this class.
    ''' The display property returns the string to display.
    ''' </summary>
    Private Class searchOutputClass
        Private Const callLength As Integer = 8
        Private Const datLength As Integer = 10
        Private Const handlLength As Integer = 10
        Private Const qthLength As Integer = 15
        Private Const bandLength As Integer = 3
        Private Const modeLength As Integer = 5
        Private Const stateCountryLength As Integer = 15
        Private line As StringBuilder
        Private Const lineLength As Integer = callLength + 1 + _
            datLength + 1 + handlLength + 1 + qthLength + 1 + bandLength + 1 + stateCountryLength + 1
        ''' <summary>
        ''' Provide data for the list.
        ''' </summary>
        ''' <param name="m">match data</param>
        ''' <remarks></remarks>
        Public Sub New(m As LogSession.MatchClass)
            line = New StringBuilder(lineLength)
            addField(m, AdifTags.ADIF_Call, callLength)
            addField(m, AdifTags.ADIF_Band, bandLength)
            addField(m, AdifTags.ADIF_Mode, modeLength)
            addField(m, AdifTags.ADIF_DateOn, datLength)
            addField(m, AdifTags.ADIF_Name, handlLength)
            addField(m, AdifTags.ADIF_QTH, qthLength)
            Dim fld As LogFieldElement = Nothing
            If m.Fields.TryGetValue("STATE", fld) AndAlso _
               (fld.Data <> "") Then
                addField(m, "STATE", stateCountryLength)
            Else
                addField(m, "COUNTRY", stateCountryLength)
            End If
            Position = m.Pos
        End Sub
        Private Sub addField(m As LogSession.MatchClass, name As String, _
                             len As Integer)
            Dim fld As LogFieldElement = Nothing
            Dim str As String = "-"
            If m.Fields.TryGetValue(name, fld) AndAlso (fld.Data <> vbNullString) Then
                str = fld.Data
            End If
            Dim strLen As Integer = System.Math.Min(len, str.Length)
            line.Append(str, 0, strLen)
            line.Append(" ")
        End Sub
        Public ReadOnly Property Display As String
            Get
                Return line.ToString
            End Get
        End Property
        ''' <summary>
        ''' file position
        ''' </summary>
        ''' <value>position as long</value>
        ''' <returns>position of type long</returns>
        Public Property Position As Long
        ''' <summary>
        ''' Log entry form thread
        ''' </summary>
        ''' <value>thread</value>
        ''' <returns>the thread</returns>
        Public Property ScreenThread As Thread
    End Class
    Private itemListSource As ArrayList

    Private Property inDialog As Boolean
        Get
            Return FindDialog
        End Get
        Set(value As Boolean)
            FindDialog = value
        End Set
    End Property
    Private Function cleanup() As Boolean
        Dim rv As Boolean
        If inDialog Then
            MsgBox(mustClose)
            rv = False
        Else
            matchData.EndSession()
            matchData = Nothing
            rv = True
        End If
        Tracing.TraceLine("find cleanup returning:" & rv.ToString(), TraceLevel.Info)
        Return (rv)
    End Function

    Private Sub leaveForm(r As Integer)
        DialogResult = r
        inDialog = False
    End Sub

    Private Sub FindLogEntry_Load(sender As System.Object, e As System.EventArgs) Handles MyBase.Load
        Tracing.TraceLine("find_load", TraceLevel.Info)
        inDialog = True
        DialogResult = DialogResult.None
        ' We must have a log file.
        If CurrentOp.LogFile = vbNullString Then
            MsgBox(MustHaveLog)
            leaveForm(DialogResult.Abort)
            Return
        End If
        ' Start a search session.
        matchData = New LogSession(ContactLog)
        Dim clean = New LogClass.cleanupClass("FindLogEntry", AddressOf cleanup)
        If Not matchData.Start(Nothing, clean) Then
            leaveForm(DialogResult.Abort)
            Tracing.TraceLine("find couldn't start session", TraceLevel.Info)
            Return
        End If
        If SearchCall IsNot Nothing Then
            Tracing.TraceLine("find searchcall", TraceLevel.Info)
            matchData.SetFieldText(AdifTags.ADIF_Call, SearchCall)
            SearchCall = Nothing
        Else
            Tracing.TraceLine("find getSearchArg", TraceLevel.Info)
            If Not LogEntry.GetSearchArg(matchData) Then
                ' search arg not entered.
                leaveForm(DialogResult.Cancel)
                Return
            End If
        End If
        ' Record the search argument.
        If Not matchData.SetSearchArg() Then
            leaveForm(DialogResult.Abort)
            Tracing.TraceLine("find can't set arg", TraceLevel.Info)
            Return
        End If
        itemListSource = New ArrayList
        ' see sourceOutputClass for the members.
        q = Queue.Synchronized(New Queue)
        searchThread = New Thread(AddressOf searcher)
        searchThread.Start()
        CheckQTimer.Start()
    End Sub

    ' Search thread.
    Private Sub searcher()
        Dim keepLooking As Boolean = True
        While keepLooking
            Dim m As LogSession.MatchClass = matchData.Match
            If m IsNot Nothing Then
                Tracing.TraceLine("find found entry at:" & m.Pos.ToString, TraceLevel.Info)
                q.Enqueue(m)
                'Thread.Sleep(5000)
            Else
                keepLooking = False
            End If
        End While
        ' We're at EOF or an error occurred.
    End Sub

    Private Sub CheckQTimer_Tick(sender As System.Object, e As System.EventArgs) Handles CheckQTimer.Tick
        ' This runs under the main search thread.
        If Not searchThread.IsAlive Then
            ' Won't have any more to display.
            CheckQTimer.Stop()
        End If
        Dim m As LogSession.MatchClass
        While q.Count > 0
            m = q.Dequeue
            itemListSource.Add(New searchOutputClass(m))
            ItemList.DataSource = Nothing ' For some strange reason...
            ItemList.DataSource = itemListSource
            ItemList.DisplayMember = "Display"
            ItemList.ValueMember = "Position"
        End While
        If (Not searchThread.IsAlive) And (itemListSource.Count = 0) Then
            ' no items and no more coming
            MsgBox(noSearchItems)
            DialogResult = System.Windows.Forms.DialogResult.Abort
        End If
    End Sub

    Private Sub ItemList_Click(sender As System.Object, e As System.EventArgs) Handles ItemList.Click
        Dim i As searchOutputClass = itemListSource(ItemList.SelectedIndex)
        i.ScreenThread = New Thread(AddressOf showItem)
        i.ScreenThread.Start(i)
    End Sub

    Private Sub showItem(i As searchOutputClass)
        Dim rv As Integer
        Dim logForm As New LogEntry
        logForm.FilePosition = i.Position
        ' This will create a session which we'll end here.
        rv = logForm.ShowDialog()
        If (rv = DialogResult.OK) And logForm.NeedsWrite Then
            logForm.optionalWrite()
        Else ' Still may have been written from the form.
        End If
        ' Record any new position.
        i.Position = logForm.FilePosition
        ' Now end the session and the form.
        logForm.EndSession()
        logForm.Dispose()
    End Sub

    Private Sub ItemList_KeyPress(sender As System.Object, e As System.Windows.Forms.KeyPressEventArgs) Handles ItemList.KeyPress
        If e.KeyChar = vbCr Then
            ItemList_Click(sender, e)
        End If
    End Sub

    Private Sub DoneButton_Click(sender As System.Object, e As System.EventArgs) Handles DoneButton.Click
        leaveForm(DialogResult.OK)
    End Sub

    Private Sub FindLogEntry_FormClosing(sender As System.Object, e As System.Windows.Forms.FormClosingEventArgs) Handles MyBase.FormClosing
        If matchData IsNot Nothing Then
            matchData.EndSession()
            matchData = Nothing
        End If
    End Sub
End Class