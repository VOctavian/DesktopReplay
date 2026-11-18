<#
.SYNOPSIS
  Builds DesktopReplay and packs a Velopack installer (Setup.exe) + update packages.

.EXAMPLE
  ./tools/release.ps1 -Version 0.1.0                 # local build into ./Releases
  ./tools/release.ps1 -Version 0.2.0 -Upload -Token <github token>   # also publish a GitHub release
#>
param(
    [Parameter(Mandatory)] [string]$Version,
    [switch]$Upload,
    [string]$Token = $env:GITHUB_TOKEN,
    [string]$RepoUrl = "https://github.com/VOctavian/DesktopReplay"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$publish = Join-Path $root "publish"
$releases = Join-Path $root "Releases"
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue

dotnet tool restore
dotnet publish src/DesktopReplay/DesktopReplay.csproj -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=none -o $publish
if ($LASTEXITCODE) { throw "publish failed" }

# Pull previous releases so vpk can build delta updates.
if ($Upload) {
    dotnet vpk download github --repoUrl $RepoUrl --token $Token -o $releases
}

dotnet vpk pack --runtime win-x64 --packId DesktopReplay --packVersion $Version --packDir $publish --mainExe DesktopReplay.exe `
    --packTitle "DesktopReplay" --packAuthors "DesktopReplay" --icon src/DesktopReplay/Assets/app.ico `
    --outputDir $releases
if ($LASTEXITCODE) { throw "vpk pack failed" }

if ($Upload) {
    dotnet vpk upload github --repoUrl $RepoUrl --token $Token -o $releases --publish --releaseName "DesktopReplay $Version" --tag "v$Version"
    if ($LASTEXITCODE) { throw "upload failed" }
}
Write-Host "Done: $releases"
