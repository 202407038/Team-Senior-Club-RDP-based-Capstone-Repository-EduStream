[CmdletBinding()]
param(
    [ValidateRange(1, 10)][int]$Repeat = 3,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$OutputDirectory = (Join-Path $env:TEMP ("EduStream-CoreFile-" + [Guid]::NewGuid().ToString("N")))
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Choose a new evidence directory: $output" }
New-Item -ItemType Directory -Path $output | Out-Null
Push-Location $repo
$previousSmoke = [Environment]::GetEnvironmentVariable('EDUSTREAM_WDS_SMOKE', 'Process')
$filter = (@('FinalReadiness', 'CoreFrame', 'CoreEngineHandoff', 'CoreAnnotationEngine',
    'FileStorageBoundary', 'ReverseCollaboration', 'ReverseViewingControlRevoke',
    'ServerRemoteControlCoordinator', 'ProfessorViewerAsyncRelease', 'ProfessorViewerRelease',
    'ReverseSharingPlacement', 'SecureRoomJoinTests', 'SecureCollaborationChannelTests',
    'StudentPermissionSyncTests', 'AutoReconnectTests', 'ClientReconnectViewModelTests',
    'SecureFileRoutingWiringTests', 'RdpInvitationSecretDeliveryTests', 'RdpAutoConnectDeduplicationTests') |
    ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
$fullFilter = 'FullyQualifiedName!~WindowsNativeInputPipeline'
$runs = [Collections.Generic.List[object]]::new()
$stage = 'source-snapshot'
$success = $false
$failure = $null
$head = $null
$changes = @()
$hashes = @()
$finalHashes = @()

function Get-SourceHashes {
    $sourceFiles = @(& git -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate source files' }
    @($sourceFiles | Sort-Object -Unique | Where-Object {
        $_ -match '^(src/|tests/|tools/EduStream.ContractTestDoubles/|EduStream.sln$|scripts/Test-CoreFileReadiness.ps1$|Directory\.|global.json$|NuGet.Config$)' -and
        (Test-Path -LiteralPath $_ -PathType Leaf)
    } | ForEach-Object {
        [pscustomobject]@{ Path = $_; Sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
    })
}

function Read-TestResult([string]$Name, [int]$ExitCode) {
    $path = Join-Path $output "$Name.trx"
    $counts = $null
    if (Test-Path -LiteralPath $path) {
        [xml]$xml = Get-Content -LiteralPath $path -Raw
        $c = $xml.SelectSingleNode("//*[local-name()='Counters']")
        if ($c) {
            $counts = [pscustomobject]@{
                Total = [int]$c.total; Executed = [int]$c.executed; Passed = [int]$c.passed
                Failed = [int]$c.failed
                # xUnit TRX의 notExecuted는 0이어도 total-executed에 Skip이 포함될 수 있습니다.
                Skipped = [Math]::Max(0, [int]$c.total - [int]$c.executed)
                RawNotExecuted = [int]$c.notExecuted
            }
        }
    }
    $runs.Add([pscustomobject]@{ Name = $Name; ExitCode = $ExitCode; Counts = $counts })
    if ($ExitCode -ne 0 -or !$counts -or $counts.Executed -eq 0 -or $counts.Failed -gt 0) {
        throw "$Name failed or has no executed-test evidence"
    }
}

try {
    $head = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read base commit' }
    $changes = @(& git status --porcelain)
    $hashes = @(Get-SourceHashes)
    $env:EDUSTREAM_WDS_SMOKE = '0'
    $stage = 'build'
    & dotnet build EduStream.sln --nologo -c $Configuration
    $runs.Add([pscustomobject]@{ Name = $stage; ExitCode = $LASTEXITCODE; Counts = $null })
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    for ($i = 1; $i -le $Repeat; $i++) {
        $stage = "readiness-$i"
        & dotnet test EduStream.sln --no-build --nologo -c $Configuration --filter $filter --logger "trx;LogFileName=$stage.trx" --results-directory $output
        Read-TestResult $stage $LASTEXITCODE
    }
    $stage = 'full'
    & dotnet test EduStream.sln --no-build --nologo -c $Configuration --filter $fullFilter --logger 'trx;LogFileName=full.trx' --results-directory $output
    Read-TestResult $stage $LASTEXITCODE
    $stage = 'source-stability'
    $finalHashes = @(Get-SourceHashes)
    $finalHead = & git rev-parse HEAD
    if ($head -ne $finalHead -or (ConvertTo-Json -InputObject $hashes -Compress) -ne
        (ConvertTo-Json -InputObject $finalHashes -Compress)) {
        throw 'Source changed during verification; rerun on a stable tree'
    }
    $success = $true
    $stage = 'complete'
}
catch {
    $failure = $_.Exception.Message
    throw
}
finally {
    try {
    # 실패/건너뜀도 남기고, 실제 입력·native WDS 미실행을 성공과 구분합니다.
    [pscustomobject]@{
        Timestamp = [DateTimeOffset]::Now.ToString('o')
        Succeeded = $success
        Stage = $stage
        Failure = $failure
        Configuration = $Configuration
        BaseCommit = $head
        UncommittedChanges = $changes
        Scope = 'Core/file and same-PC service regressions; not native WDS, actual desktop input, UI or multi-PC acceptance'
        RepeatedTestFilter = $filter
        RepeatCount = $Repeat
        FullTestFilter = $fullFilter
        NativeWdsEnabled = $false
        ExcludedTestName = 'WindowsNativeInputPipeline'
        Runs = @($runs.ToArray())
        SourceHashes = $hashes
        FinalSourceHashes = $finalHashes
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'evidence.json') -Encoding UTF8
    Write-Host "Evidence (success=$success): $output"
    }
    finally {
        [Environment]::SetEnvironmentVariable('EDUSTREAM_WDS_SMOKE', $previousSmoke, 'Process')
        Pop-Location
    }
}
