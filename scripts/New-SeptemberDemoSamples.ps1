param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
# 기존 샘플을 덮어쓰지 않도록 새 출력 폴더에서만 생성합니다.
if (Test-Path -LiteralPath $OutputDirectory) { throw '새 출력 폴더를 지정하세요.' }
$sampleRoot = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($sampleRoot) | Out-Null
$manifest = foreach ($sample in @(
    @{ Name='small-64k.bin'; Size=65536 },
    @{ Name='medium-1m.bin'; Size=1048576 },
    @{ Name='large-8m.bin'; Size=8388608 }
)) {
    $bytes = New-Object byte[] $sample.Size
    for ($i=0; $i -lt $bytes.Length; $i++) { $bytes[$i] = $i % 251 }
    $samplePath = Join-Path $sampleRoot $sample.Name
    [IO.File]::WriteAllBytes($samplePath, $bytes)
    [pscustomobject]@{ File=$sample.Name; Bytes=$sample.Size; SHA256=(Get-FileHash -LiteralPath $samplePath -Algorithm SHA256).Hash }
}
$manifest | Export-Csv -LiteralPath (Join-Path $sampleRoot 'manifest.csv') -NoTypeInformation -Encoding utf8
$manifest | Format-Table -AutoSize
