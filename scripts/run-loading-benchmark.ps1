param(
    [ValidateSet('Low','BelowNormal','Normal','High')]
    [string]$Priority = 'Normal',
    [int]$Runs = 10,
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\MrPrepper',
    [string]$AutoHotkeyExe = 'C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe',
    [int]$StartupDelayMs = 25000,
    [int]$BetweenClicksMs = 1500,
    [int]$RecoveryToContinueMs = 1200,
    [bool]$DismissRecoveryPrompt = $true,
    [int]$RunTimeoutSeconds = 90,
    [int]$GracefulCloseSeconds = 10,
    [int]$CooldownSeconds = 5
)

$ErrorActionPreference = 'Stop'

if ($Runs -lt 1) { throw 'Runs must be >= 1.' }

$repoRoot = Split-Path -Parent $PSScriptRoot
$ahkScript = Join-Path $PSScriptRoot 'mrprepper-benchmark.ahk'
$gameExe = Join-Path $GameDir 'MrPrepper.exe'
$bepInExDir = Join-Path $GameDir 'BepInEx'
$benchmarkCfg = Join-Path $bepInExDir 'config\actepukc.mrprepper.loadingbenchmark.cfg'
$profilerCfg = Join-Path $bepInExDir 'config\actepukc.mrprepper.loadingprofiler.cfg'
$csvPath = Join-Path $bepInExDir 'benchmark-results.csv'
$logPath = Join-Path $bepInExDir 'LogOutput.log'
$logArchiveDir = Join-Path $bepInExDir 'benchmark-logs'

foreach ($required in @($gameExe, $AutoHotkeyExe, $ahkScript, $benchmarkCfg)) {
    if (-not (Test-Path $required)) { throw "Required path not found: $required" }
}

New-Item -ItemType Directory -Path $logArchiveDir -Force | Out-Null

function Set-IniValue {
    param([string]$Path, [string]$Key, [string]$Value)
    $text = Get-Content -Raw -LiteralPath $Path
    $pattern = "(?m)^\s*" + [regex]::Escape($Key) + "\s*=.*$"
    if ($text -match $pattern) {
        $text = [regex]::Replace($text, $pattern, "$Key = $Value")
    } else {
        $text += "`r`n$Key = $Value`r`n"
    }
    Set-Content -LiteralPath $Path -Value $text -Encoding UTF8
}

function Get-CsvRunCount {
    if (-not (Test-Path $csvPath)) { return 0 }
    try { return @((Import-Csv -LiteralPath $csvPath)).Count } catch { return 0 }
}

