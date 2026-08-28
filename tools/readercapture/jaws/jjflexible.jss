; jjflexible.jss - JAWS half of the JJ Flexible reader-side capture instrument.
;
; WHAT THIS IS FOR
; JJ Flexible's own trace records what the APPLICATION SENT. It writes "Spoke"
; whether or not anything was ever heard. This script records what JAWS
; RECEIVED and what JAWS actually SPOKE, with timestamps, in the same JSON
; Lines format the NVDA half writes, so a capture can be laid beside the
; application trace and read rather than listened to.
;
; It is a debugging instrument for the developers and for testers who agree to
; run it. It is NOT something an operator installs in order to use JJ Flexible.
;
; HOW EACH CHANNEL IS OBSERVED, and why
;
;   SPEECH. JJ Flexible reaches JAWS through the COM object
;   freedomsci.jawsapi, method SayString. That call does NOT pass through the
;   script layer, so it cannot be intercepted from here. What CAN be observed
;   is the other end: GetSpeechHistory returns what JAWS actually put into
;   speech. This script polls it and stamps each new line with the time the
;   poll saw it. Resolution is therefore the poll interval, not the utterance.
;
;   BRAILLE. JJ Flexible reaches JAWS braille through
;   RunFunction("BrailleString(...)"), which IS the script layer, and JAWS
;   resolves a function name in the application script file before the
;   built-in. So the override below sees the string as it ARRIVES. It forwards
;   to Builtin::BrailleString first and logs second, so a fault in the logging
;   can never cost the operator their braille.
;
; THE DISCRIMINATION THIS EXISTS TO MAKE
;   In the application trace but in NEITHER record here: it never arrived.
;   Recorded as braille received but never as speech: it arrived and was
;   dropped on the way to the synth. Those need opposite fixes.
;
; POSITIVE CONTROL. An instrument that records nothing looks exactly like a
; reader that received nothing. Nothing this script exports is presented as
; evidence unless a control token was emitted and came back. The exported
; header says so in its first line, either way.

include "hjConst.jsh"
include "hjGlobal.jsh"
include "common.jsm"

const
    JJFCAP_POLL_TENTHS = 2,        ; ScheduleFunction resolution is tenths of a second
    JJFCAP_RING_MAX = 500,
    JJFCAP_CONTROL_NONE = 0,
    JJFCAP_CONTROL_ARMED = 1,
    JJFCAP_CONTROL_PASSED = 2,
    JJFCAP_CONTROL_FAILED = 3

globals
    object gJjfCapStream,
    string gJjfCapPath,
    string gJjfCapSessionWall,
    int gJjfCapT0,
    int gJjfCapSeq,
    int gJjfCapOn,
    int gJjfCapTimer,
    int gJjfCapFileOk,
    string gJjfCapLastHistory,
    int gJjfCapLastSegments,
    string gJjfCapRing,
    int gJjfCapRingCount,
    string gJjfCapControlToken,
    int gJjfCapControlArmedMono,
    int gJjfCapControlState,
    string gJjfCapControlChannels

;=======================================================================
; time
;=======================================================================

; Wall clock to the second. SysGetTime has no sub-second form, so the
; millisecond ordering lives in the monotonic field instead. Do not "improve"
; this by inventing a millisecond value the platform did not give us.
string function JjfCapWallNow ()
return SysGetDate ("yyyy-MM-dd") + "T" + SysGetTime ("HH:mm:ss")
endFunction

int function JjfCapMono ()
return GetTickCount () - gJjfCapT0
endFunction

; There is no GetJAWSVersion. The version is assembled from the three cached
; numbers InitFSProductVersionInfo populates.
string function JjfCapReaderVersion ()
return IntToString (GetJAWSMajorVersionNumber ()) + "."
    + IntToString (GetJAWSMinorVersionNumber ()) + "."
    + IntToString (GetJAWSBuildVersionNumber ())
endFunction

;=======================================================================
; JSON
;=======================================================================

string function JjfCapEscape (string sText)
var string s
s = sText
s = StringReplaceSubstrings (s, "\\", "\\\\")
s = StringReplaceSubstrings (s, "\"", "\\\"")
s = StringReplaceSubstrings (s, "\r", "\\r")
s = StringReplaceSubstrings (s, "\n", "\\n")
s = StringReplaceSubstrings (s, "\t", "\\t")
return s
endFunction

