# One block, one command: generate the LAD from its spec, import it into the
# practice PLC, compile, then compare the instruction mix against the 23019
# block of the same name so drift is caught immediately.
param(
    [Parameter(Mandatory = $true)][string]$Block,
    [string]$Project = $env:TIA_PRACTICE_PROJECT,
    [string]$Plc = 'PRACTICE_1215',
    [string]$RefPlc = 'PLC_KP_23019_TF',
    [switch]$SkipCompare
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$here = $root
$dataRoot = $here
$exe = Join-Path $here 'host\TiaOpennessCheck.exe'
if ([string]::IsNullOrWhiteSpace($Project)) {
    throw '請指定練習專案：-Project <絕對路徑.ap21> 或環境變數 TIA_PRACTICE_PROJECT'
}
$project = $Project
$plc = $Plc
$spec = Join-Path $dataRoot "Practice\$Block.lad.xml"
$generated = Join-Path $dataRoot "Practice\$Block.lad.generated.xml"
$roundTrip = Join-Path $dataRoot "Practice\$Block.exported.xml"

if (-not (Test-Path $spec)) { throw "找不到規格：$spec" }

function Run($label, $arguments) {
    $output = & $exe @arguments 2>&1 | Out-String
    return $output
}

Write-Host "=== $Block ===" -ForegroundColor Cyan

$out = Run 'generate' @("--write-lad:$spec")
if ($out -notmatch 'LAD XML') { Write-Host $out; throw '產生失敗' }
Write-Host '  [1/4] 產生 XML'

$out = Run 'import' @("--project:$project", "--plc:$plc", "--import-block:$generated")
if ($out -match 'Cannot|Error when') {
    ($out -split "`n" | Select-String -Pattern 'Cannot|Error when|line number' | Select-Object -First 3) | ForEach-Object { Write-Host ('      ' + $_.Line.Trim()) -ForegroundColor Red }
    throw '匯入失敗'
}
Write-Host '  [2/4] 匯入成功'

$out = Run 'compile' @("--project:$project", "--plc:$plc", "--compile")
  if ($out -match 'errors: 0' -or $out -match '已匯入') {
    # Prefer an explicit compile line when present.
  }
  if ($out -match 'errors: ([0-9]+); warnings: ([0-9]+)') {
    $errors = [int]$Matches[1]
    $warnings = [int]$Matches[2]
    if ($errors -gt 0) {
      foreach ($l in ($out -split "`r?`n")) {
        if ($l -match '\bError\b' -and $l -notmatch 'Compiling finished') { Write-Host ('      ' + $l.Trim()) -ForegroundColor Red }
      }
      throw 'compile failed'
    }
    Write-Host ("  [3/4] compile 0 errors / $warnings warnings")
  }
  elseif ($out -match 'Block was successfully compiled' -or $out -match 'Success') {
    Write-Host '  [3/4] compile ok'
  }
  else { Write-Host $out; throw 'compile result missing' }

if ($SkipCompare) { return }

# Compare the instruction multiset with the reference implementation.
$refPath = Join-Path $here "HmiExport\Templates\23019KP_B5_TCP_V21\$RefPlc\Blocks\$Block.xml"
if (-not (Test-Path $refPath)) {
    Write-Host '  [4/4] 沒有參考檔可比對' -ForegroundColor Yellow
    return
}

Run 'export' @("--project:$project", "--plc:$plc", "--export-block:$Block", "--to:$roundTrip") | Out-Null
if (-not (Test-Path $roundTrip)) {
    Write-Host '  [4/4] 匯出失敗，略過比對' -ForegroundColor Yellow
    return
}

function Tally($path) {
    $text = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    $counts = @{}
    foreach ($m in [regex]::Matches($text, '<Part Name="([^"]+)"')) {
        $counts[$m.Groups[1].Value] = [int]$counts[$m.Groups[1].Value] + 1
    }
    foreach ($m in [regex]::Matches($text, '<CallInfo Name="([^"]+)"')) {
        $k = 'CALL:' + $m.Groups[1].Value
        $counts[$k] = [int]$counts[$k] + 1
    }
    return $counts
}

$a = Tally $refPath
$b = Tally $roundTrip
$keys = ($a.Keys + $b.Keys) | Sort-Object -Unique
$diff = 0
foreach ($k in $keys) {
    $x = [int]$a[$k]; $y = [int]$b[$k]
    if ($x -ne $y) { $diff++; Write-Host ("      {0,-18} 23019={1,-4} 我的={2}" -f $k, $x, $y) -ForegroundColor Yellow }
}
if ($diff -eq 0) { Write-Host '  [4/4] 指令組成與 23019 完全一致' -ForegroundColor Green }
else { Write-Host "  [4/4] 指令組成有 $diff 項不同（若參考用 SCL 網路則屬預期）" -ForegroundColor Yellow }
