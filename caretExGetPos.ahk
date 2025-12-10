#Requires AutoHotkey v1.1.36+
#Include %A_ScriptDir%
#Include .\lib\DpiAwareCoord.ahk
#Include .\lib\winGetWhichMonitor.ahk
;==============================================================
; caretExGetPos — Retrieves caret position using ACC / UIA / JAB
;
; GitHub: https://github.com/SevenKeyboard/caret-ex-get-pos
; Author: SevenKeyboard Ltd. (2025)
; License: The Unlicense
;
; Documentation / References:
;   Get the caret location in any program
;     https://www.reddit.com/r/AutoHotkey/comments/ysuawq/get_the_caret_location_in_any_program/
;   CaretGetPos not working in Chrome?
;     https://www.autohotkey.com/boards/viewtopic.php?p=568784
;   Get cursor position in JetBrains IDE (Solved)
;     https://www.autohotkey.com/boards/viewtopic.php?p=576467
;
;   ACC / MSAA reference implementations:
;     https://github.com/Drugoy/Autohotkey-scripts-.ahk/blob/master/Libraries/Acc.ahk
;     https://github.com/Descolada/Acc-v2/blob/main/Lib/Acc.ahk
;
; Example:
;   coordMode ToolTip, Screen
;   caretExGetPos(x, y,,, caretType)
;   toolTip % "Caret X: " x "`nCaret Y: " y "`nType: " caretType, x, y
;==============================================================
class VersionManager_caretExGetPos
{
    static _ := VersionManager_caretExGetPos._init()
    _init() {
        global
        CARETEXGETPOS_VERSION := "1.0.0"
        if (!this._verCheck(WINGETWHICHMONITOR_VERSION, "1.0.1"))
            throw exception("WinGetWhichMonitor version 1.x is required (minimum 1.0.1).")
        if (!this._verCheck(DPIAWARECOORD_VERSION, "1.0.0"))
            throw exception("DpiAwareCoord version 1.x is required (minimum 1.0.0).")
        return true
    }
    _verCheck(byRef actual, required)    {
        if !isSet(actual)
            return false
        actualMajor     := strSplit(actual, ".",, 2)[1]
        requiredMajor   := strSplit(required, ".",, 2)[1]
        if (actualMajor != requiredMajor)
            return false
        return verCompare(actual, ">=" required)
    }
}
caretExGetPos(byRef outX := "", byRef outY := "", byRef outWidth := "", byRef outHeight := "", byRef outCaretType := "")    {
    static sub := "JabInitializer_E41A0E5A"
    outX:= outY:= outWidth:= outHeight:= outCaretType:= ""
    ;  Default
    coordMode Caret, % format("{2}", prevCMC := A_CoordModeCaret, "Screen")
    x := A_CaretX, y := A_CaretY
    coordMode Caret, % prevCMC
    if (x !== "" && y !== "")    {
        outX := x, outY := y, outWidth := 4, outHeight := 20, outCaretType := "Default"
        return true
    }
    ;  ACC
    static hOleacc := dllCall("Kernel32.dll\LoadLibrary", "Str","Oleacc.dll", "Ptr")
    static OBJID_CARET := 0xFFFFFFF8
    static MONITOR_DEFAULTTONEAREST := 0x00000002
    try    {
        idObject := OBJID_CARET
        if (dllCall("oleacc\AccessibleObjectFromWindow"
            ,"Ptr",hWnd := dllCall("User32.dll\GetForegroundWindow", "Ptr")
            ,"UInt",idObject &= 0xFFFFFFFF
            ,"Ptr",-varSetCapacity(IID, 16) + numPut(idObject == 0xFFFFFFF0 ? 0x46000000000000C0 : 0x719B3800AA000C81, numPut(idObject == 0xFFFFFFF0 ? 0x0000000000020400 : 0x11CF3C3D618736E0, IID, "Int64"), "Int64")
            ,"Ptr*",pacc := 0) == 0)    {
                oAcc := comObjEnwrap(9, pacc, 1)
                varSetCapacity(bufX, 4), varSetCapacity(bufY, 4), varSetCapacity(bufW, 4), varSetCapacity(bufH, 4)
                oAcc.accLocation(comObject(0x4003, &bufX), comObject(0x4003, &bufY), comObject(0x4003, &bufW), comObject(0x4003, &bufH), 0)
                x := numGet(bufX,0,"int"), y := numGet(bufY,0,"int"), w := numGet(bufW,0,"int"), h := numGet(bufH,0,"int")
                if ((x | y) !== 0)    {
                    DpiAwareCoord.convertMonToSys(outX := x, outY := y, WinGetWhichMonitor(hWnd,, MONITOR_DEFAULTTONEAREST))
                    outWidth := w, outHeight := h, outCaretType := "ACC"                    
                    return true
                }
        }
    }  catch  {
        outX:= outY:= outWidth:= outHeight:= outCaretType:= ""
    }
    ;  UIA
    static IUIA := comObjCreate("{e22ad333-b25f-460c-83d0-0581107395c9}", "{34723aff-0c9d-49d0-9896-7ab52df8cd8a}")
    try    {
        dllCall(%sub%.vtable(IUIA,8), "Ptr",IUIA, "Ptr*",FocusedEl := 0) ;  GetFocusedElement
        dllCall(%sub%.vtable(FocusedEl,16), "Ptr",FocusedEl, "Int",10014, "Ptr*",patternObject := 0), objRelease(FocusedEl) ;  GetCurrentPattern. TextPattern = 10014
        if (patternObject)    {
            dllCall(%sub%.vtable(patternObject,5), "Ptr",patternObject, "Ptr*",selectionRanges := 0), objRelease(patternObject) ;  GetSelections
            dllCall(%sub%.vtable(selectionRanges,4), "Ptr",selectionRanges, "Int",0, "Ptr*",selectionRange := 0) ;  GetElement
            dllCall(%sub%.vtable(selectionRange,10), "Ptr",selectionRange, "Ptr*", boundingRects := 0), objRelease(selectionRange), objRelease(selectionRanges) ;  GetBoundingRectangles
            if ((rect := comObject(0x2005, boundingRects+0)).maxIndex() == 3)    { ;  VT_ARRAY | VT_R8
                outX := round(rect[0])
                ,outY := round(rect[1])
                ,outWidth := round(rect[2])
                ,outHeight := round(rect[3])
                ,outCaretType := "UIA"
                return true
            }
        }
    }  catch  {
        outX:= outY:= outWidth:= outHeight:= outCaretType:= ""
    }    
    ;  JAB
    static JAB := %sub%.initJAB()
    ;  static MONITOR_DEFAULTTONEAREST := 0x00000002
    static S_OK := 0x00000000
    try    {
        if JAB && (hWnd := dllCall("User32.dll\GetForegroundWindow", "Ptr")) && dllCall(JAB.module "\isJavaWindow", "Ptr",hWnd, "Cdecl Int")    {
            if (JAB.firstRun)    {
                sleep 200
                JAB.firstRun := false
            }
            dllCall(JAB.module "\getAccessibleContextWithFocus", "Ptr",hWnd, "Int*",vmID := 0, JAB.acType "*", ac := 0, "Cdecl Int")
            varSetCapacity(info, 16, 0)
            dllCall(JAB.module "\getCaretLocation", "Int",vmID, JAB.acType,ac, "Ptr",&info, "Int",0, "Cdecl Int")
            dllCall(JAB.module "\releaseJavaObject", "Int",vmId, JAB.acType,ac, "Cdecl")
            x := numGet(info, 0, "Int"), y := numGet(info, 4, "Int"), w := numGet(info, 8, "Int"), h := numGet(info, 12, "Int")
            hMonitor := dllCall("User32.dll\MonitorFromWindow", "Ptr",hWnd, "Int",MONITOR_DEFAULTTONEAREST, "Ptr")
            if (dllCall("Shcore.dll\GetDpiForMonitor", "Ptr",hMonitor, "Int",0, "UInt*",dpiX := 0, "UInt*",dpiY := 0) == S_OK)    {
                outX := x * dpiX // 96
                ,outY := y * dpiY // 96
                ,outWidth := outX + (w * dpiX // 96)
                ,outHeight := outY + (h * dpiY // 96)
                ,outCaretType := "JAB"
                return true
            }
        }
    }  catch  {
        outX:= outY:= outWidth:= outHeight:= outCaretType:= ""
    }
    return false
}
class JabInitializer_E41A0E5A    { ;  initJAB
    static _jabPtr := 0
    initJAB() {
        obj := {}, obj.firstRun := true, obj.module := A_PtrSize == 8 ? "WindowsAccessBridge-64.dll" : "WindowsAccessBridge-32.dll", obj.acType := "Int64"
        if !(obj.ptr := dllCall("Kernel32.dll\LoadLibrary", "Str",obj.module, "Ptr")) && A_PtrSize == 4    {
            ;  Try the legacy version, which is available only for 32-bit systems.
            obj.acType := "Int", obj.module := "WindowsAccessBridge.dll", obj.ptr := dllCall("Kernel32.dll\LoadLibrary", "Str",obj.module, "ptr")
        }
        if (!obj.ptr)    {
            return ;  Failed to load library. Please ensure that you are running the script with the correct bitness and that Java for the appropriate architecture is installed.
        }  else  {
            this._jabPtr := obj.ptr  
            onExit(this._objbmOnAppExit := objBindMethod(JabInitializer_E41A0E5A, "_onAppExit"))
        }
        dllCall(obj.module "\Windows_run", "Cdecl Int")
        return obj
    }
    _onAppExit(exitReason, exitCode)    {
        if (this._jabPtr)
            dllCall("Kernel32.dll\FreeLibrary", "Ptr",this._jabPtr)
    }
    vtable(ptr, n) {
        return numGet(numGet(ptr + 0), n*A_PtrSize)
    }
}