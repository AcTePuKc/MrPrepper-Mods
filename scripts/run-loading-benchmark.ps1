param(
    [ValidateSet('Low','BelowNormal','Normal','High')]
    [string]$Priority = 'High',

    [ValidateSet('Single','RegexCacheAB')]
    [string]$Experiment = 'RegexCacheAB',

    [int]$Runs = 10,

    [ValidateSet('Alternating','Grouped','Random')]
    [string]$VariantOrder = 'Alternating',

    [ValidateSet('Clean','Diagnostic','Full')]
    [string]$ProfilerMode = 'Clean',

    [bool]$RegexCacheEnabled = $true,

    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\MrPrepper',
    [string]$AutoHotkeyExe = 'C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe',
    [int]$StartupDelayMs = 25000,
    [int]$BetweenClicksMs = 1500,
    [int]$RecoveryToContinueMs = 1200,
    [bool]$DismissRecoveryPrompt = $true,
    [int]$RunTimeoutSeconds = 90,
    [int]$GracefulCloseSeconds = 10,
    [int]$CooldownSeconds = 5,
    [bool]$RestoreConfigs = $true
)

$ErrorActionPreference = 'Stop'

if ($Runs -lt 1) { throw 'Runs must be >= 1.' }

$ahkScript = Join-Path $PSScriptRoot 'mrprepper-benchmark.ahk'
$gameExe = Join-Path $GameDir 'MrPrepper.exe'
$bepInExDir = Join-Path $GameDir 'BepInEx'
$configDir = Join-Path $bepInExDir 'config'

$benchmarkCfg = Join-Path $configDir 'actepukc.mrprepper.loadingbenchmark.cfg'
$loadingProfilerCfg = Join-Path $configDir 'actepukc.mrprepper.loadingprofiler.cfg'
$dialogueLocalizationCfg = Join-Path $configDir 'actepukc.mrprepper.dialoguelocalizationprofiler.cfg'
$dialogueTagCfg = Join-Path $configDir 'actepukc.mrprepper.dialoguetagprofiler.cfg'
$lifecycleCfg = Join-Path $configDir 'actepukc.mrprepper.main16lifecycleprofiler.cfg'
$regexCacheCfg = Join-Path $configDir 'actepukc.mrprepper.dialoguetagregexcacheexperiment.cfg'

$csvPath = Join-Path $bepInExDir 'benchmark-results.csv'
$experimentCsvPath = Join-Path $bepInExDir 'benchmark-experiment-results.csv'
$logPath = Join-Path $bepInExDir 'LogOutput.log'
$logArchiveDir = Join-Path $bepInExDir 'benchmark-logs'

foreach ($required in @($gameExe, $AutoHotkeyExe, $ahkScript, $benchmarkCfg)) {
    if (-not (Test-Path $required)) { throw "Required path not found: $required" }
}

New-Item -ItemType Directory -Path $logArchiveDir -Force | Out-Null

function Set-IniValue {
    param(
        [string]$Path,
        [string]$Section,
        [string]$Key,
        [string]$Value,
        [bool]$CreateIfMissing = $true
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        if (-not $CreateIfMissing) { return }
        $parent = Split-Path -Parent $Path
        if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        Set-Content -LiteralPath $Path -Value "[$Section]`r`n$Key = $Value`r`n" -Encoding UTF8
        return
    }

    $text = Get-Content -Raw -LiteralPath $Path
    $sectionPattern = '(?ms)^\s*\[' + [regex]::Escape($Section) + '\]\s*\r?\n(?<body>.*?)(?=^\s*\[|\z)'
    $sectionMatch = [regex]::Match($text, $sectionPattern)

    if ($sectionMatch.Success) {
        $body = $sectionMatch.Groups['body'].Value
        $keyPattern = '(?m)^\s*' + [regex]::Escape($Key) + '\s*=.*$'
        if ([regex]::IsMatch($body, $keyPattern)) {
            $newBody = [regex]::Replace($body, $keyPattern, "$Key = $Value")
        } else {
            $newBody = $body
            if ($newBody.Length -gt 0 -and -not $newBody.EndsWith("`n")) { $newBody += "`r`n" }
            $newBody += "$Key = $Value`r`n"
        }
        $text = $text.Substring(0, $sectionMatch.Groups['body'].Index) + $newBody +
                $text.Substring($sectionMatch.Groups['body'].Index + $sectionMatch.Groups['body'].Length)
    } else {
        if ($text.Length -gt 0 -and -not $text.EndsWith("`n")) { $text += "`r`n" }
        $text += "`r`n[$Section]`r`n$Key = $Value`r`n"
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
        [int]$PlanIndex,
        [int]$PlanCount,
        [string]$Variant,
        [int]$Round,
        [bool]$Completed
    )

    if (-not (Test-Path -LiteralPath $logPath)) {
        Write-Warning "BepInEx log not found after plan item ${PlanIndex}: $logPath"
        return
    }

    $status = if ($Completed) { 'ok' } else { 'failed' }
    $safeVariant = $Variant -replace '[^A-Za-z0-9_.-]', '_'
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $name = '{0}-{1}-r{2:D2}-{3:D3}of{4:D3}-{5}-{6}.log' -f $Priority,$safeVariant,$Round,$PlanIndex,$PlanCount,$status,$timestamp
    $destination = Join-Path $logArchiveDir $name
    Copy-Item -LiteralPath $logPath -Destination $destination -Force
    Write-Host "[$PlanIndex/$PlanCount] archived log: $destination"
}

