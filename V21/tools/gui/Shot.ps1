param(
    [string]$Out = "shot.png",
    [switch]$Foreground,
    [string]$TitleMatch,
    [int]$CropX = -1,
    [int]$CropY = -1,
    [int]$CropW = -1,
    [int]$CropH = -1,
    [double]$Zoom = 1.0
)

Add-Type -AssemblyName System.Windows.Forms, System.Drawing

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@

$proc = Get-Process | Where-Object {
    $_.ProcessName -eq 'Siemens.Automation.Portal' -and $_.MainWindowHandle -ne 0 -and
    (-not $TitleMatch -or $_.MainWindowTitle -like "*$TitleMatch*")
} | Select-Object -First 1

if (-not $proc) {
    Write-Error "TIA Portal window not found"
    exit 1
}

if ($Foreground) {
    # TIA often ignores a bare SetForegroundWindow; restoring first makes it stick.
    [Win]::ShowWindow($proc.MainWindowHandle, 9) | Out-Null
    [Win]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 700
}

$rect = New-Object Win+RECT
[Win]::GetWindowRect($proc.MainWindowHandle, [ref]$rect) | Out-Null

$x = $rect.L; $y = $rect.T
$w = $rect.R - $rect.L; $h = $rect.B - $rect.T

if ($CropW -gt 0) {
    $x = $rect.L + $CropX
    $y = $rect.T + $CropY
    $w = $CropW
    $h = $CropH
}

$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
$g.Dispose()

if ($Zoom -ne 1.0) {
    $zw = [int]($w * $Zoom); $zh = [int]($h * $Zoom)
    $big = New-Object System.Drawing.Bitmap $zw, $zh
    $zg = [System.Drawing.Graphics]::FromImage($big)
    $zg.InterpolationMode = 'NearestNeighbor'
    $zg.DrawImage($bmp, 0, 0, $zw, $zh)
    $zg.Dispose(); $bmp.Dispose()
    $bmp = $big
}

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Host "saved $Out  window=($($rect.L),$($rect.T)) ${w}x${h}"
