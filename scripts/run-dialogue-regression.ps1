param(
    [int]$Runs = 5,
    [ValidateSet('Alternating','Grouped')]
    [string]$VariantOrder = 'Alternating',
    [string]$GameDir = 'C:\Program Files (x86)\Steam\steamapps\common\MrPrepper',
    [string]$AutoHotkeyExe = 'C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe',
    [int]$CooldownSeconds = 5,
    [bool]$RestoreConfigs = $true
)

$ErrorActionPreference = 'Stop'
if ($Runs -lt 1) { throw 'Runs must be >= 1.' }

$gameExe = Join-Path $GameDir 'MrPrepper.exe'
$bepInExDir = Join-Path $GameDir 'BepInEx'
$configDir = Join-Path $bepInExDir 'config'
$ahkScript = Join-Path $PSScriptRoot 'mrprepper-newgame-regression.ahk'
$ahkIni = Join-Path $PSScriptRoot 'mrprepper-newgame-regression.ini'
$logPath = Join-Path $bepInExDir 'LogOutput.log'
$csvPath = Join-Path $bepInExDir 'dialogue-regression-results.csv'
$logArchiveDir = Join-Path $bepInExDir 'dialogue-regression-logs'

$cfg = @{
    Benchmark   = Join-Path $configDir 'actepukc.mrprepper.loadingbenchmark.cfg'
    Loading     = Join-Path $configDir 'actepukc.mrprepper.loadingprofiler.cfg'
    DialogueLoc = Join-Path $configDir 'actepukc.mrprepper.dialoguelocalizationprofiler.cfg'
    DialogueTag = Join-Path $configDir 'actepukc.mrprepper.dialoguetagprofiler.cfg'
    Lifecycle   = Join-Path $configDir 'actepukc.mrprepper.main16lifecycleprofiler.cfg'
    RegexCache  = Join-Path $configDir 'actepukc.mrprepper.dialoguetagregexcacheexperiment.cfg'
}

foreach ($required in @($gameExe,$AutoHotkeyExe,$ahkScript,$ahkIni)) {
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
    $m = [regex]::Match($text,$sectionPattern)
    if ($m.Success) {
        $body = $m.Groups['body'].Value
        $keyPattern = '(?m)^\s*' + [regex]::Escape($Key) + '\s*=.*$'
        if ([regex]::IsMatch($body,$keyPattern)) {
            $body2 = [regex]::Replace($body,$keyPattern,"$Key = $Value")
        } else {
            $body2 = $body + $(if($body.EndsWith("`n")){''}else{"`r`n"}) + "$Key = $Value`r`n"
        }
        $text = $text.Substring(0,$m.Groups['body'].Index) + $body2 + $text.Substring($m.Groups['body'].Index + $m.Groups['body'].Length)
    } else {
        if (-not $text.EndsWith("`n")) { $text += "`r`n" }
        $text += "`r`n[$Section]`r`n$Key = $Value`r`n"
    }
    Set-Content -LiteralPath $Path -Value $text -Encoding UTF8
}

function BoolText([bool]$v) { if($v){'true'}else{'false'} }

