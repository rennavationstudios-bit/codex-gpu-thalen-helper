[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$LockedMode,
    [switch]$Coverage
)

. (Join-Path $PSScriptRoot 'common.ps1')
$testRoot = Join-Path $RepositoryRoot '.test-results\isolated'
Reset-RepositoryDirectory -Path $testRoot
$priorCodexHome = $env:CODEX_HOME
$priorState = $env:THALEN_HELPER_STATE_DIR
$priorGpuTest = $env:THALEN_HELPER_REAL_GPU_TEST
$env:CODEX_HOME = Join-Path $testRoot 'codex-home'
$env:THALEN_HELPER_STATE_DIR = Join-Path $testRoot 'state'
$env:THALEN_HELPER_REAL_GPU_TEST = $null
New-Item -ItemType Directory -Path $env:CODEX_HOME -Force | Out-Null
New-Item -ItemType Directory -Path $env:THALEN_HELPER_STATE_DIR -Force | Out-Null

Push-Location $RepositoryRoot
try {
    $restoreArguments = @('restore', 'CodexGpuThalenHelper.slnx')
    if ($LockedMode) { $restoreArguments += '--locked-mode' }
    Invoke-Dotnet @restoreArguments
    $arguments = @(
        'test',
        'tests\ThalenHelper.Tests\ThalenHelper.Tests.csproj',
        '--no-restore',
        '--configuration', $Configuration,
        '--logger', "trx;LogFileName=unit-$Configuration.trx",
        '--results-directory', (Join-Path $RepositoryRoot '.test-results')
    )
    if ($Coverage) {
        $arguments += @('--collect', 'XPlat Code Coverage')
    }
    Invoke-Dotnet @arguments
}
finally {
    Pop-Location
    $env:CODEX_HOME = $priorCodexHome
    $env:THALEN_HELPER_STATE_DIR = $priorState
    $env:THALEN_HELPER_REAL_GPU_TEST = $priorGpuTest
}
