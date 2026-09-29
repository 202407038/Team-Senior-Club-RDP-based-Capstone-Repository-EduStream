[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(-[A-Za-z0-9.]+)?$')][string]$Version = '0.1.0-preview.1',
    [string]$IsccPath,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
if (-not $IsccPath) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $repo 'artifacts\tooling\inno\ISCC.exe')
    )
    $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw 'Install Inno Setup 6 on the build PC, or pass -IsccPath. End users do not need this tool.'
}
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo ("artifacts\installer\" + $Version) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Output already exists: $output" }
$payload = Join-Path $output 'payload'
& (Join-Path $PSScriptRoot 'Publish-EduStreamDesktop.ps1') -OutputDirectory $payload
& (Join-Path $PSScriptRoot 'Test-EduStreamDesktop.ps1') -PackageDirectory $payload
& $IsccPath /Q ("/DPackageDir=" + $payload) ("/DInstallerOutputDir=" + $output) ("/DAppVersion=" + $Version) ("/DFileVersion=" + $Version.Split('-')[0] + '.0') (Join-Path $repo 'installer\EduStream.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
$setup = Join-Path $output 'EduStream-Setup.exe'
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'Installer output missing.' }
$hash = Get-FileHash -LiteralPath $setup -Algorithm SHA256
($hash.Hash.ToLowerInvariant() + '  EduStream-Setup.exe') | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding Ascii
$hash
Write-Host "DISTRIBUTE_THIS_FILE: $setup"
