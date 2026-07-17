[CmdletBinding()]
param(
    [string]$SetupPath,
    [string]$ExpectedVersion = '0.1.0-beta.5'
)

. (Join-Path $PSScriptRoot 'common.ps1')
if ($env:GITHUB_ACTIONS -cne 'true' -or
    $env:CI -cne 'true' -or
    $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
    $env:RUNNER_OS -cne 'Windows' -or
    $env:THALEN_HELPER_ALLOW_INSTALLER_LIFECYCLE -cne '1' -or
    [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    throw 'Installer lifecycle tests require explicit opt-in on a disposable GitHub-hosted Windows runner.'
}
if ([string]::IsNullOrWhiteSpace($SetupPath)) {
    $SetupPath = Join-Path $RepositoryRoot '.artifacts\release\Codex-GPU-Thalen-Helper-Setup.exe'
}
$SetupPath = [System.IO.Path]::GetFullPath($SetupPath)
if (-not (Test-Path -LiteralPath $SetupPath)) { throw "Setup executable not found: $SetupPath" }

$runnerTemp = [System.IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd([char[]]'\/') + [System.IO.Path]::DirectorySeparatorChar
$testRoot = Join-Path $runnerTemp ('codex-gpu-thalen-helper-installer-' + [guid]::NewGuid().ToString('N'))
if (-not ([System.IO.Path]::GetFullPath($testRoot).StartsWith($runnerTemp, [System.StringComparison]::OrdinalIgnoreCase))) {
    throw 'Installer lifecycle root escaped RUNNER_TEMP.'
}
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

function New-QuotedInstallerSwitch {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Value
    )

    if ($Name -notmatch '^[A-Z]+$' -or $Value.Contains('"')) {
        throw 'Installer lifecycle switch contains unsupported characters.'
    }
    return ('/{0}="{1}"' -f $Name, $Value)
}

function Assert-SilentSetupRejected {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$InstallPath,
        [Parameter(Mandatory)][string[]]$Arguments
    )

    $process = Start-Process -FilePath $SetupPath -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -eq 0) {
        throw "Silent setup unexpectedly accepted the $Name case."
    }
    if (Test-Path -LiteralPath (Join-Path $InstallPath 'thalen-helper.exe')) {
        throw "Rejected silent setup installed application files for the $Name case."
    }
}

$negativeCodexHome = Join-Path $testRoot 'Rejected Codex home'
$negativeModels = Join-Path $testRoot 'Rejected Models'
New-Item -ItemType Directory -Path $negativeCodexHome, $negativeModels -Force | Out-Null
$missingChoiceInstall = Join-Path $testRoot 'Rejected missing choice'
Assert-SilentSetupRejected -Name 'missing explicit AUTOSTART choice' -InstallPath $missingChoiceInstall -Arguments @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', (New-QuotedInstallerSwitch 'DIR' $missingChoiceInstall),
    (New-QuotedInstallerSwitch 'CODEXHOME' $negativeCodexHome), (New-QuotedInstallerSwitch 'MODELSDIR' $negativeModels), '/MODEL=auto', '/PULLANDVALIDATE=false', '/RELIABILITYBASELINE=false',
    (New-QuotedInstallerSwitch 'LOG' (Join-Path $testRoot 'rejected-missing-choice.log'))
)
$malformedChoiceInstall = Join-Path $testRoot 'Rejected malformed choice'
Assert-SilentSetupRejected -Name 'malformed PULLANDVALIDATE choice' -InstallPath $malformedChoiceInstall -Arguments @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', (New-QuotedInstallerSwitch 'DIR' $malformedChoiceInstall),
    (New-QuotedInstallerSwitch 'CODEXHOME' $negativeCodexHome), (New-QuotedInstallerSwitch 'MODELSDIR' $negativeModels), '/MODEL=auto', '/AUTOSTART=false', '/PULLANDVALIDATE=maybe', '/RELIABILITYBASELINE=false',
    (New-QuotedInstallerSwitch 'LOG' (Join-Path $testRoot 'rejected-malformed-choice.log'))
)
$duplicateChoiceInstall = Join-Path $testRoot 'Rejected duplicate choice'
Assert-SilentSetupRejected -Name 'duplicate AUTOSTART choice' -InstallPath $duplicateChoiceInstall -Arguments @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', (New-QuotedInstallerSwitch 'DIR' $duplicateChoiceInstall),
    (New-QuotedInstallerSwitch 'CODEXHOME' $negativeCodexHome), (New-QuotedInstallerSwitch 'MODELSDIR' $negativeModels), '/MODEL=auto',
    '/AUTOSTART=false', '/AUTOSTART=true', '/PULLANDVALIDATE=false', '/RELIABILITYBASELINE=false',
    (New-QuotedInstallerSwitch 'LOG' (Join-Path $testRoot 'rejected-duplicate-choice.log'))
)
$conflictingPackageOnlyInstall = Join-Path $testRoot 'Rejected package-only conflict'
Assert-SilentSetupRejected -Name 'NOCONFIGURE conflict' -InstallPath $conflictingPackageOnlyInstall -Arguments @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCONFIGURE=1', (New-QuotedInstallerSwitch 'DIR' $conflictingPackageOnlyInstall),
    (New-QuotedInstallerSwitch 'CODEXHOME' $negativeCodexHome), (New-QuotedInstallerSwitch 'LOG' (Join-Path $testRoot 'rejected-package-only-conflict.log'))
)

