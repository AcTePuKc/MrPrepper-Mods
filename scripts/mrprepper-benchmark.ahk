#Requires AutoHotkey v2.0
#SingleInstance Force

; Mr. Prepper loading benchmark click helper.
; Reference coordinates come from Unity Input.mousePosition logs, which use a
; bottom-left origin. AutoHotkey client coordinates use a top-left origin, so
; Y is inverted before scaling.

startupDelayMs := A_Args.Length >= 1 ? Integer(A_Args[1]) : 25000
betweenClicksMs := A_Args.Length >= 2 ? Integer(A_Args[2]) : 1500
windowTimeoutSec := A_Args.Length >= 3 ? Integer(A_Args[3]) : 45
dismissRecoveryPrompt := A_Args.Length >= 4 ? Integer(A_Args[4]) != 0 : true
recoveryToContinueMs := A_Args.Length >= 5 ? Integer(A_Args[5]) : 1200

exe := "ahk_exe MrPrepper.exe"
CoordMode "Mouse", "Client"

if !WinWait(exe, , windowTimeoutSec) {
    ExitApp 2
}

WinActivate exe
if !WinWaitActive(exe, , 10) {
    ExitApp 3
}

Sleep startupDelayMs

; Mr. Prepper may show its recovery/display-change confirmation after an
; unclean shutdown. The Yes button was observed at Unity coordinates ~796,499.
; If no dialog is present this lands in a normally inert area of the menu.
if dismissRecoveryPrompt {
    ClickUnity(796, 499)
    Sleep recoveryToContinueMs
}

; Main-menu Continue, then the selected save-slot Play/Continue button.
ClickUnity(1724, 292)
Sleep betweenClicksMs
ClickUnity(556, 170)
ExitApp 0

ClickUnity(refX, refY) {
    global exe
    WinGetClientPos &x, &y, &w, &h, exe
    if (w <= 0 || h <= 0) {
        ExitApp 4
    }

    sx := Round(refX * w / 1920)
    sy := Round((1080 - refY) * h / 1080)
    Click sx, sy
}
