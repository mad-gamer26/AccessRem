<#
.SYNOPSIS
    Builds AccessRem into dist\AccessRem: the app, the NVDA backend add-on and a bundled portable NVDA.

.PARAMETER NvdaVersion
    NVDA release to download and bundle (default 2026.2). Ignored when -NvdaSource is given.

.PARAMETER NvdaSource
    Use an existing NVDA program folder (for example C:\Program Files\NVDA) instead of downloading.
    Only program files are copied; no configuration or add-ons.

.PARAMETER SkipNvda
    Build only the app and add-on (keeps any NVDA already in dist).

.PARAMETER Zip
    Also produce dist\AccessRem-<version>-windows-x64.zip and its release manifest, dist\latest.json.
#>
[CmdletBinding()]
param(
    [string]$NvdaVersion = "2026.2",
    [string]$NvdaSource,
    [switch]$SkipNvda,
    [switch]$Zip,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist\AccessRem"
$work = Join-Path $root "build\out"
New-Item -ItemType Directory -Force -Path $dist, $work | Out-Null

Write-Host "Publishing the AccessRem app..."
dotnet publish (Join-Path $root "src\AccessRem\AccessRem.csproj") -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $dist --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "Copying the NVDA backend add-on..."
$addonTarget = Join-Path $dist "backend-addon"
if (Test-Path $addonTarget) { Remove-Item -Recurse -Force $addonTarget }
Copy-Item -Recurse (Join-Path $root "addon") $addonTarget
Get-ChildItem $addonTarget -Recurse -Directory -Filter "__pycache__" | Remove-Item -Recurse -Force

if (-not $SkipNvda) {
    $nvdaTarget = Join-Path $dist "nvda"
    if (Test-Path $nvdaTarget) { Remove-Item -Recurse -Force $nvdaTarget }
    if ($NvdaSource) {
        Write-Host "Copying NVDA program files from $NvdaSource..."
        if (-not (Test-Path (Join-Path $NvdaSource "nvda_noUIAccess.exe"))) { throw "$NvdaSource does not look like an NVDA program folder." }
        # Program files only: never the installation's configuration, add-ons or uninstaller.
        robocopy $NvdaSource $nvdaTarget /E /NFL /NDL /NJH /NJS /NP /XD systemConfig userConfig /XF uninstall.exe | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Copying NVDA failed (robocopy exit $LASTEXITCODE)" }
    }
    else {
        $installer = Join-Path $work "nvda_$NvdaVersion.exe"
        if (-not (Test-Path $installer)) {
            $url = "https://download.nvaccess.org/releases/$NvdaVersion/nvda_$NvdaVersion.exe"
            Write-Host "Downloading $url..."
            Invoke-WebRequest -Uri $url -OutFile $installer -UseBasicParsing
        }
        $sevenZip = @(
            (Get-Command 7z -ErrorAction SilentlyContinue).Source,
            "$env:ProgramFiles\7-Zip\7z.exe",
            "$env:USERPROFILE\scoop\apps\7zip\current\7z.exe"
        ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
        if (-not $sevenZip) { throw "7-Zip is needed to unpack the NVDA launcher. Install it, or pass -NvdaSource." }
        # The NVDA launcher is a self-extracting archive containing the complete program folder.
        $extract = Join-Path $work "nvda_$NvdaVersion"
        if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
        & $sevenZip x $installer "-o$extract" -y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Unpacking the NVDA launcher failed" }
        $programDir = Get-ChildItem $extract -Recurse -Filter "nvda_noUIAccess.exe" | Select-Object -First 1
        if (-not $programDir) { throw "nvda_noUIAccess.exe was not found in the NVDA launcher." }
        robocopy $programDir.DirectoryName $nvdaTarget /E /NFL /NDL /NJH /NJS /NP /XD '$PLUGINSDIR' /XF uninstall.exe | Out-Null
        if ($LASTEXITCODE -ge 8) { throw "Copying NVDA failed (robocopy exit $LASTEXITCODE)" }
    }
    $nvdaExe = Join-Path $nvdaTarget "nvda_noUIAccess.exe"
    $version = (Get-Item $nvdaExe).VersionInfo.ProductVersion
    Write-Host "Bundled NVDA $version."
}

Copy-Item (Join-Path $root "README.md") $dist -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root "LICENSE.txt") $dist -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.txt") $dist -Force -ErrorAction SilentlyContinue

if ($Zip) {
    # ProductVersion carries the commit ("1.0.0+6365449..."); release names use the plain version.
    $appVersion = ((Get-Item (Join-Path $dist "AccessRem.exe")).VersionInfo.ProductVersion -split '\+')[0]
    $zipName = "AccessRem-$appVersion-windows-x64.zip"
    $zipPath = Join-Path $root "dist\$zipName"
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    Compress-Archive -Path $dist -DestinationPath $zipPath
    Write-Host "Created $zipPath"

    # The release manifest that install.ps1 (irm https://accessrem.mad-gamer.com | iex) checks the download against.
    $manifest = [ordered]@{
        product = "accessrem-windows-x64"
        version = $appVersion
        file    = $zipName
        size    = (Get-Item $zipPath).Length
        sha256  = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $manifestPath = Join-Path $root "dist\latest.json"
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json))
    Write-Host "Created $manifestPath"
}

Write-Host "Done: $dist"
exit 0
