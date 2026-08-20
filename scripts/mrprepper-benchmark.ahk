#Requires AutoHotkey v2.0
#SingleInstance Force

; Mr. Prepper loading benchmark click helper.
; Coordinates are scaled from a 1920x1080 client area.

startupDelayMs := A_Args.Length >= 1 ? Integer(A_Args[1]) : 25000
betweenClicksMs := A_Args.Length >= 2 ? Integer(A_Args[2]) : 1500
windowTimeoutSec := A_Args.Length >= 3 ? Integer(A_Args[3]) : 45

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
ClickScaled(1724, 292)
Sleep betweenClicksMs
ClickScaled(556, 170)
ExitApp 0

ClickScaled(refX, refY) {
    global exe
    WinGetClientPos &x, &y, &w, &h, exe
    if (w <= 0 || h <= 0) {
        ExitApp 4
    }
    sx := Round(refX * w / 1920)
    sy := Round(refY * h / 1080)
    Click sx, sy
}
