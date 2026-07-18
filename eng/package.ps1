[CmdletBinding()]
param(
    [string]$Version = '0.1.0-beta.15',
    [string]$InnoCompiler,
    [string]$SbomTool
)

. (Join-Path $PSScriptRoot 'common.ps1')

$pinnedInnoCompilerSha256 = '0FF6140D641F84B64204A2C4D52207C6FC437C9F4DB8779C83083D84F7E3D70D'

function Assert-SafeReleaseVersion {
    param([Parameter(Mandatory)][string]$Value)

    if ($Value.Length -gt 128 -or
        $Value -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
        throw 'Version must be a SemVer-compatible value containing only safe filename characters.'
    }
}

function Get-ContainedOutputPath {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$FileName
    )

    if ([System.IO.Path]::IsPathRooted($FileName)) {
        throw 'Release output names must be relative file names.'
    }
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd([char[]]'\/') + [System.IO.Path]::DirectorySeparatorChar
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $rootFull $FileName))
    if (-not $candidate.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'A release output path escaped the release directory.'
    }
    return $candidate
}

function Assert-VerifiedInnoCompiler {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw 'Inno Setup compiler was not found.'
    }
    if ((Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash -ne $pinnedInnoCompilerSha256) {
        throw 'Inno Setup compiler checksum mismatch.'
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $fullPath
    if ($signature.Status -ne 'Valid' -or
        $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Pyrsys B\.V\.(,|$)') {
        throw 'Inno Setup compiler publisher validation failed.'
    }
    return $fullPath
}

function Get-JsonStringValues {
    param([AllowNull()]$Value)

    if ($null -eq $Value) {
        return
    }
    if ($Value -is [string]) {
        $Value
        return
    }
    if ($Value -is [System.Collections.IDictionary]) {
        foreach ($key in $Value.Keys) {
            if ($key -is [string]) {
                $key
            }
            Get-JsonStringValues -Value $Value[$key]
        }
        return
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        foreach ($item in $Value) {
            Get-JsonStringValues -Value $item
        }
        return
    }
    if ($Value -is [pscustomobject]) {
        foreach ($property in $Value.PSObject.Properties) {
            $property.Name
            Get-JsonStringValues -Value $property.Value
        }
    }
}

function Assert-NoLocalPathsInDecodedJson {
    param([Parameter(Mandatory)]$Value)

    $sensitiveRoots = @($RepositoryRoot, $env:USERPROFILE) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { [System.IO.Path]::GetFullPath($_) }
    foreach ($text in Get-JsonStringValues -Value $Value) {
        $forward = $text.Replace('\', '/')
        foreach ($root in $sensitiveRoots) {
            if ($text.IndexOf($root, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                $forward.IndexOf($root.Replace('\', '/'), [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw 'SBOM contains a local repository or user-profile path.'
            }
        }
        if ($text -match '(?i)(?:^|[^A-Za-z0-9])(?:[A-Z]:[\\/]|\\\\[^\\/\r\n]+[\\/][^\\/\r\n]+)') {
            throw 'SBOM contains an absolute Windows or UNC path.'
        }
    }
}

Assert-SafeReleaseVersion -Value $Version
$betaVersionMatch = [System.Text.RegularExpressions.Regex]::Match(
    $Version,
    '^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)-beta\.(?<beta>0|[1-9][0-9]*)$',
    [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
if (-not $betaVersionMatch.Success) {
    throw 'Unsigned packages require an exact <major>.<minor>.<patch>-beta.<number> version.'
}
$peParts = @('major', 'minor', 'patch', 'beta') | ForEach-Object {
    [uint32]::Parse($betaVersionMatch.Groups[$_].Value, [System.Globalization.CultureInfo]::InvariantCulture)
}
if (@($peParts | Where-Object { $_ -gt 65535 }).Count -ne 0) {
    throw 'Each installer PE version component must be between 0 and 65535.'
}
$peVersion = $peParts -join '.'
$artifacts = Join-Path $RepositoryRoot '.artifacts'
$publish = Join-Path $artifacts 'publish'
$stage = Join-Path $artifacts 'stage'
$release = Join-Path $artifacts 'release'
Reset-RepositoryDirectory -Path $artifacts
New-Item -ItemType Directory -Path $publish, $stage, $release -Force | Out-Null
$priorCodexHome = $env:CODEX_HOME
$priorStateDir = $env:THALEN_HELPER_STATE_DIR
$priorRealGpuTest = $env:THALEN_HELPER_REAL_GPU_TEST
$env:CODEX_HOME = Join-Path $artifacts 'test-isolation\codex-home'
$env:THALEN_HELPER_STATE_DIR = Join-Path $artifacts 'test-isolation\state'
$env:THALEN_HELPER_REAL_GPU_TEST = $null
New-Item -ItemType Directory -Path $env:CODEX_HOME, $env:THALEN_HELPER_STATE_DIR -Force | Out-Null

Push-Location $RepositoryRoot
try {
    Invoke-Dotnet restore CodexGpuThalenHelper.slnx --locked-mode
    Invoke-Dotnet build CodexGpuThalenHelper.slnx --configuration Release --no-restore
    Invoke-Dotnet test tests\ThalenHelper.Tests\ThalenHelper.Tests.csproj --configuration Release --no-restore --logger 'console;verbosity=minimal'

    $projects = @(
        @{ Path = 'src\ThalenHelper.Cli\ThalenHelper.Cli.csproj'; Name = 'cli'; Exe = 'thalen-helper.exe' },
        @{ Path = 'src\ThalenHelper.Mcp\ThalenHelper.Mcp.csproj'; Name = 'mcp'; Exe = 'local-gpu-reviewer.exe' },
        @{ Path = 'src\ThalenHelper.ControlCenter\ThalenHelper.ControlCenter.csproj'; Name = 'control-center'; Exe = 'ThalenHelper.ControlCenter.exe' }
    )
    foreach ($project in $projects) {
        $output = Join-Path $publish $project.Name
        Invoke-Dotnet restore $project.Path --runtime win-x64 --locked-mode '-p:SelfContained=true'
        Invoke-Dotnet publish $project.Path --configuration Release --runtime win-x64 --self-contained true --no-restore --output $output `
            '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:DebugType=None' '-p:DebugSymbols=false'
        Copy-Item -LiteralPath (Join-Path $output $project.Exe) -Destination (Join-Path $stage $project.Exe)
    }

    Copy-Item -LiteralPath 'model-catalog' -Destination (Join-Path $stage 'model-catalog') -Recurse
    Copy-Item -LiteralPath 'templates' -Destination (Join-Path $stage 'templates') -Recurse
    Copy-Item -LiteralPath 'docs' -Destination (Join-Path $stage 'docs') -Recurse
    foreach ($file in @('README.md', 'LICENSE', 'SECURITY.md', 'PRIVACY.md', 'SUPPORT.md', 'THIRD_PARTY_NOTICES.md', 'MODEL_LICENSES.md', 'CHANGELOG.md')) {
        Copy-Item -LiteralPath $file -Destination (Join-Path $stage $file)
    }

    $portable = Get-ContainedOutputPath -Root $release -FileName "Codex-GPU-Thalen-Helper-$Version-win-x64-portable.zip"
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $portable -CompressionLevel Optimal

    if ([string]::IsNullOrWhiteSpace($InnoCompiler)) {
        $InnoCompiler = Join-Path $RepositoryRoot '.tools\inno\ISCC.exe'
    }
    if ([string]::IsNullOrWhiteSpace($InnoCompiler) -or -not (Test-Path -LiteralPath $InnoCompiler)) {
        throw 'Inno Setup compiler was not found. Pass -InnoCompiler or install the pinned local tool.'
    }
    $InnoCompiler = Assert-VerifiedInnoCompiler -Path $InnoCompiler

    & $InnoCompiler "/DMyAppVersion=$Version" "/DMyAppPeVersion=$peVersion" 'installer\ThalenHelper.iss'
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }
    $setup = Join-Path $artifacts 'installer\Codex-GPU-Thalen-Helper-Setup.exe'
    if (-not (Test-Path -LiteralPath $setup)) { throw 'Expected setup executable was not created.' }
    Copy-Item -LiteralPath $setup -Destination (Join-Path $release 'Codex-GPU-Thalen-Helper-Setup.exe')

    if ([string]::IsNullOrWhiteSpace($SbomTool)) {
        $SbomTool = Join-Path $RepositoryRoot '.tools\sbom\sbom-tool.exe'
    }
    if (-not (Test-Path -LiteralPath $SbomTool)) {
        throw 'Microsoft SBOM Tool was not found at the pinned local path.'
    }
    $sbomOutput = Join-Path $release 'sbom'
    $componentSource = Join-Path $artifacts 'sbom-components'
    New-Item -ItemType Directory -Path $sbomOutput -Force | Out-Null
    New-Item -ItemType Directory -Path $componentSource -Force | Out-Null
    Copy-Item -LiteralPath 'Directory.Packages.props' -Destination $componentSource
    @(
        Get-ChildItem -Path 'src','tests' -Recurse -File -Filter '*.csproj'
        Get-ChildItem -Path 'src','tests' -Recurse -File -Filter 'packages.lock.json'
        Get-ChildItem -Path 'src','tests' -Recurse -File -Filter 'project.assets.json'
    ) | ForEach-Object {
        $relative = $_.FullName.Substring($RepositoryRoot.Length).TrimStart([char[]]'\')
        $destination = Join-Path $componentSource $relative
        New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Copy-Item -LiteralPath $_.FullName -Destination $destination
    }
    & $SbomTool generate -b $stage -bc $componentSource -pn 'Codex GPU Thalen Helper' -pv $Version -ps 'Codex GPU Thalen Helper contributors' -nsb 'https://github.com/rennavationstudios-bit/codex-gpu-thalen-helper' -m $sbomOutput -D true -F false -pm true
    if ($LASTEXITCODE -ne 0) { throw "SBOM generation failed with exit code $LASTEXITCODE" }
    $manifestPath = Join-Path $sbomOutput '_manifest\spdx_2.2\manifest.spdx.json'
    $manifestContent = Get-Content -LiteralPath $manifestPath -Raw
    $manifest = $manifestContent | ConvertFrom-Json
    if ($manifest.packages.Count -le 1) { throw 'SBOM generation did not detect third-party packages.' }
    Assert-NoLocalPathsInDecodedJson -Value $manifest
    $sbomArchive = Get-ContainedOutputPath -Root $release -FileName "Codex-GPU-Thalen-Helper-$Version-sbom.zip"
    Compress-Archive -Path (Join-Path $sbomOutput '*') -DestinationPath $sbomArchive -CompressionLevel Optimal

    $signing = Get-AuthenticodeSignature -LiteralPath (Join-Path $release 'Codex-GPU-Thalen-Helper-Setup.exe')
    @(
        "Version: $Version",
        "Authenticode status: $($signing.Status)",
        'This beta installer is not Authenticode-signed. GitHub artifact attestation and SHA-256 checksums do not replace Authenticode signing.',
        'Windows SmartScreen may display an unknown publisher warning.'
    ) | Set-Content -LiteralPath (Join-Path $release 'SIGNING_STATUS.txt') -Encoding ascii

    $checksumPath = Join-Path $release 'SHA256SUMS.txt'
    Get-ChildItem -LiteralPath $release -File | Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    } | Set-Content -LiteralPath $checksumPath -Encoding ascii
}
finally {
    Pop-Location
    $env:CODEX_HOME = $priorCodexHome
    $env:THALEN_HELPER_STATE_DIR = $priorStateDir
    $env:THALEN_HELPER_REAL_GPU_TEST = $priorRealGpuTest
}
