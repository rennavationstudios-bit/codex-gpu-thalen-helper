[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$script:RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$localDotnet = Join-Path $script:RepositoryRoot '.tools\dotnet\dotnet.exe'
$script:Dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }

function Assert-RepositoryChildPath {
    param([Parameter(Mandatory)][string]$Path)
    $full = [System.IO.Path]::GetFullPath($Path)
    $root = $script:RepositoryRoot.TrimEnd('\') + '\'
    if (-not $full.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the repository: $full"
    }
}

function Reset-RepositoryDirectory {
    param([Parameter(Mandatory)][string]$Path)
    Assert-RepositoryChildPath -Path $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Invoke-Dotnet {
    $environmentNames = @(
        'DOTNET_SKIP_FIRST_TIME_EXPERIENCE',
        'DOTNET_GENERATE_ASPNET_CERTIFICATE',
        'DOTNET_CLI_TELEMETRY_OPTOUT'
    )
    $originalEnvironment = @{}
    foreach ($name in $environmentNames) {
        $item = Get-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
        $originalEnvironment[$name] = @{
            Exists = $null -ne $item
            Value = if ($null -eq $item) { $null } else { $item.Value }
        }
    }

    try {
        $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

        & $script:Dotnet @args
        $exitCode = $LASTEXITCODE
    }
    finally {
        foreach ($name in $environmentNames) {
            $original = $originalEnvironment[$name]
            if ($original.Exists) {
                Set-Item -LiteralPath "Env:$name" -Value $original.Value
            }
            else {
                Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
            }
        }
    }

    if ($exitCode -ne 0) {
        throw "dotnet failed with exit code $exitCode"
    }
}
