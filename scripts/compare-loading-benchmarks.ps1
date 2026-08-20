param(
    [Parameter(Position = 0)]
    [string]$Path = ".",

    [string]$Csv
)

$ErrorActionPreference = "Stop"

function Get-Median {
    param([double[]]$Values)

    if (-not $Values -or $Values.Count -eq 0) {
        return [double]::NaN
    }

    $sorted = $Values | Sort-Object
    $count = $sorted.Count
    if (($count % 2) -eq 1) {
        return [double]$sorted[[int][math]::Floor($count / 2)]
    }

    $upper = [int]($count / 2)
    return ([double]$sorted[$upper - 1] + [double]$sorted[$upper]) / 2.0
}

function Parse-BenchmarkLine {
    param(
        [string]$Line,
        [string]$FileName
    )

    if ($Line -notmatch '\[LOAD BENCHMARK\]\s+(?<data>.+)$') {
        return $null
    }

    $values = @{}
    foreach ($match in [regex]::Matches($Matches.data, '(?<key>[A-Za-z0-9]+)=(?<value>[^\s]+)')) {
        $values[$match.Groups['key'].Value] = $match.Groups['value'].Value
    }

    if (-not $values.ContainsKey('priority')) {
        return $null
    }

    function Num([string]$key) {
        if (-not $values.ContainsKey($key)) { return [double]::NaN }
        $raw = $values[$key] -replace 'ms$','' -replace 's$',''
        $number = 0.0
        if ([double]::TryParse($raw, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$number)) {
            return $number
        }
        return [double]::NaN
    }

    [pscustomobject]@{
        File                     = $FileName
        Priority                 = $values['priority']
        NaturalPriority          = $values['naturalPriority']
        ButtonToRequest_s        = Num 'buttonToRequest'
        RequestTo90_s            = Num 'requestTo90'
        Progress90ToSceneLoaded_s= Num 'progress90ToSceneLoaded'
        RequestToSceneLoaded_s   = Num 'requestToSceneLoaded'
        LargestPreLoadFrame_ms   = Num 'largestPreLoadFrame'
        PostLoadLargest_ms       = Num 'postLoadLargest'
        PostLoadSecond_ms        = Num 'postLoadSecond'
        PostLoadWindow_ms        = Num 'postLoadWindow'
        TotalToPostWindowEnd_s   = Num 'totalButtonToPostWindowEnd'
        PostLoadSamples          = Num 'postLoadSamples'
    }
}

$files = if (Test-Path -LiteralPath $Path -PathType Leaf) {
    Get-Item -LiteralPath $Path
}
else {
    Get-ChildItem -LiteralPath $Path -File -Filter '*.log' | Sort-Object Name
}

$rows = foreach ($file in $files) {
    foreach ($line in Get-Content -LiteralPath $file.FullName) {
        $parsed = Parse-BenchmarkLine -Line $line -FileName $file.Name
        if ($null -ne $parsed) { $parsed }
    }
}

if (-not $rows) {
    Write-Warning "No [LOAD BENCHMARK] lines found in '$Path'."
    exit 1
}

Write-Host ""
Write-Host "Individual runs" -ForegroundColor Cyan
$rows |
    Sort-Object Priority, File |
    Format-Table File, Priority, NaturalPriority, RequestTo90_s, LargestPreLoadFrame_ms, RequestToSceneLoaded_s, PostLoadLargest_ms, PostLoadSecond_ms, TotalToPostWindowEnd_s -AutoSize

$summary = foreach ($group in ($rows | Group-Object Priority | Sort-Object Name)) {
    $g = @($group.Group)
    [pscustomobject]@{
        Priority                    = $group.Name
        Runs                        = $g.Count
        MedianRequestTo90_s         = Get-Median @($g.RequestTo90_s)
        MedianLargestPreLoadFrame_ms= Get-Median @($g.LargestPreLoadFrame_ms)
        MedianRequestToSceneLoaded_s= Get-Median @($g.RequestToSceneLoaded_s)
        MedianPostLoadLargest_ms    = Get-Median @($g.PostLoadLargest_ms)
        MedianPostLoadSecond_ms     = Get-Median @($g.PostLoadSecond_ms)
        MedianTotalToPostWindowEnd_s= Get-Median @($g.TotalToPostWindowEnd_s)
    }
}

Write-Host ""
Write-Host "Median by priority" -ForegroundColor Cyan
$summary | Format-Table -AutoSize

if ($Csv) {
    $rows | Export-Csv -LiteralPath $Csv -NoTypeInformation -Encoding UTF8
    Write-Host ""
    Write-Host "CSV written to: $Csv"
}
