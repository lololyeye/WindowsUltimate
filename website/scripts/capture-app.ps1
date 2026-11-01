# Captures real screenshots of Windows Ultimate for the website. Only navigates and runs read-only analysis.
# Usage (see website/README.md): build the app, then run once per language, e.g.
#   .\capture-app.ps1 -Exe ..\..\build\WindowsUltimate.exe -Lang en -Out shots\en -Pages "Dashboard|dashboard.png|Quick Scan|20","Gaming|gaming.png||"
# Each page spec is "<nav title>|<file>[|<button to invoke>|<seconds to wait>]". Only name read-only buttons.
# The app's settings are switched to that language and an opaque window for the capture, then restored.
# PrintWindow renders the app's own pixels, so nothing else on screen can end up in a capture.
param([string]$Exe, [string]$Lang = "en", [string]$Out, [string[]]$Pages)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@
[W]::SetProcessDPIAware() | Out-Null

$exe = (Resolve-Path $Exe).Path
$settingsPath = Join-Path $env:LOCALAPPDATA "WindowsUltimate\settings.json"
$backup = Get-Content $settingsPath -Raw
New-Item -ItemType Directory -Force $Out | Out-Null

try {
  @{ UiLanguage = $Lang; MonitorIntervalMs = 1000; CreateRestorePointByDefault = $true; CheckForUpdates = $false; Translucency = $false } |
    ConvertTo-Json | Set-Content $settingsPath -Encoding utf8

  $p = Start-Process $exe -PassThru
  $deadline = (Get-Date).AddSeconds(30)
  while ($p.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300; $p.Refresh() }
  $h = $p.MainWindowHandle
  [W]::ShowWindow($h, 1) | Out-Null
  [W]::MoveWindow($h, 40, 40, 1600, 1000, $true) | Out-Null   # physical pixels
  Start-Sleep -Seconds 6

  $root = [Windows.Automation.AutomationElement]::FromHandle($h)
  $walker = [Windows.Automation.TreeWalker]::ControlViewWalker
  function Find-Text($name) {
    $c = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)
    $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $c)
  }
  function Up($el, $type) {
    while ($el -and $el.Current.ControlType -ne $type) { $el = $walker.GetParent($el) }
    $el
  }
  function Shot($file) {
    Start-Sleep -Milliseconds 700
    $r = New-Object W+RECT
    [W]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $dc = $g.GetHdc()
    [W]::PrintWindow($h, $dc, 2) | Out-Null   # PW_RENDERFULLCONTENT: the window's own pixels, even if covered
    $g.ReleaseHdc($dc)
    $bmp.Save((Join-Path $Out $file), [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "saved $file"
  }
  function Find-Button($name) {
    $c1 = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)
    $all = $root.FindAll([Windows.Automation.TreeScope]::Descendants, $c1)
    foreach ($b in $all) {
      if ($b.Current.Name -like "*$name*") { return $b }
      $t = $b.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $name)))
      if ($t) { return $b }
    }
    $null
  }
  foreach ($spec in $Pages) {
    # spec: "<NavTitle>|<file>[|<button to invoke>|<wait seconds>]"
    $parts = $spec.Split("|")
    $t = Find-Text $parts[0]
    if (-not $t) { Write-Host "nav not found: $($parts[0])"; continue }
    $rb = Up $t ([Windows.Automation.ControlType]::RadioButton)
    $rb.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 3
    if ($parts.Length -ge 3 -and $parts[2]) {
      $btn = Find-Button $parts[2]
      if ($btn) { $btn.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Seconds ([int]$parts[3]) }
      else { Write-Host "button not found: $($parts[2])" }
    }
    Shot $parts[1]
  }
  $p.CloseMainWindow() | Out-Null
  Start-Sleep -Seconds 2
  if (-not $p.HasExited) { $p.Kill() }
}
finally {
  Set-Content $settingsPath $backup -Encoding utf8 -NoNewline
  Write-Host "settings restored"
}
