# AccessRem installer for Windows.
#
#   irm https://accessrem.mad-gamer.com | iex
#
# Installs the latest AccessRem from the project's GitHub releases, or
# updates or uninstalls an existing copy, then starts it. Asks a few
# questions; pressing Enter takes the answer in brackets. No administrator
# rights: everything goes in the current user's folders and registry. The
# one exception is a copy installed for all users (Settings, Getting help,
# in AccessRem): this offers to update that copy too, and Windows asks for
# permission first. Served by nginx on mad-gamer.com from
# /var/www/accessrem/install.ps1 (see accessrem.mad-gamer.com.conf and
# Deploy-Site.ps1 next to this file).
#
# The download is checked against the size and SHA-256 named in the
# release's manifest (latest.json), fetched over HTTPS from GitHub.
#
# Output is plain lines for screen readers: no colours, progress bars or
# cursor movement. Plain ASCII, for Windows PowerShell 5.1.

& {
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
# Windows PowerShell's download progress bar is slow and visual only.
$ProgressPreference = 'SilentlyContinue'

$Release = 'https://github.com/mad-gamer26/AccessRem/releases/latest/download'
$Product = 'accessrem-windows-x64'
$AppExe = 'AccessRem.exe'
# Everything a release puts in the program folder. AccessRem keeps saved
# computers and settings in %APPDATA%\AccessRem, never here.
$PackageItems = @($AppExe, 'nvda', 'backend-addon', 'README.md', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.txt')
# AccessRem and the copy of NVDA it bundles.
$ProcessNames = @('AccessRem', 'nvda', 'nvda_noUIAccess', 'nvda_uiAccess', 'nvda_slave')
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$LinkKey = 'HKCU:\Software\Classes\nvdaremote'
$SystemKey = 'HKLM:\SOFTWARE\AccessRem'
$StartMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\AccessRem.lnk'
$Desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'AccessRem.lnk'
$DefaultFolder = Join-Path $env:LOCALAPPDATA 'Programs\AccessRem'
$DataFolder = Join-Path $env:APPDATA 'AccessRem'

function Say([string]$Text = '') { Write-Host $Text }

# A yes or no question; Enter takes the default.
function Ask-YesNo([string]$Question, [bool]$Default) {
    $hint = if ($Default) { 'Y/n' } else { 'y/N' }
    while ($true) {
        $answer = (Read-Host "$Question ($hint)").Trim().ToLowerInvariant()
        if ($answer -eq '') { return $Default }
        if ($answer -in @('y', 'yes')) { return $true }
        if ($answer -in @('n', 'no')) { return $false }
        Say 'Please answer y for yes or n for no.'
    }
}

# A numbered choice; Enter takes choice 1. Returns the number.
function Ask-Choice([string]$Question, [string[]]$Choices) {
    Say $Question
    for ($i = 0; $i -lt $Choices.Count; $i++) {
        Say "$($i + 1). $($Choices[$i])"
    }
    while ($true) {
        $answer = (Read-Host "Choice, 1 to $($Choices.Count) (1)").Trim()
        if ($answer -eq '') { return 1 }
        $n = 0
        if ([int]::TryParse($answer, [ref]$n) -and $n -ge 1 -and $n -le $Choices.Count) { return $n }
        Say "Please type a number from 1 to $($Choices.Count)."
    }
}

function Ask-Folder([string]$Default) {
    $answer = (Read-Host "Install folder ($Default)").Trim().Trim('"')
    if ($answer -eq '') { return $Default }
    return [Environment]::ExpandEnvironmentVariables($answer)
}

function Same-Folder([string]$A, [string]$B) {
    return $A -and $B -and ($A.TrimEnd('\') -ieq $B.TrimEnd('\'))
}

# The installed version, from the program's version information
# ("1.0.0+<commit>"), or $null when it cannot be read.
function Installed-Version([string]$Folder) {
    $exe = Join-Path $Folder $AppExe
    if (-not (Test-Path -LiteralPath $exe)) { return $null }
    try {
        if ((Get-Item -LiteralPath $exe).VersionInfo.ProductVersion -match '^(\d+\.\d+\.\d+)') { return $Matches[1] }
    } catch { }
    return $null
}

function Version-Text([string]$Folder) {
    $version = Installed-Version $Folder
    if ($version) { return "AccessRem $version" }
    return 'AccessRem'
}

function Is-Current([string]$Folder, [string]$Latest) {
    $version = Installed-Version $Folder
    return $version -and ([version]$version -ge [version]$Latest)
}

# The copy installed for all users (in Program Files), or $null.
function System-Folder {
    $value = Get-ItemProperty -LiteralPath $SystemKey -Name 'InstallDir' -ErrorAction SilentlyContinue
    if ($null -eq $value) { return $null }
    if (Test-Path -LiteralPath (Join-Path $value.InstallDir $AppExe)) { return $value.InstallDir.TrimEnd('\') }
    return $null
}

# The folder of the program the Run value starts, or $null.
function Run-Folder {
    $value = Get-ItemProperty -LiteralPath $RunKey -Name 'AccessRem' -ErrorAction SilentlyContinue
    if ($null -eq $value) { return $null }
    if ($value.AccessRem -match '^"([^"]+)"') { return Split-Path -Parent $Matches[1] }
    return $null
}

function Shortcut-Folder([string]$Link) {
    if (-not (Test-Path -LiteralPath $Link)) { return $null }
    try {
        $target = (New-Object -ComObject WScript.Shell).CreateShortcut($Link).TargetPath
        if ($target) { return Split-Path -Parent $target }
    } catch { }
    return $null
}

# The processes of AccessRem, or of its NVDA, running from $Folder.
function Running-In([string]$Folder) {
    $prefix = $Folder.TrimEnd('\') + '\'
    return @(Get-Process -Name $ProcessNames -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    })
}

# Where this user's copy of AccessRem is installed already: the running copy,
# the Start menu shortcut, the start-at-sign-in entry, or the default folder.
# The copy installed for all users is handled separately.
function Find-Installed {
    $system = System-Folder
    $candidates = @()
    foreach ($p in @(Get-Process -Name 'AccessRem' -ErrorAction SilentlyContinue)) {
        try { if ($p.Path) { $candidates += Split-Path -Parent $p.Path } } catch { }
    }
    $candidates += @((Shortcut-Folder $StartMenu), (Run-Folder), $DefaultFolder)
    foreach ($c in $candidates) {
        if ($c -and -not (Same-Folder $c $system) -and (Test-Path -LiteralPath (Join-Path $c $AppExe))) {
            return (Resolve-Path -LiteralPath $c).Path
        }
    }
    return $null
}

# Closes the copy running from $Folder, after asking. False: it keeps
# running (the user said no, or it is in a session).
function Close-Running([string]$Folder, [string]$Why) {
    $running = @(Running-In $Folder)
    if ($running.Count -eq 0) { return $true }
    $app = @($running | Where-Object { $_.ProcessName -ieq 'AccessRem' })
    $nvda = @($running | Where-Object { $_.ProcessName -ine 'AccessRem' })
    if ($app.Count -gt 0 -and $nvda.Count -gt 0) {
        # Its NVDA only runs during a session. Closing AccessRem now would end
        # the session, and a screen reader it paused for the session would not
        # be started again.
        Say 'AccessRem is in a remote session. End the session (Disconnect), then run this command again.'
        return $false
    }
    if ($app.Count -gt 0) {
        Say "AccessRem is running. It must close $Why."
        if (-not (Ask-YesNo 'Close AccessRem now?' $true)) { return $false }
    }
    # Without AccessRem, its NVDA is left over from a crash: close it as well.
    $running | Stop-Process -Force
    for ($i = 0; $i -lt 20 -and @(Running-In $Folder).Count -gt 0; $i++) { Start-Sleep -Milliseconds 250 }
    if ($app.Count -gt 0) { Say 'AccessRem closed.' }
    return $true
}

function Make-Shortcut([string]$Link, [string]$Target) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Link) | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $s = $shell.CreateShortcut($Link)
    $s.TargetPath = $Target
    $s.WorkingDirectory = Split-Path -Parent $Target
    $s.Description = 'AccessRem: remote assistance compatible with NVDA Remote Access'
    $s.Save()
}

function Get-Manifest {
    $r = Invoke-WebRequest -UseBasicParsing -Uri "$Release/latest.json"
    $text = if ($r.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($r.Content) } else { [string]$r.Content }
    $m = $text | ConvertFrom-Json
    if ($m.product -ne $Product -or -not ($m.version -match '^\d+\.\d+\.\d+$') -or -not ($m.file -match '^[A-Za-z0-9._-]+\.zip$')) {
        throw 'the release manifest on GitHub is not one for AccessRem for Windows'
    }
    return $m
}

# Downloads and unpacks the release into $Work. Returns the unpacked program
# folder.
function Get-Package($Manifest, [string]$Work) {
    $sizeMb = [math]::Round($Manifest.size / 1MB, 1)
    $zip = Join-Path $Work $Manifest.file
    Say "Downloading AccessRem $($Manifest.version) ($sizeMb MB) from GitHub. It includes its own copy of NVDA, so this can take a few minutes."
    Invoke-WebRequest -UseBasicParsing -Uri "$Release/$($Manifest.file)" -OutFile $zip
    $size = (Get-Item -LiteralPath $zip).Length
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($size -ne [int64]$Manifest.size -or $hash -ne $Manifest.sha256.ToLowerInvariant()) {
        throw 'the download does not match the release manifest (size or SHA-256); nothing was installed'
    }
    Say 'Download checked: size and SHA-256 match the release.'
    Say 'Unpacking.'
    $unpacked = Join-Path $Work 'files'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $unpacked)
    Remove-Item -LiteralPath $zip -Force
    # The zip holds an AccessRem folder.
    foreach ($root in @($unpacked, (Join-Path $unpacked 'AccessRem'))) {
        if ((Test-Path -LiteralPath (Join-Path $root $AppExe)) -and (Test-Path -LiteralPath (Join-Path $root 'nvda'))) {
            Get-ChildItem -LiteralPath $root -Recurse -File | Unblock-File
            return $root
        }
    }
    throw "the download has no $AppExe and nvda folder; nothing was installed"
}

# Replaces the program files in $Folder with those in $Source.
function Copy-Package([string]$Source, [string]$Folder) {
    New-Item -ItemType Directory -Force -Path $Folder | Out-Null
    foreach ($name in $PackageItems) {
        $old = Join-Path $Folder $name
        if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force }
    }
    foreach ($item in Get-ChildItem -LiteralPath $Source) {
        Copy-Item -LiteralPath $item.FullName -Destination $Folder -Recurse -Force
    }
}

# Updates the copy installed for all users from the unpacked release in
# $Source, the same way AccessRem's own Settings do. Windows asks for
# permission. True when it was updated.
function Update-System([string]$Source, [string]$System) {
    if (-not (Close-Running $System 'to be updated')) { return $false }
    Say 'Updating the copy installed for all users. Windows will ask for permission.'
    try {
        $p = Start-Process -FilePath (Join-Path $Source $AppExe) -ArgumentList "--install-system `"$Source`"" -Verb RunAs -Wait -PassThru
    } catch {
        Say 'Windows did not give permission, so the copy installed for all users was not updated.'
        return $false
    }
    if ($p.ExitCode -ne 0) {
        Say 'The copy installed for all users could not be updated. To see why, use Update the installed copy in AccessRem''s Settings, on the Getting help tab.'
        return $false
    }
    Say "The copy installed for all users in $System was updated."
    return $true
}

function Uninstall([string]$Folder) {
    Say "This removes AccessRem from $Folder, with its shortcuts, start at sign-in and nvdaremote link handling."
    if (-not (Ask-YesNo 'Uninstall AccessRem?' $false)) { Say 'Nothing was changed.'; return }
    if (-not (Close-Running $Folder 'to be uninstalled')) { Say 'Nothing was changed.'; return }
    $system = System-Folder
    $removeData = $false
    if (-not $system) {
        $removeData = Ask-YesNo 'Also remove your saved computers, trusted certificates and other AccessRem settings from this computer?' $false
    }
    foreach ($name in $PackageItems) {
        Remove-Item -LiteralPath (Join-Path $Folder $name) -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (@(Get-ChildItem -LiteralPath $Folder -Force -ErrorAction SilentlyContinue).Count -eq 0) {
        Remove-Item -LiteralPath $Folder -Force -ErrorAction SilentlyContinue
    } else {
        Say "Other files in $Folder were left in place."
    }
    foreach ($link in @($StartMenu, $Desktop)) {
        if (Same-Folder (Shortcut-Folder $link) $Folder) { Remove-Item -LiteralPath $link -Force }
    }
    if (Same-Folder (Run-Folder) $Folder) {
        Remove-ItemProperty -LiteralPath $RunKey -Name 'AccessRem' -ErrorAction SilentlyContinue
    }
    $command = Get-Item -LiteralPath "$LinkKey\shell\open\command" -ErrorAction SilentlyContinue
    if ($command -and ([string]$command.GetValue('')).StartsWith("`"$(Join-Path $Folder $AppExe)`"", [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $LinkKey -Recurse -Force
    }
    if ($removeData) {
        Remove-Item -LiteralPath $DataFolder -Recurse -Force -ErrorAction SilentlyContinue
        Say 'Your AccessRem settings were removed.'
    }
    Say 'AccessRem was uninstalled. To install it again, run the same command.'
    if ($system) {
        Say "AccessRem is also installed for all users in $system, and keeps your settings. To remove it, use Installed apps in Windows Settings."
    }
}

function Start-AccessRem([string]$Folder) {
    if (@(Running-In $Folder | Where-Object { $_.ProcessName -ieq 'AccessRem' }).Count -gt 0) {
        Say 'AccessRem is already running: find it in the system tray (Windows+B).'
        return
    }
    Start-Process -FilePath (Join-Path $Folder $AppExe) -WorkingDirectory $Folder
    Say 'AccessRem is starting.'
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('accessrem-install-' + [guid]::NewGuid().ToString('N'))
try {
    Say 'AccessRem installer for Windows.'
    Say 'Press Enter to accept the answer in brackets.'
    Say ''
    if (-not [Environment]::Is64BitOperatingSystem -or [Environment]::OSVersion.Version.Major -lt 10) {
        Say 'AccessRem needs 64-bit Windows 10 or 11. Nothing was installed.'
        return
    }
    # Windows PowerShell 5.1 may not offer TLS 1.2 by itself; GitHub needs it.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    Say 'Checking the latest release on GitHub.'
    $manifest = Get-Manifest
    $latest = $manifest.version
    Say "Latest version: $latest"

    $installed = Find-Installed
    $system = System-Folder
    if ($system) {
        Say "Installed for all users: $(Version-Text $system) in $system"
    }

    # Only the copy installed for all users: open it, or update it.
    if (-not $installed -and $system) {
        Say ''
        if (Is-Current $system $latest) {
            $choice = Ask-Choice 'It is up to date. What would you like to do?' @('Open AccessRem', 'Also install a separate copy just for you', 'Quit')
            $choice = @(0, 1, 3, 4)[$choice]
        } else {
            $choice = Ask-Choice 'What would you like to do?' @("Update it to AccessRem $latest (Windows will ask for permission)", 'Open AccessRem', 'Also install a separate copy just for you', 'Quit')
            $choice = @(0, 2, 1, 3, 4)[$choice]
        }
        # 1 open, 2 update, 3 separate copy, 4 quit.
        switch ($choice) {
            1 { Start-AccessRem $system; return }
            4 { Say 'Nothing was changed.'; return }
            2 {
                New-Item -ItemType Directory -Path $work | Out-Null
                $source = Get-Package $manifest $work
                if (Update-System $source $system) {
                    Start-AccessRem $system
                    Say 'To update it later, run this command again.'
                }
                return
            }
        }
        Say 'The copy just for you is updated by this installer, without asking Windows for permission.'
    }

    $fresh = $true
    if ($installed) {
        Say "Installed: $(Version-Text $installed) in $installed"
        Say ''
        if (Is-Current $installed $latest) {
            $choice = Ask-Choice 'It is up to date. What would you like to do?' @('Open AccessRem', "Reinstall AccessRem $latest", 'Uninstall AccessRem', 'Quit')
        } else {
            $choice = Ask-Choice 'What would you like to do?' @("Update to AccessRem $latest", 'Uninstall AccessRem', 'Quit')
            # Same numbering as above: 2 reinstall/update, 3 uninstall, 4 quit.
            $choice = @(0, 2, 3, 4)[$choice]
        }
        switch ($choice) {
            1 { Start-AccessRem $installed; return }
            3 { Uninstall $installed; return }
            4 { Say 'Nothing was changed.'; return }
        }
        $folder = $installed
        $fresh = $false
    } else {
        if (-not $system) { Say 'AccessRem is not installed on this computer yet.' }
        $folder = Ask-Folder $DefaultFolder
        if (Same-Folder $folder $system) {
            Say "$folder is the copy installed for all users. Run this command again and choose another folder."
            return
        }
    }

    $wantStartMenu = $true
    $wantDesktop = $false
    if ($fresh) {
        # The copy installed for all users has its own Start menu shortcut.
        $wantStartMenu = Ask-YesNo 'Add AccessRem to the Start menu?' (-not $system)
        $wantDesktop = Ask-YesNo 'Add an AccessRem shortcut to the desktop?' $false
    }
    $updateSystem = $false
    if ($system -and -not (Is-Current $system $latest)) {
        $updateSystem = Ask-YesNo "Also update the copy installed for all users to AccessRem $latest? Windows will ask for permission." $true
    }

    if (-not (Close-Running $folder 'to be updated')) { Say 'Nothing was changed.'; return }
    New-Item -ItemType Directory -Path $work | Out-Null
    $source = Get-Package $manifest $work
    Copy-Package $source $folder
    $exe = Join-Path $folder $AppExe

    if ($wantStartMenu) { Make-Shortcut $StartMenu $exe; if ($fresh) { Say 'Added AccessRem to the Start menu.' } }
    if ($wantDesktop) { Make-Shortcut $Desktop $exe; Say 'Added an AccessRem shortcut to the desktop.' }
    if (-not $fresh) {
        # A start-at-sign-in entry that points at a copy that is gone now
        # points at this one. AccessRem's Settings own this entry otherwise.
        $run = Run-Folder
        if ($run -and -not (Test-Path -LiteralPath (Join-Path $run $AppExe))) {
            New-ItemProperty -LiteralPath $RunKey -Name 'AccessRem' -Value "`"$exe`" --startup" -PropertyType String -Force | Out-Null
            Say 'Start at sign-in now starts this copy (it pointed at a copy that no longer exists).'
        }
    }

    Say ''
    Say "AccessRem $latest is installed in $folder"
    if ($updateSystem) { [void](Update-System $source $system) }
    Start-AccessRem $folder
    if ($fresh) {
        Say 'To get help, choose a computer or connect with the server and key your helper gives you. To give help, connect with the details the other person gives you.'
        Say 'In Settings you can start AccessRem when you sign in to Windows, open nvdaremote links with it, and install it for all users so that a helper can operate User Account Control and sign-in screens.'
    }
    Say 'To update, reinstall or uninstall AccessRem later, run this command again.'
} catch {
    Say ''
    Say "The AccessRem installer stopped: $($_.Exception.Message)"
    if ($_.Exception -is [Net.WebException]) { Say 'Check your internet connection and try again.' }
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
}
