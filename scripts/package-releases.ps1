param(
    [string]$BgVersion = "0.1.1",
    [string]$TooltipVersion = "0.1.0",
    [string]$CyrillicVersion = "0.1.0",
    [string]$SkipVersion = "0.1.0",
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
    & (Join-Path $repo "scripts\build-font-fix.ps1") | Out-Host
    dotnet build (Join-Path $repo "src\MrPrepperSkipStartupVideo\MrPrepperSkipStartupVideo.csproj") --configuration Release | Out-Host
}

$bgDll = Join-Path $repo "dist\AcTePuKc Mr Prepper Bulgarian Translation\MrPrepperTranslationMod.dll"
$tooltipDll = Join-Path $repo "dist\MrPrepperTooltipScaleFix\MrPrepperTooltipScaleFix.dll"
$labels = Join-Path $repo "src\MrPrepperTranslationMod\translations\labels.txt"
$changelog = Join-Path $repo "src\MrPrepperTranslationMod\translations\changelog.txt"
$imageDir = Join-Path $repo "src\MrPrepperTranslationMod\images"
$tooltipCfg = Join-Path $repo "src\MrPrepperTooltipScaleFix\config\actepukc.mrprepper.tooltipscalefix.cfg"
$cyrDll = Join-Path $repo "dist\AcTePuKc Cyrillic Font Fix\CyrillicFontFix.dll"
$cyrCfg = Join-Path $repo "src\CyrillicFontFix\config\actepukc.mrprepper.cyrillicfontfix.cfg"
$skipDll = Join-Path $repo "dist\MrPrepperSkipStartupVideo\MrPrepperSkipStartupVideo.dll"

$required = @($bgDll, $tooltipDll, $labels, $changelog, $tooltipCfg, $cyrDll, $cyrCfg, $skipDll)
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
$bgImages = Join-Path $bgPlugin "images"
New-Item -ItemType Directory -Path $bgImages -Force | Out-Null
Get-ChildItem -LiteralPath $imageDir -Filter "*.png" -File | Copy-Item -Destination $bgImages

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

Write-Host "Created release packages:"
Write-Host "  $bgZip"
Write-Host "  $tooltipZip"
Write-Host ""
Write-Host "Archive contents:"
Write-Host "--- Bulgarian Translation ---"
tar -tf $bgZip
Write-Host "--- Tooltip Scale Fix ---"
tar -tf $tooltipZip

# Cyrillic Font Fix package
$cyrStage = Join-Path $stageRoot "cyrillic"
$cyrConfigDir = Join-Path $cyrStage "BepInEx\config"
$cyrPluginDir = Join-Path $cyrStage "BepInEx\plugins\AcTePuKc Cyrillic Font Fix"
New-Item -ItemType Directory -Path $cyrConfigDir -Force | Out-Null
New-Item -ItemType Directory -Path $cyrPluginDir -Force | Out-Null
Copy-Item -LiteralPath $cyrCfg -Destination (Join-Path $cyrConfigDir "actepukc.mrprepper.cyrillicfontfix.cfg")
Copy-Item -LiteralPath $cyrDll -Destination (Join-Path $cyrPluginDir "CyrillicFontFix.dll")
$cyrZip = Join-Path $outRoot "MrPrepper-Cyrillic-Font-Fix-$CyrillicVersion.zip"
if (Test-Path -LiteralPath $cyrZip) { Remove-Item -LiteralPath $cyrZip -Force }
Compress-Archive -Path (Join-Path $cyrStage "BepInEx") -DestinationPath $cyrZip -CompressionLevel Optimal

Write-Host "--- Cyrillic Font Fix ---"
tar -tf $cyrZip

# Skip Intro package
$skipStage = Join-Path $stageRoot "skip"
$skipPluginDir = Join-Path $skipStage "BepInEx\plugins\MrPrepperSkipStartupVideo"
New-Item -ItemType Directory -Path $skipPluginDir -Force | Out-Null
Copy-Item -LiteralPath $skipDll -Destination (Join-Path $skipPluginDir "MrPrepperSkipStartupVideo.dll")
$skipZip = Join-Path $outRoot "MrPrepper-Skip-Intro-$SkipVersion.zip"
if (Test-Path -LiteralPath $skipZip) { Remove-Item -LiteralPath $skipZip -Force }
Compress-Archive -Path (Join-Path $skipStage "BepInEx") -DestinationPath $skipZip -CompressionLevel Optimal

Write-Host "--- Skip Intro ---"
tar -tf $skipZip

Remove-Item -LiteralPath $stageRoot -Recurse -Force

[pscustomobject]@{
    BulgarianTranslation = $bgZip
    TooltipScaleFix = $tooltipZip
    CyrillicFontFix = $cyrZip
    SkipIntro = $skipZip
}
