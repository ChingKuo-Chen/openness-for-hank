param(
    [Parameter(Mandatory = $true)]
    [string]$Root,
    [Parameter(Mandatory = $true)]
    [string]$Out
)

# Derives the LAD XML rules from real exported blocks. Guessing port names or
# TemplateValue attributes produces import errors that are slow to diagnose,
# so every rule below comes from measured usage in the reference project.
# Pass -Root/-Out explicitly (team machines differ).

Add-Type -AssemblyName System.Xml.Linq

$files = Get-ChildItem -Path $Root -Recurse -File -Filter '*.xml' |
    Where-Object { $_.Directory.Name -eq 'Blocks' }

$parts     = @{}   # part name -> stats
$accesses  = @{}   # scope -> stats
$callInfos = @{}   # call type -> stats
$blockKind = @{}

function Get-Child([object]$node, [string]$local) {
    $node.Elements() | Where-Object { $_.Name.LocalName -eq $local }
}

$ladCount = 0

foreach ($file in $files) {
    $text = Get-Content $file.FullName -Raw -Encoding UTF8
    if ($text -notmatch '<ProgrammingLanguage>LAD<') { continue }
    $ladCount++

    try { $doc = [System.Xml.Linq.XDocument]::Parse($text) } catch { continue }

    $blockRoot = $doc.Root.Elements() | Where-Object { $_.Name.LocalName -like 'SW.Blocks.*' } | Select-Object -First 1
    if ($blockRoot) {
        $kind = $blockRoot.Name.LocalName
        if (-not $blockKind.ContainsKey($kind)) { $blockKind[$kind] = 0 }
        $blockKind[$kind]++
    }

    foreach ($net in $doc.Descendants() | Where-Object { $_.Name.LocalName -eq 'FlgNet' }) {

        $uidToName = @{}

        foreach ($p in $net.Descendants() | Where-Object { $_.Name.LocalName -eq 'Part' }) {
            $name = [string]$p.Attribute('Name').Value
            $uid  = [string]$p.Attribute('UId').Value
            $uidToName[$uid] = $name

            if (-not $parts.ContainsKey($name)) {
                $parts[$name] = [pscustomobject]@{
                    Count      = 0
                    Templates  = @{}
                    Attributes = @{}
                    Children   = @{}
                    Ports      = @{}
                    Example    = $file.Name
                }
            }
            $e = $parts[$name]
            $e.Count++

            foreach ($a in $p.Attributes()) {
                $key = $a.Name.LocalName
                if ($key -eq 'UId' -or $key -eq 'Name') { continue }
                $e.Attributes[$key] = [string]$a.Value
            }

            foreach ($c in $p.Elements()) {
                $local = $c.Name.LocalName
                if (-not $e.Children.ContainsKey($local)) { $e.Children[$local] = 0 }
                $e.Children[$local]++

                if ($local -eq 'TemplateValue') {
                    $tn = [string]$c.Attribute('Name').Value
                    $tt = [string]$c.Attribute('Type').Value
                    $e.Templates["$tn ($tt)"] = [string]$c.Value
                }
            }
        }

        # Calls to other blocks are a separate element, not a Part.
        foreach ($call in $net.Descendants() | Where-Object { $_.Name.LocalName -eq 'Call' }) {
            $uid = [string]$call.Attribute('UId').Value
            $info = Get-Child $call 'CallInfo' | Select-Object -First 1
            if (-not $info) { continue }

            $cname = [string]$info.Attribute('Name').Value
            $ctype = [string]$info.Attribute('BlockType').Value
            $uidToName[$uid] = "Call:$ctype"

            $key = "Call $ctype"
            if (-not $callInfos.ContainsKey($key)) {
                $callInfos[$key] = [pscustomobject]@{
                    Count = 0; Samples = @{}; Children = @{}; Ports = @{}; Example = $file.Name
                }
            }
            $ci = $callInfos[$key]
            $ci.Count++
            if ($ci.Samples.Count -lt 6) { $ci.Samples[$cname] = 1 }
            foreach ($c in $info.Elements()) {
                $local = $c.Name.LocalName
                if (-not $ci.Children.ContainsKey($local)) { $ci.Children[$local] = 0 }
                $ci.Children[$local]++
            }
        }

        foreach ($acc in $net.Descendants() | Where-Object { $_.Name.LocalName -eq 'Access' }) {
            $scope = [string]$acc.Attribute('Scope').Value
            if (-not $accesses.ContainsKey($scope)) {
                $accesses[$scope] = [pscustomobject]@{ Count = 0; Shapes = @{}; Example = $file.Name }
            }
            $a = $accesses[$scope]
            $a.Count++

            $shape = ($acc.Descendants() | ForEach-Object {
                $n = $_.Name.LocalName
                $attrs = ($_.Attributes() | ForEach-Object { $_.Name.LocalName }) -join ','
                if ($attrs) { "$n[$attrs]" } else { $n }
            }) -join ' > '
            if ($shape -and $a.Shapes.Count -lt 40) { $a.Shapes[$shape] = 1 }
        }

        foreach ($wire in $net.Descendants() | Where-Object { $_.Name.LocalName -eq 'Wire' }) {
            foreach ($con in $wire.Elements() | Where-Object { $_.Name.LocalName -eq 'NameCon' }) {
                $uid  = [string]$con.Attribute('UId').Value
                $port = [string]$con.Attribute('Name').Value
                $owner = $uidToName[$uid]
                if (-not $owner) { continue }

                if ($parts.ContainsKey($owner)) {
                    $tbl = $parts[$owner].Ports
                } elseif ($callInfos.ContainsKey($owner.Replace('Call:', 'Call '))) {
                    $tbl = $callInfos[$owner.Replace('Call:', 'Call ')].Ports
                } else {
                    continue
                }

                if (-not $tbl.ContainsKey($port)) { $tbl[$port] = 0 }
                $tbl[$port]++
            }
        }
    }
}