function Stop-Game {
    foreach($p in @(Get-Process MrPrepper -ErrorAction SilentlyContinue)) {
        try { if(-not $p.HasExited){ $p.Kill(); $p.WaitForExit() } } catch { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    }
}

function Get-Plan {
    $on  = [pscustomobject]@{Name='RegexCacheOn'; Enabled=$true}
    $off = [pscustomobject]@{Name='RegexCacheOff';Enabled=$false}
    if($VariantOrder -eq 'Grouped') {
        foreach($v in @($on,$off)){ for($r=1;$r -le $Runs;$r++){ [pscustomobject]@{Round=$r;Variant=$v} } }
        return
    }
    for($r=1;$r -le $Runs;$r++) {
        $order = if(($r % 2)-eq 1){@($on,$off)}else{@($off,$on)}
        foreach($v in $order){ [pscustomobject]@{Round=$r;Variant=$v} }
    }
}

function Get-LogAnalysis([string]$Text,[bool]$CacheEnabled) {
    $newGame = $Text -match "text='Нова игра'"
    $remove = $Text -match "SavedPanel/Remove' text='Премахни'"
    $play = $Text -match "SavedPanel/Play' text='Играй'"
    $noTutorial = $Text -match "TutAsk/.+text='Не'"
    $exit = $Text -match "ExitToWindows' text='Изход от играта'"
    $exitYes = $Text -match "AskReusable/Yes' text='Да'"

    $runtimeErrors = @($Text -split "`r?`n" | Where-Object {
        ($_ -match '^\[(Error|Fatal)\s*:') -or
        (($_ -match 'DialogueKeyError') -and ($_ -notmatch 'Dialogue IL'))
    })

    $cachePatterns = $null; $cacheHits = $null; $cacheMisses = $null
    $m = [regex]::Match($Text,'\[DIALOGUE TAG CACHE SUMMARY\]\s+patterns=(\d+)\s+hits=(\d+)\s+misses=(\d+)')
    if($m.Success) {
        $cachePatterns = [int]$m.Groups[1].Value
        $cacheHits = [int]$m.Groups[2].Value
        $cacheMisses = [int]$m.Groups[3].Value
    }

    $cacheSummaryOk = if($CacheEnabled){ $m.Success -and $cachePatterns -eq 7 -and $cacheMisses -eq 7 } else { -not $m.Success }
    $uiOk = $newGame -and $remove -and $play -and $noTutorial -and $exit -and $exitYes

    [pscustomobject]@{
        UiFlowOk=$uiOk; NewGame=$newGame; Remove=$remove; Play=$play; NoTutorial=$noTutorial; Exit=$exit; ExitYes=$exitYes
        RuntimeErrorCount=$runtimeErrors.Count; CacheSummaryOk=$cacheSummaryOk
        CachePatterns=$cachePatterns; CacheHits=$cacheHits; CacheMisses=$cacheMisses
    }
}

$backup=@{}
foreach($p in $cfg.Values){
    $backup[$p]=if(Test-Path $p){[pscustomobject]@{Exists=$true;Content=(Get-Content -Raw $p)}}else{[pscustomobject]@{Exists=$false;Content=$null}}
}

$plan=@(Get-Plan)
Write-Host "Dialogue regression: RunsPerVariant=$Runs TotalRuns=$($plan.Count) Order=$VariantOrder"
Write-Host "Results: $csvPath"

try {
    ; Match the successful manual diagnostic setup while avoiding the hot DialogueTag profiler.
    Set-CfgValue $cfg.Benchmark 'Benchmark' 'Enabled' 'true'
    Set-CfgValue $cfg.Benchmark 'Benchmark' 'WriteCsv' 'true'
    Set-CfgValue $cfg.Loading 'General' 'Enabled' 'true'
    Set-CfgValue $cfg.Loading 'Experiment' 'OverrideBackgroundLoadingPriority' 'false'
    Set-CfgValue $cfg.DialogueLoc 'DialogueLocalization' 'Enabled' 'true'
    Set-CfgValue $cfg.DialogueTag 'DialogueTag' 'Enabled' 'false'
    Set-CfgValue $cfg.Lifecycle 'Main16Lifecycle' 'Enabled' 'false'

    for($i=0;$i -lt $plan.Count;$i++) {
        $item=$plan[$i]; $idx=$i+1; $v=$item.Variant; $round=[int]$item.Round
        Set-CfgValue $cfg.RegexCache 'DialogueTagRegexCache' 'Enabled' (BoolText $v.Enabled)

        Write-Host "[$idx/$($plan.Count)] $($v.Name) round=$round"
        Stop-Game
        if(Test-Path $logPath){ Remove-Item $logPath -Force }

        $game=Start-Process $gameExe -WorkingDirectory $GameDir -PassThru
        $ahk=Start-Process $AutoHotkeyExe -ArgumentList @('"'+$ahkScript+'"','run') -PassThru -Wait
        $ahkExit=$ahk.ExitCode

        if(-not $game.HasExited){
            try { $game.WaitForExit(15000) | Out-Null } catch {}
        }
        if(-not $game.HasExited){ Stop-Game }

        Start-Sleep -Milliseconds 500
        $text=if(Test-Path $logPath){Get-Content -Raw $logPath}else{''}
        $a=Get-LogAnalysis $text $v.Enabled
        $pass=($ahkExit -eq 0) -and $a.UiFlowOk -and ($a.RuntimeErrorCount -eq 0) -and $a.CacheSummaryOk

        $stamp=Get-Date -Format 'yyyyMMdd-HHmmss-fff'
        $status=if($pass){'pass'}else{'fail'}
        if(Test-Path $logPath){
            Copy-Item $logPath (Join-Path $logArchiveDir ("{0}-r{1:D2}-{2:D3}of{3:D3}-{4}-{5}.log" -f $v.Name,$round,$idx,$plan.Count,$status,$stamp)) -Force
        }

        $row=[pscustomobject]@{
            Timestamp=(Get-Date).ToString('o'); Variant=$v.Name; Round=$round; PlanIndex=$idx; Order=$VariantOrder
            AhkExitCode=$ahkExit; Pass=$pass; UiFlowOk=$a.UiFlowOk; RuntimeErrorCount=$a.RuntimeErrorCount
            CacheSummaryOk=$a.CacheSummaryOk; CachePatterns=$a.CachePatterns; CacheHits=$a.CacheHits; CacheMisses=$a.CacheMisses
        }
        if(Test-Path $csvPath){$row|Export-Csv $csvPath -Append -NoTypeInformation}else{$row|Export-Csv $csvPath -NoTypeInformation}

        Write-Host ("  {0} AHK={1} UI={2} errors={3} cache={4} hits={5} misses={6}" -f $status.ToUpper(),$ahkExit,$a.UiFlowOk,$a.RuntimeErrorCount,$a.CacheSummaryOk,$a.CacheHits,$a.CacheMisses)
        if($idx -lt $plan.Count){Start-Sleep -Seconds $CooldownSeconds}
    }

    Write-Host ''
    Write-Host '=== Dialogue regression summary ==='
    $rows=@(Import-Csv $csvPath | Select-Object -Last $plan.Count)
    $rows | Group-Object Variant | ForEach-Object {
        $g=@($_.Group)
        [pscustomobject]@{
            Variant=$_.Name
            Runs=$g.Count
            Passed=@($g|Where-Object Pass -eq 'True').Count
            Failed=@($g|Where-Object Pass -ne 'True').Count
        }
    } | Format-Table -AutoSize
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
