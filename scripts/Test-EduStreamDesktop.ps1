[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [string]$ShortcutDirectory,
    [switch]$LaunchSmokeTest
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$package = [IO.Path]::GetFullPath($PackageDirectory)
$apps = @(
    @{ Role = 'Server'; Name = 'EduStream 교수자' },
    @{ Role = 'Client'; Name = 'EduStream 학생' }
)
$shell = $null
try {
    if ($ShortcutDirectory) { $shell = New-Object -ComObject WScript.Shell }
    foreach ($app in $apps) {
        $folder = Join-Path $package $app.Role
        $exe = Join-Path $folder ("EduStream." + $app.Role + ".exe")
        foreach ($file in @($exe, (Join-Path $folder 'coreclr.dll'), (Join-Path $folder 'hostfxr.dll'))) {
            if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing: $file" }
        }
        $config = Get-Content -LiteralPath (Join-Path $folder ("EduStream." + $app.Role + ".runtimeconfig.json")) -Raw | ConvertFrom-Json
        if (-not $config.runtimeOptions.PSObject.Properties['includedFrameworks']) {
            throw ("Not self-contained: " + $app.Role)
        }
        if ($ShortcutDirectory) {
            $path = Join-Path $ShortcutDirectory ($app.Name + '.lnk')
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing shortcut: $path" }
            $link = $shell.CreateShortcut($path)
            try {
                if ($link.TargetPath -ne $exe -or $link.WorkingDirectory -ne $folder -or $link.Arguments) {
                    throw "Shortcut target/working directory/arguments mismatch: $path"
                }
            }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
        }
        if ($LaunchSmokeTest) {
            $process = Start-Process -FilePath $exe -WorkingDirectory $folder -WindowStyle Hidden -PassThru
            try {
                if (-not $process.WaitForInputIdle(15000) -or $process.HasExited) {
                    throw ("GUI startup failed: " + $app.Role)
                }
                Write-Host ("GUI_STARTUP=PASS " + $app.Role)
            }
            finally {
                # 테스트가 생성한 프로세스만 정리한다. 기존 실행 앱에는 접근하지 않는다.
                if (-not $process.HasExited) {
                    [void]$process.CloseMainWindow()
                    if (-not $process.WaitForExit(3000)) {
                        $process.Kill()
                        $process.WaitForExit()
                    }
                }
                $process.Dispose()
            }
        }
        Write-Host ("PACKAGE_AND_SHORTCUT=PASS " + $app.Role)
    }
    foreach ($file in @('AxRDPCOMAPILib.dll', 'RDPCOMAPILib.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $package "Client\$file") -PathType Leaf)) {
            throw "RDP interop missing: $file"
        }
    }
    Write-Host 'RDP_INTEROP=PASS'
}
finally {
    if ($null -ne $shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
