<#
.SYNOPSIS
    Build VSaver and package a first-time-install zip (VSaver.exe + credentials.json + SETUP).

.DESCRIPTION
    Produces two things with very different audiences:

      1. dist\VSaver-v<version>.zip  -- the PRIVATE first-time-install bundle
         (VSaver.exe + credentials.json + settings.json + SETUP.txt). It contains real
         secrets (the OAuth credentials.json and the shared Drive folder id), so it is for
         hand-delivery to your group only -- Discord/email/USB. NEVER upload it anywhere
         public. This script deliberately does NOT attach it to the GitHub Release.

      2. The bare VSaver.exe -- safe to publish. With -Publish it is the ONLY asset uploaded
         to the GitHub Release (named exactly VSaver.exe, which the auto-updater looks for).
         It carries no credentials.json.

    The version comes from <Version> in the .csproj; the release tag is v<version>.

.EXAMPLE
    pwsh scripts\release.ps1
        Build the exe + the private install zip. No GitHub release (dry run).

.EXAMPLE
    pwsh scripts\release.ps1 -Publish -Notes "Fix credentials.json lookup"
        Build, then publish ONLY the bare VSaver.exe to the GitHub Release. The install zip
        stays local in dist\ for you to hand to friends privately.
#>
[CmdletBinding()]
param(
    # Create the GitHub Release and upload assets (requires the `gh` CLI, authenticated).
    [switch]$Publish,

    # Release notes body. Ignored unless -Publish is set.
    [string]$Notes = "",

    # The shared Drive folder id to bake into the PRIVATE install zip's settings.json.
    # Never stored in tracked source. If omitted, the script reads DriveFolderId from a
    # private (gitignored) src\ValheimSync.App\settings.json; if that's missing too, the zip
    # ships a blank folder id and friends paste it in the app on first run.
    [string]$DriveFolderId = ""
)

$ErrorActionPreference = "Stop"

$repoRoot   = Split-Path $PSScriptRoot -Parent
$appProj    = Join-Path $repoRoot "src\ValheimSync.App\ValheimSync.App.csproj"
$publishDir = Join-Path $repoRoot "src\ValheimSync.App\bin\Release\net8.0\win-x64\publish"
$distDir    = Join-Path $repoRoot "dist"
$setupDoc   = Join-Path $repoRoot "dist\SETUP.md"
$settingsSeed = Join-Path $repoRoot "src\ValheimSync.App\settings.json.example"

# --- Read <Version> from the app csproj ------------------------------------------------
[xml]$csproj = Get-Content $appProj
$version = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $appProj." }
$tag = "v$version"
Write-Host "Building VSaver $tag" -ForegroundColor Cyan

# --- Publish the single-file exe -------------------------------------------------------
& dotnet publish $appProj -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$exe = Join-Path $publishDir "VSaver.exe"
if (-not (Test-Path $exe)) { throw "Expected build output not found: $exe" }

# --- Locate a real credentials.json to bundle ------------------------------------------
# The csproj copies src\ValheimSync.App\credentials.json into the publish folder when it
# exists, so prefer that; fall back to the source copy.
$creds = Join-Path $publishDir "credentials.json"
if (-not (Test-Path $creds)) { $creds = Join-Path $repoRoot "src\ValheimSync.App\credentials.json" }
if (-not (Test-Path $creds)) {
    throw "credentials.json not found. Put your real one in src\ValheimSync.App\credentials.json (see README Part 1) so it can be bundled into the first-time-install zip."
}
if (Select-String -Path $creds -Pattern "YOUR_CLIENT_ID" -SimpleMatch -Quiet) {
    throw "credentials.json is still the placeholder (contains YOUR_CLIENT_ID). Replace it with the real download from Google Cloud Console before releasing."
}

# --- Stage + zip -----------------------------------------------------------------------
if (-not (Test-Path $distDir)) { New-Item -ItemType Directory -Path $distDir | Out-Null }
$staging = Join-Path $distDir ".stage"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging | Out-Null

Copy-Item $exe   (Join-Path $staging "VSaver.exe")
Copy-Item $creds (Join-Path $staging "credentials.json")
# Ship a ready-made settings.json (the shared Drive folder id is already filled in) so a
# fresh install syncs the right folder without anyone editing anything. The app rewrites
# this file with per-user fields (name, selected worlds) on first run.
if (Test-Path $settingsSeed) {
    # Resolve the real folder id from a PRIVATE source only (param, else a gitignored dev
    # settings.json). It must never come from tracked files.
    $seedFolderId = $DriveFolderId
    $privateSettings = Join-Path $repoRoot "src\ValheimSync.App\settings.json"
    if (-not $seedFolderId -and (Test-Path $privateSettings)) {
        try { $seedFolderId = (Get-Content $privateSettings -Raw | ConvertFrom-Json).DriveFolderId } catch {}
    }

    $settings = Get-Content $settingsSeed -Raw | ConvertFrom-Json
    if ($seedFolderId) {
        $settings.DriveFolderId = $seedFolderId
        Write-Host "Seeded the zip's settings.json with the shared Drive folder id." -ForegroundColor Green
    } else {
        Write-Warning "No Drive folder id supplied (-DriveFolderId or a private settings.json) - the zip ships a blank folder id; friends paste it in the app on first run."
    }
    ($settings | ConvertTo-Json) | Set-Content (Join-Path $staging "settings.json") -Encoding UTF8
} else {
    Write-Warning "settings.json.example not found - the zip will omit a seeded settings.json."
}
if (Test-Path $setupDoc) {
    # Ship the setup guide as a .txt so a double-click opens it in Notepad on any PC.
    Copy-Item $setupDoc (Join-Path $staging "SETUP.txt")
} else {
    Write-Warning "dist\SETUP.md not found - the zip will omit the setup guide."
}

$zip = Join-Path $distDir "VSaver-$tag.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $staging "*") -DestinationPath $zip
Remove-Item $staging -Recurse -Force
Write-Host "Packaged $zip" -ForegroundColor Green
Write-Host "  ^ PRIVATE: contains real credentials.json + Drive folder id. Hand-deliver only; never upload to a public release." -ForegroundColor Yellow

# --- Optionally cut the GitHub Release -------------------------------------------------
# SECURITY: only the bare VSaver.exe (no credentials) is ever uploaded. The install zip is
# deliberately NOT attached here so real secrets can never reach a public release.
if ($Publish) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "The GitHub CLI (gh) is not installed or not on PATH - cannot -Publish."
    }
    Write-Host "Creating GitHub Release $tag (bare VSaver.exe only)..." -ForegroundColor Cyan
    & gh release create $tag $exe --title $tag --notes $Notes
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed." }
    Write-Host "Released $tag. The install zip was NOT uploaded - share it privately." -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "Dry run complete. To publish (uploads ONLY the bare exe), run:" -ForegroundColor Yellow
    Write-Host "  gh release create $tag `"$exe`" --title `"$tag`" --notes `"...`"" -ForegroundColor Yellow
}