function Stop-MrPrepperGracefully {
    $processes = @(Get-Process MrPrepper -ErrorAction SilentlyContinue)
    foreach ($proc in $processes) {
        try {
            if (-not $proc.HasExited) {
                [void]$proc.CloseMainWindow()
                if (-not $proc.WaitForExit([math]::Max(1, $GracefulCloseSeconds) * 1000)) {
                    Write-Warning "MrPrepper did not close gracefully within ${GracefulCloseSeconds}s; forcing exit."
                    $proc.Kill()
                    $proc.WaitForExit()
                }
            }
        } catch {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

function Archive-BepInExLog {
    param(
        [int]$RunNumber,
        [bool]$Completed
    )

    if (-not (Test-Path -LiteralPath $logPath)) {
        Write-Warning "BepInEx log not found after run ${RunNumber}: $logPath"
        return
    }

    $status = if ($Completed) { 'ok' } else { 'failed' }
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $name = '{0}-{1:D3}-{2}-{3}.log' -f $Priority, $RunNumber, $status, $timestamp
    $destination = Join-Path $logArchiveDir $name
    Copy-Item -LiteralPath $logPath -Destination $destination -Force
    Write-Host "[$RunNumber/$Runs] archived log: $destination"
}

Write-Host "Preparing benchmark: priority=$Priority runs=$Runs"
Set-IniValue -Path $benchmarkCfg -Key 'BackgroundLoadingPriority' -Value $Priority
Set-IniValue -Path $benchmarkCfg -Key 'Enabled' -Value 'true'
Set-IniValue -Path $benchmarkCfg -Key 'WriteCsv' -Value 'true'

if (Test-Path $profilerCfg) {
    Set-IniValue -Path $profilerCfg -Key 'OverrideBackgroundLoadingPriority' -Value 'false'
}

$startCount = Get-CsvRunCount
Write-Host "Existing CSV rows: $startCount"
Write-Host "Archived logs: $logArchiveDir"

for ($i = 1; $i -le $Runs; $i++) {
    Write-Host "[$i/$Runs] Starting Mr. Prepper ($Priority)..."

    if (Get-Process MrPrepper -ErrorAction SilentlyContinue) {
        Stop-MrPrepperGracefully
        Start-Sleep -Seconds 1
    }

    $before = Get-CsvRunCount
    Start-Process -FilePath $gameExe -WorkingDirectory $GameDir | Out-Null

    $dismissFlag = if ($DismissRecoveryPrompt) { 1 } else { 0 }
    Start-Process -FilePath $AutoHotkeyExe -ArgumentList @(
        '"' + $ahkScript + '"',
        $StartupDelayMs,
        $BetweenClicksMs,
        45,
        $dismissFlag,
        $RecoveryToContinueMs
    ) -Wait | Out-Null

    $deadline = (Get-Date).AddSeconds($RunTimeoutSeconds)
    $completed = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if ((Get-CsvRunCount) -gt $before) {
            $completed = $true
            break
        }
        if (-not (Get-Process MrPrepper -ErrorAction SilentlyContinue)) {
            break
        }
    }

    if ($completed) {
        $last = Import-Csv -LiteralPath $csvPath | Select-Object -Last 1
        Write-Host ("[{0}/{1}] done: RequestTo90={2}s RequestToSceneLoaded={3}s Total={4}s" -f $i,$Runs,$last.RequestTo90_s,$last.RequestToSceneLoaded_s,$last.TotalButtonToPostWindowEnd_s)
    } else {
        Write-Warning "[$i/$Runs] benchmark did not complete before timeout/game exit."
    }

    Stop-MrPrepperGracefully
    Start-Sleep -Milliseconds 300
    Archive-BepInExLog -RunNumber $i -Completed $completed

    if ($i -lt $Runs) { Start-Sleep -Seconds $CooldownSeconds }
}

$endCount = Get-CsvRunCount
Write-Host "Finished. New CSV rows: $($endCount - $startCount)"
Write-Host "CSV: $csvPath"
Write-Host "Logs: $logArchiveDir"

if (Test-Path $csvPath) {
    $rows = Import-Csv -LiteralPath $csvPath | Where-Object Priority -eq $Priority | Select-Object -Last $Runs
    if ($rows.Count -gt 0) {
        function Median([double[]]$values) {
            $v = $values | Sort-Object
            $n = $v.Count
            if ($n -eq 0) { return [double]::NaN }
            if ($n % 2) { return $v[[int]($n/2)] }
            return ($v[$n/2-1] + $v[$n/2]) / 2.0
        }
        [pscustomobject]@{
            Priority = $Priority
            Runs = $rows.Count
            MedianRequestTo90_s = [math]::Round((Median ([double[]]$rows.RequestTo90_s)), 3)
            MedianRequestToSceneLoaded_s = [math]::Round((Median ([double[]]$rows.RequestToSceneLoaded_s)), 3)
            MedianLargestPreLoadFrame_ms = [math]::Round((Median ([double[]]$rows.LargestPreLoadFrame_ms)), 1)
            MedianPostLoadLargest_ms = [math]::Round((Median ([double[]]$rows.PostLoadLargest_ms)), 1)
            MedianTotal_s = [math]::Round((Median ([double[]]$rows.TotalButtonToPostWindowEnd_s)), 3)
        } | Format-List
    }
}
