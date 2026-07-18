[CmdletBinding()]
param(
    [string]$Version = '0.1.0-beta.15',
    [string]$ReleaseDirectory
)

. (Join-Path $PSScriptRoot 'common.ps1')

if ($Version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-beta\.(0|[1-9][0-9]*)$') {
    throw "Friend bundle version must be an unsigned beta semantic version: $Version"
}

if ([string]::IsNullOrWhiteSpace($ReleaseDirectory)) {
    $ReleaseDirectory = Join-Path $RepositoryRoot '.artifacts\release'
}
$ReleaseDirectory = [System.IO.Path]::GetFullPath($ReleaseDirectory)
$releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.artifacts\release'))
if (-not $ReleaseDirectory.Equals($releaseRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Friend bundles may be created only from the audited repository release directory.'
}

$sourceInstaller = Join-Path $ReleaseDirectory 'Codex-GPU-Thalen-Helper-Setup.exe'
$sourceSigning = Join-Path $ReleaseDirectory 'SIGNING_STATUS.txt'
foreach ($required in @($sourceInstaller, $sourceSigning)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Friend bundle source is missing: $required"
    }
}

$friendRoot = Join-Path $RepositoryRoot '.artifacts\friend'
$stage = Join-Path $friendRoot 'stage'
$validation = Join-Path $friendRoot 'validation'
Reset-RepositoryDirectory -Path $friendRoot
New-Item -ItemType Directory -Path $stage, (Join-Path $stage 'docs') -Force | Out-Null

