[CmdletBinding()]
param(
    [string]$SetupPath,
    [string]$ExpectedVersion = '0.1.0-beta.23',
    [string]$PreviousSetupPath
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

$previousReleaseVersion = '0.1.0-beta.5'
$previousReleaseSha256 = '57dd7b1fa2b740a6ca10a26f6a458cecf126019efa1acc3171e06d3a1b30f3fa'
$previousReleaseUrl = 'https://github.com/rennavationstudios-bit/codex-gpu-thalen-helper/releases/download/v0.1.0-beta.5/Codex-GPU-Thalen-Helper-Setup.exe'

function Resolve-PreviousReleaseSetup {
    if ([string]::IsNullOrWhiteSpace($PreviousSetupPath)) {
        $resolved = Join-Path $testRoot 'Codex-GPU-Thalen-Helper-Setup-v0.1.0-beta.5.exe'
        Invoke-WebRequest -Uri $previousReleaseUrl -OutFile $resolved -MaximumRedirection 5
    }
    else {
        $resolved = [System.IO.Path]::GetFullPath($PreviousSetupPath)
    }

    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Previous release setup executable not found: $resolved"
    }
    $actual = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -cne $previousReleaseSha256) {
        throw "Previous release setup hash mismatch: expected $previousReleaseSha256, actual $actual"
    }
    return $resolved
}

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

function Invoke-ConfiguredSilentSetup {
    param(
        [Parameter(Mandatory)][string]$Installer,
        [Parameter(Mandatory)][string]$InstallPath,
        [Parameter(Mandatory)][string]$CodexHome,
        [Parameter(Mandatory)][string]$StatePath,
        [Parameter(Mandatory)][string]$ModelsPath,
        [Parameter(Mandatory)][string]$LogPath
    )

    $process = Start-Process -FilePath $Installer -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        (New-QuotedInstallerSwitch 'DIR' $InstallPath),
        (New-QuotedInstallerSwitch 'CODEXHOME' $CodexHome),
        (New-QuotedInstallerSwitch 'STATEDIR' $StatePath),
        (New-QuotedInstallerSwitch 'MODELSDIR' $ModelsPath),
        '/MODEL=auto', '/AUTOSTART=false', '/PULLANDVALIDATE=false', '/RELIABILITYBASELINE=false',
        (New-QuotedInstallerSwitch 'LOG' $LogPath)
    ) -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "Isolated configured setup failed with exit code $($process.ExitCode): $Installer"
    }
}

