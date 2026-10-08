<#
.SYNOPSIS
    Deploys https://accessrem.mad-gamer.com: the Windows installer (install.ps1) and its nginx site.

.DESCRIPTION
    Copies install.ps1 and accessrem.mad-gamer.com.conf to the server over SSH, installs them with sudo
    (/var/www/accessrem/install.ps1 and /etc/nginx/conf.d/), checks the nginx configuration and reloads
    nginx. If the check fails, the previous site configuration is put back and nginx is not reloaded.
    Finally checks that the site serves this install.ps1.

    The installer itself downloads the latest GitHub release, so publishing a release needs no deploy.

.PARAMETER Server
    The SSH destination (default matthew@mad-gamer.com). The account needs sudo.
#>
[CmdletBinding()]
param(
    [string]$Server = "matthew@mad-gamer.com"
)

$ErrorActionPreference = "Stop"
$site = "accessrem.mad-gamer.com"
$script = Join-Path $PSScriptRoot "install.ps1"
$conf = Join-Path $PSScriptRoot "$site.conf"

# install.ps1 runs in Windows PowerShell 5.1 through irm | iex: keep it plain ASCII.
if ([IO.File]::ReadAllBytes($script) | Where-Object { $_ -gt 127 }) { throw "install.ps1 must be plain ASCII." }
$errors = $null
[void][Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$errors)
if ($errors) { throw "install.ps1 does not parse: $($errors[0].Message)" }

$remoteDir = "/tmp/accessrem-deploy-$([guid]::NewGuid().ToString('N'))"
Write-Host "Copying files to $Server..."
ssh $Server "mkdir -m 700 $remoteDir"
if ($LASTEXITCODE -ne 0) { throw "ssh $Server failed" }
scp -q $script $conf "${Server}:$remoteDir/"
if ($LASTEXITCODE -ne 0) { throw "scp failed" }

Write-Host "Installing and reloading nginx..."
# Runs in bash on the server, so LF line endings only.
$commands = @"
set -e
d=$remoteDir
trap 'rm -rf "`$d"' EXIT
sudo install -d -m 755 /var/www/accessrem
sudo install -m 644 "`$d/install.ps1" /var/www/accessrem/install.ps1
target=/etc/nginx/conf.d/$site.conf
if [ -f "`$target" ]; then sudo cp -p "`$target" "`$d/previous.conf"; fi
sudo install -m 644 "`$d/$site.conf" "`$target"
if ! sudo nginx -t 2>&1; then
    if [ -f "`$d/previous.conf" ]; then sudo cp -p "`$d/previous.conf" "`$target"; else sudo rm -f "`$target"; fi
    echo "nginx rejected the configuration; the previous one was put back and nginx was not reloaded." >&2
    exit 1
fi
sudo systemctl reload nginx
"@ -replace "`r", ""
# Not piped: Windows PowerShell adds a byte order mark and CRLF line endings to text piped to a program.
$encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($commands))
ssh $Server "echo $encoded | base64 -d | bash"
if ($LASTEXITCODE -ne 0) { throw "Deploying on $Server failed" }

Write-Host "Checking https://$site..."
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$expected = [IO.File]::ReadAllText($script)
foreach ($url in @("https://$site", "https://$site/install.ps1")) {
    # nginx's old workers can answer for a few seconds after a reload.
    for ($attempt = 1; ; $attempt++) {
        try {
            # The user agent of irm in Windows PowerShell, which is what the site serves the script to.
            $r = Invoke-WebRequest -UseBasicParsing -Uri $url -UserAgent "Mozilla/5.0 (Windows NT; Windows NT 10.0; en-US) WindowsPowerShell/5.1"
            $text = if ($r.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($r.Content) } else { [string]$r.Content }
            if ($text -eq $expected) { break }
            $problem = "it serves something else"
        } catch {
            $problem = $_.Exception.Message
        }
        if ($attempt -ge 10) { throw "$url does not serve this install.ps1: $problem" }
        Start-Sleep -Seconds 2
    }
    Write-Host "$url serves install.ps1."
}
Write-Host "Deployed. Install with: irm https://$site | iex"
