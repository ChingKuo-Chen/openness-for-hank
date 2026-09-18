# Dismiss TIA Portal resource-warning dialog (0024:000004).
param([switch]$ListOnly)

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class W {
  public delegate bool E(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(E cb, IntPtr p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
}
"@

$titles = New-Object System.Collections.Generic.List[string]
$cb = [W+E]{
  param($h, $l)
  if ([W]::IsWindowVisible($h)) {
    $sb = New-Object System.Text.StringBuilder 512
    [void][W]::GetWindowText($h, $sb, 512)
    $t = $sb.ToString()
    if ($t) { [void]$titles.Add($t) }
  }
  return $true
}
[void][W]::EnumWindows($cb, [IntPtr]::Zero)

$hits = $titles | Where-Object {
  $_ -like '*GUIResourceUtilizationRateGetsCritical*' -or
  $_ -like '*0024:000004*' -or
  $_ -like 'ERROR: ResourceID*' -or
  $_ -like 'Compile (0125*' -or
  $_ -like '*not closed properly*' -or
  $_ -like '*was not closed*' -or
  $_ -like '*Recover*'
}

if ($ListOnly) {
  Write-Host 'Dialog candidates:'
  $hits | ForEach-Object { Write-Host "  $_" }
  return
}

$shell = New-Object -ComObject WScript.Shell
$enter = '{ENTER}'
$dismissed = 0

foreach ($title in $hits) {
  $prefix = if ($title.Length -gt 40) { $title.Substring(0, 40) } else { $title }
  if ($shell.AppActivate($prefix)) {
    Start-Sleep -Milliseconds 400
    $shell.SendKeys($enter)
    Write-Host "Dismissed: $title"
    $dismissed++
  }
}

if ($dismissed -eq 0) {
  foreach ($prefix in @('ERROR:', 'GUIResource', '0024:000004')) {
    if ($shell.AppActivate($prefix)) {
      Start-Sleep -Milliseconds 400
      $shell.SendKeys($enter)
      Write-Host "Dismissed via prefix: $prefix"
      $dismissed++
      break
    }
  }
}

if ($dismissed -eq 0) { Write-Host 'No dialog found.' }
else { Write-Host "Done ($dismissed)." }