function Get-VariantDefinitions {
    if ($Experiment -eq 'RegexCacheAB') {
        return @(
            [pscustomobject]@{ Name = 'RegexCacheOn'; RegexCache = $true },
            [pscustomobject]@{ Name = 'RegexCacheOff'; RegexCache = $false }
        )
    }

    return @(
        [pscustomobject]@{ Name = if ($RegexCacheEnabled) { 'RegexCacheOn' } else { 'RegexCacheOff' }; RegexCache = $RegexCacheEnabled }
    )
}

function Build-RunPlan {
    param([object[]]$Variants)

    $plan = New-Object System.Collections.Generic.List[object]

    if ($Variants.Count -eq 1) {
        for ($round = 1; $round -le $Runs; $round++) {
            $plan.Add([pscustomobject]@{ Round = $round; Variant = $Variants[0] })
        }
        return $plan
    }

    if ($VariantOrder -eq 'Grouped') {
        foreach ($variant in $Variants) {
            for ($round = 1; $round -le $Runs; $round++) {
                $plan.Add([pscustomobject]@{ Round = $round; Variant = $variant })
            }
        }
        return $plan
    }

    if ($VariantOrder -eq 'Random') {
        $temp = New-Object System.Collections.Generic.List[object]
        for ($round = 1; $round -le $Runs; $round++) {
            foreach ($variant in $Variants) {
                $temp.Add([pscustomobject]@{ Round = $round; Variant = $variant })
            }
        }
        return @($temp | Sort-Object { Get-Random })
    }

    for ($round = 1; $round -le $Runs; $round++) {
        $ordered = if (($round % 2) -eq 1) { $Variants } else { @($Variants | Select-Object -Reverse) }
        foreach ($variant in $ordered) {
            $plan.Add([pscustomobject]@{ Round = $round; Variant = $variant })
        }
    }

    return $plan
}

function Set-ProfilerMode {
    switch ($ProfilerMode) {
        'Clean' {
            Set-IniValue $loadingProfilerCfg 'General' 'Enabled' 'false'
            Set-IniValue $dialogueLocalizationCfg 'DialogueLocalization' 'Enabled' 'false'
            Set-IniValue $dialogueTagCfg 'DialogueTag' 'Enabled' 'false'
            Set-IniValue $lifecycleCfg 'Main16Lifecycle' 'Enabled' 'false'
        }
        'Diagnostic' {
            Set-IniValue $loadingProfilerCfg 'General' 'Enabled' 'true'
            Set-IniValue $dialogueLocalizationCfg 'DialogueLocalization' 'Enabled' 'true'
            Set-IniValue $dialogueTagCfg 'DialogueTag' 'Enabled' 'false'
            Set-IniValue $lifecycleCfg 'Main16Lifecycle' 'Enabled' 'true'
        }
        'Full' {
            Set-IniValue $loadingProfilerCfg 'General' 'Enabled' 'true'
            Set-IniValue $dialogueLocalizationCfg 'DialogueLocalization' 'Enabled' 'true'
            Set-IniValue $dialogueTagCfg 'DialogueTag' 'Enabled' 'true'
            Set-IniValue $lifecycleCfg 'Main16Lifecycle' 'Enabled' 'true'
        }
    }

    if (Test-Path $loadingProfilerCfg) {
        Set-IniValue $loadingProfilerCfg 'Experiment' 'OverrideBackgroundLoadingPriority' 'false' $false
    }
}

function Apply-Variant {
    param($Variant)
    Set-IniValue $regexCacheCfg 'DialogueTagRegexCache' 'Enabled' ($(if ($Variant.RegexCache) { 'true' } else { 'false' }))
}

