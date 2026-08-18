param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
dotnet build "src\MrPrepperTranslationMod\MrPrepperTranslationMod.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

[pscustomobject]@{
    PluginDir = (Resolve-Path "dist\AcTePuKc Mr Prepper Bulgarian Translation").Path
    Dll = (Resolve-Path "dist\AcTePuKc Mr Prepper Bulgarian Translation\MrPrepperTranslationMod.dll").Path
}
