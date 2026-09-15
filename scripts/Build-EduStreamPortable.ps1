[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Output already exists: $output" }
$payload = Join-Path $output 'payload'
& "$PSScriptRoot/Publish-EduStreamDesktop.ps1" -OutputDirectory $payload
& "$PSScriptRoot/Test-EduStreamDesktop.ps1" -PackageDirectory $payload
$portable = Join-Path $output 'EduStream'
[void](New-Item -ItemType Directory -Path $portable)

# 두 앱의 공통 런타임은 내용이 동일할 때만 공유한다. 충돌을 덮어쓰지 않는다.
foreach ($role in @('Server', 'Client')) {
    $source = Join-Path $payload $role
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
        $relative = $file.FullName.Substring($source.Length + 1)
        if ($relative -eq "EduStream.$role.exe") {
            $relative = if ($role -eq 'Server') { 'EduStream 교수자.exe' } else { 'EduStream 학생.exe' }
        }
        $target = Join-Path $portable $relative
        if (Test-Path -LiteralPath $target) {
            if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
                throw "Conflicting published file: $relative"
            }
        }
        else {
            [void](New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force)
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
}
Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'installer/PORTABLE_README.txt') -Destination (Join-Path $portable '먼저 읽어주세요.txt')
$zip = Join-Path $output 'EduStream-Portable-win-x64.zip'
Compress-Archive -LiteralPath $portable -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($zip + '.sha256'), "$hash  $([IO.Path]::GetFileName($zip))`r`n", [Text.Encoding]::ASCII)
Write-Host "PORTABLE_ZIP=$zip"
Write-Host "SHA256=$hash"