function Median([double[]]$values) {
    $v = @($values | Sort-Object)
    $n = $v.Count
    if ($n -eq 0) { return [double]::NaN }
    if ($n % 2) { return [double]$v[[int]($n / 2)] }
    return ([double]$v[$n / 2 - 1] + [double]$v[$n / 2]) / 2.0
}

function Add-ExperimentRow {
    param(
        [string]$Variant,
        [int]$Round,
        [int]$PlanIndex,
        $BenchmarkRow
    )

    $row = [pscustomobject]@{
        Timestamp = (Get-Date).ToString('o')
        Experiment = $Experiment
        Variant = $Variant
        VariantOrder = $VariantOrder
        ProfilerMode = $ProfilerMode
        Round = $Round
        PlanIndex = $PlanIndex
        Priority = $BenchmarkRow.Priority
        NaturalPriority = $BenchmarkRow.NaturalPriority
        ButtonToRequest_s = $BenchmarkRow.ButtonToRequest_s
        RequestTo90_s = $BenchmarkRow.RequestTo90_s
        Progress90ToSceneLoaded_s = $BenchmarkRow.Progress90ToSceneLoaded_s
        RequestToSceneLoaded_s = $BenchmarkRow.RequestToSceneLoaded_s
        LargestPreLoadFrame_ms = $BenchmarkRow.LargestPreLoadFrame_ms
        PostLoadLargest_ms = $BenchmarkRow.PostLoadLargest_ms
        PostLoadSecond_ms = $BenchmarkRow.PostLoadSecond_ms
        PostLoadWindow_ms = $BenchmarkRow.PostLoadWindow_ms
        TotalButtonToPostWindowEnd_s = $BenchmarkRow.TotalButtonToPostWindowEnd_s
    }

    if (Test-Path $experimentCsvPath) {
        $row | Export-Csv -LiteralPath $experimentCsvPath -Append -NoTypeInformation
    } else {
        $row | Export-Csv -LiteralPath $experimentCsvPath -NoTypeInformation
    }
}

function Show-ExperimentSummary {
    param([object[]]$SessionRows)

    if ($SessionRows.Count -eq 0) { return }

    Write-Host ''
    Write-Host '=== Experiment summary ==='

    $summaries = @()
    foreach ($group in ($SessionRows | Group-Object Variant)) {
        $rows = @($group.Group)
        $summary = [pscustomobject]@{
            Variant = $group.Name
            Runs = $rows.Count
            MedianRequestTo90_s = [math]::Round((Median ([double[]]$rows.RequestTo90_s)), 3)
            MedianRequestToSceneLoaded_s = [math]::Round((Median ([double[]]$rows.RequestToSceneLoaded_s)), 3)
            MedianLargestPreLoadFrame_ms = [math]::Round((Median ([double[]]$rows.LargestPreLoadFrame_ms)), 1)
            MedianPostLoadLargest_ms = [math]::Round((Median ([double[]]$rows.PostLoadLargest_ms)), 1)
            MedianPostLoadWindow_ms = [math]::Round((Median ([double[]]$rows.PostLoadWindow_ms)), 1)
            MedianTotal_s = [math]::Round((Median ([double[]]$rows.TotalButtonToPostWindowEnd_s)), 3)
        }
        $summaries += $summary
    }

    $summaries | Format-Table -AutoSize

    if ($Experiment -eq 'RegexCacheAB') {
        $on = $summaries | Where-Object Variant -eq 'RegexCacheOn' | Select-Object -First 1
        $off = $summaries | Where-Object Variant -eq 'RegexCacheOff' | Select-Object -First 1
        if ($on -and $off) {
            Write-Host ''
            Write-Host 'RegexCacheOn minus RegexCacheOff (negative is faster):'
            [pscustomobject]@{
                DeltaRequestToSceneLoaded_s = [math]::Round(($on.MedianRequestToSceneLoaded_s - $off.MedianRequestToSceneLoaded_s), 3)
                DeltaPostLoadLargest_ms = [math]::Round(($on.MedianPostLoadLargest_ms - $off.MedianPostLoadLargest_ms), 1)
                DeltaPostLoadWindow_ms = [math]::Round(($on.MedianPostLoadWindow_ms - $off.MedianPostLoadWindow_ms), 1)
                DeltaTotal_s = [math]::Round(($on.MedianTotal_s - $off.MedianTotal_s), 3)
            } | Format-List
        }
    }
}

