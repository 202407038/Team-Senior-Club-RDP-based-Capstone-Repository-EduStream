[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    # 이미 검증한 동일 버전의 publish 결과를 재포장할 때만 지정한다.
    [string]$PayloadDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Output already exists: $output" }
if ($PayloadDirectory) {
    $payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
}
else {
    $payload = Join-Path $output 'payload'
    & "$PSScriptRoot/Publish-EduStreamDesktop.ps1" -OutputDirectory $payload
}
& "$PSScriptRoot/Test-EduStreamDesktop.ps1" -PackageDirectory $payload

# 각 publish 폴더 전체를 복사한다. 학생의 역방향 공유용 ShareHost도 필요하다.
foreach ($role in @('Server', 'Client')) {
    $source = Join-Path $payload $role
    $other = if ($role -eq 'Server') { 'Client' } else { 'Server' }
    if (Get-ChildItem -LiteralPath $source -Recurse -File -Filter "EduStream.$other.*") {
        throw "Other role app found in $role payload"
    }
    $portable = Join-Path $output "$role/EduStream"
    [void](New-Item -ItemType Directory -Path $portable -Force)
    foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
        $relative = $file.FullName.Substring($source.Length + 1)
        if ($relative -eq "EduStream.$role.exe") {
            $relative = if ($role -eq 'Server') { 'EduStream 교수자.exe' } else { 'EduStream 학생.exe' }
        }
        $target = Join-Path $portable $relative
        [void](New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force)
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
    Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'installer/PORTABLE_README.txt') -Destination (Join-Path $portable '먼저 읽어주세요.txt')
    $zip = Join-Path $output "EduStream-$role-win-x64.zip"
    Compress-Archive -LiteralPath $portable -DestinationPath $zip -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($zip + '.sha256'), "$hash  $([IO.Path]::GetFileName($zip))`r`n", [Text.Encoding]::ASCII)
    Write-Host "PORTABLE_ZIP=$zip"
    Write-Host "SHA256=$hash"
}