$silentBaselineInstall = Join-Path $testRoot 'rejected-silent-baseline'
Assert-SilentSetupRejected -Name 'silent reliability baseline bypasses diff preview' -InstallPath $silentBaselineInstall -Arguments @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', (New-QuotedInstallerSwitch 'DIR' $silentBaselineInstall),
    (New-QuotedInstallerSwitch 'CODEXHOME' $negativeCodexHome), (New-QuotedInstallerSwitch 'MODELSDIR' $negativeModels), '/MODEL=auto',
    '/AUTOSTART=false', '/PULLANDVALIDATE=false', '/RELIABILITYBASELINE=true',
    (New-QuotedInstallerSwitch 'LOG' (Join-Path $testRoot 'rejected-silent-baseline.log'))
)

$configuredInstall = Join-Path $testRoot 'Configured install folder ü'
$configuredCodexHome = Join-Path $testRoot 'Configured Codex home ü'
$configuredState = Join-Path $testRoot 'Configured state folder ü'
$configuredModels = Join-Path $testRoot 'Configured models folder ü'
New-Item -ItemType Directory -Path $configuredCodexHome, $configuredState, $configuredModels -Force | Out-Null
$configuredConfig = Join-Path $configuredCodexHome 'config.toml'
$configuredAgents = Join-Path $configuredCodexHome 'AGENTS.override.md'
Set-Content -LiteralPath $configuredConfig -Value 'model = "configured-installer-sentinel"' -Encoding ascii
Set-Content -LiteralPath $configuredAgents -Value '# configured installer sentinel' -Encoding ascii
$configuredConfigBefore = Get-FileHash -LiteralPath $configuredConfig -Algorithm SHA256
$configuredAgentsBefore = Get-FileHash -LiteralPath $configuredAgents -Algorithm SHA256
$configuredProcess = Start-Process -FilePath $SetupPath -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', (New-QuotedInstallerSwitch 'DIR' $configuredInstall),
    (New-QuotedInstallerSwitch 'CODEXHOME' $configuredCodexHome), (New-QuotedInstallerSwitch 'STATEDIR' $configuredState), (New-QuotedInstallerSwitch 'MODELSDIR' $configuredModels),
    '/MODEL=auto', '/AUTOSTART=false', '/PULLANDVALIDATE=false', '/RELIABILITYBASELINE=false',
    (New-QuotedInstallerSwitch 'LOG' (Join-Path $testRoot 'configured-install.log'))
) -Wait -PassThru -WindowStyle Hidden
if ($configuredProcess.ExitCode -ne 0) { throw "Explicit isolated silent configuration failed: $($configuredProcess.ExitCode)" }
if (-not (Select-String -LiteralPath $configuredConfig -SimpleMatch 'BEGIN CODEX GPU THALEN HELPER (managed)' -Quiet)) {
    throw 'Configured silent setup did not add the managed Codex section.'
}
if (-not (Select-String -LiteralPath $configuredAgents -SimpleMatch 'BEGIN CODEX GPU THALEN HELPER (managed)' -Quiet)) {
    throw 'Configured silent setup did not add the managed AGENTS section.'
}
if (Select-String -LiteralPath $configuredAgents -SimpleMatch 'BEGIN CODEX RELIABILITY BASELINE' -Quiet) {
    throw 'Configured silent setup installed the opt-in reliability baseline without an interactive diff preview.'
}
$configuredStateFile = Join-Path $configuredState 'state.json'
if (-not (Test-Path -LiteralPath $configuredStateFile)) { throw 'Configured silent setup did not use the isolated state directory.' }
$configuredStateJson = Get-Content -LiteralPath $configuredStateFile -Raw | ConvertFrom-Json
if ($configuredStateJson.preferences.autoStartOllama -ne $false) {
    throw 'Configured silent setup did not preserve the explicit automatic-start decline.'
}
if ($configuredStateJson.availability -ne 'disabled') {
    throw 'A no-pull silent setup unexpectedly enabled local review.'
}
$configuredUninstaller = Join-Path $configuredInstall 'unins000.exe'
if (-not (Test-Path -LiteralPath $configuredUninstaller)) { throw 'Configured test uninstaller was not installed.' }
$configuredUninstall = Start-Process -FilePath $configuredUninstaller -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', (New-QuotedInstallerSwitch 'LOG' (Join-Path $testRoot 'configured-uninstall.log'))
) -Wait -PassThru -WindowStyle Hidden
if ($configuredUninstall.ExitCode -ne 0) { throw "Configured silent uninstall failed: $($configuredUninstall.ExitCode)" }
if ((Get-FileHash -LiteralPath $configuredConfig -Algorithm SHA256).Hash -ne $configuredConfigBefore.Hash) {
    throw 'Configured lifecycle did not restore the isolated Codex sentinel byte-for-byte.'
}
if ((Get-FileHash -LiteralPath $configuredAgents -Algorithm SHA256).Hash -ne $configuredAgentsBefore.Hash) {
    throw 'Configured lifecycle did not restore the isolated AGENTS sentinel byte-for-byte.'
}
if (Test-Path -LiteralPath $configuredStateFile) { throw 'Configured lifecycle left isolated helper state behind.' }

