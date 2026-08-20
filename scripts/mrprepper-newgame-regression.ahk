#Requires AutoHotkey v2.0
#SingleInstance Force

; Mr. Prepper new-game regression helper.
;
; Modes:
;   AutoHotkey64.exe mrprepper-newgame-regression.ahk calibrate
;   AutoHotkey64.exe mrprepper-newgame-regression.ahk run
;
; Calibration is needed once because the profiler logs UI button paths/timestamps
; but not mouse coordinates. Hover each requested point and press F8.
; Coordinates are stored normalized to a 1920x1080 Unity-style bottom-left
; reference space so the runner scales to the current client size.
;
; Current deterministic test flow:
;   New Game
;   -> Remove existing Slot 6 save -> Yes
;   -> Slot 6 -> Normal game -> Normal difficulty -> Play
;   -> wait for Main16 -> No tutorial
;   -> hold left mouse to skip the new-game intro/video
;   -> 8 paced left-clicks to advance the scripted opening sequence
;   -> Esc -> Exit to Windows -> Yes
;
; The opening dialogue does not advance reliably with a single uniform cadence.
; The fifth click needs a longer wait before it because that line can still be
; revealing when a faster click arrives. Use an explicit per-click schedule.
;
; The script uses a normal in-game exit. Force-close is only a timeout fallback.

mode := A_Args.Length >= 1 ? StrLower(A_Args[1]) : "run"
startupDelayMs := A_Args.Length >= 2 ? Integer(A_Args[2]) : 14000
stepDelayMs := A_Args.Length >= 3 ? Integer(A_Args[3]) : 1400
loadWaitMs := A_Args.Length >= 4 ? Integer(A_Args[4]) : 23000
mouseHoldMs := A_Args.Length >= 5 ? Integer(A_Args[5]) : 3500
postSequenceWaitMs := A_Args.Length >= 6 ? Integer(A_Args[6]) : 8000
exitWaitMs := A_Args.Length >= 7 ? Integer(A_Args[7]) : 10000
windowTimeoutSec := 45

; Delay BEFORE each opening-sequence click after the first one.
; Click 5 deliberately gets a much longer lead-in because it was repeatedly
; observed to arrive before the preceding text had fully appeared.
advanceBeforeClickMs := [0, 2200, 2200, 2200, 4500, 3200, 3200, 3200]

exe := "ahk_exe MrPrepper.exe"
ini := A_ScriptDir "\\mrprepper-newgame-regression.ini"
CoordMode "Mouse", "Client"

if (mode = "calibrate") {
    Calibrate()
    ExitApp 0
}

if (mode != "run") {
    MsgBox "Unknown mode: " mode "`nUse: calibrate or run"
    ExitApp 1
}

if !WinWait(exe, , windowTimeoutSec) {
    ExitApp 2
}
WinActivate exe
if !WinWaitActive(exe, , 10) {
    ExitApp 3
}

required := [
    "NewGame",
    "RemoveSave",
    "RemoveYes",
    "Slot6",
    "NormalMode",
    "NormalDifficulty",
    "Play",
    "NoTutorial",
    "SkipArea",
    "ExitToWindows",
    "ExitYes"
]
for name in required {
    if !HasPoint(name) {
        MsgBox "Missing calibration point: " name "`nRun once with:`n`n" A_ScriptName " calibrate"
        ExitApp 5
    }
}

Sleep startupDelayMs

; Main menu -> remove the existing dedicated test save first.
ClickPoint("NewGame")
Sleep stepDelayMs
ClickPoint("RemoveSave")
Sleep stepDelayMs
ClickPoint("RemoveYes")
Sleep (stepDelayMs * 2)

; Re-create Slot 6 as a fresh normal game.
ClickPoint("Slot6")
Sleep stepDelayMs
ClickPoint("NormalMode")
Sleep stepDelayMs
ClickPoint("NormalDifficulty")
Sleep stepDelayMs
ClickPoint("Play")

; The no-tutorial prompt appears after Main16 has loaded.
Sleep loadWaitMs
ClickPoint("NoTutorial")
Sleep stepDelayMs

