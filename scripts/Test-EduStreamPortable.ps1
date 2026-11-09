[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ZipPath,
    [ValidateSet('Combined', 'Server', 'Client')][string]$Role = 'Combined',
    [string]$PayloadDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$zip = (Resolve-Path -LiteralPath $ZipPath).Path
$expected = ((Get-Content -LiteralPath ($zip + '.sha256') -Raw).Trim() -split '\s+')[0]
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $expected) { throw 'ZIP checksum mismatch.' }
$root = Join-Path (Split-Path -Parent $zip) ('압축 해제 테스트 ' + [guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $zip -DestinationPath $root
$package = Join-Path $root 'EduStream'
foreach ($file in @('coreclr.dll', 'hostfxr.dll', 'AxRDPCOMAPILib.dll', 'RDPCOMAPILib.dll', '먼저 읽어주세요.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $file))) { throw "Missing: $file" }
}
$roles = if ($Role -eq 'Combined') { @('Server', 'Client') } else { @($Role) }
if ($Role -ne 'Combined') {
    $other = if ($Role -eq 'Server') { 'Client' } else { 'Server' }
    $otherExe = if ($Role -eq 'Server') { 'EduStream 학생.exe' } else { 'EduStream 교수자.exe' }
    if ((Test-Path -LiteralPath (Join-Path $package $otherExe)) -or
        (Get-ChildItem -LiteralPath $package -Recurse -File -Filter "EduStream.$other.*")) {
        throw 'Other role app included.'
    }
    if ($PayloadDirectory) {
        $source = Join-Path (Resolve-Path -LiteralPath $PayloadDirectory).Path $Role
        $sourceFiles = @(Get-ChildItem -LiteralPath $source -Recurse -File)
        if (@(Get-ChildItem -LiteralPath $package -Recurse -File).Count -ne $sourceFiles.Count + 1) {
            throw 'Unexpected packaged file count.'
        }
        foreach ($file in $sourceFiles) {
            $relative = $file.FullName.Substring($source.Length + 1)
            if ($relative -eq "EduStream.$Role.exe") {
                $relative = if ($Role -eq 'Server') { 'EduStream 교수자.exe' } else { 'EduStream 학생.exe' }
            }
            $target = Join-Path $package $relative
            if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
                throw "Published file mismatch: $relative"
            }
        }
        Write-Host "PUBLISH_FILE_HASHES=PASS $Role ($($sourceFiles.Count) files)"
    }
}
foreach ($role in $roles) {
    $config = Get-Content -LiteralPath (Join-Path $package "EduStream.$role.runtimeconfig.json") -Raw | ConvertFrom-Json
    if (-not $config.runtimeOptions.PSObject.Properties['includedFrameworks']) { throw "Not self-contained: $role" }
    $exe = if ($role -eq 'Server') { 'EduStream 교수자.exe' } else { 'EduStream 학생.exe' }
    # 공백/한글 경로와 앱 폴더가 아닌 작업 디렉터리에서도 apphost 실행을 확인한다.
    $process = Start-Process -FilePath (Join-Path $package $exe) -WorkingDirectory $env:TEMP -WindowStyle Hidden -PassThru
    try {
        if (-not $process.WaitForInputIdle(15000) -or $process.HasExited) { throw "GUI startup failed: $role" }
        Write-Host "PORTABLE_GUI_STARTUP=PASS $role"
    }
    finally {
        if (-not $process.HasExited) {
            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(3000)) { $process.Kill(); $process.WaitForExit() }
        }
        $process.Dispose()
    }
}
Write-Host 'ZIP_CHECKSUM_EXTRACTION_RUNTIME_AND_INTEROP=PASS'
Write-Host "EXTRACTED_PACKAGE=$package"
