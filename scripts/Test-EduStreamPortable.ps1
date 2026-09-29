[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ZipPath
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
foreach ($role in @('Server', 'Client')) {
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
