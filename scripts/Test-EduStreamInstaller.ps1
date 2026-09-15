[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$InstallerPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\EduStream.Desktop_is1'
if (Test-Path -LiteralPath $uninstallKey) { throw 'An EduStream installation exists. Run this test on an isolated Windows account.' }
$repo = Split-Path -Parent $PSScriptRoot
$testRoot = [IO.Path]::GetFullPath((Join-Path $repo ('artifacts\installer-smoke-' + [guid]::NewGuid().ToString('N'))))
$programs = [Environment]::GetFolderPath('Programs')
if (Test-Path -LiteralPath (Join-Path $programs 'EduStream')) {
    throw 'An existing EduStream Start Menu folder exists. Use an isolated Windows account.'
}
$desktop = [Environment]::GetFolderPath('Desktop')
$desktopHashes = @{}
foreach ($name in @('EduStream 교수자.lnk', 'EduStream 학생.lnk')) {
    $path = Join-Path $desktop $name
    if (Test-Path -LiteralPath $path) { $desktopHashes[$name] = (Get-FileHash -LiteralPath $path).Hash }
}
$shell = New-Object -ComObject WScript.Shell
try {
    foreach ($type in @('student', 'professor', 'both')) {
        $install = Join-Path $testRoot $type
        $group = 'EduStream'
        $groupFolder = Join-Path $programs $group
        New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
        $log = Join-Path $testRoot ($type + '-install.log')
        $roles = switch ($type) {
            'student' { @('Client') }
            'professor' { @('Server') }
            'both' { @('Server', 'Client') }
        }
        try {
            $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/NOCLOSEAPPLICATIONS',
                ('/DIR="' + $install + '"'), ('/TYPE=' + $type), '/TASKS=""', ('/LOG="' + $log + '"'))
            $process = Start-Process -FilePath $installer -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
            if ($process.ExitCode -ne 0) { throw "Install failed ($type): $($process.ExitCode)" }
            if (-not (Test-Path -LiteralPath $uninstallKey)) { throw 'Uninstall registration missing.' }
            foreach ($role in @('Server', 'Client')) {
                $exe = Join-Path $install ("$role\EduStream.$role.exe")
                if ($role -notin $roles) {
                    if (Test-Path -LiteralPath $exe) { throw "Unselected role was installed: $role" }
                    continue
                }
                if (-not (Test-Path -LiteralPath $exe)) { throw "Executable missing: $exe" }
                $config = Get-Content -LiteralPath (Join-Path $install "$role\EduStream.$role.runtimeconfig.json") -Raw | ConvertFrom-Json
                foreach ($framework in $config.runtimeOptions.includedFrameworks) {
                    if ($framework.version -ne '8.0.31') { throw 'Unexpected bundled runtime version.' }
                }
                $label = if ($role -eq 'Server') { 'EduStream 교수자' } else { 'EduStream 학생' }
                if (-not (Test-Path -LiteralPath (Join-Path $groupFolder "$label.lnk"))) {
                    Get-Content -LiteralPath $log | Select-String 'icon|shortcut|group|아이콘|바로'
                    throw "Start Menu shortcut missing in $groupFolder"
                }
                $link = $shell.CreateShortcut((Join-Path $groupFolder "$label.lnk"))
                try {
                    if ($link.TargetPath -ne $exe -or $link.WorkingDirectory.TrimEnd('\') -ne (Split-Path $exe)) {
                        throw "Start Menu shortcut mismatch. Target=$($link.TargetPath); Working=$($link.WorkingDirectory); Expected=$exe"
                    }
                }
                finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
                $app = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) -WindowStyle Hidden -PassThru
                try {
                    if (-not $app.WaitForInputIdle(15000) -or $app.HasExited) { throw "GUI startup failed: $role" }
                }
                finally {
                    if (-not $app.HasExited) {
                        [void]$app.CloseMainWindow()
                        if (-not $app.WaitForExit(3000)) { $app.Kill(); $app.WaitForExit() }
                    }
                    $app.Dispose()
                }
            }
            if ($type -eq 'both') {
                $update = Start-Process -FilePath $installer -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
                if ($update.ExitCode -ne 0) { throw 'Reinstallation failed.' }
                Write-Host 'REINSTALL=PASS'
            }
            Write-Host "INSTALL_ROLE_AND_GUI=PASS $type"
        }
        finally {
            $uninstaller = Join-Path $install 'unins000.exe'
            if (Test-Path -LiteralPath $uninstaller) {
                $resolved = (Resolve-Path -LiteralPath $uninstaller).Path
                if (-not $resolved.StartsWith($testRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Refusing to uninstall outside the test directory.'
                }
                $uninstall = Start-Process -FilePath $resolved -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -WindowStyle Hidden -PassThru -Wait
                if ($uninstall.ExitCode -ne 0) { throw "Uninstall failed: $($uninstall.ExitCode)" }
            }
        }
        if (Test-Path -LiteralPath $uninstallKey) { throw 'Uninstall registry entry remained.' }
        foreach ($role in $roles) {
            if (Test-Path -LiteralPath (Join-Path $install "$role\EduStream.$role.exe")) { throw 'Installed executable remained after uninstall.' }
        }
        if (Test-Path -LiteralPath $groupFolder) { throw 'Test Start Menu group remained after uninstall.' }
        Write-Host "UNINSTALL=PASS $type"
    }
    foreach ($name in @('EduStream 교수자.lnk', 'EduStream 학생.lnk')) {
        $path = Join-Path $desktop $name
        if ($desktopHashes.ContainsKey($name)) {
            if ((Get-FileHash -LiteralPath $path).Hash -ne $desktopHashes[$name]) { throw 'Existing desktop shortcut changed.' }
        } elseif (Test-Path -LiteralPath $path) { throw 'Unexpected desktop shortcut created by smoke test.' }
    }
    Write-Host 'EXISTING_DESKTOP_PRESERVED=PASS'
}
finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
