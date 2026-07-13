[CmdletBinding()]
param(
    [string]$Version = '0.1.0-beta.1',
    [switch]$SkipPackage,
    [switch]$RunInstallerLifecycle
)

. (Join-Path $PSScriptRoot 'common.ps1')
if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-beta\.(0|[1-9][0-9]*)$') {
    throw "Release version must be an unsigned beta semantic version such as 0.1.0-beta.1: $Version"
}

if ($SkipPackage -and $RunInstallerLifecycle) {
    throw 'Installer lifecycle execution cannot be combined with -SkipPackage.'
}

if ($RunInstallerLifecycle) {
    if ($env:GITHUB_ACTIONS -cne 'true' -or
        $env:CI -cne 'true' -or
        $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
        $env:THALEN_HELPER_ALLOW_INSTALLER_LIFECYCLE -cne '1' -or
        [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
        throw 'Installer lifecycle execution is allowed only by explicit opt-in on a disposable GitHub-hosted runner.'
    }
}

Push-Location $RepositoryRoot
try {
    & (Join-Path $PSScriptRoot 'privacy-scan.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Privacy scan failed.' }

    Invoke-Dotnet restore CodexGpuThalenHelper.slnx --locked-mode
    Invoke-Dotnet format CodexGpuThalenHelper.slnx --no-restore --verify-no-changes --verbosity minimal
    Invoke-Dotnet build CodexGpuThalenHelper.slnx --configuration Release --no-restore
    & (Join-Path $PSScriptRoot 'test.ps1') -Configuration Release -LockedMode -Coverage
    if ($LASTEXITCODE -ne 0) { throw 'Test suite failed.' }

    & (Join-Path $PSScriptRoot 'nuget-audit.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'NuGet vulnerability audit failed.' }

    if (-not $SkipPackage) {
        & (Join-Path $PSScriptRoot 'package.ps1') -Version $Version
        if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }

        $release = Join-Path $RepositoryRoot '.artifacts\release'
        $required = @(
            'Codex-GPU-Thalen-Helper-Setup.exe',
            "Codex-GPU-Thalen-Helper-$Version-win-x64-portable.zip",
            "Codex-GPU-Thalen-Helper-$Version-sbom.zip",
            'SHA256SUMS.txt',
            'SIGNING_STATUS.txt'
        )
        foreach ($requiredFile in $required) {
            if (-not (Test-Path -LiteralPath (Join-Path $release $requiredFile) -PathType Leaf)) {
                throw "Release artifact missing: $requiredFile"
            }
        }

        $actualRootFiles = @(Get-ChildItem -LiteralPath $release -File | ForEach-Object Name | Sort-Object)
        $expectedRootFiles = @($required | Sort-Object)
        $unexpected = @(Compare-Object -ReferenceObject $expectedRootFiles -DifferenceObject $actualRootFiles)
        if ($unexpected.Count -ne 0) {
            throw "Release directory contains a missing or unexpected root file: $($unexpected.InputObject -join ', ')"
        }

        $checksumSubjects = @($required | Where-Object { $_ -ne 'SHA256SUMS.txt' } | Sort-Object)
        $checksumPath = Join-Path $release 'SHA256SUMS.txt'
        $seenChecksums = @{}
        foreach ($line in @(Get-Content -LiteralPath $checksumPath)) {
            if ($line -cnotmatch '^(?<hash>[0-9a-f]{64})  (?<name>[^\\/:*?"<>|]+)$') {
                throw 'SHA256SUMS.txt contains a malformed line.'
            }
            $name = $Matches['name']
            if ($seenChecksums.ContainsKey($name)) { throw "SHA256SUMS.txt contains a duplicate entry: $name" }
            $seenChecksums[$name] = $Matches['hash']
        }
        $checksumNames = @($seenChecksums.Keys | Sort-Object)
        $checksumSetDifference = @(Compare-Object -ReferenceObject $checksumSubjects -DifferenceObject $checksumNames)
        if ($checksumSetDifference.Count -ne 0) {
            throw "SHA256SUMS.txt does not describe the exact release subject set: $($checksumSetDifference.InputObject -join ', ')"
        }
        foreach ($name in $checksumSubjects) {
            $actualHash = (Get-FileHash -LiteralPath (Join-Path $release $name) -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actualHash -cne $seenChecksums[$name]) { throw "Release checksum mismatch: $name" }
        }

        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $release 'Codex-GPU-Thalen-Helper-Setup.exe')
        if ($signature.Status -eq 'Valid') { throw 'Unexpected signed status: update beta disclosure and signing policy before release.' }

        if ($RunInstallerLifecycle) {
            & (Join-Path $PSScriptRoot 'installer-lifecycle-test.ps1') `
                -SetupPath (Join-Path $release 'Codex-GPU-Thalen-Helper-Setup.exe') `
                -ExpectedVersion $Version
            if ($LASTEXITCODE -ne 0) { throw 'Installer lifecycle test failed.' }
        }
    }

    Write-Host 'Release audit passed.'
}
finally {
    Pop-Location
}
