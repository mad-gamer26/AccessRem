# AccessRem installer for Windows.
#
#   irm https://accessrem.mad-gamer.com | iex
#
# Installs the latest AccessRem from the project's GitHub releases, or
# updates or uninstalls it, then starts it. Asks a few questions; pressing
# Enter takes the answer in brackets. Two ways to install:
#
# * For everyone on this computer (installed mode, in Program Files): a
#   helper can also use User Account Control and sign-in screens, and
#   Control+Alt+Delete. AccessRem itself does the install, exactly as its
#   Settings do; Windows asks for permission first.
# * Just for this user: no administrator rights, everything in the user's
#   own folders and registry.
#
# The copy for everyone replaces a copy just for the user (AccessRem hands
# over to it as it starts), so this offers to remove the second copy rather
# than keep both. Served by nginx on mad-gamer.com from
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

# Like Ask-Choice, but returns the action named for the chosen line, so
# menus that show different lines share one switch.
function Ask-Action([string]$Question, [object[]]$Lines) {
    $choice = Ask-Choice $Question @($Lines | ForEach-Object { $_[0] })
    return $Lines[$choice - 1][1]
}

function Ask-Folder([string]$Default) {
    $answer = (Read-Host "Install folder ($Default)").Trim().Trim('"')
    if ($answer -eq '') { return $Default }
    return [Environment]::ExpandEnvironmentVariables($answer)
}

