# Renders an exported LAD block as readable rungs so the logic can be understood
# and re-authored as a generator spec. Read-only: it never writes SimaticML.
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [int]$Network = 0
)

$ErrorActionPreference = 'Stop'
[xml]$doc = Get-Content -LiteralPath $Path -Raw -Encoding UTF8

function Get-Text($node, $composition) {
    if (-not $node) { return '' }
    $t = $node.SelectSingleNode(".//*[local-name()='MultilingualText'][@CompositionName='$composition']//*[local-name()='Text']")
    if ($t) { return $t.InnerText.Trim() }
    return ''
}

$block = $doc.SelectSingleNode("//*[local-name()='SW.Blocks.FB' or local-name()='SW.Blocks.FC' or local-name()='SW.Blocks.OB']")
Write-Host ("BLOCK  " + $block.LocalName.Replace('SW.Blocks.', '') + "  " +
    $block.SelectSingleNode("*[local-name()='AttributeList']/*[local-name()='Name']").InnerText)

Write-Host ''
Write-Host '--- INTERFACE ---'
$block.SelectNodes("*[local-name()='AttributeList']/*[local-name()='Interface']/*[local-name()='Sections']/*[local-name()='Section']") |
    ForEach-Object {
        $members = $_.SelectNodes("*[local-name()='Member']")
        if ($members.Count -eq 0) { return }
        Write-Host ("  [" + $_.Name + "]")
        foreach ($m in $members) {
            $sv = $m.SelectSingleNode("*[local-name()='StartValue']")
            $init = if ($sv) { '  := ' + $sv.InnerText } else { '' }
            Write-Host ("     {0,-26} {1}{2}" -f $m.Name, $m.Datatype, $init)
        }
    }

# Resolve an Access/Call part UId into readable text.
function Describe-Operand($net, $uid) {
    $a = $net.SelectSingleNode("*[local-name()='Parts']/*[local-name()='Access'][@UId='$uid']")
    if (-not $a) { return "#$uid" }
    $scope = $a.Scope
    $sym = $a.SelectSingleNode("*[local-name()='Symbol']")
    if ($sym) {
        $parts = $sym.SelectNodes("*[local-name()='Component']") | ForEach-Object {
            $idx = $_.SelectNodes(".//*[local-name()='ConstantValue']") | ForEach-Object { $_.InnerText }
            if ($idx) { $_.Name + '[' + ($idx -join ',') + ']' } else { $_.Name }
        }
        $text = $parts -join '.'
        if ($scope -eq 'GlobalVariable') { return '"' + $text + '"' }
        return '#' + $text
    }
    $c = $a.SelectSingleNode("*[local-name()='Constant']/*[local-name()='ConstantValue']")
    if ($c) { return $c.InnerText }
    return "?$uid"
}

$units = $doc.SelectNodes("//*[local-name()='SW.Blocks.CompileUnit']")
$i = 0
foreach ($u in $units) {
    $i++
    if ($Network -gt 0 -and $i -ne $Network) { continue }

    $net = $u.SelectSingleNode(".//*[local-name()='FlgNet']")
    Write-Host ''
    Write-Host ("=== NETWORK {0}: {1}" -f $i, (Get-Text $u 'Title'))
    $cmt = Get-Text $u 'Comment'
    if ($cmt) { Write-Host ("    // " + $cmt) }
    if (-not $net) { Write-Host '    (not LAD)'; continue }

    # Index every connection so each part can report what feeds it.
    $incoming = @{}
    $outgoing = @{}
    foreach ($w in $net.SelectNodes("*[local-name()='Wires']/*[local-name()='Wire']")) {
        $kids = @($w.ChildNodes)
        $src = $kids[0]
        for ($k = 1; $k -lt $kids.Count; $k++) {
            $dst = $kids[$k]
            $srcText = switch ($src.LocalName) {
                'Powerrail' { '<rail>' }
                'IdentCon'  { Describe-Operand $net $src.UId }
                'NameCon'   { '@' + $src.UId + '.' + $src.Name }
                'OpenCon'   { '<open>' }
                default     { $src.LocalName }
            }
            if ($dst.LocalName -eq 'NameCon') {
                $key = $dst.UId
                if (-not $incoming.ContainsKey($key)) { $incoming[$key] = @() }
                $incoming[$key] += ($dst.Name + '=' + $srcText)
            }
            elseif ($dst.LocalName -eq 'IdentCon' -and $src.LocalName -eq 'NameCon') {
                $key = $src.UId
                if (-not $outgoing.ContainsKey($key)) { $outgoing[$key] = @() }
                $outgoing[$key] += ($src.Name + '->' + (Describe-Operand $net $dst.UId))
            }
        }
    }

    foreach ($p in $net.SelectNodes("*[local-name()='Parts']/*")) {
        if ($p.LocalName -eq 'Access') { continue }

        $uid = $p.UId
        $label = if ($p.LocalName -eq 'Call') {
            $ci = $p.SelectSingleNode("*[local-name()='CallInfo']")
            $inst = $ci.SelectNodes("*[local-name()='Instance']//*[local-name()='Component']") | ForEach-Object { $_.Name }
            'CALL ' + $ci.Name + '(' + $ci.BlockType + ')' + $(if ($inst) { ' inst=' + ($inst -join '.') } else { '' })
        }
        else {
            $n = $p.Name
            $ver = if ($p.Version) { ' v' + $p.Version } else { '' }
            $neg = $p.SelectNodes("*[local-name()='Negated']") | ForEach-Object { $_.Name }
            $tmpl = $p.SelectNodes("*[local-name()='TemplateValue']") | ForEach-Object { $_.Name + '=' + $_.InnerText }
            $inst = $p.SelectNodes("*[local-name()='Instance']//*[local-name()='Component']") | ForEach-Object { $_.Name }
            $n + $ver +
                $(if ($neg) { ' NOT(' + ($neg -join ',') + ')' } else { '' }) +
                $(if ($tmpl) { ' {' + ($tmpl -join ' ') + '}' } else { '' }) +
                $(if ($inst) { ' inst=' + ($inst -join '.') } else { '' })
        }

        $in = if ($incoming.ContainsKey($uid)) { $incoming[$uid] -join '  ' } else { '' }
        $out = if ($outgoing.ContainsKey($uid)) { $outgoing[$uid] -join '  ' } else { '' }
        Write-Host ("  [{0,3}] {1,-34} {2}" -f $uid, $label, ($in + $(if ($out) { '   >> ' + $out } else { '' })))
    }
}
