# Regenerate and reimport all practice LAD blocks, then SCL, compile both PLCs.
param(
    [string]$Project = $env:TIA_PRACTICE_PROJECT
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
$sclDir = Join-Path $dataRoot 'Practice\Scl'
$ladDir = Join-Path $dataRoot 'Practice'

Write-Host '=== annotate ===' -ForegroundColor Cyan
& $exe --annotate-practice

Write-Host '=== generate + import LAD ===' -ForegroundColor Cyan
$fail = @()
$specs = Get-ChildItem -LiteralPath $ladDir -Filter '*.lad.xml' | Sort-Object Name
foreach ($spec in $specs) {
    $name = $spec.BaseName -replace '\.lad$',''
    $gen = Join-Path $ladDir ($name + '.lad.generated.xml')
    & $exe "--write-lad:$($spec.FullName)" | Out-Null
    foreach ($plc in @('PRACTICE_1215','Main')) {
        $out = & $exe "--project:$proj" "--plc:$plc" "--import-block:$gen" 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0 -or $out -match 'Cannot|Error when|Import failed') {
            $fail += "$name@$plc"
            Write-Host "FAIL $name $plc" -ForegroundColor Red
        }
    }
}
Write-Host ("LAD blocks: $($specs.Count), import fail: $($fail.Count)")

foreach ($plc in @('PRACTICE_1215','Main')) {
    Write-Host "=== SCL -> $plc ===" -ForegroundColor Cyan
    & $exe "--project:$proj" "--plc:$plc" "--import-scl-dir:$sclDir"
    Write-Host "=== compile $plc ===" -ForegroundColor Cyan
    & $exe "--project:$proj" "--plc:$plc" --compile 2>&1 | Select-String 'errors:|warnings:|Compiling finished|Success'
}

& $exe "--project:$proj" --save | Out-Null
Write-Host '=== done ===' -ForegroundColor Green
