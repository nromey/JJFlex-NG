; jjbrailletrial.jss - the braille trial's JAWS application script (register #347).
;
; DRAFT. NEVER COMPILED. Written against JAWS 2026's builtin.jsd and shipped
; scripts, and checked for names, argument counts and block balance by
; tools/readercapture/verify/verify_jaws_script.py, but scompile.exe cannot
; compile a file outside a JAWS settings folder (established 2026-08-28,
; #347), so the first compile is compile-jaws-trial.ps1 run with the operator
; present. Expect the compiler to have opinions. NVDA is tested first; this is
; for after NVDA passes all four steps.
;
; THROWAWAY TRIAL CODE, scoped to jjbrailletrial.exe: JAWS loads it only
; while the trial program is in front. It is not the shipped script.
;
; The same contract as the NVDA app module (braille Home design sections 4.4
; and 4.7):
; - Claims the trial's Home control, automation id JJFlexHome, as custom
;   control 1 (see jjbrailletrial.jbs) and paints the line in its value
;   component; if JAWS never asks BrailleCallbackObjectIdentify (it is called
;   for objects of unknown type, and whether Home counts is unmeasured), the
;   name component paints the same line instead. The status key says which.
; - Reads the view string from the carrier element, JJFlexBrailleView, through
;   FreedomSci.UIA in the raw view, each time JAWS rebuilds the line (about
;   ten times a second, by itself).
; - Paints each field as its own segment with BrailleAddStringWithCallback,
;   padded to the widest it has been (design section 3.3), so a routing press
;   reaches JjbtRouted with the field's ordinal. The character comes from
;   GetLastBrailleRoutingKey and BrailleGetDataOffsetFromDisplayOffset.
; - Posts Hello when Home gains focus, Route on a press, Command from a key.
;
; Known gaps, all unmeasured:
; - No cursor. A segment carries no cursor position; where JAWS puts its
;   cursor on this line is for the first session to observe.
; - Whether the data offset counts from the start of the whole line or of the
;   segment, and whether it allows for panning. JjbtRouted assumes the whole
;   line and logs every number it used, so one session settles it.
; - Whether the rebuild keeps the operator's pan position while values change
;   (the trial's step 3).
;
; Keys, inside the trial program only (jjbrailletrial.jkm):
;   Insert+Alt+Shift+B  status: script loaded, display size, carrier found,
;                       how many times each path painted. Press this first.
;   Insert+Alt+Shift+V  switch between the Home view and the field view.
;
; Log: %LOCALAPPDATA%\jjbrailletrial\jaws-trial-<date>-<time>.log

include "hjconst.jsh"
include "UIA.jsh"
; CreateUIARawViewTreeWalker is defined in UIA.jss, which default.jss links
; with use "UIA.jsb"; the import gives the compiler its declaration, as
; ExplorerFrame.jss does.
import "UIA.jsd"

const
    JJBT_HOME_ID = "JJFlexHome",
    JJBT_CARRIER_ID = "JJFlexBrailleView",
    JJBT_FORMAT = "JJFV1",
    JJBT_PROTOCOL = 1,
    JJBT_READER_JAWS = 2,
    JJBT_CUSTOM = 1,
    JJBT_COMMAND_SWITCH_VIEW = 1,
    JJBT_LOG_EVERY = 50

globals
    int gJjbtHelloMsg,
    int gJjbtRouteMsg,
    int gJjbtCommandMsg,
    handle gJjbtWindow,
    object gJjbtCarrier,
    int gJjbtLayout,
    int gJjbtRevision,
    string gJjbtWidths,
    int gJjbtWidthsLayout,
    string gJjbtSegOrdinals,
    string gJjbtSegStarts,
    string gJjbtSegLengths,
    int gJjbtPaintsCustom,
    int gJjbtPaintsName,
    int gJjbtLastLoggedRevision,
    object gJjbtLog,
    int gJjbtLogOk

;=======================================================================
; log
;=======================================================================

void function JjbtLogOpen ()
var
    object oFSO,
    string sDir
gJjbtLogOk = FALSE
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
sDir = sDir + "\\jjbrailletrial"
if (!oFSO.FolderExists (sDir)) then
    oFSO.CreateFolder (sDir)
endIf
; 8 = ForAppending, TRUE = create if missing
gJjbtLog = oFSO.OpenTextFile (sDir + "\\jaws-trial-" + SysGetDate ("yyyyMMdd") + "-" + SysGetTime ("HHmmss") + ".log", 8, TRUE)
if (gJjbtLog) then
    gJjbtLogOk = TRUE
endIf
endFunction

void function JjbtLog (string sText)
if (gJjbtLogOk) then
    gJjbtLog.WriteLine (SysGetDate ("yyyy-MM-dd") + " " + SysGetTime ("HH:mm:ss") + "  " + sText)
endIf
endFunction

;=======================================================================
; lifecycle
;=======================================================================

void function AutoStartEvent ()
gJjbtHelloMsg = RegisterWindowMessage ("JJFlexBraille_Hello")
gJjbtRouteMsg = RegisterWindowMessage ("JJFlexBraille_Route")
gJjbtCommandMsg = RegisterWindowMessage ("JJFlexBraille_Command")
gJjbtCarrier = Null ()
gJjbtWidths = ""
gJjbtWidthsLayout = 0
gJjbtPaintsCustom = 0
gJjbtPaintsName = 0
gJjbtLastLoggedRevision = 0
JjbtLogOpen ()
JjbtLog ("script started; messages hello " + IntToString (gJjbtHelloMsg)
    + " route " + IntToString (gJjbtRouteMsg) + " command " + IntToString (gJjbtCommandMsg))
endFunction

void function AutoFinishEvent ()
JjbtLog ("script finished; painted " + IntToString (gJjbtPaintsCustom) + " times as the custom control, "
    + IntToString (gJjbtPaintsName) + " times in the name component")
if (gJjbtLogOk) then
    gJjbtLog.Close ()
    gJjbtLogOk = FALSE
endIf
gJjbtLog = Null ()
gJjbtCarrier = Null ()
endFunction

void function FocusChangedEventEx (
    handle hwndFocus, int nObject, int nChild,
    handle hwndPrevFocus, int nPrevObject, int nPrevChild,
    int nChangeDepth)
FocusChangedEventEx (
    hwndFocus, nObject, nChild,
    hwndPrevFocus, nPrevObject, nPrevChild,
    nChangeDepth)
if (GetObjectAutomationId () == JJBT_HOME_ID) then
    JjbtOnHome ()
endIf
endFunction

void function JjbtOnHome ()
var
    int nRows,
    int nCells
gJjbtWindow = GetFocus ()
gJjbtCarrier = JjbtFindCarrier ()
nCells = BrailleGetCellCount ()
nRows = BrailleGetLineCount ()
if (nRows < 1) then
    nRows = 1
endIf
PostMessage (gJjbtWindow, gJjbtHelloMsg, JJBT_READER_JAWS * 65536 + JJBT_PROTOCOL, nRows * 65536 + nCells)
if (gJjbtCarrier) then
    JjbtLog ("Home has focus; hello posted, " + IntToString (nRows) + " row(s) of " + IntToString (nCells) + " cells; carrier found")
else
    JjbtLog ("Home has focus; hello posted, " + IntToString (nRows) + " row(s) of " + IntToString (nCells) + " cells; carrier NOT found")
endIf
endFunction

;=======================================================================
; the carrier
;=======================================================================

; The carrier sits beside Home in the raw view (it is neither a control nor
; content, so the control view does not show it). Walk Home's siblings.
object function JjbtFindCarrier ()
var
    object walker,
    int nGuard
walker = CreateUIARawViewTreeWalker (TRUE)
if (!walker) then
    return Null ()
endIf
nGuard = 0
while (nGuard < 50 && walker.GoToNextSibling ())
    nGuard = nGuard + 1
    if (walker.currentElement.automationID == JJBT_CARRIER_ID) then
        return walker.currentElement
    endIf
endWhile
walker = CreateUIARawViewTreeWalker (TRUE)
if (!walker) then
    return Null ()
endIf
nGuard = 0
while (nGuard < 50 && walker.GoToPriorSibling ())
    nGuard = nGuard + 1
    if (walker.currentElement.automationID == JJBT_CARRIER_ID) then
        return walker.currentElement
    endIf
endWhile
return Null ()
endFunction

string function JjbtReadView ()
var object fresh
if (!gJjbtCarrier) then
    gJjbtCarrier = JjbtFindCarrier ()
endIf
if (!gJjbtCarrier) then
    return ""
endIf
; FreedomSci.UIA elements answer from their cache; refresh it to read the
; Name as it is now.
fresh = gJjbtCarrier.BuildUpdatedCache ()
if (fresh) then
    return fresh.name
endIf
return gJjbtCarrier.name
endFunction

;=======================================================================
; widths: each field keeps the widest it has been (design section 3.3)
;=======================================================================

int function JjbtKeepWidth (int nOrdinal, int nLength)
var
    int i,
    int nCount,
    int nOld,
    int nWidth,
    string sOut
if (gJjbtWidthsLayout != gJjbtLayout) then
    gJjbtWidths = ""
    gJjbtWidthsLayout = gJjbtLayout
endIf
nWidth = nLength
nCount = 0
if (!StringIsBlank (gJjbtWidths)) then
    nCount = StringSegmentCount (gJjbtWidths, ",")
endIf
sOut = ""
i = 1
while (i <= nCount || i <= nOrdinal)
    nOld = 0
    if (i <= nCount) then
        nOld = StringToInt (StringSegment (gJjbtWidths, ",", i))
    endIf
    if (i == nOrdinal) then
        if (nOld > nWidth) then
            nWidth = nOld
        endIf
        nOld = nWidth
    endIf
    if (i > 1) then
        sOut = sOut + ","
    endIf
    sOut = sOut + IntToString (nOld)
    i = i + 1
endWhile
gJjbtWidths = sOut
return nWidth
endFunction

string function JjbtPad (string sText, int nWidth)
var string s
s = sText
while (StringLength (s) < nWidth)
    s = s + " "
endWhile
return s
endFunction

; The value at the position of nOrdinal in the painted segment lists, or -1.
int function JjbtSegValue (string sList, int nOrdinal)
var
    int i,
    int nCount
nCount = StringSegmentCount (gJjbtSegOrdinals, ",")
i = 1
while (i <= nCount)
    if (StringToInt (StringSegment (gJjbtSegOrdinals, ",", i)) == nOrdinal) then
        return StringToInt (StringSegment (sList, ",", i))
    endIf
    i = i + 1
endWhile
return -1
endFunction

;=======================================================================
; painting
;=======================================================================

; Paint the line from the carrier. Returns FALSE, painting nothing, when there
; is no usable view; the caller then lets JAWS do what it would have done.
int function JjbtPaint ()
var
    string sView,
    string sHeader,
    string sWhich,
    string sLine,
    string sText,
    string sPadded,
    int nParts,
    int nLines,
    int nCaret,
    int nMark,
    int nAttrib,
    int nOrdinal,
    int nWidth,
    int nPos,
    int nShown,
    int i
sView = JjbtReadView ()
if (StringIsBlank (sView)) then
    return FALSE
endIf
sHeader = StringSegment (sView, "\n", 1)
nParts = StringSegmentCount (sHeader, "\t")
if (nParts < 6 || StringSegment (sHeader, "\t", 1) != JJBT_FORMAT) then
    JjbtLog ("view not usable, JAWS's own line is shown: " + sHeader)
    return FALSE
endIf
gJjbtRevision = StringToInt (StringSegment (sHeader, "\t", 2))
gJjbtLayout = StringToInt (StringSegment (sHeader, "\t", 3))
sWhich = StringSegment (sHeader, "\t", 4)
nCaret = StringToInt (StringSegment (sHeader, "\t", 5))
nMark = FALSE
if (StringSegment (sHeader, "\t", 6) == "1") then
    nMark = TRUE
endIf
if (sWhich == "off") then
    return FALSE
endIf
nAttrib = 0
if (nMark) then
    nAttrib = ATTRIB_HIGHLIGHT
endIf
gJjbtSegOrdinals = ""
gJjbtSegStarts = ""
gJjbtSegLengths = ""
nLines = StringSegmentCount (sView, "\n")
nPos = 0
nShown = 0
i = 2
while (i <= nLines)
    sLine = StringSegment (sView, "\n", i)
    nOrdinal = StringToInt (StringSegment (sLine, "\t", 1))
    sText = StringSegment (sLine, "\t", 3)
    if (sWhich == "home" || nOrdinal == nCaret) then
        nWidth = JjbtKeepWidth (nOrdinal, StringLength (sText))
        sPadded = JjbtPad (sText, nWidth)
        if (i < nLines && sWhich == "home") then
            ; One more blank; JAWS's segment delimiter is the second.
            sPadded = sPadded + " "
        endIf
        if (nShown > 0) then
            gJjbtSegOrdinals = gJjbtSegOrdinals + ","
            gJjbtSegStarts = gJjbtSegStarts + ","
            gJjbtSegLengths = gJjbtSegLengths + ","
        endIf
        gJjbtSegOrdinals = gJjbtSegOrdinals + IntToString (nOrdinal)
        gJjbtSegStarts = gJjbtSegStarts + IntToString (nPos)
        gJjbtSegLengths = gJjbtSegLengths + IntToString (StringLength (sText))
        BrailleAddStringWithCallback (sPadded, "JjbtRouted(" + IntToString (nOrdinal) + ")", nAttrib)
        nPos = nPos + StringLength (sPadded) + 1
        nShown = nShown + 1
    endIf
    i = i + 1
endWhile
if (nShown == 0) then
    return FALSE
endIf
if (gJjbtLastLoggedRevision == 0
    || gJjbtRevision >= gJjbtLastLoggedRevision + JJBT_LOG_EVERY) then
    JjbtLog ("painted revision " + IntToString (gJjbtRevision) + ", " + IntToString (nShown)
        + " segment(s), starts " + gJjbtSegStarts)
    gJjbtLastLoggedRevision = gJjbtRevision
endIf
return TRUE
endFunction

int function BrailleCallbackObjectIdentify ()
if (GetObjectAutomationId () == JJBT_HOME_ID) then
    return WT_CUSTOM_CONTROL_BASE + JJBT_CUSTOM
endIf
return BrailleCallbackObjectIdentify ()
endFunction

int function BrailleAddObjectValue (int nSubtype)
if (nSubtype == WT_CUSTOM_CONTROL_BASE + JJBT_CUSTOM) then
    if (JjbtPaint ()) then
        gJjbtPaintsCustom = gJjbtPaintsCustom + 1
        return TRUE
    endIf
    BrailleAddString (GetObjectName (), 0, 0, 0)
    return TRUE
endIf
return BrailleAddObjectValue (nSubtype)
endFunction

int function BrailleAddObjectName (int nSubtype)
if (nSubtype != WT_CUSTOM_CONTROL_BASE + JJBT_CUSTOM
    && GetObjectAutomationId () == JJBT_HOME_ID) then
    if (JjbtPaint ()) then
        gJjbtPaintsName = gJjbtPaintsName + 1
        return TRUE
    endIf
endIf
return BrailleAddObjectName (nSubtype)
endFunction

;=======================================================================
; routing
;=======================================================================

; Called by JAWS when a routing key over a field's segment is pressed.
void function JjbtRouted (int nOrdinal)
var
    int nCell,
    int nData,
    int nStart,
    int nLength,
    int nChar,
    int nCode
nCell = GetLastBrailleRoutingKey ()
; The routing key is 1-based; the display offset is 0-based (MathServer.jss
; does the same).
nData = BrailleGetDataOffsetFromDisplayOffset (nCell - 1)
nStart = JjbtSegValue (gJjbtSegStarts, nOrdinal)
nLength = JjbtSegValue (gJjbtSegLengths, nOrdinal)
nChar = nData - nStart
nCode = 0
if (nData >= 0 && nStart >= 0 && nChar >= 0 && nChar < nLength) then
    nCode = nChar + 1
endIf
PostMessage (gJjbtWindow, gJjbtRouteMsg, gJjbtLayout, nOrdinal * 65536 + nCode)
JjbtLog ("route: cell " + IntToString (nCell) + ", data offset " + IntToString (nData)
    + ", segment start " + IntToString (nStart) + ", length " + IntToString (nLength)
    + " -> field " + IntToString (nOrdinal) + ", character code " + IntToString (nCode)
    + " (0 = not known), layout " + IntToString (gJjbtLayout) + ", revision " + IntToString (gJjbtRevision))
endFunction

;=======================================================================
; scripts
;=======================================================================

Script JjbtStatus ()
var string s
s = "braille trial script loaded"
if (BrailleInUse ()) then
    s = s + ", display of " + IntToString (BrailleGetCellCount ()) + " cells"
else
    s = s + ", no braille display"
endIf
if (gJjbtCarrier) then
    s = s + ", carrier found"
else
    s = s + ", carrier not found"
endIf
s = s + ", painted " + IntToString (gJjbtPaintsCustom) + " times as the custom control and "
    + IntToString (gJjbtPaintsName) + " times in the name, revision " + IntToString (gJjbtRevision)
SayString (s)
JjbtLog ("status: " + s)
EndScript

Script JjbtSwitchView ()
PostMessage (gJjbtWindow, gJjbtCommandMsg, JJBT_COMMAND_SWITCH_VIEW, JJBT_READER_JAWS)
JjbtLog ("command: switch view posted")
EndScript
