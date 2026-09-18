# First-open confirm. stdout = JSON only. Keep this file ASCII.
$ErrorActionPreference = 'Stop'
$proj = $env:CURSOR_PROJECT_DIR
if (-not $proj) { $proj = (Get-Location).Path }
$confirmPath = Join-Path $proj '.local\clone-confirmed'
$event = 'workspaceOpen'
$prompt = ''

try {
    $raw = [Console]::In.ReadToEnd()
    if ($raw) {
        $j = $raw | ConvertFrom-Json
        if ($j.hook_event_name) { $event = [string]$j.hook_event_name }
        if ($j.prompt) { $prompt = [string]$j.prompt }
    }
} catch {
    $event = 'workspaceOpen'
}

function Write-HookJson([hashtable]$obj) {
    $obj | ConvertTo-Json -Compress
}

function Set-Confirmed {
    $dir = Split-Path $confirmPath
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
    Set-Content -Path $confirmPath -Encoding utf8 -Value (
        "$(Get-Date -Format o)`n$env:COMPUTERNAME"
    )
}

if (Test-Path $confirmPath) {
    if ($event -eq 'beforeSubmitPrompt') {
        Write-HookJson @{ continue = $true }
        exit 0
    }
    Write-Output '{}'
    exit 0
}

# "I have read this" or Chinese 我看過了 / 我確認了 / 確認這些 (char codes keep this file ASCII)
$wo = [char]0x6211
$kan = [char]0x770B
$guo = [char]0x904E
$le = [char]0x4E86
$que = [char]0x78BA
$ren = [char]0x8A8D
$looksConfirmed = ($prompt -match 'I have read this') -or
    $prompt.Contains("$wo$kan$guo$le") -or
    $prompt.Contains("$wo$que$ren$le")

$boxText = @(
    'First open of openness-standard on this PC.'
    ''
    '1. Root folder must be this repo, not TiaOpennessCheck.'
    '2. Basic HMI Softkey/Discrete: read V21/doc/OPENNESS_NOTES.md section 12. Do not retry dead paths.'
    '3. Cursor Yes to All is Settings / Agents / Approvals on THIS PC. Clone does not copy it.'
    '4. TIA Openness AllowList is also this PC.'
    ''
    'Yes = confirmed. No = ask again next time. In chat you can also send: I have read this'
    'See V21/doc/CLONE-CONFIRM.md'
) -join [Environment]::NewLine

$blockText = @(
    'First open of openness-standard on this PC. Confirm first:'
    '1) Root is this repo, not TiaOpennessCheck'
    '2) HMI Softkey/Discrete: V21/doc/OPENNESS_NOTES.md section 12'
    '3) Cursor Yes to All is Settings / Agents / Approvals (not in git)'
    '4) TIA Openness AllowList is this PC'
    'Reply: I have read this'
) -join [Environment]::NewLine

if ($event -eq 'beforeSubmitPrompt') {
    if ($looksConfirmed) {
        Set-Confirmed
        Write-HookJson @{ continue = $true }
        exit 0
    }
    Write-HookJson @{ continue = $false; user_message = $blockText }
    exit 0
}

try {
    Add-Type -AssemblyName System.Windows.Forms | Out-Null
    $r = [System.Windows.Forms.MessageBox]::Show(
        $boxText,
        'openness-standard',
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Information
    )
    if ($r -eq [System.Windows.Forms.DialogResult]::Yes) {
        Set-Confirmed
    }
} catch {
    # CLI / no UI: first chat will ask instead.
}

Write-Output '{}'
exit 0