;=======================================================================
; sink
;=======================================================================

void function JjfCapOpen ()
var
    object oFSO,
    string sDir,
    string sStamp

gJjfCapFileOk = FALSE
gJjfCapPath = ""
oFSO = CreateObject ("Scripting.FileSystemObject")
if (!oFSO) then
    return
endIf
sDir = GetEnvironmentVariable ("LOCALAPPDATA")
if (StringIsBlank (sDir)) then
    sDir = GetEnvironmentVariable ("TEMP")
endIf
if (StringIsBlank (sDir)) then
    return
endIf
sDir = sDir + "\\jjfcapture"
if (!oFSO.FolderExists (sDir)) then
    oFSO.CreateFolder (sDir)
endIf
; The tick count is part of the name, not decoration. Two sessions starting
; inside one second would otherwise append to the same file, silently merging
; two captures into one and making the record lie about what happened.
sStamp = SysGetDate ("yyyyMMdd") + "-" + SysGetTime ("HHmmss")
    + "-" + IntToString (GetTickCount ())
gJjfCapPath = sDir + "\\jjfcapture-jaws-" + sStamp + ".jsonl"
; 8 = ForAppending, TRUE = create if missing
gJjfCapStream = oFSO.OpenTextFile (gJjfCapPath, 8, TRUE)
if (gJjfCapStream) then
    gJjfCapFileOk = TRUE
endIf
endFunction

void function JjfCapEmit (string sChannel, string sEvent, string sText, string sExtraJson)
var string sLine, string sHuman

if (!gJjfCapOn && sEvent != "session" && sEvent != "selftest") then
    return
endIf
gJjfCapSeq = gJjfCapSeq + 1
sLine = "{\"v\":1,\"seq\":" + IntToString (gJjfCapSeq)
    + ",\"t\":\"" + JjfCapWallNow () + "\""
    + ",\"mono\":" + IntToString (JjfCapMono ())
    + ",\"reader\":\"jaws\""
    + ",\"ch\":\"" + sChannel + "\""
    + ",\"ev\":\"" + sEvent + "\""
    + ",\"text\":\"" + JjfCapEscape (sText) + "\""
if (!StringIsBlank (sExtraJson)) then
    sLine = sLine + "," + sExtraJson
endIf
sLine = sLine + "}"

if (gJjfCapFileOk) then
    gJjfCapStream.WriteLine (sLine)
endIf

sHuman = JjfCapWallNow () + "  +" + IntToString (JjfCapMono ()) + "ms  "
    + sChannel + " " + sEvent + " | text: " + sText
gJjfCapRing = gJjfCapRing + sHuman + "\r\n"
gJjfCapRingCount = gJjfCapRingCount + 1
if (gJjfCapRingCount > JJFCAP_RING_MAX) then
    ; Drop the oldest line so a long session cannot grow without bound. The
    ; file on disk keeps everything; only the pasteable ring is trimmed.
    gJjfCapRing = StringSegmentRemove (gJjfCapRing, "\n", 1)
    gJjfCapRingCount = gJjfCapRingCount - 1
endIf
endFunction

;=======================================================================
; speech: poll what JAWS actually spoke
;=======================================================================

void function JjfCapEmitHistorySegment (string sSegment)
if (StringIsBlank (sSegment)) then
    return
endIf
JjfCapEmit ("speech", "emitted", sSegment, "\"via\":\"GetSpeechHistory\"")
endFunction

void function JjfCapPoll ()
var
    string sNow,
    int nNow,
    int nOld,
    int i,
    int nNew

