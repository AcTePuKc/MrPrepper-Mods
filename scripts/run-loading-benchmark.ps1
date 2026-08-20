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

$cfg = @{
    Benchmark = Join-Path $configDir 'actepukc.mrprepper.loadingbenchmark.cfg'
    Loading = Join-Path $configDir 'actepukc.mrprepper.loadingprofiler.cfg'
    DialogueLoc = Join-Path $configDir 'actepukc.mrprepper.dialoguelocalizationprofiler.cfg'
    DialogueTag = Join-Path $configDir 'actepukc.mrprepper.dialoguetagprofiler.cfg'
    Lifecycle = Join-Path $configDir 'actepukc.mrprepper.main16lifecycleprofiler.cfg'
    RegexCache = Join-Path $configDir 'actepukc.mrprepper.dialoguetagregexcacheexperiment.cfg'
}

$csvPath = Join-Path $bepInExDir 'benchmark-results.csv'
$experimentCsvPath = Join-Path $bepInExDir 'benchmark-experiment-results.csv'
$logPath = Join-Path $bepInExDir 'LogOutput.log'
$logArchiveDir = Join-Path $bepInExDir 'benchmark-logs'

foreach ($required in @($gameExe, $AutoHotkeyExe, $ahkScript, $cfg.Benchmark)) {
    if (-not (Test-Path $required)) { throw "Required path not found: $required" }
}
New-Item -ItemType Directory -Path $logArchiveDir -Force | Out-Null

function Set-CfgValue {
    param([string]$Path,[string]$Section,[string]$Key,[string]$Value)

    if (-not (Test-Path $Path)) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
        Set-Content -LiteralPath $Path -Value "[$Section]`r`n$Key = $Value`r`n" -Encoding UTF8
        return
    }

    $text = Get-Content -Raw -LiteralPath $Path
    $sectionPattern = '(?ms)^\s*\[' + [regex]::Escape($Section) + '\]\s*\r?\n(?<body>.*?)(?=^\s*\[|\z)'
    $m = [regex]::Match($text, $sectionPattern)

    if ($m.Success) {
        $body = $m.Groups['body'].Value
        $keyPattern = '(?m)^\s*' + [regex]::Escape($Key) + '\s*=.*$'
        if ([regex]::IsMatch($body, $keyPattern)) {
            $body2 = [regex]::Replace($body, $keyPattern, "$Key = $Value")
        } else {
            $body2 = $body + $(if ($body.EndsWith("`n")) { '' } else { "`r`n" }) + "$Key = $Value`r`n"
        }
        $text = $text.Substring(0,$m.Groups['body'].Index) + $body2 + $text.Substring($m.Groups['body'].Index + $m.Groups['body'].Length)
    } else {
        if (-not $text.EndsWith("`n")) { $text += "`r`n" }
        $text += "`r`n[$Section]`r`n$Key = $Value`r`n"
    }
    Set-Content -LiteralPath $Path -Value $text -Encoding UTF8
}

function BoolText([bool]$v) { if ($v) { 'true' } else { 'false' } }
function Get-CsvRunCount { if (Test-Path $csvPath) { try { @((Import-Csv $csvPath)).Count } catch { 0 } } else { 0 } }
function Median([double[]]$values) {
    $v = @($values | Sort-Object); $n = $v.Count
    if ($n -eq 0) { return [double]::NaN }
    if ($n % 2) { return [double]$v[[int]($n/2)] }
    return ([double]$v[$n/2-1] + [double]$v[$n/2]) / 2.0
}

