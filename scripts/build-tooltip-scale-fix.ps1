param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
dotnet build "src\MrPrepperTooltipScaleFix\MrPrepperTooltipScaleFix.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

[pscustomobject]@{
    PluginDir = (Resolve-Path "dist\MrPrepperTooltipScaleFix").Path
    Dll = (Resolve-Path "dist\MrPrepperTooltipScaleFix\MrPrepperTooltipScaleFix.dll").Path
}