gJjfCapTimer = 0
if (gJjfCapOn) then
    sNow = GetSpeechHistory (FALSE)
    if (sNow != gJjfCapLastHistory) then
        nNow = StringSegmentCount (sNow, "\n")
        nOld = gJjfCapLastSegments
        if (StringStartsWith (sNow, gJjfCapLastHistory) && nNow >= nOld) then
            ; history grows at the end: the new lines are the last nNow-nOld
            i = nOld + 1
            while (i <= nNow)
                JjfCapEmitHistorySegment (StringSegment (sNow, "\n", i))
                i = i + 1
            endWhile
        else
            ; Nested rather than "else if" on one line: no script that ships
            ; with JAWS uses that form, and this file cannot be compiled here
            ; to find out whether it is accepted.
            if (StringEndsWith (sNow, gJjfCapLastHistory) && nNow >= nOld) then
                ; history grows at the front: the new lines are the first
                ; nNow-nOld, newest first, so walk them backwards to keep the
                ; record in the order they actually happened
                nNew = nNow - nOld
                i = nNew
                while (i >= 1)
                    JjfCapEmitHistorySegment (StringSegment (sNow, "\n", i))
                    i = i - 1
                endWhile
            else
                ; The buffer rolled or was cleared. Everything currently in it
                ; is re-emitted and FLAGGED, because some of it may duplicate
                ; what was already recorded. A silent guess here would be worse
                ; than a labelled one.
                i = 1
                while (i <= nNow)
                    JjfCapEmit ("speech", "emitted", StringSegment (sNow, "\n", i),
                        "\"via\":\"GetSpeechHistory\",\"resync\":true")
                    i = i + 1
                endWhile
            endIf
        endIf
        gJjfCapLastHistory = sNow
        gJjfCapLastSegments = nNow
    endIf
    if (gJjfCapControlState == JJFCAP_CONTROL_ARMED
        && JjfCapMono () - gJjfCapControlArmedMono > 3000) then
        JjfCapControlResolve ()
    endIf
endIf
gJjfCapTimer = ScheduleFunction ("JjfCapPoll", JJFCAP_POLL_TENTHS)
endFunction

;=======================================================================
; braille: observed as it arrives
;=======================================================================

; JJ Flexible sends braille as RunFunction("BrailleString(...)"). JAWS resolves
; a function name in the application script file before the built-in, so this
; override sees the string on the way in. Forward FIRST: if anything below
; throws, the operator still gets their braille.
void function BrailleString (string sText)
Builtin::BrailleString (sText)
JjfCapEmit ("braille", "received", sText, "\"via\":\"BrailleString\"")
endFunction

;=======================================================================
; positive control
;=======================================================================

void function JjfCapControlResolve ()
var string sExtra
gJjfCapControlChannels = ""
if (StringContains (gJjfCapRing, gJjfCapControlToken)) then
    gJjfCapControlState = JJFCAP_CONTROL_PASSED
    gJjfCapControlChannels = "speech"
else
    gJjfCapControlState = JJFCAP_CONTROL_FAILED
endIf
sExtra = "\"phase\":\"result\",\"token\":\"" + gJjfCapControlToken + "\""
if (gJjfCapControlState == JJFCAP_CONTROL_PASSED) then
    sExtra = sExtra + ",\"passed\":true,\"channels\":\"speech\""
    JjfCapEmit ("meta", "selftest", "positive control resolved", sExtra)
    SayMessage (OT_STATUS, "Positive control passed. The capture is recording.")
else
    sExtra = sExtra + ",\"passed\":false"
    JjfCapEmit ("meta", "selftest", "positive control resolved", sExtra)
    SayMessage (OT_STATUS,
        "Positive control failed. The capture is not recording. Do not trust an empty result.")
endIf
endFunction

;=======================================================================
; header and export
;=======================================================================

string function JjfCapHeader ()
var string s
s = "JJ Flexible reader capture\r\n"
if (gJjfCapControlState == JJFCAP_CONTROL_NONE
    || gJjfCapControlState == JJFCAP_CONTROL_ARMED) then
    s = s + "NO POSITIVE CONTROL RAN IN THIS SESSION. An empty or short capture "
        + "proves nothing: a broken instrument and a silent reader look identical "
        + "here. Run the control, then capture again.\r\n"
else
    if (gJjfCapControlState == JJFCAP_CONTROL_FAILED) then
        s = s + "POSITIVE CONTROL FAILED. Token " + gJjfCapControlToken
            + " was emitted and never came back, so this instrument is not recording. "
            + "Draw no conclusions from anything below.\r\n"
    else
        s = s + "Positive control passed. Token " + gJjfCapControlToken
            + " came back on " + gJjfCapControlChannels
            + ", so the capture below was live for this session.\r\n"
    endIf
