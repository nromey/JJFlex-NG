Imports HamBands
Imports Radios

Public Class PersonalInfo
    Private wasActive As Boolean
    Const noDupMsg As String = "There is already an operator with this name and handle."
    Const noName As String = "The operator must have a name"
    Const BRLDispError As String = "The braille display size must be a positive, nonzero, value."
    Friend theOp As PersonalData.personal_v1
    Private originalOp As PersonalData.personal_v1
    Private updateFlag As Boolean

    Private Sub setupLicense()
        Static alreadyDone As Boolean = False
        If Not alreadyDone Then
            alreadyDone = True
            For Each l As Bands.Licenses In [Enum].GetValues(GetType(Bands.Licenses))
                LicenseList.Items.Add(l.ToString)
            Next
        End If
    End Sub

    Private Sub PersonalInfo_Load(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles MyBase.Load
        wasActive = False
        DialogResult = DialogResult.None
        setupLicense()
        originalOp = theOp
        If theOp Is Nothing Then
            theOp = New PersonalData.personal_v1
            theOp.DefaultFlag = _
                ((Operators Is Nothing) OrElse (Operators.Length = 0))
            ' New operators default to Modern mode.
            theOp.UIModeSetting = CInt(UIMode.Modern)
            theOp.UIModeDismissed = True  ' No upgrade prompt needed for new operators.
            OKButton.Text = "Add"
            updateFlag = False
        Else
            OKButton.Text = "Update"
            updateFlag = True
        End If
        ' Fill in the fields with any existing data.
        With theOp
            FullNameBox.Text = .fullName
            CallSignBox.Text = .callSign
            HandleBox.Text = .handl
            QTHBox.Text = .qth
            LicenseList.SelectedIndex = .License
            BRLSizeBox.Text = .BrailleDisplaySize
            AddressBox.Text = .ClusterHostname
            If .ClusterLoginName = vbNullString Then
                ClusterLoginNameBox.Text = .callSign
            Else
                ClusterLoginNameBox.Text = .ClusterLoginName
            End If
#If 0 Then
            If .HamqthID = vbNullString Then
                HamqthIDBox.Text = .callSign
            Else
                HamqthIDBox.Text = .HamqthID
            End If
            HamqthPasswordBox.Text = .HamqthPassword
#End If
            DefaultBox.Checked = .DefaultFlag
            ' Can't modify the default on an update if it's set.
            DefaultBox.Enabled = (Not (updateFlag And .DefaultFlag))

            ' Callbook lookup settings.
            Dim srcIdx = CallbookSourceCombo.Items.IndexOf(If(.CallbookLookupSource, "None"))
            CallbookSourceCombo.SelectedIndex = If(srcIdx >= 0, srcIdx, 0)
            CallbookUsernameBox.Text = If(.CallbookUsername, "")
            CallbookPasswordBox.Text = If(.CallbookPassword, "")
            UpdateCallbookFieldsEnabled()
        End With
        ' Now use a new theOp for an update.
        If updateFlag Then
            theOp = New PersonalData.personal_v1(originalOp)
        End If
    End Sub

    Private Sub LogButton_Click(sender As System.Object, e As System.EventArgs) Handles LogButton.Click
        ' We need to copy the file name on an update.
        If updateFlag And (theOp.LogFile = vbNullString) Then
            theOp.LogFile = originalOp.LogFile
        End If
        LogCharacteristics.theOp = theOp
        LogCharacteristics.ShowDialog()
    End Sub

    Private Sub OKButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles OKButton.Click
        With theOp
            ' Must have a name at least.
            If FullNameBox.Text = "" Then
                MsgBox(noName)
                FullNameBox.Focus()
                Return
            End If
            .fullName = Trim(FullNameBox.Text)
            .handl = Trim(HandleBox.Text)
            ' Can't have duplicate fullName and handle.
            If Operators IsNot Nothing Then
                For i As Integer = 0 To Operators.Length - 1
                    ' special case for update.
                    If (updateFlag AndAlso originalOp.Equals(Operators(i))) Then
                        Continue For
                    End If
                    If (.fullName.ToLower = Operators(i).fullName.ToLower) And _
                       (.handl.ToLower = Operators(i).handl.ToLower) Then
                        MsgBox(noDupMsg)
                        FullNameBox.Focus()
                        Return
                    End If
                Next
            End If
            .callSign = Trim(CallSignBox.Text)
            .qth = Trim(QTHBox.Text)
            If LicenseList.SelectedIndex = -1 Then
                .License = Bands.Licenses.none
            Else
                .License = LicenseList.SelectedIndex
            End If
            ' hostname may be host:port
            Dim clustHost As String = AddressBox.Text.Trim
            If IsValidHostname(clustHost) Then
                .ClusterHostname = clustHost
            Else
                MsgBox(NotValidHost)
                AddressBox.Focus()
                Return
            End If

            .ClusterLoginName = ClusterLoginNameBox.Text.Trim
#If 0 Then
            .HamqthID = HamqthIDBox.Text.Trim()
            .HamqthPassword = HamqthPasswordBox.Text.Trim
#End If
            ' Callbook lookup settings.
            .CallbookLookupSource = CallbookSourceCombo.SelectedItem?.ToString()
            If .CallbookLookupSource Is Nothing Then .CallbookLookupSource = "None"
            .CallbookUsername = CallbookUsernameBox.Text.Trim
            .CallbookPassword = CallbookPasswordBox.Text.Trim
            Dim n As Integer
            ' Default the braille display size if none given.
            If BRLSizeBox.Text = "" Then
                .BrailleDisplaySize = 0
                .CWDecodeCells = 0
            Else
                If System.Int32.TryParse(BRLSizeBox.Text, n) AndAlso
                   (n >= 0) Then
                    .BrailleDisplaySize = n
                    ' Set the decode size if not set.
                    If .CWDecodeCells = 0 Then
                        .CWDecodeCells = n
                    End If
                Else
                    MsgBox(BRLDispError)
                    Return
                End If
            End If

            If .Profiles Is Nothing Then
                ' Default profile, global and tx, must be a new user.
                .Profiles = New List(Of Profile_t)
                Dim profileName As String = Profile_t.GenerateProfileName(.callSign)
                .Profiles.Add(New Profile_t(profileName, ProfileTypes.global, True))
                .Profiles.Add(New Profile_t("Default", ProfileTypes.tx, True))
            End If

            .DefaultFlag = DefaultBox.Checked
        End With
        DialogResult = DialogResult.OK
    End Sub

    Private Sub CnclButton_Click(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles CnclButton.Click
        DialogResult = DialogResult.Cancel
    End Sub

    Private Sub PersonalInfo_Activated(ByVal sender As System.Object, ByVal e As System.EventArgs) Handles MyBase.Activated
        If Not wasActive Then
            ' dialogue just loaded.
            wasActive = True
            FullNameBox.Focus()
        End If
    End Sub

    Private Sub ClusterLoginNameBox_Enter(sender As System.Object, e As System.EventArgs) Handles ClusterLoginNameBox.Enter
        If ClusterLoginNameBox.Text = "" Then
            ClusterLoginNameBox.Text = CallSignBox.Text
        End If
    End Sub

    ''' <summary>
    ''' Enable/disable callbook username and password based on the selected source.
    ''' "None" disables the credential fields.
    ''' </summary>
    Private Sub UpdateCallbookFieldsEnabled()
        Dim enabled = (CallbookSourceCombo.SelectedItem?.ToString() <> "None")
        CallbookUsernameBox.Enabled = enabled
        CallbookPasswordBox.Enabled = enabled
        CallbookUsernameLabel.Enabled = enabled
        CallbookPasswordLabel.Enabled = enabled
    End Sub

    Private Sub CallbookSourceCombo_SelectedIndexChanged(sender As Object, e As EventArgs) Handles CallbookSourceCombo.SelectedIndexChanged
        UpdateCallbookFieldsEnabled()
    End Sub
End Class