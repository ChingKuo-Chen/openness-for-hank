# Add block / member / network comments to Practice LAD specs and SCL sources.
# LadWriter emits comment= into TIA MultilingualText; SCL gets // header + REGION notes.
param(
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$here = $root
$dataRoot = $here
$ladDir = Join-Path $dataRoot 'Practice'
$sclDir = Join-Path $dataRoot 'Practice\Scl'
$sclDumpDir = Join-Path $dataRoot 'Practice\SclDump'

function Escape-XmlAttr([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) { return '' }
    return ($text -replace '&', '&amp;' -replace '"', '&quot;' -replace '<', '&lt;' -replace '\r?\n', '&#10;')
}

function Describe-Member([string]$section, [string]$name, [string]$type) {
    $map = @{
        enable = '使能'
        pause = '暫停'
        reset = '復位'
        ack = '確認'
        clear = '清除'
        SP = '設定值 Setpoint'
        PV = '程序值 Process Value'
        err = '偏差'
        out = '輸出'
        Kp = '比例增益'
        Ti = '積分時間常數（秒）'
        Td = '微分時間常數（秒）'
        Ts = '掃描週期（秒）'
        feed_fwd = '前饋量'
        Kf = '前饋增益'
        ctrl_inv = '反向控制（err = PV - SP）'
        s_in = '閃爍使能輸入'
        s_out = '閃爍輸出'
        tm_on = '導通時間'
        tm_off = '關斷時間'
        WM_lo = '低水位'
        WM_hi = '高水位'
        charge = '充水/泵運轉輸出'
        charge_tm = '低水位延時'
        run = '運轉'
        stop = '停止'
        estop = '急停'
        fault = '故障'
        ready = '就緒'
        valid = '資料有效'
        diff = '差值'
        accu = '累加值'
        prev_cnt = '上次計數'
        cur_cnt = '目前計數'
        bipolar = '雙向計數'
        linespeed = '線速度'
        finish_length = '完成長度'
        ramped_ref = '斜坡後參考（%）'
        ramp_dec = '減速斜坡'
        ramp_NStop = '停止斜坡'
        finishing_ref = '收尾參考（%）'
        Slow_at = '開始減速位置'
        Stop_at = '停止位置'
        pb = '按鈕'
        pb_edge = '按鈕上次狀態（邊緣偵測）'
        sw = '切換狀態'
        windup = '積分飽和（輸出到限幅）'
        at_limit = '輸出已達上下限'
    }
    if ($map.ContainsKey($name)) { return $map[$name] }
    $sec = switch ($section) {
        'Input' { '輸入' }
        'Output' { '輸出' }
        'InOut' { 'InOut' }
        'Static' { '靜態' }
        'Temp' { '暫存' }
        'Constant' { '常數' }
        default { $section }
    }
    return "$sec：$name（$type）"
}

function Get-SclDumpSummary([string]$blockName) {
    $dump = Join-Path $sclDumpDir ($blockName + '.txt')
    if (-not (Test-Path -LiteralPath $dump)) { return $null }
    $lines = Get-Content -LiteralPath $dump -Encoding UTF8
    $nets = @($lines | Where-Object { $_ -match '^=== NETWORK' })
    if ($nets.Count -gt 0) {
        return ('23019 參考：' + $nets.Count + ' 個邏輯段。詳見 Practice\SclDump\' + $blockName + '.txt')
    }
    return '23019 SCL 原版邏輯；可攜腳位練習版。'
}

function Annotate-LadFile([string]$path) {
    $raw = [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false))
    if ($raw -match '<Lad\b[^>]*\bcomment=') {
        return 'skip-already'
    }

    $blockComment = $null
    if ($raw -match '(?s)<!--\s*(.*?)\s*-->\s*<Lad') {
        $blockComment = ($matches[1] -replace '\s+', ' ').Trim()
    }
    if ([string]::IsNullOrWhiteSpace($blockComment)) {
        $base = [IO.Path]::GetFileNameWithoutExtension($path) -replace '\.lad$',''
        $blockComment = "練習區塊 $base（LAD）。可攜腳位，不依賴廠端全域標籤。"
    }

    if ($raw -notmatch '<Lad\b([^>]*)>') { throw "No Lad root: $path" }
    $ladAttrs = $matches[1]
    if ($ladAttrs -notmatch '\bcomment=') {
        $raw = $raw -replace '<Lad\b([^>]*)>', ('<Lad$1 comment="' + (Escape-XmlAttr $blockComment) + '">')
    }

    $section = 'Input'
    $raw = [regex]::Replace($raw, '<Section name="([^"]+)">', {
        param($m)
        $script:section = $m.Groups[1].Value
        return $m.Value
    })

    $raw = [regex]::Replace($raw, '<Member\b([^>]*)/>', {
        param($m)
        $attrs = $m.Groups[1].Value
        if ($attrs -match '\bcomment=') { return $m.Value }
        $nameMatch = [regex]::Match($attrs, '\bname="([^"]+)"')
        $typeMatch = [regex]::Match($attrs, '\btype="([^"]+)"')
        if (-not $nameMatch.Success -or -not $typeMatch.Success) { return $m.Value }
        $n = $nameMatch.Groups[1].Value
        $t = $typeMatch.Groups[1].Value
        $note = Escape-XmlAttr (Describe-Member $script:section $n $t)
        return '<Member' + $attrs + ' comment="' + $note + '" />'
    })

    $netIdx = 0
    $raw = [regex]::Replace($raw, '<Network\b([^>]*)>', {
        param($m)
        $script:netIdx++
        $attrs = $m.Groups[1].Value
        if ($attrs -match '\bcomment=') { return $m.Value }
        $title = ''
        if ($attrs -match '\btitle="([^"]+)"') { $title = $matches[1] }
        if ([string]::IsNullOrWhiteSpace($title)) { $title = "Network $($script:netIdx)" }
        $note = Escape-XmlAttr ("$($script:netIdx). $title")
        if ($attrs -match '\btitle=') {
            return '<Network' + $attrs + ' comment="' + $note + '">'
        }
        return '<Network title="' + (Escape-XmlAttr $title) + '" comment="' + $note + '">'
    })

    if (-not $WhatIf) {
        [IO.File]::WriteAllText($path, $raw, [Text.UTF8Encoding]::new($false))
    }
    return 'updated'
}

function Annotate-SclFile([string]$path) {
    $raw = [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false))
    if ($raw -match '(?m)^// =====') { return 'skip-already' }

    $blockName = $null
    $kind = 'FC'
    if ($raw -match 'FUNCTION_BLOCK\s+"([^"]+)"') { $kind = 'FB'; $blockName = $matches[1] }
    elseif ($raw -match 'FUNCTION\s+"([^"]+)"') { $blockName = $matches[1] }
    else { throw "Not SCL block: $path" }

    $summary = Get-SclDumpSummary $blockName
    if (-not $summary) { $summary = "練習用 SCL 區塊 $blockName。可攜腳位。" }

    $header = @(
        '// ============================================================================='
        "// 區塊：$blockName（$kind）"
        "// $summary"
        '// ============================================================================='
        ''
    ) -join "`r`n"

    $varComment = '// --- 介面腳位 ---'
    $raw = $raw -replace '(VAR_(?:INPUT|OUTPUT|IN_OUT|TEMP|CONSTANT)\s*)', ($varComment + "`r`n`$1")
    $raw = $raw -replace ('(' + [regex]::Escape($varComment) + '\r?\n){2,}', ($varComment + "`r`n"))

    $beginComment = @(
        ''
        '// --- 主要邏輯 ---'
        '// 依 23019 原版算法改寫；細節見 Practice\SclDump\' + $blockName + '.txt'
    ) -join "`r`n"
    $raw = $raw -replace '\r?\nBEGIN\r?\n', ($beginComment + "`r`nBEGIN`r`n")

    if ($raw -notmatch '(?m)^// =====') {
        $raw = $raw -replace '(FUNCTION(?:_BLOCK)?\s+"[^"]+"[^\r\n]*)', ($header + '$1')
    }

    if (-not $WhatIf) {
        [IO.File]::WriteAllText($path, $raw, [Text.UTF8Encoding]::new($false))
    }
    return 'updated'
}

