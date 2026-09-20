[CmdletBinding()]
param(
    [ValidateRange(1, 10)][int]$Repeat = 2,
    [string]$OutputDirectory = (Join-Path $env:TEMP ("EduStream-CoreFile-" + [Guid]::NewGuid().ToString("N")))
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Choose a new evidence directory: $output" }
New-Item -ItemType Directory -Path $output | Out-Null
Push-Location $repo
$previousSmoke = [Environment]::GetEnvironmentVariable('EDUSTREAM_WDS_SMOKE', 'Process')
try {
    $env:EDUSTREAM_WDS_SMOKE = '0'
    & dotnet build EduStream.sln --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    for ($i = 1; $i -le $Repeat; $i++) {
        & dotnet test EduStream.sln --no-build --nologo --filter 'FullyQualifiedName~FinalReadiness' --logger "trx;LogFileName=readiness-$i.trx" --results-directory $output
        if ($LASTEXITCODE -ne 0) { throw "Readiness run $i failed" }
    }
    & dotnet test EduStream.sln --no-build --nologo --logger 'trx;LogFileName=full.trx' --results-directory $output
    if ($LASTEXITCODE -ne 0) { throw 'Full regression failed' }
    $head = & git rev-parse HEAD
    $changes = @(& git status --porcelain)
    $sourceFiles = @(& git ls-files --cached --others --exclude-standard)
    $hashes = @($sourceFiles | Where-Object {
        $_ -match '^(src/|tests/|tools/EduStream.ContractTestDoubles/|EduStream.sln$|scripts/Test-CoreFileReadiness.ps1$)' -and
        (Test-Path -LiteralPath $_ -PathType Leaf)
    } | ForEach-Object {
        [pscustomobject]@{ Path = $_; Sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
    })
    [pscustomobject]@{
        Timestamp = [DateTimeOffset]::Now.ToString('o')
        BaseCommit = $head
        UncommittedChanges = $changes
        Scope = 'Core/file local contracts and simulated integration; not real WDS or multi-PC acceptance'
        SourceHashes = $hashes
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'evidence.json') -Encoding UTF8
    Write-Host "Local automatic checks passed; not real RDP or multi-PC acceptance. Evidence: $output"
}
finally {
    [Environment]::SetEnvironmentVariable('EDUSTREAM_WDS_SMOKE', $previousSmoke, 'Process')
    Pop-Location
}
