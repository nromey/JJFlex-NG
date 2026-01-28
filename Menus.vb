Imports System.Collections
Imports System.Drawing
Imports JJTrace
Imports Radios

Public Class Menus
    ''' <summary>
    ''' Provide menu information for menus and submenus
    ''' </summary>
    Friend Class MenuInfo
        Public nBanks As Integer
        Public ReadOnly Property Bank As Integer
            Get
                If nBanks = 1 Then
                    Return 0
                Else
                    Return RigControl.MenuBank
                End If
            End Get
        End Property
        Private myMenus As Radios.AllRadios.MenuDescriptor(,)
        Public ReadOnly Property Menus As Radios.AllRadios.MenuDescriptor(,)
            Get
                If myMenus Is Nothing Then
                    Return RigControl.Menus
                Else
                    Return myMenus
                End If
            End Get
        End Property
        Private loc As Point
        Public ReadOnly Property Location As Point
            Get
                Return loc
            End Get
        End Property
        Private Subm As Boolean
        Public ReadOnly Property SubMenu As Boolean
            Get
                Return Subm
            End Get
        End Property

        ''' <summary>
        ''' base-level constructor
        ''' </summary>
        Public Sub New()
            Subm = False
            nBanks = RigControl.MenuBanks
            myMenus = Nothing
            loc = Nothing
        End Sub
        ''' <summary>
        ''' constructor for submenus
        ''' </summary>
        ''' <param name="m">submenu array</param>
        ''' <param name="l">submenu location</param>
        Public Sub New(m As Radios.AllRadios.MenuDescriptor(,), l As Point)
            Subm = True
            nBanks = 1
            myMenus = m
            loc = l
        End Sub
    End Class
    ''' <summary>
    ''' Provides the menu/submenu invocation data.
    ''' </summary>
    Friend InvocationInfo As MenuInfo = Nothing

    Private Class menuSource
        Private menu As Radios.AllRadios.MenuDescriptor
        Public ReadOnly Property Display As String
            Get
                Dim rv As String
                ' Submenus have letters.
                ' If we have more than 26, we'll use the number.
                If menu.isSubMenu And (menu.High < 26) Then
                    ' Note this menu number is 1 based.
                    rv = ChrW((Asc("A") + menu.Number - 1)).ToString
                Else
                    rv = CStr(menu.Number)
                End If
                rv &= " " & menu.Description
                Return rv
            End Get
        End Property
        ' Note that the actual menu's value, menu.Value, is read 
        ' from the rig whenever used.
        Public ReadOnly Property menuItem As Radios.AllRadios.MenuDescriptor
            Get
                Return menu
            End Get
        End Property
        Public Sub New(m As Radios.AllRadios.MenuDescriptor)
            menu = m
        End Sub
    End Class
    Private menuList As ArrayList()
    Private menuBank As Integer
    Private Class changed
        Public md As Radios.AllRadios.MenuDescriptor
        Public newValue As Object
        Public Sub New(m As Radios.AllRadios.MenuDescriptor, val As Object)
            md = m
            newValue = val
        End Sub
    End Class

    ' Fields in the ValueBox group box
    Private WithEvents MenuValueBox As New ComboBox
    Private WithEvents MenuValueText As New TextBox
    Private listLocation As Point

    Private change As changed
    Private realChange As Boolean ' true if a real change, not just initial setup.
    Private wasActive As Boolean
    Private Const menusNotSetup As String = "The menus aren't setup yet."
    Private Const numSubs As String = " submenus"

    Private Sub Menus_Load(sender As System.Object, e As System.EventArgs) Handles MyBase.Load
        ' Initial menu setup check.
        If RigControl.MenuBank = AllRadios.MenuBankNotSetup Then
            Tracing.TraceLine("Menus not setup", TraceLevel.Error)
            DialogResult = Windows.Forms.DialogResult.Abort
            Return
        End If
        If InvocationInfo Is Nothing Then
            ' This is the first call from the main program, not a submenu.
            InvocationInfo = New MenuInfo
            ' Quit if no menus yet.
            If InvocationInfo.Menus Is Nothing Then
                Tracing.TraceLine("menus:no menus yet", TraceLevel.Error)
                DialogResult = Windows.Forms.DialogResult.Abort
                Return
            End If
        End If
        ' activate/deactivate the menu bank box.
        If (Not InvocationInfo.SubMenu) AndAlso (RigControl.MenuBanks > 0) Then
            ' call from main program.
            Tracing.TraceLine("Menus:call from main", TraceLevel.Info)
            BankCombo.Enabled = True
            BankCombo.Visible = True
            BankLabel.Visible = True
        Else
            ' submenu, one menu bank.
            Tracing.TraceLine("Menus:call with submenus", TraceLevel.Info)
            BankCombo.Enabled = False
            BankCombo.Visible = False
            BankLabel.Visible = False
        End If
        ' setup the value group box.
        setupValueBox()
        DialogResult = Windows.Forms.DialogResult.None
        wasActive = False
        ' Get the bank in use.
        menuBank = InvocationInfo.Bank
        If menuList Is Nothing Then
            Tracing.TraceLine("Menus:setting up " & InvocationInfo.nBanks & " banks.", TraceLevel.Info)
            ReDim menuList(InvocationInfo.nBanks - 1)
            ' for each bank
            For bank As Integer = 0 To InvocationInfo.nBanks - 1
                ' Use a letter for the bank
                BankCombo.Items.Add(ChrW(AscW("A") + bank))
                menuList(bank) = New ArrayList
                ' for each menu item
                For i As Integer = 0 To InvocationInfo.Menus.GetLength(1) - 1
                    menuList(bank).Add(New menuSource(InvocationInfo.Menus(bank, i)))
                Next
            Next
        End If
        MenuListBox.DataSource = menuList(menuBank)
        MenuListBox.DisplayMember = "Display"
        MenuListBox.ValueMember = "menuItem"
        AddHandler MenuListBox.SelectedIndexChanged, AddressOf MenuListBox_SelectedIndexChanged
        ' Setup the current item.
        MenuListBox_SelectedIndexChanged(Nothing, Nothing)
        BankCombo.SelectedIndex = menuBank
    End Sub
    Private Sub setupValueBox()
        Tracing.TraceLine("setupValueBox", TraceLevel.Info)
        Static boxSetup As Boolean = False
        Tracing.TraceLine("Menus setupValueBox:" & boxSetup.ToString, TraceLevel.Info)
        If Not boxSetup Then
            ValueBox.SuspendLayout()
            listLocation = New Point(MenuListBox.Location.X, MenuListBox.Location.Y)
            ValueBox.Controls.Add(MenuValueBox)
            ValueBox.Controls.Add(MenuValueText)
            Dim loc As New Point(0, 20)
            MenuValueBox.Location = loc
            MenuValueText.Location = loc
            MenuValueBox.Size = New Size(200, 60)
            MenuValueText.Size = New Size(100, 20)
            AddHandler MenuValueBox.SelectedIndexChanged, AddressOf MenuValueBox_SelectedIndexChanged
            AddHandler MenuValueBox.Enter, AddressOf MenuValueBox_Enter
            AddHandler MenuValueBox.Leave, AddressOf MenuValueBox_Leave
            AddHandler MenuValueText.TextChanged, AddressOf MenuValueText_TextChanged
            ValueBox.SendToBack()
            ValueBox.ResumeLayout()
        End If
        boxSetup = True
    End Sub

    Private Sub MenuListBox_SelectedIndexChanged(sender As Object, e As EventArgs)
        Tracing.TraceLine("Menus MenuListBox_SelectedIndexChanged:" & MenuListBox.SelectedIndex.ToString, TraceLevel.Info)
        realChange = False ' setting up values isn't a change.
        MenuValueBox.Enabled = False
        MenuValueBox.Visible = False
        MenuValueText.Enabled = False
        MenuValueText.Visible = False
        Dim md As Radios.AllRadios.MenuDescriptor = MenuListBox.SelectedValue
        If md Is Nothing Then
            ShowInternalError(MenuMalfunction)
            Return
        End If
        Dim r As Rectangle = MenuListBox.GetItemRectangle(MenuListBox.SelectedIndex)
        ' Position the value box.
        ValueBox.Location = New Point(listLocation.X + r.X + r.Width, listLocation.Y + r.Y + r.Height)
        If md.HasSubMenus Then
            ' We'll put up the menu when the value is selected.
            MenuValueText.Enabled = True
            MenuValueText.Text = md.subMenus.Length.ToString & numSubs
            MenuValueText.Visible = True
            ValueBox.BringToFront()
        Else
            ' Setup MenuValueBox or MenuValueText.
            ' We need to ensure we're using the correct value.
            Dim val As Object = md.Value
            Select Case md.Type
                Case AllRadios.MenuTypes.Text
                    MenuValueText.Enabled = True
                    MenuValueText.Text = val
                    MenuValueText.Visible = True
                Case Else
                    MenuValueBox.SuspendLayout()
                    MenuValueBox.Enabled = True
                    MenuValueBox.Visible = True
                    MenuValueBox.Items.Clear()
                    Select Case md.Type
                        Case AllRadios.MenuTypes.OnOff
                            MenuValueBox.Items.Add(OffWord)
                            MenuValueBox.Items.Add(OnWord)
                            MenuValueBox.SelectedIndex = CType(val, Integer)
                        Case AllRadios.MenuTypes.Enumerated
                            MenuValueBox.SelectedIndex = -1
                            For i As Integer = 0 To md.Enumerants.Length - 1
                                Dim en As AllRadios.EnumAndValue = md.Enumerants(i)
                                MenuValueBox.Items.Add(en.Description)
                                If en.Value = val Then
                                    MenuValueBox.SelectedIndex = i
                                End If
                            Next
                        Case AllRadios.MenuTypes.NumberRange
                            For i As Integer = md.Low To md.High
                                MenuValueBox.Items.Add(CStr(i))
                            Next
                            MenuValueBox.SelectedIndex = CType(val, Integer)
                        Case AllRadios.MenuTypes.NumberRangeOff0
                            MenuValueBox.Items.Add(OffWord)
                            For i As Integer = 1 To md.High
                                MenuValueBox.Items.Add(CStr(i))
                            Next
                            MenuValueBox.SelectedIndex = CType(val, Integer)
                    End Select
                    MenuValueBox.Visible = True
                    MenuValueBox.ResumeLayout()
            End Select
        End If
        change = Nothing
        realChange = True ' allow changes now
    End Sub

    ''' <summary>
    ''' This runs when the value group box is entered
    ''' </summary>
    Private Sub MenuValueBox_Enter(sender As System.Object, e As System.EventArgs)
        Tracing.TraceLine("Menus MenuValueBox_Enter:", TraceLevel.Info)
        'change = Nothing
        AddHandler MenuValueBox.SelectedIndexChanged, AddressOf MenuValueBox_SelectedIndexChanged
    End Sub

    Private Sub MenuValueBox_Leave(sender As System.Object, e As System.EventArgs)
        Tracing.TraceLine("Menus MenuValueBox_Leave:", TraceLevel.Info)
        RemoveHandler MenuValueBox.SelectedIndexChanged, AddressOf MenuValueBox_SelectedIndexChanged
    End Sub

    Private Sub MenuValueBox_SelectedIndexChanged(sender As Object, e As EventArgs)
        If Not realChange Then
            Tracing.TraceLine("MenuValueBox_SelectedIndexChanged:no real change", TraceLevel.Info)
            Return
        End If
        Dim listID As Integer = MenuListBox.SelectedIndex
        Dim valueID As Integer = MenuValueBox.SelectedIndex
        Tracing.TraceLine("Menus MenuValueBox_SelectedIndexChanged:" & listID.ToString & " " & valueID.ToString, TraceLevel.Info)
        If (listID <> -1) And (valueID <> -1) Then
            Dim md As Radios.AllRadios.MenuDescriptor = MenuListBox.SelectedValue
            Select Case md.Type
                Case AllRadios.MenuTypes.Enumerated
                    change = New changed(MenuListBox.SelectedValue,
                                         md.Enumerants(valueID).Value)
                Case Else
                    change = New changed(MenuListBox.SelectedValue, valueID + md.Low)
            End Select
            changeMenu()
        End If
    End Sub

    Private Sub MenuValueText_TextChanged(sender As System.Object, e As System.EventArgs)
        If Not realChange Then
            Tracing.TraceLine("MenuValueText_TextChanged:no real change", TraceLevel.Info)
            Return
        End If
        Tracing.TraceLine("Menus MenuValueText_TextChanged:" & MenuValueText.Text, TraceLevel.Info)
        change = New changed(MenuListBox.SelectedValue, MenuValueText.Text)
    End Sub

    Private Sub changeMenu()
        If change IsNot Nothing Then
            Tracing.TraceLine("Menus Ok:" & change.ToString, TraceLevel.Info)
            change.md.Value = change.newValue ' change the rig's menu.
            change = Nothing
        End If
    End Sub

    Private Sub OKButton_Click(sender As System.Object, e As System.EventArgs) Handles OKButton.Click
        Tracing.TraceLine("OKButton_Click", TraceLevel.Info)
        ' See if any menu change is needed.
        changeMenu()
        DialogResult = Windows.Forms.DialogResult.OK
    End Sub

    Private Sub CnclButton_Click(sender As System.Object, e As System.EventArgs) Handles CnclButton.Click
        Tracing.TraceLine("Menus Cancel:", TraceLevel.Info)
        DialogResult = Windows.Forms.DialogResult.Cancel
    End Sub

    Private Sub Menus_Activated(sender As System.Object, e As System.EventArgs) Handles MyBase.Activated
        Tracing.TraceLine("Menus Menus_Active:" & wasActive.ToString, TraceLevel.Info)
        If Not wasActive Then
            wasActive = True
            MenuListBox.Focus()
        End If
    End Sub

    Private Sub BankCombo_SelectedIndexChanged(sender As System.Object, e As System.EventArgs) Handles BankCombo.SelectedIndexChanged
        Tracing.TraceLine("Menus BankCombo_SelectedIndexChanged:" & BankCombo.SelectedIndex.ToString, TraceLevel.Info)
        If (InvocationInfo.nBanks > 1) And (BankCombo.SelectedIndex > -1) Then
            If RigControl.MenuBank <> BankCombo.SelectedIndex Then
                RigControl.MenuBank = BankCombo.SelectedIndex
            End If
            menuBank = BankCombo.SelectedIndex
            MenuListBox.DataSource = menuList(menuBank)
        End If
    End Sub

    ''' <summary>
    ''' This runs when a value box is entered, either a submenu or a leaf.
    ''' </summary>
    Private Sub ValueBox_Enter(sender As System.Object, e As System.EventArgs) Handles ValueBox.Enter
        Tracing.TraceLine("Menus ValueBox_Enter:", TraceLevel.Info)
        Dim md As Radios.AllRadios.MenuDescriptor = MenuListBox.SelectedValue
        ValueBox.BringToFront()
        If md.HasSubMenus Then
            ' Get submenu display location
            Dim r As Rectangle = MenuListBox.GetItemRectangle(MenuListBox.SelectedIndex)
            Dim loc As New Point(listLocation.X + r.X + (r.Width / 5),
                                 listLocation.Y + r.Y + r.Height)
            ' Make a 2-dimentional array of submenus
            Dim subs As Radios.AllRadios.MenuDescriptor(,)
            ReDim subs(0, md.subMenus.Length - 1)
            For i As Integer = 0 To subs.GetLength(1) - 1
                subs(0, i) = md.subMenus(i)
            Next
            ' Create next level display 
            Dim m As New Menus
            m.InvocationInfo = New MenuInfo(subs, loc)
            m.ShowDialog()
            MenuListBox.Focus()
        End If
    End Sub

    Private Sub ValueBox_Leave(sender As System.Object, e As System.EventArgs) Handles ValueBox.Leave
        Tracing.TraceLine("Menus ValueBox_Leave:", TraceLevel.Info)
        ValueBox.SendToBack()
    End Sub

    Friend Sub Done()
        Tracing.TraceLine("Menus Done:", TraceLevel.Info)
        InvocationInfo = Nothing
        menuList = Nothing
        MenusLoaded = False
    End Sub
End Class
