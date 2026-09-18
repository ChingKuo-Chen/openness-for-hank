# Batch-drags devices in the TIA network view.
# Device positions in that view are graphical data with no Openness API, so the
# only way to lay them out is to move them with the mouse.  Doing it in batches
# keeps the window pinned for the whole sequence, which keeps coordinates valid.
#
# Moves are given as "fromX,fromY,toX,toY" strings, in screen pixels.
#   .\Arrange.ps1 -Moves '350,220,320,350', '545,220,520,350'

param(
    [Parameter(Mandatory = $true)][string[]]$Moves,
    [string]$TitleMatch = 'TEST_PROJECT_CUSOR',
    [int]$SettleMs = 350
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Arr {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool r);
    public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;

    public static void Drag(int x1, int y1, int x2, int y2) {
        SetCursorPos(x1, y1);
        System.Threading.Thread.Sleep(220);
        mouse_event(LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        System.Threading.Thread.Sleep(220);
        for (int i = 1; i <= 25; i++) {
            SetCursorPos(x1 + (x2 - x1) * i / 25, y1 + (y2 - y1) * i / 25);
            System.Threading.Thread.Sleep(12);
        }
        System.Threading.Thread.Sleep(220);
        mouse_event(LEFTUP, 0, 0, 0, UIntPtr.Zero);
    }
}
"@

$proc = Get-Process Siemens.Automation.Portal -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like "*$TitleMatch*" } |
    Select-Object -First 1

if (-not $proc) { Write-Error "TIA window not found: $TitleMatch"; exit 1 }

[Arr]::ShowWindow($proc.MainWindowHandle, 9) | Out-Null
[Arr]::MoveWindow($proc.MainWindowHandle, 0, 0, 1920, 1200, $true) | Out-Null
[Arr]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 800

$index = 0
foreach ($move in $Moves) {
    $parts = $move -split ','
    if ($parts.Count -ne 4) { Write-Warning "skip malformed move: $move"; continue }

    $index++
    [Arr]::Drag([int]$parts[0], [int]$parts[1], [int]$parts[2], [int]$parts[3])
    Write-Host ("{0,2}. {1},{2} -> {3},{4}" -f $index, $parts[0], $parts[1], $parts[2], $parts[3])
    Start-Sleep -Milliseconds $SettleMs
}

# Park the cursor away from the canvas so nothing shows a hover highlight in
# the verification screenshot.
[Arr]::SetCursorPos(1750, 1000) | Out-Null
Write-Host "done: $index moves"