$installerName = '2 - INSTALL Codex GPU Thalen Helper.exe'
$installer = Join-Path $stage $installerName
Copy-Item -LiteralPath $sourceInstaller -Destination $installer
Copy-Item -LiteralPath $sourceSigning -Destination (Join-Path $stage 'SIGNING_STATUS.txt')
Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'README.md') -Destination (Join-Path $stage 'README.md')
Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'LICENSE') -Destination (Join-Path $stage 'LICENSE')
Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'INSTALL-WITH-CODEX.md') -Destination (Join-Path $stage '0 - PASTE THIS INTO CODEX.md')
Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'docs\friend-install-and-use-guide.md') -Destination (Join-Path $stage 'INSTALL AND USE GUIDE.md')
Copy-Item -LiteralPath (Join-Path $RepositoryRoot 'docs\CODEX-HANDOFF.md') -Destination (Join-Path $stage '3 - CODEX HANDOFF.md')
$friendDocs = [ordered]@{
    'model-selection.md' = 'MODEL SELECTION.md'
    'privacy-and-security.md' = 'PRIVACY AND SECURITY.md'
    'troubleshooting.md' = 'TROUBLESHOOTING.md'
    'uninstall.md' = 'UNINSTALL.md'
}
foreach ($entry in $friendDocs.GetEnumerator()) {
    Copy-Item -LiteralPath (Join-Path $RepositoryRoot ('docs\' + $entry.Key)) -Destination (Join-Path $stage ('docs\' + $entry.Value))
}

$installerHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
@("$installerHash  $installerName") |
    Set-Content -LiteralPath (Join-Path $stage 'INSTALLER-SHA256.txt') -Encoding ascii
@(
    "CODEX GPU THALEN HELPER $Version",
    '',
    'EASIEST: Open 0 - PASTE THIS INTO CODEX.md and paste its prompt into a new Codex task.',
    'Codex will verify the official GitHub release, ask before running the unsigned installer, and guide the remaining setup.',
    '',
    '1. Extract this entire ZIP to a normal local folder.',
    "2. Verify $installerName with INSTALLER-SHA256.txt:",
    "   Open PowerShell in this folder and run:",
    "   (Get-FileHash -LiteralPath '.\$installerName' -Algorithm SHA256).Hash.ToLowerInvariant()",
    '   Compare the 64 characters it prints with the 64 characters in INSTALLER-SHA256.txt. They must match exactly.',
    "3. Double-click $installerName.",
    '4. Read INSTALL AND USE GUIDE.md for the beginner walkthrough and control explanations.',
    '5. Drag 3 - CODEX HANDOFF.md into a new Codex task. Codex is told to do the technical work and guide a beginner one simple decision at a time.',
    '',
    'The item labeled 2 is the installer itself, placed directly in the root so it keeps working after the folder is moved. A machine-specific Windows shortcut is intentionally not used.',
    '',
    'This beta is not Authenticode-signed. Windows SmartScreen may show Unknown publisher. Continue only when the checksum matches and you trust the bundle source.',
    '',
    'Base installation does not download or load a model. Model download or validation requires a separate explicit choice.',
    'For modest hardware, the Codex handoff prefers the smallest safe supported model, low-impact mode, and immediate unloading. It leaves review disabled when no model fits safely.',
    'Existing unmarked local_gpu_reviewer integrations are preserved and remain outside packaged control.',
    'If automatic startup is declined, Ollama must be started manually after each sign-in.'
) | Set-Content -LiteralPath (Join-Path $stage '1 - START HERE.txt') -Encoding utf8

$expectedRoot = @(
    '0 - PASTE THIS INTO CODEX.md',
    '1 - START HERE.txt',
    $installerName,
    '3 - CODEX HANDOFF.md',
    'INSTALL AND USE GUIDE.md',
    'INSTALLER-SHA256.txt',
    'LICENSE',
    'README.md',
    'SIGNING_STATUS.txt',
    'docs'
) | Sort-Object
$actualRoot = @(Get-ChildItem -LiteralPath $stage | ForEach-Object Name | Sort-Object)
$rootDifference = @(Compare-Object -ReferenceObject $expectedRoot -DifferenceObject $actualRoot)
if ($rootDifference.Count -ne 0) {
    throw "Friend bundle root is missing or has an unexpected item: $($rootDifference.InputObject -join ', ')"
}

Add-Type -TypeDefinition @'
using System;
using System.IO;

public static class FriendBundleByteScanner
{
    public static bool Contains(string path, byte[] pattern)
    {
        if (pattern == null || pattern.Length == 0)
        {
            return false;
        }

        const int chunkSize = 1024 * 1024;
        var buffer = new byte[chunkSize + pattern.Length - 1];
        var carry = 0;
        using (var stream = File.OpenRead(path))
        {
            while (true)
            {
                var read = stream.Read(buffer, carry, chunkSize);
                if (read == 0)
                {
                    return false;
                }

                var available = carry + read;
                var maximum = available - pattern.Length;
                var index = 0;
                while (index <= maximum)
                {
                    index = Array.IndexOf(buffer, pattern[0], index, maximum - index + 1);
                    if (index < 0)
                    {
                        break;
                    }

                    var matches = true;
                    for (var offset = 1; offset < pattern.Length; offset++)
                    {
                        if (buffer[index + offset] != pattern[offset])
                        {
                            matches = false;
                            break;
                        }
                    }

                    if (matches)
                    {
                        return true;
                    }

                    index++;
                }

                carry = Math.Min(pattern.Length - 1, available);
                Buffer.BlockCopy(buffer, available - carry, buffer, 0, carry);
            }
        }
    }
}
'@

$sensitiveValues = @(
    $RepositoryRoot,
    $env:USERPROFILE,
    $env:USERNAME,
    $env:COMPUTERNAME
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_.Length -ge 4 } | Select-Object -Unique
foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File) {
    foreach ($sensitive in $sensitiveValues) {
        foreach ($encoding in @([System.Text.Encoding]::UTF8, [System.Text.Encoding]::Unicode, [System.Text.Encoding]::BigEndianUnicode)) {
            $needle = $encoding.GetBytes($sensitive)
            if ([FriendBundleByteScanner]::Contains($file.FullName, $needle)) {
                throw "Friend bundle contains local machine metadata in $($file.Name)."
            }
        }
    }
}

$zip = Join-Path $friendRoot "Codex-GPU-Thalen-Helper-$Version-Friend-Installer.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
Expand-Archive -LiteralPath $zip -DestinationPath $validation
$validatedInstaller = Join-Path $validation $installerName
if (-not (Test-Path -LiteralPath $validatedInstaller -PathType Leaf)) {
    throw 'The relocated friend bundle does not contain the clearly labeled root installer.'
}
$validatedHash = (Get-FileHash -LiteralPath $validatedInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
if ($validatedHash -cne $installerHash) {
    throw 'The installer hash changed while constructing the friend bundle.'
}
$validatedRoot = @(Get-ChildItem -LiteralPath $validation | ForEach-Object Name | Sort-Object)
$validatedDifference = @(Compare-Object -ReferenceObject $expectedRoot -DifferenceObject $validatedRoot)
if ($validatedDifference.Count -ne 0) {
    throw 'The extracted friend bundle layout differs from the audited staging layout.'
}

Write-Host "Friend installer bundle passed relocation, checksum, layout, and local-metadata checks: $zip"
Write-Output $zip