$install = Join-Path $testRoot 'Install folder ü'
$codexHome = Join-Path $testRoot 'Codex home ü'
$state = Join-Path $testRoot 'State folder ü'
New-Item -ItemType Directory -Path $codexHome, $state -Force | Out-Null
Set-Content -LiteralPath (Join-Path $codexHome 'config.toml') -Value 'model = "installer-sentinel"' -Encoding ascii
Set-Content -LiteralPath (Join-Path $codexHome 'AGENTS.override.md') -Value '# installer sentinel' -Encoding ascii
$beforeConfig = Get-FileHash -LiteralPath (Join-Path $codexHome 'config.toml') -Algorithm SHA256
$beforeAgents = Get-FileHash -LiteralPath (Join-Path $codexHome 'AGENTS.override.md') -Algorithm SHA256

$installProcess = Start-Process -FilePath $SetupPath -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCONFIGURE=1', (New-QuotedInstallerSwitch 'DIR' $install), (New-QuotedInstallerSwitch 'LOG' (Join-Path $testRoot 'install.log'))
) -Wait -PassThru -WindowStyle Hidden
if ($installProcess.ExitCode -ne 0) { throw "Silent package install failed: $($installProcess.ExitCode)" }

foreach ($name in @('thalen-helper.exe', 'local-gpu-reviewer.exe', 'ThalenHelper.ControlCenter.exe', 'README.md', 'LICENSE', 'docs\CODEX-HANDOFF.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $install $name))) { throw "Installed file missing: $name" }
}
$versionOutput = & (Join-Path $install 'thalen-helper.exe') version
if ($LASTEXITCODE -ne 0) { throw 'Installed CLI version command failed.' }
try {
    $versionJson = ($versionOutput -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop
}
catch {
    throw 'Installed CLI version output was not valid JSON.'
}
if ($versionJson.version -cne $ExpectedVersion) { throw 'Installed CLI version check failed.' }

$uninstaller = Join-Path $install 'unins000.exe'
if (-not (Test-Path -LiteralPath $uninstaller)) { throw 'Inno uninstaller was not installed.' }
$uninstallProcess = Start-Process -FilePath $uninstaller -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', (New-QuotedInstallerSwitch 'LOG' (Join-Path $testRoot 'uninstall.log'))
) -Wait -PassThru -WindowStyle Hidden
if ($uninstallProcess.ExitCode -ne 0) { throw "Silent package uninstall failed: $($uninstallProcess.ExitCode)" }

if ((Get-FileHash -LiteralPath (Join-Path $codexHome 'config.toml') -Algorithm SHA256).Hash -ne $beforeConfig.Hash) {
    throw 'Isolated Codex config sentinel changed during package-only lifecycle test.'
}
if ((Get-FileHash -LiteralPath (Join-Path $codexHome 'AGENTS.override.md') -Algorithm SHA256).Hash -ne $beforeAgents.Hash) {
    throw 'Isolated AGENTS sentinel changed during package-only lifecycle test.'
}
if (Test-Path -LiteralPath (Join-Path $install 'thalen-helper.exe')) { throw 'Installed application files remain after uninstall.' }
Write-Host 'Installer lifecycle passed explicit-consent rejection, isolated no-pull/manual-start configuration, package-only install, surgical uninstall, and unchanged Codex sentinels.'