; Skip the following new-game intro/video by holding the left mouse button.
MovePoint("SkipArea")
SendEvent "{LButton down}"
Sleep mouseHoldMs
SendEvent "{LButton up}"
Sleep stepDelayMs

; Advance the deterministic scripted opening sequence. The delays are before
; each click so click #5 can be held back without changing the timing after it.
for index, delayMs in advanceBeforeClickMs {
    if (delayMs > 0) {
        Sleep delayMs
    }
    ClickPoint("SkipArea")
}

; Give dialogue/loading instrumentation enough time to finish writing results.
Sleep postSequenceWaitMs

; Prefer a clean shutdown so the next automated run starts from a predictable state.
Send "{Esc}"
Sleep 1000
ClickPoint("ExitToWindows")
Sleep 1000
ClickPoint("ExitYes")

; Wait for a normal exit. Only kill the process if the game failed to close.
if WinWaitClose(exe, , exitWaitMs / 1000) {
    ExitApp 0
}

pid := WinGetPID(exe)
if pid {
    ProcessClose pid
}
ExitApp 6

Calibrate() {
    global exe, ini, windowTimeoutSec

    if !WinWait(exe, , windowTimeoutSec) {
        MsgBox "MrPrepper.exe window not found."
        ExitApp 2
    }
    WinActivate exe
    if !WinWaitActive(exe, , 10) {
        MsgBox "Could not activate MrPrepper.exe."
        ExitApp 3
    }

    steps := [
        ["NewGame", "Hover NEW GAME / Нова игра"],
        ["RemoveSave", "Hover REMOVE / Премахни on the test slot"],
        ["RemoveYes", "Hover YES / Да on the remove-save confirmation"],
        ["Slot6", "Hover the SECOND TEST SLOT (Slot 6)"],
        ["NormalMode", "Hover NORMAL GAME / Нормална игра"],
        ["NormalDifficulty", "Hover NORMAL difficulty / Нормален"],
        ["Play", "Hover PLAY / Играй"],
        ["NoTutorial", "Hover NO / Не on the tutorial prompt"],
        ["SkipArea", "Hover a safe central area used for holding/clicking through the opening sequence"],
        ["ExitToWindows", "Hover EXIT TO WINDOWS / Изход от играта"],
        ["ExitYes", "Hover YES / Да on the exit confirmation"]
    ]

    MsgBox "Calibration mode.`n`nFor each step, hover the requested point and press F8.`nThe game will not be clicked by the calibration hotkey."

    for step in steps {
        name := step[1]
        description := step[2]
        ToolTip description "`nPress F8 to capture"
        KeyWait "F8"
        KeyWait "F8", "D"
        SaveCurrentPoint(name)
        KeyWait "F8"
        Sleep 200
    }

    ToolTip
    MsgBox "Calibration saved:`n" ini "`n`nNow run:`n" A_ScriptName " run"
}

SaveCurrentPoint(name) {
    global exe, ini
    MouseGetPos &mx, &my
    WinGetClientPos &x, &y, &w, &h, exe
    if (w <= 0 || h <= 0) {
        ExitApp 4
    }

    ; Store normalized Unity-style coordinates (bottom-left origin).
    ux := Round(mx * 1920 / w)
    uy := Round(1080 - (my * 1080 / h))
    IniWrite ux, ini, "Points", name "X"
    IniWrite uy, ini, "Points", name "Y"
}

HasPoint(name) {
    global ini
    try {
        IniRead ini, "Points", name "X"
        IniRead ini, "Points", name "Y"
        return true
    } catch {
        return false
    }
}

MovePoint(name) {
    global exe, ini
    ux := Integer(IniRead(ini, "Points", name "X"))
    uy := Integer(IniRead(ini, "Points", name "Y"))
    WinGetClientPos &x, &y, &w, &h, exe
    if (w <= 0 || h <= 0) {
        ExitApp 4
    }

    sx := Round(ux * w / 1920)
    sy := Round((1080 - uy) * h / 1080)
    MouseMove sx, sy, 0
}

ClickPoint(name) {
    MovePoint(name)
    Click
}
