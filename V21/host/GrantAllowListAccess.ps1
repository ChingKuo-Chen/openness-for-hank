$ErrorActionPreference = 'Stop'

# TIA Portal only skips its Openness firewall dialog when the running executable
# matches an AllowList entry by path, timestamp and hash.  Every rebuild changes
# the hash, so Build.ps1 has to rewrite that entry.  Granting the engineering
# account write access to this one key once removes the elevation prompt from
# every later build.  The 64-bit registry view is requested explicitly because an
# elevated 32-bit host would otherwise be redirected to WOW6432Node.
$grantSubKey = 'SOFTWARE\Siemens\Automation\Openness\AllowList'
$grantLogPath = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'HmiExport\grant-allowlist.log'
$grantLogDir = Split-Path -Parent $grantLogPath
# New-Item -LiteralPath is PowerShell 7+; Windows PowerShell 5.1 needs -Path.
if (-not (Test-Path -LiteralPath $grantLogDir)) {
    New-Item -ItemType Directory -Path $grantLogDir -Force | Out-Null
}
$grantAccountName = $args[0]

function Write-GrantLog([string] $message) {
    Add-Content -LiteralPath $grantLogPath -Value $message -Encoding UTF8
}

Set-Content -LiteralPath $grantLogPath -Value ('start ' + (Get-Date -Format 'HH:mm:ss')) -Encoding UTF8

try {
    if ([string]::IsNullOrWhiteSpace($grantAccountName)) {
        $grantAccountName = "$env:USERDOMAIN\$env:USERNAME"
    }

    Write-GrantLog "account=$grantAccountName"
    Write-GrantLog "is64BitProcess=$([Environment]::Is64BitProcess)"

    $grantBaseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64)

    $grantRights =
        [System.Security.AccessControl.RegistryRights]::ReadPermissions -bor
        [System.Security.AccessControl.RegistryRights]::ChangePermissions

    $grantKey = $grantBaseKey.OpenSubKey(
        $grantSubKey,
        [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree,
        $grantRights)

    if ($null -eq $grantKey) {
        $grantKey = $grantBaseKey.CreateSubKey($grantSubKey)
        Write-GrantLog 'created missing AllowList key'
    }

    try {
        $grantSecurity = $grantKey.GetAccessControl(
            [System.Security.AccessControl.AccessControlSections]::Access)
        $grantRule = New-Object System.Security.AccessControl.RegistryAccessRule(
            $grantAccountName,
            [System.Security.AccessControl.RegistryRights]::FullControl,
            ([System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
             [System.Security.AccessControl.InheritanceFlags]::ObjectInherit),
            [System.Security.AccessControl.PropagationFlags]::None,
            [System.Security.AccessControl.AccessControlType]::Allow)
        $grantSecurity.AddAccessRule($grantRule)
        $grantKey.SetAccessControl($grantSecurity)
    }
    finally {
        $grantKey.Close()
        $grantBaseKey.Close()
    }

    Write-GrantLog 'OK'
    exit 0
}
catch {
    Write-GrantLog ('FAILED: ' + $_.Exception.GetType().FullName + ' :: ' + $_.Exception.Message)
    exit 1
}
