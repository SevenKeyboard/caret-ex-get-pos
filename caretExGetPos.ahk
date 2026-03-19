#Requires AutoHotkey v1.1.36+
#Include %A_ScriptDir%
#Include .\lib\DpiAwareCoord.ahk
#Include .\lib\DpiAwarenessContextUtils.ahk
#Include .\lib\ShellHookWindow.ahk
#Include .\lib\winGetWhichMonitor.ahk
;==============================================================
; caretExGetPos — Retrieves caret position using ACC / UIA / JAB
;
; GitHub: https://github.com/SevenKeyboard/caret-ex-get-pos
; Author: SevenKeyboard Ltd. (2025)
; License: MIT License
;
; Documentation / References:
;   Get the caret location in any program
;     https://www.reddit.com/r/AutoHotkey/comments/ysuawq/get_the_caret_location_in_any_program/
;
;   Simple script that automatically toggles on and off when I'm typing in a text box
;     https://www.autohotkey.com/boards/viewtopic.php?t=114802
;   CaretGetPos not working in Chrome?
;     https://www.autohotkey.com/boards/viewtopic.php?t=129030
;   How to get the cursor position in JetBrains IDE?
;     https://www.autohotkey.com/boards/viewtopic.php?t=130941
;
;   Descolada/Acc-v2/Lib/Acc.ahk
;     https://github.com/Descolada/Acc-v2/blob/main/Lib/Acc.ahk
;   Drugoy/Autohotkey-scripts-.ahk/Libraries/Acc.ahk
;     https://github.com/Drugoy/Autohotkey-scripts-.ahk/blob/master/Libraries/Acc.ahk
;
;   UIAutomationClient.h
;     C:\Program Files (x86)\Windows Kits\10\Include\10.0.xxxxx.0\um\UIAutomationClient.h
;==============================================================

/*
Example Usage:
    ;  setThreadDpiAwarenessContext(-2)
    caretExGetPos(x, y, w, h, caretType)
    coordMode % "Tooltip", % "Screen"
    toolTip % "Caret X: " x "`nCaret Y: " y "`nCaret W: " w "`nCaret H: " h "`nType: " caretType, x, y
*/