$sb = New-Object System.Text.StringBuilder
function Add-Line([string]$t) { [void]$sb.AppendLine($t) }

Add-Line "LAD cookbook derived from: $Root"
Add-Line "LAD block files analysed: $ladCount"
Add-Line ("Block kinds: " + (($blockKind.GetEnumerator() | Sort-Object Value -Descending |
    ForEach-Object { "$($_.Key) x$($_.Value)" }) -join ', '))
Add-Line ''
Add-Line '=================== ACCESS (operands) ==================='
foreach ($kv in $accesses.GetEnumerator() | Sort-Object { $_.Value.Count } -Descending) {
    Add-Line ''
    Add-Line ("Scope=$($kv.Key)   x$($kv.Value.Count)   e.g. $($kv.Value.Example)")
    foreach ($s in $kv.Value.Shapes.Keys | Sort-Object) { Add-Line "    $s" }
}

Add-Line ''
Add-Line '=================== PARTS (instructions) ==================='
foreach ($kv in $parts.GetEnumerator() | Sort-Object { $_.Value.Count } -Descending) {
    $p = $kv.Value
    Add-Line ''
    Add-Line ("{0,-20} x{1}   e.g. {2}" -f $kv.Key, $p.Count, $p.Example)

    if ($p.Attributes.Count) {
        Add-Line ("    attrs     : " + (($p.Attributes.GetEnumerator() | Sort-Object Key |
            ForEach-Object { "$($_.Key)=$($_.Value)" }) -join '  '))
    }
    if ($p.Templates.Count) {
        Add-Line ("    templates : " + (($p.Templates.GetEnumerator() | Sort-Object Key |
            ForEach-Object { "$($_.Key)=$($_.Value)" }) -join '  '))
    }
    $others = $p.Children.Keys | Where-Object { $_ -ne 'TemplateValue' }
    if ($others) { Add-Line ("    children  : " + ($others -join ', ')) }
    if ($p.Ports.Count) {
        Add-Line ("    ports     : " + (($p.Ports.GetEnumerator() | Sort-Object { $_.Value } -Descending |
            ForEach-Object { "$($_.Key)($($_.Value))" }) -join ' '))
    }
}

Add-Line ''
Add-Line '=================== CALLS ==================='
foreach ($kv in $callInfos.GetEnumerator() | Sort-Object { $_.Value.Count } -Descending) {
    $c = $kv.Value
    Add-Line ''
    Add-Line ("{0,-20} x{1}   e.g. {2}" -f $kv.Key, $c.Count, $c.Example)
    Add-Line ("    names     : " + (($c.Samples.Keys | Sort-Object) -join ', '))
    if ($c.Children.Count) {
        Add-Line ("    children  : " + (($c.Children.Keys | Sort-Object) -join ', '))
    }
    if ($c.Ports.Count) {
        Add-Line ("    ports     : " + (($c.Ports.GetEnumerator() | Sort-Object { $_.Value } -Descending |
            ForEach-Object { "$($_.Key)($($_.Value))" }) -join ' '))
    }
}

Set-Content -Path $Out -Value $sb.ToString() -Encoding UTF8
Write-Host "wrote $Out"
Write-Host "LAD files: $ladCount   parts: $($parts.Count)   access scopes: $($accesses.Count)   call kinds: $($callInfos.Count)"