$ladStats = @{ updated = 0; skip = 0; fail = 0 }
foreach ($file in Get-ChildItem -LiteralPath $ladDir -Filter '*.lad.xml' | Sort-Object Name) {
    try {
        $r = Annotate-LadFile $file.FullName
        if ($r -eq 'skip-already') { $ladStats.skip++ } else { $ladStats.updated++ }
        Write-Host ("LAD " + $r + " " + $file.Name)
    }
    catch {
        $ladStats.fail++
        Write-Warning ($file.Name + ': ' + $_.Exception.Message)
    }
}

$sclStats = @{ updated = 0; skip = 0; fail = 0 }
foreach ($file in Get-ChildItem -LiteralPath $sclDir -Filter '*.scl' | Sort-Object Name) {
    try {
        $r = Annotate-SclFile $file.FullName
        if ($r -eq 'skip-already') { $sclStats.skip++ } else { $sclStats.updated++ }
        Write-Host ("SCL " + $r + " " + $file.Name)
    }
    catch {
        $sclStats.fail++
        Write-Warning ($file.Name + ': ' + $_.Exception.Message)
    }
}

Write-Host ''
Write-Host ("LAD: updated $($ladStats.updated), skip $($ladStats.skip), fail $($ladStats.fail)")
Write-Host ("SCL: updated $($sclStats.updated), skip $($sclStats.skip), fail $($sclStats.fail)")