function Get-InstalledVersion {
    param([Parameter(Mandatory)][string]$InstallPath)

    $output = & (Join-Path $InstallPath 'thalen-helper.exe') version
    if ($LASTEXITCODE -ne 0) { throw 'Installed CLI version command failed.' }
    try {
        return (($output -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop).version
    }
    catch {
        throw 'Installed CLI version output was not valid JSON.'
    }
}

function Assert-SingleManagedSections {
    param(
        [Parameter(Mandatory)][string]$ConfigPath,
        [Parameter(Mandatory)][string]$AgentsPath
    )

    $configText = [System.IO.File]::ReadAllText($ConfigPath)
    $agentsText = [System.IO.File]::ReadAllText($AgentsPath)
    foreach ($marker in @(
        '# BEGIN CODEX GPU THALEN HELPER (managed)',
        '# END CODEX GPU THALEN HELPER (managed)')) {
        if ([regex]::Matches($configText, [regex]::Escape($marker)).Count -ne 1) {
            throw "Managed config marker was missing or duplicated: $marker"
        }
    }
    if ([regex]::Matches($configText, [regex]::Escape('env_vars = ["OLLAMA_MODELS"]')).Count -ne 1) {
        throw 'Managed config did not contain exactly one OLLAMA_MODELS MCP environment whitelist.'
    }
    foreach ($marker in @(
        '<!-- BEGIN CODEX GPU THALEN HELPER (managed) -->',
        '<!-- END CODEX GPU THALEN HELPER (managed) -->')) {
        if ([regex]::Matches($agentsText, [regex]::Escape($marker)).Count -ne 1) {
            throw "Managed AGENTS marker was missing or duplicated: $marker"
        }
    }
}

function Get-ProtectedBackups {
    param([Parameter(Mandatory)][string]$ProtectedPath)

    $directory = Split-Path -Parent $ProtectedPath
    $leaf = Split-Path -Leaf $ProtectedPath
    return @(Get-ChildItem -LiteralPath $directory -File -Filter "$leaf.thalen-helper.*.bak" | Sort-Object Name)
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

# Exercise an actual in-place upgrade from the immutable public beta.5 installer to
# the installer produced by this checkout. All protected files, state, models, and
# installed binaries remain beneath the disposable runner root.
$previousSetup = Resolve-PreviousReleaseSetup
$upgradeInstall = Join-Path $testRoot 'Upgrade install'
$upgradeCodexHome = Join-Path $testRoot 'Upgrade Codex home'
$upgradeState = Join-Path $testRoot 'Upgrade state'
$upgradeModels = Join-Path $testRoot 'Upgrade models'
New-Item -ItemType Directory -Path $upgradeCodexHome, $upgradeState, $upgradeModels -Force | Out-Null

$upgradeConfig = Join-Path $upgradeCodexHome 'config.toml'
$upgradeAgents = Join-Path $upgradeCodexHome 'AGENTS.override.md'
$originalConfigText = @'
# user comment retained across helper upgrades
model = "user-owned-model-setting"

[mcp_servers.local_gpu_reviewer]
command = "C:\\Program Files\\External Reviewer\\external-reviewer.exe"
enabled = true

[mcp_servers.local_gpu_reviewer.env]
OLLAMA_HOST = "http://127.0.0.1:11434"

[user_extension]
unknown_upgrade_fixture = true
'@
$originalAgentsText = @'
# User-owned Codex instructions

<!-- user comment retained across helper upgrades -->
Use local_gpu_reviewer only for explicitly approved, non-sensitive reviews.
Keep this unknown user instruction unchanged.
'@
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($upgradeConfig, $originalConfigText, $utf8NoBom)
[System.IO.File]::WriteAllText($upgradeAgents, $originalAgentsText, $utf8NoBom)
$originalConfigHash = (Get-FileHash -LiteralPath $upgradeConfig -Algorithm SHA256).Hash
$originalAgentsHash = (Get-FileHash -LiteralPath $upgradeAgents -Algorithm SHA256).Hash

Invoke-ConfiguredSilentSetup `
    -Installer $previousSetup `
    -InstallPath $upgradeInstall `
    -CodexHome $upgradeCodexHome `
    -StatePath $upgradeState `
    -ModelsPath $upgradeModels `
    -LogPath (Join-Path $testRoot 'upgrade-beta5-install.log')
if ((Get-InstalledVersion -InstallPath $upgradeInstall) -cne $previousReleaseVersion) {
    throw 'The verified previous-release installer did not install beta.5.'
}
$upgradeStateFile = Join-Path $upgradeState 'state.json'
if (-not (Test-Path -LiteralPath $upgradeStateFile -PathType Leaf)) {
    throw 'The previous release did not write isolated preservation state.'
}
if ((Get-FileHash -LiteralPath $upgradeConfig -Algorithm SHA256).Hash -cne $originalConfigHash -or
    (Get-FileHash -LiteralPath $upgradeAgents -Algorithm SHA256).Hash -cne $originalAgentsHash) {
    throw 'Beta.5 did not preserve the external integration and user-owned AGENTS file byte-for-byte.'
}
$preservedState = Get-Content -LiteralPath $upgradeStateFile -Raw | ConvertFrom-Json
if ($preservedState.existingIntegrationPreserved -ne $true -or
    $preservedState.productVersion -cne $previousReleaseVersion) {
    throw 'Beta.5 did not record the external local_gpu_reviewer integration in preservation mode.'
}
if (@(Get-ProtectedBackups -ProtectedPath $upgradeConfig).Count -ne 0 -or
    @(Get-ProtectedBackups -ProtectedPath $upgradeAgents).Count -ne 0) {
    throw 'Beta.5 wrote backups even though preservation mode was required to leave both files unchanged.'
}
$preservedStateHash = (Get-FileHash -LiteralPath $upgradeStateFile -Algorithm SHA256).Hash

$binaryUpgrade = Start-Process -FilePath $SetupPath -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCONFIGURE=1',
    (New-QuotedInstallerSwitch 'DIR' $upgradeInstall),
    (New-QuotedInstallerSwitch 'LOG' (Join-Path $testRoot 'upgrade-current-binaries-only.log'))
) -Wait -PassThru -WindowStyle Hidden
if ($binaryUpgrade.ExitCode -ne 0) {
    throw "Current package-only binary upgrade failed: $($binaryUpgrade.ExitCode)"
}
if ((Get-FileHash -LiteralPath $upgradeConfig -Algorithm SHA256).Hash -cne $originalConfigHash -or
    (Get-FileHash -LiteralPath $upgradeAgents -Algorithm SHA256).Hash -cne $originalAgentsHash -or
    (Get-FileHash -LiteralPath $upgradeStateFile -Algorithm SHA256).Hash -cne $preservedStateHash) {
    throw 'The package-only current installer changed protected files or preservation state before dry-run.'
}
if ((Get-InstalledVersion -InstallPath $upgradeInstall) -cne $ExpectedVersion) {
    throw 'The in-place upgrade did not install the expected current binary version.'
}
$currentExecutables = @('thalen-helper.exe', 'local-gpu-reviewer.exe', 'ThalenHelper.ControlCenter.exe')
foreach ($executable in $currentExecutables) {
    $expectedCurrentExecutable = Join-Path $RepositoryRoot ".artifacts\stage\$executable"
    $installedCurrentExecutable = Join-Path $upgradeInstall $executable
    if (-not (Test-Path -LiteralPath $expectedCurrentExecutable -PathType Leaf)) {
        throw "Current staged executable is missing, so the installed upgrade cannot be verified: $expectedCurrentExecutable"
    }
    if (-not (Test-Path -LiteralPath $installedCurrentExecutable -PathType Leaf) -or
        (Get-FileHash -LiteralPath $installedCurrentExecutable -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $expectedCurrentExecutable -Algorithm SHA256).Hash) {
        throw "The upgraded executable bytes do not match the current staged build: $executable"
    }
}

$migrationDiff = Join-Path $testRoot 'private\beta5-external-migration.diff'
$dryRunArguments = @(
    'repair', '--dry-run', '--diff-out', $migrationDiff, '--migrate-existing',
    '--install-dir', $upgradeInstall,
    '--state-dir', $upgradeState,
    '--codex-home', $upgradeCodexHome
)
$dryRunOutput = & (Join-Path $upgradeInstall 'thalen-helper.exe') @dryRunArguments
if ($LASTEXITCODE -ne 0) { throw 'Protected migration dry-run failed.' }
try {
    $dryRun = ($dryRunOutput -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop
}
catch {
    throw 'Protected migration dry-run output was not valid JSON.'
}
if ($dryRun.success -ne $true -or $dryRun.code -cne 'REPAIR_DRY_RUN_READY' -or
    -not (Test-Path -LiteralPath $migrationDiff -PathType Leaf)) {
    throw 'Protected migration dry-run did not return a ready result and private diff.'
}
foreach ($hash in @(
    $dryRun.codexConfig.sourceSha256,
    $dryRun.codexConfig.plannedSha256,
    $dryRun.agentsOverride.sourceSha256,
    $dryRun.agentsOverride.plannedSha256)) {
    if ([string]::IsNullOrWhiteSpace($hash) -or $hash -cnotmatch '^[A-Fa-f0-9]{64}$') {
        throw 'Protected migration dry-run did not return all four SHA-256 bindings.'
    }
}
if ((Get-FileHash -LiteralPath $upgradeConfig -Algorithm SHA256).Hash -cne $originalConfigHash -or
    (Get-FileHash -LiteralPath $upgradeAgents -Algorithm SHA256).Hash -cne $originalAgentsHash -or
    (Get-FileHash -LiteralPath $upgradeStateFile -Algorithm SHA256).Hash -cne $preservedStateHash -or
    @(Get-ProtectedBackups -ProtectedPath $upgradeConfig).Count -ne 0 -or
    @(Get-ProtectedBackups -ProtectedPath $upgradeAgents).Count -ne 0) {
    throw 'Protected migration dry-run changed files, state, or backups.'
}
$privateDiffText = [System.IO.File]::ReadAllText($migrationDiff)
if (-not $privateDiffText.Contains('external-reviewer.exe', [System.StringComparison]::Ordinal) -or
    -not $privateDiffText.Contains('# BEGIN CODEX GPU THALEN HELPER (managed)', [System.StringComparison]::Ordinal) -or
    -not $privateDiffText.Contains('<!-- BEGIN CODEX GPU THALEN HELPER (managed) -->', [System.StringComparison]::Ordinal)) {
    throw 'The private migration diff did not show the expected external-to-managed replacement.'
}

$migrationArguments = @(
    'repair', '--migrate-existing',
    '--expected-config-source-sha256', $dryRun.codexConfig.sourceSha256,
    '--expected-config-planned-sha256', $dryRun.codexConfig.plannedSha256,
    '--expected-agents-source-sha256', $dryRun.agentsOverride.sourceSha256,
    '--expected-agents-planned-sha256', $dryRun.agentsOverride.plannedSha256,
    '--install-dir', $upgradeInstall,
    '--state-dir', $upgradeState,
    '--codex-home', $upgradeCodexHome
)
$migrationOutput = & (Join-Path $upgradeInstall 'thalen-helper.exe') @migrationArguments
if ($LASTEXITCODE -ne 0) { throw 'Hash-bound external integration migration failed.' }
try {
    $migration = ($migrationOutput -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop
}
catch {
    throw 'Hash-bound migration output was not valid JSON.'
}
if ($migration.success -ne $true -or $migration.state.existingIntegrationPreserved -ne $false -or
    $migration.state.productVersion -cne $ExpectedVersion) {
    throw 'Hash-bound migration did not establish current helper ownership and product version.'
}

Assert-SingleManagedSections -ConfigPath $upgradeConfig -AgentsPath $upgradeAgents
$configAfterUpgrade = [System.IO.File]::ReadAllText($upgradeConfig)
$agentsAfterUpgrade = [System.IO.File]::ReadAllText($upgradeAgents)
if (-not $configAfterUpgrade.Contains('# user comment retained across helper upgrades', [System.StringComparison]::Ordinal) -or
    -not $configAfterUpgrade.Contains('unknown_upgrade_fixture = true', [System.StringComparison]::Ordinal) -or
    -not $configAfterUpgrade.Contains('env_vars = ["OLLAMA_MODELS"]', [System.StringComparison]::Ordinal) -or
    $configAfterUpgrade.Contains('external-reviewer.exe', [System.StringComparison]::Ordinal)) {
    throw 'The migration did not replace only the external reviewer family while preserving unknown config content.'
}
if (-not $agentsAfterUpgrade.Contains('<!-- user comment retained across helper upgrades -->', [System.StringComparison]::Ordinal) -or
    -not $agentsAfterUpgrade.Contains('Use local_gpu_reviewer only for explicitly approved, non-sensitive reviews.', [System.StringComparison]::Ordinal) -or
    -not $agentsAfterUpgrade.Contains('Keep this unknown user instruction unchanged.', [System.StringComparison]::Ordinal)) {
    throw 'The migration did not preserve user-owned AGENTS comments and local_gpu_reviewer guidance.'
}
$migrationConfigBackups = @(Get-ProtectedBackups -ProtectedPath $upgradeConfig)
$migrationAgentsBackups = @(Get-ProtectedBackups -ProtectedPath $upgradeAgents)
if ($migrationConfigBackups.Count -lt 1 -or $migrationAgentsBackups.Count -lt 1) {
    throw 'The protected migration did not create timestamped backups for both existing files.'
}
foreach ($backup in @($migrationConfigBackups + $migrationAgentsBackups)) {
    if ($backup.Name -cnotmatch '\.thalen-helper\.\d{8}-\d{6}-\d{3}\.[0-9a-f]{32}\.bak$') {
        throw "Protected migration backup name was not timestamped as expected: $($backup.Name)"
    }
}
$matchingConfigBackups = @($migrationConfigBackups | Where-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -ceq $originalConfigHash
    })
$matchingAgentsBackups = @($migrationAgentsBackups | Where-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -ceq $originalAgentsHash
    })
if ($matchingConfigBackups.Count -lt 1 -or $matchingAgentsBackups.Count -lt 1) {
    throw 'The protected migration backups did not preserve the original external integration bytes.'
}

$migratedConfigHash = (Get-FileHash -LiteralPath $upgradeConfig -Algorithm SHA256).Hash
$migratedAgentsHash = (Get-FileHash -LiteralPath $upgradeAgents -Algorithm SHA256).Hash
$migratedStateHash = (Get-FileHash -LiteralPath $upgradeStateFile -Algorithm SHA256).Hash
$configBackupsBeforeSecondRepair = @($migrationConfigBackups | ForEach-Object Name)
$agentsBackupsBeforeSecondRepair = @($migrationAgentsBackups | ForEach-Object Name)
$secondDiff = Join-Path $testRoot 'private\managed-idempotency.diff'
$secondDryRunArguments = @(
    'repair', '--dry-run', '--diff-out', $secondDiff,
    '--install-dir', $upgradeInstall,
    '--state-dir', $upgradeState,
    '--codex-home', $upgradeCodexHome
)
$secondDryRunOutput = & (Join-Path $upgradeInstall 'thalen-helper.exe') @secondDryRunArguments
if ($LASTEXITCODE -ne 0) { throw 'Managed idempotency dry-run failed.' }
try {
    $secondDryRun = ($secondDryRunOutput -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop
}
catch {
    throw 'Managed idempotency dry-run output was not valid JSON.'
}
if ($secondDryRun.success -ne $true -or $secondDryRun.code -cne 'REPAIR_DRY_RUN_READY') {
    throw 'Managed idempotency dry-run was not ready.'
}
if ((Get-FileHash -LiteralPath $upgradeConfig -Algorithm SHA256).Hash -cne $migratedConfigHash -or
    (Get-FileHash -LiteralPath $upgradeAgents -Algorithm SHA256).Hash -cne $migratedAgentsHash -or
    (Get-FileHash -LiteralPath $upgradeStateFile -Algorithm SHA256).Hash -cne $migratedStateHash) {
    throw 'Managed idempotency dry-run changed protected files or state.'
}
$secondRepairArguments = @(
    'repair',
    '--expected-config-source-sha256', $secondDryRun.codexConfig.sourceSha256,
    '--expected-config-planned-sha256', $secondDryRun.codexConfig.plannedSha256,
    '--expected-agents-source-sha256', $secondDryRun.agentsOverride.sourceSha256,
    '--expected-agents-planned-sha256', $secondDryRun.agentsOverride.plannedSha256,
    '--install-dir', $upgradeInstall,
    '--state-dir', $upgradeState,
    '--codex-home', $upgradeCodexHome
)
$secondRepairOutput = & (Join-Path $upgradeInstall 'thalen-helper.exe') @secondRepairArguments
if ($LASTEXITCODE -ne 0) { throw 'Second hash-bound non-migration repair failed.' }
try {
    $secondRepair = ($secondRepairOutput -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop
}
catch {
    throw 'Second hash-bound repair output was not valid JSON.'
}
if ($secondRepair.success -ne $true -or $secondRepair.codexConfig.changed -ne $false -or
    $secondRepair.agentsOverride.changed -ne $false -or
    (Get-FileHash -LiteralPath $upgradeConfig -Algorithm SHA256).Hash -cne $migratedConfigHash -or
    (Get-FileHash -LiteralPath $upgradeAgents -Algorithm SHA256).Hash -cne $migratedAgentsHash -or
    (Get-FileHash -LiteralPath $upgradeStateFile -Algorithm SHA256).Hash -cne $migratedStateHash) {
    throw 'Second hash-bound non-migration repair was not idempotent.'
}
$configBackupsAfterSecondRepair = @(Get-ProtectedBackups -ProtectedPath $upgradeConfig | ForEach-Object Name)
$agentsBackupsAfterSecondRepair = @(Get-ProtectedBackups -ProtectedPath $upgradeAgents | ForEach-Object Name)
if (@(Compare-Object $configBackupsBeforeSecondRepair $configBackupsAfterSecondRepair).Count -ne 0 -or
    @(Compare-Object $agentsBackupsBeforeSecondRepair $agentsBackupsAfterSecondRepair).Count -ne 0) {
    throw 'Second hash-bound non-migration repair created duplicate backups.'
}
if (@(Get-ChildItem -LiteralPath $upgradeModels -Recurse -File -ErrorAction SilentlyContinue).Count -ne 0) {
    throw 'The no-pull upgrade lifecycle unexpectedly downloaded or created model files.'
}
Assert-SingleManagedSections -ConfigPath $upgradeConfig -AgentsPath $upgradeAgents

$packageLifecycleMarker = Join-Path $upgradeInstall '.package-lifecycle-test'
if (-not (Test-Path -LiteralPath $packageLifecycleMarker -PathType Leaf)) {
    throw 'Package-only binary upgrade did not leave its expected managed-cleanup suppression marker.'
}
Remove-Item -LiteralPath $packageLifecycleMarker -Force
$upgradeUninstaller = Join-Path $upgradeInstall 'unins000.exe'
if (-not (Test-Path -LiteralPath $upgradeUninstaller -PathType Leaf)) {
    throw 'The upgraded installation did not contain the Inno uninstaller.'
}
$upgradeUninstall = Start-Process -FilePath $upgradeUninstaller -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
    (New-QuotedInstallerSwitch 'LOG' (Join-Path $testRoot 'upgrade-uninstall.log'))
) -Wait -PassThru -WindowStyle Hidden
if ($upgradeUninstall.ExitCode -ne 0) { throw "Upgraded silent uninstall failed: $($upgradeUninstall.ExitCode)" }
if ((Get-FileHash -LiteralPath $upgradeConfig -Algorithm SHA256).Hash -cne $originalConfigHash -or
    (Get-FileHash -LiteralPath $upgradeAgents -Algorithm SHA256).Hash -cne $originalAgentsHash) {
    throw 'Upgraded uninstall did not restore the original protected files byte-for-byte.'
}
if ([System.IO.File]::ReadAllText($upgradeConfig) -cne $originalConfigText -or
    [System.IO.File]::ReadAllText($upgradeAgents) -cne $originalAgentsText) {
    throw 'Upgraded uninstall was not surgical for unknown user content.'
}
if (Test-Path -LiteralPath (Join-Path $upgradeInstall 'thalen-helper.exe')) {
    throw 'Upgraded uninstall left current application binaries behind.'
}
if (Test-Path -LiteralPath (Join-Path $upgradeState 'state.json')) {
    throw 'Upgraded uninstall left isolated helper state behind.'
}

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
Write-Host 'Installer lifecycle passed explicit-consent rejection, isolated no-pull/manual-start configuration, beta.5 external-integration preservation, package-only binary upgrade, hash-bound migration dry-run/apply, idempotent repair, surgical uninstall, and unchanged user content.'
