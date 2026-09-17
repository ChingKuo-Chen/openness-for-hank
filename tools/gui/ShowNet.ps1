param(
    [Parameter(Mandatory = $true)][string]$File,
    [int]$Index = 0,
    [string]$Contains,
    [int]$MaxNets = 1
)

# Prints one FlgNet from an exported LAD block so the wiring of a specific
# instruction can be copied verbatim instead of reconstructed from memory.

Add-Type -AssemblyName System.Xml.Linq

if (-not (Test-Path $File)) {
    if (-not $env:TIA_HMI_EXPORT_TEMPLATES) {
        Write-Error "not found: $File（可設環境變數 TIA_HMI_EXPORT_TEMPLATES 指向匯出 Templates 根目錄）"
        exit 1
    }
    $hit = Get-ChildItem -Path $env:TIA_HMI_EXPORT_TEMPLATES `
        -Recurse -File -Filter $File | Select-Object -First 1
    if (-not $hit) { Write-Error "not found: $File"; exit 1 }
    $File = $hit.FullName
}

$doc  = [System.Xml.Linq.XDocument]::Load($File)
$nets = @($doc.Descendants() | Where-Object { $_.Name.LocalName -eq 'FlgNet' })

Write-Host "file : $File"
Write-Host "nets : $($nets.Count)"

$shown = 0
for ($i = 0; $i -lt $nets.Count; $i++) {
    $xml = $nets[$i].ToString()
    if ($Contains -and $xml -notmatch [regex]::Escape($Contains)) { continue }
    if (-not $Contains -and $i -ne $Index) { continue }

    Write-Host ''
    Write-Host "---------- network #$i ----------"
    Write-Host $xml

    $shown++
    if ($shown -ge $MaxNets) { break }
}

if ($shown -eq 0) { Write-Host 'no matching network' }