function Same-Folder([string]$A, [string]$B) {
    return $A -and $B -and ($A.TrimEnd('\') -ieq $B.TrimEnd('\'))
}

function In-Folder([string]$Path, [string]$Folder) {
    return $Path -and $Folder -and $Path.StartsWith($Folder.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
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
    return [bool]($version -and ([version]$version -ge [version]$Latest))
}

# The copy installed for everyone (in Program Files), or $null.
function System-Folder {
    $value = Get-ItemProperty -LiteralPath $SystemKey -Name 'InstallDir' -ErrorAction SilentlyContinue
    if ($null -eq $value) { return $null }
    if (Test-Path -LiteralPath (Join-Path $value.InstallDir $AppExe)) { return $value.InstallDir.TrimEnd('\') }
    return $null
}

# The program the Run value starts, or $null.
function Run-Exe {
    $value = Get-ItemProperty -LiteralPath $RunKey -Name 'AccessRem' -ErrorAction SilentlyContinue
    if ($null -eq $value) { return $null }
    if ($value.AccessRem -match '^"([^"]+)"') { return $Matches[1] }
    return $null
}

function Shortcut-Target([string]$Link) {
    if (-not (Test-Path -LiteralPath $Link)) { return $null }
    try { return (New-Object -ComObject WScript.Shell).CreateShortcut($Link).TargetPath } catch { }
    return $null
}

# The program that opens nvdaremote links, or $null.
function Link-Exe {
    $command = Get-Item -LiteralPath "$LinkKey\shell\open\command" -ErrorAction SilentlyContinue
    if ($command -and ([string]$command.GetValue('')) -match '^"([^"]+)"') { return $Matches[1] }
    return $null
}

# The processes of AccessRem, or of its NVDA, running from $Folder.
function Running-In([string]$Folder) {
    return @(Get-Process -Name $ProcessNames -ErrorAction SilentlyContinue | Where-Object {
        try { In-Folder $_.Path $Folder } catch { $false }
    })
}

# Where this user's own copy of AccessRem is installed: the running copy, the
# Start menu shortcut, the start-at-sign-in entry, or the default folder.
function Find-Installed {
    $system = System-Folder
    $candidates = @()
    foreach ($p in @(Get-Process -Name 'AccessRem' -ErrorAction SilentlyContinue)) {
        try { if ($p.Path) { $candidates += Split-Path -Parent $p.Path } } catch { }
    }
    foreach ($exe in @((Shortcut-Target $StartMenu), (Run-Exe))) {
        if ($exe) { $candidates += Split-Path -Parent $exe }
    }
    $candidates += $DefaultFolder
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

# This user's shortcuts, start at sign-in and nvdaremote link handling that
# use the copy in $Folder: they move to $Replacement (another AccessRem.exe),
# or go when there is none. The copy for everyone has its own Start menu
# shortcut, so this user's is removed either way.
function Move-UserEntries([string]$Folder, [string]$Replacement) {
    if (In-Folder (Shortcut-Target $StartMenu) $Folder) { Remove-Item -LiteralPath $StartMenu -Force }
    if (In-Folder (Shortcut-Target $Desktop) $Folder) {
        if ($Replacement) { Make-Shortcut $Desktop $Replacement } else { Remove-Item -LiteralPath $Desktop -Force }
    }
    if (In-Folder (Run-Exe) $Folder) {
        if ($Replacement) {
            New-ItemProperty -LiteralPath $RunKey -Name 'AccessRem' -Value "`"$Replacement`" --startup" -PropertyType String -Force | Out-Null
        } else {
            Remove-ItemProperty -LiteralPath $RunKey -Name 'AccessRem'
        }
    }
    if (In-Folder (Link-Exe) $Folder) {
        if ($Replacement) {
            Set-Item -LiteralPath "$LinkKey\shell\open\command" -Value "`"$Replacement`" `"%1`""
            if (Test-Path -LiteralPath "$LinkKey\DefaultIcon") { Set-Item -LiteralPath "$LinkKey\DefaultIcon" -Value "`"$Replacement`",0" }
        } else {
            Remove-Item -LiteralPath $LinkKey -Recurse -Force
        }
    }
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
    New-Item -ItemType Directory -Force -Path $Work | Out-Null
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

# NVDA loads its IAccessible2 proxy DLLs (nvda\lib\<version>) into every
# program it reads, Explorer included, and they stay loaded until those
# programs exit. Such a file cannot be replaced or deleted, but it can be
# renamed: it is moved aside as *.delete, for a later run (or the clean-up
# at sign-in after uninstalling) to remove.
function Move-Aside([string]$Path) {
    Rename-Item -LiteralPath $Path -NewName ((Split-Path -Leaf $Path) + '.' + [guid]::NewGuid().ToString('N') + '.delete')
}

# The files of a copy in $Folder (not other files someone put there).
function Package-Files([string]$Folder) {
    foreach ($name in $PackageItems) {
        $path = Join-Path $Folder $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { Get-Item -LiteralPath $path -Force }
        elseif (Test-Path -LiteralPath $path) { Get-ChildItem -LiteralPath $path -Recurse -File -Force }
    }
    Get-ChildItem -LiteralPath $Folder -Filter '*.delete' -File -Force -ErrorAction SilentlyContinue
}

# Removes the empty folders under $Folder's package items.
function Remove-EmptyFolders([string]$Folder) {
    foreach ($name in $PackageItems) {
        $path = Join-Path $Folder $name
        if (-not (Test-Path -LiteralPath $path -PathType Container)) { continue }
        $dirs = @(Get-ChildItem -LiteralPath $path -Recurse -Directory -Force | Sort-Object { $_.FullName.Length } -Descending) + @(Get-Item -LiteralPath $path)
        foreach ($d in $dirs) {
            if (@(Get-ChildItem -LiteralPath $d.FullName -Force).Count -eq 0) { Remove-Item -LiteralPath $d.FullName -Force }
        }
    }
}

# Installs this user's copy in $Folder from the unpacked release in $Source,
# replacing the program files of any copy there.
function Install-User([string]$Source, [string]$Folder) {
    New-Item -ItemType Directory -Force -Path $Folder | Out-Null
    # An uninstall's clean-up at the next sign-in would remove this copy.
    $runOnce = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce'
    $cleanup = Get-ItemProperty -LiteralPath $runOnce -Name 'AccessRemCleanup' -ErrorAction SilentlyContinue
    if ($cleanup -and $cleanup.AccessRemCleanup.Contains("`"$Folder`"")) {
        Remove-ItemProperty -LiteralPath $runOnce -Name 'AccessRemCleanup'
    }
    $root = (Resolve-Path -LiteralPath $Source).Path.TrimEnd('\')
    $new = @{}
    foreach ($f in Get-ChildItem -LiteralPath $root -Recurse -File) { $new[$f.FullName.Substring($root.Length + 1)] = $f }
    # Files the new release does not have, and those moved aside before.
    $folderPrefix = (Resolve-Path -LiteralPath $Folder).Path.TrimEnd('\').Length + 1
    foreach ($old in @(Package-Files $Folder)) {
        if ($new.ContainsKey($old.FullName.Substring($folderPrefix)) -and $old.Name -notlike '*.delete') { continue }
        try { Remove-Item -LiteralPath $old.FullName -Force } catch { if ($old.Name -notlike '*.delete') { Move-Aside $old.FullName } }
    }
    foreach ($rel in $new.Keys) {
        $f = $new[$rel]
        $dest = Join-Path $Folder $rel
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
        try {
            Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
        } catch {
            $old = Get-Item -LiteralPath $dest -Force -ErrorAction SilentlyContinue
            if (-not $old) { throw }
            # The same file (unpacking and copying keep the time): nothing to replace.
            if ($old.Length -eq $f.Length -and $old.LastWriteTimeUtc -eq $f.LastWriteTimeUtc) { continue }
            Move-Aside $dest
            Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
        }
    }
    Remove-EmptyFolders $Folder
}

# Runs AccessRem.exe with administrator permission, which Windows asks for,
# and waits. The exit code, or $null when permission was not given.
function Run-Elevated([string]$Exe, [string]$Arguments) {
    try {
        return (Start-Process -FilePath $Exe -ArgumentList $Arguments -Verb RunAs -Wait -PassThru).ExitCode
    } catch {
        return $null
    }
}

# Installs or updates the copy for everyone from the unpacked release in
# $Source, as AccessRem's own Settings do. True when it worked.
function Install-System([string]$Source) {
    $system = System-Folder
    if ($system -and -not (Close-Running $system 'to be updated')) { return $false }
    Say 'Installing AccessRem for everyone on this computer. Windows will ask for permission.'
    $code = Run-Elevated (Join-Path $Source $AppExe) "--install-system `"$Source`""
    if ($null -eq $code) {
        Say 'Windows did not give permission, so AccessRem was not installed for everyone.'
        return $false
    }
    if ($code -ne 0 -or -not (System-Folder)) {
        Say 'AccessRem could not be installed for everyone. Details are in the AccessRem log of the administrator account that gave permission (in its AppData\Roaming\AccessRem\logs folder).'
        return $false
    }
    Say "AccessRem is installed for everyone in $(System-Folder)"
    return $true
}

# Removes this user's copy in $Folder (not the settings), moving its
# shortcuts and other entries to $Replacement when given.
function Remove-UserCopy([string]$Folder, [string]$Replacement) {
    $inUse = $false
    foreach ($f in @(Package-Files $Folder)) {
        try { Remove-Item -LiteralPath $f.FullName -Force } catch { $inUse = $true }
    }
    Remove-EmptyFolders $Folder
    if ($inUse) {
        # Removed at the next sign-in, when no program has them loaded. Only
        # the copy's own items: cd must succeed first, and the folder itself
        # only goes when empty.
        $cleanup = "cmd.exe /c cd /d `"$Folder`" && (rmdir /s /q nvda & rmdir /s /q backend-addon & del /f /q *.delete & cd .. & rmdir `"$Folder`")"
        New-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce' -Name 'AccessRemCleanup' -Value $cleanup -PropertyType String -Force | Out-Null
        Say "Some files in $Folder are in use by other programs. They are removed the next time you sign in to Windows."
    } elseif (@(Get-ChildItem -LiteralPath $Folder -Force -ErrorAction SilentlyContinue).Count -eq 0) {
        Remove-Item -LiteralPath $Folder -Force -ErrorAction SilentlyContinue
    } else {
        Say "Other files in $Folder were left in place."
    }
    Move-UserEntries $Folder $Replacement
}

# Uninstalls the copy for everyone, as Windows' Installed apps list does.
# True when it is gone.
function Remove-System([string]$System) {
    Say 'Uninstalling the copy for everyone. Windows will ask for permission.'
    $code = Run-Elevated (Join-Path $System $AppExe) '--uninstall-system --confirmed'
    if ($null -eq $code) {
        Say "Windows did not give permission, so the copy for everyone in $System was not uninstalled."
        return $false
    }
    if ($code -ne 0) {
        Say "The copy for everyone in $System could not be uninstalled. Try Installed apps in Windows Settings."
        return $false
    }
    Move-UserEntries $System $null
    # It removes its folder a few seconds after exiting.
    for ($i = 0; $i -lt 40 -and (Test-Path -LiteralPath $System); $i++) { Start-Sleep -Milliseconds 250 }
    if (Test-Path -LiteralPath $System) { Say "Some files in $System are in use by other programs. Windows removes them when it restarts." }
    return $true
}

function Uninstall([string]$Installed, [string]$System) {
    $where = @($Installed, $System | Where-Object { $_ }) -join ' and '
    Say "This removes AccessRem from $where, with its shortcuts, start at sign-in and nvdaremote link handling."
    if (-not (Ask-YesNo 'Uninstall AccessRem?' $false)) { Say 'Nothing was changed.'; return }
    foreach ($folder in @($Installed, $System | Where-Object { $_ })) {
        if (-not (Close-Running $folder 'to be uninstalled')) { Say 'Nothing was changed.'; return }
    }
    $removeData = Ask-YesNo 'Also remove your saved computers, trusted certificates and other AccessRem settings?' $false
    $gone = $true
    if ($Installed) { Remove-UserCopy $Installed $null }
    if ($System) { $gone = Remove-System $System }
    if ($removeData -and $gone) {
        Remove-Item -LiteralPath $DataFolder -Recurse -Force -ErrorAction SilentlyContinue
        Say 'Your AccessRem settings were removed.'
    }
    if ($gone) { Say 'AccessRem was uninstalled. To install it again, run the same command.' }
}

function Start-AccessRem([string]$Folder) {
    if (@(Running-In $Folder | Where-Object { $_.ProcessName -ieq 'AccessRem' }).Count -gt 0) {
        Say 'AccessRem is already running: find it in the system tray (Windows+B).'
        return
    }
    Start-Process -FilePath (Join-Path $Folder $AppExe) -WorkingDirectory $Folder
    Say 'AccessRem is starting.'
}

# Replaces this user's copy with the copy for everyone, updated from $Source
# when given.
function Move-ToSystem([string]$Installed, [string]$Source) {
    if (-not (Close-Running $Installed 'to be replaced')) { Say 'Nothing was changed.'; return $false }
    if ($Source -and -not (Install-System $Source)) { return $false }
    $system = System-Folder
    Remove-UserCopy $Installed (Join-Path $system $AppExe)
    Say "The copy just for you in $Installed was removed. Your saved computers and settings are kept."
    return $true
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
    if ($system) { Say "Installed for everyone: $(Version-Text $system) in $system" }
    if ($installed) { Say "Installed just for you: $(Version-Text $installed) in $installed" }
    Say ''

    if ($installed -and $system) {
        Say 'The copy for everyone does everything the copy just for you does, and also works on User Account Control and sign-in screens, so the copy just for you is not needed.'
        $keep = if (Is-Current $system $latest) { 'Keep only the copy for everyone' } else { "Keep only the copy for everyone, updated to AccessRem $latest (Windows will ask for permission)" }
        $action = Ask-Action 'What would you like to do?' @(
            @($keep, 'keep'), @('Open AccessRem', 'open'), @('Uninstall AccessRem (both copies)', 'uninstall'), @('Quit', 'quit'))
        switch ($action) {
            'keep' {
                $source = $null
                if (-not (Is-Current $system $latest)) {
                    if (-not (Close-Running $installed 'to be replaced')) { Say 'Nothing was changed.'; return }
                    $source = Get-Package $manifest $work
                }
                if (Move-ToSystem $installed $source) { Start-AccessRem (System-Folder) }
            }
            'open' { Start-AccessRem $system }
            'uninstall' { Uninstall $installed $system }
            'quit' { Say 'Nothing was changed.' }
        }
        return
    }

    if ($system) {
        if (Is-Current $system $latest) {
            $action = Ask-Action 'It is up to date. What would you like to do?' @(
                @('Open AccessRem', 'open'), @("Reinstall AccessRem $latest (Windows will ask for permission)", 'update'), @('Uninstall AccessRem', 'uninstall'), @('Quit', 'quit'))
        } else {
            $action = Ask-Action 'What would you like to do?' @(
                @("Update to AccessRem $latest (Windows will ask for permission)", 'update'), @('Open AccessRem', 'open'), @('Uninstall AccessRem', 'uninstall'), @('Quit', 'quit'))
        }
        switch ($action) {
            'open' { Start-AccessRem $system }
            'update' {
                if (-not (Close-Running $system 'to be updated')) { Say 'Nothing was changed.'; return }
                $source = Get-Package $manifest $work
                if (Install-System $source) {
                    Start-AccessRem (System-Folder)
                    Say 'To update, reinstall or uninstall AccessRem later, run this command again.'
                }
            }
            'uninstall' { Uninstall $null $system }
            'quit' { Say 'Nothing was changed.' }
        }
        return
    }

    $everyone = 'Windows will ask for permission. Your helper can also use User Account Control and sign-in screens, and send Control+Alt+Delete.'
    $fresh = -not $installed
    if ($installed) {
        if (Is-Current $installed $latest) {
            $action = Ask-Action 'It is up to date. What would you like to do?' @(
                @('Open AccessRem', 'open'), @("Install for everyone instead. $everyone", 'system'), @("Reinstall AccessRem $latest just for you", 'user'), @('Uninstall AccessRem', 'uninstall'), @('Quit', 'quit'))
        } else {
            $action = Ask-Action 'What would you like to do?' @(
                @("Update to AccessRem $latest just for you", 'user'), @("Update and install for everyone instead. $everyone", 'system'), @('Uninstall AccessRem', 'uninstall'), @('Quit', 'quit'))
        }
        switch ($action) {
            'open' { Start-AccessRem $installed; return }
            'uninstall' { Uninstall $installed $null; return }
            'quit' { Say 'Nothing was changed.'; return }
        }
    } else {
        Say 'AccessRem is not installed on this computer yet.'
        $action = Ask-Action 'How would you like to install it?' @(
            @("For everyone on this computer. $everyone", 'system'), @('Just for you (no permission needed). Your helper cannot use User Account Control or sign-in screens.', 'user'))
    }

    if ($action -eq 'system') {
        $wantDesktop = Ask-YesNo 'Add an AccessRem shortcut to the desktop? (The Start menu always gets one.)' $false
        if ($installed -and -not (Close-Running $installed 'to be replaced')) { Say 'Nothing was changed.'; return }
        $source = Get-Package $manifest $work
        if ($installed) {
            if (-not (Move-ToSystem $installed $source)) { return }
        } elseif (-not (Install-System $source)) {
            if (-not (Ask-YesNo 'Install AccessRem just for you instead?' $true)) { Say 'Nothing was installed.'; return }
            $action = 'user'
        }
        if ($action -eq 'system') {
            $system = System-Folder
            if ($wantDesktop) { Make-Shortcut $Desktop (Join-Path $system $AppExe); Say 'Added an AccessRem shortcut to the desktop.' }
            Say ''
            Start-AccessRem $system
        }
    }

    if ($action -eq 'user') {
        $wantStartMenu = $true
        $wantDesktop = $false
        if ($fresh) {
            $folder = Ask-Folder $DefaultFolder
            $wantStartMenu = Ask-YesNo 'Add AccessRem to the Start menu?' $true
            $wantDesktop = Ask-YesNo 'Add an AccessRem shortcut to the desktop?' $false
        } else {
            $folder = $installed
            if (-not (Close-Running $folder 'to be updated')) { Say 'Nothing was changed.'; return }
        }
        if (-not (Test-Path -LiteralPath $work)) { $source = Get-Package $manifest $work }
        Install-User $source $folder
        $exe = Join-Path $folder $AppExe
        if ($wantStartMenu) { Make-Shortcut $StartMenu $exe; if ($fresh) { Say 'Added AccessRem to the Start menu.' } }
        if ($wantDesktop) { Make-Shortcut $Desktop $exe; Say 'Added an AccessRem shortcut to the desktop.' }
        if (-not $fresh) {
            # A start-at-sign-in entry that points at a copy that is gone now
            # points at this one. AccessRem's Settings own this entry otherwise.
            $run = Run-Exe
            if ($run -and -not (Test-Path -LiteralPath $run)) {
                New-ItemProperty -LiteralPath $RunKey -Name 'AccessRem' -Value "`"$exe`" --startup" -PropertyType String -Force | Out-Null
                Say 'Start at sign-in now starts this copy (it pointed at a copy that no longer exists).'
            }
        }
        Say ''
        Say "AccessRem $latest is installed in $folder"
        Start-AccessRem $folder
    }

    if ($fresh) {
        Say 'To get help, choose a computer or connect with the server and key your helper gives you. To give help, connect with the details the other person gives you.'
        Say 'In Settings you can start AccessRem when you sign in to Windows, and open nvdaremote links with it.'
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
