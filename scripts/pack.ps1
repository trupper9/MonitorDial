param(
    [string]$Version,
    [switch]$Upload,
    [switch]$SkipBuild
)

# Publishes MonitorDial self-contained and packs it with Velopack.
# Output: artifacts\release (Setup.exe, the update package and the release feed).
#   pwsh scripts\pack.ps1            build and pack
#   pwsh scripts\pack.ps1 -Upload    also create the GitHub Release (needs gh signed in)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root "MonitorDial.csproj"
$publish = Join-Path $root "artifacts\publish"
$release = Join-Path $root "artifacts\release"
$icon = Join-Path $root "Assets\MonitorDial.ico"
$repo = "trupper9/MonitorDial"

if (-not $Version) {
    [xml]$proj = Get-Content $csproj
    $Version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw "Pass -Version or set <Version> in the csproj." }

if (-not $SkipBuild) {
    Remove-Item $publish, $release -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $publish, $release | Out-Null

    dotnet publish $csproj -c Release -r win-x64 -p:Platform=x64 -p:Version=$Version -o $publish --self-contained true
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

    $dotnetTools = Join-Path $env:USERPROFILE ".dotnet\tools"
    if (Test-Path $dotnetTools) { $env:PATH = "$dotnetTools;$env:PATH" }
    if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
        throw "Install the Velopack CLI: dotnet tool install -g vpk --version 1.2.0"
    }

    & vpk pack `
        --packId MonitorDial `
        --packVersion $Version `
        --packDir $publish `
        --mainExe MonitorDial.exe `
        --packTitle MonitorDial `
        --packAuthors "Luan Barbosa" `
        --icon $icon `
        --outputDir $release
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }
}

if (-not $Upload) {
    Write-Host "Package at $release"
    return
}

# The update client reads the feed and the .nupkg from the release assets,
# so those are uploaded along with the installer. The portable zip is left out.
$assets = Get-ChildItem $release -File | Where-Object { $_.Name -notlike "*Portable.zip" } | ForEach-Object { $_.FullName }
if (-not $assets) { throw "Nothing to upload in $release." }

& gh release create "v$Version" @assets --repo $repo --title "MonitorDial $Version" --generate-notes
if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }
Write-Host "Release v$Version uploaded."
