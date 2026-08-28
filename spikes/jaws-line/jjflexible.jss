; jjflexible.jss - Sprint 38 Track H research spike: paint a braille line and
; get cursor routing back, inside JAWS, scoped to jjflexible.exe.
;
; THIS IS A RESEARCH SPIKE, NOT THE SHIPPED SCRIPT. It exists so a human with
; a braille display can answer, in one sitting, the questions the design
; document (JJFlex-private\planning\jaws-braille-line-design.md) names as
; unanswerable from a desk. Nothing in JJ Flexible depends on it.
;
; INSTALL WARNING. This compiles to jjflexible.jsb, the SAME application
; script slot the readercapture instrument uses (tools/readercapture/jaws/).
; Installing one replaces the other until you reinstall it. That is fine for
; a test session; the design document explains how the real script will merge
; the two with a Use statement.
;
; WHAT EACH KEY PROVES (all keys are application-scoped; they do nothing
; outside JJ Flexible):
;
;   Insert+Shift+V - status. Speaks whether the script is loaded, whether a
;       braille display is present, and its cell count. Press this FIRST -
;       it is the positive control for everything else. If this key says
;       nothing, no other result from this spike means anything.
;
;   Insert+Shift+B - Stage A: transient paint. Sends a test line through
;       BrailleMessage with a 60-second duration, then arms the routing
;       override. Pressing a routing key over a word should speak
;       "clicked <word>". Proves: flash paint lands; whether routing keys
;       reach the script's BrailleRouting override while a flash message is
;       showing; whether BrailleGetDataOffsetFromDisplayOffset maps the cell
;       back through translation to the right character.
;
;   Insert+Shift+L - Stage B: structured line toggle. Overrides
;       BrailleAddObjectName so that, while on, the braille line is built
;       from three segments, each with its own routing callback via
;       BrailleAddStringWithCallback. Routing over a segment should speak
;       which element was clicked. Proves: the persistent line shape - JAWS
;       rebuilds and owns it, no timers, panning is JAWS's - and per-segment
;       routing dispatch. This is the shape the design document recommends.
;
;   Insert+Shift+R - Stage C: raw paint. Sends a line through BrailleString,
;       the exact call JJ Flexible's Prism backend makes today. Watch how
;       long it survives against JAWS's own refresh. This measures the
;       CURRENT baseline behaviour - the "unfocusable, badly formatted"
;       claim from 2026-05-02 - on real hardware.
;
; The routing word-lookup uses the UNTRANSLATED string. If contracted
; braille is on, BrailleGetDataOffsetFromDisplayOffset is what should bridge
; display cells back to source offsets; whether it does so inside a flash
; message is one of the things this spike exists to observe.

include "hjconst.jsh"

const
    JJFSPIKE_FLASH_MS = 60000

globals
    int gJJFStructuredOn,
    string gJJFFlashLine,
    int gJJFFlashArmed

; ----------------------------------------------------------------------
; helpers
; ----------------------------------------------------------------------

; Word containing the 1-based offset nOffset in sLine, or "" when the offset
; is out of range or on a space.
string function JJFSpikeWordAt (string sLine, int nOffset)
var
    int nLen,
    int nStart,
    int nEnd
let nLen = StringLength (sLine)
if nOffset < 1 || nOffset > nLen then
    return ""
endIf
if SubString (sLine, nOffset, 1) == " " then
    return ""
endIf
let nStart = nOffset
while nStart > 1 && SubString (sLine, nStart - 1, 1) != " "
    let nStart = nStart - 1
endWhile
let nEnd = nOffset
while nEnd < nLen && SubString (sLine, nEnd + 1, 1) != " "
    let nEnd = nEnd + 1
endWhile
return SubString (sLine, nStart, nEnd - nStart + 1)
endFunction

; ----------------------------------------------------------------------
; status - press first, this is the positive control
; ----------------------------------------------------------------------

Script JJFSpikeStatus ()
var
    string sState
if BrailleInUse () then
    let sState = "braille display present, " + IntToString (BrailleGetCellCount ()) + " cells"
else
    let sState = "no braille display detected"
endIf
if gJJFStructuredOn then
    let sState = sState + ", structured line on"
endIf
SayString ("jjf braille spike loaded, " + sState)
EndScript

; ----------------------------------------------------------------------
; stage A - transient paint via BrailleMessage, routing echo
; ----------------------------------------------------------------------

Script JJFSpikeFlashPaint ()
let gJJFFlashLine = "freq 14.100 mode usb fartsnoodle"
let gJJFFlashArmed = true
BrailleMessage (gJJFFlashLine, 0, JJFSPIKE_FLASH_MS)
SayString ("flash painted, press a routing key over a word")
EndScript

; Routing override. When the stage A flash is armed and showing, identify the
; word under the routing key and speak it. Otherwise chain to the next lower
; BrailleRouting, exactly the idiom Notepad++.jss ships (PerformScript chains
; down, it does not recurse).
Script BrailleRouting ()
var
    int nCell,
    int nData,
    string sWord
if gJJFFlashArmed && BrailleIsMessageBeingShown () then
    let nCell = GetLastBrailleRoutingKey ()
    let nData = BrailleGetDataOffsetFromDisplayOffset (nCell)
    let sWord = JJFSpikeWordAt (gJJFFlashLine, nData)
    if sWord != "" then
        SayString ("clicked " + sWord)
    else
        SayString ("clicked cell " + IntToString (nCell) + ", data offset " + IntToString (nData))
    endIf
    return
endIf
let gJJFFlashArmed = false
PerformScript BrailleRouting ()
EndScript

; ----------------------------------------------------------------------
; stage B - persistent structured line with per-segment callbacks
; ----------------------------------------------------------------------

Script JJFSpikeStructuredToggle ()
let gJJFStructuredOn = ! gJJFStructuredOn
BrailleRefresh ()
if gJJFStructuredOn then
    SayString ("structured line on, press routing keys over the segments")
else
    SayString ("structured line off")
endIf
EndScript

; While the toggle is on, the Name component of whatever has focus inside
; JJ Flexible is replaced by three callback segments. The control's TYPE
; abbreviation will still appear - that is JAWS's own component handling, and
; observing how our segments sit beside it is part of the point. The real
; implementation claims the whole line with a custom control type through
; BrailleCallbackObjectIdentify plus a jjflexible.jbs; the design document
; covers that. Returning true tells JAWS the script handled the component;
; chaining down otherwise is mandatory (see Structured_Braille.html).
int function BrailleAddObjectName (int iSubtype)
if gJJFStructuredOn then
    BrailleAddStringWithCallback ("14.100", "JJFSpikeClicked(1)", attrib_highlight)
    BrailleAddStringWithCallback ("usb", "JJFSpikeClicked(2)", 0)
    BrailleAddStringWithCallback ("fartsnoodle", "JJFSpikeClicked(3)", 0)
    return true
endIf
return BrailleAddObjectName (iSubtype)
endFunction

void function JJFSpikeClicked (int nElement)
var
    int nCell
let nCell = GetLastBrailleRoutingKey ()
if nElement == 1 then
    SayString ("clicked frequency, cell " + IntToString (nCell))
elif nElement == 2 then
    SayString ("clicked mode, cell " + IntToString (nCell))
else
    SayString ("clicked fartsnoodle, cell " + IntToString (nCell))
endIf
endFunction

; ----------------------------------------------------------------------
; stage C - raw BrailleString, the current Prism baseline
; ----------------------------------------------------------------------

Script JJFSpikeRawPaint ()
BrailleString ("raw braillestring fartsnoodle")
SayString ("raw painted, watch how long it survives and what routing does")
EndScript
