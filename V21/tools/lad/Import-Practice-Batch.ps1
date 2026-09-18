# Import practice blocks in tiny batches to avoid TIA GUI resource exhaustion.
# Tip: close all block editor tabs in TIA before running; keep only LAD_PRACTICE open.
param(
    [ValidateSet('LAD','SCL','Both')]
    [string]$Kind = 'SCL',
    [string]$Project = $env:TIA_PRACTICE_PROJECT,
    [string]$Plc = 'PRACTICE_1215',
    [int]$BatchSize = 1,
    [int]$Start = -1,
    [int]$DelaySeconds = 25,
    [switch]$Continue,
    [switch]$Compile,
    [switch]$Save,
    [switch]$List
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$here = $root
$dataRoot = $here
$exe = Join-Path $here 'host\TiaOpennessCheck.exe'
if ([string]::IsNullOrWhiteSpace($Project)) {
    throw '請指定練習專案：-Project <絕對路徑.ap21> 或環境變數 TIA_PRACTICE_PROJECT'
}
$proj = $Project
$ladDir = Join-Path $dataRoot 'Practice'
$sclDir = Join-Path $dataRoot 'Practice\Scl'
$stateFile = Join-Path $dataRoot 'Practice\import-batch.state.json'

function Get-Queue {
    $q = @()
    if ($Kind -eq 'LAD' -or $Kind -eq 'Both') {
        foreach ($f in Get-ChildItem -LiteralPath $ladDir -Filter '*.lad.xml' | Sort-Object Name) {
            $name = $f.BaseName -replace '\.lad$',''
            $q += [pscustomobject]@{ Kind='LAD'; Name=$name; Path=$f.FullName; Gen=(Join-Path $ladDir ($name + '.lad.generated.xml')) }
        }
    }
    if ($Kind -eq 'SCL' -or $Kind -eq 'Both') {
        foreach ($f in Get-ChildItem -LiteralPath $sclDir -Filter '*.scl' | Sort-Object Name) {
            $q += [pscustomobject]@{ Kind='SCL'; Name=$f.BaseName; Path=$f.FullName; Gen=$null }
        }
    }
    return ,$q
}

$queue = Get-Queue
$total = $queue.Count

if ($List) {
    for ($i = 0; $i -lt $total; $i++) {
        Write-Host ("{0,4} {1,-4} {2}" -f $i, $queue[$i].Kind, $queue[$i].Name)
    }
    Write-Host "total=$total"
    if (Test-Path $stateFile) { Write-Host "state:"; Get-Content $stateFile }
    return
}

if ($Continue -and (Test-Path $stateFile)) {
    $prev = Get-Content $stateFile -Raw | ConvertFrom-Json
    if ($prev.lastPlc) { $Plc = $prev.lastPlc }
    if ($prev.lastKind) { $Kind = $prev.lastKind }
    if ($Start -lt 0) { $Start = [int]$prev.nextStart }
}

if ($Start -lt 0) { $Start = 0 }
if ($Start -ge $total) { Write-Host "nothing left (Start=$Start total=$total)"; return }

$end = [Math]::Min($Start + $BatchSize, $total) - 1
$slice = $queue[$Start..$end]

Write-Host ("=== batch [{0}..{1}] / {2} on {3}  delay={4}s ===" -f $Start, $end, $total, $Plc, $DelaySeconds) -ForegroundColor Cyan
Write-Host 'Close block editor tabs in TIA before continuing.' -ForegroundColor Yellow

$dismiss = Join-Path $here 'tools\gui\Dismiss-TiaDialog.ps1'

$ok = 0; $fail = @()
foreach ($item in $slice) {
    if (Test-Path $dismiss) { & $dismiss | Out-Null }
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    if ($item.Kind -eq 'LAD') {
        & $exe "--write-lad:$($item.Path)" | Out-Null
        $out = & $exe "--project:$proj" "--plc:$Plc" "--import-block:$($item.Gen)" 2>&1 | Out-String
    } else {
        $out = & $exe "--project:$proj" "--plc:$Plc" "--import-scl:$($item.Path)" 2>&1 | Out-String
    }
    $exit = $LASTEXITCODE
    $ErrorActionPreference = $prevEap
    if ($exit -ne 0 -or $out -match 'Cannot|Error when|failed') {
        if (Test-Path $dismiss) { & $dismiss | Out-Null }
        $fail += $item
        Write-Host ("FAIL [{0}] {1}" -f $item.Kind, $item.Name) -ForegroundColor Red
        break
    }
    $ok++
    Write-Host ("OK   [{0}] {1}" -f $item.Kind, $item.Name)
    if ($DelaySeconds -gt 0) { Start-Sleep -Seconds $DelaySeconds }
}

if ($Compile -and $fail.Count -eq 0) {
    Write-Host '=== compile ===' -ForegroundColor Cyan
    & $exe "--project:$proj" "--plc:$Plc" --compile 2>&1 | Select-String 'errors:|warnings:|Compiling finished|Success'
}

if ($Save -and $fail.Count -eq 0) {
    & $exe "--project:$proj" --save | Out-Null
    Write-Host 'saved'
}

$next = if ($fail.Count -gt 0) { $Start + $ok } else { $end + 1 }
$state = @{
    lastPlc = $Plc
    lastKind = $Kind
    lastEnd = $end
    nextStart = $next
    total = $total
    ok = $ok
    fail = @($fail | ForEach-Object { "$($_.Kind):$($_.Name)" })
    updated = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
}
$state | ConvertTo-Json | Set-Content -LiteralPath $stateFile -Encoding UTF8

Write-Host ("done: ok=$ok fail=$($fail.Count)") -ForegroundColor Green
if ($fail.Count -gt 0) {
    Write-Host 'Stopped early. Close TIA block tabs, click OK on resource warning, then -Continue'
} elseif ($next -lt $total) {
    Write-Host ("next: .\Import-Practice-Batch.ps1 -Continue -BatchSize $BatchSize -DelaySeconds $DelaySeconds")
    Write-Host 'Add -Compile -Save on the last batch only.'
} else {
    Write-Host 'all blocks processed for this Kind queue on this PLC'
}
