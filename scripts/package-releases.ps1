param(
    [string]$BgVersion = "0.1.0",
    [string]$TooltipVersion = "0.1.0",
    [switch]$Build
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repo = (Resolve-Path ".").Path
$outRoot = Join-Path $repo "release-packages"
$stageRoot = Join-Path $outRoot ".stage"

if ($Build) {
    & (Join-Path $repo "scripts\build-plugin.ps1") | Out-Host
    & (Join-Path $repo "scripts\build-tooltip-scale-fix.ps1") | Out-Host
}

$bgDll = Join-Path $repo "dist\AcTePuKc Mr Prepper Bulgarian Translation\MrPrepperTranslationMod.dll"
$tooltipDll = Join-Path $repo "dist\MrPrepperTooltipScaleFix\MrPrepperTooltipScaleFix.dll"
$labels = Join-Path $repo "src\MrPrepperTranslationMod\translations\labels.txt"
$changelog = Join-Path $repo "src\MrPrepperTranslationMod\translations\changelog.txt"
$tooltipCfg = Join-Path $repo "src\MrPrepperTooltipScaleFix\config\actepukc.mrprepper.tooltipscalefix.cfg"

$required = @($bgDll, $tooltipDll, $labels, $changelog, $tooltipCfg)
foreach ($file in $required) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Required release file is missing: $file`nRun with -Build first if the DLLs have not been built yet."
    }
}

if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
New-Item -ItemType Directory -Path $outRoot -Force | Out-Null

# Bulgarian Translation package
$bgStage = Join-Path $stageRoot "bg"
$bgPlugin = Join-Path $bgStage "BepInEx\plugins\AcTePuKc Mr Prepper Bulgarian Translation"
$bgTranslations = Join-Path $bgPlugin "translations"
New-Item -ItemType Directory -Path $bgTranslations -Force | Out-Null
Copy-Item -LiteralPath $bgDll -Destination (Join-Path $bgPlugin "MrPrepperTranslationMod.dll")
Copy-Item -LiteralPath $labels -Destination (Join-Path $bgTranslations "labels.txt")
Copy-Item -LiteralPath $changelog -Destination (Join-Path $bgTranslations "changelog.txt")

$bgZip = Join-Path $outRoot "MrPrepper-Bulgarian-Translation-$BgVersion.zip"
if (Test-Path -LiteralPath $bgZip) { Remove-Item -LiteralPath $bgZip -Force }
Compress-Archive -Path (Join-Path $bgStage "BepInEx") -DestinationPath $bgZip -CompressionLevel Optimal

# Tooltip Scale Fix package
$tooltipStage = Join-Path $stageRoot "tooltip"
$tooltipConfigDir = Join-Path $tooltipStage "BepInEx\config"
$tooltipPluginDir = Join-Path $tooltipStage "BepInEx\plugins\MrPrepperTooltipScaleFix"
New-Item -ItemType Directory -Path $tooltipConfigDir -Force | Out-Null
New-Item -ItemType Directory -Path $tooltipPluginDir -Force | Out-Null
Copy-Item -LiteralPath $tooltipCfg -Destination (Join-Path $tooltipConfigDir "actepukc.mrprepper.tooltipscalefix.cfg")
Copy-Item -LiteralPath $tooltipDll -Destination (Join-Path $tooltipPluginDir "MrPrepperTooltipScaleFix.dll")

$tooltipZip = Join-Path $outRoot "MrPrepperTooltipScaleFix-$TooltipVersion.zip"
if (Test-Path -LiteralPath $tooltipZip) { Remove-Item -LiteralPath $tooltipZip -Force }
Compress-Archive -Path (Join-Path $tooltipStage "BepInEx") -DestinationPath $tooltipZip -CompressionLevel Optimal

Remove-Item -LiteralPath $stageRoot -Recurse -Force

Write-Host "Created release packages:"
Write-Host "  $bgZip"
Write-Host "  $tooltipZip"
Write-Host ""
Write-Host "Archive contents:"
Write-Host "--- Bulgarian Translation ---"
tar -tf $bgZip
Write-Host "--- Tooltip Scale Fix ---"
tar -tf $tooltipZip

[pscustomobject]@{
    BulgarianTranslation = $bgZip
    TooltipScaleFix = $tooltipZip
}
