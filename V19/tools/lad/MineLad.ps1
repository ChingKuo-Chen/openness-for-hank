param(
    [Parameter(Mandatory = $true)]
    [string]$Root
)

# Mines exported LAD XML to find which TemplateValue entries each instruction
# actually carries. Replaces the rules that were previously guessed by trial and error.
# Pass -Root to a reference export tree (Blocks folders), not a hardcoded machine path.

$ErrorActionPreference = 'Stop'

$partRegex = [regex]'(?s)<Part\b([^>]*?)(/>|>(.*?)</Part>)'
$nameRegex = [regex]'Name="([^"]+)"'
$tvRegex = [regex]'<TemplateValue\s+Name="([^"]+)"\s+Type="([^"]+)"'

$combo = @{}
$scanned = 0

foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File -Filter *.xml) {
    $text = [IO.File]::ReadAllText($file.FullName)
    if ($text.IndexOf('FlgNet') -lt 0) { continue }
    $scanned++

    foreach ($m in $partRegex.Matches($text)) {
        $attrs = $m.Groups[1].Value
        $body = $m.Groups[3].Value

        $nm = $nameRegex.Match($attrs)
        if (-not $nm.Success) { continue }
        $instruction = $nm.Groups[1].Value

        $tvNames = @()
        foreach ($t in $tvRegex.Matches($body)) {
            $tvNames += ($t.Groups[1].Value + '=' + $t.Groups[2].Value)
        }

        $key = $instruction + '|' + (($tvNames | Sort-Object -Unique) -join ' + ')
        if ($combo.ContainsKey($key)) { $combo[$key]++ } else { $combo[$key] = 1 }
    }
}

Write-Host ("Files containing FlgNet: " + $scanned)
Write-Host ("Distinct instruction+TemplateValue combos: " + $combo.Count)
Write-Host ('-' * 66)

$combo.GetEnumerator() |
    Sort-Object Value -Descending |
    ForEach-Object {
        $parts = $_.Key.Split('|')
        $tv = $parts[1]
        if ($tv -eq '') { $tv = '(none)' }
        '{0,-18} {1,-38} x{2}' -f $parts[0], $tv, $_.Value
    }