function Stop-Game {
    foreach ($p in @(Get-Process MrPrepper -ErrorAction SilentlyContinue)) {
        try {
            if (-not $p.HasExited) {
                [void]$p.CloseMainWindow()
                if (-not $p.WaitForExit([math]::Max(1,$GracefulCloseSeconds)*1000)) { $p.Kill(); $p.WaitForExit() }
            }
        } catch { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
}

function Set-ProfilerMode {
    $loading = $ProfilerMode -ne 'Clean'
    $diag = $ProfilerMode -ne 'Clean'
    $tag = $ProfilerMode -eq 'Full'
    Set-CfgValue $cfg.Loading 'General' 'Enabled' (BoolText $loading)
    Set-CfgValue $cfg.DialogueLoc 'DialogueLocalization' 'Enabled' (BoolText $diag)
    Set-CfgValue $cfg.DialogueTag 'DialogueTag' 'Enabled' (BoolText $tag)
    Set-CfgValue $cfg.Lifecycle 'Main16Lifecycle' 'Enabled' (BoolText $diag)
    Set-CfgValue $cfg.Loading 'Experiment' 'OverrideBackgroundLoadingPriority' 'false'
}

function Get-Variants {
    if ($Experiment -eq 'RegexCacheAB') {
        [pscustomobject]@{ Name='RegexCacheOn'; RegexCache=$true }
        [pscustomobject]@{ Name='RegexCacheOff'; RegexCache=$false }
        return
    }
    [pscustomobject]@{ Name=$(if($RegexCacheEnabled){'RegexCacheOn'}else{'RegexCacheOff'}); RegexCache=$RegexCacheEnabled }
}

function Get-Plan([object[]]$Variants) {
    if ($VariantOrder -eq 'Grouped') {
        foreach ($v in $Variants) {
            for ($r=1; $r -le $Runs; $r++) {
                [pscustomobject]@{ Round=$r; Variant=$v }
            }
        }
        return
    }

    if ($VariantOrder -eq 'Random') {
        $tmp = @()
        for ($r=1; $r -le $Runs; $r++) {
            foreach ($v in $Variants) {
                $tmp += [pscustomobject]@{ Round=$r; Variant=$v }
            }
        }
        $tmp | Sort-Object { Get-Random }
        return
    }

    for ($r=1; $r -le $Runs; $r++) {
        $ordered = if (($r % 2) -eq 1) {
            @($Variants)
        } else {
            @($Variants[($Variants.Count-1)..0])
        }
        foreach ($v in $ordered) {
            [pscustomobject]@{ Round=$r; Variant=$v }
        }
    }
}

function Archive-Log([int]$Index,[int]$Total,[string]$Variant,[int]$Round,[bool]$Completed) {
    if(-not(Test-Path $logPath)){ return }
    $status = if($Completed){'ok'}else{'failed'}
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $name = '{0}-{1}-r{2:D2}-{3:D3}of{4:D3}-{5}-{6}.log' -f $Priority,$Variant,$Round,$Index,$Total,$status,$stamp
    Copy-Item $logPath (Join-Path $logArchiveDir $name) -Force
}

function Add-ExperimentRow($Variant,[int]$Round,[int]$Index,$b) {
    $row = [pscustomobject]@{
        Timestamp=(Get-Date).ToString('o'); Experiment=$Experiment; Variant=$Variant; VariantOrder=$VariantOrder; ProfilerMode=$ProfilerMode
        Round=$Round; PlanIndex=$Index; Priority=$b.Priority; NaturalPriority=$b.NaturalPriority
        ButtonToRequest_s=$b.ButtonToRequest_s; RequestTo90_s=$b.RequestTo90_s; Progress90ToSceneLoaded_s=$b.Progress90ToSceneLoaded_s
        RequestToSceneLoaded_s=$b.RequestToSceneLoaded_s; LargestPreLoadFrame_ms=$b.LargestPreLoadFrame_ms
        PostLoadLargest_ms=$b.PostLoadLargest_ms; PostLoadSecond_ms=$b.PostLoadSecond_ms; PostLoadWindow_ms=$b.PostLoadWindow_ms
        TotalButtonToPostWindowEnd_s=$b.TotalButtonToPostWindowEnd_s
    }
    if(Test-Path $experimentCsvPath){ $row | Export-Csv $experimentCsvPath -Append -NoTypeInformation }
    else { $row | Export-Csv $experimentCsvPath -NoTypeInformation }
}

$backup = @{}
foreach($p in $cfg.Values){
    $backup[$p] = if(Test-Path $p){ [pscustomobject]@{Exists=$true;Content=(Get-Content -Raw $p)} } else { [pscustomobject]@{Exists=$false;Content=$null} }
}

$variants = @(Get-Variants)
$plan = @(Get-Plan -Variants $variants)
$session = @()

if ($variants.Count -lt 1 -or $plan.Count -lt 1) {
    throw "Experiment produced an empty run plan. Experiment=$Experiment Runs=$Runs"
}

Write-Host "Experiment=$Experiment Priority=$Priority RunsPerVariant=$Runs Variants=$($variants.Count) TotalRuns=$($plan.Count) ProfilerMode=$ProfilerMode Order=$VariantOrder"
Write-Host "Results: $experimentCsvPath"

try {
    Set-CfgValue $cfg.Benchmark 'Benchmark' 'BackgroundLoadingPriority' $Priority
    Set-CfgValue $cfg.Benchmark 'Benchmark' 'Enabled' 'true'
    Set-CfgValue $cfg.Benchmark 'Benchmark' 'WriteCsv' 'true'
    Set-ProfilerMode

    for($i=0;$i -lt $plan.Count;$i++){
        $item=$plan[$i]; $idx=$i+1; $v=$item.Variant; $round=[int]$item.Round
        Set-CfgValue $cfg.RegexCache 'DialogueTagRegexCache' 'Enabled' (BoolText $v.RegexCache)
        Write-Host "[$idx/$($plan.Count)] $($v.Name) round=$round"

        if(Get-Process MrPrepper -ErrorAction SilentlyContinue){ Stop-Game; Start-Sleep 1 }
        $before=Get-CsvRunCount
        Start-Process $gameExe -WorkingDirectory $GameDir | Out-Null
        $dismiss=if($DismissRecoveryPrompt){1}else{0}
        Start-Process $AutoHotkeyExe -ArgumentList @('"'+$ahkScript+'"',$StartupDelayMs,$BetweenClicksMs,45,$dismiss,$RecoveryToContinueMs) -Wait | Out-Null

        $deadline=(Get-Date).AddSeconds($RunTimeoutSeconds); $done=$false
        while((Get-Date)-lt$deadline){
            Start-Sleep -Milliseconds 500
            if((Get-CsvRunCount)-gt$before){$done=$true;break}
            if(-not(Get-Process MrPrepper -ErrorAction SilentlyContinue)){break}
        }

        if($done){
            $b=Import-Csv $csvPath | Select-Object -Last 1
            Add-ExperimentRow $v.Name $round $idx $b
            $session += [pscustomobject]@{
                Variant=$v.Name; RequestToSceneLoaded_s=[double]$b.RequestToSceneLoaded_s; LargestPreLoadFrame_ms=[double]$b.LargestPreLoadFrame_ms
                PostLoadLargest_ms=[double]$b.PostLoadLargest_ms; PostLoadWindow_ms=[double]$b.PostLoadWindow_ms; Total_s=[double]$b.TotalButtonToPostWindowEnd_s
            }
            Write-Host ("  Scene={0}s Pre={1}ms Post={2}ms Window={3}ms Total={4}s" -f $b.RequestToSceneLoaded_s,$b.LargestPreLoadFrame_ms,$b.PostLoadLargest_ms,$b.PostLoadWindow_ms,$b.TotalButtonToPostWindowEnd_s)
        } else { Write-Warning "[$idx/$($plan.Count)] run failed or timed out" }

        Stop-Game; Start-Sleep -Milliseconds 300; Archive-Log $idx $plan.Count $v.Name $round $done
        if($idx -lt $plan.Count){ Start-Sleep -Seconds $CooldownSeconds }
    }

    Write-Host ''; Write-Host '=== Session medians ==='
    $summaries=@()
    foreach($g in ($session | Group-Object Variant)){
        $r=@($g.Group)
        $s=[pscustomobject]@{
            Variant=$g.Name; Runs=$r.Count
            Scene_s=[math]::Round((Median ([double[]]$r.RequestToSceneLoaded_s)),3)
            Pre_ms=[math]::Round((Median ([double[]]$r.LargestPreLoadFrame_ms)),1)
            Post_ms=[math]::Round((Median ([double[]]$r.PostLoadLargest_ms)),1)
            Window_ms=[math]::Round((Median ([double[]]$r.PostLoadWindow_ms)),1)
            Total_s=[math]::Round((Median ([double[]]$r.Total_s)),3)
        }
        $summaries += $s
    }
    $summaries | Format-Table -AutoSize

    if($Experiment -eq 'RegexCacheAB'){
        $on=$summaries|Where-Object Variant -eq 'RegexCacheOn'|Select-Object -First 1
        $off=$summaries|Where-Object Variant -eq 'RegexCacheOff'|Select-Object -First 1
        if($on -and $off){
            Write-Host 'ON minus OFF (negative = faster):'
            [pscustomobject]@{
                Scene_s=[math]::Round($on.Scene_s-$off.Scene_s,3)
                Pre_ms=[math]::Round($on.Pre_ms-$off.Pre_ms,1)
                Post_ms=[math]::Round($on.Post_ms-$off.Post_ms,1)
                Window_ms=[math]::Round($on.Window_ms-$off.Window_ms,1)
                Total_s=[math]::Round($on.Total_s-$off.Total_s,3)
            }|Format-List
        }
    }
}
finally {
    Stop-Game
    if($RestoreConfigs){
        foreach($p in $cfg.Values){
            $b=$backup[$p]
            if($b.Exists){Set-Content -LiteralPath $p -Value $b.Content -Encoding UTF8}
            elseif(Test-Path $p){Remove-Item $p -Force}
        }
        Write-Host 'Original profiler configs restored.'
    }
}
