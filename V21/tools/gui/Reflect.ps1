# Dump the real member list of an Openness type instead of guessing it.
# Guessing attribute and property names is the single biggest time sink when
# working against this API, and the compiler only catches it one member at a time.
#
#   .\Reflect.ps1 IoController
#   .\Reflect.ps1 Device, DeviceItem
#   .\Reflect.ps1 -Match 'Io.*'

param(
    [Parameter(Position = 0)][string[]]$TypeName,
    [string]$Match,
    [string]$ApiFolder = 'C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48'
)

$assemblies = @(
    'Siemens.Engineering.Base.dll',
    'Siemens.Engineering.Step7.dll',
    'Siemens.Engineering.WinCC.dll',
    'Siemens.Engineering.WinCC.Extension.dll'
)

$types = @()
foreach ($name in $assemblies) {
    $path = Join-Path $ApiFolder $name
    if (-not (Test-Path $path)) { continue }

    $asm = [Reflection.Assembly]::LoadFrom($path)
    # These assemblies reference types that cannot be resolved outside TIA,
    # so a partial type list is the best we can get - and it is enough.
    try {
        $types += $asm.GetTypes()
    }
    catch [Reflection.ReflectionTypeLoadException] {
        $types += $_.Exception.Types | Where-Object { $_ -ne $null }
    }
}

Write-Host "loaded $($types.Count) types from $ApiFolder" -ForegroundColor DarkGray

if ($Match) {
    $types | Where-Object { $_.Name -match $Match } |
        Sort-Object Name -Unique |
        ForEach-Object { '{0,-40} {1}' -f $_.Name, $_.Namespace }
    return
}

foreach ($wanted in $TypeName) {
    $type = $types | Where-Object { $_.Name -eq $wanted } | Select-Object -First 1

    Write-Host "=== $wanted ===" -ForegroundColor Cyan
    if (-not $type) {
        Write-Host '  not found (try -Match)' -ForegroundColor Yellow
        continue
    }

    $chain = @()
    $b = $type.BaseType
    while ($b -and $b.Name -ne 'Object') { $chain += $b.Name; $b = $b.BaseType }
    if ($chain) { Write-Host ('  base: ' + ($chain -join ' <- ')) -ForegroundColor DarkGray }
    $ifaces = $type.GetInterfaces() | ForEach-Object { $_.Name }
    if ($ifaces) { Write-Host ('  impl: ' + ($ifaces -join ', ')) -ForegroundColor DarkGray }

    $type.GetProperties() | ForEach-Object {
        '  P {0,-32} : {1}' -f $_.Name, $_.PropertyType.Name
    }
    # Inherited members matter here: PlugNew lives on HardwareObject, not on
    # Device or DeviceItem, so filtering by DeclaringType hides the very method
    # you are looking for.
    $type.GetMethods() |
        Where-Object { -not $_.IsSpecialName -and $_.DeclaringType.Name -ne 'Object' } |
        ForEach-Object {
            '  M {0}({1}) : {2}   [{3}]' -f $_.Name,
                (($_.GetParameters() | ForEach-Object { $_.ParameterType.Name + ' ' + $_.Name }) -join ', '),
                $_.ReturnType.Name,
                $_.DeclaringType.Name
        }
}
