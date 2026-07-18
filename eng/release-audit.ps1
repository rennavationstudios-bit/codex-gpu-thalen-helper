[CmdletBinding()]
param(
    [string]$Version = '0.1.0-beta.16',
    [switch]$SkipPackage,
    [switch]$RunInstallerLifecycle
)

. (Join-Path $PSScriptRoot 'common.ps1')
if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-beta\.(0|[1-9][0-9]*)$') {
    throw "Release version must be an unsigned beta semantic version such as 0.1.0-beta.6: $Version"
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
        $versionMatch = [System.Text.RegularExpressions.Regex]::Match(
            $Version,
            '^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)-beta\.(?<beta>0|[1-9][0-9]*)$',
            [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
        $expectedPeVersion = (@('major', 'minor', 'patch', 'beta') |
            ForEach-Object { $versionMatch.Groups[$_].Value }) -join '.'

        [xml]$buildProps = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'Directory.Build.props') -Raw
        $sourceAssemblyVersion = "$($buildProps.Project.PropertyGroup.VersionPrefix)-$($buildProps.Project.PropertyGroup.VersionSuffix)"
        if ($sourceAssemblyVersion -cne $Version) {
            throw "Directory.Build.props version mismatch: $sourceAssemblyVersion"
        }
        $domainSource = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'src\ThalenHelper.Core\Domain.cs') -Raw
        if ($domainSource -cnotmatch 'public const string Version = "(?<version>[^"]+)";' -or $Matches['version'] -cne $Version) {
            throw 'ProductInfo.Version does not match the release version.'
        }
        $installerSource = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'installer\ThalenHelper.iss') -Raw
        if ($installerSource -cnotmatch '#define MyAppVersion "(?<version>[^"]+)"' -or $Matches['version'] -cne $Version) {
            throw 'The installer source default version does not match the release version.'
        }
        if ($installerSource -cnotmatch '#define MyAppPeVersion "(?<version>[^"]+)"' -or $Matches['version'] -cne $expectedPeVersion) {
            throw 'The installer source PE version does not match the release version.'
        }
        $installerNotice = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'installer-notice.txt') -Raw
        if ($installerNotice.IndexOf("v$Version", [System.StringComparison]::Ordinal) -lt 0) {
            throw 'The installer unsigned-build notice does not name the exact release version.'
        }

        foreach ($executable in @('thalen-helper.exe', 'local-gpu-reviewer.exe', 'ThalenHelper.ControlCenter.exe')) {
            $productVersion = (Get-Item -LiteralPath (Join-Path $RepositoryRoot ".artifacts\stage\$executable")).VersionInfo.ProductVersion
            $normalizedProductVersion = ($productVersion -split '\+', 2)[0]
            if ($normalizedProductVersion -cne $Version) {
                throw "Published executable version mismatch for ${executable}: $productVersion"
            }
        }
        $setupPath = Join-Path $release 'Codex-GPU-Thalen-Helper-Setup.exe'
        $setupVersion = (Get-Item -LiteralPath $setupPath).VersionInfo
        $setupFileVersion = $setupVersion.FileVersion.Trim()
        $setupProductVersion = $setupVersion.ProductVersion.Trim()
        if ($setupFileVersion -cne $expectedPeVersion -or $setupProductVersion -cne $expectedPeVersion) {
            throw "Installer version metadata mismatch: file=$($setupVersion.FileVersion), product=$($setupVersion.ProductVersion)"
        }

        & (Join-Path $PSScriptRoot 'friend-bundle.ps1') -Version $Version -ReleaseDirectory $release
        if ($LASTEXITCODE -ne 0) { throw 'Friend installer bundle validation failed.' }
        $friendName = "Codex-GPU-Thalen-Helper-$Version-Friend-Installer.zip"
        $friendZip = Join-Path $RepositoryRoot ".artifacts\friend\$friendName"
        if (-not (Test-Path -LiteralPath $friendZip -PathType Leaf)) {
            throw 'Validated friend installer bundle was not created.'
        }
        Copy-Item -LiteralPath $friendZip -Destination (Join-Path $release $friendName)

        $checksumPath = Join-Path $release 'SHA256SUMS.txt'
        Get-ChildItem -LiteralPath $release -File |
            Where-Object Name -ne 'SHA256SUMS.txt' |
            Sort-Object Name |
            ForEach-Object {
                $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                "$hash  $($_.Name)"
            } | Set-Content -LiteralPath $checksumPath -Encoding ascii

        $required = @(
            'Codex-GPU-Thalen-Helper-Setup.exe',
            "Codex-GPU-Thalen-Helper-$Version-win-x64-portable.zip",
            "Codex-GPU-Thalen-Helper-$Version-sbom.zip",
            $friendName,
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
