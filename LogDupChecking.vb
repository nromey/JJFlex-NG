Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports adif
Imports JJLogLib
Imports JJTrace

Public Class LogDupChecking
    ''' <summary>
    ''' duplicate types
    ''' </summary>
    Public Enum DupTypes
        none
        justCall
        callAndBand
        callAndBandAndMode
    End Enum

    ''' <summary>
    ''' A logged contact's band key, the one the duplicate check and the ADIF
    ''' export both use (repair round 2 of junk box batch 31): the logged BAND
    ''' through HamBands.Bands.ContactBandKey's aliases, or, when it is empty,
    ''' ADIF's band for the transmit frequency, else the receive frequency. Each
    ''' frequency is parsed once by FreqInt64; Nothing, "" or text it cannot read
    ''' counts as missing, so the stored mm.kkk.hhh form and the export's MHz text
    ''' give the same key.
    ''' </summary>
    Public Shared Function BandKey(band As String, txFreq As String, rxFreq As String) As String
        Return HamBands.Bands.ContactBandKey(band, HertzOf(txFreq), HertzOf(rxFreq))
    End Function

    ''' <summary>The session's band key, from its BAND, TXFREQ and FREQ (a log form without them reads as empty).</summary>
    Public Shared Function BandKey(session As LogSession) As String
        Return BandKey(session.GetFieldText(AdifTags.ADIF_Band, False),
                       session.GetFieldText(AdifTags.ADIF_TXFreq, False),
                       session.GetFieldText(AdifTags.ADIF_RXFreq, False))
    End Function

    Private Shared Function HertzOf(freq As String) As ULong
        If String.IsNullOrEmpty(freq) Then Return 0UL
        Try
            Return FreqInt64(freq)
        Catch ex As Exception
            Return 0UL   ' too many digits for a number: unreadable
        End Try
    End Function

    Public Class keyElement
        Public Value As String
        Public ID As String
        Public Sub New(session As LogSession, typ As DupTypes)
            ' Set the dictionary key according to the type. The band is the
            ' contact's band key, so an old entry and a new one on the same band
            ' match although their stored BAND differs (repair round 2).
            Select Case typ
                Case DupTypes.justCall
                    Value = session.GetFieldText(AdifTags.ADIF_Call)
                Case DupTypes.callAndBand
                    Value = session.GetFieldText(AdifTags.ADIF_Call) &
                        BandKey(session)
                Case DupTypes.callAndBandAndMode
                    Value = session.GetFieldText(AdifTags.ADIF_Call) &
                        BandKey(session) &
                        session.GetFieldText(AdifTags.ADIF_Mode)
            End Select
            Value = Value.ToLower
            ID = New String(session.GetFieldText(AdifTags.ADIF_SentSerial))
        End Sub
    End Class

    Private Class dupCheckElement
        Public Key As String
        Public count As Integer
        Public recordIDs As List(Of Integer)
        Public Sub New()
            recordIDs = New List(Of Integer)
        End Sub
        Public Sub New(k As keyElement)
            count = 1
            Key = k.Value
            recordIDs = New List(Of Integer)
            Try
                Dim i As Integer = k.ID
                recordIDs.Add(i)
            Catch ex As Exception
                Tracing.TraceLine("new DupCheckElement exception:" & k.ID & " " & ex.Message, TraceLevel.Error)
            End Try
        End Sub
    End Class

    Private dups As Dictionary(Of String, dupCheckElement)

    ''' <summary>
    ''' dup checking type
    ''' </summary>
    Public dupType As DupTypes

    ''' <summary>
    ''' new dup checking
    ''' </summary>
    ''' <param name="typ">the dup type</param>
    Public Sub New(typ As DupTypes)
        Tracing.TraceLine("LogDupChecking:" & typ.ToString, TraceLevel.Info)
        dups = New Dictionary(Of String, dupCheckElement)
        dupType = typ
    End Sub

    Public Sub AddToDictionary(key As keyElement)
        Dim el As dupCheckElement = Nothing
        If dups.TryGetValue(key.Value, el) Then
            Dim i As Integer = key.ID
            ' only add if unique.
            If el.recordIDs.IndexOf(i) = -1 Then
                el.count += 1
                el.recordIDs.Add(i)
            End If
        Else
            el = New dupCheckElement(key)
            dups.Add(key.Value, el)
        End If
    End Sub

    Public Sub RemoveFromDictionary(key As keyElement)
        Dim el As dupCheckElement = Nothing
        If dups.TryGetValue(key.Value, el) Then
            Dim i As Integer = el.recordIDs.IndexOf(key.ID)
            If i <> -1 Then
                If el.count = 1 Then
                    dups.Remove(key.Value)
                Else
                    el.count -= 1
                    el.recordIDs.RemoveAt(i)
                End If
            End If
        End If
    End Sub

    Public Function DupTest(key As keyElement) As Integer
        Dim el As dupCheckElement = Nothing
        Dim rv As Integer = 0
        If dups.TryGetValue(key.Value, el) Then
            rv = el.count
        End If
        Return rv
    End Function
End Class