endIf
s = s + "Reader: JAWS " + JjfCapReaderVersion () + "\r\n"
s = s + "Record format: version 1, JSON Lines.\r\n"
if (gJjfCapFileOk) then
    s = s + "Full machine-readable capture: " + gJjfCapPath + "\r\n"
else
    s = s + "NO FILE WAS OPENED. Only the lines below exist; nothing was written to disk.\r\n"
endIf
s = s + "Speech is observed by polling GetSpeechHistory every "
    + IntToString (JJFCAP_POLL_TENTHS)
    + " tenths of a second, so speech timestamps are accurate to the poll, not to the utterance.\r\n"
s = s + "Braille is observed as it arrives, so braille timestamps are exact.\r\n"
s = s + "Records below: " + IntToString (gJjfCapRingCount) + ".\r\n\r\n"
return s
endFunction

;=======================================================================
; lifecycle
;=======================================================================

void function AutoStartEvent ()
gJjfCapT0 = GetTickCount ()
gJjfCapSeq = 0
gJjfCapOn = TRUE
gJjfCapRing = ""
gJjfCapRingCount = 0
gJjfCapLastHistory = ""
gJjfCapLastSegments = 0
gJjfCapControlState = JJFCAP_CONTROL_NONE
gJjfCapControlToken = ""
gJjfCapSessionWall = JjfCapWallNow ()
JjfCapOpen ()
JjfCapEmit ("meta", "session", "capture session started",
    "\"reader_version\":\"" + JjfCapReaderVersion () + "\",\"format\":1,\"hooks\":\"BrailleString,GetSpeechHistory\"")
; Prime the history baseline so the first poll does not re-emit whatever was
; already in the buffer before we attached.
gJjfCapLastHistory = GetSpeechHistory (FALSE)
gJjfCapLastSegments = StringSegmentCount (gJjfCapLastHistory, "\n")
gJjfCapTimer = ScheduleFunction ("JjfCapPoll", JJFCAP_POLL_TENTHS)
endFunction

void function AutoFinishEvent ()
if (gJjfCapTimer) then
    UnscheduleFunction (gJjfCapTimer)
    gJjfCapTimer = 0
endIf
JjfCapEmit ("meta", "session", "capture session ended", "")
if (gJjfCapFileOk) then
    gJjfCapStream.Close ()
    gJjfCapFileOk = FALSE
endIf
gJjfCapStream = null ()
endFunction

;=======================================================================
; scripts
;=======================================================================

Script JJFCaptureCopy ()
CopyToClipboard (JjfCapHeader () + gJjfCapRing)
if (gJjfCapControlState == JJFCAP_CONTROL_PASSED) then
    SayMessage (OT_STATUS, "Capture copied. Positive control passed.")
else
    ; Said out loud on purpose. The most expensive mistake this instrument
    ; could cause is someone trusting an unvalidated capture.
    SayMessage (OT_STATUS,
        "Capture copied. No positive control passed, so an empty capture proves nothing.")
endIf
EndScript

Script JJFCaptureToggle ()
if (gJjfCapOn) then
    gJjfCapOn = FALSE
    SayMessage (OT_STATUS, "Reader capture paused.")
else
    gJjfCapOn = TRUE
    SayMessage (OT_STATUS, "Reader capture on.")
endIf
EndScript

Script JJFCaptureMarker ()
JjfCapEmit ("meta", "marker", "operator marker", "")
SayMessage (OT_STATUS, "Marker.")
EndScript

Script JJFCaptureControl ()
gJjfCapControlToken = "jjfcap" + StringRight (IntToString (GetTickCount ()), 4)
gJjfCapControlState = JJFCAP_CONTROL_ARMED
gJjfCapControlArmedMono = JjfCapMono ()
; scope "internal": this proves the polling loop is attached and recording. It
; does NOT prove the COM door JJ Flexible actually comes through is open. For
; that, run jaws-ingress-control.ps1 while JJ Flexible has focus.
JjfCapEmit ("meta", "selftest", "positive control armed",
    "\"phase\":\"begin\",\"token\":\"" + gJjfCapControlToken
    + "\",\"scope\":\"internal\",\"how\":\"SayString\"")
Builtin::SayString (gJjfCapControlToken)
EndScript