class VersionManager_caretExGetPos
{
    static _ := VersionManager_caretExGetPos._init()
    _init() {
        global
        CARETEXGETPOS_VERSION := "2.0.0"
        if (!this._verCheck(DPIAWARECOORD_VERSION, "1.1.0"))
            throw exception("DpiAwareCoord version 1.x is required (minimum 1.1.0).")
        if (!this._verCheck(DPIAWARENESSCONTEXTUTILS_VERSION, "1.0.0"))
            throw exception("DpiAwarenessContextUtils version 1.x is required (minimum 1.0.0).")
        if (!this._verCheck(SHELLHOOKWINDOW_VERSION, "1.0.0"))
            throw exception("ShellHookWindow version 1.x is required (minimum 1.0.0).")
        if (!this._verCheck(WINGETWHICHMONITOR_VERSION, "1.0.1"))
            throw exception("WinGetWhichMonitor version 1.x is required (minimum 1.0.1).")
        return true
    }
    _verCheck(byRef actual, required)    {
        if !isSet(actual)
            return false
        actualMajor     := strSplit(actual, ".",, 2)[1]
        requiredMajor   := strSplit(required, ".",, 2)[1]
        if (actualMajor !== requiredMajor)
            return false
        return verCompare(actual, ">=" required)
    }
}
caretExGetPos(byRef outX := "", byRef outY := "", byRef outW := "", byRef outH := "", byRef outCaretType := "")    {
    return _CaretExGetPosProvider.caretExGetPos(outX, outY, outW, outH, outCaretType)
}
class _CaretExGetPosProvider
{
    ;  WARNING: Backward compatibility is not guaranteed for any methods or properties in this class.
    static _stateByHwnd := object()
    caretExGetPos(byRef outX := "", byRef outY := "", byRef outW := "", byRef outH := "", byRef outCaretType := "")    {
        static defaultOrder   := ["tryJavaAccessBridge", "tryUIATextPattern2", "tryMSAA", "tryGUIThreadInfo", "tryUIATextPattern"]
        outX:= outY:= outW:= outH:= outCaretType:= ""
        hWnd := dllCall("User32.dll\GetForegroundWindow", "Ptr") & 0xFFFFFFFF
        if (!hWnd)
            return false
        order := []
        if (!this._stateByHwnd.hasKey(hWnd))
            this._stateByHwnd[hWnd] := {method:"", priority:999, fails:0}
        if (this._stateByHwnd[hWnd].method !== "")    {
            order.push(this._stateByHwnd[hWnd].method)
            for _,defMethod in defaultOrder    {
                if (defMethod !== this._stateByHwnd[hWnd].method)
                    order.push(defMethod)
            }
        }  else  {
            order.push(defaultOrder*)
        }
        lastMethod := ""
        lastPriority := 999
        focusedElement := ""
        ok := false
        for i,method in order    {
            switch (method)
            {
                case "tryGUIThreadInfo":
                    if (this._tryGUIThreadInfo(0, outX, outY, outW, outH))    {
                        outCaretType := "GUIThreadInfo"
                        ok := true
                    }
                case "tryMSAA":
                    if (this._tryMSAA(hWnd, outX, outY, outW, outH))    {
                        outCaretType := "MSAA"
                        ok := true
                    }
                case "tryUIATextPattern":
                    if (focusedElement == "")
                        focusedElement := this._getFocusedUIAElement()
                    if (this._tryUIATextPattern(focusedElement, outX, outY, outW, outH))    {
                        outCaretType := "UIATextPattern"
                        ok := true
                    }
                case "tryUIATextPattern2":
                    if (focusedElement == "")
                        focusedElement := this._getFocusedUIAElement()
                    if (this._tryUIATextPattern2(focusedElement, outX, outY, outW, outH))    {
                        outCaretType := "UIATextPattern2"
                        ok := true
                    }
                case "tryJavaAccessBridge":
                    if (this._tryJavaAccessBridge(hWnd, outX, outY, outW, outH))    {
                        outCaretType := "JavaAccessBridge"
                        ok := true
                    }
            }
            if (ok)    {
                lastMethod := method
                for j,defMethod in defaultOrder    {
                    if (defMethod == method)    {
                        lastPriority := j
                        break
                    }
                }
                break
            }
        }
        if (focusedElement !== "" && focusedElement)
            this._releaseFocusedUIAElement(focusedElement)
        if (ok)    {
            if (lastPriority <= this._stateByHwnd[hWnd].priority)    {
                this._stateByHwnd[hWnd].method    := lastMethod
                this._stateByHwnd[hWnd].priority  := lastPriority
                this._stateByHwnd[hWnd].fails     := 0
            }  else  {
                if (3 <= ++this._stateByHwnd[hWnd].fails)    {
                    this._stateByHwnd[hWnd].method    := lastMethod
                    this._stateByHwnd[hWnd].priority  := lastPriority
                    this._stateByHwnd[hWnd].fails     := 0
                }
            }
            return true
        }  else  {
            if (this._stateByHwnd[hWnd].method !== "")
                this._stateByHwnd[hWnd].fails := min(99, this._stateByHwnd[hWnd].fails + 1)
        }
        return false
    }
    ;------------------------------------------------
    static _ := _CaretExGetPosProvider._init()
    _init()    {
        ShellHookWindow.register(objBindMethod(this, "_shellMessage"))
        ShellHookWindow.unregisterOnExit()
        this._initWab()
        objbmOnExiting := objBindMethod(this, "_onExiting")
        onExit(objbmOnExiting)
        return true
    }
    _shellMessage(wParam, lParam, _*)    {
        static HSHELL_WINDOWDESTROYED := 2
        if (wParam == HSHELL_WINDOWDESTROYED)    {
            hWnd := lParam & 0xFFFFFFFF
            if (this._stateByHwnd.hasKey(hWnd))
                this._stateByHwnd.delete(hWnd)
        }
    }
    _onExiting(exitReason, exitCode)    {
        this._freeWab()
        if (this._hOleacc)
            dllCall("Kernel32.dll\FreeLibrary", "Ptr",this._hOleacc)
        this._hOleacc := 0
        this._pAccessibleObjectFromWindow := 0
    }
    _getComMethodPtr(index, comObj)    {
        return numGet(numGet(comObj + 0, 0, "Ptr"), A_PtrSize * index, "Ptr")
    }
    ;------------------------------------------------
    _tryGUIThreadInfo(idThread := 0, byRef outX := "", byRef outY := "", byRef outW:= "", byRef outH:= "") {
        static cbSize           := 4 * 2 + (A_PtrSize * 6) + 4 * 4
            ,hwndCaretOffset    := 8 + A_PtrSize * 5
            ,rcCaretOffset      := 8 + (A_PtrSize * 6)
        outX:= outY:= outW:= outH:= ""
        varSetCapacity(info, cbSize, 0)
        numPut(cbSize, info, "UInt")
        if !dllCall("User32.dll\GetGUIThreadInfo", "UInt",idThread, "Ptr",&info, "Int")
            return false
        hwndCaret := numGet(info, hwndCaretOffset, "Ptr")
        if (!hwndCaret)
            return false
        l := numGet(info, rcCaretOffset + 0, "Int")
        t := numGet(info, rcCaretOffset + 4, "Int")
        r := numGet(info, rcCaretOffset + 8, "Int")
        b := numGet(info, rcCaretOffset + 12, "Int")
        varSetCapacity(pt, 8, 0)
        numPut(l, pt, 0, "Int")
        numPut(t, pt, 4, "Int")
        if (dllCall("User32.dll\ClientToScreen", "Ptr",hwndCaret, "Ptr",&pt, "Int"))    {
            outX := numGet(pt, 0, "Int")
            outY := numGet(pt, 4, "Int")
            outW := r - l
            outH := b - t
            return true
        }
        return false
    }
    ;------------------------------------------------
    static _hOleacc := 0
        ,_pAccessibleObjectFromWindow := 0
    _tryMSAA(hWnd := 0, byRef outX := "", byRef outY := "", byRef outW := "", byRef outH := "")    {
        local
        global DpiAwareCoord
        static OBJID_CARET := 0xFFFFFFF8
            ,IID_IAccessible
            ,S_OK := 0x00000000
            ,VT_I4 := 3
            ,VT_DISPATCH := 9
            ,VT_BYREF := 0x4000
            ,CHILDID_SELF := 0
            ,MONITOR_DEFAULTTONEAREST := 0x00000002
        if (!isSet(IID_IAccessible))    {
            varSetCapacity(IID_IAccessible, 16, 0)
            numPut(0x11CF3C3D618736E0, IID_IAccessible, 0, "Int64")
            numPut(0x719B3800AA000C81, IID_IAccessible, 8, "Int64")
        }
        outX:= outY:= outW:= outH:= ""
        if (!this._pAccessibleObjectFromWindow)    {
            this._hOleacc := dllCall("Kernel32.dll\LoadLibraryW", "WStr","Oleacc.dll", "Ptr")
            if (!this._hOleacc)
                return false
            this._pAccessibleObjectFromWindow := dllCall("Kernel32.dll\GetProcAddress", "Ptr",this._hOleacc, "AStr","AccessibleObjectFromWindow", "Ptr")
            if (!this._pAccessibleObjectFromWindow)    {
                dllCall("Kernel32.dll\FreeLibrary", "Ptr",this._hOleacc), this._hOleacc := 0
                return false
            }
        }
        if (!hWnd)    {
            hWnd := dllCall("User32.dll\GetForegroundWindow", "Ptr")
            if (!hWnd)
                return false
        }
        hResult := dllCall(this._pAccessibleObjectFromWindow
            ,"Ptr",hWnd
            ,"UInt",OBJID_CARET
            ,"Ptr",&IID_IAccessible
            ,"Ptr*",ppvObject := 0
            ,"Int")
        if (hResult == S_OK && ppvObject)    {
            try  {
                oAcc := comObject(VT_DISPATCH, ppvObject, 1)
                varSetCapacity(bufX, 4, 0)
                varSetCapacity(bufY, 4, 0)
                varSetCapacity(bufW, 4, 0)
                varSetCapacity(bufH, 4, 0)
                ;  IAccessible::accLocation
                oAcc.accLocation(comObject(VT_I4 | VT_BYREF, &bufX)
                    ,comObject(VT_I4 | VT_BYREF, &bufY)
                    ,comObject(VT_I4 | VT_BYREF, &bufW)
                    ,comObject(VT_I4 | VT_BYREF, &bufH)
                    ,CHILDID_SELF)
            }  catch  {
                return false
            }
            x := numGet(bufX, 0, "Int")
            y := numGet(bufY, 0, "Int")
            w := numGet(bufW, 0, "Int")
            h := numGet(bufH, 0, "Int")
            ;  Treat (0, 0) as an invalid ACC caret location in practice.
            if (x || y)    {
                l := x, t := y, r := x + w, b := y + h
                switch (getThreadDpiAwarenessContextIgnoringInfoFlag())
                {
                    case -1, -5:
                        i := winGetWhichMonitor(hWnd,, MONITOR_DEFAULTTONEAREST)
                        DpiAwareCoord.convertMonToUnw(l, t, i, false)
                        DpiAwareCoord.convertMonToUnw(r, b, i, false)
                        outX := round(l), outY := round(t), outW := round(r - l), outH := round(b - t)
                    default: ;  -2
                        i := winGetWhichMonitor(hWnd,, MONITOR_DEFAULTTONEAREST)
                        DpiAwareCoord.convertMonToSys(l, t, i, false)
                        DpiAwareCoord.convertMonToSys(r, b, i, false)
                        outX := round(l), outY := round(t), outW := round(r - l), outH := round(b - t)
                    case -3, -4:
                        outX := round(l), outY := round(t), outW := round(r - l), outH := round(b - t)
                }
                return true
            }
        }
        return false
    }
    ;------------------------------------------------
    _getUIA()    {
        static CLSID_CUIAutomation8 := "{E22AD333-B25F-460C-83D0-0581107395C9}"
            ,IID_IUIAutomation2     := "{34723AFF-0C9D-49D0-9896-7AB52DF8CD8A}"
            ,oUIA := ""
        if (!oUIA)    {
            try  {
                oUIA := comObjCreate(CLSID_CUIAutomation8, IID_IUIAutomation2)
            }  catch  {
                oUIA := ""
            }
        }
        return oUIA
    }
    _getFocusedUIAElement()    {
        static S_OK := 0x00000000
        oUIA := this._getUIA()
        if (!oUIA)
            return 0
        ;  IUIAutomation::GetFocusedElement
        hResult := dllCall(this._getComMethodPtr(8, oUIA), "Ptr",oUIA, "Ptr*",focusedElement := 0, "Int")
        if (hResult == S_OK && focusedElement)
            return focusedElement
        return 0
    }
    _releaseFocusedUIAElement(focusedElement)    {
        if (focusedElement)
            objRelease(focusedElement)
    }
    _tryUIATextPattern(focusedElement, byRef outX := "", byRef outY := "", byRef outW := "", byRef outH := "")    {
        local
        static S_OK := 0x00000000
            ,UIA_TextPatternId  := 10014
            ,VT_R8 := 5
            ,VT_ARRAY := 0x2000
        outX:= outY:= outW:= outH:= ""
        if (!focusedElement)
            return false
        try  {
            ;  IUIAutomationElement::GetCurrentPattern
            hResult := dllCall(this._getComMethodPtr(16, focusedElement), "Ptr",focusedElement, "Int",UIA_TextPatternId, "Ptr*",textPatternObj := 0, "Int")
            if (hResult == S_OK && textPatternObj)    {
                ranges:= range:= 0
                try  {
                    ;  IUIAutomationTextPattern::GetSelection
                    hResult := dllCall(this._getComMethodPtr(5, textPatternObj), "Ptr",textPatternObj, "Ptr*",ranges, "Int")
                    if (hResult !== S_OK || !ranges)
                        return false
                    ;  IUIAutomationTextRangeArray::GetElement
                    hResult := dllCall(this._getComMethodPtr(4, ranges), "Ptr",ranges, "Int",0, "Ptr*",range, "Int")
                    if (hResult !== S_OK || !range)
                        return false
                    ;  IUIAutomationTextRange::GetBoundingRectangles
                    hResult := dllCall(this._getComMethodPtr(10, range), "Ptr",range, "Ptr*",returnValue2 := 0, "Int")
                    if (hResult !== S_OK || !returnValue2)
                        return false
                    rect := comObject(VT_R8 | VT_ARRAY, returnValue2 + 0)
                    if (rect.maxIndex() == 3)    {
                        outX := round(rect[0])
                        outY := round(rect[1])
                        outW := 1
                        outH := round(rect[3])
                        return true
                    }
                }  finally  {
                    if (range)
                        objRelease(range), range := 0
                    if (ranges)
                        objRelease(ranges), ranges := 0
                    if (textPatternObj)
                        objRelease(textPatternObj), textPatternObj := 0
                }
            }
        }
        return false
    }
    _tryUIATextPattern2(focusedElement, byRef outX := "", byRef outY := "", byRef outW := "", byRef outH := "")    {
        local
        static S_OK := 0x00000000
            ,UIA_TextPattern2Id := 10024
            ,VT_R8 := 5
            ,VT_ARRAY := 0x2000
        outX:= outY:= outW:= outH:= ""
        if (!focusedElement)
            return false
        try  {
            ;  IUIAutomationElement::GetCurrentPattern
            hResult := dllCall(this._getComMethodPtr(16, focusedElement), "Ptr",focusedElement, "Int",UIA_TextPattern2Id, "Ptr*",textPattern2Obj := 0, "Int")
            if (hResult == S_OK && textPattern2Obj)    {
                range := 0
                try  {
                    ;  IUIAutomationTextPattern2::GetCaretRange
                    hResult := dllCall(this._getComMethodPtr(10, textPattern2Obj), "Ptr",textPattern2Obj, "Int*",isActive, "Ptr*",range := 0, "Int")
                    if (hResult !== S_OK || !range)
                        return false
                    ;  IUIAutomationTextRange::GetBoundingRectangles
                    hResult := dllCall(this._getComMethodPtr(10, range), "Ptr",range, "Ptr*",returnValue2 := 0, "Int")
                    if (hResult !== S_OK || !returnValue2)
                        return false
                    rect := comObject(VT_R8 | VT_ARRAY, returnValue2 + 0)
                    if (rect.maxIndex() == 3)    {
                        outX := round(rect[0])
                        outY := round(rect[1])
                        outW := round(rect[2])
                        outH := round(rect[3])
                        return true
                    }
                }  finally  {
                    if (range)
                        objRelease(range), range := 0
                    if (textPattern2Obj)
                        objRelease(textPattern2Obj), textPattern2Obj := 0
                }
            }
        }
        return false
    }
    ;------------------------------------------------
    _initWab()    {
        this._hWab := 0
        if (A_PtrSize !== 8)
            return false
        ;  C:\Program Files\Microsoft\jdk-25.0.2.10-hotspot\bin\windowsaccessbridge-64.dll
        this._hWab := dllCall("Kernel32.dll\LoadLibraryW", "WStr",A_ScriptDir . "\dll\x64\windowsaccessbridge-64.dll", "Ptr")
        if (this._hWab)    {
            this._pIsJavaWindow                     := dllCall("Kernel32.dll\GetProcAddress", "Ptr",this._hWab, "AStr","isJavaWindow", "Ptr")
            this._pGetAccessibleContextWithFocus    := dllCall("Kernel32.dll\GetProcAddress", "Ptr",this._hWab, "AStr","getAccessibleContextWithFocus", "Ptr")
            this._pGetCaretLocation                 := dllCall("Kernel32.dll\GetProcAddress", "Ptr",this._hWab, "AStr","getCaretLocation", "Ptr")
            this._pReleaseJavaObject                := dllCall("Kernel32.dll\GetProcAddress", "Ptr",this._hWab, "AStr","releaseJavaObject", "Ptr")
            if !(this._pIsJavaWindow
                && this._pGetAccessibleContextWithFocus
                && this._pGetCaretLocation
                && this._pReleaseJavaObject)    {
                this._freeWab()
                return false
            }
            dllCall("windowsaccessbridge-64.dll\Windows_run", "Cdecl Int")
            return true
        }
        return false
    }
    _freeWab()    {
        if (this._hWab)
            dllCall("Kernel32.dll\FreeLibrary", "Ptr",this._hWab)
        this._hWab := 0
        this._pIsJavaWindow                     := 0
        this._pGetAccessibleContextWithFocus    := 0
        this._pGetCaretLocation                 := 0
        this._pReleaseJavaObject                := 0
    }
    _tryJavaAccessBridge(hWnd := 0, byRef outX := "", byRef outY := "", byRef outW := "", byRef outH := "")    {
        local
        global DpiAwareCoord
        static MONITOR_DEFAULTTONEAREST := 0x00000002
        outX:= outY:= outW:= outH:= ""
        if (!this._hWab)
            return false
        if (!hWnd)    {
            hWnd := dllCall("User32.dll\GetForegroundWindow", "Ptr")
            if (!hWnd)
                return false
        }
        if (!dllCall(this._pIsJavaWindow, "Ptr",hWnd, "Cdecl Int"))
            return false
        ok := dllCall(this._pGetAccessibleContextWithFocus, "Ptr",hWnd, "Int*",vmID := 0, "Int64*",ac := 0, "Cdecl Int")
        if (!ok || !ac)
            return false
        varSetCapacity(info, 16, 0)
        ok := dllCall(this._pGetCaretLocation, "Int",vmID, "Int64",ac, "Ptr",&info, "Int",0, "Cdecl Int")
        dllCall(this._pReleaseJavaObject, "Int",vmID, "Int64",ac, "Cdecl")
        if (!ok)
            return false
        x := numGet(info, 0, "Int")
        y := numGet(info, 4, "Int")
        w := numGet(info, 8, "Int")
        h := numGet(info, 12, "Int")
        i := 0
        threadDpiAwarenessContextIgnoringInfoFlag := getThreadDpiAwarenessContextIgnoringInfoFlag()
        switch (getWindowDpiAwarenessContextIgnoringInfoFlag(hWnd))
        {
            case -1, -5:
                ;  Untested.
                ;  Assumed to already be in a 96-DPI-based virtualized coordinate space,
                ;  so no additional scaling is applied for now.
                if (threadDpiAwarenessContextIgnoringInfoFlag !== -2)
                    i := winGetWhichMonitor(hWnd,, MONITOR_DEFAULTTONEAREST)
                outX := x
                outY := y
                outW := w
                outH := h
            case -2:
                ;  Verified experimentally with SwingSet2.
                ;  The monitor origin appears to be preserved, but monitor-local offsets
                ;  behave like scaled-down logical coordinates relative to AHK v1 (System aware).
                ;  Therefore only the monitor-local offset is scaled by the system ratio.
                ratio := monitorExGetScaleFactor() / 100
                i := winGetWhichMonitor(hWnd,, MONITOR_DEFAULTTONEAREST)
                if (!i)
                    return false
                if (!monitorExGet(i, l, t, r, b))
                    return false
                outX := l + (x - l) * ratio
                outY := t + (y - t) * ratio
                outW := w * ratio
                outH := h * ratio
            case -3, -4:
                ;  Verified experimentally with IntelliJ IDEA.
                ;  Coordinates appear to behave like global 96-DPI logical screen coordinates
                ;  relative to AHK v1 (System aware), so the entire rectangle is scaled
                ;  by the system ratio.
                ratio := monitorExGetScaleFactor() / 100
                if (threadDpiAwarenessContextIgnoringInfoFlag !== -2)
                    i := winGetWhichMonitor(hWnd,, MONITOR_DEFAULTTONEAREST)
                outX := x * ratio
                outY := y * ratio
                outW := w * ratio
                outH := h * ratio
        }
        l := outX, t := outY, r := outX + outW, b := outY + outH
        switch (threadDpiAwarenessContextIgnoringInfoFlag)
        {
            case -1, -5:
                if (!i)
                    return false
                DpiAwareCoord.convertSysToUnw(l, t, i, false)
                DpiAwareCoord.convertSysToUnw(r, b, i, false)
                outX := l, outY := t, outW := r - l, outH := b - t
            case -3, -4:
                if (!i)
                    return false
                DpiAwareCoord.convertSysToMon(l, t, i, false)
                DpiAwareCoord.convertSysToMon(r, b, i, false)
                outX := l, outY := t, outW := r - l, outH := b - t
        }
        outX := round(outX)
        outY := round(outY)
        outW := round(outW)
        outH := round(outH)
        return true
    }
}