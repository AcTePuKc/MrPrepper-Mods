param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
dotnet build "src\CyrillicFontFix\CyrillicFontFix.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

[pscustomobject]@{
    PluginDir = (Resolve-Path "dist\AcTePuKc Cyrillic Font Fix").Path
    Dll = (Resolve-Path "dist\AcTePuKc Cyrillic Font Fix\CyrillicFontFix.dll").Path
}