$configFilesToRestore = @(
    $benchmarkCfg,
    $loadingProfilerCfg,
    $dialogueLocalizationCfg,
    $dialogueTagCfg,
    $lifecycleCfg,
    $regexCacheCfg
)

$configBackup = @{}
foreach ($cfg in $configFilesToRestore) {
    if (Test-Path -LiteralPath $cfg) {
        $configBackup[$cfg] = [pscustomobject]@{ Exists = $true; Content = Get-Content -Raw -LiteralPath $cfg }
    } else {
        $configBackup[$cfg] = [pscustomobject]@{ Exists = $false; Content = $null }
    }
}

$variants = @(Get-VariantDefinitions)
$plan = @(Build-RunPlan -Variants $variants)
$sessionRows = New-Object System.Collections.Generic.List[object]

Write-Host "Preparing benchmark: experiment=$Experiment priority=$Priority runsPerVariant=$Runs variants=$($variants.Count) totalRuns=$($plan.Count) profilerMode=$ProfilerMode order=$VariantOrder"
Write-Host "Experiment CSV: $experimentCsvPath"
Write-Host "Archived logs: $logArchiveDir"

try {
    Set-IniValue $benchmarkCfg 'Benchmark' 'BackgroundLoadingPriority' $Priority
    Set-IniValue $benchmarkCfg 'Benchmark' 'Enabled' 'true'
    Set-IniValue $benchmarkCfg 'Benchmark' 'WriteCsv' 'true'
    Set-ProfilerMode

    $startCount = Get-CsvRunCount
    Write-Host "Existing benchmark CSV rows: $startCount"

    for ($index = 0; $index -lt $plan.Count; $index++) {
        $item = $plan[$index]
        $planIndex = $index + 1
        $variant = $item.Variant
        $round = [int]$item.Round

        Apply-Variant -Variant $variant
        Write-Host "[$planIndex/$($plan.Count)] Starting Mr. Prepper variant=$($variant.Name) round=$round priority=$Priority..."

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
            Add-ExperimentRow -Variant $variant.Name -Round $round -PlanIndex $planIndex -BenchmarkRow $last
            $sessionRows.Add([pscustomobject]@{
                Variant = $variant.Name
                Round = $round
                RequestTo90_s = [double]$last.RequestTo90_s
                RequestToSceneLoaded_s = [double]$last.RequestToSceneLoaded_s
                LargestPreLoadFrame_ms = [double]$last.LargestPreLoadFrame_ms
                PostLoadLargest_ms = [double]$last.PostLoadLargest_ms
                PostLoadWindow_ms = [double]$last.PostLoadWindow_ms
                TotalButtonToPostWindowEnd_s = [double]$last.TotalButtonToPostWindowEnd_s
            })

            Write-Host ("[{0}/{1}] done {2} r{3}: RequestTo90={4}s SceneLoaded={5}s PostLargest={6}ms PostWindow={7}ms Total={8}s" -f `
                $planIndex,$plan.Count,$variant.Name,$round,$last.RequestTo90_s,$last.RequestToSceneLoaded_s,$last.PostLoadLargest_ms,$last.PostLoadWindow_ms,$last.TotalButtonToPostWindowEnd_s)
        } else {
            Write-Warning "[$planIndex/$($plan.Count)] benchmark did not complete before timeout/game exit."
        }

        Stop-MrPrepperGracefully
        Start-Sleep -Milliseconds 300
        Archive-BepInExLog -PlanIndex $planIndex -PlanCount $plan.Count -Variant $variant.Name -Round $round -Completed $completed

        if ($planIndex -lt $plan.Count) { Start-Sleep -Seconds $CooldownSeconds }
    }

    $endCount = Get-CsvRunCount
    Write-Host "Finished. New benchmark CSV rows: $($endCount - $startCount)"
    Write-Host "Benchmark CSV: $csvPath"
    Write-Host "Experiment CSV: $experimentCsvPath"
    Write-Host "Logs: $logArchiveDir"

    Show-ExperimentSummary -SessionRows @($sessionRows)
}
finally {
    Stop-MrPrepperGracefully

    if ($RestoreConfigs) {
        foreach ($cfg in $configFilesToRestore) {
            $backup = $configBackup[$cfg]
            if ($backup.Exists) {
                Set-Content -LiteralPath $cfg -Value $backup.Content -Encoding UTF8
            } elseif (Test-Path -LiteralPath $cfg) {
                Remove-Item -LiteralPath $cfg -Force
            }
        }
        Write-Host 'Original BepInEx config files restored.'
    }
}
