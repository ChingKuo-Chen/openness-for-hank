$ErrorActionPreference = 'Stop'

$taskProjectFolder = Split-Path -Parent $MyInvocation.MyCommand.Path
$taskCompiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskApiFolder = 'C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48'
$taskBaseDll = Join-Path $taskApiFolder 'Siemens.Engineering.Base.dll'
$taskStep7Dll = Join-Path $taskApiFolder 'Siemens.Engineering.Step7.dll'
$taskWinccDll = Join-Path $taskApiFolder 'Siemens.Engineering.WinCC.dll'
$taskWinccExtDll = Join-Path $taskApiFolder 'Siemens.Engineering.WinCC.Extension.dll'
$taskXmlLinqDll = Join-Path (Split-Path -Parent $taskCompiler) 'System.Xml.Linq.dll'
$taskSource = @(Get-ChildItem -LiteralPath $taskProjectFolder -Filter '*.cs' -File |
    Sort-Object Name | Select-Object -ExpandProperty FullName)
$taskOutput = Join-Path $taskProjectFolder 'TiaOpennessCheck.exe'

if (-not (Test-Path -LiteralPath $taskCompiler)) {
    throw "找不到 .NET Framework C# 編譯器：$taskCompiler"
}

if (-not (Test-Path -LiteralPath $taskBaseDll)) {
    throw "找不到 TIA Portal V21 Openness DLL：$taskBaseDll"
}

foreach ($taskReference in @($taskStep7Dll, $taskWinccDll, $taskWinccExtDll, $taskXmlLinqDll)) {
    if (-not (Test-Path -LiteralPath $taskReference)) {
        throw "找不到編譯參考：$taskReference"
    }
}

& $taskCompiler `
    /nologo `
    /target:exe `
    /platform:x64 `
    /optimize+ `
    /utf8output `
    "/reference:$taskBaseDll" `
    "/reference:$taskStep7Dll" `
    "/reference:$taskWinccDll" `
    "/reference:$taskWinccExtDll" `
    "/reference:$taskXmlLinqDll" `
    "/out:$taskOutput" `
    $taskSource

if ($LASTEXITCODE -ne 0) {
    throw "編譯失敗，csc.exe 結束代碼：$LASTEXITCODE"
}

Write-Host "編譯成功：$taskOutput" -ForegroundColor Green

# TIA Portal V21 identifies an Openness application by its full path,
# LastWriteTimeUtc, and SHA-256 hash.  Refreshing this exact AllowList entry
# after every build prevents a new TIA firewall dialog for this executable.
$taskAllowListRoot = 'HKLM:\SOFTWARE\Siemens\Automation\Openness\AllowList'
$taskApplicationKey = Join-Path $taskAllowListRoot ([System.IO.Path]::GetFileName($taskOutput))
$taskAllowListEntry = Join-Path $taskApplicationKey 'Entry (Build)'

try {
    $taskOutputInfo = Get-Item -LiteralPath $taskOutput
    $taskDateModified = $taskOutputInfo.LastWriteTimeUtc.ToString(
        'yyyy/MM/dd HH:mm:ss.fff',
        [System.Globalization.CultureInfo]::InvariantCulture)
    $taskSha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $taskFileHash = [Convert]::ToBase64String(
            $taskSha256.ComputeHash([System.IO.File]::ReadAllBytes($taskOutput)))
    }
    finally {
        $taskSha256.Dispose()
    }

    New-Item -Path $taskApplicationKey -Force | Out-Null
    New-Item -Path $taskAllowListEntry -Force | Out-Null
    New-ItemProperty -LiteralPath $taskAllowListEntry -Name Path -Value $taskOutput -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $taskAllowListEntry -Name DateModified -Value $taskDateModified -PropertyType String -Force | Out-Null
    New-ItemProperty -LiteralPath $taskAllowListEntry -Name FileHash -Value $taskFileHash -PropertyType String -Force | Out-Null
    Write-Host ("Updated TIA Openness AllowList: " + $taskOutput) -ForegroundColor Green
}
catch {
    Write-Warning ('Cannot update TIA Openness AllowList: ' + $_.Exception.Message)
    Write-Warning 'Run Build.ps1 once as Administrator to write the HKLM AllowList entry.'
}
