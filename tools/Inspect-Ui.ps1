<#
.SYNOPSIS
    Developer check: launches AssistBridge with a throwaway data folder, lists the UI Automation tree of the
    main window (to catch unnamed controls), saves a screenshot, then closes the app.
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot "..\dist\AssistBridge\AssistBridge.exe"),
    [string]$Screenshot = (Join-Path $env:TEMP "assistbridge-main.png"),
    [string]$SeedSettings
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

$data = Join-Path $env:TEMP ("AssistBridgeUi-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $data | Out-Null
if ($SeedSettings) { Copy-Item $SeedSettings (Join-Path $data "settings.json") }
$env:ASSISTBRIDGE_DATA_DIR = $data
$p = Start-Process -FilePath $Exe -PassThru
try {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
    $window = $null
    for ($i = 0; $i -lt 60 -and -not $window; $i++) {
        Start-Sleep -Milliseconds 500
        $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    }
    if (-not $window) { throw "Main window did not appear." }
    Start-Sleep -Seconds 1

    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $unnamed = New-Object System.Collections.Generic.List[string]
    function Walk($el, $depth) {
        $c = $el.Current
        $type = $c.ControlType.ProgrammaticName -replace "ControlType\.", ""
        $line = ("  " * $depth) + "$type '$($c.Name)'"
        if ($c.AcceleratorKey) { $line += " accel=$($c.AcceleratorKey)" }
        if ($c.AccessKey) { $line += " access=$($c.AccessKey)" }
        if ($c.HelpText) { $line += " help='$($c.HelpText)'" }
        if (-not $c.IsEnabled) { $line += " [disabled]" }
        Write-Output $line
        $interactive = @("Button", "Edit", "ComboBox", "CheckBox", "RadioButton", "List", "ListItem", "Slider", "Tab", "TabItem", "MenuItem")
        if ($interactive -contains $type -and [string]::IsNullOrWhiteSpace($c.Name)) { $unnamed.Add($line.Trim()) }
        $child = $walker.GetFirstChild($el)
        while ($child) { Walk $child ($depth + 1); $child = $walker.GetNextSibling($child) }
    }
    Walk $window 0

    $rect = $window.Current.BoundingRectangle
    $bmp = New-Object System.Drawing.Bitmap([int]$rect.Width, [int]$rect.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, $bmp.Size)
    $bmp.Save($Screenshot)
    Write-Output "Screenshot: $Screenshot"
    if ($unnamed.Count) { Write-Output "UNNAMED INTERACTIVE ELEMENTS:"; $unnamed | ForEach-Object { Write-Output "  $_" } }
    else { Write-Output "All interactive elements have accessible names." }
}
finally {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
}
