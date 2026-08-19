param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
dotnet build "src\MrPrepperTooltipScaleFix\MrPrepperTooltipScaleFix.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

[pscustomobject]@{
    PluginDir = (Resolve-Path "dist\AcTePuKc Mr Prepper Tooltip Scale Fix").Path
    Dll = (Resolve-Path "dist\AcTePuKc Mr Prepper Tooltip Scale Fix\MrPrepperTooltipScaleFix.dll").Path
}
