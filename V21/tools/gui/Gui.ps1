# Mouse and window helpers for driving TIA Portal where Openness has no API.
# The network view layout is the main case: device positions are graphical data
# that the API does not expose, so they can only be arranged by dragging.
#
#   .\Gui.ps1 -Action focus
#   .\Gui.ps1 -Action scroll -X 900 -Y 400 -Delta -3 -Ctrl
#   .\Gui.ps1 -Action drag -X 300 -Y 215 -ToX 300 -ToY 400

param(
    [ValidateSet('focus', 'click', 'dblclick', 'drag', 'scroll', 'move')]
    [string]$Action = 'focus',
    [int]$X = 0,
    [int]$Y = 0,
    [int]$ToX = 0,
    [int]$ToY = 0,
    [int]$Delta = 0,
    [switch]$Ctrl,
    [string]$TitleMatch = 'TEST_PROJECT_CUSOR',
    [int]$WinX = 0,
    [int]$WinY = 0,
    [int]$WinW = 1920,
    [int]$WinH = 1200
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Gui {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, UIntPtr e);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int t, bool r);
    [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, UIntPtr e);

    public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004, WHEEL = 0x0800;
    public const byte VK_CONTROL = 0x11;
    public const uint KEYUP = 0x0002;

    public static void Glide(int x1, int y1, int x2, int y2, int steps) {
        for (int i = 1; i <= steps; i++) {
            SetCursorPos(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps);
            System.Threading.Thread.Sleep(12);
        }
    }
}
"@

$proc = Get-Process Siemens.Automation.Portal -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like "*$TitleMatch*" } |
    Select-Object -First 1

if (-not $proc) { Write-Error "TIA window not found: $TitleMatch"; exit 1 }

# Pin the window to a fixed rectangle so screen coordinates stay valid between
# calls; this machine has a second monitor and TIA drifts onto it otherwise.
[Gui]::ShowWindow($proc.MainWindowHandle, 9) | Out-Null
[Gui]::MoveWindow($proc.MainWindowHandle, $WinX, $WinY, $WinW, $WinH, $true) | Out-Null
[Gui]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 600

switch ($Action) {
    'focus' {
        Write-Host "focused PID $($proc.Id) at ${WinX},${WinY} ${WinW}x${WinH}"
    }
    'move' {
        [Gui]::SetCursorPos($X, $Y) | Out-Null
        Write-Host "moved to $X,$Y"
    }
    'click' {
        [Gui]::SetCursorPos($X, $Y) | Out-Null
        Start-Sleep -Milliseconds 150
        [Gui]::mouse_event([Gui]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [Gui]::mouse_event([Gui]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
        Write-Host "clicked $X,$Y"
    }
    'dblclick' {
        [Gui]::SetCursorPos($X, $Y) | Out-Null
        Start-Sleep -Milliseconds 150
        for ($i = 0; $i -lt 2; $i++) {
            [Gui]::mouse_event([Gui]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
            [Gui]::mouse_event([Gui]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 40
        }
        Write-Host "double-clicked $X,$Y"
    }
    'drag' {
        [Gui]::SetCursorPos($X, $Y) | Out-Null
        Start-Sleep -Milliseconds 250
        [Gui]::mouse_event([Gui]::LEFTDOWN, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 250
        # TIA ignores instant jumps, so move in steps to register the drag.
        [Gui]::Glide($X, $Y, $ToX, $ToY, 25)
        Start-Sleep -Milliseconds 250
        [Gui]::mouse_event([Gui]::LEFTUP, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 300
        Write-Host "dragged $X,$Y -> $ToX,$ToY"
    }
    'scroll' {
        [Gui]::SetCursorPos($X, $Y) | Out-Null
        Start-Sleep -Milliseconds 150
        if ($Ctrl) { [Gui]::keybd_event([Gui]::VK_CONTROL, 0, 0, [UIntPtr]::Zero) }
        for ($i = 0; $i -lt [Math]::Abs($Delta); $i++) {
            [Gui]::mouse_event([Gui]::WHEEL, 0, 0, $(if ($Delta -gt 0) { 120 } else { -120 }), [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 120
        }
        if ($Ctrl) { [Gui]::keybd_event([Gui]::VK_CONTROL, 0, [Gui]::KEYUP, [UIntPtr]::Zero) }
        Write-Host "scrolled $Delta at $X,$Y (ctrl=$Ctrl)"
    }
}
