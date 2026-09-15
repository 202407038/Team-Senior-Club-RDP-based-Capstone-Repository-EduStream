[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [ValidatePattern('^8\.0\.\d+$')][string]$RuntimeVersion = '8.0.31',
    [switch]$CreateDesktopShortcuts,
    [string]$ShortcutDirectory = [Environment]::GetFolderPath('Desktop')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -ne 'Windows_NT') { throw 'This package requires Windows x64.' }
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
    $version = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $OutputDirectory = Join-Path $env:LOCALAPPDATA "EduStream\$version"
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Output already exists; choose a new folder: $output" }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK is required to prepare this package.' }

$apps = @(
    @{ Role = 'Server'; Name = 'EduStream 교수자' },
    @{ Role = 'Client'; Name = 'EduStream 학생' }
)
$shell = $null
try {
    # 기존 사용자 바로가기를 임의로 덮지 않는다. 이 스크립트가 만든 바로가기만 갱신한다.
    if ($CreateDesktopShortcuts) {
        if (-not (Test-Path -LiteralPath $ShortcutDirectory -PathType Container)) {
            throw "Shortcut directory does not exist: $ShortcutDirectory"
        }
        $shell = New-Object -ComObject WScript.Shell
        foreach ($app in $apps) {
            $link = Join-Path $ShortcutDirectory ($app.Name + '.lnk')
            if (Test-Path -LiteralPath $link) {
                $existing = $shell.CreateShortcut($link)
                try {
                    if ($existing.Description -ne ("EduStream managed launcher: " + $app.Role)) {
                        throw "An unmanaged shortcut already exists: $link"
                    }
                }
                finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($existing) }
            }
        }
    }

    foreach ($app in $apps) {
        $project = Join-Path $repo ("src\EduStream." + $app.Role + "\EduStream." + $app.Role + ".csproj")
        $target = Join-Path $output $app.Role
        & dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --output $target --nologo '-p:UseAppHost=true' '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' ("-p:RuntimeFrameworkVersion=" + $RuntimeVersion)
        if ($LASTEXITCODE -ne 0) { throw ("Publish failed: " + $app.Role) }
        foreach ($file in @(("EduStream." + $app.Role + ".exe"), 'coreclr.dll', 'hostfxr.dll')) {
            if (-not (Test-Path -LiteralPath (Join-Path $target $file) -PathType Leaf)) {
                throw "Published file missing: $target\$file"
            }
        }
    }
    foreach ($file in @('AxRDPCOMAPILib.dll', 'RDPCOMAPILib.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $output "Client\$file") -PathType Leaf)) {
            throw "RDP interop missing: $file"
        }
    }

    # 두 앱 게시가 모두 성공한 뒤에만 바로가기를 새 버전으로 연결한다.
    if ($CreateDesktopShortcuts) {
        foreach ($app in $apps) {
            $folder = Join-Path $output $app.Role
            $exe = Join-Path $folder ("EduStream." + $app.Role + ".exe")
            $shortcut = $shell.CreateShortcut((Join-Path $ShortcutDirectory ($app.Name + '.lnk')))
            try {
                $shortcut.TargetPath = $exe
                $shortcut.WorkingDirectory = $folder
                $shortcut.Arguments = ''
                $shortcut.Description = "EduStream managed launcher: " + $app.Role
                $shortcut.IconLocation = $exe + ',0'
                $shortcut.WindowStyle = 1
                $shortcut.Save()
            }
            finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
        }
    }
    Write-Host "Ready: $output"
    Write-Host 'Double-click EduStream.Server.exe or EduStream.Client.exe (or the desktop shortcuts).'
}
finally {
    if ($null -ne $shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}
